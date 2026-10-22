using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

// MTGA-Forge.exe: the one window. Guides the first start (copy + patch the game), then lets the player choose single player,
// hosting a friend or joining a friend, and shows what is happening. The work is done by tools\forge-patch.ps1 (check/patch)
// and tools\launcher.ps1 (server + game); this program only collects settings, runs them hidden and explains their output.
//   MTGA-Forge.exe --selftest <shot.png>   build both pages, save pictures of them (<shot>.png, <shot>-setup.png), exit
namespace MtgaForgeLauncher
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm();
            if (args.Length >= 1 && args[0] == "--selftest") return form.SelfTest(args.Length > 1 ? args[1] : null);
            if (args.Length >= 3 && (args[0] == "--check" || args[0] == "--setup")) return form.CheckProbe(args[1], args[2], args[0] == "--setup");
            Application.Run(form);
            return 0;
        }
    }

    /// <summary>A hidden powershell child whose output arrives on the UI thread.</summary>
    sealed class Runner
    {
        public readonly Process Proc;
        public readonly List<string> Lines = new List<string>();

        public Runner(Control ui, string script, string args, Action<string> onLine, Action<int> onExit)
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                WorkingDirectory = Path.GetDirectoryName(script),
            };
            Proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler h = (s, e) =>
            {
                if (e.Data == null) return;
                ui.BeginInvoke((Action)(() => { Lines.Add(e.Data); onLine(e.Data); }));
            };
            Proc.OutputDataReceived += h;
            Proc.ErrorDataReceived += h;
            Proc.Start();
            Proc.BeginOutputReadLine();
            Proc.BeginErrorReadLine();
            var p = Proc;
            Task.Run(() =>
            {
                p.WaitForExit();     // also waits for the redirected streams to drain
                int code = p.ExitCode;
                try { ui.BeginInvoke((Action)(() => onExit(code))); } catch (InvalidOperationException) { }
            });
        }

        public bool Running { get { try { return !Proc.HasExited; } catch { return false; } } }

        public void KillTree()
        {
            try
            {
                if (!Proc.HasExited)
                    Process.Start(new ProcessStartInfo("taskkill", "/PID " + Proc.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(5000);
            }
            catch { }
        }
    }

    sealed class MainForm : Form
    {
        const int PeerPort = 30012;
        static readonly string[] PatchSteps = { "refresh", "orig", "identity", "unityconnect", "storeconfig", "bridge", "patch", "carddb", "manifest" };

        readonly string root;
        readonly string tools;
        readonly string homeDefault = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? ".", "MTGA-Forge");
        readonly string iniPath;
        readonly Dictionary<string, string> set = new Dictionary<string, string>
            { { "name", "Forge Player" }, { "mode", "bot" }, { "peer", "" }, { "key", "" }, { "folder", "" }, { "source", "" } };

        // setup page
        readonly Panel pgSetup = new Panel();
        readonly Label lblSetupTitle = new Label(), lblSetupText = new Label(), lblDisk = new Label(), lblSetupStatus = new Label();
        readonly ProgressBar bar = new ProgressBar();
        readonly Button btnPrepare = new Button(), btnSource = new Button(), btnRecheck = new Button();
        // play page
        readonly Panel pgPlay = new Panel();
        readonly Label lblProfile = new Label();
        readonly TextBox txtName = new TextBox(), txtPeer = new TextBox(), txtKey = new TextBox(), txtFolder = new TextBox();
        readonly RadioButton rbBot = new RadioButton(), rbHost = new RadioButton(), rbJoin = new RadioButton();
        readonly ListBox lstAddr = new ListBox();
        readonly Label lblHost = new Label(), lblTs = new Label(), lblJoin = new Label(), lblKey = new Label(), lblHelp = new Label(), lblStatus = new Label(), lblTest = new Label();
        readonly Button btnCopy = new Button(), btnTest = new Button(), btnBrowse = new Button(), btnStart = new Button(), btnStop = new Button();
        readonly TextBox log = new TextBox();
        readonly Timer logTimer = new Timer { Interval = 700 };

        Runner runner;
        string serverLog;
        long serverLogPos;
        bool busy;
        bool selfTest;

        public MainForm()
        {
            root = FindRoot();
            tools = Path.Combine(root, "tools");
            iniPath = Path.Combine(homeDefault, "ui.ini");
            LoadSettings();
            BuildUi();
            Load += (s, e) => { if (!selfTest) RunCheck(); };
            FormClosing += (s, e) => { SaveSettings(); runner?.KillTree(); };
        }

        // ---------------------------------------------------------------------------------------------- locations
        static string FindRoot()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            for (int i = 0; i < 6 && d != null; i++, d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, "tools", "launcher.ps1"))) return d;
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        }

        string Folder { get { return string.IsNullOrWhiteSpace(txtFolder.Text) ? homeDefault : txtFolder.Text.Trim(); } }

        static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "") + "\""; }

        // ---------------------------------------------------------------------------------------------- settings
        void LoadSettings()
        {
            try
            {
                if (File.Exists(iniPath))
                    foreach (var line in File.ReadAllLines(iniPath))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0 && set.ContainsKey(line.Substring(0, i))) set[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
            if (string.IsNullOrEmpty(set["folder"]))
            {
                // the folder the command-line launcher remembered
                try
                {
                    string cfg = Path.Combine(homeDefault, "config.json");
                    if (File.Exists(cfg))
                    {
                        var m = Regex.Match(File.ReadAllText(cfg), "\"home\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (m.Success) set["folder"] = m.Groups[1].Value.Replace("\\\\", "\\");
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(set["folder"])) set["folder"] = homeDefault;
            // the player's name lives in the profile (data\profile.json, written by the server); what was typed last is only the fallback
            string profName, profId, profTag;
            if (ReadProfile(set["folder"], out profName, out profId, out profTag) && profName != "") set["name"] = profName;
        }

        /// <summary>The player's profile of this install: name, player id and the #tag the game shows after the name.</summary>
        static bool ReadProfile(string folder, out string name, out string id, out string tag)
        {
            name = id = tag = "";
            try
            {
                string file = Path.Combine(folder, "data", "profile.json");
                if (!File.Exists(file)) return false;
                string json = File.ReadAllText(file);
                name = JsonString(json, "displayName"); id = JsonString(json, "playerId"); tag = JsonString(json, "tag");
                return id != "";
            }
            catch { return false; }
        }

        static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }

        /// <summary>"Player ID 791602cb · shown as Name#85191" under the name box (nothing before the first game).</summary>
        string ProfileNote()
        {
            string n, id, tag;
            if (!ReadProfile(Folder, out n, out id, out tag)) return "A player ID is made the first time you start the game.";
            return "Player ID " + (id.Length > 8 ? id.Substring(0, 8) : id) + (tag != "" ? "  -  shown as " + n + "#" + tag : "");
        }

        void SaveSettings()
        {
            set["name"] = txtName.Text; set["peer"] = txtPeer.Text; set["key"] = txtKey.Text; set["folder"] = txtFolder.Text;
            set["mode"] = rbHost.Checked ? "host" : rbJoin.Checked ? "join" : "bot";
            try
            {
                Directory.CreateDirectory(homeDefault);
                File.WriteAllLines(iniPath, set.Select(kv => kv.Key + "=" + kv.Value));
            }
            catch { }
        }

        // ---------------------------------------------------------------------------------------------- window
        static T At<T>(T c, int x, int y, int w, int h) where T : Control { c.Location = new Point(x, y); c.Size = new Size(w, h); return c; }

        void BuildUi()
        {
            Text = "MTGA-Forge";
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(7f, 15f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(640, 620);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            // ---- setup page
            At(pgSetup, 0, 0, 640, 620);
            lblSetupTitle.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
            pgSetup.Controls.Add(At(lblSetupTitle, 20, 16, 600, 30));
            lblSetupText.AutoSize = false;
            pgSetup.Controls.Add(At(lblSetupText, 20, 56, 600, 130));
            pgSetup.Controls.Add(At(lblDisk, 20, 190, 600, 20));
            pgSetup.Controls.Add(At(bar, 20, 222, 600, 22));
            lblSetupStatus.AutoSize = false;
            pgSetup.Controls.Add(At(lblSetupStatus, 20, 250, 600, 40));
            btnPrepare.Text = "Prepare my game";
            btnSource.Text = "Choose MTGA folder...";
            btnRecheck.Text = "Check again";
            pgSetup.Controls.Add(At(btnPrepare, 20, 300, 170, 30));
            pgSetup.Controls.Add(At(btnSource, 200, 300, 170, 30));
            pgSetup.Controls.Add(At(btnRecheck, 380, 300, 120, 30));
            btnPrepare.Click += (s, e) => Prepare();
            btnSource.Click += (s, e) => ChooseSource();
            btnRecheck.Click += (s, e) => RunCheck();

            // ---- play page
            At(pgPlay, 0, 0, 640, 620);
            pgPlay.Controls.Add(At(new Label { Text = "Your name in the game" }, 20, 14, 300, 20));
            txtName.Text = set["name"];
            txtName.MaxLength = 24;
            pgPlay.Controls.Add(At(txtName, 20, 36, 280, 24));
            lblProfile.Text = ProfileNote();
            lblProfile.ForeColor = SystemColors.GrayText;
            pgPlay.Controls.Add(At(lblProfile, 310, 40, 310, 20));

            var grp = At(new GroupBox { Text = "How do you want to play?" }, 20, 74, 600, 100);
            rbBot.Text = "Single player: against the Forge AI";
            rbHost.Text = "Play a friend: I host (my friend connects to me)";
            rbJoin.Text = "Play a friend: I join (I connect to my friend)";
            int ry = 22;
            foreach (var r in new[] { rbBot, rbHost, rbJoin }) { grp.Controls.Add(At(r, 16, ry, 560, 22)); ry += 24; r.CheckedChanged += (s, e) => UpdateMode(); }
            pgPlay.Controls.Add(grp);

            lblHost.Text = "Send your friend one of these addresses (the first one is the best choice):";
            pgPlay.Controls.Add(At(lblHost, 20, 184, 600, 20));
            pgPlay.Controls.Add(At(lstAddr, 20, 206, 480, 70));
            btnCopy.Text = "Copy address";
            btnCopy.Click += (s, e) => CopyAddress();
            pgPlay.Controls.Add(At(btnCopy, 510, 206, 110, 28));
            lblTs.AutoSize = false;
            pgPlay.Controls.Add(At(lblTs, 20, 280, 600, 36));

            lblJoin.Text = "Your friend's address (what they sent you, like 100.64.1.2:30012)";
            pgPlay.Controls.Add(At(lblJoin, 20, 184, 600, 20));
            txtPeer.Text = set["peer"];
            pgPlay.Controls.Add(At(txtPeer, 20, 206, 380, 24));
            btnTest.Text = "Can I reach them?";
            btnTest.Click += (s, e) => TestConnection();
            pgPlay.Controls.Add(At(btnTest, 410, 204, 130, 28));
            lblTest.AutoSize = false;
            pgPlay.Controls.Add(At(lblTest, 20, 236, 600, 60));

            lblKey.Text = "Shared key (optional: both players type the same word)";
            pgPlay.Controls.Add(At(lblKey, 20, 324, 600, 20));
            txtKey.Text = set["key"];
            pgPlay.Controls.Add(At(txtKey, 20, 346, 280, 24));

            pgPlay.Controls.Add(At(new Label { Text = "Game folder (the patched copy of MTGA and your decks live here)" }, 20, 382, 600, 20));
            txtFolder.Text = set["folder"];
            pgPlay.Controls.Add(At(txtFolder, 20, 404, 480, 24));
            btnBrowse.Text = "Browse...";
            btnBrowse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Folder for the patched game copy (about 15 GB) and your decks", SelectedPath = Folder })
                    if (d.ShowDialog(this) == DialogResult.OK) { txtFolder.Text = d.SelectedPath; RunCheck(); }
            };
            pgPlay.Controls.Add(At(btnBrowse, 510, 402, 110, 28));

            lblHelp.AutoSize = false;
            pgPlay.Controls.Add(At(lblHelp, 20, 438, 600, 54));
            btnStart.Text = "Start";
            btnStop.Text = "Stop";
            btnStop.Enabled = false;
            btnStart.Click += (s, e) => StartGame();
            btnStop.Click += (s, e) => runner?.KillTree();
            pgPlay.Controls.Add(At(btnStart, 20, 496, 120, 30));
            pgPlay.Controls.Add(At(btnStop, 150, 496, 120, 30));
            lblStatus.AutoSize = false;
            pgPlay.Controls.Add(At(lblStatus, 285, 496, 335, 34));
            log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.Font = new Font("Consolas", 8f);
            pgPlay.Controls.Add(At(log, 20, 536, 600, 74));

            Controls.Add(pgPlay);
            Controls.Add(pgSetup);
            pgPlay.Visible = false;

            switch (set["mode"]) { case "host": rbHost.Checked = true; break; case "join": rbJoin.Checked = true; break; default: rbBot.Checked = true; break; }
            UpdateMode();
            logTimer.Tick += (s, e) => TailServerLog();
        }

        void ShowPage(bool play)
        {
            pgPlay.Visible = play;
            pgSetup.Visible = !play;
            (play ? pgPlay : pgSetup).BringToFront();
        }

        // ---------------------------------------------------------------------------------------------- is the game ready?
        void RunCheck()
        {
            if (busy) return;
            ShowPage(false);
            lblSetupTitle.Text = "Checking your game...";
            lblSetupText.Text = "";
            lblDisk.Text = "";
            lblSetupStatus.Text = "";
            bar.Style = ProgressBarStyle.Marquee;
            foreach (var b in new[] { btnPrepare, btnSource, btnRecheck }) b.Enabled = false;
            string args = "-Target " + Q(Path.Combine(Folder, "client")) + (set["source"] != "" ? " -Source " + Q(set["source"]) : "") + " -Check";
            busy = true;
            string last = "";
            runner = new Runner(this, Path.Combine(tools, "forge-patch.ps1"), args, l => { if (l.Trim() != "") last = l.Trim(); }, code =>
            {
                busy = false;
                bar.Style = ProgressBarStyle.Blocks; bar.Value = 0;
                btnRecheck.Enabled = true;
                switch (code)
                {
                    case 0: ShowPage(true); UpdateMode(); break;
                    case 3: ShowSetup(last); break;
                    case 4: ShowNoInstall(); break;
                    case 5:
                        lblSetupTitle.Text = "Steam is still updating MTGA";
                        lblSetupText.Text = "Wait until Steam has finished updating MTGA, then press Check again.";
                        break;
                    default:
                        lblSetupTitle.Text = "Could not check your game";
                        lblSetupText.Text = "The check failed (exit code " + code + "):\r\n" + last;
                        break;
                }
            });
        }

        void ShowSetup(string why)
        {
            bool first = why.IndexOf("not patched yet", StringComparison.OrdinalIgnoreCase) >= 0 || why.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0;
            lblSetupTitle.Text = first ? "First start: preparing your game" : "Your game needs to be updated";
            lblSetupText.Text = first
                ? "MTGA-Forge makes a separate copy of your MTGA installation (about 15 GB) and patches it to talk to the Forge rules engine. "
                  + "Your Steam installation is only read, never changed, and the copy never signs in to Wizards.\r\n\r\n"
                  + "This takes several minutes, mostly copying files. You can do something else meanwhile."
                : "MTGA was updated (or this version of MTGA-Forge changed), so the patched copy is rebuilt. Your decks stay.\r\n\r\n" + why;
            ShowDisk();
            btnPrepare.Enabled = true; btnSource.Enabled = true;
            lblSetupStatus.Text = "";
        }

        void ShowNoInstall()
        {
            lblSetupTitle.Text = "MTGA was not found";
            lblSetupText.Text = "Install MTGA with Steam first. If it is installed somewhere else, choose the folder that contains MTGA.exe.";
            btnSource.Enabled = true;
            btnPrepare.Enabled = false;
        }

        void ShowDisk()
        {
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Folder)));
                lblDisk.Text = "Free space on " + d.Name + ": " + (d.AvailableFreeSpace / (1024L * 1024 * 1024)) + " GB (about 15 GB needed). Folder: " + Folder;
            }
            catch { lblDisk.Text = "Folder: " + Folder; }
        }

        void ChooseSource()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose the folder that contains MTGA.exe" })
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    if (!File.Exists(Path.Combine(d.SelectedPath, "MTGA.exe"))) { MessageBox.Show(this, "MTGA.exe is not in that folder.", "MTGA-Forge"); return; }
                    set["source"] = d.SelectedPath;
                    SaveSettings();
                    RunCheck();
                }
        }

        void Prepare()
        {
            if (busy) return;
            busy = true;
            foreach (var b in new[] { btnPrepare, btnSource, btnRecheck }) b.Enabled = false;
            lblSetupTitle.Text = "Preparing your game...";
            bar.Style = ProgressBarStyle.Blocks; bar.Minimum = 0; bar.Maximum = PatchSteps.Length; bar.Value = 0;
            lblSetupStatus.Text = "Starting...";
            string args = "-NoPause -PatchOnly -Folder " + Q(Folder) + (set["source"] != "" ? " -Source " + Q(set["source"]) : "");
            runner = new Runner(this, Path.Combine(tools, "launcher.ps1"), args, l =>
            {
                var m = Regex.Match(l, @"^== (\w+)");
                if (m.Success)
                {
                    int i = Array.IndexOf(PatchSteps, m.Groups[1].Value);
                    if (i >= 0)
                    {
                        bar.Value = Math.Min(bar.Maximum, i);
                        lblSetupStatus.Text = "Step " + (i + 1) + " of " + PatchSteps.Length + ": " + Describe(PatchSteps[i]);
                    }
                }
                else if (l.Contains("Not enough free space") || l.Contains("is not installed") || l.Contains("Steam is still updating")) lblSetupStatus.Text = l.Trim();
            }, code =>
            {
                busy = false;
                runner = null;
                if (code == 0) { bar.Value = bar.Maximum; RunCheck(); return; }
                lblSetupTitle.Text = "Preparing the game failed";
                string tail = string.Join("\r\n", new List<string>(LastLines(8)));
                lblSetupStatus.Text = "";
                lblSetupText.Text = "Nothing was started and your MTGA installation is untouched. Last messages:\r\n\r\n" + tail;
                btnPrepare.Enabled = true; btnSource.Enabled = true; btnRecheck.Enabled = true;
            });
        }

        IEnumerable<string> LastLines(int n) { return runner == null ? new string[0] : runner.Lines.Where(l => l.Trim() != "").Reverse().Take(n).Reverse(); }

        static string Describe(string step)
        {
            switch (step)
            {
                case "refresh": return "copying your MTGA installation (the long one)";
                case "orig": return "saving the original program files";
                case "identity": return "giving the copy its own identity";
                case "unityconnect": return "switching off analytics";
                case "storeconfig": return "switching off the store";
                case "bridge": return "installing the Forge connection";
                case "patch": return "patching the game";
                case "carddb": return "building the card database (Forge cards for the game)";
                case "manifest": return "finishing";
                default: return step;
            }
        }

        // ---------------------------------------------------------------------------------------------- play page
        RadioButton[] Radios { get { return new[] { rbBot, rbHost, rbJoin }; } }
        string Mode { get { return rbHost.Checked ? "host" : rbJoin.Checked ? "join" : "bot"; } }

        void UpdateMode()
        {
            string m = Mode;
            foreach (Control c in new Control[] { lblHost, lstAddr, btnCopy, lblTs }) c.Visible = m == "host";
            foreach (Control c in new Control[] { lblJoin, txtPeer, btnTest, lblTest }) c.Visible = m == "join";
            foreach (Control c in new Control[] { lblKey, txtKey }) c.Visible = m != "bot";
            switch (m)
            {
                case "bot": lblHelp.Text = "Press Start, then in the game: Play > Find Match > Bot Match and pick a deck. Build decks in the normal deck editor."; break;
                case "host": lblHelp.Text = "Press Start, then in the game: Play > Find Match > Bot Match and pick a deck. It waits for your friend (start the host first). Windows may ask if Java can accept connections: allow it."; break;
                default: lblHelp.Text = "Wait until the host is waiting in their game, press Start, then in the game: Play > Find Match > Bot Match and pick a deck. You both need the same MTGA version and the same MTGA-Forge package."; break;
            }
            if (m == "host")
            {
                lstAddr.Items.Clear();
                var addrs = MyAddresses();
                foreach (var a in addrs) lstAddr.Items.Add(a.Item1);
                if (lstAddr.Items.Count > 0) lstAddr.SelectedIndex = 0;
                lblTs.Text = addrs.Any(a => a.Item3 == 0)
                    ? "Tailscale/ZeroTier found: that address works for a friend anywhere."
                    : "No Tailscale/ZeroTier found. For a friend outside your home network install Tailscale (free, tailscale.com) on both PCs and use the 100.x address it shows. The other addresses only work inside one network.";
            }
        }

        /// <summary>(text, address, rank) of this PC's IPv4 addresses, best first.</summary>
        static List<Tuple<string, string, int>> MyAddresses()
        {
            var res = new List<Tuple<string, string, int>>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                    var b = ua.Address.GetAddressBytes();
                    string kind = "local network"; int rank = 2;
                    if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) { kind = "Tailscale - works over the internet"; rank = 0; }
                    else if (nic.Name.IndexOf("ZeroTier", StringComparison.OrdinalIgnoreCase) >= 0 || nic.Description.IndexOf("ZeroTier", StringComparison.OrdinalIgnoreCase) >= 0) { kind = "ZeroTier - works over the internet"; rank = 0; }
                    else if (nic.Name.StartsWith("vEthernet") || nic.Description.IndexOf("VMware", StringComparison.OrdinalIgnoreCase) >= 0 || nic.Description.IndexOf("VirtualBox", StringComparison.OrdinalIgnoreCase) >= 0) { kind = "virtual adapter - probably not this"; rank = 3; }
                    string addr = ip + ":" + PeerPort;
                    res.Add(Tuple.Create(addr + "   (" + kind + ")", addr, rank));
                }
            }
            return res.OrderBy(t => t.Item3).ThenBy(t => t.Item2).ToList();
        }

        void CopyAddress()
        {
            if (lstAddr.SelectedItem == null) return;
            string addr = ((string)lstAddr.SelectedItem).Split(' ')[0];
            try { Clipboard.SetText(addr); lblStatus.Text = "Address copied: " + addr; } catch { lblStatus.Text = "Could not use the clipboard; type " + addr; }
        }

        static bool SplitPeer(string text, out string host, out int port)
        {
            host = ""; port = PeerPort;
            text = (text ?? "").Trim();
            if (text == "") return false;
            int c = text.LastIndexOf(':');
            if (c > 0 && int.TryParse(text.Substring(c + 1), out port)) host = text.Substring(0, c);
            else { host = text; port = PeerPort; }
            return host != "";
        }

        async void TestConnection()
        {
            string host; int port;
            if (!SplitPeer(txtPeer.Text, out host, out port)) { lblTest.Text = "Type your friend's address first."; return; }
            lblTest.Text = "Trying " + host + ":" + port + " ...";
            btnTest.Enabled = false;
            bool ok = false; string why = "";
            try
            {
                using (var c = new TcpClient())
                {
                    var t = c.ConnectAsync(host, port);
                    if (await Task.WhenAny(t, Task.Delay(4000)) == t && t.Status == TaskStatus.RanToCompletion && c.Connected) ok = true;
                    else why = t.IsFaulted && t.Exception != null ? t.Exception.GetBaseException().Message : "no answer within 4 seconds";
                }
            }
            catch (Exception e) { why = e.Message; }
            btnTest.Enabled = true;
            lblTest.Text = ok
                ? "Reached " + host + ":" + port + ": your friend's MTGA-Forge is listening. They still have to press Play in their game before you do."
                : "Could not reach " + host + ":" + port + " (" + why + "). Check: the address is right, your friend started MTGA-Forge with \"I host\", both PCs are connected to Tailscale/ZeroTier, and their firewall allowed Java.";
        }

        // ---------------------------------------------------------------------------------------------- run the game
        void SetRunning(bool running)
        {
            foreach (Control c in new Control[] { btnStart, txtName, rbBot, rbHost, rbJoin, txtPeer, txtKey, txtFolder, btnBrowse, btnTest }) c.Enabled = !running;
            btnStop.Enabled = running;
        }

        string BuildArgs()
        {
            var a = new StringBuilder("-NoPause -Folder " + Q(Folder));
            if (set["source"] != "") a.Append(" -Source " + Q(set["source"]));
            if (txtName.Text.Trim() != "") a.Append(" -Name " + Q(txtName.Text.Trim()));
            if (Mode != "bot")
            {
                a.Append(" -P2P " + Mode);
                if (Mode == "join") a.Append(" -Peer " + Q(txtPeer.Text.Trim()));
                if (txtKey.Text.Trim() != "") a.Append(" -PeerKey " + Q(txtKey.Text.Trim()));
            }
            return a.ToString();
        }

        void StartGame()
        {
            if (busy) return;
            if (Mode == "join" && txtPeer.Text.Trim() == "") { MessageBox.Show(this, "Type your friend's address first.", "MTGA-Forge"); return; }
            if (txtName.Text.Trim() == "") { MessageBox.Show(this, "Type a name first.", "MTGA-Forge"); return; }
            SaveSettings();
            log.Text = "";
            serverLog = null; serverLogPos = 0;
            busy = true;
            SetRunning(true);
            lblStatus.Text = "Starting...";
            runner = new Runner(this, Path.Combine(tools, "launcher.ps1"), BuildArgs(), OnLauncherLine, code =>
            {
                busy = false;
                logTimer.Stop();
                SetRunning(false);
                lblStatus.Text = code == 0 ? "Closed. Start again any time." : "Stopped (exit code " + code + "). See the messages below.";
                runner = null;
            });
        }

        void Log(string line)
        {
            log.AppendText(line + "\r\n");
        }

        void OnLauncherLine(string l)
        {
            if (l.Trim() == "") return;
            Log(l.Trim());
            var m = Regex.Match(l, @"Starting the Forge server \(log: (.+)\)");
            if (m.Success) { serverLog = m.Groups[1].Value; serverLogPos = 0; logTimer.Start(); lblStatus.Text = "Starting the server..."; }
            else if (l.Contains("Patching the game")) lblStatus.Text = "Updating the patched game first (a few minutes)...";
            else if (l.Contains("Server ready")) lblStatus.Text = "Server ready, starting the game...";
            else if (l.Contains("MTGA started")) lblStatus.Text = Mode == "bot" ? "Game running. Close the game window to finish." : "Game running. Now pick Bot Match and a deck in the game.";
        }

        /// <summary>The server's log carries the connection events: translate them into plain sentences.</summary>
        void TailServerLog()
        {
            if (serverLog == null || !File.Exists(serverLog)) return;
            string text;
            try
            {
                using (var fs = new FileStream(serverLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length <= serverLogPos) return;
                    fs.Position = serverLogPos;
                    var buf = new byte[fs.Length - serverLogPos];
                    int n = fs.Read(buf, 0, buf.Length);
                    serverLogPos += n;
                    text = Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch { return; }
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.Trim();
                if (l == "") continue;
                Match m;
                if (l.Contains("peer: waiting for a guest")) lblStatus.Text = "Your deck is in. Waiting for your friend to join...";
                else if ((m = Regex.Match(l, @"peer: guest '(.+)' joined")).Success) lblStatus.Text = m.Groups[1].Value + " joined. The game is starting.";
                else if ((m = Regex.Match(l, @"peer: rejected [^:]+: (.+)$")).Success) lblStatus.Text = "A guest was turned away: " + m.Groups[1].Value;
                else if ((m = Regex.Match(l, @"peer: join failed: (.+)$")).Success) lblStatus.Text = "Could not join: " + m.Groups[1].Value;
                else if (l.Contains("peer: joined ")) lblStatus.Text = "Connected to the host. The game is starting.";
                else if (l.Contains("[mtga] game over")) lblStatus.Text = "Game over. Close the game window to finish.";
                else if (l.Contains("game thread died")) lblStatus.Text = "The game stopped unexpectedly (see the server log).";
                else continue;
                Log(l);
            }
        }

        // ---------------------------------------------------------------------------------------------- picture / smoke test
        public int SelfTest(string shot)
        {
            selfTest = true;
            var report = new StringBuilder();
            report.AppendLine("root " + root + ", tools " + (Directory.Exists(tools) ? "ok" : "MISSING"));
            foreach (var mode in new[] { "bot", "host", "join" })
            {
                (mode == "host" ? rbHost : mode == "join" ? rbJoin : rbBot).Checked = true;
                if (mode == "join") txtPeer.Text = "100.64.1.2:30012";
                report.AppendLine(mode + ": launcher.ps1 " + BuildArgs());
            }
            if (shot != null)
            {
                rbHost.Checked = true;
                ShowPage(true);
                Save(shot);
                ShowSetup("not patched yet");
                bar.Style = ProgressBarStyle.Blocks; bar.Maximum = PatchSteps.Length; bar.Value = 3;
                lblSetupStatus.Text = "Step 4 of 9: " + Describe("unityconnect");
                ShowPage(false);
                Save(Path.ChangeExtension(shot, null) + "-setup.png");
            }
            report.AppendLine("SELFTEST OK");
            if (shot != null) File.WriteAllText(Path.ChangeExtension(shot, null) + ".txt", report.ToString());
            return 0;
        }

        /// <summary>--check &lt;folder&gt; &lt;out.txt&gt;: run the real readiness check against a folder, write which page it ends on.</summary>
        public int CheckProbe(string folder, string outFile, bool prepare)
        {
            selfTest = true;
            txtFolder.Text = folder;
            var t = new Timer { Interval = 300 };
            DateTime end = DateTime.Now.AddSeconds(120);
            bool started = false, prepared = false;
            Shown += (s, e) => { started = true; RunCheck(); };
            t.Tick += (s, e) =>
            {
                if (!started || (busy && DateTime.Now < end)) return;
                if (prepare && !prepared && !pgPlay.Visible && btnPrepare.Enabled && DateTime.Now < end) { prepared = true; end = DateTime.Now.AddMinutes(30); Prepare(); return; }
                t.Stop();
                File.WriteAllText(outFile, (pgPlay.Visible ? "play" : "setup: " + lblSetupTitle.Text + " | " + lblSetupText.Text.Replace(Environment.NewLine, " ")) + Environment.NewLine);
                Close();
            };
            t.Start();
            Application.Run(this);
            return 0;
        }

        void Save(string path)
        {
            Show(); Refresh();
            using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}

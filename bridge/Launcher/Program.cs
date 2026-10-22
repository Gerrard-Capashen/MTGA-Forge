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
// the friend code / offline choice, and shows what is happening. The work is done by tools\forge-patch.ps1 (check/patch)
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
        static readonly string[] PatchSteps = { "refresh", "orig", "identity", "unityconnect", "storeconfig", "bridge", "patch", "carddb", "manifest" };

        readonly string root;
        readonly string tools;
        readonly string homeDefault = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? ".", "MTGA-Forge");
        readonly string iniPath;
        readonly Dictionary<string, string> set = new Dictionary<string, string>
            { { "name", "Forge Player" }, { "code", "" }, { "offline", "0" }, { "folder", "" }, { "source", "" } };

        // setup page
        readonly Panel pgSetup = new Panel();
        readonly Label lblSetupTitle = new Label(), lblSetupText = new Label(), lblDisk = new Label(), lblSetupStatus = new Label();
        readonly ProgressBar bar = new ProgressBar();
        readonly Button btnPrepare = new Button(), btnSource = new Button(), btnRecheck = new Button();
        // play page
        readonly Panel pgPlay = new Panel();
        readonly Label lblProfile = new Label();
        readonly TextBox txtName = new TextBox(), txtCode = new TextBox(), txtFolder = new TextBox();
        readonly CheckBox chkOffline = new CheckBox();
        readonly Label lblHelp = new Label(), lblStatus = new Label();
        readonly Button btnBrowse = new Button(), btnStart = new Button(), btnStop = new Button();
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
            set["name"] = txtName.Text; set["code"] = FriendCode; set["offline"] = chkOffline.Checked ? "1" : "0"; set["folder"] = txtFolder.Text;
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
            ClientSize = new Size(640, 530);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            // ---- setup page
            At(pgSetup, 0, 0, 640, 530);
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
            At(pgPlay, 0, 0, 640, 530);
            pgPlay.Controls.Add(At(new Label { Text = "Your name in the game" }, 20, 14, 300, 20));
            txtName.Text = set["name"];
            txtName.MaxLength = 24;
            pgPlay.Controls.Add(At(txtName, 20, 36, 280, 24));
            lblProfile.Text = ProfileNote();
            lblProfile.ForeColor = SystemColors.GrayText;
            pgPlay.Controls.Add(At(lblProfile, 310, 40, 310, 20));

            var grp = At(new GroupBox { Text = "Playing" }, 20, 74, 600, 134);
            grp.Controls.Add(At(new Label { Text = "Bot Play (against the Forge AI) always works. Casual Play finds another player online for the same format: nothing to set up.", AutoSize = false }, 16, 22, 570, 34));
            grp.Controls.Add(At(new Label { Text = "Friend code (optional)" }, 16, 62, 160, 20));
            txtCode.Text = set["code"];
            txtCode.MaxLength = 24;
            grp.Controls.Add(At(txtCode, 180, 59, 200, 24));
            grp.Controls.Add(At(new Label { Text = "To play a friend: both type the same code (like blue-otter-7), then both pick the same Casual Play format. Empty = random opponents.", AutoSize = false, ForeColor = SystemColors.GrayText }, 16, 88, 570, 32));
            pgPlay.Controls.Add(grp);

            pgPlay.Controls.Add(At(new Label { Text = "Game folder (the patched copy of MTGA and your decks live here)" }, 20, 218, 600, 20));
            txtFolder.Text = set["folder"];
            pgPlay.Controls.Add(At(txtFolder, 20, 240, 480, 24));
            btnBrowse.Text = "Browse...";
            btnBrowse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Folder for the patched game copy (about 15 GB) and your decks", SelectedPath = Folder })
                    if (d.ShowDialog(this) == DialogResult.OK) { txtFolder.Text = d.SelectedPath; RunCheck(); }
            };
            pgPlay.Controls.Add(At(btnBrowse, 510, 238, 110, 28));

            chkOffline.Text = "Offline: no online play (Casual Play is switched off, Bot Play works)";
            chkOffline.Checked = set["offline"] == "1";
            pgPlay.Controls.Add(At(chkOffline, 20, 274, 600, 22));

            lblHelp.AutoSize = false;
            pgPlay.Controls.Add(At(lblHelp, 20, 302, 600, 54));
            btnStart.Text = "Start";
            btnStop.Text = "Stop";
            btnStop.Enabled = false;
            btnStart.Click += (s, e) => StartGame();
            btnStop.Click += (s, e) => runner?.KillTree();
            pgPlay.Controls.Add(At(btnStart, 20, 364, 120, 30));
            pgPlay.Controls.Add(At(btnStop, 150, 364, 120, 30));
            lblStatus.AutoSize = false;
            pgPlay.Controls.Add(At(lblStatus, 285, 364, 335, 34));
            log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.Font = new Font("Consolas", 8f);
            pgPlay.Controls.Add(At(log, 20, 404, 600, 116));

            Controls.Add(pgPlay);
            Controls.Add(pgSetup);
            pgPlay.Visible = false;

            chkOffline.CheckedChanged += (s, e) => UpdateMode();
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
        /// <summary>The friend code as typed, reduced to letters, digits, '-' and '_' (it goes on a command line and into a URL).</summary>
        string FriendCode { get { return Regex.Replace(txtCode.Text.Trim(), "[^A-Za-z0-9_-]", ""); } }

        void UpdateMode()
        {
            txtCode.Enabled = !chkOffline.Checked && !busy;
            lblHelp.Text = chkOffline.Checked
                ? "Press Start, then in the game: Play > Find Match > Bot Play, choose Standard, Pioneer or Modern and pick a deck of that format. Build decks in the normal deck editor."
                : "Press Start, then in the game: Play > Find Match. Bot Play: against the Forge AI. Casual Play: against another player online; choose Standard, Pioneer or Modern and a deck of that format. You both need the same MTGA version and the same MTGA-Forge package.";
        }

        // ---------------------------------------------------------------------------------------------- run the game
        void SetRunning(bool running)
        {
            foreach (Control c in new Control[] { btnStart, txtName, txtCode, chkOffline, txtFolder, btnBrowse }) c.Enabled = !running;
            btnStop.Enabled = running;
            if (!running) UpdateMode();
        }

        string BuildArgs()
        {
            var a = new StringBuilder("-NoPause -Folder " + Q(Folder));
            if (set["source"] != "") a.Append(" -Source " + Q(set["source"]));
            if (txtName.Text.Trim() != "") a.Append(" -Name " + Q(txtName.Text.Trim()));
            if (chkOffline.Checked) a.Append(" -Offline");
            else if (FriendCode != "") a.Append(" -FriendCode " + Q(FriendCode));
            return a.ToString();
        }

        void StartGame()
        {
            if (busy) return;
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
            else if (l.Contains("MTGA started")) lblStatus.Text = "Game running. Pick Bot Play or Casual Play, a format and a deck. Close the game window to finish.";
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
                if ((m = Regex.Match(l, @"FD online: '.*' queues in \S+ with friend code (\S+)")).Success) lblStatus.Text = "Waiting for your friend (code " + m.Groups[1].Value + ")...";
                else if (Regex.IsMatch(l, @"FD online: '.*' queues in ")) lblStatus.Text = "In the queue. Waiting for an opponent...";
                else if ((m = Regex.Match(l, @"FD online: paired with (.+) as (host|guest)")).Success) lblStatus.Text = "Matched with " + m.Groups[1].Value + ". The game is starting.";
                else if ((m = Regex.Match(l, @"FD online: no match \((.+)\)")).Success) lblStatus.Text = "No match: " + m.Groups[1].Value;
                else if ((m = Regex.Match(l, @"FD online: join failed: (.+)$")).Success) lblStatus.Text = "Could not join: " + m.Groups[1].Value;
                else if (l.Contains("matchmaking server unreachable")) lblStatus.Text = "Can't reach the matchmaking server: check your internet connection.";
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
            report.AppendLine("online: launcher.ps1 " + BuildArgs());
            txtCode.Text = "blue-otter-7";
            report.AppendLine("friend: launcher.ps1 " + BuildArgs());
            chkOffline.Checked = true;
            report.AppendLine("offline: launcher.ps1 " + BuildArgs());
            chkOffline.Checked = false;
            if (shot != null)
            {
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

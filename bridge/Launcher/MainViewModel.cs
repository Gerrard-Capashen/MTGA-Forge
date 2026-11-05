using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MtgaForgeLauncher
{
    enum Stage { Checking, NeedSource, NeedPrepare, Preparing, Ready, Running, Problem }

    /// <summary>0 = still to do, 1 = current, 2 = done.</summary>
    sealed class StepItem : Bindable
    {
        int state;
        public string Title { get; set; }
        public int State { get { return state; } set { Set(ref state, value); } }
    }

    sealed class RailItem : Bindable
    {
        int state;
        public string Number { get; set; }
        public string Title { get; set; }
        public string Hint { get; set; }
        public int State { get { return state; } set { Set(ref state, value); } }
    }

    sealed class SourceItem
    {
        public string Path { get; set; }
        public string Note { get; set; }
    }

    /// <summary>
    /// The state of the window: which of the guided steps we are at and what the scripts report. The work itself is done by
    /// tools\forge-patch.ps1 (check) and tools\launcher.ps1 (patch, server, game), run as child processes whose output goes to the console panel.
    /// </summary>
    sealed class MainViewModel : Bindable
    {
        // the steps of launcher.ps1 -PatchOnly as it prints them ("== name") and what each one does, in plain words
        static readonly string[][] PatchSteps =
        {
            new[] { "refresh", "Copying your MTGA files (the long one)" },
            new[] { "orig", "Saving the original program files" },
            new[] { "identity", "Giving the copy its own identity" },
            new[] { "unityconnect", "Switching off analytics" },
            new[] { "storeconfig", "Switching off the store" },
            new[] { "bridge", "Installing the Forge connection" },
            new[] { "patch", "Patching the game" },
            new[] { "carddb", "Building the card database" },
            new[] { "manifest", "Finishing" },
        };

        readonly Dispatcher ui = Application.Current.Dispatcher;
        readonly Settings settings = new Settings();
        readonly string tools;
        readonly Dictionary<string, long> sizes = new Dictionary<string, long>();
        readonly List<string> log = new List<string>();
        readonly DispatcherTimer logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        ScriptRunner runner;
        ServerLog serverLog;
        bool busy, editing;

        /// <summary>False for --probe runs, which must not touch the saved settings.</summary>
        public bool Persist { get; set; } = true;

        Stage stage = Stage.Checking;
        string headline = "Looking for your game...", body = "", sourceNote = "", folder = "", diskInfo = "", folderProblem = "";
        string name = "", playStatus = "", profileNote = "";
        bool offline, consoleOpen, diskLow, firewallOn, updateAvailable, updateReady;
        string updateText = "", updateUrl = "";
        UpdateRelease pendingRelease;
        List<UpdatePart> pendingParts;
        SourceItem selectedSource;
        double progress;

        public ObservableCollection<SourceItem> Sources { get; } = new ObservableCollection<SourceItem>();
        public ObservableCollection<StepItem> Steps { get; } = new ObservableCollection<StepItem>();
        public ObservableCollection<RailItem> Rail { get; } = new ObservableCollection<RailItem>
        {
            new RailItem { Number = "1", Title = "Find MTGA", Hint = "Your MTGA install, read only" },
            new RailItem { Number = "2", Title = "Pick a folder", Hint = "Where the patched copy goes" },
            new RailItem { Number = "3", Title = "Prepare the game", Hint = "Copy and patch, once" },
            new RailItem { Number = "4", Title = "Play", Hint = "Start the server and the game" },
        };

        public ICommand PrepareCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RecheckCommand { get; }
        public ICommand ChangeLocationCommand { get; }
        public ICommand DefaultFolderCommand { get; }
        public ICommand ToggleConsoleCommand { get; }
        public ICommand OpenUpdateCommand { get; }
        public ICommand DismissUpdateCommand { get; }
        public ICommand UpdateNowCommand { get; }

        /// <summary>A line for the console panel.</summary>
        public event Action<string> LogAdded;

        public MainViewModel(bool demo, string folderOverride = null)
        {
            tools = Path.Combine(FindRoot(), "tools");
            // a known update comes first: the game is prepared and started only with the newest tools (players on other versions can't meet online)
            PrepareCommand = new RelayCommand(Prepare, () => !busy && !updateReady && (stage == Stage.NeedPrepare || stage == Stage.Problem) && HasSource && folderProblem == "");
            StartCommand = new RelayCommand(Start, () => !busy && !updateReady && stage == Stage.Ready);
            UpdateNowCommand = new RelayCommand(InstallUpdate, () => !busy && updateReady && stage != Stage.Running && stage != Stage.Preparing);
            StopCommand = new RelayCommand(() => { if (runner != null) runner.KillTree(); }, () => stage == Stage.Running);
            RecheckCommand = new RelayCommand(Recheck, () => !busy && stage != Stage.Running);
            ChangeLocationCommand = new RelayCommand(() => { editing = true; RaiseStage(); }, () => stage == Stage.Ready);
            DefaultFolderCommand = new RelayCommand(() => SetFolder(Settings.DefaultFolder), () => !busy && stage != Stage.Running);
            ToggleConsoleCommand = new RelayCommand(() => ConsoleOpen = !ConsoleOpen);
            OpenUpdateCommand = new RelayCommand(OpenUpdate);
            DismissUpdateCommand = new RelayCommand(() => UpdateAvailable = false);
            logTimer.Tick += (s, e) => PollServerLog();

            settings.Load();
            folder = string.IsNullOrEmpty(folderOverride) ? settings.Folder : folderOverride; name = settings.Name; offline = settings.Offline; consoleOpen = settings.ConsoleOpen;
            profileNote = Profile.Note(folder);
            foreach (var s in PatchSteps) Steps.Add(new StepItem { Title = s[1] });
            if (!demo) { RefreshFirewall(); ui.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)CheckForUpdate); }
            if (!demo) Task.Run(() => MtgaLocator.Find()).ContinueWith(t => ui.BeginInvoke((Action)(() => OnSourcesFound(t.Result))));
        }

        // ------------------------------------------------------------------------------------------ bound properties
        public Stage Stage { get { return stage; } private set { if (Set(ref stage, value)) RaiseStage(); } }
        public string Headline { get { return headline; } private set { Set(ref headline, value); } }
        public string Body { get { return body; } private set { Set(ref body, value); } }
        public string SourceNote { get { return sourceNote; } private set { Set(ref sourceNote, value); } }
        public string DiskInfo { get { return diskInfo; } private set { Set(ref diskInfo, value); } }
        public bool DiskLow { get { return diskLow; } private set { Set(ref diskLow, value); } }
        public string FolderProblem { get { return folderProblem; } private set { if (Set(ref folderProblem, value)) { Raise("HasFolderProblem"); UpdateRail(); } } }
        public bool HasFolderProblem { get { return folderProblem != ""; } }
        public string PlayStatus { get { return playStatus; } private set { Set(ref playStatus, value); } }
        public string ProfileNote { get { return profileNote; } private set { Set(ref profileNote, value); } }
        public double Progress { get { return progress; } private set { Set(ref progress, value); } }
        public double ProgressMax { get { return PatchSteps.Length; } }

        public string Folder
        {
            get { return folder; }
            set { if (value != null && value.Trim() != folder) SetFolder(value.Trim()); else Raise("Folder"); }
        }

        public SourceItem SelectedSource
        {
            get { return selectedSource; }
            set
            {
                if (Equals(selectedSource, value) || value == null) { Raise("SelectedSource"); return; }
                selectedSource = value;
                Raise("SelectedSource"); Raise("HasSource");
                UpdateDisk();
                if (!busy) Recheck();
            }
        }

        public bool HasSource { get { return selectedSource != null; } }
        public string Name { get { return name; } set { Set(ref name, value); } }
        public bool Offline { get { return offline; } set { if (Set(ref offline, value)) Raise("OnlineEnabled"); } }
        public bool UpdateAvailable { get { return updateAvailable; } private set { if (Set(ref updateAvailable, value)) Raise("UpdateLinkVisible"); } }
        /// <summary>A signed update can be installed from here (the banner's Update now button; Start waits for it).</summary>
        public bool UpdateReady { get { return updateReady; } private set { if (Set(ref updateReady, value)) { Raise("UpdateLinkVisible"); CommandManager.InvalidateRequerySuggested(); } } }
        /// <summary>Only a link to the download (no installable update list): the old banner with Get the update / Dismiss.</summary>
        public bool UpdateLinkVisible { get { return updateAvailable && !updateReady; } }
        public string UpdateText { get { return updateText; } private set { Set(ref updateText, value); } }
        public bool FirewallOn { get { return firewallOn; } }
        public string FirewallTip { get { return Firewall.Explanation; } }
        public bool OnlineEnabled { get { return !offline; } }
        public bool ConsoleOpen { get { return consoleOpen; } set { if (Set(ref consoleOpen, value)) settings.ConsoleOpen = value; } }

        public bool SetupVisible { get { return (stage != Stage.Ready && stage != Stage.Running) || editing; } }
        public bool PlayVisible { get { return !SetupVisible; } }
        public bool IsPreparing { get { return stage == Stage.Preparing; } }
        public bool NotPreparing { get { return stage != Stage.Preparing; } }
        public bool CanUninstall { get { return !busy && stage != Stage.Running && stage != Stage.Preparing && stage != Stage.Checking; } }
        public string CurrentSource { get { return SourcePath; } }
        public bool IsRunning { get { return stage == Stage.Running; } }
        public bool CanEditFields { get { return stage != Stage.Running && stage != Stage.Preparing && stage != Stage.Checking; } }
        public string PrepareLabel { get { return stage == Stage.Problem ? "Try again" : "Prepare my game"; } }

        public string PillText
        {
            get
            {
                switch (stage)
                {
                    case Stage.Checking: return "Checking your game";
                    case Stage.Preparing: return "Preparing";
                    case Stage.Ready: return "Ready";
                    case Stage.Running: return "Playing";
                    case Stage.Problem: return "Needs attention";
                    default: return "Setup";
                }
            }
        }

        public string PlayHelp
        {
            get
            {
                return offline
                    ? "Press Start, then in the game go to Play > Find Match > Bot Play, pick a format and a deck, and you are in."
                    : "Press Start, then in the game go to Play > Find Match for ranked and casual games in Standard, Pioneer, Modern, Legacy, Vintage, Pauper and Premodern (Brawl has its own tab, casual only), or to Events to draft. Challenge a friend from the Friends list any time.";
            }
        }

        void RaiseStage()
        {
            foreach (var p in new[] { "Stage", "SetupVisible", "PlayVisible", "IsPreparing", "NotPreparing", "IsRunning", "CanEditFields", "CanUninstall", "PrepareLabel", "PillText" }) Raise(p);
            UpdateRail();
            CommandManager.InvalidateRequerySuggested();
        }

        void UpdateRail()
        {
            bool src = HasSource, fold = folderProblem == "";
            int active = stage == Stage.Ready || stage == Stage.Running ? 3 : !src ? 0 : !fold ? 1 : 2;
            if (stage == Stage.Checking) active = src ? 2 : 0;
            for (int i = 0; i < Rail.Count; i++)
                Rail[i].State = i < active ? 2 : i == active ? 1 : 0;
            if (stage == Stage.Ready || stage == Stage.Running) Rail[3].State = 1;
        }

        // ------------------------------------------------------------------------------------------ locations
        static string FindRoot()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            for (int i = 0; i < 6 && d != null; i++, d = Path.GetDirectoryName(d))
                if (File.Exists(Path.Combine(d, "tools", "launcher.ps1"))) return d;
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        }

        static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "") + "\""; }

        string SourcePath { get { return selectedSource == null ? "" : selectedSource.Path; } }
        public string LogsFolder { get { return Path.Combine(folder, "logs"); } }
        public string DataFolder { get { return folder; } }

        void OnSourcesFound(List<string> found)
        {
            foreach (var p in found) Sources.Add(new SourceItem { Path = p, Note = MtgaLocator.Describe(p) });
            if (settings.Source != "" && MtgaLocator.IsMtga(settings.Source) && !Sources.Any(s => SamePath(s.Path, settings.Source)))
                Sources.Insert(0, new SourceItem { Path = settings.Source, Note = "Chosen by you" });
            var pick = Sources.FirstOrDefault(s => settings.Source != "" && SamePath(s.Path, settings.Source)) ?? Sources.FirstOrDefault();
            SourceNote = found.Count == 0 ? "Not found automatically. Press Browse and choose the folder that contains MTGA.exe."
                       : found.Count == 1 ? "Found automatically." : "Found " + found.Count + " installs automatically.";
            ValidateFolder();
            if (pick != null) { selectedSource = pick; Raise("SelectedSource"); Raise("HasSource"); UpdateDisk(); }
            Recheck();
        }

        static bool SamePath(string a, string b) { return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }

        /// <summary>The player picked a folder with MTGA.exe (Browse).</summary>
        public string AddSource(string dir)
        {
            if (!MtgaLocator.IsMtga(dir)) return "MTGA.exe is not in that folder.";
            var item = Sources.FirstOrDefault(s => SamePath(s.Path, dir));
            if (item == null) { item = new SourceItem { Path = dir, Note = "Chosen by you" }; Sources.Insert(0, item); }
            SourceNote = "";
            SelectedSource = item;
            return null;
        }

        public void SetFolder(string dir)
        {
            folder = dir; Raise("Folder");
            profileNote = Profile.Note(folder); Raise("ProfileNote");
            ValidateFolder();
            UpdateDisk();
            RefreshFirewall();
            if (!busy) Recheck();
        }

        /// <summary>Can the copy go there? Checked by writing a small file, because Program Files and the like refuse a normal program.</summary>
        void ValidateFolder()
        {
            string problem = "";
            try
            {
                string full = Path.GetFullPath(folder);
                if (!Path.IsPathRooted(folder)) problem = "Type a full path, like D:\\Games\\MTGA Forge.";
                else if (full.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase) >= 0) problem = "Not inside a Steam library: Steam would treat it as part of the game.";
                else if (SourcePath != "" && (full.StartsWith(SourcePath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || SamePath(full, SourcePath)))
                    problem = "That is the MTGA install itself. Choose another folder.";
                else if (full.StartsWith(Path.GetDirectoryName(tools).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || SamePath(full, Path.GetDirectoryName(tools)))
                    problem = "That is the MTGA-Forge download folder itself. Choose another folder, so the download stays clean.";
                else
                {
                    // test the nearest folder that exists, so looking at a folder never creates it
                    string where = full;
                    while (where != null && !Directory.Exists(where)) where = Path.GetDirectoryName(where);
                    if (where == null) throw new DirectoryNotFoundException("The drive does not exist.");
                    // a folder that exists is tested by writing a file into it; one that doesn't yet is tested by creating a folder in its
                    // parent, because that is what will happen (a normal user may create folders in C:\ but not files)
                    string probe = Path.Combine(where, ".write-test-" + Guid.NewGuid().ToString("N"));
                    if (SamePath(where, full)) { File.WriteAllText(probe, "x"); File.Delete(probe); }
                    else { Directory.CreateDirectory(probe); Directory.Delete(probe); }
                }
            }
            catch (UnauthorizedAccessException) { problem = "Windows does not let programs write there without administrator rights (Program Files does this). Choose another folder."; }
            catch (Exception e) { problem = "Can't use that folder: " + e.Message; }
            FolderProblem = problem;
        }

        async void UpdateDisk()
        {
            string src = SourcePath, dir = folder;
            long need = 0;
            if (src != "")
            {
                if (!sizes.TryGetValue(src, out need)) { need = await Task.Run(() => MtgaLocator.SizeOf(src)); sizes[src] = need; }
                need += 2L * 1024 * 1024 * 1024;     // room for the card database
            }
            try
            {
                var d = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)));
                long free = d.AvailableFreeSpace;
                const double GB = 1024.0 * 1024 * 1024;
                DiskInfo = need > 0 ? string.Format("The copy needs about {0:0} GB. {1:0} GB free on {2}", need / GB, free / GB, d.Name.TrimEnd('\\'))
                                    : string.Format("{0:0} GB free on {1}", free / GB, d.Name.TrimEnd('\\'));
                DiskLow = need > 0 && free < need;
            }
            catch { DiskInfo = ""; DiskLow = false; }
        }

        // ------------------------------------------------------------------------------------------ is the game ready?
        public void Recheck()
        {
            if (busy) return;
            if (!HasSource)
            {
                Stage = Stage.NeedSource;
                Headline = Sources.Count == 0 ? "MTGA was not found" : "Choose your MTGA install";
                Body = "Install MTG Arena (Steam or the standalone installer), or choose the folder that contains MTGA.exe. It is only read, never changed.";
                return;
            }
            if (folderProblem != "")
            {
                Stage = Stage.NeedSource;
                Headline = "Choose another folder";
                Body = "The patched copy needs a folder this program can write to.";
                return;
            }
            Stage = Stage.Checking;
            Headline = "Checking your game...";
            Body = "";
            busy = true;
            string last = "";
            string args = "-Target " + Q(Path.Combine(folder, "client")) + " -Source " + Q(SourcePath) + " -Check";
            Run("forge-patch.ps1", args, l => { if (l.Trim() != "") last = l.Trim(); }, code =>
            {
                busy = false;
                runner = null;
                switch (code)
                {
                    case 0: editing = false; Headline = "Ready to play!"; Stage = Stage.Ready; break;
                    case 3: ShowNeedsPrepare(last); break;
                    case 4: Stage = Stage.NeedSource; Headline = "MTGA was not found"; Body = "Choose the folder that contains MTGA.exe."; break;
                    case 5: ShowProblem("Steam is still updating MTGA", "Wait until Steam has finished, then press Check again."); break;
                    case 6: ShowProblem("MTGA has not finished downloading", "MTG Arena downloads most of its game files (card art, the card database) after you sign in, before it shows the main menu. Start MTG Arena once and sign in. Once you see the main menu, close it and press Check again."); break;
                    default: ShowProblem("Could not check your game", "The check failed (exit code " + code + "): " + last + " Open the details below for the full output."); break;
                }
            });
        }

        void ShowNeedsPrepare(string why)
        {
            bool first = why.IndexOf("not patched yet", StringComparison.OrdinalIgnoreCase) >= 0 || why.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (var s in Steps) s.State = 0;
            Progress = 0;
            Headline = first ? "First start: prepare your game" : "Your game needs an update";
            Body = first
                ? "MTGA Forge makes a separate copy of your MTGA install and patches it to talk to the Forge rules engine. Your MTGA install is only read. The copy never signs in to MTGA's servers."
                : "MTGA was updated, or this version of MTGA Forge changed, so the patched copy is rebuilt. Your decks stay.";
            Stage = Stage.NeedPrepare;
        }

        void ShowProblem(string title, string text) { Headline = title; Body = text; Stage = Stage.Problem; }

        // ------------------------------------------------------------------------------------------ prepare
        void Prepare()
        {
            if (busy) return;
            busy = true;
            foreach (var s in Steps) s.State = 0;
            Progress = 0;
            Headline = "Preparing your game";
            Body = "This takes several minutes, mostly copying files. You can do something else meanwhile.";
            Stage = Stage.Preparing;
            string args = "-NoPause -PatchOnly -Folder " + Q(folder) + " -Source " + Q(SourcePath);
            Run("launcher.ps1", args, l =>
            {
                var m = Regex.Match(l, @"^== (\w+)");
                if (!m.Success) return;
                int i = Array.FindIndex(PatchSteps, s => s[0] == m.Groups[1].Value);
                if (i < 0) return;
                for (int k = 0; k < Steps.Count; k++) Steps[k].State = k < i ? 2 : k == i ? 1 : 0;
                Progress = i;
            }, code =>
            {
                busy = false;
                var lines = runner == null ? new List<string>() : runner.Lines.Where(x => x.Trim() != "").Reverse().Take(3).Reverse().ToList();
                runner = null;
                if (code == 0) { foreach (var s in Steps) s.State = 2; Progress = PatchSteps.Length; Recheck(); return; }
                if (code == 6) { ShowProblem("MTGA has not finished downloading", "MTG Arena downloads most of its game files (card art, the card database) after you sign in, before it shows the main menu. Start MTG Arena once and sign in. Once you see the main menu, close it and press Check again."); return; }
                ShowProblem("Preparing the game failed", "Nothing was started and your MTGA install is untouched. " + string.Join(" ", lines));
                ConsoleOpen = true;
            });
        }

        // ------------------------------------------------------------------------------------------ play
        string StartArgs()
        {
            var a = new StringBuilder("-NoPause -Folder " + Q(folder) + " -Source " + Q(SourcePath));
            if (name.Trim() != "") a.Append(" -Name " + Q(name.Trim()));
            if (offline) a.Append(" -Offline");     // friends play each other through the game's own Friends list (direct challenges)
            return a.ToString();
        }

        void Start()
        {
            if (busy) return;
            if (name.Trim() == "") { PlayStatus = "Type a name first."; return; }
            settings.Name = name; settings.Offline = offline; settings.Folder = folder; settings.Source = SourcePath;
            settings.Save();
            serverLog = null;
            busy = true;
            PlayStatus = "Starting...";
            Stage = Stage.Running;
            Run("launcher.ps1", StartArgs(), OnLauncherLine, code =>
            {
                busy = false;
                logTimer.Stop();
                runner = null;
                PlayStatus = code == 0 ? "Closed. Start again any time." : "Stopped (exit code " + code + "). The details below show why.";
                if (code != 0) ConsoleOpen = true;
                Stage = Stage.Ready;
                ProfileNote = Profile.Note(folder);
            });
        }

        void OnLauncherLine(string l)
        {
            var m = Regex.Match(l, @"Starting the Forge server \(log: (.+)\)");
            if (m.Success) { serverLog = new ServerLog(m.Groups[1].Value); logTimer.Start(); PlayStatus = "Starting the server..."; }
            else if (l.Contains("Patching the game")) PlayStatus = "Updating the patched game first (a few minutes)...";
            else if (l.Contains("Server ready")) PlayStatus = "Server ready, starting the game...";
            else if (l.Contains("MTGA started")) PlayStatus = "Game running. Pick a queue or an event in the game. Close the game window to finish.";
        }

        void PollServerLog()
        {
            if (serverLog == null) return;
            foreach (var raw in serverLog.ReadNew())
            {
                string s = ServerLog.Translate(raw);
                if (s == null) continue;
                PlayStatus = s;
                Log(raw.Trim());
            }
        }

        // ------------------------------------------------------------------------------------------ updates
        string PackageRoot { get { return Path.GetDirectoryName(tools); } }

        /// <summary>
        /// On start: the latest release's signed update list (GitHub). A newer release shows "Update now" and holds Prepare / Start
        /// until it is installed. Without a usable list (offline, a release from before auto-update) the old website check shows a link.
        /// </summary>
        void CheckForUpdate()
        {
            DateTimeOffset built; string commit;
            if (!UpdateChecker.OwnVersion(PackageRoot, out built, out commit)) { Log("Update check skipped: this is a development copy."); return; }
            Log("Update check: this package is commit " + commit + ", built " + built.ToString("yyyy-MM-dd HH:mm") + ". Asking " + Updater.ManifestUrl);
            string error = ""; bool missing = false;
            string ua = "MTGA-Forge-Launcher/" + commit;
            Task.Run(() => Updater.Fetch(ua, out error, out missing)).ContinueWith(t => ui.BeginInvoke((Action)(() =>
            {
                var r = t.Result;
                if (r == null) { Log("Update check: no update list (" + error + ")" + (missing ? ", asking the website instead." : ". You can still play.")); if (missing) CheckWebsite(built, commit); return; }
                if (r.BuiltAt <= built.AddMinutes(1)) { Log("Update check: you have the latest version (" + r.Version + ")."); return; }
                var parts = Updater.Changed(r, PackageRoot);
                long size = parts.Sum(p => p.Size);
                Log("Update check: " + r.Version + " is available (commit " + r.Commit + "); parts to download: " +
                    (parts.Count == 0 ? "none" : string.Join(", ", parts.Select(p => p.Name + " " + (p.Size / 1048576.0).ToString("0.0") + " MB"))));
                pendingRelease = r; pendingParts = parts;
                UpdateText = "MTGA Forge " + r.Version + " is ready to install (" + (size / 1048576.0).ToString("0.0") + " MB). Update before you play: players on different versions can't meet online.";
                UpdateReady = true;
                UpdateAvailable = true;
            })));
        }

        /// <summary>Downloads and installs the pending update; restarts the launcher when its own files changed.</summary>
        void InstallUpdate()
        {
            if (busy || pendingRelease == null) return;
            busy = true;
            CommandManager.InvalidateRequerySuggested();
            var r = pendingRelease; var parts = pendingParts;
            string root = PackageRoot, ua = "MTGA-Forge-Launcher/" + r.Commit;
            Log("Update: installing " + r.Version + " into " + root);
            Action<string> progress = s => ui.BeginInvoke((Action)(() => UpdateText = s));
            Task.Run(() => Updater.Install(r, parts, root, ua, progress)).ContinueWith(t => ui.BeginInvoke((Action)(() =>
            {
                busy = false;
                if (t.IsFaulted)
                {
                    string why = t.Exception.GetBaseException().Message;
                    Log("Update failed: " + why);
                    UpdateText = "The update failed: " + why;
                    CommandManager.InvalidateRequerySuggested();
                    return;
                }
                Log("Update: " + r.Version + " installed.");
                if (t.Result)
                {
                    Log("Update: the launcher changed, restarting it.");
                    try { Updater.Restart(); Application.Current.Shutdown(); return; }
                    catch (Exception e) { Log("Could not restart the launcher: " + e.Message); UpdateText = "Updated to " + r.Version + ". Close and open MTGA Forge again to finish."; return; }
                }
                pendingRelease = null; pendingParts = null;
                UpdateReady = false;
                UpdateAvailable = false;
                Recheck();          // the game check sees new patch tools or server files and asks to prepare again
            })));
        }

        /// <summary>The website's latest.json (releases from before auto-update): a link to the download, nothing installed.</summary>
        void CheckWebsite(DateTimeOffset built, string commit)
        {
            string error = "";
            Task.Run(() => UpdateChecker.Fetch(commit, out error)).ContinueWith(t => ui.BeginInvoke((Action)(() =>
            {
                var u = t.Result;
                if (u == null) { Log("Update check: the website could not be reached (" + error + "). Nothing is needed."); return; }
                if (!u.Released.HasValue) { Log("Update check: no release is listed yet."); return; }
                if (u.Released.Value <= built.AddMinutes(1)) { Log("Update check: you have the latest version."); return; }
                Log("Update check: a newer version exists (commit " + u.Commit + ", released " + u.Released.Value.ToString("yyyy-MM-dd") + ", SHA-256 " + u.Sha256 + ").");
                updateUrl = u.Url;
                UpdateText = "A newer MTGA Forge is available (" + u.Released.Value.ToString("yyyy-MM-dd") + "). Players on different versions cannot match online.";
                UpdateAvailable = true;
            })));
        }

        void OpenUpdate()
        {
            Uri uri;
            if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps) { Log("Update link ignored (not an https address): " + updateUrl); return; }
            try { System.Diagnostics.Process.Start(uri.AbsoluteUri); } catch (Exception e) { Log("Could not open the browser: " + e.Message); }
        }

        // ------------------------------------------------------------------------------------------ firewall
        /// <summary>Reads whether the block rule for this folder's client exists (the switch shows the real state, not a remembered one).</summary>
        public void RefreshFirewall()
        {
            string dir = folder;
            Task.Run(() => Firewall.Exists(dir)).ContinueWith(t => ui.BeginInvoke((Action)(() => { firewallOn = t.Result; Raise("FirewallOn"); })));
        }

        public void SetFirewall(bool on)
        {
            string dir = folder;
            PlayStatus = on ? "Waiting for Windows to approve the firewall rule..." : "Waiting for Windows to approve removing the firewall rule...";
            Task.Run(() => Firewall.Set(dir, on)).ContinueWith(t => ui.BeginInvoke((Action)(() =>
            {
                PlayStatus = t.Result ?? (on ? "The game is now blocked from the internet. Forge and matchmaking are not affected." : "The firewall rule is removed.");
                RefreshFirewall();
            })));
        }

        // ------------------------------------------------------------------------------------------ identity backup
        /// <summary>The uninstall removed things: look at the game again.</summary>
        public void AfterUninstall()
        {
            profileNote = Profile.Note(folder); Raise("ProfileNote");
            ValidateFolder(); UpdateDisk(); RefreshFirewall();
            editing = false;
            Recheck();
        }

        public bool HasIdentity { get { return Profile.HasIdentity(folder); } }

        public string BackupIdentity(string target)
        {
            try { return "Saved to " + Profile.Backup(folder, target) + ". Keep it private: whoever has it can play as you. \"Restore\" brings your name, tag and friends back on another PC."; }
            catch (Exception e) { return "Could not save the backup: " + e.Message; }
        }

        public string RestoreIdentity(string source)
        {
            try
            {
                if (!Profile.Restore(folder, source)) return "That folder has no identity.json.";
                string n, id, tag;
                if (Profile.Read(folder, out n, out id, out tag) && n != "") Name = n;
                ProfileNote = Profile.Note(folder);
                return "Restored. Start the game and you are back with your name and tag.";
            }
            catch (Exception e) { return "Could not restore: " + e.Message; }
        }

        // ------------------------------------------------------------------------------------------ console and lifetime
        void Run(string script, string args, Action<string> onLine, Action<int> onExit)
        {
            Log("> " + script + " " + args);
            runner = new ScriptRunner(ui, Path.Combine(tools, script), args, l => { if (l.Trim() != "") Log(l.TrimEnd()); onLine(l); }, onExit);
        }

        void Log(string line)
        {
            log.Add(line);
            var h = LogAdded;
            if (h != null) h(line);
        }

        public string Summary { get { return Stage + " | " + Headline + " | sources: " + string.Join("; ", Sources.Select(s => s.Path)) + " | selected: " + SourcePath + " | folder: " + folder + " | problem: " + folderProblem + " | disk: " + diskInfo; } }

        public string AllLog { get { return string.Join(Environment.NewLine, log); } }

        public void Shutdown()
        {
            if (!Persist) { if (runner != null) runner.KillTree(); return; }
            settings.Name = name; settings.Offline = offline; settings.Folder = folder; settings.Source = SourcePath;
            settings.Save();
            if (runner != null) runner.KillTree();
        }

        // ------------------------------------------------------------------------------------------ pictures for --shots
        public void Demo(Stage s)
        {
            if (Sources.Count == 0)
            {
                Sources.Add(new SourceItem { Path = @"C:\Program Files (x86)\Steam\steamapps\common\MTGA", Note = "Steam library on C:" });
                Sources.Add(new SourceItem { Path = @"D:\SteamLibrary\steamapps\common\MTGA", Note = "Steam library on D:" });
                SourceNote = "Found 2 installs automatically.";
                selectedSource = Sources[1]; Raise("SelectedSource"); Raise("HasSource");
                folder = Settings.DefaultFolder; Raise("Folder");
                DiskInfo = "The copy needs about 16 GB. 412 GB free on C:";
                FolderProblem = "";
                name = "QuickOtter"; Raise("Name");
                profileNote = "Player ID 791602cb, shown as QuickOtter#85191"; Raise("ProfileNote");
                foreach (var line in new[] { "> launcher.ps1 -NoPause -Folder \"...\\Programs\\MTGA Forge\"", "Starting the Forge server (log: ...\\MTGA Forge\\logs\\server.log)", "[QuickOtter, instance 0] Server ready on ports 30003, 30010, 30011.", "MTGA started (pid 4120). Close the game window to stop the server." }) Log(line);
            }
            switch (s)
            {
                case Stage.NeedPrepare: ShowNeedsPrepare("not patched yet"); break;
                case Stage.Preparing:
                    Headline = "Preparing your game"; Body = "This takes several minutes, mostly copying files. You can do something else meanwhile.";
                    for (int k = 0; k < Steps.Count; k++) Steps[k].State = k < 3 ? 2 : k == 3 ? 1 : 0;
                    Progress = 3; Stage = Stage.Preparing; break;
                case Stage.Ready: Headline = "Ready to play!"; PlayStatus = ""; Stage = Stage.Ready; UpdateText = "A newer MTGA Forge is available (2026-10-25). Players on different versions cannot match online."; UpdateAvailable = true; break;
                case Stage.Running: PlayStatus = "Game running. Pick a queue or an event in the game. Close the game window to finish."; Stage = Stage.Running; break;
                default: Stage = s; break;
            }
        }
    }
}

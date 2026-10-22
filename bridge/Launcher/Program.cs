using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// MTGA-Forge.exe: the launcher window. Guides the first start (find MTGA, pick a folder, copy and patch the game), then starts the
// Forge server and the game. The work is done by tools\forge-patch.ps1 and tools\launcher.ps1; this program collects the settings,
// runs them and shows what they print.
//   MTGA-Forge.exe --shots <folder>   render the window in each state to PNG files and exit (no scripts are run)
//   MTGA-Forge.exe --probe <out.txt> [folder]   start for real (find MTGA, check the game, nothing is patched or started), write where it ended up, exit
namespace MtgaForgeLauncher
{
    public partial class App : Application { }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var app = new App();
            app.InitializeComponent();
            if (args.Length >= 2 && args[0] == "--shots") return Shots(app, args[1]);
            if (args.Length >= 2 && args[0] == "--probe") return Probe(app, args[1], args.Length > 2 ? args[2] : null);
            var window = new MainWindow(new MainViewModel(false));
            return app.Run(window);
        }

        static int Probe(App app, string outFile, string folder)
        {
            var vm = new MainViewModel(false, folder) { Persist = false };
            var w = new MainWindow(vm);
            var end = DateTime.Now.AddSeconds(120);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            t.Tick += (s, e) =>
            {
                if (vm.Stage == Stage.Checking && DateTime.Now < end) return;
                t.Stop();
                File.WriteAllText(outFile, vm.Summary + Environment.NewLine + vm.AllLog + Environment.NewLine);
                w.Close();
            };
            t.Start();
            return app.Run(w);
        }

        /// <summary>Pictures of the window for review: one per state.</summary>
        static int Shots(App app, string dir)
        {
            Directory.CreateDirectory(dir);
            var vm = new MainViewModel(true) { Persist = false };
            var w = new MainWindow(vm);
            w.Show();
            var states = new[] { Stage.NeedSource, Stage.NeedPrepare, Stage.Preparing, Stage.Ready, Stage.Running };
            foreach (var s in states)
            {
                if (s == Stage.NeedSource) { vm.Demo(Stage.NeedPrepare); vm.Demo(Stage.NeedSource); }
                else vm.Demo(s);
                vm.ConsoleOpen = s == Stage.Running || s == Stage.Preparing;
                Idle(); Save(w, Path.Combine(dir, s.ToString().ToLowerInvariant() + ".png"));
            }
            var dlg = new UninstallWindow(Path.Combine(dir, "no-such-folder"), "") { Owner = w };
            dlg.Show();
            Idle(); Save(dlg, Path.Combine(dir, "uninstall.png"));
            dlg.Close();
            w.Close();
            return 0;
        }

        static void Idle()
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            System.Threading.Thread.Sleep(250);
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }

        static void Save(Window w, string path)
        {
            var root = (FrameworkElement)w.Content;
            int pw = (int)root.ActualWidth, ph = (int)root.ActualHeight;
            var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }
}

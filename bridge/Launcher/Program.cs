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
//   MTGA-Forge.exe --ico <file.ico>   draw the anvil at 16..256 px into an icon file and exit (app.ico is made this way)
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
            if (args.Length >= 2 && args[0] == "--ico") return MakeIcon(args[1]);
            if (args.Length >= 2 && args[0] == "--probe") return Probe(app, args[1], args.Length > 2 ? args[2] : null);
            var window = new MainWindow(new MainViewModel(false));
            return app.Run(window);
        }

        static int MakeIcon(string path)
        {
            var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                var art = new ForgeIcon { Width = sizes[i], Height = sizes[i] };
                art.Measure(new Size(sizes[i], sizes[i])); art.Arrange(new Rect(0, 0, sizes[i], sizes[i])); art.UpdateLayout();
                var bmp = new RenderTargetBitmap(sizes[i], sizes[i], 96, 96, PixelFormats.Pbgra32);
                bmp.Render(art);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var ms = new MemoryStream()) { enc.Save(ms); pngs[i] = ms.ToArray(); }
            }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var png in pngs) w.Write(png);
            }
            return 0;
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

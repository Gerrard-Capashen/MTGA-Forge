using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace MtgaForgeLauncher
{
    sealed class ChevronConverter : IValueConverter
    {
        public static readonly ChevronConverter Instance = new ChevronConverter();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) { return (bool)value ? "  ▾" : "  ▸"; }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) { throw new NotSupportedException(); }
    }

    public partial class MainWindow : Window
    {
        const int DwmUseImmersiveDarkMode = 20;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        sealed class WindowHandle : WinForms.IWin32Window
        {
            public IntPtr Handle { get; set; }
        }

        internal MainViewModel Model { get; }

        internal MainWindow(MainViewModel model)
        {
            Model = model;
            DataContext = model;
            InitializeComponent();
            SourceInitialized += (s, e) =>
            {
                int on = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int));   // dark title bar
            };
            model.LogAdded += line =>
            {
                Console.AppendText(line + Environment.NewLine);
                Console.ScrollToEnd();
            };
            Closing += (s, e) => model.Shutdown();
        }

        string PickFolder(string description, string start)
        {
            using (var d = new WinForms.FolderBrowserDialog { Description = description, SelectedPath = start ?? "" })
                return d.ShowDialog(new WindowHandle { Handle = new WindowInteropHelper(this).Handle }) == WinForms.DialogResult.OK ? d.SelectedPath : null;
        }

        void Info(string text) { MessageBox.Show(this, text, "MTGA Forge"); }

        void BrowseSource_Click(object sender, RoutedEventArgs e)
        {
            string dir = PickFolder("Choose the folder that contains MTGA.exe", null);
            if (dir == null) return;
            string problem = Model.AddSource(dir);
            if (problem != null) Info(problem);
        }

        void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            string dir = PickFolder("Folder for the patched game copy (about 15 GB) and your decks", Model.Folder);
            if (dir != null) Model.SetFolder(dir);
        }

        void Backup_Click(object sender, RoutedEventArgs e)
        {
            if (!Model.HasIdentity) { Info("There is no identity yet. It is made the first time you start the game online."); return; }
            string dir = PickFolder("Where to keep the backup (a folder MTGA-Forge-identity is made inside)", null);
            if (dir != null) Info(Model.BackupIdentity(dir));
        }

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            string dir = PickFolder("Choose the MTGA-Forge-identity folder of your backup", null);
            if (dir == null) return;
            if (Model.HasIdentity && MessageBox.Show(this, "This replaces the identity and profile of this install. Your decks stay. Continue?", "MTGA Forge", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Info(Model.RestoreIdentity(dir));
        }

        void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(Model.AllLog); } catch { Info("Could not use the clipboard."); }
        }

        void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            string dir = Model.LogsFolder;
            try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", "\"" + dir + "\""); } catch { Info("Could not open " + dir); }
        }
    }
}

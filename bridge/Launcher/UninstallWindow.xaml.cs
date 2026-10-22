using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MtgaForgeLauncher
{
    public partial class UninstallWindow : Window
    {
        readonly string folder;
        readonly List<UninstallItem> items;
        bool removing;

        public System.Collections.IEnumerable Items { get { return items; } }

        /// <summary>True once something was removed (the main window checks the game again).</summary>
        public bool Changed { get; private set; }

        internal UninstallWindow(string folder, string source)
        {
            this.folder = folder;
            items = Uninstaller.Plan(folder, source);
            DataContext = this;
            InitializeComponent();
            Result.Text = items.Any(i => i.Exists) ? "Looking at sizes..." : "Nothing of MTGA Forge was found in " + folder + ".";
            Remove.IsEnabled = false;
            Task.Run(() => Uninstaller.Measure(items)).ContinueWith(t => Dispatcher.BeginInvoke((Action)(() =>
            {
                foreach (var i in items) i.Raise("SizeText");
                if (items.Any(i => i.Exists)) { Result.Text = "Folder: " + folder; Remove.IsEnabled = true; }
            })));
            Closing += (s, e) => { if (removing) e.Cancel = true; };
        }

        void Cancel_Click(object sender, RoutedEventArgs e) { Close(); }

        async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var chosen = items.Where(i => i.IsChecked && i.Exists).ToList();
            if (chosen.Count == 0) { Result.Text = "Nothing is selected."; return; }
            if (chosen.Any(i => i.Title.StartsWith("Decks")) &&
                MessageBox.Show(this, "This deletes your decks, your name and tag and your friends for good. Continue?", "MTGA Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            removing = true;
            Remove.IsEnabled = false; Cancel.IsEnabled = false;
            var problems = await Task.Run(() => Uninstaller.Remove(items, folder, s => Dispatcher.BeginInvoke((Action)(() => Result.Text = s))));
            removing = false;
            Changed = true;
            Cancel.IsEnabled = true; Cancel.Content = "Close";
            Result.Text = problems.Count == 0 ? "Done. You can delete MTGA-Forge.exe and its folder yourself."
                                              : "Removed what it could. Left over:\n" + string.Join("\n", problems.Take(4));
            foreach (var i in items.Where(i => i.IsChecked)) { i.Exists = false; i.IsChecked = false; i.Raise("SizeText"); i.Raise("Exists"); }
        }
    }
}

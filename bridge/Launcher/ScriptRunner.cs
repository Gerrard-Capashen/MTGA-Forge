using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MtgaForgeLauncher
{
    /// <summary>A PowerShell script of the package run as a child process; every output line is handed to <c>onLine</c> on the UI thread.</summary>
    sealed class ScriptRunner
    {
        readonly Process proc;
        public readonly List<string> Lines = new List<string>();

        /// <summary>The command line as shown in the console, so nothing runs unseen.</summary>
        public readonly string CommandLine;

        public ScriptRunner(Dispatcher ui, string script, string args, Action<string> onLine, Action<int> onExit)
        {
            var enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            CommandLine = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + args;
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" " + args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = enc, StandardErrorEncoding = enc, WorkingDirectory = Path.GetDirectoryName(script),
            };
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler h = (s, e) =>
            {
                if (e.Data == null) return;
                ui.BeginInvoke((Action)(() => { Lines.Add(e.Data); onLine(e.Data); }));
            };
            proc.OutputDataReceived += h;
            proc.ErrorDataReceived += h;
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            var p = proc;
            Task.Run(() =>
            {
                p.WaitForExit();     // also waits for the redirected streams to drain
                int code = p.ExitCode;
                try { ui.BeginInvoke((Action)(() => onExit(code))); } catch (InvalidOperationException) { }
            });
        }

        public bool Running { get { try { return !proc.HasExited; } catch { return false; } } }

        public void KillTree()
        {
            try
            {
                if (!proc.HasExited)
                    Process.Start(new ProcessStartInfo("taskkill", "/PID " + proc.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(5000);
            }
            catch { }
        }
    }
}

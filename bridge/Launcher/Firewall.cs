using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace MtgaForgeLauncher
{
    /// <summary>
    /// The optional outbound block for the patched MTGA.exe, the same rule tools\forge-patch.ps1 -Firewall makes ("MTGF block (&lt;client&gt;)").
    /// It covers that one program only: the Forge server (a Java process) and the matchmaking connection are not affected.
    /// </summary>
    static class Firewall
    {
        public const string Explanation =
            "Blocks the patched MTGA client, and only that program, from the internet with a Windows Firewall rule. "
            + "It does not affect the Forge engine or matchmaking, which run separately. "
            + "Turn it on if you want to be certain the client never contacts Unity, MTGA or advertising services, even though those calls are already removed. "
            + "Windows asks for administrator approval when you change it.";

        static string Client(string folder) { return Path.GetFullPath(Path.Combine(folder, "client")); }
        static string RuleName(string folder) { return "MTGF block (" + Client(folder) + ")"; }

        public static bool Exists(string folder)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + RuleName(folder) + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode == 0;      // netsh exits 1 when no rule matches (whatever the Windows language)
                }
            }
            catch { return false; }
        }

        /// <summary>Adds or removes the rule (needs administrator rights, so Windows shows its prompt). Returns a message on failure, null on success.</summary>
        public static string Set(string folder, bool on)
        {
            string name = RuleName(folder);
            string args = on
                ? "advfirewall firewall add rule name=\"" + name + "\" dir=out action=block program=\"" + Path.Combine(Client(folder), "MTGA.exe") + "\" enable=yes profile=any"
                : "advfirewall firewall delete rule name=\"" + name + "\"";
            try
            {
                var psi = new ProcessStartInfo("netsh", args) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit();
            }
            catch (Win32Exception) { return "Administrator approval was declined, so the firewall rule was not changed."; }
            return Exists(folder) == on ? null : "The firewall rule was not changed.";
        }
    }
}

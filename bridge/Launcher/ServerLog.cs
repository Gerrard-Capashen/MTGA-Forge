using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MtgaForgeLauncher
{
    /// <summary>Follows the Forge server's log file and turns its connection events into plain sentences.</summary>
    sealed class ServerLog
    {
        readonly string path;
        long pos;

        public ServerLog(string path) { this.path = path; }

        /// <summary>New raw lines since the last call.</summary>
        public string[] ReadNew()
        {
            if (!File.Exists(path)) return new string[0];
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length <= pos) return new string[0];
                    fs.Position = pos;
                    var buf = new byte[fs.Length - pos];
                    int n = fs.Read(buf, 0, buf.Length);
                    pos += n;
                    return Encoding.UTF8.GetString(buf, 0, n).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch { return new string[0]; }
        }

        /// <summary>A player-facing sentence for a log line, or null when the line is not one of the events worth showing.</summary>
        public static string Translate(string raw)
        {
            string l = raw.Trim();
            Match m;
            if ((m = Regex.Match(l, @"FD online: '.*' queues in \S+ with friend code (\S+)")).Success) return "Waiting for your friend (code " + m.Groups[1].Value + ")...";
            if (Regex.IsMatch(l, @"FD online: '.*' queues in ")) return "In the queue. Waiting for an opponent...";
            if ((m = Regex.Match(l, @"FD online: paired with (.+) as (host|guest)")).Success) return "Matched with " + m.Groups[1].Value + ". The game is starting.";
            if ((m = Regex.Match(l, @"FD online: no match \((.+)\)")).Success) return "No match: " + m.Groups[1].Value;
            if ((m = Regex.Match(l, @"FD online: join failed: (.+)$")).Success) return "Could not join: " + m.Groups[1].Value;
            if (Regex.IsMatch(l, @"table queue: waiting in ")) return "Looking for a draft table...";
            if ((m = Regex.Match(l, @"FD premier \S+: table \S+ formed, (\d+) people")).Success) return m.Groups[1].Value == "1" ? "Nobody else came: drafting with bots." : "Draft table found: " + m.Groups[1].Value + " players. Press Ready in the game.";
            if ((m = Regex.Match(l, @"FD premier \S+: table cancelled \((.+)\)")).Success) return "Draft table cancelled: " + m.Groups[1].Value + ".";
            if (l.Contains("matchmaking server unreachable")) return "Can't reach the matchmaking server: check your internet connection.";
            if (l.Contains("[mtga] game over")) return "Game over. Close the game window to finish.";
            if (l.Contains("game thread died")) return "The game stopped unexpectedly (see the details below).";
            return null;
        }
    }
}

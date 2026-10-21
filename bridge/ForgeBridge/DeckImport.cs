using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Wotc.Mtga.Cards.Database;

namespace ForgeBridge
{
    /// <summary>
    /// Deck import (Decks page "Import"): the client looks every line's name up by EXACT English title and aborts the
    /// whole import at the first unknown card (WrapperDeckUtilities.TryImportDeck/ParseLine). Patched to call
    /// <see cref="Rewrite"/> on the text first: names are matched ignoring case, accents and punctuation, split cards
    /// written "A // B" find the client's "A /// B" title, "Front // Back" double-faced names find the front face, and
    /// "A-Name" falls back to "Name". Lines whose card the client does not have are dropped and listed in a popup.
    /// </summary>
    public static class DeckImport
    {
        // "4 Name", "4x Name", "4 Name (SET) 123"
        private static readonly Regex Line = new Regex(@"^(?<n>\d+)x?\s+(?<name>.+?)(?<print>\s+\([^()]*\)(\s+\S+)?)?\s*$");
        private static Dictionary<string, string> _titles;   // folded title -> client title (primary, non-token printings)

        public static string Rewrite(string text, ICardDatabaseAdapter db)
        {
            // scripted test runs: FORGEBRIDGE_CLIPBOARD names a file that replaces the clipboard (the OS clipboard is not
            // reachable from a locked session, and must not leak the user's own clipboard into a test)
            string testFile = Environment.GetEnvironmentVariable("FORGEBRIDGE_CLIPBOARD");
            if (!string.IsNullOrEmpty(testFile) && System.IO.File.Exists(testFile)) text = System.IO.File.ReadAllText(testFile);
            if (string.IsNullOrEmpty(text) || db == null) return text;
            try
            {
                var skipped = new List<string>();
                int renamed = 0;
                var lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var m = Line.Match(lines[i].Trim());
                    if (!m.Success) continue;
                    string name = m.Groups["name"].Value.Trim();
                    if (Known(db, name)) continue;
                    string title = Resolve(db, name);
                    if (title == null)
                    {
                        skipped.Add(m.Groups["n"].Value + " " + name);
                        lines[i] = null;   // removed, not blanked: a blank line starts the next pile (DeckImportParser)
                        continue;
                    }
                    lines[i] = m.Groups["n"].Value + " " + title;   // printing hint dropped: it belonged to the other spelling
                    renamed++;
                }
                if (renamed == 0 && skipped.Count == 0) return text;
                Log.Info("deck import: " + renamed + " names matched loosely; skipped: " + string.Join("; ", skipped));
                if (skipped.Count > 0)
                {
                    SystemMessageManager.ShowSystemMessage("Cards not in this client",
                        "These cards are not in the client's card database and were left out:\n" + string.Join("\n", skipped.Take(25))
                        + (skipped.Count > 25 ? "\n... and " + (skipped.Count - 25) + " more" : ""));
                }
                return string.Join("\n", lines.Where(l => l != null));
            }
            catch (Exception e)
            {
                Log.Error("deck import rewrite failed: " + e);
                return text;
            }
        }

        private static bool Known(ICardDatabaseAdapter db, string title)
        {
            var list = db.DatabaseUtilities.GetPrintingsByEnglishTitle(title);
            return list != null && list.Any(p => p.IsPrimaryCard && !p.IsToken);
        }

        private static string Resolve(ICardDatabaseAdapter db, string name)
        {
            var titles = Titles(db);
            var tries = new List<string> { name };
            int split = name.IndexOf("//", StringComparison.Ordinal);
            if (split > 0) tries.Add(name.Substring(0, split));                          // double-faced: front face
            if (name.StartsWith("A-", StringComparison.Ordinal)) tries.Add(name.Substring(2));   // rebalanced -> original
            foreach (var t in tries)
            {
                if (titles.TryGetValue(Fold(t), out var title)) return title;
            }
            return null;
        }

        private static Dictionary<string, string> Titles(ICardDatabaseAdapter db)
        {
            if (_titles != null) return _titles;
            var map = new Dictionary<string, string>();
            foreach (uint id in db.CardDataProvider.GetCardIds())
            {
                var p = db.CardDataProvider.GetCardPrintingById(id);
                if (p == null || !p.IsPrimaryCard || p.IsToken) continue;
                string title = db.CardTitleProvider.GetCardTitle(id, null, false, "en-US");
                if (string.IsNullOrWhiteSpace(title)) continue;
                string key = Fold(title);
                if (!map.ContainsKey(key)) map[key] = title;
            }
            Log.Info("deck import: " + map.Count + " titles indexed");
            return _titles = map;
        }

        /// <summary>Lower case, no accents, "///" as "//", only letters, digits, '/' and '-'.</summary>
        public static string Fold(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.Replace("///", "//").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                char l = char.ToLowerInvariant(c);
                if (char.IsLetterOrDigit(l) || l == '/' || l == '-') sb.Append(l);
            }
            return sb.ToString();
        }
    }
}

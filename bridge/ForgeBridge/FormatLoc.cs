using System.Collections.Generic;
using Core.Code.Localization;
using Wizards.Mtga;
using Wotc.Mtga.Loc;

namespace ForgeBridge
{
    /// <summary>
    /// Labels for the deck formats the local Front Door defines from Forge's format list. The client shows a format as
    /// loc key MainNav/DeckFormat/&lt;Name&gt; ("LOCME: Name" without one); Modern, Legacy, Vintage and Commander have no
    /// key in the client's localization DB, and some keys carry Arena wording ("Arena Pioneer").
    /// Inserted first into the client's CompositeLocProvider, so these win over the SQL localization DB.
    /// </summary>
    public sealed class FormatLoc : IClientLocProvider
    {
        private static readonly Dictionary<string, string> Text = new Dictionary<string, string>
        {
            // the direct challenge screen has fixed deck types: Standard / Brawl / Limited / Alchemy; the Brawl and Alchemy slots are Pioneer and Modern here
            { "MainNav/PrivateGame/DeckType_60_Card", "Standard" },
            { "MainNav/PrivateGame/DeckType_Brawl", "Pioneer" },
            { "MainNav/PrivateGame/DeckType_60_Card_Alchemy", "Modern" },
            { "MainNav/DeckFormat/Pioneer", "Pioneer" },
            { "MainNav/DeckFormat/Modern", "Modern" },
            { "MainNav/DeckFormat/Legacy", "Legacy" },
            { "MainNav/DeckFormat/Vintage", "Vintage" },
            { "MainNav/DeckFormat/Commander", "Commander" },
            { "MainNav/DeckFormat/Draft", "Draft" },
            { "MainNav/Settings/ReportABug/Support_URL", "https://mtgaforge.com" },
            { "MainNav/Profile/Tabs/OverviewTab", "Overview" },
            { "MainNav/Profile/Tabs/CustomizationTab", "Customization" },
            { "MainNav/Profile/Tabs/FavoriteStores", "Favorite Stores" },
            { "MainNav/DeckFormatInfo/Standard", "Standard as Forge defines it." },
            { "MainNav/DeckFormatInfo/Pioneer", "Cards from Return to Ravnica onward, with Forge's Pioneer ban list." },
            { "MainNav/DeckFormatInfo/Modern", "Cards from Eighth Edition onward, with Forge's Modern ban list." },
            { "MainNav/DeckFormatInfo/Legacy", "Every card, with Forge's Legacy ban list." },
            { "MainNav/DeckFormatInfo/Vintage", "Every card, with Forge's Vintage restricted list (one copy)." },
            { "MainNav/DeckFormatInfo/Pauper", "Cards printed at common, with Forge's Pauper ban list." },
            { "MainNav/DeckFormatInfo/Commander", "100-card singleton deck led by a legendary commander. Deck building only: matches ignore the command zone." },
            { "MainNav/DeckFormatInfo/Historic", "Historic as Forge defines it." },
            { "MainNav/DeckFormatInfo/Alchemy", "Every card Forge implements, rebalanced versions included." },
            { "MainNav/DeckFormatInfo/Draft", "A drafted deck kept in your decks: 40 cards, any card allowed." },
            { "MainNav/DeckFormatInfo/Timeless", "Every card Forge implements. Bot matches accept decks of any format." },
            // the draft events' prize for winning out (FrontDoor StartHook token definition Forge_EventChampion)
            { "Forge/Token/EventChampionSingular", "Event Champion" },
            { "Forge/Token/EventChampionPlural", "Event Champion" },
            { "Forge/Token/EventChampion_Desc", "Congratulations on winning the event!" },
            // home carousel banners (FrontDoor CarouselGetCarouselItems)
            { "Forge/Carousel/ForgeWelcome_Title", "Welcome to MTGA Forge!" },
            { "Forge/Carousel/ForgeWelcome_Desc", "Enjoy all your favorite formats" },
            { "Forge/Carousel/ForgeDiscord_Title", "Join the MTGA Forge Discord" },
            { "Forge/Carousel/ForgeDiscord_Desc", "Find opponents, get help and report bugs" },
        };

        /// <summary>Call after the localization DB is loaded (it is only loaded while the composite has fewer than 2 providers).</summary>
        public static void Install()
        {
            if (Pantry.Get<IClientLocProvider>() is CompositeLocProvider composite)
            {
                composite.InsertProvider(new FormatLoc());
                Log.Info("format labels installed (" + Text.Count + " keys)");
            }
            else Log.Error("format labels: no CompositeLocProvider");
        }

        private const string DraftTitle = "Events/Event_Title_QuickDraft_";
        private const string DraftDesc = "Events/Event_Desc_QuickDraft_";

        private const string PremierTitle = "Events/Event_Title_PremierDraft_";
        private const string PremierDesc = "Events/Event_Desc_PremierDraft_";

        /// <summary>
        /// Quick draft events are one per Forge set (QuickDraft_&lt;SET&gt;), premier drafts PremierDraft_&lt;SET&gt; or
        /// PremierDraft_VCUBE: their labels are derived from the set code.
        /// </summary>
        private static bool TryDraft(string key, out string loc)
        {
            loc = null;
            if (key == null) return false;
            if (key.StartsWith(DraftTitle)) loc = SetBlurbs.Title(key.Substring(DraftTitle.Length));
            else if (key.StartsWith(DraftDesc)) loc = SetBlurbs.Of(key.Substring(DraftDesc.Length));
            else if (key.StartsWith(PremierTitle)) loc = SetBlurbs.PremierTitle(key.Substring(PremierTitle.Length));
            else if (key.StartsWith(PremierDesc)) loc = SetBlurbs.PremierOf(key.Substring(PremierDesc.Length));
            return loc != null;
        }

        private const string EventTitle = "Events/Event_Title_";
        private const string EventDesc = "Events/Event_Desc_";

        private static readonly string[] QueueFormats = { "Standard", "Pioneer", "Modern", "Legacy", "Vintage", "Pauper" };

        /// <summary>
        /// Play blade queues are events named BotPlay_&lt;Format&gt; / CasualPlay_&lt;Format&gt; / RankedPlay_&lt;Format&gt; (formats: <see cref="QueueFormats"/>, the server's PlayQueues.FORMATS):
        /// Bot Play is against the Forge AI, Casual Play and Ranked against another player.
        /// </summary>
        private static bool TryQueue(string key, out string loc)
        {
            loc = null;
            if (key == null) return false;
            bool title = key.StartsWith(EventTitle), desc = key.StartsWith(EventDesc);
            if (!title && !desc) return false;
            string name = key.Substring(title ? EventTitle.Length : EventDesc.Length);
            int us = name.IndexOf('_');
            if (us < 0) return false;
            string mode = name.Substring(0, us), format = name.Substring(us + 1);
            if (System.Array.IndexOf(QueueFormats, format) < 0) return false;
            if (mode == "BotPlay")
                loc = title ? "Bot Play: " + format : "Play a " + format + " deck against the Forge AI. Only decks legal in " + format + " can be chosen.";
            else if (mode == "CasualPlay")
                loc = title ? "Casual Play: " + format : "Play a casual " + format + " match against another player. Only decks legal in " + format + " can be chosen.";
            else if (mode == "RankedPlay")
                loc = title ? "Ranked: " + format : "Climb the Constructed ladder in " + format + " against another player. Every ranked format shares one rank. Only decks legal in " + format + " can be chosen.";
            return loc != null;
        }

        private const string WinsTitle = "Events/Forge_Wins_";
        private const string WinsDesc = "Events/Forge_Wins_Desc_";
        private const string WinsFinal = "Events/Forge_Wins_Final_";   // the last bubble, the one with the prize

        /// <summary>Win track bubbles of the draft events (no rewards offline): "3 Wins" / "Win 3 matches in this event."</summary>
        private static bool TryWins(string key, out string loc)
        {
            loc = null;
            if (key == null) return false;
            int n;
            if (key.StartsWith(WinsFinal) && int.TryParse(key.Substring(WinsFinal.Length), out n))
                loc = "Win " + n + " matches to complete the event and earn the Event Champion token.";
            else if (key.StartsWith(WinsDesc) && int.TryParse(key.Substring(WinsDesc.Length), out n))
                loc = n == 0 ? "Your record starts here." : "Win " + n + (n == 1 ? " match" : " matches") + " in this event.";
            else if (key.StartsWith(WinsTitle) && int.TryParse(key.Substring(WinsTitle.Length), out n))
                loc = n + (n == 1 ? " Win" : " Wins");
            return loc != null;
        }

        private static bool TryGet(string key, out string loc) => Text.TryGetValue(key, out loc) || TryDraft(key, out loc) || TryQueue(key, out loc) || TryWins(key, out loc)
            || TryMastery(key, out loc);

        private const string TrackTitle = "MainNav/BattlePass/BattlePass_";
        private const string MasteryKind = "Forge/Mastery/Kind_";
        private const string MasteryItem = "Forge/Mastery/Item/";

        /// <summary>
        /// The mastery track (server MasteryTrack): it borrows the art of one of the client's set tracks (BattlePass_&lt;SET&gt;), whose
        /// name becomes "Forge" ("Forge Mastery"); its rewards are "Forge/Mastery/Kind_&lt;kind&gt;" and "Forge/Mastery/Item/&lt;cosmetic id&gt;".
        /// </summary>
        private static bool TryMastery(string key, out string loc)
        {
            loc = null;
            if (key == null) return false;
            if (key.StartsWith(TrackTitle)) loc = "Forge";
            else if (key.StartsWith(MasteryKind))
            {
                switch (key.Substring(MasteryKind.Length))
                {
                    case "avatar": loc = "Avatar"; break;
                    case "sleeve": loc = "Card Sleeve"; break;
                    case "pet": loc = "Companion"; break;
                    case "sticker": loc = "Sticker"; break;
                    case "phrase": loc = "Emote"; break;
                    case "title": loc = "Title"; break;
                }
            }
            else if (key.StartsWith(MasteryItem)) loc = CosmeticName(key.Substring(MasteryItem.Length));
            return loc != null;
        }

        /// <summary>A readable name from a cosmetic id: Avatar_Basic_JaceBeleren -> "Jace Beleren", AFR_Dragon.Level2 -> "AFR Dragon (Level 2)".</summary>
        internal static string CosmeticName(string id)
        {
            // the client's own names first: sleeves and avatars have them
            string own = id.StartsWith("CardBack_") ? "MainNav/DeckBuilder/Sleeves/" + id : id.StartsWith("Avatar_") ? "MainNav/Profile/Avatars/" + id + "_Name" : null;
            if (own != null)
            {
                string text = Pantry.Get<IClientLocProvider>()?.GetLocalizedText(own);
                if (!string.IsNullOrEmpty(text) && !text.StartsWith("LOCME")) return text;
            }
            if (id.StartsWith("Title_"))
            {
                // Title_<group>_<name> was built from the client's key Titles/<group>/<name> (server CosmeticCatalog)
                int us = id.IndexOf('_', 6);
                string titleKey = us > 0 ? "Titles/" + id.Substring(6, us - 6) + "/" + id.Substring(us + 1) : null;
                string text = titleKey == null ? null : (Pantry.Get<IClientLocProvider>()?.GetLocalizedText(titleKey));
                if (!string.IsNullOrEmpty(text)) return text;
            }
            string variant = null;
            int dot = id.IndexOf('.');
            if (dot > 0) { variant = id.Substring(dot + 1); id = id.Substring(0, dot); }
            foreach (var prefix in new[] { "Avatar_Basic_", "Avatar_", "CardBack_", "Phrase_", "Sticker_", "Title_" })
                if (id.StartsWith(prefix)) { id = id.Substring(prefix.Length); break; }
            string name = Words(id.Replace('_', ' '));
            return variant == null ? name : name + " (" + Words(variant) + ")";
        }

        /// <summary>"JaceBeleren" -> "Jace Beleren", "Level2" -> "Level 2".</summary>
        private static string Words(string s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && s[i - 1] != ' ' && ((char.IsUpper(c) && char.IsLower(s[i - 1])) || (char.IsDigit(c) && char.IsLetter(s[i - 1])))) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        public string GetLocalizedText(string key, params (string, string)[] locParams)
        {
            return TryGet(key, out var s) ? s : null;
        }

        public bool TryGetLocalizedTextForLanguage(string key, string overrideLangCode, (string, string)[] locParams, out string loc)
        {
            return TryGet(key, out loc);
        }

        public string GetLocalizedTextForLanguage(string key, string overrideLangCode, params (string, string)[] locParams)
        {
            return GetLocalizedText(key);
        }

        public bool DoesContainTranslation(string key) => TryGet(key, out _);

        public bool IsDisposed() => false;

        public IEnumerable<string> GetKeys() => Text.Keys;

        public void OpenConnection() { }

        public void CloseConnection() { }
    }
}

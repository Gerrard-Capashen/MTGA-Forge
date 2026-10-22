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
            { "MainNav/DeckFormat/Pioneer", "Pioneer" },
            { "MainNav/DeckFormat/Modern", "Modern" },
            { "MainNav/DeckFormat/Legacy", "Legacy" },
            { "MainNav/DeckFormat/Vintage", "Vintage" },
            { "MainNav/DeckFormat/Commander", "Commander" },
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
            { "MainNav/DeckFormatInfo/Timeless", "Every card Forge implements. Bot matches accept decks of any format." },
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

        /// <summary>Quick draft events are one per Forge set (QuickDraft_&lt;SET&gt;): their labels are derived from the set code.</summary>
        private static bool TryDraft(string key, out string loc)
        {
            loc = null;
            if (key == null) return false;
            if (key.StartsWith(DraftTitle)) loc = SetBlurbs.Title(key.Substring(DraftTitle.Length));
            else if (key.StartsWith(DraftDesc)) loc = SetBlurbs.Of(key.Substring(DraftDesc.Length));
            return loc != null;
        }

        private const string EventTitle = "Events/Event_Title_";
        private const string EventDesc = "Events/Event_Desc_";

        /// <summary>
        /// Play blade queues are events named BotPlay_&lt;Format&gt; / CasualPlay_&lt;Format&gt; (Standard, Pioneer, Modern):
        /// Bot Play is against the Forge AI, Casual Play against another player.
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
            if (format != "Standard" && format != "Pioneer" && format != "Modern") return false;
            if (mode == "BotPlay")
                loc = title ? "Bot Play: " + format : "Play a " + format + " deck against the Forge AI. Only decks legal in " + format + " can be chosen.";
            else if (mode == "CasualPlay")
                loc = title ? "Casual Play: " + format : "Play a casual " + format + " match against another player. Only decks legal in " + format + " can be chosen.";
            return loc != null;
        }

        private static bool TryGet(string key, out string loc) => Text.TryGetValue(key, out loc) || TryDraft(key, out loc) || TryQueue(key, out loc);

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

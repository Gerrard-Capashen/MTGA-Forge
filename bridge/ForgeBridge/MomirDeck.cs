using System;
using System.Linq;
using System.Reflection;
using Wizards.Mtga.PlayBlade;

namespace ForgeBridge
{
    /// <summary>
    /// Momir Basic needs no deck choice: every legal deck is 60 basic lands and the match deck is built by the server anyway. The
    /// play blade, though, enables Play only with a selected deck (and JoinMatchMaking drops the click without one), so when the
    /// Momir queue is the selected queue this picks the Momir deck for the player. The Patcher calls Apply when
    /// FindMatchBladeView.UpdateViewFromSelection returns; a deck the player already chose among Momir-format decks stays.
    /// </summary>
    public static class MomirDeck
    {
        private const string Event = "MomirPlay_Momir";
        private const string Format = "Momir";
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static void Apply(object view)
        {
            try
            {
                var t = view.GetType();
                var info = t.GetField("_selectionInfo", All)?.GetValue(view) as BladeSelectionInfo;
                var data = t.GetField("_data", All)?.GetValue(view) as FindMatchBladeData;
                var sel = info?.FindMatchInfo;
                if (sel == null || data == null || sel.SelectedEventName != Event) return;

                var decks = data.Decks;
                var current = decks.FirstOrDefault(d => d.deckId == sel.SelectedDeckId);
                if (current != null && current.DeckFormat == Format) return;              // already on a Momir deck
                var pick = decks.FirstOrDefault(d => d.DeckFormat == Format);
                if (pick == null) return;                                                   // none yet (decks not loaded): next refresh retries

                sel.SelectedDeckId = pick.deckId;
                sel.SelectedDeckIsInvalidForFormat = false;
                // SendUpdatedSelection -> both blade views refresh from the shared selection (deck highlighted, Play enabled);
                // that refresh runs UpdateViewFromSelection again, where the deck now matches and this returns at once.
                t.GetMethod("SendUpdatedSelection", All)?.Invoke(view, null);
            }
            catch (Exception e) { Log.Error("MomirDeck.Apply: " + e); }
        }
    }
}

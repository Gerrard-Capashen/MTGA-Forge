using System;
using System.Collections.Generic;
using System.Linq;
using Wizards.Mtga;
using Wizards.Mtga.Decks;
using Wizards.Mtga.Format;

namespace ForgeBridge
{
    /// <summary>
    /// The play blade's deck picker only splits decks into valid and invalid for the queue's format, so a Standard deck (also
    /// legal in Pioneer and Modern) sat between the Modern decks. The Patcher passes the result of
    /// DeckViewUtilities.SortDeckViewInfo through <see cref="Reorder"/>: decks saved in the format being played come first,
    /// the client's own order is kept inside each group.
    /// </summary>
    public static class DeckOrder
    {
        public static IReadOnlyList<DeckViewInfo> Reorder(IReadOnlyList<DeckViewInfo> sorted, string formatForSort)
        {
            try
            {
                if (sorted == null || string.IsNullOrWhiteSpace(formatForSort)) return sorted;
                var formats = Pantry.Get<FormatManager>();
                string target = formats.GetSafeFormat(formatForSort).FormatName;
                return sorted.OrderByDescending(d => Own(formats, d, target)).ToList();      // OrderBy is stable
            }
            catch (Exception e)
            {
                Log.Error("DeckOrder failed: " + e);
                return sorted;
            }
        }

        private static bool Own(FormatManager formats, DeckViewInfo d, string target)
        {
            string saved = d.deck?.Summary?.Format;
            return !string.IsNullOrWhiteSpace(saved) && formats.GetSafeFormat(saved).FormatName == target;
        }
    }
}

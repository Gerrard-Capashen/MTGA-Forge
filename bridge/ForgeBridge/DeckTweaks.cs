using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using Wizards.Mtga.Decks;
using Wizards.Mtga.Format;
using Wotc.Mtga.Loc;

namespace ForgeBridge
{
    /// <summary>
    /// The Deck Manager's format dropdown is hardcoded to Arena's formats (Standard, Alchemy, Explorer, Historic, Timeless, Brawl): a
    /// deck of any other format, which here means Pioneer, Modern, Legacy, Vintage, Pauper, Premodern and Commander, was only found under
    /// "All decks" / "Other decks". The Patcher calls Apply at the end of DeckManagerController.CreateDeckBuckets: one bucket per Forge
    /// format, taking its decks out of "Other decks", sorted with the client's own format order.
    /// </summary>
    public static class DeckTweaks
    {
        private static readonly string[] Extra = { "Pioneer", "Modern", "Legacy", "Vintage", "Pauper", "Premodern", "Commander" };
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static void Apply(DeckManagerController controller)
        {
            try
            {
                var t = typeof(DeckManagerController);
                var listField = t.GetField("_deckBuckets", Any);
                var list = (IList)listField.GetValue(controller);
                var bucketType = listField.FieldType.GetGenericArguments()[0];
                var formats = (FormatManager)t.GetField("_formatManager", Any).GetValue(controller);
                var dropdown = (TMP_Dropdown)t.GetField("_deckBucketDropdown", Any).GetValue(controller);
                var ctor = bucketType.GetConstructors(Any)[0];       // (localizedName, createsFormat, params formatNames)
                FieldInfo fName = bucketType.GetField("LocalizedName", Any), fCreates = bucketType.GetField("CreatesFormat", Any),
                    fNames = bucketType.GetField("FormatNames", Any), fDecks = bucketType.GetField("Decks", Any);

                // "All decks" and "Other decks" are the buckets without format names; "Other decks" is the later one and is appended last
                object other = null;
                var buckets = new List<object>();
                foreach (var b in list)
                {
                    if (((string[])fNames.GetValue(b)).Length == 0) other = b;
                    else buckets.Add(b);
                }
                bool haveAll = false;
                foreach (var b in list) if (!ReferenceEquals(b, other) && ((string[])fNames.GetValue(b)).Length == 0) haveAll = true;
                if (!haveAll) other = null;       // only "All decks" exists: nothing to take decks from

                var all = new List<object>();
                foreach (var b in list) if (((string[])fNames.GetValue(b)).Length == 0 && !ReferenceEquals(b, other)) all.Add(b);

                foreach (var name in Extra)
                {
                    bool exists = false;
                    foreach (var b in buckets) if (Array.IndexOf((string[])fNames.GetValue(b), name) >= 0) exists = true;
                    if (exists) continue;
                    var bucket = ctor.Invoke(new object[] { Languages.ActiveLocProvider.GetLocalizedText("MainNav/DeckFormat/" + name), name, new[] { name } });
                    if (other != null)
                    {
                        var from = (IList)fDecks.GetValue(other);
                        var to = (IList)fDecks.GetValue(bucket);
                        for (int i = 0; i < from.Count; i++)
                        {
                            var deck = (Client_Deck)from[i];
                            if (formats.GetSafeFormat(deck.Summary.Format).FormatName != name) continue;
                            to.Add(deck);
                            from.RemoveAt(i--);
                        }
                    }
                    buckets.Add(bucket);
                }

                // the client's own order (the same comparator CreateDeckBuckets sorted with), "All decks" first, "Other decks" last
                buckets.Sort((a, b) => FormatUtilities.FormatSortOrderComparator(formats.GetSafeFormat((string)fCreates.GetValue(a)), formats.GetSafeFormat((string)fCreates.GetValue(b))));
                list.Clear();
                foreach (var b in all) list.Add(b);
                foreach (var b in buckets) list.Add(b);
                if (other != null && ((IList)fDecks.GetValue(other)).Count > 0) list.Add(other);

                var options = new List<TMP_Dropdown.OptionData>();
                foreach (var b in list) options.Add(new TMP_Dropdown.OptionData((string)fName.GetValue(b)));
                dropdown.options = options;
            }
            catch (Exception e)
            {
                Log.Info("DeckTweaks failed: " + e);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Wizards.Mtga.Decks;
using Wotc.Mtga.Cards.Database;

namespace ForgeBridge
{
    /// <summary>
    /// Format of an imported deck. The client picks it in DeckFormatUtil.GetBestFormat from a fixed list of Arena formats
    /// (Standard, Historic, Explorer, Timeless...) and only accepts fully valid decks, so a Modern list never becomes
    /// "Modern" and a deck short of 60 cards falls back to "Historic". Patched to pass that result through
    /// <see cref="Best"/>: the narrowest Front Door format (Forge-defined) whose card rules the deck meets, deck size not
    /// checked (the deck builder shows the count).
    /// </summary>
    public static class DeckFormats
    {
        private static readonly string[] Narrowest = { "Standard", "Pioneer", "Pauper", "Modern", "Legacy", "Vintage", "Timeless" };

        public static string Best(string clientChoice, Client_Deck deck, FormatManager formats, CardDatabase db)
        {
            try
            {
                var piles = deck.Contents.Piles;
                bool commander = piles.TryGetValue(EDeckPile.CommandZone, out var cz) && cz.Count > 0;
                foreach (string name in commander ? new[] { "Commander" } : Narrowest)
                {
                    var format = formats.GetSafeFormat(name, null);
                    if (format != null && CardsLegal(format, piles, db))
                    {
                        if (name != clientChoice) Log.Info("import format: " + name + " (client chose " + clientChoice + ")");
                        return name;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("import format failed: " + e);
            }
            return clientChoice;
        }

        private static bool CardsLegal(DeckFormat format, Dictionary<EDeckPile, List<Client_DeckCard>> piles, CardDatabase db)
        {
            var copies = new Dictionary<uint, uint>();   // title id -> copies over all piles
            var basics = new HashSet<uint>();
            foreach (var pile in piles.Values)
            {
                foreach (var card in pile)
                {
                    var printing = db.CardDataProvider.GetCardPrintingById(card.Id);
                    if (printing == null) return false;
                    uint title = printing.TitleId;
                    if (printing.IsBasicLandUnlimited) basics.Add(title);
                    else if (!format.IsCardLegal(title) || format.IsCardBanned(title)) return false;
                    copies[title] = (copies.TryGetValue(title, out var n) ? n : 0) + card.Quantity;
                }
            }
            foreach (var kv in copies)
            {
                if (basics.Contains(kv.Key)) continue;
                int max = format.IsCardRestricted(kv.Key) ? format.GetRestrictedQuotaMax(kv.Key) : format.MaxCardsByTitle;
                if (kv.Value > max) return false;
            }
            return true;
        }
    }
}

using System.Collections.Generic;

namespace ForgeBridge
{
    /// <summary>One or two sentences per set for the quick draft event page (written from the MTG wiki's set articles).</summary>
    public static class SetBlurbs
    {
        private static readonly Dictionary<string, string> Text = new Dictionary<string, string>
        {
            { "TRK", "A Universes Beyond set spanning the Star Trek franchise, from the original series to its newest crews. Released in November 2026." },
            { "FRA", "The climax of the Echoverse arc, where the multiverse fractures and Jace Beleren's story comes to a head. An event set released in October 2026." },
            { "HOB", "A Universes Beyond set based on J.R.R. Tolkien's The Hobbit, designed with the four-player Pick-Two Draft in mind." },
            { "MSH", "A Universes Beyond set based on the original Marvel comics, not the films, with heroes and villains from across the Marvel universe." },
            { "SOS", "A return to Strixhaven University on the plane of Arcavios, showing more of the world outside the school than the first Strixhaven set." },
            { "TMT", "A Universes Beyond set featuring characters and stories from the whole Teenage Mutant Ninja Turtles franchise." },
            { "ECL", "The first return to the plane of Lorwyn-Shadowmoor since 2008, with both its bright and its twilight sides in one set." },
            { "TLA", "A Universes Beyond set based on the animated series Avatar: The Last Airbender, made with its original creators." },
            { "SPM", "A Universes Beyond tentpole set made with Marvel Entertainment, featuring Spider-Man, his allies and his rogues' gallery. Standard-legal." },
            { "EOE", "A science-fantasy space opera set in a region of outer space called the Edge, with Spacecraft, Planets and the warp and void mechanics." },
            { "FIN", "A Universes Beyond set based on the Final Fantasy video games, featuring Saga creatures, Towns and transforming double-faced cards." },
            { "TDM", "A return to Tarkir, a decade after Khans of Tarkir, themed around the conflict between the plane's dragons and its humanoid clans." },
            { "DFT", "A multiplanar race across Avishkar, Amonkhet and Muraganda that makes heavy use of Vehicles, Mounts and the start your engines! mechanic." },
            { "FDN", "An entry-level \"evergreen core set\" designed as an on-ramp for new players, and legal in Standard for years." },
            { "DSK", "A top-down haunted-house set inspired by 70s, 80s and 90s horror, with a very different tone from Bloomburrow." },
            { "BLB", "A world of anthropomorphic animals with no humans in it, built around tribal animal archetypes." },
            { "MH3", "The third Modern Horizons: a tentpole set of new cards for Modern and eternal formats, never legal in Standard." },
            { "OTJ", "A Wild West set on the plane of Thunder Junction, full of outlaws, heists and gunslingers." },
            { "MKM", "A top-down murder mystery set in Ravnica: collect evidence, don disguises and identify the culprit." },
            { "LCI", "An expedition into the caverns beneath Ixalan, where dinosaurs, merfolk, vampires and pirates race for hidden treasure." },
            { "WOE", "A return to the fairy-tale plane of Eldraine, with enchanted storybook creatures and Roles." },
            { "LTR", "A Universes Beyond set based on The Lord of the Rings, created in close cooperation with Middle-earth Enterprises." },
            { "MOM", "An event set depicting the New Phyrexian invasion across the multiverse." },
            { "ONE", "The Phyrexian conquest of Mirrodin comes to a head, as rebel Mirrans fight compleated Phyrexians." },
            { "BRO", "An event set about the Brothers' War between Urza and Mishra, built around artifacts." },
            { "DMU", "A return to Dominaria as its heroes face the Phyrexian threat, with plenty of legendary creatures." },
            { "SNC", "A gangster set inspired by early 20th-century organized crime, set on the plane of New Capenna." },
            { "NEO", "Magic's first science-fiction plane: a neon-lit Kamigawa where tradition clashes with modernity." },
            { "DBL", "All the cards of Innistrad: Midnight Hunt and Crimson Vow in one draft, with a greyscale art treatment inspired by classic horror." },
            { "VOW", "The second Innistrad set of the pair, centered on its vampires and gothic romance." },
            { "MID", "The first Innistrad set of the pair, centered on its werewolves and the night they transform." },
            { "AFR", "A Dungeons & Dragons crossover set on the Forgotten Realms: venture into dungeons, roll a d20 and face dragons." },
            { "MH2", "The second Modern Horizons: powerful toolbox cards and throwbacks for Modern and eternal formats, never legal in Standard." },
            { "STX", "Magic's take on the magical school genre, with five colleges each tied to a pair of enemy colors." },
            { "KHM", "A Norse-mythology-inspired world of Vikings, giants and snow." },
            { "ZNR", "A return to Zendikar's adventure world without the Eldrazi, with a focus on lands, exploration and reward." },
            { "M21", "A core set that mixes new and reprinted cards." },
            { "IKO", "A monster-movie set inspired by kaiju, where giant creatures are the focal point of the plane." },
            { "THB", "The final set of the Theros block: ancient Greek heroes, monsters and gods in the Underworld." },
            { "ELD", "A fairy-tale set inspired by Arthurian legend on one hand and Grimm's fairy tales on the other." },
            { "M20", "A core set that mixes new and reprinted cards." },
            { "MH1", "The original Modern Horizons: new cards for Modern and eternal formats without ever being legal in Standard." },
            { "WAR", "Nicol Bolas invades Ravnica in a set where Planeswalkers are at the heart of the war." },
            { "RNA", "A return to Ravnica with the other five guilds: Azorius, Rakdos, Gruul, Selesnya and Orzhov." },
            { "GRN", "A return to Ravnica with five of its ten guilds: Boros, Dimir, Golgari, Izzet and Selesnya." },
            { "M19", "A core set reintroduced after Magic Origins, mixing new cards with reprints." },
            { "DOM", "A return to Dominaria and the first set to follow the Three-and-One model, with Richard Garfield on the design team." },
            { "RIX", "The last regular small expansion, set on Ixalan with its four factions at war." },
            { "XLN", "A set of pirates, dinosaurs, vampires and merfolk on the plane of Ixalan, built around tribes." },
            { "HOU", "The second Amonkhet set, where Nicol Bolas arrives and the Hour of Devastation begins." },
            { "AKH", "A top-down Ancient Egypt-inspired set, with imagery referencing Nicol Bolas." },
            { "AER", "The second set of the Kaladesh block, where a revolt against the Consulate breaks out." },
            { "KLD", "An inventors' world inspired by the Far East, where aether powers new Vehicles and contraptions." },
            { "EMN", "The second Innistrad block set, expanding its horror theme into Lovecraftian cosmic horror as Emrakul rises." },
            { "SOI", "A return to the gothic horror plane of Innistrad, emphasizing mystery and creeping dread." },
            { "OGW", "The small set where the Gatewatch forms to fight the Eldrazi on Zendikar." },
            { "BFZ", "A return to Zendikar as the Eldrazi spawn from beneath the surface." },
            { "ORI", "A set telling the origin stories of five Planeswalkers; the last core-style set before the Two-Block Paradigm changed." },
            { "DTK", "The dragons return to Tarkir. Designed to be drafted with Fate Reforged, not Khans of Tarkir." },
            { "FRF", "A time-warped Tarkir in which the dragons never died out, drafted together with Khans of Tarkir in a unique format." },
            { "KTK", "Five wedge-based clans fight over Tarkir, in a set built around three-color decks and a time-traveling story." },
            { "M15", "A core set that mixes new and reprinted cards; it moved large sets to 80 uncommons for better limited play." },
            { "JOU", "The end of the first Theros block, with an enchantments-matter theme and Greek-mythology tropes." },
            { "BNG", "The second Theros set, with the gods returning and an ancient Greek-themed top-down design." },
            { "THS", "An ancient Greek-themed plane of heroes, monsters and gods, the first set to take place on Theros." },
            { "M14", "A core set built around Slivers, closely tied to its digital counterpart, Duels of the Planeswalkers." },
            { "DGM", "The only small set in the Return to Ravnica block, featuring all ten guilds and multicolor cards." },
            { "GTC", "Ravnica's guild system returns, with the Boros, Dimir, Gruul, Orzhov and Simic." },
            { "RTR", "A return to the city plane of Ravnica, focused on guilds and multicolor cards." },
            { "M13", "A core set with Exalted as its returning mechanic and no cards left from every core set since Alpha." },
            { "AVR", "The third Innistrad set and a large expansion, continuing the arc of the angel Avacyn's return." },
            { "DKA", "The second Innistrad set, a top-down horror set with a stronger emphasis on tribal creature types." },
            { "ISD", "A top-down gothic horror set with werewolves, vampires, zombies and ghosts, molding mechanics to fit the concept." },
            { "M12", "A core set with returning Planeswalkers and Titans, a minor Aura theme and the new hexproof ability." },
            { "NPH", "The Phyrexians conquer Mirrodin, concluding the story that began in Scars of Mirrodin." },
            { "MBS", "The Mirrans and the Phyrexians clash in the second Scars of Mirrodin set, a small expansion." },
            { "SOM", "A return to the artifact plane of Mirrodin, as the Phyrexians corrupt it from within." },
            { "M11", "A core set in which nearly half the cards were new, like its predecessor Magic 2010." },
            { "ROE", "The Eldrazi titans are freed on Zendikar, in a large third set of the block." },
            { "WWK", "The second Zendikar set, centered on lands, with an emphasis on Manlands and cards that turn lands into creatures." },
            { "ZEN", "Zendikar is the \"land set\", centered around lands and how players interact with them." }
        };

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "AER", "Aether Revolt" },
            { "DFT", "Aetherdrift" },
            { "AKH", "Amonkhet" },
            { "AVR", "Avacyn Restored" },
            { "TLA", "Avatar: The Last Airbender" },
            { "BFZ", "Battle for Zendikar" },
            { "BLB", "Bloomburrow" },
            { "BNG", "Born of the Gods" },
            { "M19", "Core Set 2019" },
            { "M20", "Core Set 2020" },
            { "M21", "Core Set 2021" },
            { "DKA", "Dark Ascension" },
            { "DMU", "Dominaria United" },
            { "DOM", "Dominaria" },
            { "DGM", "Dragon's Maze" },
            { "DTK", "Dragons of Tarkir" },
            { "AFR", "Dungeons & Dragons: Adventures in the Forgotten Realms" },
            { "DSK", "Duskmourn: House of Horror" },
            { "EOE", "Edge of Eternities" },
            { "EMN", "Eldritch Moon" },
            { "FRF", "Fate Reforged" },
            { "FIN", "Final Fantasy" },
            { "FDN", "Foundations" },
            { "GTC", "Gatecrash" },
            { "GRN", "Guilds of Ravnica" },
            { "HOU", "Hour of Devastation" },
            { "IKO", "Ikoria: Lair of Behemoths" },
            { "VOW", "Innistrad: Crimson Vow" },
            { "DBL", "Innistrad: Double Feature" },
            { "MID", "Innistrad: Midnight Hunt" },
            { "ISD", "Innistrad" },
            { "XLN", "Ixalan" },
            { "JOU", "Journey into Nyx" },
            { "KLD", "Kaladesh" },
            { "KHM", "Kaldheim" },
            { "NEO", "Kamigawa: Neon Dynasty" },
            { "KTK", "Khans of Tarkir" },
            { "ECL", "Lorwyn Eclipsed" },
            { "M11", "Magic 2011" },
            { "M12", "Magic 2012" },
            { "M13", "Magic 2013" },
            { "M14", "Magic 2014" },
            { "M15", "Magic 2015" },
            { "ORI", "Magic Origins" },
            { "MOM", "March of the Machine" },
            { "MSH", "Marvel Super Heroes" },
            { "SPM", "Marvel's Spider-Man" },
            { "MBS", "Mirrodin Besieged" },
            { "MH2", "Modern Horizons 2" },
            { "MH3", "Modern Horizons 3" },
            { "MH1", "Modern Horizons" },
            { "MKM", "Murders at Karlov Manor" },
            { "NPH", "New Phyrexia" },
            { "OGW", "Oath of the Gatewatch" },
            { "OTJ", "Outlaws of Thunder Junction" },
            { "ONE", "Phyrexia: All Will Be One" },
            { "RNA", "Ravnica Allegiance" },
            { "FRA", "Reality Fracture" },
            { "RTR", "Return to Ravnica" },
            { "ROE", "Rise of the Eldrazi" },
            { "RIX", "Rivals of Ixalan" },
            { "SOM", "Scars of Mirrodin" },
            { "SOS", "Secrets of Strixhaven" },
            { "SOI", "Shadows over Innistrad" },
            { "TRK", "Star Trek" },
            { "SNC", "Streets of New Capenna" },
            { "STX", "Strixhaven: School of Mages" },
            { "TDM", "Tarkir: Dragonstorm" },
            { "TMT", "Teenage Mutant Ninja Turtles" },
            { "BRO", "The Brothers' War" },
            { "HOB", "The Hobbit" },
            { "LTR", "The Lord of the Rings: Tales of Middle-earth" },
            { "LCI", "The Lost Caverns of Ixalan" },
            { "THB", "Theros Beyond Death" },
            { "THS", "Theros" },
            { "ELD", "Throne of Eldraine" },
            { "WAR", "War of the Spark" },
            { "WOE", "Wilds of Eldraine" },
            { "WWK", "Worldwake" },
            { "ZNR", "Zendikar Rising" },
            { "ZEN", "Zendikar" }
        };

        /// <summary>Title of a QuickDraft_&lt;SET&gt; event.</summary>
        public static string Title(string setCode)
        {
            string name;
            return "Quick Draft: " + (Names.TryGetValue(setCode.ToUpperInvariant(), out name) ? name : setCode);
        }

        /// <summary>Description of a QuickDraft_&lt;SET&gt; event.</summary>
        public static string Of(string setCode)
        {
            string blurb;
            string lead = Text.TryGetValue(setCode.ToUpperInvariant(), out blurb) ? blurb + " " : "";
            return lead + "Draft three packs against seven bots, then play with the deck you build.";
        }

        /// <summary>Key of the premier draft cube event (PremierDraft_VCUBE).</summary>
        public const string CubeKey = "VCUBE";

        /// <summary>Title of a PremierDraft_&lt;SET&gt; event.</summary>
        public static string PremierTitle(string key)
        {
            string name;
            if (key.ToUpperInvariant() == CubeKey) return "Premier Draft: Vintage Cube";
            return "Premier Draft: " + (Names.TryGetValue(key.ToUpperInvariant(), out name) ? name : key);
        }

        /// <summary>Description of a PremierDraft_&lt;SET&gt; event.</summary>
        public static string PremierOf(string key)
        {
            string blurb, lead;
            if (key.ToUpperInvariant() == CubeKey)
                lead = "MTGO's Vintage Cube: 540 singleton powerhouses, the Power Nine included. ";
            else
                lead = Text.TryGetValue(key.ToUpperInvariant(), out blurb) ? blurb + " " : "";
            return lead + "Draft at a table of eight, then play best-of-one matches against other players until 7 wins or 3 losses.";
        }
    }
}

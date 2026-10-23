using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Wizards.Mtga.FrontDoorModels;

namespace ForgeBridge
{
    /// <summary>Builds the match description the Front Door would normally push, pointing at the local Match Door.</summary>
    public static class LocalMatch
    {
        public static string Host = Environment.GetEnvironmentVariable("FORGEBRIDGE_HOST") ?? "127.0.0.1";
        public static int Port = int.TryParse(Environment.GetEnvironmentVariable("FORGEBRIDGE_PORT"), out var p) ? p : 30003;

        public static void InstallCertificateTrust()
        {
            // Only our self-signed local certificate (subject == issuer == CN=localhost) is accepted; everything else
            // keeps the normal validation result.
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                var c = cert as X509Certificate2 ?? (cert == null ? null : new X509Certificate2(cert));
                bool ours = c != null && c.Subject == "CN=localhost" && c.Issuer == c.Subject;
                Log.Info("TLS validation errors=" + errors + " subject=" + c?.Subject + " -> " + (ours ? "ACCEPT (local cert)" : "REJECT"));
                return ours;
            };
        }

        private static MatchPlayerConfig Player(uint seat, string name, bool dev = false) => new MatchPlayerConfig
        {
            SeatId = seat,
            TeamId = seat,
            ScreenName = name,
            IsWotc = dev,
            RankClass = "None",
            RankTier = 0,
            Commanders = new ClientCommanderInfo[0],
            VanitySelections = new Wizards.Mtga.Inventory.ClientVanitySelectionsV3
            {
                avatarSelection = null,
                cardBackSelection = null,
                titleSelection = null,
                emoteSelections = new List<string>(),
                petSelection = null,
            },
        };

        public static NewMatchCreatedConfig BuildConfig()
        {
            return new NewMatchCreatedConfig
            {
                controllerFabricUri = "fabric:/forge/local-match-1",
                matchEndpointHost = Host,
                matchEndpointPort = Port,
                eventId = "ForgeLocal",
                matchId = "local-match-1",
                matchType = MatchType.Queue,
                battlefield = "",
                clientMetadata = new Dictionary<string, string>(),
                PlayerInfos = new[] { Player(1, LocalAccountClient.DisplayName, LocalProfile.IsDev), Player(2, "Forge AI") },
                YourSeatId = 1,
            };
        }
    }
}

using System;
using WAS;
using Wizards.Arena.Promises;
using Wizards.Platform.Sdk.Model;

namespace ForgeBridge
{
    /// <summary>
    /// Offline stand-in for WizardsAccountsClient. Always "logged in" as a fixed local persona.
    /// Everything that would talk to Wizards' account/store services fails fast.
    /// </summary>
    public class LocalAccountClient : IAccountClient
    {
        public const string PersonaId = "LOCALPLAYER0000000000000001";
        // FORGEBRIDGE_NAME: the launcher's -Name, so two clients on one machine show different names
        public static readonly string DisplayName = (Environment.GetEnvironmentVariable("FORGEBRIDGE_NAME") ?? "Forge Player") + "#00001";

        private readonly AccountInformation _info;
        private LoginState _state = LoginState.FullyRegisteredLogin;

        public LocalAccountClient()
        {
            _info = new AccountInformation
            {
                AccountID = "local-account",
                GameID = "local-game",
                DisplayName = DisplayName,
                PersonaID = PersonaId,
                AccessToken = "local-token",
                Email = "local@forgebridge.invalid",
                ExternalID = "local-external",
                CountryCode = "US",
                Expiration = DateTime.UtcNow.AddYears(10),
                // expiresAfter is added to "now" as an int: int.MaxValue overflowed into an always-expired token
                // (TokenManager then showed "Connection Lost"). Ten years is plenty.
                Credentials = new Credentials("local-jwt", "local-refresh", 10 * 365 * 24 * 3600, "local"),
                // MTGA_DEBUG enables the client's debug overlays (log window on every error); opt in with FORGEBRIDGE_DEBUGROLE=1
                Roles = Environment.GetEnvironmentVariable("FORGEBRIDGE_DEBUGROLE") == "1" ? new[] { "MTGA_DEBUG" } : new string[0],
            };
        }

        public AccountInformation AccountInformation => _info;
        public LoginState CurrentLoginState => _state;
        public bool RememberMe { get; set; } = true;
        public bool AllowAccountCreation { get; set; }
        public bool IsPreProd => false;
        public string UpdateToken { get; set; }

        public event Action<LoginState> LoginStateChanged;

        private static Promise<T> Fail<T>(string what)
        {
            Log.Info("LocalAccountClient: unsupported call " + what);
            return new SimplePromise<T>(new Error(-1, "ForgeBridge local mode: " + what));
        }

        public void SetCredentials(EnvironmentDescription env) { }

        public Promise<AccountInformation> LogIn_Fast() => new SimplePromise<AccountInformation>(_info);
        public Promise<AccountInformation> LogIn_Credentials(string email, string password) => new SimplePromise<AccountInformation>(_info);
        public Promise<AccountInformation> SocialLogin_Fast(bool manualLogin) => new SimplePromise<AccountInformation>(_info);
        public Promise<AccountInformation> Debug_LogIn_Familiar(string email, string password) => new SimplePromise<AccountInformation>(_info);

        public Promise<CreateUserResponse> RegisterAsSocialAccount(string email, string displayName, bool receiveOffersOptIn, bool dataShareOptIn, string birthday, string country) => Fail<CreateUserResponse>("RegisterAsSocialAccount");
        public Promise<CreateUserResponse> RegisterAsFullAccount(string email, string password, string displayName, bool receiveOffersOptIn, bool dataShareOptIn, string birthday, string country) => Fail<CreateUserResponse>("RegisterAsFullAccount");
        public Promise<CreateUserResponse> CanRegister(string email, string password, string displayName, bool receiveOffersOptIn, bool dataShareOptIn, string birthday, string country) => Fail<CreateUserResponse>("CanRegister");

        public void LogOut() { }

        public Promise<string> RedeemCode(string code) => Fail<string>("RedeemCode");
        public Promise<AgeCheckForAgeGatingResponse> GetAgeGate(string country, string dateOfBirth) => Fail<AgeCheckForAgeGatingResponse>("GetAgeGate");
        public Promise<string> UpdateParentalConsent(string country, string dateOfBirth) => Fail<string>("UpdateParentalConsent");
        public Promise<string> SendPasswordRecoveryEmail(string email) => Fail<string>("SendPasswordRecoveryEmail");
        public Promise<string> ValidateUsername(string username) => Fail<string>("ValidateUsername");
        public Promise<LoginResponse> RefreshAccessToken() => new SimplePromise<LoginResponse>(new LoginResponse());
        public Promise<ValidateReceiptResponse> TryValidateReceipt(string productId, AppStore platform, string packageName, string receipt) => Fail<ValidateReceiptResponse>("TryValidateReceipt");
        public Promise<PurchaseTokenResponse> GetPurchaseToken(string currency, string sku, TransactionType transactionType) => Fail<PurchaseTokenResponse>("GetPurchaseToken");
        public Promise<ProfileToken> GetProfileToken() => Fail<ProfileToken>("GetProfileToken");
        public Promise<ItemsResponse> GetStoreItems(string currency) => Fail<ItemsResponse>("GetStoreItems");
        public Promise<SteamReceipt> InitSteamPurchase(string steamSessionTicket, string language, string currency, string sku, bool sandbox) => Fail<SteamReceipt>("InitSteamPurchase");
        public Promise<LoginResponse> LinkSocialAccount() => Fail<LoginResponse>("LinkSocialAccount");
        public Promise<ConflictingPersonas> GetLinkConflictInfo() => Fail<ConflictingPersonas>("GetLinkConflictInfo");
        public Promise<LoginResponse> ResolveLinkConflict(ConflictingPersona personaToKeep, ConflictingPersona personaToDiscard) => Fail<LoginResponse>("ResolveLinkConflict");
        public Promise<LoginResponse> CancelAccountLinking() => Fail<LoginResponse>("CancelAccountLinking");
        public Promise<SocialIdentities> GetLinkedAccounts() => Fail<SocialIdentities>("GetLinkedAccounts");
        public void Reset() { }
    }
}

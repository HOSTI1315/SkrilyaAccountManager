using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    public sealed class GeneratorBalance
    {
        public decimal Balance { get; set; }
        public string Role { get; set; }
    }

    public sealed class GeneratedAccount
    {
        public string Username { get; set; }
        public long UserId { get; set; }
        public string Type { get; set; }
        public decimal Cost { get; set; }
        public string Region { get; set; }
        public Account Account { get; set; }
    }

    /// <summary>
    /// Thin opt-in BloxGen client. The provider API key is deliberately never persisted by Skrilya: it only lives
    /// in the request that the user explicitly makes. Generated cookies immediately enter the normal encrypted
    /// account store and are never returned to the HTML UI.
    /// </summary>
    internal static class AccountGenerator
    {
        private const string BaseUrl = "https://core.bloxgen.net";
        private static readonly HttpClient Http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(45) };
        private static readonly string[] Types = { "alt", "+30 days old", "+1 year old", "5+ years old", "dump" };

        public static string[] AccountTypes => Types.ToArray();
        public static bool Enabled
        {
            get
            {
                try { return AccountManager.General != null && AccountManager.General.Exists("BloxGenEnabled") && AccountManager.General.Get<bool>("BloxGenEnabled"); }
                catch { return false; }
            }
        }

        public static async Task<GeneratorBalance> GetBalanceAsync(string ApiKey, CancellationToken Token = default)
        {
            RequireEnabled();
            RequireKey(ApiKey);
            using HttpResponseMessage Response = await Http.GetAsync("/api/balance?apiKey=" + Uri.EscapeDataString(ApiKey.Trim()), Token).ConfigureAwait(false);
            JObject Body = await Read(Response, Token).ConfigureAwait(false);
            EnsureSuccess(Response, Body);

            JToken Data = Body["data"] ?? throw new InvalidOperationException("BloxGen returned no balance data.");
            return new GeneratorBalance
            {
                Balance = Data.Value<decimal?>("balance") ?? 0,
                Role = Data.Value<string>("role") ?? "unknown"
            };
        }

        public static async Task<GeneratedAccount> GenerateAsync(string ApiKey, string Type, string Region = null, CancellationToken Token = default)
        {
            RequireEnabled();
            RequireKey(ApiKey);
            if (!Types.Contains(Type)) throw new ArgumentException("Unknown BloxGen account type.");

            string NormalizedRegion = string.IsNullOrWhiteSpace(Region) ? null : Region.Trim().ToUpperInvariant();
            if (NormalizedRegion != null && (NormalizedRegion.Length != 2 || !NormalizedRegion.All(char.IsLetter)))
                throw new ArgumentException("Region must be a two-letter code such as DE, US or GB.");

            var Payload = new JObject { ["apiKey"] = ApiKey.Trim(), ["type"] = Type };
            if (NormalizedRegion != null) Payload["region"] = NormalizedRegion;

            using StringContent Content = new StringContent(Payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using HttpResponseMessage Response = await Http.PostAsync("/api/generate", Content, Token).ConfigureAwait(false);
            JObject Body = await Read(Response, Token).ConfigureAwait(false);
            EnsureSuccess(Response, Body);

            JToken Data = Body["data"] ?? throw new InvalidOperationException("BloxGen returned no account data.");
            string Cookie = Data.Value<string>("cookie");
            if (string.IsNullOrWhiteSpace(Cookie)) throw new InvalidOperationException("BloxGen response did not include a Roblox cookie.");

            string Password = AccountManager.General.Get<bool>("SavePasswords") ? (Data.Value<string>("password") ?? string.Empty) : string.Empty;

            // AddAccount performs Roblox-side validation before writing anything to the store. A provider success
            // whose cookie is already dead is therefore surfaced as a failure instead of adding a broken row.
            Account Account = await Task.Run(() => AccountManager.AddAccount(Cookie, Password)).ConfigureAwait(false);
            if (Account == null) throw new InvalidOperationException("BloxGen generated an account, but Roblox rejected the returned cookie.");

            return new GeneratedAccount
            {
                Username = Account.Username,
                UserId = Account.UserID,
                Type = Data.Value<string>("type") ?? Type,
                Cost = Data.Value<decimal?>("cost") ?? 0,
                Region = Data.Value<string>("region") ?? NormalizedRegion,
                Account = Account
            };
        }

        private static async Task<JObject> Read(HttpResponseMessage Response, CancellationToken Token)
        {
            string Text = await Response.Content.ReadAsStringAsync(Token).ConfigureAwait(false);
            try { return JObject.Parse(Text); }
            catch { throw new InvalidOperationException($"BloxGen returned {(int)Response.StatusCode} with an unreadable response."); }
        }

        private static void EnsureSuccess(HttpResponseMessage Response, JObject Body)
        {
            if (Response.IsSuccessStatusCode && Body.Value<bool?>("success") == true) return;

            string Message = Body.Value<string>("message") ?? Body.Value<string>("error") ?? $"HTTP {(int)Response.StatusCode}";
            long? WaitMs = Body.Value<long?>("timeRemaining");
            if (WaitMs > 0) Message += $" Try again in {Math.Ceiling(WaitMs.Value / 1000.0):0}s.";
            throw new InvalidOperationException(Message);
        }

        private static void RequireKey(string ApiKey)
        {
            if (string.IsNullOrWhiteSpace(ApiKey)) throw new ArgumentException("A BloxGen API key is required.");
        }

        private static void RequireEnabled()
        {
            if (!Enabled) throw new InvalidOperationException("BloxGen is disabled. Enable [General] BloxGenEnabled explicitly before using the third-party provider.");
        }
    }
}

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Classes.Android;
using RBX_Alt_Manager.Forms;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace RBX_Alt_Manager
{
    public class Account : IComparable<Account>
    {
        public bool Valid;
        public string SecurityToken;
        public string Username;
        public DateTime LastUse;
        private string _Alias = "";
        private string _Description = "";
        private string _Password = "";
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string Group { get; set; } = "Default";
        public long UserID;
        // Concurrent, not plain: SetField is called from launch workers and the web API while the save timer
        // serializes every account's Fields on another thread. A Dictionary torn mid-enumeration throws inside
        // SerializeObject, and that exception is swallowed by the save path — the write is simply lost.
        // Serializes to exactly the same JSON object, so AccountData.json is unchanged.
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Fields = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        public DateTime LastAttemptedRefresh;
        [JsonIgnore] public DateTime PinUnlocked;
        [JsonIgnore] public DateTime TokenSet;
        [JsonIgnore] public DateTime LastAppLaunch;
        [JsonIgnore] public string CSRFToken;

        /// <summary>
        /// Why the last authentication attempt failed, in a form worth showing: "CAPTCHA" when Roblox answered
        /// the ticket request with a challenge rather than a ticket. That case is indistinguishable from a dead
        /// cookie at the HTTP level unless the challenge headers are read, and telling them apart is the
        /// difference between "log in once by hand" and "this account is gone".
        /// </summary>
        [JsonIgnore] public string LastAuthFailure;
        [JsonIgnore] public UserPresence Presence;

        // Serializes launches for THIS account. A manual launch, a batch launch, the web API and Nexus
        // AutoRelaunch can all call JoinServer concurrently; two overlapping launches double-log-in the account
        // and Roblox kicks the first session ("played from another device"). Try-acquire, reject if busy.
        [JsonIgnore] private readonly System.Threading.SemaphoreSlim LaunchLock = new System.Threading.SemaphoreSlim(1, 1);

        // Matched against every RobloxPlayerBeta command line on the 350ms window-position poll. Shared with the
        // watcher and the resource manager so all three recognise both the flag and the URI form of the tracker id.
        private static readonly Regex TrackerRegex = ClientLauncher.TrackerRegex;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        public int CompareTo(Account compareTo)
        {
            if (compareTo == null)
                return 1;
            else
                return Group.CompareTo(compareTo.Group);
        }

        public string BrowserTrackerID;

        public string Alias
        {
            get => _Alias;
            set
            {
                if (value == null || value.Length > 50 || value == _Alias)
                    return;

                _Alias = value;
                AccountManager.SaveAccounts();
            }
        }
        public string Description
        {
            get => _Description;
            set
            {
                if (value == null || value.Length > 5000 || value == _Description)
                    return;

                _Description = value;
                AccountManager.SaveAccounts();
            }
        }
        public string Password
        {
            get => _Password;
            set
            {
                if (value == null || value.Length > 5000 || value == _Password)
                    return;

                _Password = value;
                AccountManager.SaveAccounts();
            }
        }

        public Account() { }

        public Account(string Cookie, string AccountJSON = null)
        {
            SecurityToken = Cookie;

            AccountJSON ??= AccountManager.MainClient.Execute(MakeRequest("my/account/json", Method.Get)).Content;

            // Lenient parse: only require UserId + Name. The strict AccountJson mapping
            // (MissingMemberHandling.Error) makes EVERY add fail if Roblox adds a field to this response.
            if (!string.IsNullOrEmpty(AccountJSON) && AccountJSON.TryParseJson(out JObject Data) && Data != null && Data["UserId"] != null && Data["Name"] != null)
            {
                Username = Data["Name"].Value<string>();
                UserID = Data["UserId"].Value<long>();

                Valid = true;

                LastUse = DateTime.Now;

                AccountManager.LastValidAccount = this;
            }
        }

        public RestRequest MakeRequest(string url, Method method = Method.Get) => new RestRequest(url, method).AddCookie(".ROBLOSECURITY", SecurityToken, "/", ".roblox.com");

        /// <summary>
        /// Requests an authentication ticket. When <paramref name="Proxy"/> is set the request leaves through that
        /// proxy — the ticket is bound to the IP that asked for it, so this has to be the same exit the client
        /// will redeem it from, or Roblox answers 403 and the launch dies looking like a bad cookie.
        /// </summary>
        public bool GetAuthTicket(out string Ticket, ProxyConfig Proxy = null)
        {
            Ticket = string.Empty;
            LastAuthFailure = null;

            RestClient Client = AccountProxies.AuthClientFor(Proxy) ?? AccountManager.AuthClient;

            // Two attempts: a 403/409 here means the CSRF token went stale between the two calls (Roblox rotates
            // it on its own schedule), and the fix is a fresh token, not a fresh cookie. Retrying with the SAME
            // token is pointless, which is why the token is re-fetched at the top of each attempt.
            for (int Attempt = 0; Attempt < 2; Attempt++)
            {
                if (!GetCSRFToken(out string Token, Proxy)) return false;

                // Roblox rejects this POST with 415 UnsupportedMediaType unless it carries a JSON body and content
                // type. The explicit Content-Type header is load-bearing: on this stack AddJsonBody alone emits
                // "application/json; charset=utf-8", while every working client sends the bare "application/json".
                // Do NOT remove it on the grounds that AddJsonBody already sets one.
                RestRequest request = MakeRequest("/v1/authentication-ticket/", Method.Post)
                    .AddHeader("X-CSRF-TOKEN", Token)
                    .AddHeader("Content-Type", "application/json")
                    .AddHeader("Origin", "https://www.roblox.com")
                    .AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP")
                    .AddJsonBody(new { });

                RestResponse response = Client.Execute(request);

                // Roblox rotates .ROBLOSECURITY via Set-Cookie every few hours-to-days; a client that keeps sending
                // the old value eventually gets 401'd — this is why alts "go invalid so quickly". Pick the rotated
                // cookie up here, on the launch hot path.
                HarvestRotatedCookie(response);

                Parameter TicketHeader = response.Headers.FirstOrDefault(x => x.Name == "rbx-authentication-ticket");

                if (TicketHeader != null && !string.IsNullOrEmpty((string)TicketHeader.Value))
                {
                    Ticket = (string)TicketHeader.Value;

                    return true;
                }

                // A challenge is not a dead cookie. Roblox answers the ticket request with 403 and a set of
                // rblx-challenge-* headers when it wants a captcha solved, which looks identical to "signed out"
                // unless those headers are read — and the two need completely different responses from the user.
                if (IsChallenge(response, out string ChallengeType))
                {
                    LastAuthFailure = "CAPTCHA";

                    Program.Logger.Warn($"[AuthTicket] {Username}: Roblox demanded a challenge ({ChallengeType}) instead of issuing a ticket");

                    return false;
                }

                // 429 is per-IP, not per-account: retrying from the same address only digs the hole deeper. It is
                // the symptom a per-account proxy exists to cure, so say so instead of burning another attempt.
                if ((int)response.StatusCode == 429)
                {
                    LastAuthFailure = "RATE LIMITED";

                    Program.Logger.Warn($"[AuthTicket] {Username}: rate limited (429){(Proxy == null ? " — assign proxies to accounts to spread launches across IPs" : $" on {Proxy}")}");

                    return false;
                }

                bool Stale = response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Conflict;

                // Log what Roblox actually said, so a persistent failure is diagnosable instead of a blank "invalid ticket".
                Program.Logger.Warn($"[AuthTicket] {Username}: no ticket header [{(int)response.StatusCode} {response.StatusCode}]{(Proxy != null ? $" via {Proxy}" : string.Empty)} {response.Content}");

                if (!Stale || Attempt == 1)
                {
                    LastAuthFailure = LastAuthFailure ?? $"{(int)response.StatusCode}";

                    return false;
                }

                CSRFToken = string.Empty;
            }

            return false;
        }

        /// <summary>
        /// True when the response is Roblox asking for a challenge (captcha / 2-step) rather than refusing the
        /// account. Roblox marks these with rblx-challenge-id / rblx-challenge-type headers; older flows put the
        /// same information in the body's errors[].code 0 with a challenge message.
        /// </summary>
        private static bool IsChallenge(RestResponse response, out string ChallengeType)
        {
            ChallengeType = null;

            if (response?.Headers == null) return false;

            Parameter Type = response.Headers.FirstOrDefault(Header => string.Equals(Header.Name, "rblx-challenge-type", StringComparison.OrdinalIgnoreCase));
            Parameter Id = response.Headers.FirstOrDefault(Header => string.Equals(Header.Name, "rblx-challenge-id", StringComparison.OrdinalIgnoreCase));

            if (Type != null || Id != null)
            {
                ChallengeType = (string)(Type?.Value) ?? "unknown";

                return true;
            }

            // Body fallback: the ticket endpoint has historically returned a plain 403 whose content names the
            // challenge, with no headers at all.
            if (!string.IsNullOrEmpty(response.Content) &&
                (response.Content.IndexOf("challenge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 response.Content.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ChallengeType = "body";

                return true;
            }

            return false;
        }

        /// <summary>
        /// Adopts a rotated .ROBLOSECURITY if the response carried one. Only replaces the stored cookie when the
        /// new value looks genuine (has the WARNING marker, plausible length) and actually differs, so a
        /// truncated or junk Set-Cookie can never clobber a working cookie and sign the account out.
        /// </summary>
        internal void HarvestRotatedCookie(RestResponse response)
        {
            var Rotated = response?.Cookies?[".ROBLOSECURITY"];

            if (Rotated != null && !string.IsNullOrEmpty(Rotated.Value) && Rotated.Value.Length > 100 && Rotated.Value.Contains("WARNING") && Rotated.Value != SecurityToken)
            {
                SecurityToken = Rotated.Value;
                LastUse = DateTime.Now;

                AccountManager.SaveAccounts();
            }
        }

        public bool GetCSRFToken(out string Result, ProxyConfig Proxy = null)
        {
            RestRequest request = MakeRequest("v1/authentication-ticket/", Method.Post).AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP");

            // Same exit as the ticket request: this call is what mints the token the ticket call must present.
            RestResponse response = (AccountProxies.AuthClientFor(Proxy) ?? AccountManager.AuthClient).Execute(request);

            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                Result = $"[{(int)response.StatusCode} {response.StatusCode}] {response.Content}";
                return false;
            }

            Parameter result = response.Headers.FirstOrDefault(x => x.Name == "x-csrf-token");

            string Token = string.Empty;

            if (result != null)
            {
                Token = (string)result.Value;
                LastUse = DateTime.Now;

                AccountManager.LastValidAccount = this;
                // Note: do NOT SaveAccounts() here. GetCSRFToken gates almost every operation (join, webserver
                // request, etc.); saving the whole list on each call froze the app at scale. LastUse is durable
                // state that gets flushed by the periodic/exit save instead.
            }

            CSRFToken = Token;
            TokenSet = DateTime.Now;
            Result = Token;

            return !string.IsNullOrEmpty(Result);
        }

        public bool CheckPin(bool Internal = false)
        {
            if (!GetCSRFToken(out _))
            {
                if (!Internal) Shell.Error("Invalid Account Session!");

                return false;
            }

            if (DateTime.Now < PinUnlocked)
                return true;

            RestRequest request = MakeRequest("v1/account/pin/", Method.Get).AddHeader("Referer", "https://www.roblox.com/");

            RestResponse response = AccountManager.AuthClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                JObject pinInfo = JObject.Parse(response.Content);

                if (!pinInfo["isEnabled"].Value<bool>() || (pinInfo["unlockedUntil"].Type != JTokenType.Null && pinInfo["unlockedUntil"].Value<int>() > 0)) return true;
            }

            if (!Internal) Shell.Error("Pin required!");

            return false;
        }

        public bool UnlockPin(string Pin)
        {
            if (Pin.Length != 4) return false;
            if (CheckPin(true)) return true;

            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("v1/account/pin/unlock", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("pin", Pin);

            RestResponse response = AccountManager.AuthClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                JObject pinInfo = JObject.Parse(response.Content);

                if (pinInfo["isEnabled"].Value<bool>() && pinInfo["unlockedUntil"].Value<int>() > 0)
                    PinUnlocked = DateTime.Now.AddSeconds(pinInfo["unlockedUntil"].Value<int>());

                if (PinUnlocked > DateTime.Now)
                {
                    Shell.Info("Pin unlocked for 5 minutes");

                    return true;
                }
            }

            return false;
        }

        public async Task<string> GetEmailJSON()
        {
            RestRequest DataRequest = MakeRequest("v1/email", Method.Get);

            RestResponse response = await AccountManager.AccountClient.ExecuteAsync(DataRequest);

            return response.Content;
        }

        public async Task<JToken> GetMobileInfo()
        {
            RestRequest DataRequest = MakeRequest("mobileapi/userinfo", Method.Get);

            RestResponse response = await AccountManager.MainClient.ExecuteAsync(DataRequest);

            if (response.StatusCode == HttpStatusCode.OK && Utilities.TryParseJson(response.Content, out JToken Data))
                return Data;

            return null;
        }

        public async Task<JToken> GetUserInfo()
        {
            RestRequest DataRequest = MakeRequest($"v1/users/{UserID}", Method.Get);

            RestResponse response = await AccountManager.UsersClient.ExecuteAsync(DataRequest);

            if (response.StatusCode == HttpStatusCode.OK && Utilities.TryParseJson(response.Content, out JToken Data))
                return Data;

            return null;
        }

        public async Task<long> GetRobux() => (await GetMobileInfo())?["RobuxBalance"]?.Value<long>() ?? 0;

        public bool SetFollowPrivacy(int Privacy)
        {
            if (!CheckPin()) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("account/settings/follow-me-privacy", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/my/account")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded");

            switch (Privacy)
            {
                case 0:
                    request.AddParameter("FollowMePrivacy", "All");
                    break;
                case 1:
                    request.AddParameter("FollowMePrivacy", "Followers");
                    break;
                case 2:
                    request.AddParameter("FollowMePrivacy", "Following");
                    break;
                case 3:
                    request.AddParameter("FollowMePrivacy", "Friends");
                    break;
                case 4:
                    request.AddParameter("FollowMePrivacy", "NoOne");
                    break;
            }

            RestResponse response = AccountManager.MainClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK) return true;

            return false;
        }

        public bool ChangePassword(string Current, string New)
        {
            if (!CheckPin()) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("v2/user/passwords/change", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("currentPassword", Current)
                .AddParameter("newPassword", New);

            RestResponse response = AccountManager.AuthClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                Password = New;

                var SToken = response.Cookies[".ROBLOSECURITY"];

                if (SToken != null)
                {
                    SecurityToken = SToken.Value;
                    AccountManager.SaveAccounts();
                }
                else
                    Shell.Error("An error occured while changing passwords, you will need to re-login with your new password!");

                Shell.Info("Password changed!");

                return true;
            }

            Shell.Error("Failed to change password!");

            return false;
        }

        public bool ChangeEmail(string Password, string NewEmail)
        {
            if (!CheckPin()) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("v1/email", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded")
                .AddParameter("password", Password)
                .AddParameter("emailAddress", NewEmail);

            RestResponse response = AccountManager.AccountClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                Shell.Info("Email changed!");

                return true;
            }

            Shell.Error("Failed to change email, maybe your password is incorrect!");

            return false;
        }

        public bool LogOutOfOtherSessions(bool Internal = false)
        {
            if (!CheckPin(Internal)) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest request = MakeRequest("authentication/signoutfromallsessionsandreauthenticate", Method.Post)
                .AddHeader("Referer", "https://www.roblox.com/")
                .AddHeader("X-CSRF-TOKEN", Token)
                .AddHeader("Content-Type", "application/x-www-form-urlencoded");

            RestResponse response = AccountManager.MainClient.Execute(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                var SToken = response.Cookies[".ROBLOSECURITY"];

                if (SToken != null)
                {
                    SecurityToken = SToken.Value;
                    AccountManager.SaveAccounts(true);
                }
                else if (!Internal)
                    Shell.Error("An error occured, you will need to re-login!");

                if (!Internal) Shell.Info("Signed out of all other sessions!");

                return true;
            }

            if (!Internal) Shell.Error("Failed to log out of other sessions!");

            return false;
        }

        public bool TogglePlayerBlocked(string Username, ref bool Unblocked)
        {
            if (!CheckPin()) throw new Exception("Pin is Locked!");
            if (!AccountManager.GetUserID(Username, out long BlockeeID, out _)) throw new Exception($"Failed to obtain UserId of {Username}!");

            RestResponse BlockedResponse = GetBlockedList();

            if (!BlockedResponse.IsSuccessful) throw new Exception("Failed to obtain blocked users list!");

            string BlockedUsers = BlockedResponse.Content;

            if (!Regex.IsMatch(BlockedUsers, $"\\b{BlockeeID}\\b"))
                return BlockUserId($"{BlockeeID}").IsSuccessful;

            Unblocked = true;

            return BlockUserId($"{BlockeeID}", Unblock: true).IsSuccessful;
        }

        public RestResponse BlockUserId(string UserID, bool SkipPinCheck = false, HttpListenerContext Context = null, bool Unblock = false)
        {
            if (Context != null) Context.Response.StatusCode = 401;
            if (!SkipPinCheck && !CheckPin(true)) throw new Exception("Pin Locked");
            if (!GetCSRFToken(out string Token)) throw new Exception("Invalid X-CSRF-Token");

            RestRequest blockReq = MakeRequest($"v1/users/{UserID}/{(Unblock ? "unblock" : "block")}", Method.Post).AddHeader("X-CSRF-TOKEN", Token);

            RestResponse blockRes = AccountManager.AccountClient.Execute(blockReq);

            Program.Logger.Info($"Block Response for {UserID} | Unblocking: {Unblock}: [{blockRes.StatusCode}] {blockRes.Content}");

            if (Context != null)
                Context.Response.StatusCode = (int)blockRes.StatusCode;

            return blockRes;
        }

        public RestResponse UnblockUserId(string UserID, bool SkipPinCheck = false, HttpListenerContext Context = null) => BlockUserId(UserID, SkipPinCheck, Context, true);

        public bool UnblockEveryone(out string Response)
        {
            if (!CheckPin(true)) { Response = "Pin is Locked"; return false; }

            RestResponse response = GetBlockedList();

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK)
            {
                Task.Run(async () =>
                {
                    JObject List = JObject.Parse(response.Content);

                    if (List.ContainsKey("blockedUsers"))
                    {
                        foreach (var User in List["blockedUsers"])
                        {
                            if (!UnblockUserId(User["userId"].Value<string>(), true).IsSuccessful)
                            {
                                await Task.Delay(20000);

                                UnblockUserId(User["userId"].Value<string>(), true);

                                if (!CheckPin(true))
                                    break;
                            }
                        }
                    }
                });

                Response = "Unblocking Everyone";

                return true; 
            }

            Response = "Failed to unblock everyone";

            return false;
        }

        public RestResponse GetBlockedList(HttpListenerContext Context = null)
        {
            if (Context != null) Context.Response.StatusCode = 401;

            if (!CheckPin(true)) throw new Exception("Pin is Locked");

            RestRequest request = MakeRequest($"v1/users/get-detailed-blocked-users", Method.Get);

            RestResponse response = AccountManager.AccountClient.Execute(request);

            if (Context != null) Context.Response.StatusCode = (int)response.StatusCode;

            return response;
        }

        public bool ParseAccessCode(RestResponse response, out string Code)
        {
            Code = "";

            Match match = Regex.Match(response.Content, "Roblox.GameLauncher.joinPrivateGame\\(\\d+\\,\\s*'(\\w+\\-\\w+\\-\\w+\\-\\w+\\-\\w+)'");

            if (match.Success && match.Groups.Count == 2)
            {
                Code = match.Groups[1]?.Value ?? string.Empty;

                return true;
            }

            return false;
        }

        public async Task<string> JoinServer(long PlaceID, string JobID = "", bool FollowUser = false, bool JoinVIP = false, bool Internal = false) // oh god i am not refactoring everything to be async im sorry
        {
            if (!LaunchLock.Wait(0))
                return "ERROR: A launch for this account is already in progress. Please wait a moment.";

            try
            {
            LastAppLaunch = DateTime.Now;
            LastUse = DateTime.Now; // a launched account is by definition in use — stops AutoCookieRefresh's
                                    // 20-day-idle sign-out from kicking a client we just launched.

            if (string.IsNullOrEmpty(BrowserTrackerID))
            {
                Random r = new Random();

                BrowserTrackerID = r.Next(100000, 175000).ToString() + r.Next(100000, 900000).ToString(); // oh god this is ugly
            }

            try { ClientSettingsPatcher.PatchSettings(); } catch (Exception Ex) { Program.Logger.Error($"Failed to patch ClientAppSettings: {Ex}"); }

            // Resolved before anything is requested: every identity call below, and the client itself, have to
            // leave through this one exit or the ticket will not be redeemable.
            ProxyConfig Proxy = AccountProxies.For(this);

            if (Proxy != null)
            {
                string ExitIP = await AccountProxies.CheckExitIP(this, Proxy);

                if (AccountProxies.VerifyExitIP && string.IsNullOrEmpty(ExitIP))
                    return $"ERROR: Proxy {Proxy} is not reachable. Fix or clear the account's Proxy field, or turn off ProxyVerifyExitIP.";
            }

            if (!GetCSRFToken(out string Token, Proxy)) return $"ERROR: Account Session Expired, re-add the account or try again. (Invalid X-CSRF-Token)\n{Token}";

            if (AccountManager.ShuffleJobID && string.IsNullOrEmpty(JobID))
                JobID = await Utilities.GetRandomJobId(PlaceID);

            if (GetAuthTicket(out string Ticket, Proxy))
            {
                if (AccountManager.General.Get<bool>("AutoCloseLastProcess"))
                {
                    try
                    {
                        foreach(Process proc in Process.GetProcessesByName("RobloxPlayerBeta"))
                        {
                            var TrackerMatch = TrackerRegex.Match(proc.GetCommandLine());
                            string TrackerID = TrackerMatch.Success ? TrackerMatch.Groups[1].Value : string.Empty;

                            if (TrackerID == BrowserTrackerID)
                            {
                                try // ignore ObjectDisposedExceptions
                                {
                                    proc.CloseMainWindow();
                                    await Task.Delay(250);
                                    proc.CloseMainWindow(); // Allows Roblox to disconnect from the server so we don't get the "Same account launched" error
                                    await Task.Delay(250);
                                    proc.Kill();
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception x) { Program.Logger.Error($"An error occured attempting to close {Username}'s last process(es): {x}"); }
                }

                string LinkCode = string.IsNullOrEmpty(JobID) ? string.Empty : Regex.Match(JobID, "privateServerLinkCode=(.+)")?.Groups[1]?.Value;
                string AccessCode = JobID;

                if (!string.IsNullOrEmpty(LinkCode))
                {
                    RestRequest request = MakeRequest(string.Format("/games/{0}?privateServerLinkCode={1}", PlaceID, LinkCode), Method.Get).AddHeader("X-CSRF-TOKEN", Token).AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP");

                    RestResponse response = await AccountManager.MainClient.ExecuteAsync(request);

                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (ParseAccessCode(response, out string Code))
                        {
                            JoinVIP = true;
                            AccessCode = Code;
                        }
                    }
                    else if (response.StatusCode == HttpStatusCode.Redirect) // thx wally (p.s. i hate wally)
                    {
                        request = MakeRequest(string.Format("/games/{0}?privateServerLinkCode={1}", PlaceID, LinkCode), Method.Get).AddHeader("X-CSRF-TOKEN", Token).AddHeader("Referer", "https://www.roblox.com/games/4924922222/Brookhaven-RP");

                        RestResponse result = await AccountManager.Web13Client.ExecuteAsync(request);

                        if (result.StatusCode == HttpStatusCode.OK)
                        {
                            // Parse the re-fetched page (result), NOT the stale 302 redirect (response) — the
                            // redirect body has no joinPrivateGame markup, so this silently found no access code
                            // and fell back to a public join instead of entering the VIP server.
                            if (ParseAccessCode(result, out string Code))
                            {
                                JoinVIP = true;
                                AccessCode = Code;
                            }
                        }
                    }
                }

                if (JoinVIP)
                {
                    var request = MakeRequest("/account/settings/private-server-invite-privacy").AddHeader("X-CSRF-TOKEN", Token).AddHeader("Referer", "https://www.roblox.com/my/account");

                    RestResponse result = await AccountManager.MainClient.ExecuteAsync(request);

                    if (result.IsSuccessful && !result.Content.Contains("\"AllUsers\""))
                    {
                        if (Shell.Confirm("SkrilyaAccountManager", "Account Manager has detected your account's privacy settings do not allow you to join private servers.", "Would you like to change this setting to Everyone now?") && CheckPin(true))
                        {
                            var setRequest = MakeRequest("/account/settings/private-server-invite-privacy", Method.Post);

                            setRequest.AddHeader("X-CSRF-TOKEN", Token);
                            setRequest.AddHeader("Referer", "https://www.roblox.com/my/account");
                            setRequest.AddHeader("Content-Type", "application/x-www-form-urlencoded");

                            setRequest.AddParameter("PrivateServerInvitePrivacy", "AllUsers");

                            // No longer on the UI thread: this request used to run inside the Invoke and froze
                            // painting until Roblox answered.
                            AccountManager.MainClient.Execute(setRequest);
                        }
                    }
                }

                double LaunchTime = Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds * 1000);

                string PlaceLauncherUrl = JoinVIP
                    ? $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestPrivateGame&placeId={PlaceID}&accessCode={AccessCode}&linkCode={LinkCode}"
                    : FollowUser
                        ? $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestFollowUser&userId={PlaceID}"
                        : $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{(string.IsNullOrEmpty(JobID) ? "" : "Job")}&browserTrackerId={BrowserTrackerID}&placeId={PlaceID}{(string.IsNullOrEmpty(JobID) ? "" : ("&gameId=" + JobID))}&isPlayTogetherGame=false{(AccountManager.IsTeleport ? "&isTeleport=true" : "")}";

                string LaunchUri = $"roblox-player:1+launchmode:play+gameinfo:{Ticket}+launchtime:{LaunchTime}+placelauncherurl:{HttpUtility.UrlEncode(PlaceLauncherUrl)}+browsertrackerid:{BrowserTrackerID}+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

                // The legacy flag form, kept behind the debug toggle. It used to build a ProcessStartInfo and then
                // never start it — dead since it was written. It launches for real now, and carries -b so the
                // tracker-based features (window position, priority, auto-close) can still find the client.
                IReadOnlyList<string> Arguments = AccountManager.UseOldJoin
                    ? new[] { "--app", "-t", Ticket, "-j", PlaceLauncherUrl, "-b", BrowserTrackerID }
                    : new[] { LaunchUri };

                // Starting RobloxPlayerBeta.exe ourselves is the only way to hand this one client its own proxy
                // environment, put it on its own desktop, or learn its PID. It is also indistinguishable from the
                // protocol launch as far as Roblox is concerned: roblox-player: is registered as
                // `RobloxPlayerBeta.exe "%1"`, so the resulting command line is identical.
                bool Direct = AccountProxies.DirectLaunch || AccountManager.UseOldJoin || Proxy != null
                    || !string.IsNullOrEmpty(AccountProxies.TitleTemplate) || !string.IsNullOrEmpty(AccountProxies.DesktopName);

                if (Direct)
                {
                    SlotProxy Slot = AccountProxies.StartSlot(this, Proxy);
                    string ProxyUrl = Slot != null ? Slot.Address : Proxy?.ToEnvUrl();

                    string LaunchError = null;

                    Process Client = await Task.Run(() =>
                    {
                        Process Started = ClientLauncher.Launch(Arguments, ProxyUrl, AccountProxies.DesktopName, out string Error);

                        LaunchError = Error;

                        return Started;
                    });

                    if (Client == null)
                    {
                        Slot?.Dispose();

                        Shell.CancelLaunching();
                        Shell.LaunchFinished();

                        return $"ERROR: Failed to launch Roblox! {LaunchError}";
                    }

                    AccountProxies.BindToProcess(Slot, Client);

                    // Where this account was sent, for the supervisor that may have to send it back.
                    Relauncher.Remember(this, PlaceID, JobID);

                    string Title = ClientWindows.FormatTitle(AccountProxies.TitleTemplate, this, PlaceID, Client.Id);

                    if (!string.IsNullOrEmpty(Title)) ClientWindows.Track(Client.Id, Title);

                    Program.Logger.Info($"[Launch] {Username} -> pid {Client.Id}{(Proxy != null ? $" via {Proxy}{(Slot != null ? " (identity hosts only)" : string.Empty)}" : string.Empty)}");

                    Shell.LaunchFinished();

                    // This client writes its own log, but under the tracker id this account has used for every
                    // launch it ever made — so what the detector read for the previous session has to go, or its
                    // verdicts from here on are a replay of that one.
                    StuckDetector.NoteLaunch(BrowserTrackerID);

                    _ = Task.Run(AdjustWindowPosition);
                    _ = ResourceManager.OnLaunched(this); // per-instance priority/affinity/trim for the new client
                    _ = ClientLogWatcher.Watch(this, Client, PlaceID);

                    return "Success";
                }

                await Task.Run(() => // prevents roblox launcher hanging our main process
                {
                    try
                    {
                        // UseShellExecute is load-bearing here: FileName is a protocol URI rather than a file, and
                        // on .NET (Core) the default is false — CreateProcess then fails on the URI and every join
                        // ends in the catch below with "Try re-installing Roblox".
                        Process Launcher = Process.Start(new ProcessStartInfo(LaunchUri) { UseShellExecute = true });

                        // Deliberately not WaitForExit: roblox-player: now resolves straight to RobloxPlayerBeta.exe,
                        // so waiting would block until the player quits — the whole batch would stall on it.
                        Launcher?.Dispose();

                        Shell.LaunchFinished();

                        // Same reason as the direct branch: the tracker id outlives the session, the log does not.
                        StuckDetector.NoteLaunch(BrowserTrackerID);

                        _ = Task.Run(AdjustWindowPosition);
                        _ = ResourceManager.OnLaunched(this); // per-instance priority/affinity/trim for the new client
                    }
                    catch (Exception x)
                    {
                        Shell.Error($"Failed to launch Roblox! Try re-installing Roblox.\n\n{x.Message}{x.StackTrace}", "SkrilyaAccountManager");
                        Shell.CancelLaunching();
                        Shell.LaunchFinished();
                    }
                });

                return "Success";
            }
            else
                // Naming the cause matters here: a challenge, a rate limit and a dead cookie all arrive as
                // "no ticket", and each needs a different thing from the user.
                return LastAuthFailure == "CAPTCHA"
                    ? "ERROR: CAPTCHA — Roblox is challenging this login. The cookie is fine; the challenge has to be cleared before this account can launch."
                    : LastAuthFailure == "RATE LIMITED"
                        ? "ERROR: RATE LIMITED — too many launches from this IP. Give it a few minutes, or give this account its own proxy."
                        : "ERROR: Invalid Authentication Ticket, re-add the account or try again\n(Failed to get Authentication Ticket, Roblox has probably signed you out)";
            }
            finally { LaunchLock.Release(); }
        }

        /// <summary>
        /// Starts this account inside a running Android emulator. This shares LaunchLock with JoinServer so a PC
        /// launch and an Android cookie swap cannot race and kick each other as a duplicate session.
        /// </summary>
        public async Task<string> JoinServerAndroid(long PlaceID, string Serial, long? FollowUserId = null)
        {
            if (!LaunchLock.Wait(0))
                return "ERROR: A launch for this account is already in progress. Please wait a moment.";

            try
            {
                if (PlaceID <= 0) return "ERROR: A valid Android place id is required.";

                LastAppLaunch = DateTime.Now;
                LastUse = DateTime.Now;

                AndroidLaunchResult Result = await AndroidRuntime.LaunchAsync(this, PlaceID, Serial, FollowUserId);

                Program.Logger.Info($"[Android] {Username} -> {Result.Serial} place {PlaceID}" +
                    (FollowUserId.HasValue ? $" user {FollowUserId.Value}" : string.Empty));

                return "Success";
            }
            catch (Exception Ex)
            {
                Program.Logger.Error($"[Android] launch failed for {Username}: {Ex.Message}");
                return $"ERROR: Android launch failed: {Ex.Message}";
            }
            finally { LaunchLock.Release(); }
        }

        public async void AdjustWindowPosition()
        {
            if (!RobloxWatcher.RememberWindowPositions)
                return;

            if (!(int.TryParse(GetField("Window_Position_X"), out int PosX) && int.TryParse(GetField("Window_Position_Y"), out int PosY) && int.TryParse(GetField("Window_Width"), out int Width) && int.TryParse(GetField("Window_Height"), out int Height)))
                return;

            bool Found = false;
            DateTime Ends = DateTime.Now.AddSeconds(45);

            while (true)
            {
                await Task.Delay(350);

                foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta").Reverse())
                {
                    if (process.MainWindowHandle == IntPtr.Zero) continue;

                    string CommandLine = process.GetCommandLine();

                    var TrackerMatch = TrackerRegex.Match(CommandLine);
                    string TrackerID = TrackerMatch.Success ? TrackerMatch.Groups[1].Value : string.Empty;

                    if (TrackerID != BrowserTrackerID) continue;

                    Found = true;

                    MoveWindow(process.MainWindowHandle, PosX, PosY, Width, Height, true);

                    break;
                }

                if (Found) break;

                if (DateTime.Now > Ends) break;
            }
        }

        public string SetServer(long PlaceID, string JobID, out bool Successful)
        {
            Successful = false;

            if (!GetCSRFToken(out string Token)) return $"ERROR: Account Session Expired, re-add the account or try again. (Invalid X-CSRF-Token)\n{Token}";

            if (string.IsNullOrEmpty(Token))
                return "ERROR: Account Session Expired, re-add the account or try again. (Invalid X-CSRF-Token)";

            RestRequest request = MakeRequest("v1/join-game-instance", Method.Post).AddHeader("Content-Type", "application/json").AddJsonBody(new { gameId = JobID, placeId = PlaceID });

            RestResponse response = AccountManager.GameJoinClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                Successful = true;
                return Regex.IsMatch(response.Content, "\"joinScriptUrl\":[%s+]?null") ? response.Content : "Success";
            }
            else
                return $"Failed {response.StatusCode}: {response.Content} {response.ErrorMessage}";
        }

        public bool SendFriendRequest(string Username)
        {
            if (!AccountManager.GetUserID(Username, out long UserId, out _)) return false;
            if (!GetCSRFToken(out string Token)) return false;

            RestRequest friendRequest = MakeRequest($"/v1/users/{UserId}/request-friendship", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddHeader("Content-Type", "application/json");

            RestResponse friendResponse = AccountManager.FriendsClient.Execute(friendRequest);

            return friendResponse.IsSuccessful && friendResponse.StatusCode == HttpStatusCode.OK;
        }

        public void SetDisplayName(string DisplayName)
        {
            if (!GetCSRFToken(out string Token)) return;

            RestRequest dpRequest = MakeRequest($"/v1/users/{UserID}/display-names", Method.Patch).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(new { newDisplayName = DisplayName });

            RestResponse dpResponse = AccountManager.UsersClient.Execute(dpRequest);

            if (dpResponse.StatusCode != HttpStatusCode.OK)
                throw new Exception(JObject.Parse(dpResponse.Content)?["errors"]?[0]?["message"].Value<string>() ?? $"Something went wrong\n{dpResponse.StatusCode}: {dpResponse.Content}");
        }

        public void SetAvatar(string AvatarJSONData)
        {
            if (string.IsNullOrEmpty(AvatarJSONData)) return;
            if (!AvatarJSONData.TryParseJson(out JObject Avatar)) return;
            if (Avatar == null) return;
            if (!GetCSRFToken(out string Token)) return;

            RestRequest request;

            if (Avatar.ContainsKey("playerAvatarType"))
            {
                request = MakeRequest("v1/avatar/set-player-avatar-type", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(new { playerAvatarType = Avatar["playerAvatarType"].Value<string>() });

                AccountManager.AvatarClient.Execute(request);
            }

            JToken ScaleObject = Avatar.ContainsKey("scales") ? Avatar["scales"] : (Avatar.ContainsKey("scale") ? Avatar["scale"] : null);

            if (ScaleObject != null)
            {
                request = MakeRequest("v1/avatar/set-scales", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(ScaleObject.ToString());

                AccountManager.AvatarClient.Execute(request);
            }

            if (Avatar.ContainsKey("bodyColors"))
            {
                request = MakeRequest("v1/avatar/set-body-colors", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(Avatar["bodyColors"].ToString());

                AccountManager.AvatarClient.Execute(request);
            }

            if (Avatar.ContainsKey("assets"))
            {
                request = MakeRequest("v2/avatar/set-wearing-assets", Method.Post).AddHeader("X-CSRF-TOKEN", Token).AddJsonBody($"{{\"assets\":{Avatar["assets"]}}}");

                RestResponse Response = AccountManager.AvatarClient.Execute(request);

                if (Response.IsSuccessful)
                {
                    var ResponseJson = JObject.Parse(Response.Content);

                    if (ResponseJson.ContainsKey("invalidAssetIds"))
                        Shell.ShowMissingAssets(this, ResponseJson["invalidAssetIds"].Select(asset => asset.Value<long>()).ToArray());
                }
            }
        }

        public async Task<bool> QuickLogIn(string Code)
        {
            if (string.IsNullOrEmpty(Code) || Code.Length != 6) return false;
            if (!GetCSRFToken(out string Token)) return false;

            using var API = new RestClient("https://apis.roblox.com/");
            var Response = await API.PostAsync(MakeRequest("auth-token-service/v1/login/enterCode").AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(new { code = Code }));

            if (Response.IsSuccessful && Response.Content.TryParseJson(out dynamic Info))
                if (Shell.Confirm("Quick Log In", "Please confirm you are logging in with this device", $"Device: {Info?.deviceInfo ?? "Unknown"}\nLocation: {Info?.location ?? "Unknown"}"))
                    return (await API.PostAsync(MakeRequest("auth-token-service/v1/login/validateCode").AddHeader("X-CSRF-TOKEN", Token).AddJsonBody(new { code = Code }))).IsSuccessful;

            return false;
        }

        /// <summary>
        /// Buys a zero-price catalog item. Prefers the collectible path: Roblox migrated most free catalog items
        /// (including 2017-era classics) onto marketplace-sales, and the classic economy endpoint rejects anything
        /// that carries a collectible id.
        /// </summary>
        internal async Task<PurchaseResult> PurchaseFreeAsync(CatalogItem Item, string Csrf, System.Threading.CancellationToken Token)
        {
            if (Item == null) return PurchaseResult.NotPurchasable;

            // Never let a non-zero price through, whatever the caller believes: this method is the last gate before
            // spending Robux, and a stale catalog entry is the exact way that would happen by accident.
            if (Item.Price != 0) return PurchaseResult.PriceChanged;

            if (Item.HasCollectiblePath)
                return await PurchaseCollectible(Item, Csrf, Token);

            if (Item.ProductId > 0)
                return await PurchaseClassic(Item, Csrf, Token);

            return PurchaseResult.NotPurchasable;
        }

        private async Task<PurchaseResult> PurchaseCollectible(CatalogItem Item, string Csrf, System.Threading.CancellationToken Token)
        {
            RestRequest Request = new RestRequest($"marketplace-sales/v1/item/{Item.CollectibleItemId}/purchase-item", Method.Post)
                .AddCookie(".ROBLOSECURITY", SecurityToken, "/", ".roblox.com")
                .AddHeader("X-CSRF-TOKEN", Csrf)
                .AddHeader("Content-Type", "application/json")
                .AddHeader("Origin", "https://www.roblox.com")
                .AddHeader("Referer", "https://www.roblox.com/catalog")
                .AddJsonBody(new
                {
                    collectibleItemId = Item.CollectibleItemId,
                    collectibleProductId = Item.CollectibleProductId,
                    expectedCurrency = 1,
                    expectedPrice = 0,
                    expectedPurchaserId = UserID,
                    expectedPurchaserType = "User",
                    expectedSellerId = Item.CreatorTargetId,
                    expectedSellerType = Item.CreatorType,
                    // Roblox dedupes retries on this key. A fresh one per attempt is correct here: a retry after a
                    // 429 is a genuinely new attempt, and reusing the key would make it a silent no-op.
                    idempotencyKey = Guid.NewGuid().ToString(),
                });

            RestResponse Response = await AccountManager.ApisClient.ExecuteAsync(Request, Token);

            return Interpret(Response, Item, "collectible");
        }

        private async Task<PurchaseResult> PurchaseClassic(CatalogItem Item, string Csrf, System.Threading.CancellationToken Token)
        {
            RestRequest Request = new RestRequest($"v1/purchases/products/{Item.ProductId}", Method.Post)
                .AddCookie(".ROBLOSECURITY", SecurityToken, "/", ".roblox.com")
                .AddHeader("X-CSRF-TOKEN", Csrf)
                .AddHeader("Content-Type", "application/json")
                .AddHeader("Origin", "https://www.roblox.com")
                .AddHeader("Referer", "https://www.roblox.com/catalog")
                .AddJsonBody(new
                {
                    expectedCurrency = 1,
                    expectedPrice = 0,
                    expectedSellerId = Item.CreatorTargetId,
                });

            RestResponse Response = await AccountManager.EconClient.ExecuteAsync(Request, Token);

            return Interpret(Response, Item, "classic");
        }

        /// <summary>
        /// Maps a purchase response onto <see cref="PurchaseResult"/>. Both endpoints answer 200 for outcomes that
        /// are not a purchase ("already owned", "sold out"), so the body decides, not the status code.
        /// Anything unrecognised is logged verbatim — that log is how an API shape change gets noticed.
        /// </summary>
        private PurchaseResult Interpret(RestResponse Response, CatalogItem Item, string Path)
        {
            HarvestRotatedCookie(Response);

            if (Response.StatusCode == HttpStatusCode.TooManyRequests) return PurchaseResult.RateLimited;
            if (Response.StatusCode == HttpStatusCode.Unauthorized || Response.StatusCode == HttpStatusCode.Forbidden) return PurchaseResult.Unauthorized;

            string Content = Response.Content ?? string.Empty;

            if (Content.TryParseJson(out JObject Body) && Body != null)
            {
                if (Body["purchased"]?.Value<bool>() == true) return PurchaseResult.Ok;

                // Field order is load-bearing. The collectible endpoint puts a useless generic sentence in
                // "purchaseResult" ("Purchase transaction is failed.") for EVERY refusal and the actual machine
                // readable cause in "errorMessage". Reading purchaseResult first made every refusal look alike.
                // "reason" is the classic economy endpoint's equivalent and is specific there.
                string Specific = Body["errorMessage"]?.Value<string>() ?? string.Empty;
                string Generic = Body["purchaseResult"]?.Value<string>() ?? Body["reason"]?.Value<string>() ?? string.Empty;

                string Reason = Specific.Length > 0 ? Specific : Generic;

                // QuantityLimitExceeded is how a free item reports "you already have it": these carry a per-user
                // limit of one, so hitting the limit and owning it are the same state.
                if (Reason.IndexOf("QuantityLimit", StringComparison.OrdinalIgnoreCase) >= 0) return PurchaseResult.AlreadyOwned;
                if (Reason.IndexOf("AlreadyOwn", StringComparison.OrdinalIgnoreCase) >= 0) return PurchaseResult.AlreadyOwned;
                if (Reason.IndexOf("SoldOut", StringComparison.OrdinalIgnoreCase) >= 0 || Reason.IndexOf("OutOfStock", StringComparison.OrdinalIgnoreCase) >= 0) return PurchaseResult.SoldOut;
                if (Reason.IndexOf("Price", StringComparison.OrdinalIgnoreCase) >= 0) return PurchaseResult.PriceChanged;
                if (Reason.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0) return PurchaseResult.Ok;

                if (Reason.Length > 0)
                {
                    // The collectible endpoint answers a refused purchase with the generic "Purchase transaction is
                    // failed." for every cause, including "you already own it" - so the body is logged whole rather
                    // than just the reason. Without it a systemic refusal is indistinguishable from a run where
                    // everything happened to be owned already.
                    Program.Logger.Info($"[FreeItems] {Username} / {Item} [{Path}]: not purchased, reason '{Reason}' | body {Content}");

                    return PurchaseResult.NotPurchasable;
                }
            }

            Program.Logger.Warn($"[FreeItems] {Username} / {Item} [{Path}]: unrecognised response [{(int)Response.StatusCode}] {Content}");

            return PurchaseResult.Failed;
        }

        /// <summary>
        /// Adds a game to this account's favourites. Checks the current state first: re-favouriting is a no-op the
        /// API still charges against the rate limit, and the warmer runs on a timer where that adds up.
        /// </summary>
        internal async Task<WarmResult> FavoriteGameAsync(WarmGame Game, string Csrf, System.Threading.CancellationToken Token)
        {
            if (Game == null || Game.UniverseId <= 0) return WarmResult.Failed;

            RestResponse Current = await AccountManager.GamesClient.ExecuteAsync(MakeRequest($"v1/games/{Game.UniverseId}/favorites"), Token);

            if (Current.StatusCode == HttpStatusCode.TooManyRequests) return WarmResult.RateLimited;

            if (Current.IsSuccessful && Current.Content.TryParseJson(out JObject State) && State?["isFavorited"]?.Value<bool>() == true)
                return WarmResult.AlreadyDone;

            RestRequest Request = MakeRequest($"v1/games/{Game.UniverseId}/favorites", Method.Post)
                .AddHeader("X-CSRF-TOKEN", Csrf)
                .AddHeader("Content-Type", "application/json")
                .AddHeader("Origin", "https://www.roblox.com")
                .AddHeader("Referer", $"https://www.roblox.com/games/{Game.RootPlaceId}/")
                .AddJsonBody(new { isFavorited = true });

            RestResponse Response = await AccountManager.GamesClient.ExecuteAsync(Request, Token);

            HarvestRotatedCookie(Response);

            if (Response.StatusCode == HttpStatusCode.TooManyRequests) return WarmResult.RateLimited;
            if (Response.StatusCode == HttpStatusCode.Unauthorized || Response.StatusCode == HttpStatusCode.Forbidden) return WarmResult.Unauthorized;

            if (Response.IsSuccessful) return WarmResult.Ok;

            Program.Logger.Warn($"[Warmer] {Username} / {Game}: favorite failed [{(int)Response.StatusCode}] {Response.Content}");

            return WarmResult.Failed;
        }

        public string GetField(string Name) => Fields.ContainsKey(Name) ? Fields[Name] : string.Empty;
        public void SetField(string Name, string Value) { Fields[Name] = Value; AccountManager.SaveAccounts(); }
        public void RemoveField(string Name) { Fields.TryRemove(Name, out _); AccountManager.SaveAccounts(); }
    }

    public class AccountJson
    {
        public long UserId { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string UserEmail { get; set; }
        public bool IsEmailVerified { get; set; }
        public int AgeBracket { get; set; }
        public bool UserAbove13 { get; set; }
    }
}

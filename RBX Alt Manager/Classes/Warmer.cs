using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    internal enum WarmResult
    {
        Ok,
        AlreadyDone,
        RateLimited,
        Unauthorized,
        Failed
    }

    internal class WarmGame
    {
        public long UniverseId;
        public long RootPlaceId;
        public string Name;
        public int PlayerCount;

        public override string ToString() => $"{Name} ({UniverseId})";
    }

    internal class WarmerSummary
    {
        public int Favorited;
        public int Skipped;
        public int Failed;
        public int AccountsProcessed;
        public int AccountsStoppedEarly;

        public override string ToString() =>
            $"Favorited: {Favorited}\nSkipped (already favorited): {Skipped}\nFailed: {Failed}\n\nAccounts processed: {AccountsProcessed}" +
            (AccountsStoppedEarly > 0 ? $"\nStopped early on rate limits: {AccountsStoppedEarly}" : "");
    }

    /// <summary>
    /// Builds ordinary activity history on accounts you own, through the official endpoints - currently favouriting
    /// games from the public charts.
    /// <para>
    /// Deliberately NOT a "make alts undetectable" feature: it injects nothing, spoofs nothing, and every request is
    /// one a signed-in user makes by clicking around the site. Its only claim is that an account with some history
    /// looks less like one registered five minutes ago.
    /// </para>
    /// </summary>
    internal static class Warmer
    {
        /// <summary>Shared across accounts and passes; the charts change slowly and re-fetching them per account is pure waste.</summary>
        private static List<WarmGame> CachedGames = new List<WarmGame>();
        private static DateTime GamesFetched = DateTime.MinValue;

        private static readonly Random Chooser = new Random();
        private static readonly object ChooserLock = new object();

        private static System.Timers.Timer Schedule;

        private static int ActionDelay => Math.Max(500, AccountManager.General?.Get<int>("WarmerDelay") ?? 2500);

        #region Discovery

        /// <summary>
        /// Pulls the public charts. One unauthenticated request returns every sort WITH its games inline, so there
        /// is no per-sort follow-up call. Cached for an hour: the charts move slowly and this runs on a timer.
        /// </summary>
        public static async Task<List<WarmGame>> DiscoverGamesAsync(CancellationToken Token)
        {
            if (CachedGames.Count > 0 && (DateTime.Now - GamesFetched).TotalHours < 1) return CachedGames;

            var Request = new RestRequest($"explore-api/v1/get-sorts?sessionId={Guid.NewGuid()}");

            RestResponse Response = await AccountManager.ApisClient.ExecuteAsync(Request, Token);

            if (!Response.IsSuccessful || !Response.Content.TryParseJson(out JObject Parsed)) return CachedGames;

            var Games = new Dictionary<long, WarmGame>();

            foreach (JToken Sort in Parsed["sorts"] as JArray ?? new JArray())
                foreach (JToken Game in Sort["games"] as JArray ?? new JArray())
                {
                    long UniverseId = Game["universeId"]?.Value<long>() ?? 0;

                    if (UniverseId <= 0 || Games.ContainsKey(UniverseId)) continue;

                    // Sponsored entries are ads, not organic chart placements - favouriting those is not what a
                    // player browsing the front page does.
                    if (Game["isSponsored"]?.Value<bool>() == true) continue;

                    Games[UniverseId] = new WarmGame
                    {
                        UniverseId = UniverseId,
                        RootPlaceId = Game["rootPlaceId"]?.Value<long>() ?? 0,
                        Name = Game["name"]?.Value<string>() ?? "?",
                        PlayerCount = Game["playerCount"]?.Value<int>() ?? 0,
                    };
                }

            if (Games.Count > 0)
            {
                CachedGames = Games.Values.ToList();
                GamesFetched = DateTime.Now;

                Program.Logger.Info($"[Warmer] charts: {CachedGames.Count} games");
            }

            return CachedGames;
        }

        /// <summary>Picks <paramref name="Count"/> distinct games at random so two accounts don't favourite an identical list.</summary>
        private static List<WarmGame> Pick(List<WarmGame> From, int Count)
        {
            lock (ChooserLock)
                return From.OrderBy(_ => Chooser.Next()).Take(Math.Min(Count, From.Count)).ToList();
        }

        #endregion

        #region Execution

        public static async Task<WarmerSummary> RunAsync(IList<Account> Accounts, List<WarmGame> Games, int PerAccount, IProgress<string> Progress, CancellationToken Token)
        {
            var Summary = new WarmerSummary();

            if (Games.Count == 0) return Summary;

            int MaxConcurrent = Math.Max(1, AccountManager.General?.Get<int>("WarmerMaxConcurrentAccounts") ?? 2);

            using (var Gate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent))
            {
                var Running = Accounts.Select(async Acc =>
                {
                    await Gate.WaitAsync(Token);

                    try { await WarmAccount(Acc, Games, PerAccount, Summary, Progress, Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception Ex) { Program.Logger.Error($"[Warmer] {Acc.Username}: {Ex}"); }
                    finally { Gate.Release(); }
                }).ToList();

                await Task.WhenAll(Running);
            }

            return Summary;
        }

        private static async Task WarmAccount(Account Acc, List<WarmGame> Games, int PerAccount, WarmerSummary Summary, IProgress<string> Progress, CancellationToken Token)
        {
            if (!Acc.GetCSRFToken(out string Csrf))
            {
                Program.Logger.Warn($"[Warmer] {Acc.Username}: no CSRF token, skipping");

                return;
            }

            int ConsecutiveLimits = 0;

            foreach (WarmGame Game in Pick(Games, PerAccount))
            {
                if (Token.IsCancellationRequested) return;

                try
                {
                    WarmResult Result = await Acc.FavoriteGameAsync(Game, Csrf, Token);

                    if (Result == WarmResult.Unauthorized && Acc.GetCSRFToken(out string Fresh))
                    {
                        Csrf = Fresh;
                        Result = await Acc.FavoriteGameAsync(Game, Csrf, Token);
                    }

                    switch (Result)
                    {
                        case WarmResult.Ok:
                            ConsecutiveLimits = 0;
                            Interlocked.Increment(ref Summary.Favorited);
                            Progress?.Report($"{Acc.Username}: favorited {Game.Name}");
                            break;

                        case WarmResult.AlreadyDone:
                            ConsecutiveLimits = 0;
                            Interlocked.Increment(ref Summary.Skipped);
                            break;

                        case WarmResult.RateLimited:
                            ConsecutiveLimits++;

                            if (ConsecutiveLimits >= 3)
                            {
                                Program.Logger.Warn($"[Warmer] {Acc.Username}: rate limited 3x in a row, stopping this account");
                                Interlocked.Increment(ref Summary.AccountsStoppedEarly);

                                return;
                            }

                            await Task.Delay(TimeSpan.FromSeconds(5 * ConsecutiveLimits), Token);
                            break;

                        default:
                            ConsecutiveLimits = 0;
                            Interlocked.Increment(ref Summary.Failed);
                            break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception Ex)
                {
                    Interlocked.Increment(ref Summary.Failed);
                    Program.Logger.Error($"[Warmer] {Acc.Username} / {Game}: {Ex.Message}");
                }

                await Task.Delay(ActionDelay, Token);
            }

            Interlocked.Increment(ref Summary.AccountsProcessed);
        }

        #endregion

        #region Scheduler

        /// <summary>
        /// Arms the background warming pass if enabled. Same shape as AutoCookieRefresh: AutoReset=false with a
        /// re-arm in finally, so a pass that outruns its own interval can never overlap itself.
        /// </summary>
        public static void StartScheduler()
        {
            if (Schedule != null || AccountManager.General?.Get<bool>("WarmerEnabled") != true) return;

            int Minutes = Math.Max(15, AccountManager.General.Get<int>("WarmerIntervalMinutes"));

            Schedule = new System.Timers.Timer(TimeSpan.FromMinutes(Minutes).TotalMilliseconds) { AutoReset = false, Enabled = true };

            Schedule.Elapsed += async (s, e) =>
            {
                try { await RunScheduledPass(); }
                catch (Exception Ex) { Program.Logger.Error($"[Warmer] scheduled pass failed: {Ex.Message}"); }
                finally { try { Schedule.Start(); } catch { } }
            };

            Program.Logger.Info($"[Warmer] scheduler armed, every {Minutes} minutes");
        }

        private static async Task RunScheduledPass()
        {
            string Group = AccountManager.General.Get<string>("WarmerGroup") ?? string.Empty;

            var Targets = AccountManager.GetAccountsSnapshot()
                .Where(a => a.Valid && (string.IsNullOrWhiteSpace(Group) || string.Equals(a.Group, Group, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (Targets.Count == 0) return;

            using (var Cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
            {
                List<WarmGame> Games = await DiscoverGamesAsync(Cancel.Token);

                int PerAccount = Math.Max(1, AccountManager.General.Get<int>("WarmerFavoritesPerPass"));

                WarmerSummary Summary = await RunAsync(Targets, Games, PerAccount, null, Cancel.Token);

                Program.Logger.Info($"[Warmer] scheduled pass over {Targets.Count} account(s): favorited {Summary.Favorited}, skipped {Summary.Skipped}, failed {Summary.Failed}");
            }
        }

        #endregion
    }
}

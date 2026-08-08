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
    internal enum PurchaseResult
    {
        Ok,
        AlreadyOwned,
        SoldOut,
        NotPurchasable,
        PriceChanged,
        RateLimited,
        Unauthorized,
        Failed
    }

    internal class CatalogItem
    {
        public long Id;
        public string Name;

        /// <summary>Classic economy path. 0 when the item only exists as a collectible.</summary>
        public long ProductId;

        /// <summary>Collectible (marketplace-sales) path. Most free catalog items have one now, including old classics.</summary>
        public string CollectibleItemId;

        /// <summary>Second half of the collectible purchase body; resolved from marketplace-items, not from the catalog.</summary>
        public string CollectibleProductId;

        public long CreatorTargetId;

        /// <summary>"User" or "Group" - the purchase body rejects a mismatched seller type.</summary>
        public string CreatorType = "User";

        public long Price;
        public string PriceStatus;
        public string SaleLocationType;

        /// <summary>Non-zero means a capped stock item; free UGC with a cap sells out within seconds of release.</summary>
        public long UnitsAvailableForConsumption;

        public bool HasCollectiblePath => !string.IsNullOrEmpty(CollectibleItemId) && !string.IsNullOrEmpty(CollectibleProductId);

        public override string ToString() => $"{Name} ({Id})";
    }

    internal class FreeItemsSummary
    {
        public int Collected;
        public int Skipped;
        public int Failed;
        public int AccountsProcessed;
        public int AccountsStoppedEarly;

        public override string ToString() =>
            $"Collected: {Collected}\nSkipped (owned / sold out / not purchasable): {Skipped}\nFailed: {Failed}\n\nAccounts processed: {AccountsProcessed}" +
            (AccountsStoppedEarly > 0 ? $"\nStopped early on rate limits: {AccountsStoppedEarly}" : "");
    }

    /// <summary>
    /// Bulk-collects zero-price catalog items onto selected accounts.
    /// <para>
    /// Discovery (search -> details -> collectible product ids) is account independent, so it runs ONCE and the
    /// resulting list is shared by every account. Only the ownership check and the purchase itself are per account.
    /// That matters because the catalog endpoints rate limit per IP far more aggressively than the purchase one.
    /// </para>
    /// </summary>
    internal static class FreeItems
    {
        /// <summary>
        /// CSRF for the unauthenticated catalog/marketplace reads. Roblox hands one out on the 403 bounce of any
        /// POST; it is not tied to an account, so a single value serves the whole discovery pass.
        /// </summary>
        private static string SharedToken = string.Empty;

        // Null-guarded: these are read from background tasks, and General is only assigned once the main form has
        // run its settings block. A missing section must fall back to the safe delay, not throw mid-run.
        private static int SearchDelay => Math.Max(250, AccountManager.General?.Get<int>("FreeItemsSearchDelay") ?? 1200);
        private static int PurchaseDelay => Math.Max(250, AccountManager.General?.Get<int>("FreeItemsDelay") ?? 1500);

        #region Discovery

        /// <summary>
        /// Finds up to <paramref name="MaxItems"/> purchasable zero-price assets and fills in everything both
        /// purchase paths need. Returns an empty list rather than throwing when Roblox is uncooperative.
        /// </summary>
        public static async Task<List<CatalogItem>> DiscoverAsync(int MaxItems, IProgress<string> Progress, CancellationToken Token)
        {
            var Found = new List<(long Id, string ItemType)>();
            string Cursor = string.Empty;

            Progress?.Report("Searching the catalog for free items...");

            while (Found.Count < MaxItems && !Token.IsCancellationRequested)
            {
                string Resource = $"v1/search/items?category=All&maxPrice=0&salesTypeFilter=1&limit=30{(string.IsNullOrEmpty(Cursor) ? "" : "&cursor=" + Uri.EscapeDataString(Cursor))}";

                RestResponse Response = await ExecuteWithBackoff(AccountManager.CatalogClient, new RestRequest(Resource), Token);

                if (Response == null || !Response.IsSuccessful || !Response.Content.TryParseJson(out JObject Page)) break;

                JArray Data = Page["data"] as JArray;

                if (Data == null || Data.Count == 0) break;

                foreach (JToken Entry in Data)
                {
                    // Bundles buy through a different endpoint and often bundle non-free parts. Assets only for now.
                    if (Entry["itemType"]?.Value<string>() != "Asset") continue;

                    long Id = Entry["id"]?.Value<long>() ?? 0;

                    if (Id > 0) Found.Add((Id, "Asset"));
                }

                Cursor = Page["nextPageCursor"]?.Value<string>() ?? string.Empty;

                if (string.IsNullOrEmpty(Cursor)) break;

                await Task.Delay(SearchDelay, Token);
            }

            if (Found.Count == 0) return new List<CatalogItem>();

            Progress?.Report($"Found {Found.Count} candidates, resolving details...");

            var Items = new List<CatalogItem>();

            // 30 per call rather than the documented 100: the details endpoint 429s far earlier than its own limit
            // suggests, and a rejected batch costs more than an extra round trip.
            foreach (var Chunk in Found.Take(MaxItems).Chunk(30))
            {
                if (Token.IsCancellationRequested) break;

                Items.AddRange(await ResolveDetails(Chunk, Token));

                await Task.Delay(SearchDelay, Token);
            }

            // Only the collectible path needs a second lookup, and only for items that actually carry an id.
            var Collectibles = Items.Where(i => !string.IsNullOrEmpty(i.CollectibleItemId)).ToList();

            if (Collectibles.Count > 0)
            {
                Progress?.Report($"Resolving product ids for {Collectibles.Count} collectibles...");

                foreach (var Chunk in Collectibles.Chunk(30))
                {
                    if (Token.IsCancellationRequested) break;

                    await ResolveCollectibleProducts(Chunk, Token);

                    await Task.Delay(SearchDelay, Token);
                }
            }

            // An item with neither a usable collectible pair nor a classic product id cannot be bought at all.
            var Usable = Items.Where(i => i.Price == 0 && (i.HasCollectiblePath || i.ProductId > 0)).ToList();

            Program.Logger.Info($"[FreeItems] discovery: {Found.Count} candidates -> {Items.Count} detailed -> {Usable.Count} purchasable");

            return Usable;
        }

        private static async Task<List<CatalogItem>> ResolveDetails(IEnumerable<(long Id, string ItemType)> Chunk, CancellationToken Token)
        {
            var Result = new List<CatalogItem>();

            var Body = new { items = Chunk.Select(c => new { itemType = c.ItemType, id = c.Id }).ToArray() };

            RestResponse Response = await CsrfPost(AccountManager.CatalogClient, "v1/catalog/items/details", Body, null, Token);

            if (Response == null || !Response.IsSuccessful || !Response.Content.TryParseJson(out JObject Parsed)) return Result;

            foreach (JToken Entry in Parsed["data"] as JArray ?? new JArray())
            {
                long Price = Entry["price"]?.Value<long>() ?? -1;

                // priceStatus carries "Free" / "Off Sale" / "No Resellers"; a null price with a non-Free status is
                // not a zero-price item, it is an item with no price at all. Only take genuine zeroes.
                if (Price != 0) continue;

                Result.Add(new CatalogItem
                {
                    Id = Entry["id"]?.Value<long>() ?? 0,
                    Name = Entry["name"]?.Value<string>() ?? "?",
                    ProductId = Entry["productId"]?.Value<long>() ?? 0,
                    CollectibleItemId = Entry["collectibleItemId"]?.Value<string>(),
                    CreatorTargetId = Entry["creatorTargetId"]?.Value<long>() ?? 0,
                    CreatorType = Entry["creatorType"]?.Value<string>() ?? "User",
                    Price = Price,
                    PriceStatus = Entry["priceStatus"]?.Value<string>(),
                    SaleLocationType = Entry["saleLocationType"]?.Value<string>(),
                    UnitsAvailableForConsumption = Entry["unitsAvailableForConsumption"]?.Value<long>() ?? 0,
                });
            }

            return Result;
        }

        private static async Task ResolveCollectibleProducts(IEnumerable<CatalogItem> Chunk, CancellationToken Token)
        {
            var List = Chunk.ToList();

            var Body = new { itemIds = List.Select(i => i.CollectibleItemId).ToArray() };

            RestResponse Response = await CsrfPost(AccountManager.ApisClient, "marketplace-items/v1/items/details", Body, null, Token);

            // This endpoint answers with a bare array, not the usual { data: [...] } envelope.
            if (Response == null || !Response.IsSuccessful || !Response.Content.TryParseJson(out JArray Parsed)) return;

            foreach (JToken Entry in Parsed)
            {
                string Cid = Entry["collectibleItemId"]?.Value<string>();

                CatalogItem Item = List.FirstOrDefault(i => i.CollectibleItemId == Cid);

                if (Item == null) continue;

                Item.CollectibleProductId = Entry["collectibleProductId"]?.Value<string>();

                // creatorId here is authoritative for the purchase body; the catalog's creatorTargetId can be a
                // group id while the sale is attributed to a user (and vice versa).
                long CreatorId = Entry["creatorId"]?.Value<long>() ?? 0;

                if (CreatorId > 0) Item.CreatorTargetId = CreatorId;

                string CreatorType = Entry["creatorType"]?.Value<string>();

                if (!string.IsNullOrEmpty(CreatorType)) Item.CreatorType = CreatorType;
            }
        }

        #endregion

        #region Execution

        /// <summary>
        /// Runs the collection over <paramref name="Accounts"/>. Concurrency across accounts is capped and each
        /// account is walked serially, so the worst case request rate stays predictable.
        /// </summary>
        public static async Task<FreeItemsSummary> RunAsync(IList<Account> Accounts, List<CatalogItem> Items, IProgress<string> Progress, CancellationToken Token)
        {
            var Summary = new FreeItemsSummary();

            int MaxConcurrent = Math.Max(1, AccountManager.General.Get<int>("FreeItemsMaxConcurrentAccounts"));

            using (var Gate = new SemaphoreSlim(MaxConcurrent, MaxConcurrent))
            {
                var Running = Accounts.Select(async Acc =>
                {
                    await Gate.WaitAsync(Token);

                    try { await CollectForAccount(Acc, Items, Summary, Progress, Token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception Ex) { Program.Logger.Error($"[FreeItems] {Acc.Username}: {Ex}"); }
                    finally { Gate.Release(); }
                }).ToList();

                await Task.WhenAll(Running);
            }

            return Summary;
        }

        private static async Task CollectForAccount(Account Acc, List<CatalogItem> Items, FreeItemsSummary Summary, IProgress<string> Progress, CancellationToken Token)
        {
            if (!Acc.GetCSRFToken(out string Csrf))
            {
                Program.Logger.Warn($"[FreeItems] {Acc.Username}: could not get a CSRF token, skipping");
                Progress?.Report($"{Acc.Username}: invalid session, skipped");

                return;
            }

            bool SkipOwned = AccountManager.General.Get<bool>("FreeItemsSkipOwned");

            int Collected = 0, ConsecutiveLimits = 0;

            foreach (CatalogItem Item in Items)
            {
                if (Token.IsCancellationRequested) return;

                try
                {
                    if (SkipOwned && await IsOwned(Acc, Item, Token))
                    {
                        Interlocked.Increment(ref Summary.Skipped);

                        await Task.Delay(PurchaseDelay, Token);

                        continue;
                    }

                    PurchaseResult Result = await Acc.PurchaseFreeAsync(Item, Csrf, Token);

                    // A stale token is cheap to fix and would otherwise fail every remaining item.
                    if (Result == PurchaseResult.Unauthorized && Acc.GetCSRFToken(out string Fresh))
                    {
                        Csrf = Fresh;
                        Result = await Acc.PurchaseFreeAsync(Item, Csrf, Token);
                    }

                    switch (Result)
                    {
                        case PurchaseResult.Ok:
                            Collected++;
                            ConsecutiveLimits = 0;
                            Interlocked.Increment(ref Summary.Collected);
                            Progress?.Report($"{Acc.Username}: got {Item.Name}");
                            break;

                        case PurchaseResult.AlreadyOwned:
                        case PurchaseResult.SoldOut:
                        case PurchaseResult.NotPurchasable:
                        case PurchaseResult.PriceChanged:
                            ConsecutiveLimits = 0;
                            Interlocked.Increment(ref Summary.Skipped);
                            break;

                        case PurchaseResult.RateLimited:
                            ConsecutiveLimits++;

                            // Give the bucket real time to refill; three in a row means this account is done for
                            // this run, and hammering on would only extend the limit for every other account too.
                            if (ConsecutiveLimits >= 3)
                            {
                                Program.Logger.Warn($"[FreeItems] {Acc.Username}: rate limited 3x in a row, stopping this account");
                                Progress?.Report($"{Acc.Username}: rate limited, stopped");
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
                    // One bad item must not end the run for this account.
                    Interlocked.Increment(ref Summary.Failed);
                    Program.Logger.Error($"[FreeItems] {Acc.Username} / {Item}: {Ex.Message}");
                }

                await Task.Delay(PurchaseDelay, Token);
            }

            Interlocked.Increment(ref Summary.AccountsProcessed);

            Program.Logger.Info($"[FreeItems] {Acc.Username}: collected {Collected}");
        }

        private static async Task<bool> IsOwned(Account Acc, CatalogItem Item, CancellationToken Token)
        {
            RestRequest Request = Acc.MakeRequest($"v1/users/{Acc.UserID}/items/Asset/{Item.Id}/is-owned");

            RestResponse Response = await AccountManager.InventoryClient.ExecuteAsync(Request, Token);

            // Anything other than a clean "true" is treated as not owned: the purchase call is authoritative and
            // will report AlreadyOwned itself, so a failed check costs one wasted request, not a wrong skip.
            return Response.IsSuccessful && Response.Content != null && Response.Content.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region Plumbing

        /// <summary>
        /// POSTs with the CSRF dance Roblox requires: an unarmed request comes back 403 carrying a token, which the
        /// retry then presents. The token is cached because it stays valid for the whole discovery pass.
        /// </summary>
        private static async Task<RestResponse> CsrfPost(RestClient Client, string Resource, object Body, Account Account, CancellationToken Token)
        {
            RestResponse Response = await Send(SharedToken);

            if (Response != null && Response.StatusCode == HttpStatusCode.Forbidden)
            {
                string Fresh = (string)Response.Headers?.FirstOrDefault(h => string.Equals(h.Name, "x-csrf-token", StringComparison.OrdinalIgnoreCase))?.Value;

                if (!string.IsNullOrEmpty(Fresh))
                {
                    SharedToken = Fresh;

                    await Task.Delay(200, Token);

                    Response = await Send(Fresh);
                }
            }

            return Response;

            async Task<RestResponse> Send(string Csrf)
            {
                RestRequest Request = Account != null ? Account.MakeRequest(Resource, Method.Post) : new RestRequest(Resource, Method.Post);

                // The bare content type is load-bearing here exactly as it is in GetAuthTicket: AddJsonBody alone
                // emits "application/json; charset=utf-8", which some of these endpoints answer with a 415.
                Request.AddHeader("Content-Type", "application/json")
                       .AddHeader("Origin", "https://www.roblox.com")
                       .AddHeader("Referer", "https://www.roblox.com/catalog")
                       .AddJsonBody(Body);

                if (!string.IsNullOrEmpty(Csrf)) Request.AddHeader("X-CSRF-TOKEN", Csrf);

                return await ExecuteWithBackoff(Client, Request, Token);
            }
        }

        /// <summary>Retries a request through 429s with a widening delay. Returns the last response, successful or not.</summary>
        private static async Task<RestResponse> ExecuteWithBackoff(RestClient Client, RestRequest Request, CancellationToken Token, int Attempts = 3)
        {
            RestResponse Response = null;

            for (int Attempt = 1; Attempt <= Attempts; Attempt++)
            {
                Token.ThrowIfCancellationRequested();

                Response = await Client.ExecuteAsync(Request, Token);

                if (Response.StatusCode != HttpStatusCode.TooManyRequests) return Response;

                if (Attempt < Attempts)
                {
                    Program.Logger.Warn($"[FreeItems] 429 on {Request.Resource}, backing off (attempt {Attempt}/{Attempts})");

                    await Task.Delay(TimeSpan.FromSeconds(4 * Attempt), Token);
                }
            }

            return Response;
        }

        #endregion
    }
}

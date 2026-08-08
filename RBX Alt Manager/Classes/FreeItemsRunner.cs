using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Runs a free-item collection on behalf of the HTML interface.
    ///
    /// FreeItems itself is a pair of long-running async calls; the old window drove them straight from a menu
    /// handler and reported by rewriting the menu item's text. A page cannot be driven that way: a bridge call
    /// must return quickly, and progress has to arrive as events. So this owns the run — one at a time, its
    /// cancellation, and the translation of IProgress&lt;string&gt; into messages the page subscribes to.
    /// </summary>
    internal static class FreeItemsRunner
    {
        private static CancellationTokenSource Cancellation;
        private static readonly object Lock = new object();

        /// <summary>What the last (or current) run has produced, so a page opened mid-run is not blank.</summary>
        private static readonly List<string> Transcript = new List<string>();

        private static string CurrentStage = string.Empty;
        private static int CurrentAccounts;

        public static bool Running
        {
            get { lock (Lock) return Cancellation != null; }
        }

        public static object State()
        {
            lock (Lock)
                return new
                {
                    running = Cancellation != null,
                    stage = CurrentStage,
                    accounts = CurrentAccounts,
                    lines = Transcript.ToArray()
                };
        }

        /// <summary>Starts a run. Returns immediately; everything after this arrives as freeitems.* events.</summary>
        public static object Start(IList<Account> Accounts)
        {
            if (Accounts == null || Accounts.Count == 0) throw new Exception("Tick the accounts to collect for on the Accounts tab first");

            lock (Lock)
            {
                if (Cancellation != null) throw new Exception("A collection is already running");

                Cancellation = new CancellationTokenSource();

                Transcript.Clear();
                CurrentAccounts = Accounts.Count;
                CurrentStage = "starting";
            }

            int MaxPerRun = Math.Max(1, AccountManager.General.Get<int>("FreeItemsMaxPerRun"));

            CancellationToken Token = Cancellation.Token;

            // Deliberately not awaited: the bridge call that started this returns now, and the page follows along
            // through the events below.
            _ = Task.Run(async () =>
            {
                try
                {
                    Say($"looking for up to {MaxPerRun} free items");

                    // Discovery is account independent, so it runs once and every account shares the result.
                    List<CatalogItem> Items = await FreeItems.DiscoverAsync(MaxPerRun, new Progress<string>(Say), Token);

                    if (Items.Count == 0)
                    {
                        Finish(null, "Roblox returned no purchasable free items — it may be rate limiting. Try again in a few minutes.");

                        return;
                    }

                    Say($"{Items.Count} items found, collecting on {Accounts.Count} account{(Accounts.Count == 1 ? "" : "s")}");

                    FreeItemsSummary Summary = await FreeItems.RunAsync(Accounts, Items, new Progress<string>(Say), Token);

                    Finish(Summary, null);
                }
                catch (OperationCanceledException)
                {
                    Program.Logger.Info("[FreeItems] run cancelled");

                    Finish(null, "Stopped.");
                }
                catch (Exception x)
                {
                    Program.Logger.Error($"[FreeItems] run failed: {x}");

                    Finish(null, x.Message);
                }
            }, Token);

            return new { started = true, accounts = Accounts.Count, maxPerRun = MaxPerRun };
        }

        public static object Stop()
        {
            lock (Lock)
            {
                if (Cancellation == null) return new { stopping = false };

                Cancellation.Cancel();
            }

            Say("stopping…");

            return new { stopping = true };
        }

        private static void Say(string Line)
        {
            if (string.IsNullOrWhiteSpace(Line)) return;

            lock (Lock)
            {
                CurrentStage = Line;

                Transcript.Add(Line);

                // A long run would otherwise grow this without limit; the page only shows the recent tail anyway.
                if (Transcript.Count > 300) Transcript.RemoveRange(0, Transcript.Count - 300);
            }

            Forms.WebShell.NotifyFreeItems("progress", new { line = Line });
        }

        private static void Finish(FreeItemsSummary Summary, string Error)
        {
            lock (Lock)
            {
                Cancellation?.Dispose();
                Cancellation = null;

                CurrentStage = Error ?? "done";
            }

            Forms.WebShell.NotifyFreeItems("done", new
            {
                error = Error,
                collected = Summary?.Collected ?? 0,
                skipped = Summary?.Skipped ?? 0,
                failed = Summary?.Failed ?? 0,
                accounts = Summary?.AccountsProcessed ?? 0,
                stoppedEarly = Summary?.AccountsStoppedEarly ?? 0
            });
        }
    }
}

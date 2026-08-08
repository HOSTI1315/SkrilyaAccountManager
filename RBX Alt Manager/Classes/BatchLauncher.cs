using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Launches a set of accounts the way the old window does — paced, cancellable, honouring each account's own
    /// saved destination — for callers that cannot drive a foreach themselves.
    ///
    /// The HTML interface used to loop over accounts.launch from the page, which meant no pacing at all: fifteen
    /// accounts asked Roblox for fifteen tickets within a second, from one address, which is exactly what
    /// "RATE LIMITED" is for. Worse, the settings screen offered AccountJoinDelay and AsyncJoin as if they
    /// applied to it.
    ///
    /// The one place this deliberately differs from the old window: the delay is per EXIT, not global. The point
    /// of the pause is not to ask Roblox for too many tickets from one address, and accounts on different proxies
    /// do not share an address — making them wait for each other buys nothing and multiplies the time a batch
    /// takes by the number of accounts. So accounts are grouped by the proxy they will use (all the direct ones
    /// share one queue), each queue is paced on its own, and the queues run side by side.
    /// </summary>
    internal static class BatchLauncher
    {
        private static CancellationTokenSource Cancellation;
        private static readonly object Lock = new object();

        private static int Started, Failed, Total;

        public static bool Running
        {
            get { lock (Lock) return Cancellation != null; }
        }

        public static object State()
        {
            lock (Lock)
                return new { running = Cancellation != null, started = Started, failed = Failed, total = Total };
        }

        /// <summary>
        /// Read defensively, the way AccountProxies reads its own switches. Both of these come from a settings
        /// screen whose number fields are plain text boxes, so "10s" reaches the ini intact — and Get&lt;int&gt; is a
        /// bare Convert.ChangeType, which throws on it. A bad delay must not be able to take the launcher down.
        /// </summary>
        private static int Delay =>
            int.TryParse(AccountManager.General.Get("AccountJoinDelay"), out int Seconds) ? Math.Max(0, Seconds) : 8;

        /// <summary>No pause at all: the next client starts as soon as the previous one's process is up.</summary>
        private static bool NoPacing =>
            bool.TryParse(AccountManager.General.Get("AsyncJoin"), out bool Value) && Value;

        /// <summary>
        /// Starts a batch and returns at once. Everything after this arrives as launch.progress / launch.batch.
        /// </summary>
        public static object Start(IList<Account> Accounts, long PlaceId, string JobId, bool JoinVip)
        {
            if (Accounts == null || Accounts.Count == 0) throw new Exception("No accounts were given");

            // Everything that can fail happens BEFORE the run is claimed. Claiming it first and then throwing
            // would leave the "a batch is running" flag set with no task behind it — and the only code that
            // clears the flag is the finally of that task, so batch launching would stay wedged until the app
            // was restarted, with the page's Join button stuck on "Stop".
            List<List<Account>> Queues = PlanQueues(Accounts);

            int Pause = NoPacing ? 0 : Delay;

            CancellationTokenSource Source = new CancellationTokenSource();

            lock (Lock)
            {
                if (Cancellation != null)
                {
                    Source.Dispose();

                    throw new Exception("A launch is already running — stop it first");
                }

                Cancellation = Source;

                Started = 0;
                Failed = 0;
                Total = Accounts.Count;
            }

            // From the local, not the field: a finishing batch may null and dispose the field at any moment.
            CancellationToken Token = Source.Token;

            try
            {
                Program.Logger.Info($"[Batch] {Accounts.Count} accounts over {Queues.Count} exit(s), {Pause}s between launches on each");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.WhenAll(Queues.Select(Queue => RunQueue(Queue, PlaceId, JobId, JoinVip, Pause, Token)));
                    }
                    catch (Exception x) { Program.Logger.Error($"[Batch] failed: {x}"); }
                    finally { Release(Source, Token.IsCancellationRequested); }
                });
            }
            catch
            {
                // Nothing is running, so the claim must not outlive this call.
                Release(Source, false);

                throw;
            }

            return new { started = true, accounts = Accounts.Count, exits = Queues.Count, delay = Pause };
        }

        /// <summary>
        /// Splits a batch into one queue per exit address: the pause exists so Roblox is not asked for a pile of
        /// tickets from ONE address at once, and accounts on different proxies do not share an address. Everything
        /// launching directly shares a single queue, because they do share one.
        ///
        /// Order within a queue follows the order the accounts were given, so a batch stays predictable.
        /// </summary>
        public static List<List<Account>> PlanQueues(IList<Account> Accounts) =>
            (Accounts ?? new List<Account>())
                .Where(account => account != null)
                .GroupBy(account => AccountProxies.For(account)?.Raw ?? string.Empty)
                .Select(Group => Group.ToList())
                .ToList();

        private static async Task RunQueue(List<Account> Queue, long PlaceId, string JobId, bool JoinVip, int Pause, CancellationToken Token)
        {
            for (int i = 0; i < Queue.Count; i++)
            {
                if (Token.IsCancellationRequested) return;

                Account account = Queue[i];

                // Where this account goes, if it was told to remember somewhere of its own. The old window does the
                // same, and skips it when following a user — there is no follow in a batch from the page.
                long Destination = PlaceId;
                string Job = JobId;

                bool Redirected = false;

                if (long.TryParse(account.GetField("SavedPlaceId"), out long Saved) && Saved > 0 && Saved != Destination)
                {
                    Destination = Saved;
                    Redirected = true;
                }

                string SavedJob = account.GetField("SavedJobId");

                if (!string.IsNullOrEmpty(SavedJob)) Job = SavedJob;

                // Say so when an account is not going where the launcher says: silently sending one account of a
                // batch somewhere else looks like a bug from the outside.
                Say(account.Username, "launching", Redirected ? $"its own place {Destination}" : null);

                try
                {
                    string Result = await account.JoinServer(Destination, Job ?? string.Empty, false, JoinVip);

                    if (Result != null && Result.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        Count(ref Failed);

                        Say(account.Username, "failed", Result.Substring(Result.IndexOf(':') + 1).Trim());
                    }
                    else
                    {
                        Count(ref Started);

                        Say(account.Username, "started");
                    }
                }
                catch (Exception x)
                {
                    Count(ref Failed);

                    Say(account.Username, "failed", x.Message);
                }

                // No pause after the last one — it would only delay the "batch finished" message.
                if (Pause > 0 && i < Queue.Count - 1)
                    try { await Task.Delay(Pause * 1000, Token); }
                    catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>
        /// Hands the run back and tells the page how it ended. Only releases the claim if it is still OUR claim,
        /// so a late finally from an abandoned run can never free a batch that started after it.
        /// </summary>
        private static void Release(CancellationTokenSource Source, bool Cancelled)
        {
            int done, failed, total;
            bool Mine;

            // The tally is read inside the same lock that releases the run: reading it afterwards leaves a gap in
            // which a new batch could reset the counters, and this message — about the batch that just ended —
            // would report the new one's empty numbers.
            lock (Lock)
            {
                done = Started;
                failed = Failed;
                total = Total;

                Mine = ReferenceEquals(Cancellation, Source);

                if (Mine) Cancellation = null;
            }

            Source.Dispose();

            if (Mine) Forms.WebShell.NotifyLaunch("batch", new { started = done, failed, total, cancelled = Cancelled });
        }

        private static void Count(ref int Field)
        {
            lock (Lock) Field++;
        }

        private static void Say(string Username, string State, string Detail = null)
        {
            int done, failed, total;

            lock (Lock) { done = Started; failed = Failed; total = Total; }

            Forms.WebShell.NotifyLaunch("progress", new { username = Username, state = State, detail = Detail, started = done, failed, total });
        }

        public static object Cancel()
        {
            lock (Lock)
            {
                if (Cancellation == null) return new { cancelling = false };

                Cancellation.Cancel();
            }

            Program.Logger.Info("[Batch] cancelled");

            return new { cancelling = true };
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Puts an account back where it was when its client dies, within a budget.
    ///
    /// The manager already has an auto-relaunch, but it lives in Nexus and decides "is this account still
    /// playing?" from either a Nexus ping — which needs an executor running the Nexus script in-game — or from
    /// Roblox presence, which lags. This one runs without any of that, because two better sources now exist:
    /// the launch watcher knows how a launch ended, and the stuck detector knows what a live client is doing.
    ///
    /// It deliberately does NOT retry everything. A challenge or a rate limit is not a transient crash, and
    /// retrying either makes the situation worse; those are reported and left alone.
    /// </summary>
    internal static class Relauncher
    {
        public static bool Enabled { get; private set; }
        public static bool OnCrash { get; private set; } = true;
        public static bool OnStuck { get; private set; }

        public static readonly LaunchBudget Budget = new LaunchBudget();

        /// <summary>Where each account was last sent, so it can be sent back to the same place.</summary>
        private static readonly ConcurrentDictionary<string, (long PlaceId, string JobId)> LastLaunch = new ConcurrentDictionary<string, (long, string)>();

        private static bool Subscribed;

        public const string PlaceField = "SavedPlaceId";
        public const string JobField = "SavedJobId";

        public static void LoadSettings()
        {
            Enabled = Bool("RelaunchEnabled", false);
            OnCrash = Bool("RelaunchOnCrash", true);
            OnStuck = Bool("RelaunchOnStuck", false);

            Budget.Cooldown = TimeSpan.FromSeconds(Int("RelaunchCooldownSeconds", 600, 30, 24 * 3600));
            Budget.MaxPerHour = Int("RelaunchMaxPerHour", 12, 1, 500);
            Budget.GiveUpAfter = Int("RelaunchGiveUpAfter", 3, 1, 50);

            if (Enabled) Subscribe();

            Program.Logger.Info(Enabled
                ? $"[Relaunch] on — cooldown {Budget.Cooldown.TotalMinutes:0}m, at most {Budget.MaxPerHour}/hour, giving up after {Budget.GiveUpAfter} failures"
                : "[Relaunch] off");
        }

        private static bool Bool(string Name, bool Default)
        {
            try
            {
                if (!AccountManager.General.Exists(Name)) return Default;

                return bool.TryParse(AccountManager.General.Get(Name), out bool Value) ? Value : Default;
            }
            catch { return Default; }
        }

        private static int Int(string Name, int Default, int Min, int Max)
        {
            try
            {
                if (!AccountManager.General.Exists(Name) || !int.TryParse(AccountManager.General.Get(Name), out int Value)) return Default;

                return Math.Min(Math.Max(Value, Min), Max);
            }
            catch { return Default; }
        }

        /// <summary>Called by the launch path so a relaunch knows where to go.</summary>
        public static void Remember(Account account, long PlaceId, string JobId)
        {
            if (account == null || PlaceId <= 0) return;

            LastLaunch[account.Username] = (PlaceId, JobId ?? string.Empty);
        }

        private static void Subscribe()
        {
            if (Subscribed) return;

            Subscribed = true;

            ClientLogWatcher.LaunchResolved += (account, Outcome, Detail) =>
            {
                if (account == null) return;

                // A launch that reached a server is the end of the story; a client that died before joining, or
                // dropped straight back out, is what this exists for.
                if (Outcome == LaunchOutcome.Joined) { Budget.Succeeded(account.Username); return; }
                if (!OnCrash) return;
                if (Outcome != LaunchOutcome.DiedEarly && Outcome != LaunchOutcome.Disconnected) return;

                _ = Consider(account, $"launch ended: {Detail}");
            };

            StuckDetector.StateChanged += Verdict =>
            {
                if (Verdict == null || string.IsNullOrEmpty(Verdict.Account)) return;
                if (Verdict.State == ClientState.Joined) { Budget.Succeeded(Verdict.Account); return; }
                if (!OnStuck || !Verdict.DeadWeight) return;

                Account account;

                lock (AccountManager.AccountsLock)
                    account = AccountManager.AccountsList?.FirstOrDefault(candidate => candidate.Username == Verdict.Account);

                if (account != null) _ = Consider(account, Verdict.Reason);
            };
        }

        /// <summary>Decides whether this account should go back, and sends it if so.</summary>
        private static async Task Consider(Account account, string Why)
        {
            if (!Enabled) return;

            if (!Destination(account, out long PlaceId, out string JobId))
            {
                Program.Logger.Info($"[Relaunch] {account.Username}: nowhere to send it back to");

                return;
            }

            await Request(account, PlaceId, JobId, Why);
        }

        /// <summary>
        /// The one automatic-relaunch gate used by the log supervisor and the legacy Nexus presence/ping
        /// detector. Detection stays independent, but budget, retry exclusions and failure accounting live here
        /// so two timers can never turn one outage into two launch storms.
        /// </summary>
        public static async Task<bool> Request(Account account, long PlaceId, string JobId, string Why)
        {
            if (account == null || PlaceId <= 0) return false;

            // Some failures are not worth a retry, and retrying them is actively harmful: a challenge needs a
            // person, and a rate limit needs time and a different address, not another attempt.
            if (account.LastAuthFailure == "CAPTCHA" || account.LastAuthFailure == "RATE LIMITED")
            {
                Program.Logger.Warn($"[Relaunch] {account.Username}: not retrying — {account.LastAuthFailure}");

                return false;
            }

            BudgetVerdict Verdict = Budget.Check(account.Username);

            if (Verdict != BudgetVerdict.Allowed)
            {
                Program.Logger.Info($"[Relaunch] {account.Username}: skipped — {Budget.Explain(Verdict, account.Username)}");

                return false;
            }

            Budget.Note(account.Username);

            Program.Logger.Info($"[Relaunch] {account.Username} -> place {PlaceId} ({Why})");

            try
            {
                string Result = await account.JoinServer(PlaceId, JobId);

                if (Result != null && Result.StartsWith("Success", StringComparison.OrdinalIgnoreCase))
                {
                    // Not a success yet — only that the client started. The launch watcher decides the rest, and
                    // it is what clears or increments the streak.
                    Program.Logger.Info($"[Relaunch] {account.Username}: client started");

                    return true;
                }

                if (Budget.Failed(account.Username))
                    Program.Logger.Warn($"[Relaunch] {account.Username}: giving up after {Budget.GiveUpAfter} failures — {Result}");
                else
                    Program.Logger.Warn($"[Relaunch] {account.Username}: failed — {Result}");

                return false;
            }
            catch (Exception x)
            {
                Budget.Failed(account.Username);

                Program.Logger.Error($"[Relaunch] {account.Username}: {x.Message}");

                return false;
            }
        }

        /// <summary>Where to send an account back to: this session's last launch, else the one saved on the account.</summary>
        private static bool Destination(Account account, out long PlaceId, out string JobId)
        {
            PlaceId = 0;
            JobId = string.Empty;

            if (LastLaunch.TryGetValue(account.Username, out (long PlaceId, string JobId) Known) && Known.PlaceId > 0)
            {
                PlaceId = Known.PlaceId;
                JobId = Known.JobId;

                return true;
            }

            // The old window lets a user pin a place to an account; honour it rather than inventing one.
            if (long.TryParse(account.GetField(PlaceField), out long Saved) && Saved > 0)
            {
                PlaceId = Saved;
                JobId = account.GetField(JobField) ?? string.Empty;

                return true;
            }

            return false;
        }
    }
}

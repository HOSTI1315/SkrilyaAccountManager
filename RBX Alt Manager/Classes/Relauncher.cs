using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes.Android;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Puts an account back where it was when its client dies, within a budget.
    ///
    /// PC and Android destinations are deliberately stored separately. A late Windows log verdict must never
    /// consume an Android serial, and a late Android verdict must never turn into a PC protocol launch.
    /// Detection stays platform-specific; the launch budget remains shared per account.
    /// </summary>
    internal static class Relauncher
    {
        public static bool Enabled { get; private set; }
        public static bool OnCrash { get; private set; } = true;
        public static bool OnStuck { get; private set; }

        public static readonly LaunchBudget Budget = new LaunchBudget();

        private sealed class AndroidLaunchTarget
        {
            public long PlaceId;
            public string Serial;
            public long? FollowUserId;
        }

        private static readonly ConcurrentDictionary<string, (long PlaceId, string JobId)> LastPcLaunch =
            new ConcurrentDictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, AndroidLaunchTarget> LastAndroidLaunch =
            new ConcurrentDictionary<string, AndroidLaunchTarget>(StringComparer.OrdinalIgnoreCase);

        // Android process crashes do not spend the normal relaunch budget, but still need a hard loop cap.
        private static readonly ConcurrentDictionary<string, int> AndroidCrashStreak =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>Remember a Windows client destination without touching the Android target for this account.</summary>
        public static void Remember(Account account, long PlaceId, string JobId)
        {
            if (account == null || PlaceId <= 0) return;

            LastPcLaunch[account.Username] = (PlaceId, JobId ?? string.Empty);
        }

        /// <summary>Remember an Android slot without touching the Windows destination for this account.</summary>
        public static void RememberAndroid(Account account, long PlaceId, string serial, long? followUserId = null)
        {
            if (account == null || PlaceId <= 0 || string.IsNullOrWhiteSpace(serial)) return;

            LastAndroidLaunch[account.Username] = new AndroidLaunchTarget
            {
                PlaceId = PlaceId,
                Serial = serial,
                FollowUserId = followUserId
            };
        }

        private static void Subscribe()
        {
            if (Subscribed) return;

            Subscribed = true;

            ClientLogWatcher.LaunchResolved += (account, Outcome, Detail) =>
            {
                if (account == null) return;

                if (Outcome == LaunchOutcome.Joined) { Budget.Succeeded(account.Username); return; }
                if (!OnCrash) return;
                if (Outcome != LaunchOutcome.DiedEarly && Outcome != LaunchOutcome.Disconnected) return;

                _ = ConsiderPc(account, $"launch ended: {Detail}");
            };

            StuckDetector.StateChanged += Verdict =>
            {
                if (Verdict == null || string.IsNullOrEmpty(Verdict.Account)) return;
                if (Verdict.State == ClientState.Joined) { Budget.Succeeded(Verdict.Account); return; }
                if (!OnStuck || !Verdict.DeadWeight) return;

                Account account;

                lock (AccountManager.AccountsLock)
                    account = AccountManager.AccountsList?.FirstOrDefault(candidate =>
                        string.Equals(candidate.Username, Verdict.Account, StringComparison.OrdinalIgnoreCase));

                if (account != null) _ = ConsiderPc(account, Verdict.Reason);
            };

            LogcatWatcher.StateChanged += Verdict =>
            {
                if (Verdict == null || string.IsNullOrEmpty(Verdict.Account)) return;

                Account account;

                lock (AccountManager.AccountsLock)
                    account = AccountManager.AccountsList?.FirstOrDefault(candidate =>
                        string.Equals(candidate.Username, Verdict.Account, StringComparison.OrdinalIgnoreCase));

                if (account == null) return;

                if (Verdict.State == AndroidClientState.Joined)
                {
                    AndroidCrashStreak.TryRemove(Verdict.Account, out _);
                    Budget.Succeeded(Verdict.Account);
                    return;
                }

                if (Verdict.State == AndroidClientState.Crashed)
                {
                    if (!OnCrash) return;

                    int Crashes = AndroidCrashStreak.AddOrUpdate(Verdict.Account, 1, (_, Seen) => Seen + 1);
                    if (Crashes > 15)
                    {
                        Program.Logger.Warn($"[Relaunch] {Verdict.Account}: Android crashed 15 times in a row; leaving the slot alone");
                        return;
                    }

                    _ = ConsiderAndroid(account, $"Android crash {Crashes}/15: {Verdict.Reason}", bypassBudget: true);
                    return;
                }

                AndroidCrashStreak.TryRemove(Verdict.Account, out _);

                bool ShouldRetry = Verdict.State == AndroidClientState.Disconnected
                    ? OnCrash
                    : Verdict.State == AndroidClientState.Timeout && OnStuck;

                if (!ShouldRetry)
                {
                    Program.Logger.Warn($"[Relaunch] {Verdict.Account}: Android {LogcatWatcher.StateName(Verdict.State)} — {Verdict.Reason}; not retrying");
                    return;
                }

                if (Budget.Failed(Verdict.Account))
                {
                    Program.Logger.Warn($"[Relaunch] {Verdict.Account}: giving up after {Budget.GiveUpAfter} failures — {Verdict.Reason}");
                    return;
                }

                _ = ConsiderAndroid(account, $"Android {LogcatWatcher.StateName(Verdict.State)}: {Verdict.Reason}");
            };
        }

        private static async Task ConsiderPc(Account account, string Why)
        {
            if (!Enabled) return;

            if (!Destination(account, out long PlaceId, out string JobId))
            {
                Program.Logger.Info($"[Relaunch] {account.Username}: no PC destination to send it back to");
                return;
            }

            await Request(account, PlaceId, JobId, Why);
        }

        private static async Task ConsiderAndroid(Account account, string Why, bool bypassBudget = false)
        {
            if (!Enabled) return;

            if (!LastAndroidLaunch.TryGetValue(account.Username, out AndroidLaunchTarget Target) ||
                Target == null || Target.PlaceId <= 0 || string.IsNullOrWhiteSpace(Target.Serial))
            {
                Program.Logger.Info($"[Relaunch] {account.Username}: no Android serial/destination to send it back to");
                return;
            }

            await RequestAndroid(account, Target, Why, bypassBudget);
        }

        /// <summary>
        /// Explicit PC relaunch gate used by Nexus and VersionManager. It never consults or mutates Android
        /// destinations, so callers that asked for a Windows client cannot jump to an emulator.
        /// </summary>
        public static async Task<bool> Request(Account account, long PlaceId, string JobId, string Why)
        {
            if (account == null || PlaceId <= 0) return false;

            if (account.LastAuthFailure == "CAPTCHA" || account.LastAuthFailure == "RATE LIMITED")
            {
                Program.Logger.Warn($"[Relaunch] {account.Username}: not retrying PC — {account.LastAuthFailure}");
                return false;
            }

            BudgetVerdict Verdict = Budget.Check(account.Username);

            if (Verdict != BudgetVerdict.Allowed)
            {
                Program.Logger.Info($"[Relaunch] {account.Username}: PC skipped — {Budget.Explain(Verdict, account.Username)}");
                return false;
            }

            Budget.Note(account.Username);
            Program.Logger.Info($"[Relaunch] {account.Username} -> PC place {PlaceId} ({Why})");

            try
            {
                string Result = await account.JoinServer(PlaceId, JobId);

                if (Result != null && Result.StartsWith("Success", StringComparison.OrdinalIgnoreCase))
                {
                    Program.Logger.Info($"[Relaunch] {account.Username}: PC client started");
                    return true;
                }

                if (Budget.Failed(account.Username))
                    Program.Logger.Warn($"[Relaunch] {account.Username}: giving up after {Budget.GiveUpAfter} failures — {Result}");
                else
                    Program.Logger.Warn($"[Relaunch] {account.Username}: PC failed — {Result}");

                return false;
            }
            catch (Exception x)
            {
                Budget.Failed(account.Username);
                Program.Logger.Error($"[Relaunch] {account.Username}: PC {x.Message}");
                return false;
            }
        }

        private static async Task<bool> RequestAndroid(Account account, AndroidLaunchTarget Target, string Why, bool bypassBudget)
        {
            if (account == null || Target == null || Target.PlaceId <= 0 || string.IsNullOrWhiteSpace(Target.Serial)) return false;

            if (account.LastAuthFailure == "CAPTCHA" || account.LastAuthFailure == "RATE LIMITED")
            {
                Program.Logger.Warn($"[Relaunch] {account.Username}: not retrying Android — {account.LastAuthFailure}");
                return false;
            }

            if (!bypassBudget)
            {
                BudgetVerdict Verdict = Budget.Check(account.Username);

                if (Verdict != BudgetVerdict.Allowed)
                {
                    Program.Logger.Info($"[Relaunch] {account.Username}: Android skipped — {Budget.Explain(Verdict, account.Username)}");
                    return false;
                }

                Budget.Note(account.Username);
            }

            Program.Logger.Info($"[Relaunch] {account.Username} -> Android {Target.Serial} place {Target.PlaceId} ({Why})");

            try
            {
                // JoinServerAndroidDetailed publishes the resolved logcat verdict. That event owns Android
                // success/failure streak accounting; counting the returned verdict here would count it twice.
                AndroidLaunchResult Result = await account.JoinServerAndroidDetailed(
                    Target.PlaceId, Target.Serial, Target.FollowUserId);

                Program.Logger.Info($"[Relaunch] {account.Username}: Android resolved {Result.State}");
                return Result.Joined;
            }
            catch (Exception x)
            {
                // No logcat verdict exists when launch setup itself throws, so this failure has not been counted.
                Budget.Failed(account.Username);
                Program.Logger.Error($"[Relaunch] {account.Username}: Android {x.Message}");
                return false;
            }
        }

        /// <summary>
        /// PC-only destination contract used by VersionManager fallback. Android state is intentionally invisible
        /// here; a Windows fallback must never consume an emulator serial.
        /// </summary>
        internal static bool Destination(Account account, out long PlaceId, out string JobId)
        {
            PlaceId = 0;
            JobId = string.Empty;

            if (account == null) return false;

            if (LastPcLaunch.TryGetValue(account.Username, out (long PlaceId, string JobId) Known) && Known.PlaceId > 0)
            {
                PlaceId = Known.PlaceId;
                JobId = Known.JobId;
                return true;
            }

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

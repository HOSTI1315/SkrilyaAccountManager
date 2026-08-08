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

        private sealed class LaunchTarget
        {
            public long PlaceId;
            public string JobId = string.Empty;
            public bool Android;
            public string Serial;
            public long? FollowUserId;
        }

        /// <summary>Where and how each account was last sent, so a relaunch stays on the same platform.</summary>
        private static readonly ConcurrentDictionary<string, LaunchTarget> LastLaunch =
            new ConcurrentDictionary<string, LaunchTarget>(StringComparer.OrdinalIgnoreCase);

        // A process crash is deliberately outside the normal launch budget, but it still needs a hard loop cap.
        // The Android reference supervisor allows at most 15 consecutive crashes before leaving the slot alone.
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

        /// <summary>Called by the launch path so a relaunch knows where to go.</summary>
        public static void Remember(Account account, long PlaceId, string JobId)
        {
            if (account == null || PlaceId <= 0) return;

            LastLaunch[account.Username] = new LaunchTarget
            {
                PlaceId = PlaceId,
                JobId = JobId ?? string.Empty
            };
        }

        /// <summary>Android counterpart of Remember: preserves the emulator slot as part of the destination.</summary>
        public static void RememberAndroid(Account account, long PlaceId, string serial, long? followUserId = null)
        {
            if (account == null || PlaceId <= 0 || string.IsNullOrWhiteSpace(serial)) return;

            LastLaunch[account.Username] = new LaunchTarget
            {
                PlaceId = PlaceId,
                Android = true,
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

            // StuckDetector and LogcatWatcher feed the same supervisor. Android keeps its richer verdict instead
            // of being flattened into a fake Windows ProcessId/ClientState, while the retry policy and budget are
            // shared here.
            LogcatWatcher.StateChanged += Verdict =>
            {
                if (Verdict == null || string.IsNullOrEmpty(Verdict.Account)) return;

                Account account;

                lock (AccountManager.AccountsLock)
                    account = AccountManager.AccountsList?.FirstOrDefault(candidate =>
                        candidate.Username.Equals(Verdict.Account, StringComparison.OrdinalIgnoreCase));

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

                    // A client-process crash does not spend an auth/relaunch attempt. Discovery on the next launch
                    // also reconnects emulator transports as needed; the separate streak prevents an infinite loop.
                    _ = Consider(account, $"Android crash {Crashes}/15: {Verdict.Reason}", bypassBudget: true);
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

                _ = Consider(account, $"Android {LogcatWatcher.StateName(Verdict.State)}: {Verdict.Reason}");
            };
        }

        /// <summary>Decides whether this account should go back, and sends it if so.</summary>
        private static async Task Consider(Account account, string Why, bool bypassBudget = false)
        {
            if (!Enabled) return;

            // Some failures are not worth a retry, and retrying them is actively harmful: a challenge needs a
            // person, and a rate limit needs time and a different address, not another attempt.
            if (account.LastAuthFailure == "CAPTCHA" || account.LastAuthFailure == "RATE LIMITED")
            {
                Program.Logger.Warn($"[Relaunch] {account.Username}: not retrying — {account.LastAuthFailure}");

                return;
            }

            if (!Destination(account, out LaunchTarget Target))
            {
                Program.Logger.Info($"[Relaunch] {account.Username}: nowhere to send it back to");

                return;
            }

            if (!bypassBudget)
            {
                BudgetVerdict Verdict = Budget.Check(account.Username);

                if (Verdict != BudgetVerdict.Allowed)
                {
                    Program.Logger.Info($"[Relaunch] {account.Username}: skipped — {Budget.Explain(Verdict, account.Username)}");
                    return;
                }

                Budget.Note(account.Username);
            }

            Program.Logger.Info($"[Relaunch] {account.Username} -> {(Target.Android ? $"Android {Target.Serial}" : "PC")} place {Target.PlaceId} ({Why})");

            try
            {
                if (Target.Android)
                {
                    // Detailed returns only after logcat resolves the launch and publishes its verdict. That event
                    // owns success/failure accounting, so doing it again here would double-count one Android try.
                    AndroidLaunchResult AndroidResult = await account.JoinServerAndroidDetailed(
                        Target.PlaceId, Target.Serial, Target.FollowUserId);

                    Program.Logger.Info($"[Relaunch] {account.Username}: Android resolved {AndroidResult.State}");
                    return;
                }

                string Result = await account.JoinServer(Target.PlaceId, Target.JobId);

                if (Result != null && Result.StartsWith("Success", StringComparison.OrdinalIgnoreCase))
                {
                    // Not a success yet — only that the client started. The launch watcher decides the rest, and
                    // it is what clears or increments the streak.
                    Program.Logger.Info($"[Relaunch] {account.Username}: client started");

                    return;
                }

                if (Budget.Failed(account.Username))
                    Program.Logger.Warn($"[Relaunch] {account.Username}: giving up after {Budget.GiveUpAfter} failures — {Result}");
                else
                    Program.Logger.Warn($"[Relaunch] {account.Username}: failed — {Result}");
            }
            catch (Exception x)
            {
                Budget.Failed(account.Username);

                Program.Logger.Error($"[Relaunch] {account.Username}: {x.Message}");
            }
        }

        /// <summary>Where to send an account back to: this session's last launch, else the one saved on the account.</summary>
        private static bool Destination(Account account, out LaunchTarget target)
        {
            target = null;

            if (LastLaunch.TryGetValue(account.Username, out LaunchTarget Known) && Known.PlaceId > 0)
            {
                target = Known;
                return true;
            }

            // The old window lets a user pin a place to an account; honour it rather than inventing one.
            if (long.TryParse(account.GetField(PlaceField), out long Saved) && Saved > 0)
            {
                target = new LaunchTarget
                {
                    PlaceId = Saved,
                    JobId = account.GetField(JobField) ?? string.Empty
                };
                return true;
            }

            return false;
        }
    }
}

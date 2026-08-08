using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes.Android
{
    public enum AndroidClientState
    {
        Launching,
        Joined,
        Captcha,
        ActiveAccount,
        AntiCheat,
        Outdated,
        Kicked,
        Banned,
        AccessDenied,
        Disconnected,
        Crashed,
        Timeout
    }

    /// <summary>The Android equivalent of ClientVerdict: one resolved launch, with the reason kept structured.</summary>
    public sealed class AndroidClientVerdict
    {
        public string Serial { get; internal set; }
        public string Account { get; internal set; }
        public AndroidClientState State { get; internal set; }
        public string Reason { get; internal set; }
        public double AgeSeconds { get; internal set; }
        public long PlaceId { get; internal set; }
        public string JobId { get; internal set; }
        public int? DisconnectCode { get; internal set; }

        public bool Retryable => State == AndroidClientState.Disconnected ||
            State == AndroidClientState.Crashed || State == AndroidClientState.Timeout;

        public bool DeadWeight => State == AndroidClientState.Timeout;

        public override string ToString() =>
            $"{Serial} ({Account ?? "?"}) {State}: {Reason}";
    }

    /// <summary>
    /// Resolves an Android launch from Roblox logcat markers. The launcher clears logcat immediately before the
    /// deeplink, so every marker inspected here belongs to the current launch rather than to a previous account.
    /// </summary>
    internal static class LogcatWatcher
    {
        private static readonly Regex JoiningGame = new Regex(
            @"! Joining game '([^']+)' place (\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Disconnect = new Regex(
            @"Sending disconnect with reason:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Captcha = new Regex(
            @"generic-challenge-type\s*=\s*captcha|challengeDisplayed", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static event Action<AndroidClientVerdict> StateChanged;

        public static async Task<AndroidClientVerdict> WatchAsync(
            Account account,
            AdbClient adb,
            string packageName,
            long placeId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (adb == null) throw new ArgumentNullException(nameof(adb));
            if (string.IsNullOrWhiteSpace(packageName)) throw new ArgumentException("package name is required", nameof(packageName));
            if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

            DateTime Started = DateTime.UtcNow;
            TimeSpan CrashGrace = TimeSpan.FromSeconds(3);

            while (DateTime.UtcNow - Started < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AdbCommandResult Log = await adb.LogcatRobloxAsync(cancellationToken).ConfigureAwait(false);

                if (Log.Success && !string.IsNullOrWhiteSpace(Log.StandardOutput))
                {
                    AndroidClientVerdict Verdict = Classify(Log.StandardOutput, account, adb.Serial, placeId, Started);
                    if (Verdict != null) return Verdict;
                }

                if (DateTime.UtcNow - Started >= CrashGrace &&
                    !await adb.AppRunningAsync(packageName, cancellationToken).ConfigureAwait(false))
                    return Make(account, adb.Serial, AndroidClientState.Crashed,
                        "Roblox process exited before joining", placeId, Started);

                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            }

            if (!await adb.AppRunningAsync(packageName, cancellationToken).ConfigureAwait(false))
                return Make(account, adb.Serial, AndroidClientState.Crashed,
                    "Roblox process exited before joining", placeId, Started);

            return Make(account, adb.Serial, AndroidClientState.Timeout,
                $"Roblox did not join within {timeout.TotalSeconds:0} seconds", placeId, Started);
        }

        /// <summary>Publish only after Account releases LaunchLock, so a subscriber may safely relaunch.</summary>
        internal static void Publish(AndroidClientVerdict verdict)
        {
            if (verdict == null) return;

            try { StateChanged?.Invoke(verdict); }
            catch (Exception x) { Program.Logger.Error($"[Android] verdict subscriber failed: {x}"); }
        }

        internal static string StateName(AndroidClientState state)
        {
            switch (state)
            {
                case AndroidClientState.ActiveAccount: return "active-account";
                case AndroidClientState.AntiCheat: return "anti-cheat";
                case AndroidClientState.AccessDenied: return "access-denied";
                default: return state.ToString().ToLowerInvariant();
            }
        }

        private static AndroidClientVerdict Classify(
            string log,
            Account account,
            string serial,
            long requestedPlaceId,
            DateTime started)
        {
            Match Join = LastMatch(JoiningGame, log);
            Match Drop = LastMatch(Disconnect, log);
            Match Challenge = LastMatch(Captcha, log);

            Match Latest = null;
            string Kind = null;

            Consider(Join, "join", ref Latest, ref Kind);
            Consider(Drop, "disconnect", ref Latest, ref Kind);
            Consider(Challenge, "captcha", ref Latest, ref Kind);

            if (Latest == null) return null;

            string JobId = Join?.Success == true ? Join.Groups[1].Value : null;

            if (Kind == "join")
            {
                long PlaceId = long.TryParse(Join.Groups[2].Value, out long Parsed) && Parsed > 0 ? Parsed : requestedPlaceId;
                return Make(account, serial, AndroidClientState.Joined,
                    $"joined place {PlaceId}", PlaceId, started, JobId);
            }

            if (Kind == "captcha")
                return Make(account, serial, AndroidClientState.Captcha,
                    "Roblox displayed an account/session CAPTCHA challenge", requestedPlaceId, started, JobId);

            if (!int.TryParse(Drop.Groups[1].Value, out int Code))
                return Make(account, serial, AndroidClientState.Disconnected,
                    "Roblox disconnected before joining", requestedPlaceId, started, JobId);

            AndroidClientState State = ClassifyDisconnect(Code, out string Reason);
            return Make(account, serial, State, Reason, requestedPlaceId, started, JobId, Code);
        }

        private static AndroidClientState ClassifyDisconnect(int code, out string reason)
        {
            switch (code)
            {
                case 264:
                case 273:
                case 276:
                    reason = $"account is active on another device (disconnect {code})";
                    return AndroidClientState.ActiveAccount;

                case 268:
                case 272:
                    reason = $"Roblox anti-cheat rejected the client/executor (disconnect {code})";
                    return AndroidClientState.AntiCheat;

                case 280:
                    reason = "Roblox client is out of date (disconnect 280)";
                    return AndroidClientState.Outdated;

                case 267:
                case 291:
                    reason = $"kicked by the game or moderation (disconnect {code})";
                    return AndroidClientState.Kicked;

                case 600:
                    reason = "account is banned in this place (disconnect 600)";
                    return AndroidClientState.Banned;

                case 524:
                case 533:
                case 773:
                    reason = $"account cannot access this place (disconnect {code})";
                    return AndroidClientState.AccessDenied;

                default:
                    reason = $"Roblox disconnected with reason {code}";
                    return AndroidClientState.Disconnected;
            }
        }

        private static Match LastMatch(Regex regex, string text)
        {
            MatchCollection Matches = regex.Matches(text ?? string.Empty);
            return Matches.Count == 0 ? null : Matches[Matches.Count - 1];
        }

        private static void Consider(Match candidate, string kind, ref Match latest, ref string latestKind)
        {
            if (candidate == null || !candidate.Success) return;
            if (latest != null && candidate.Index <= latest.Index) return;

            latest = candidate;
            latestKind = kind;
        }

        private static AndroidClientVerdict Make(
            Account account,
            string serial,
            AndroidClientState state,
            string reason,
            long placeId,
            DateTime started,
            string jobId = null,
            int? disconnectCode = null) => new AndroidClientVerdict
        {
            Serial = serial,
            Account = account?.Username,
            State = state,
            Reason = reason,
            AgeSeconds = Math.Max(0, (DateTime.UtcNow - started).TotalSeconds),
            PlaceId = placeId,
            JobId = jobId,
            DisconnectCode = disconnectCode
        };
    }
}

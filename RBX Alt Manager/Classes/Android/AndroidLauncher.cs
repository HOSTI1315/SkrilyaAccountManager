using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes.Android
{
    public sealed class AndroidLaunchResult
    {
        public string Serial { get; internal set; }
        public string Package { get; internal set; }
        public string State { get; internal set; }
        public string Reason { get; internal set; }
        public long PlaceId { get; internal set; }
        public string JobId { get; internal set; }
        public int? DisconnectCode { get; internal set; }
        internal AndroidClientVerdict Verdict { get; set; }

        public bool Joined => string.Equals(State, "joined", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>force-stop/inject/deeplink launch operations for one already-discovered emulator slot.</summary>
    public sealed class AndroidLauncher
    {
        private static readonly Regex SafePackage = new Regex(@"^[A-Za-z0-9_.]+$", RegexOptions.Compiled);

        private readonly AdbClient Adb;
        private readonly string PackageName;
        private readonly CookieInjector Injector;

        public AndroidLauncher(AdbClient adb, string packageName, bool backupCookieStore = true)
        {
            Adb = adb ?? throw new ArgumentNullException(nameof(adb));
            if (string.IsNullOrWhiteSpace(packageName) || !SafePackage.IsMatch(packageName))
                throw new ArgumentException("invalid Android package name", nameof(packageName));

            PackageName = packageName;
            Injector = new CookieInjector(Adb, PackageName) { BackupCookieStore = backupCookieStore };
        }

        /// <summary>Switches identity on the slot, then starts the requested place.</summary>
        public async Task<AndroidLaunchResult> LaunchAccountAsync(Account account, long placeId, long? userId = null, CancellationToken cancellationToken = default)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId));

            // InjectAsync force-stops before touching the DB. It must stay that way: a live WebView owns an
            // in-memory cookie jar and will otherwise overwrite the row we just changed.
            await Injector.InjectAsync(account.SecurityToken, cancellationToken).ConfigureAwait(false);

            // The watcher reads a dump rather than a streaming pipe. Clear immediately before the intent so an
            // old account's join/disconnect marker cannot be mistaken for the state of this launch.
            AdbCommandResult Cleared = await Adb.ClearLogcatAsync(cancellationToken).ConfigureAwait(false);
            if (!Cleared.Success)
                throw new InvalidOperationException($"Could not clear logcat on {Adb.Serial}: {Cleared.CombinedOutput.Trim()}");

            await StartDeeplinkAsync(placeId, userId, cancellationToken).ConfigureAwait(false);

            return new AndroidLaunchResult
            {
                Serial = Adb.Serial,
                Package = PackageName,
                State = "launching",
                PlaceId = placeId
            };
        }

        /// <summary>Teleports a live client without changing its account.</summary>
        public Task HopAsync(long placeId, CancellationToken cancellationToken = default) =>
            StartDeeplinkAsync(placeId, null, cancellationToken);

        /// <summary>Roblox launcher-level Follow equivalent: join place and rendezvous with a specific user.</summary>
        public Task JoinUserAsync(long placeId, long userId, CancellationToken cancellationToken = default) =>
            StartDeeplinkAsync(placeId, userId, cancellationToken);

        public Task ClearAccountAsync(CancellationToken cancellationToken = default) => Injector.ClearAsync(cancellationToken);

        private async Task StartDeeplinkAsync(long placeId, long? userId, CancellationToken cancellationToken)
        {
            if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId));
            if (userId.HasValue && userId.Value <= 0) throw new ArgumentOutOfRangeException(nameof(userId));

            string Uri = $"roblox://placeId={placeId}" + (userId.HasValue ? $"&userId={userId.Value}" : string.Empty);
            string Component = $"{PackageName}/com.roblox.client.ActivityProtocolLaunch";

            // The explicit component prevents another Roblox/repack package from stealing the intent. The URI is
            // shell-quoted because an unquoted '&userId=' is a shell background operator on Android.
            AdbCommandResult Result = await Adb.ShellAsync(
                $"am start -n {AdbClient.QuoteShell(Component)} -a android.intent.action.VIEW -d {AdbClient.QuoteShell(Uri)}",
                cancellationToken).ConfigureAwait(false);

            if (!Result.Success || Result.CombinedOutput.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
                Result.CombinedOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Roblox deeplink failed on {Adb.Serial}: {Result.CombinedOutput.Trim()}");
        }
    }
}

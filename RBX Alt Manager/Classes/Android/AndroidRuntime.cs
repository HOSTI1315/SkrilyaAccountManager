using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes.Android
{
    /// <summary>
    /// Manager-facing entry point for phases 1-3. Discovery remains runtime-only: adb, serials, Android version,
    /// Roblox package and Chromium schema are never baked into the desktop application.
    /// </summary>
    public static class AndroidRuntime
    {
        public static async Task<IReadOnlyList<EmulatorDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            string AdbPath = Setting("AdbPath");
            string Package = Setting("Package");
            string[] Serials = SplitList(Setting("Serials"));

            return await new EmulatorDiscovery().DiscoverAsync(AdbPath, Serials, Package, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<AndroidLaunchResult> LaunchAsync(Account account, long placeId, string serial, long? userId = null, CancellationToken cancellationToken = default)
        {
            if (!BoolSetting("EnableAndroid", false))
                throw new InvalidOperationException("Android launching is disabled. Set [Android] EnableAndroid=true in RAMSettings.ini first.");

            IReadOnlyList<EmulatorDevice> Devices = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            EmulatorDevice Device = SelectDevice(Devices, serial);

            if (Device.RootMode == AdbRootMode.None)
                throw new InvalidOperationException($"{Device.Serial}: emulator root is not available");
            if (string.IsNullOrWhiteSpace(Device.RobloxPackage))
                throw new InvalidOperationException($"{Device.Serial}: no package handling roblox:// was found");

            AdbClient Adb = new AdbClient(Device.AdbPath, Device.Serial);
            if (await Adb.EnsureRootAsync(cancellationToken).ConfigureAwait(false) == AdbRootMode.None)
                throw new InvalidOperationException($"{Device.Serial}: emulator root disappeared before launch");

            AndroidLauncher Launcher = new AndroidLauncher(Adb, Device.RobloxPackage, BoolSetting("BackupCookieStore", true));
            AndroidLaunchResult Result = await Launcher.LaunchAccountAsync(account, placeId, userId, cancellationToken).ConfigureAwait(false);

            // ConnectTimeout used to be seeded into RAMSettings.ini but never read. It is the deadline for a real
            // verdict now: the method does not report success merely because am start accepted the intent.
            TimeSpan Timeout = TimeSpan.FromSeconds(IntSetting("ConnectTimeout", 90, 5, 600));
            AndroidClientVerdict Verdict = await LogcatWatcher.WatchAsync(
                account, Adb, Device.RobloxPackage, placeId, Timeout, cancellationToken).ConfigureAwait(false);

            Result.State = LogcatWatcher.StateName(Verdict.State);
            Result.Reason = Verdict.Reason;
            Result.PlaceId = Verdict.PlaceId;
            Result.JobId = Verdict.JobId;
            Result.DisconnectCode = Verdict.DisconnectCode;
            Result.Verdict = Verdict;

            return Result;
        }

        private static EmulatorDevice SelectDevice(IReadOnlyList<EmulatorDevice> devices, string serial)
        {
            if (devices == null || devices.Count == 0)
                throw new InvalidOperationException("No running Android emulator instances were discovered.");

            if (!string.IsNullOrWhiteSpace(serial))
            {
                string Wanted = EmulatorDiscovery.CanonicalTransportKey(serial.Trim());
                EmulatorDevice Exact = devices.FirstOrDefault(x =>
                    x.Serial.Equals(serial.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    EmulatorDiscovery.CanonicalTransportKey(x.Serial).Equals(Wanted, StringComparison.OrdinalIgnoreCase));

                if (Exact == null) throw new InvalidOperationException($"Android emulator '{serial}' was not found.");
                return Exact;
            }

            EmulatorDevice Ready = devices.FirstOrDefault(x => x.Ready);
            return Ready ?? devices[0];
        }

        private static string Setting(string name) => AccountManager.Android?.Get(name)?.Trim();

        private static bool BoolSetting(string name, bool fallback)
        {
            string Value = Setting(name);
            return bool.TryParse(Value, out bool Parsed) ? Parsed : fallback;
        }

        private static int IntSetting(string name, int fallback, int min, int max)
        {
            string Value = Setting(name);
            if (!int.TryParse(Value, out int Parsed)) return fallback;

            return Math.Min(Math.Max(Parsed, min), max);
        }

        private static string[] SplitList(string value) => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes.Android
{
    public sealed class EmulatorDevice
    {
        public string Serial { get; internal set; }
        public string AdbPath { get; internal set; }
        public AdbRootMode RootMode { get; internal set; }
        public string RobloxPackage { get; internal set; }
        public string AndroidVersion { get; internal set; }
        public bool Ready => RootMode != AdbRootMode.None && !string.IsNullOrWhiteSpace(RobloxPackage);
    }

    /// <summary>
    /// Runtime discovery for LDPlayer, MuMu and MEmu. The adb client is taken from the running emulator process,
    /// never PATH, because mixing emulator/adbd versions is a common cause of permanently-offline transports.
    /// </summary>
    public sealed class EmulatorDiscovery
    {
        private static readonly string[] EmulatorProcessHints =
        {
            "dnplayer", "ldplayer", "ldplayer9", "mumunxmain", "mumuplayer", "nemuplayer", "memu"
        };

        public async Task<IReadOnlyList<EmulatorDevice>> DiscoverAsync(
            string configuredAdbPath = null,
            IEnumerable<string> serialFilter = null,
            string configuredPackage = null,
            CancellationToken cancellationToken = default)
        {
            List<string> AdbPaths = FindAdbPaths(configuredAdbPath).ToList();
            if (AdbPaths.Count == 0)
                throw new FileNotFoundException("No emulator adb.exe was found. Start LDPlayer, MuMu or MEmu, or set [Android] AdbPath.");

            HashSet<string> Wanted = new HashSet<string>((serialFilter ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, (string adb, string serial)> Seen = new Dictionary<string, (string adb, string serial)>(StringComparer.OrdinalIgnoreCase);

            foreach (string AdbPath in AdbPaths)
            {
                AdbClient Bootstrap = new AdbClient(AdbPath, "unused");
                await Bootstrap.StartServerAsync(cancellationToken).ConfigureAwait(false);
                await ConnectLdTransportsAsync(AdbPath, cancellationToken).ConfigureAwait(false);
            }

            // Large farms do not always appear in one adb devices pass. Stop once a pass stops adding devices,
            // with a hard ceiling of three so a sick daemon cannot keep discovery alive forever.
            for (int Pass = 0; Pass < 3; Pass++)
            {
                int Before = Seen.Count;

                foreach (string AdbPath in AdbPaths)
                {
                    AdbClient Bootstrap = new AdbClient(AdbPath, "unused");
                    AdbCommandResult Devices = await Bootstrap.DevicesAsync(cancellationToken).ConfigureAwait(false);

                    foreach ((string Serial, string State) in ParseDevices(Devices.StandardOutput))
                    {
                        if (!State.Equals("device", StringComparison.OrdinalIgnoreCase)) continue;
                        if (Wanted.Count > 0 && !Wanted.Contains(Serial) && !Wanted.Contains(AdbClient.TcpForm(Serial) ?? string.Empty)) continue;

                        string Key = CanonicalTransportKey(Serial);
                        if (!Seen.ContainsKey(Key)) Seen.Add(Key, (AdbPath, Serial));
                    }
                }

                if (Seen.Count == Before) break;
            }

            List<EmulatorDevice> Result = new List<EmulatorDevice>();

            foreach ((string AdbPath, string Serial) in Seen.Values)
            {
                AdbClient Client = new AdbClient(AdbPath, Serial);
                AdbRootMode Root = await Client.EnsureRootAsync(cancellationToken).ConfigureAwait(false);
                string Package = string.IsNullOrWhiteSpace(configuredPackage)
                    ? await DetectRobloxPackageAsync(Client, cancellationToken).ConfigureAwait(false)
                    : configuredPackage.Trim();
                string AndroidVersion = (await Client.ShellAsync("getprop ro.build.version.release", cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

                Result.Add(new EmulatorDevice
                {
                    AdbPath = AdbPath,
                    Serial = Client.Serial,
                    RootMode = Root,
                    RobloxPackage = Package,
                    AndroidVersion = AndroidVersion
                });
            }

            return Result;
        }

        public static async Task<string> DetectRobloxPackageAsync(AdbClient client, CancellationToken cancellationToken = default)
        {
            AdbCommandResult Resolved = await client.ShellAsync(
                "cmd package resolve-activity -a android.intent.action.VIEW -d 'roblox://placeId=1'",
                cancellationToken).ConfigureAwait(false);

            string Text = Resolved.CombinedOutput;
            Match PackageName = Regex.Match(Text, @"\bpackageName=([A-Za-z0-9_.]+)", RegexOptions.IgnoreCase);
            if (PackageName.Success) return PackageName.Groups[1].Value;

            Match Component = Regex.Match(Text, @"\b([A-Za-z][A-Za-z0-9_.]+)/[A-Za-z0-9_.$]+", RegexOptions.IgnoreCase);
            if (Component.Success) return Component.Groups[1].Value;

            AdbCommandResult Packages = await client.ShellAsync("pm list packages", cancellationToken).ConfigureAwait(false);
            string[] Candidates = Packages.StandardOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Substring("package:".Length))
                .Where(x => x.Contains("roblox", StringComparison.OrdinalIgnoreCase) || x.Equals("com.roblox.client", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return Candidates.FirstOrDefault(x => x.Equals("com.roblox.client", StringComparison.OrdinalIgnoreCase)) ?? Candidates.FirstOrDefault();
        }

        private static IEnumerable<string> FindAdbPaths(string configuredAdbPath)
        {
            HashSet<string> Found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(configuredAdbPath))
            {
                string Full = Environment.ExpandEnvironmentVariables(configuredAdbPath.Trim().Trim('"'));
                if (File.Exists(Full)) Found.Add(Path.GetFullPath(Full));
                return Found;
            }

            foreach (Process process in Process.GetProcesses())
            {
                string Name;
                try { Name = process.ProcessName; }
                catch { continue; }

                if (!EmulatorProcessHints.Any(Hint => Name.Contains(Hint, StringComparison.OrdinalIgnoreCase))) continue;

                string Exe;
                try { Exe = process.MainModule?.FileName; }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(Exe)) continue;

                string Dir = Path.GetDirectoryName(Exe);
                foreach (string CandidateDir in NearbyDirectories(Dir))
                {
                    string Candidate = Path.Combine(CandidateDir, "adb.exe");
                    if (File.Exists(Candidate)) Found.Add(Path.GetFullPath(Candidate));
                }
            }

            return Found;
        }

        private static IEnumerable<string> NearbyDirectories(string processDirectory)
        {
            if (string.IsNullOrWhiteSpace(processDirectory)) yield break;

            yield return processDirectory;
            yield return Path.Combine(processDirectory, "shell");
            yield return Path.Combine(processDirectory, "adb");

            DirectoryInfo Parent = Directory.GetParent(processDirectory);
            if (Parent == null) yield break;

            yield return Parent.FullName;
            yield return Path.Combine(Parent.FullName, "shell");
            yield return Path.Combine(Parent.FullName, "adb");
        }

        private static async Task ConnectLdTransportsAsync(string adbPath, CancellationToken cancellationToken)
        {
            foreach (string Console in FindLdConsoles())
            {
                string Output = await RunCaptureAsync(Console, new[] { "list2" }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

                foreach (string Line in Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string First = Line.Split(',').FirstOrDefault()?.Trim();
                    if (!int.TryParse(First, out int Index) || Index < 0) continue;

                    int Port = 5555 + (2 * Index);
                    AdbClient Bootstrap = new AdbClient(adbPath, "unused");
                    await Bootstrap.ConnectAsync($"127.0.0.1:{Port}", cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static IEnumerable<string> FindLdConsoles()
        {
            HashSet<string> Found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Process process in Process.GetProcesses())
            {
                if (!process.ProcessName.Contains("ldplayer", StringComparison.OrdinalIgnoreCase) &&
                    !process.ProcessName.Contains("dnplayer", StringComparison.OrdinalIgnoreCase)) continue;

                string Exe;
                try { Exe = process.MainModule?.FileName; }
                catch { continue; }

                string Dir = string.IsNullOrWhiteSpace(Exe) ? null : Path.GetDirectoryName(Exe);
                foreach (string CandidateDir in NearbyDirectories(Dir))
                {
                    string Candidate = Path.Combine(CandidateDir, "ldconsole.exe");
                    if (File.Exists(Candidate)) Found.Add(Candidate);
                }
            }

            return Found;
        }

        private static IEnumerable<(string Serial, string State)> ParseDevices(string output)
        {
            foreach (string Line in (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;

                string[] Parts = Regex.Split(Line.Trim(), @"\s+");
                if (Parts.Length >= 2) yield return (Parts[0], Parts[1]);
            }
        }

        internal static string CanonicalTransportKey(string serial)
        {
            string Tcp = AdbClient.TcpForm(serial);
            return !string.IsNullOrEmpty(Tcp) ? Tcp.ToLowerInvariant() : (serial ?? string.Empty).ToLowerInvariant();
        }

        private static async Task<string> RunCaptureAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ProcessStartInfo Start = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string Arg in args) Start.ArgumentList.Add(Arg);

            using Process process = Process.Start(Start);
            if (process == null) return string.Empty;

            Task<string> Stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> Stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            Task Wait = process.WaitForExitAsync(cancellationToken);

            if (await Task.WhenAny(Wait, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false) != Wait)
            {
                try { process.Kill(true); } catch { }
                return string.Empty;
            }

            await Wait.ConfigureAwait(false);
            return (await Stdout.ConfigureAwait(false)) + Environment.NewLine + (await Stderr.ConfigureAwait(false));
        }
    }
}

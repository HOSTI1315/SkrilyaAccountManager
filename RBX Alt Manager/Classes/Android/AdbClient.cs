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
    public enum AdbRootMode
    {
        None,
        Su,
        Adbd
    }

    public sealed class AdbCommandResult
    {
        public int ExitCode { get; internal set; }
        public string StandardOutput { get; internal set; } = string.Empty;
        public string StandardError { get; internal set; } = string.Empty;
        public bool TimedOut { get; internal set; }
        public bool Success => !TimedOut && ExitCode == 0;
        public string CombinedOutput => string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>
    /// Thin, argument-safe wrapper around the adb that belongs to the running emulator.
    /// start-server/connect deliberately do not redirect stdout/stderr: the adb daemon can inherit those pipes
    /// and keep them open after the client exits, which makes a ReadToEnd-based wrapper hang forever.
    /// </summary>
    public sealed class AdbClient
    {
        private static readonly Regex EmulatorSerial = new Regex(@"^emulator-(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public string AdbPath { get; }
        public string Serial { get; private set; }
        public AdbRootMode RootMode { get; private set; } = AdbRootMode.None;
        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

        public AdbClient(string adbPath, string serial)
        {
            if (string.IsNullOrWhiteSpace(adbPath)) throw new ArgumentException("adb path is required", nameof(adbPath));
            if (string.IsNullOrWhiteSpace(serial)) throw new ArgumentException("device serial is required", nameof(serial));

            AdbPath = adbPath;
            Serial = serial;
        }

        public Task<AdbCommandResult> ShellAsync(string command, CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "-s", Serial, "shell", command }, CommandTimeout, cancellationToken);

        public async Task<AdbCommandResult> RootShellAsync(string command, CancellationToken cancellationToken = default)
        {
            if (RootMode == AdbRootMode.None)
                await EnsureRootAsync(cancellationToken).ConfigureAwait(false);

            if (RootMode == AdbRootMode.Su)
                return await ShellAsync($"su -c {QuoteShell(command)}", cancellationToken).ConfigureAwait(false);

            if (RootMode == AdbRootMode.Adbd)
                return await ShellAsync(command, cancellationToken).ConfigureAwait(false);

            return new AdbCommandResult { ExitCode = -1, StandardError = "root is not available on this emulator" };
        }

        /// <summary>
        /// LDPlayer production images must be probed with su first. Calling adb root there can drop the transport
        /// offline, so adb-root is a fallback for MEmu-style images only.
        /// </summary>
        public async Task<AdbRootMode> EnsureRootAsync(CancellationToken cancellationToken = default)
        {
            AdbCommandResult Su = await ShellAsync("su -c id", cancellationToken).ConfigureAwait(false);

            if (HasRootIdentity(Su))
            {
                RootMode = AdbRootMode.Su;
                return RootMode;
            }

            AdbCommandResult Direct = await ShellAsync("id", cancellationToken).ConfigureAwait(false);

            if (HasRootIdentity(Direct))
            {
                RootMode = AdbRootMode.Adbd;
                return RootMode;
            }

            await RunWithoutRedirectAsync(new[] { "-s", Serial, "root" }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);

            string TcpSerial = TcpForm(Serial);
            if (!string.IsNullOrEmpty(TcpSerial))
            {
                await RunWithoutRedirectAsync(new[] { "connect", TcpSerial }, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

                if (await WaitForDeviceAsync(Serial, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false) == false &&
                    await WaitForDeviceAsync(TcpSerial, TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false))
                    Serial = TcpSerial;
            }
            else
                await WaitForDeviceAsync(Serial, TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);

            Direct = await ShellAsync("id", cancellationToken).ConfigureAwait(false);
            RootMode = HasRootIdentity(Direct) ? AdbRootMode.Adbd : AdbRootMode.None;

            return RootMode;
        }

        public Task<AdbCommandResult> ForceStopAsync(string packageName, CancellationToken cancellationToken = default) =>
            ShellAsync($"am force-stop {QuoteShell(packageName)}", cancellationToken);

        public Task<AdbCommandResult> PushAsync(string localPath, string remotePath, CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "-s", Serial, "push", localPath, remotePath }, CommandTimeout, cancellationToken);

        public Task<AdbCommandResult> PullAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "-s", Serial, "pull", remotePath, localPath }, CommandTimeout, cancellationToken);

        public Task<AdbCommandResult> ScreencapAsync(string remotePath, CancellationToken cancellationToken = default) =>
            ShellAsync($"screencap -p {QuoteShell(remotePath)}", cancellationToken);

        public Task<AdbCommandResult> LogcatRobloxAsync(CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "-s", Serial, "logcat", "-d", "-s", "Roblox" }, CommandTimeout, cancellationToken);

        public Task<AdbCommandResult> ClearLogcatAsync(CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "-s", Serial, "logcat", "-c" }, CommandTimeout, cancellationToken);

        public async Task<bool> RootFileExistsAsync(string remotePath, CancellationToken cancellationToken = default)
        {
            AdbCommandResult Result = await RootShellAsync($"test -f {QuoteShell(remotePath)} && echo exists", cancellationToken).ConfigureAwait(false);
            return Result.StandardOutput.Contains("exists", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> AppRunningAsync(string packageName, CancellationToken cancellationToken = default)
        {
            string Quoted = QuoteShell(packageName);
            AdbCommandResult Result = await ShellAsync($"pidof {Quoted}", cancellationToken).ConfigureAwait(false);

            if (Result.Success && !string.IsNullOrWhiteSpace(Result.StandardOutput)) return true;

            Result = await ShellAsync($"pgrep -f {Quoted}", cancellationToken).ConfigureAwait(false);
            return Result.Success && !string.IsNullOrWhiteSpace(Result.StandardOutput);
        }

        public Task StartServerAsync(CancellationToken cancellationToken = default) =>
            RunWithoutRedirectAsync(new[] { "start-server" }, TimeSpan.FromSeconds(15), cancellationToken);

        public Task ConnectAsync(string serial, CancellationToken cancellationToken = default) =>
            RunWithoutRedirectAsync(new[] { "connect", serial }, TimeSpan.FromSeconds(15), cancellationToken);

        public Task<AdbCommandResult> DevicesAsync(CancellationToken cancellationToken = default) =>
            RunAsync(new[] { "devices" }, CommandTimeout, cancellationToken);

        private async Task<bool> WaitForDeviceAsync(string serial, TimeSpan timeout, CancellationToken cancellationToken)
        {
            DateTime Until = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < Until)
            {
                AdbCommandResult State = await RunAsync(new[] { "-s", serial, "get-state" }, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                if (State.Success && State.StandardOutput.Trim().Equals("device", StringComparison.OrdinalIgnoreCase)) return true;

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        private static bool HasRootIdentity(AdbCommandResult result) =>
            result != null && result.Success && result.CombinedOutput.Contains("uid=0", StringComparison.OrdinalIgnoreCase);

        internal static string TcpForm(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return null;
            if (Regex.IsMatch(serial, @"^(?:127\.0\.0\.1|localhost):\d+$", RegexOptions.IgnoreCase)) return serial;

            Match Match = EmulatorSerial.Match(serial);
            if (Match.Success && int.TryParse(Match.Groups[1].Value, out int ConsolePort))
                return $"127.0.0.1:{ConsolePort + 1}";

            return null;
        }

        internal static string QuoteShell(string value) => "'" + (value ?? string.Empty).Replace("'", "'\"'\"'") + "'";

        private async Task<AdbCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ProcessStartInfo Start = new ProcessStartInfo(AdbPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string Argument in arguments) Start.ArgumentList.Add(Argument);

            using Process process = new Process { StartInfo = Start };
            process.Start();

            Task<string> Output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> Error = process.StandardError.ReadToEndAsync(cancellationToken);
            bool Exited = await WaitForExitAsync(process, timeout, cancellationToken).ConfigureAwait(false);

            if (!Exited)
            {
                try { process.Kill(true); } catch { }
            }

            return new AdbCommandResult
            {
                ExitCode = Exited ? process.ExitCode : -1,
                TimedOut = !Exited,
                StandardOutput = await SafeAwait(Output).ConfigureAwait(false),
                StandardError = await SafeAwait(Error).ConfigureAwait(false)
            };
        }

        private async Task RunWithoutRedirectAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ProcessStartInfo Start = new ProcessStartInfo(AdbPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };

            foreach (string Argument in arguments) Start.ArgumentList.Add(Argument);

            using Process process = Process.Start(Start);
            if (process == null) return;

            if (!await WaitForExitAsync(process, timeout, cancellationToken).ConfigureAwait(false))
            {
                try { process.Kill(true); } catch { }
            }
        }

        private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Task Wait = process.WaitForExitAsync(cancellationToken);
            Task Winner = await Task.WhenAny(Wait, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

            if (Winner != Wait) return false;

            await Wait.ConfigureAwait(false);
            return true;
        }

        private static async Task<string> SafeAwait(Task<string> task)
        {
            try { return await task.ConfigureAwait(false); }
            catch { return string.Empty; }
        }
    }
}

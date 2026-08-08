using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Starts RobloxPlayerBeta.exe directly instead of going through the roblox-player: protocol.
    ///
    /// Why it matters: Process.Start on a "roblox-player:1+..." string is a ShellExecute, so the child is spawned
    /// by the shell and inherits the shell's environment — a per-instance http_proxy set on our ProcessStartInfo
    /// never reaches the client, and we never learn the client's PID. Launching the executable ourselves fixes
    /// both, and costs nothing in fidelity: the protocol is registered as `RobloxPlayerBeta.exe "%1"`, so passing
    /// the same URI as argv produces a byte-identical command line to what the shell would have produced.
    /// </summary>
    internal static class ClientLauncher
    {
        private static string CachedExecutable;

        /// <summary>
        /// Recovers the browser tracker id from a client's command line — the only link between a running client
        /// and the account that launched it.
        ///
        /// Both shapes have to be accepted. When Roblox's launcher was a separate bootstrapper it re-spawned the
        /// player with flags (`-b 123`), and every tracker-based feature here was written against that. Today the
        /// roblox-player: protocol is registered directly to RobloxPlayerBeta.exe, so the client's command line is
        /// the launch URI itself and the id only appears as `browsertrackerid:123`. Matching just the flag form
        /// means the manager silently stops recognising its own clients.
        /// </summary>
        public static readonly Regex TrackerRegex = new Regex(@"(?:-b |browsertrackerid:)(\d+)", RegexOptions.Compiled);

        public static string TrackerOf(string CommandLine)
        {
            if (string.IsNullOrEmpty(CommandLine)) return string.Empty;

            Match Found = TrackerRegex.Match(CommandLine);

            return Found.Success ? Found.Groups[1].Value : string.Empty;
        }

        /// <summary>True when this command line belongs to a real game client (not Roblox's helper process).</summary>
        public static bool IsClientCommandLine(string CommandLine)
        {
            if (string.IsNullOrEmpty(CommandLine)) return false;

            return CommandLine.Contains("-t ") || CommandLine.Contains("-j ") || CommandLine.Contains("roblox-player:");
        }

        /// <summary>Environment variables the client's HTTP stack reads for its proxy configuration.</summary>
        private static readonly string[] ProxyVariables = { "http_proxy", "https_proxy", "all_proxy", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY" };

        /// <summary>
        /// Locates the player executable. The registered protocol handler is the most reliable source — it is
        /// whichever build Roblox itself would have launched — with a newest-folder scan as the fallback.
        /// </summary>
        public static string FindPlayerExecutable()
        {
            if (!string.IsNullOrEmpty(CachedExecutable) && File.Exists(CachedExecutable)) return CachedExecutable;

            string FromRegistry = ExecutableFromProtocol();

            if (!string.IsNullOrEmpty(FromRegistry))
            {
                CachedExecutable = FromRegistry;
                return CachedExecutable;
            }

            List<string> Roots = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
                @"C:\Program Files (x86)\Roblox\Versions"
            };

            // The version Roblox's own client-version endpoint reports wins if it is actually installed.
            if (!string.IsNullOrEmpty(AccountManager.CurrentVersion))
                foreach (string Root in Roots)
                {
                    string Candidate = Path.Combine(Root, AccountManager.CurrentVersion, "RobloxPlayerBeta.exe");

                    if (File.Exists(Candidate))
                    {
                        CachedExecutable = Candidate;
                        return CachedExecutable;
                    }
                }

            FileInfo Newest = null;

            foreach (string Root in Roots)
            {
                if (!Directory.Exists(Root)) continue;

                foreach (string VersionDirectory in Directory.GetDirectories(Root))
                {
                    string Candidate = Path.Combine(VersionDirectory, "RobloxPlayerBeta.exe");

                    if (!File.Exists(Candidate)) continue;

                    FileInfo Info = new FileInfo(Candidate);

                    if (Newest == null || Info.LastWriteTimeUtc > Newest.LastWriteTimeUtc) Newest = Info;
                }
            }

            CachedExecutable = Newest?.FullName;

            return CachedExecutable;
        }

        public static void ForgetExecutable() => CachedExecutable = null;

        private static string ExecutableFromProtocol()
        {
            foreach (RegistryKey Root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (RegistryKey Key = Root.OpenSubKey(@"SOFTWARE\Classes\roblox-player\shell\open\command"))
                    {
                        if (!(Key?.GetValue(null) is string Command) || string.IsNullOrWhiteSpace(Command)) continue;

                        Match(Command, out string Path);

                        // A third-party bootstrapper (Bloxstrap and friends) can own this protocol. Launching it
                        // directly with a URI works, but it would re-spawn the client as ITS own child, so our
                        // environment and PID would be lost again — the two things this path exists for.
                        if (string.IsNullOrEmpty(Path) || !Path.EndsWith("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(Path))
                                Program.Logger.Warn($"[Launcher] roblox-player: is handled by {Path}; falling back to a direct RobloxPlayerBeta.exe lookup");

                            continue;
                        }

                        if (File.Exists(Path)) return Path;
                    }
                }
                catch (Exception x) { Program.Logger.Warn($"[Launcher] failed reading protocol handler: {x.Message}"); }
            }

            return null;
        }

        private static void Match(string Command, out string Path)
        {
            Path = null;

            string Trimmed = Command.Trim();

            if (Trimmed.StartsWith("\""))
            {
                int End = Trimmed.IndexOf('"', 1);

                if (End > 1) Path = Trimmed.Substring(1, End - 1);
            }
            else
            {
                int Space = Trimmed.IndexOf(' ');

                Path = Space > 0 ? Trimmed.Substring(0, Space) : Trimmed;
            }
        }

        /// <summary>
        /// Launches the client. <paramref name="ProxyUrl"/>, when set, is exported to the child as
        /// http_proxy/https_proxy/all_proxy. <paramref name="Desktop"/>, when set, starts the client on a separate
        /// named desktop (its windows then exist nowhere on the visible desktop).
        /// </summary>
        public static Process Launch(IReadOnlyList<string> Arguments, string ProxyUrl, string Desktop, out string Error)
        {
            Error = null;

            string Executable = FindPlayerExecutable();

            if (string.IsNullOrEmpty(Executable))
            {
                Error = "Failed to find RobloxPlayerBeta.exe — is Roblox installed?";
                return null;
            }

            if (!string.IsNullOrEmpty(Desktop))
            {
                Process OnDesktop = HiddenDesktop.Launch(Executable, Arguments, ProxyUrl, Desktop, out string DesktopError);

                if (OnDesktop != null)
                {
                    // Its windows exist, just not on a desktop we can enumerate — tell the reaper before it
                    // concludes this client is a corpse.
                    ZombieReaper.MarkOffscreen(OnDesktop.Id);

                    return OnDesktop;
                }

                // Falling back to the visible desktop beats not launching at all; the reason is logged and surfaced.
                Program.Logger.Warn($"[Launcher] named-desktop launch failed ({DesktopError}); falling back to the normal desktop");
            }

            try
            {
                ProcessStartInfo StartInfo = new ProcessStartInfo(Executable)
                {
                    UseShellExecute = false, // required for environment variables to be inherited by the child
                    WorkingDirectory = Path.GetDirectoryName(Executable)
                };

                foreach (string Argument in Arguments)
                    StartInfo.ArgumentList.Add(Argument);

                ApplyProxyEnvironment(StartInfo.Environment, ProxyUrl);

                return Process.Start(StartInfo);
            }
            catch (Exception x)
            {
                Error = x.Message;
                Program.Logger.Error($"[Launcher] direct launch failed: {x}");

                return null;
            }
        }

        /// <summary>Quotes an argument the way CommandLineToArgvW parses it back.</summary>
        public static string Quote(string Argument)
        {
            if (!string.IsNullOrEmpty(Argument) && Argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return Argument;

            return "\"" + (Argument ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        public static void ApplyProxyEnvironment(IDictionary<string, string> Environment, string ProxyUrl)
        {
            if (string.IsNullOrEmpty(ProxyUrl)) return;

            foreach (string Variable in ProxyVariables)
                Environment[Variable] = ProxyUrl;

            // Without this the client would try to reach its own loopback slot proxy through the proxy.
            Environment["no_proxy"] = "127.0.0.1,localhost";
            Environment["NO_PROXY"] = "127.0.0.1,localhost";
        }
    }

    /// <summary>
    /// Optional: run clients on a separate named desktop.
    ///
    /// A window station can hold several desktops; only one is rendered. Processes started on a non-visible
    /// desktop draw normally (they are not throttled the way a minimized window can be) but nothing of theirs
    /// appears on screen. Useful when twenty clients are farming and none of them need to be looked at.
    ///
    /// Two consequences, both unavoidable: window titles set from this process cannot be seen there (the title
    /// is still set, there is simply nothing to look at), and RAM's window-position features have nothing to
    /// position. Screen capture of those windows requires PrintWindow, not a screen grab.
    /// </summary>
    internal static class HiddenDesktop
    {
        #region interop

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktopW(string lpszDesktop, string lpszDevice, IntPtr pDevmode, int dwFlags, uint dwDesiredAccess, IntPtr lpsa);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        private const uint GENERIC_ALL = 0x10000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        #endregion

        private static readonly Dictionary<string, IntPtr> Desktops = new Dictionary<string, IntPtr>();
        private static readonly object DesktopsLock = new object();

        /// <summary>Creates the desktop once and keeps the handle: closing it would destroy the desktop under the running clients.</summary>
        private static bool EnsureDesktop(string Name, out string Error)
        {
            Error = null;

            lock (DesktopsLock)
            {
                if (Desktops.ContainsKey(Name)) return true;

                IntPtr Handle = CreateDesktopW(Name, null, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);

                if (Handle == IntPtr.Zero)
                {
                    Error = $"CreateDesktop failed (Win32 {Marshal.GetLastWin32Error()})";
                    return false;
                }

                Desktops[Name] = Handle;

                Program.Logger.Info($"[HiddenDesktop] created desktop \"{Name}\"");

                return true;
            }
        }

        public static Process Launch(string Executable, IReadOnlyList<string> Arguments, string ProxyUrl, string Desktop, out string Error)
        {
            if (!EnsureDesktop(Desktop, out Error)) return null;

            StringBuilder CommandLine = new StringBuilder(ClientLauncher.Quote(Executable));

            foreach (string Argument in Arguments)
                CommandLine.Append(' ').Append(ClientLauncher.Quote(Argument));

            STARTUPINFO StartupInfo = new STARTUPINFO { lpDesktop = Desktop };
            StartupInfo.cb = Marshal.SizeOf(StartupInfo);

            IntPtr EnvironmentBlock = IntPtr.Zero;

            try
            {
                EnvironmentBlock = BuildEnvironmentBlock(ProxyUrl);

                if (!CreateProcessW(Executable, CommandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT,
                        EnvironmentBlock, Path.GetDirectoryName(Executable), ref StartupInfo, out PROCESS_INFORMATION Info))
                {
                    Error = $"CreateProcess failed (Win32 {Marshal.GetLastWin32Error()})";
                    return null;
                }

                try { return Process.GetProcessById(Info.dwProcessId); }
                catch (Exception x)
                {
                    Error = $"process started ({Info.dwProcessId}) but could not be opened: {x.Message}";
                    return null;
                }
                finally
                {
                    CloseHandle(Info.hThread);
                    CloseHandle(Info.hProcess);
                }
            }
            catch (Exception x)
            {
                Error = x.Message;
                return null;
            }
            finally { if (EnvironmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(EnvironmentBlock); }
        }

        /// <summary>
        /// Builds a CREATE_UNICODE_ENVIRONMENT block: our own environment plus the proxy variables, as
        /// "name=value\0name=value\0\0". Case-insensitively sorted, the way Windows expects it.
        /// </summary>
        private static IntPtr BuildEnvironmentBlock(string ProxyUrl)
        {
            Dictionary<string, string> Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (System.Collections.DictionaryEntry Entry in Environment.GetEnvironmentVariables())
                if (Entry.Key is string Key && Entry.Value is string Value) Variables[Key] = Value;

            ClientLauncher.ApplyProxyEnvironment(Variables, ProxyUrl);

            StringBuilder Block = new StringBuilder();

            foreach (KeyValuePair<string, string> Pair in Variables.OrderBy(Pair => Pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(Pair.Key) || Pair.Key.StartsWith("=")) continue; // the hidden per-drive "=C:" entries

                Block.Append(Pair.Key).Append('=').Append(Pair.Value).Append('\0');
            }

            Block.Append('\0');

            return Marshal.StringToHGlobalUni(Block.ToString());
        }
    }
}

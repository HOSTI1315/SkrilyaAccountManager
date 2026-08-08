using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Timers;

namespace RBX_Alt_Manager.Classes
{
    internal class RobloxWatcher
    {
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static readonly string HandlePath = Path.Combine(Environment.CurrentDirectory, "handle.bin");

        public static HashSet<int> Seen = new HashSet<int>();
        public static List<RobloxProcess> Instances = new List<RobloxProcess>();
        // Seen/Instances are mutated from CheckProcesses (UI timer) and from each RobloxProcess's own
        // exit timer (ThreadPool). HashSet/List aren't thread-safe, so every access must take this lock.
        internal static readonly object CollectionsLock = new object();
        public static bool VerifyDataModel = true;
        public static bool IgnoreExistingProcesses = true;
        public static bool CloseIfMemoryLow = false;
        public static bool CloseIfWindowTitle = false;
        public static bool RememberWindowPositions = false;
        public static int MemoryLowValue = 200;
        public static string ExpectedWindowTitle = "Roblox";

        public static Timer ReadTimer
        {
            get
            {
                if (readTimer == null)
                {
                    readTimer = new Timer(250);
                    readTimer.Elapsed += (s, e) => LogFileRead?.Invoke(null, new EventArgs());
                }

                return readTimer;
            }
        }
        private static Timer readTimer;

        public static event EventHandler<EventArgs> LogFileRead;

        public static void CheckProcesses()
        {
            IntPtr Focused = GetForegroundWindow();

            foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                bool keep = false; // whether ownership of this Process handle passes to a RobloxProcess instance

                try
                {
                    if (process.MainWindowHandle == Focused) continue; // Entirely ignore focused windows

                    void Kill(string Reason) { Program.Logger.Info($"Attempting to kill process {process.Id}, reason: {Reason}"); try { process.Kill(); } catch { } }

                    string CommandLine = process.GetCommandLine();

                    // This ignores the second roblox process which would cause 268 (Unexpected client behavior) kicks if it were closed.
                    if (string.IsNullOrEmpty(CommandLine)) continue; // Roblox's second process
                    if (CommandLine.StartsWith("\\??\\")) continue; // Roblox's second process

                    // Was this process launched with an authentication ticket + join script? Both shapes count:
                    // the old `-t/-j` flags, and the `roblox-player:` URI the client is handed today.
                    if (!ClientLauncher.IsClientCommandLine(CommandLine)) continue;

                    try
                    {
                        if ((DateTime.Now - process.StartTime).TotalSeconds > 30) // Roblox shouldn't take that long to startup, right? Surely nobody will be using a potato with these settings.
                        {
                            if (CloseIfMemoryLow && process.WorkingSet64 / 1024 / 1024 < MemoryLowValue)
                                Kill($"Low Memory ({process.WorkingSet64 / 1024 / 1024} < {MemoryLowValue})");

                            // Skip this rule for clients we renamed ourselves — otherwise turning on per-account
                            // window titles hands every client to the killer 30 seconds after it launches.
                            if (CloseIfWindowTitle && process.MainWindowTitle != ExpectedWindowTitle && !ClientWindows.IsOurTitle(process.Id, process.MainWindowTitle))
                                Kill($"Window Title isn't {ExpectedWindowTitle}, got {process.MainWindowTitle}");
                        }
                    }
                    catch (Exception x) { Program.Logger.Error($"Error with checking for Memory & Window Title: {x.Message}\n{x.StackTrace}"); }

                    if (RememberWindowPositions && (DateTime.Now - process.StartTime).TotalSeconds > 30)
                    {
                        string TrackerID = ClientLauncher.TrackerOf(CommandLine);

                        // AccountsList is mutated by background import/webserver threads (which take AccountsLock);
                        // enumerate it under the same lock so this off-writer read can't hit "Collection was modified".
                        Account account;
                        lock (AccountManager.AccountsLock)
                            account = AccountManager.AccountsList.FirstOrDefault(Account => Account.BrowserTrackerID == TrackerID);

                        if (account != null)
                            try
                            {
                                GetWindowRect(process.MainWindowHandle, out RECT rect);

                                account.SetField("Window_Position_X", $"{rect.Left:0}");
                                account.SetField("Window_Position_Y", $"{rect.Top:0}");
                                account.SetField("Window_Width", $"{rect.Right - rect.Left:0}");
                                account.SetField("Window_Height", $"{rect.Bottom - rect.Top:0}");
                            }
                            catch { }
                    }

                    bool alreadySeen;
                    lock (CollectionsLock) alreadySeen = Seen.Contains(process.Id);
                    if (alreadySeen) continue;

                    try
                    {
                        if (process.HasExited) continue; // Will throw an exception if we have no access, wrapped in a try-catch to ignore Roblox's second process which is ran with elevated permissions

                        RobloxProcess rp = new RobloxProcess(process);

                        lock (CollectionsLock)
                        {
                            Instances.Add(rp);
                            Seen.Add(process.Id);
                        }

                        keep = true;
                    }
                    catch (Exception x) { Program.Logger.Error($"Access to Process {process.Id} denied! This may be due to roblox being ran as admin or roblox's second process(This message can be ignored): {x.Message}"); }
                }
                finally { if (!keep) process.Dispose(); } // release handles of processes we don't track (were leaking each tick)
            }
        }

        public static bool IsHandleEulaAccepted()
        {
            RegistryKey AcceptedHEULA = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Sysinternals\Handle");
            object EulaObject = AcceptedHEULA?.GetValue("EulaAccepted");

            return AcceptedHEULA != null && EulaObject != null && int.TryParse(EulaObject.ToString(), out int EULA) && EULA == 1;
        }
    }
}
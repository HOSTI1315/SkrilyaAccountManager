using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Per-client window bookkeeping: renaming each client's window after the account running in it, and
    /// closing the Win32 error modals Roblox leaves on screen when a launch fails.
    ///
    /// Renaming is purely cosmetic from Roblox's point of view — SetWindowText writes to the window's title
    /// text, nothing in the process identity changes — but with fifteen windows called "Roblox" it is the
    /// difference between usable and not. The client rewrites the title itself on some transitions
    /// (loading -> in-game, going full screen), so the wanted title is re-applied on a timer.
    /// </summary>
    internal static class ClientWindows
    {
        #region interop

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CLOSE = 0x0010;

        #endregion

        private class TitleEntry
        {
            public string Title;
            public DateTime Added;
        }

        private static readonly ConcurrentDictionary<int, TitleEntry> Wanted = new ConcurrentDictionary<int, TitleEntry>();
        private static System.Timers.Timer KeepAlive;
        private static readonly object TimerLock = new object();

        /// <summary>Process names whose stray Win32 dialogs the reaper is allowed to close.</summary>
        private static readonly string[] ReapableProcesses = { "RobloxPlayerBeta", "RobloxPlayerLauncher", "RobloxPlayerInstaller" };

        public static bool ReapDialogs;

        public static IEnumerable<IntPtr> WindowsOf(int ProcessId, bool VisibleOnly = true)
        {
            List<IntPtr> Handles = new List<IntPtr>();

            EnumWindows((hWnd, _) =>
            {
                if (GetWindowThreadProcessId(hWnd, out int Owner) != 0 && Owner == ProcessId && (!VisibleOnly || IsWindowVisible(hWnd)))
                    Handles.Add(hWnd);

                return true;
            }, IntPtr.Zero);

            return Handles;
        }

        /// <summary>
        /// Every process that owns at least one visible, titled top-level window on this desktop.
        ///
        /// "Titled" matters: a crashed client can leave an invisible zero-size shell behind, and a splash or
        /// tooltip window is not evidence that anyone is playing. One enumeration answers the question for all
        /// processes at once.
        /// </summary>
        public static IEnumerable<int> ProcessesWithVisibleWindows()
        {
            HashSet<int> Owners = new HashSet<int>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (GetWindowThreadProcessId(hWnd, out int Owner) == 0 || Owner == 0) return true;
                if (Owners.Contains(Owner)) return true;
                if (string.IsNullOrWhiteSpace(TitleOf(hWnd))) return true;

                Owners.Add(Owner);

                return true;
            }, IntPtr.Zero);

            return Owners;
        }

        public static string TitleOf(IntPtr Window)
        {
            StringBuilder Builder = new StringBuilder(512);

            return GetWindowTextW(Window, Builder, Builder.Capacity) > 0 ? Builder.ToString() : string.Empty;
        }

        public static string ClassOf(IntPtr Window)
        {
            StringBuilder Builder = new StringBuilder(256);

            return GetClassNameW(Window, Builder, Builder.Capacity) > 0 ? Builder.ToString() : string.Empty;
        }

        /// <summary>
        /// Renders the configured title template for an account. Placeholders: {username} {alias} {name}
        /// {userid} {place} {pid}. An empty template disables renaming for that launch.
        /// </summary>
        public static string FormatTitle(string Template, Account account, long PlaceId, int ProcessId)
        {
            if (string.IsNullOrWhiteSpace(Template) || account == null) return string.Empty;

            string Name = !string.IsNullOrEmpty(account.Alias) ? account.Alias : account.Username;

            return Template
                .Replace("{username}", account.Username ?? string.Empty)
                .Replace("{alias}", account.Alias ?? string.Empty)
                .Replace("{name}", Name ?? string.Empty)
                .Replace("{userid}", account.UserID.ToString())
                .Replace("{place}", PlaceId > 0 ? PlaceId.ToString() : string.Empty)
                .Replace("{pid}", ProcessId.ToString())
                .Trim();
        }

        /// <summary>Registers a wanted title for a client process; it is applied as soon as a window appears and kept applied.</summary>
        public static void Track(int ProcessId, string Title)
        {
            if (ProcessId <= 0 || string.IsNullOrWhiteSpace(Title)) return;

            Wanted[ProcessId] = new TitleEntry { Title = Title, Added = DateTime.Now };

            EnsureTimer();
        }

        public static void Untrack(int ProcessId) => Wanted.TryRemove(ProcessId, out _);

        /// <summary>True when this title is the one we asked for on this process — so title-based kill rules can skip it.</summary>
        public static bool IsOurTitle(int ProcessId, string Title) => Wanted.TryGetValue(ProcessId, out TitleEntry Entry) && Entry.Title == Title;

        /// <summary>True when any title is being maintained for this process.</summary>
        public static bool IsTracked(int ProcessId) => Wanted.ContainsKey(ProcessId);

        /// <summary>Starts the shared maintenance timer. One timer serves every tracked client.</summary>
        public static void EnsureTimer()
        {
            lock (TimerLock)
            {
                if (KeepAlive != null) return;

                KeepAlive = new System.Timers.Timer(2000) { AutoReset = true };
                KeepAlive.Elapsed += (s, e) =>
                {
                    try { Tick(); }
                    catch (Exception x) { Program.Logger.Error($"[ClientWindows] tick failed: {x.Message}"); }
                };
                KeepAlive.Start();
            }
        }

        public static void StopTimer()
        {
            lock (TimerLock)
            {
                KeepAlive?.Stop();
                KeepAlive?.Dispose();
                KeepAlive = null;
            }
        }

        private static void Tick()
        {
            // One enumeration of the whole desktop per tick, then dispatch — enumerating separately per tracked
            // client meant a full sweep per client every two seconds.
            Dictionary<int, List<IntPtr>> ByProcess = new Dictionary<int, List<IntPtr>>();

            EnumWindows((hWnd, _) =>
            {
                if (GetWindowThreadProcessId(hWnd, out int Owner) == 0 || Owner == 0) return true;

                if (!ByProcess.TryGetValue(Owner, out List<IntPtr> Handles)) ByProcess[Owner] = Handles = new List<IntPtr>();

                Handles.Add(hWnd);

                return true;
            }, IntPtr.Zero);

            foreach (KeyValuePair<int, TitleEntry> Pair in Wanted)
            {
                if (!ByProcess.TryGetValue(Pair.Key, out List<IntPtr> Handles))
                {
                    // No windows: either the client has not drawn one yet, or it is gone. Only drop the entry
                    // once the process itself is actually dead.
                    if (!IsAlive(Pair.Key)) Wanted.TryRemove(Pair.Key, out _);

                    continue;
                }

                foreach (IntPtr Window in Handles)
                {
                    if (!IsWindowVisible(Window)) continue;

                    // Only touch the client's own window; message boxes and tooltips have their own classes.
                    if (ClassOf(Window) == "#32770") continue;

                    if (TitleOf(Window) != Pair.Value.Title)
                        SetWindowTextW(Window, Pair.Value.Title);
                }
            }

            if (ReapDialogs) ReapErrorDialogs(ByProcess);
        }

        private static bool IsAlive(int ProcessId)
        {
            try
            {
                using (Process proc = Process.GetProcessById(ProcessId))
                    return proc != null && !proc.HasExited;
            }
            catch { return false; }
        }

        /// <summary>
        /// Closes Win32 message boxes owned by Roblox processes. On a rate-limited or ticket-rejected launch the
        /// client leaves a modal up that blocks nothing but stacks up across a mass launch and holds the process
        /// alive, so nothing else notices the launch failed.
        /// </summary>
        public static int ReapErrorDialogs(Dictionary<int, List<IntPtr>> ByProcess = null)
        {
            int Closed = 0;

            foreach (string Name in ReapableProcesses)
            {
                Process[] Processes;

                try { Processes = Process.GetProcessesByName(Name); }
                catch { continue; }

                foreach (Process proc in Processes)
                {
                    try
                    {
                        IEnumerable<IntPtr> Handles = ByProcess != null
                            ? (ByProcess.TryGetValue(proc.Id, out List<IntPtr> Known) ? (IEnumerable<IntPtr>)Known : new IntPtr[0])
                            : WindowsOf(proc.Id, VisibleOnly: false);

                        foreach (IntPtr Window in Handles)
                        {
                            if (ClassOf(Window) != "#32770") continue; // the Win32 dialog class — never the game window

                            Program.Logger.Info($"[ClientWindows] closing dialog \"{TitleOf(Window)}\" of {Name} ({proc.Id})");

                            PostMessageW(Window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                            Closed++;
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }

            return Closed;
        }
    }
}

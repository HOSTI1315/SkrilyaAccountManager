using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Keeps running Roblox clients active without stealing the user's focus.  A background timer posts one
    /// key-down/key-up pair directly to every Roblox window; unlike SendInput this works without activating the
    /// client and therefore does not interrupt whatever the user is doing on the desktop.
    ///
    /// PostMessage is intentionally best-effort.  Games that implement their own raw-input stack can ignore
    /// synthetic window messages, so this feature is a convenience rather than a promise that every experience
    /// can be kept alive by the manager.
    /// </summary>
    internal static class AntiAfk
    {
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        private static readonly object TimerLock = new object();
        private static System.Threading.Timer Timer;
        private static int Running;

        public static bool Enabled { get; private set; }
        public static int IntervalSeconds { get; private set; } = 300;
        public static int InterWindowDelayMs { get; private set; } = 150;
        public static Keys Key { get; private set; } = Keys.Space;

        /// <summary>Starts the single process-wide timer and applies the current settings.</summary>
        public static void Start()
        {
            lock (TimerLock)
            {
                Timer ??= new System.Threading.Timer(_ => _ = Tick(), null, Timeout.Infinite, Timeout.Infinite);
            }

            LoadSettings();
        }

        /// <summary>Re-reads settings. Safe to call after a settings-screen edit.</summary>
        public static void LoadSettings()
        {
            Enabled = Bool("AfkEnabled", false);
            IntervalSeconds = Int("AfkIntervalSeconds", 300, 60, 24 * 60 * 60);
            InterWindowDelayMs = Int("AfkInterWindowDelayMs", 150, 0, 5000);

            string RawKey = Text("AfkKey", "Space");
            Key = Enum.TryParse(RawKey, true, out Keys Parsed) && Parsed != Keys.None ? Parsed : Keys.Space;

            lock (TimerLock)
            {
                if (Timer == null) return;

                int Due = Enabled ? IntervalSeconds * 1000 : Timeout.Infinite;
                Timer.Change(Due, Due);
            }

            Program.Logger.Info(Enabled
                ? $"[AntiAfk] on — {Key} every {IntervalSeconds}s"
                : "[AntiAfk] off");
        }

        /// <summary>Runs one pass now; also useful from the UI for a quick smoke test.</summary>
        public static async Task<int> Pulse()
        {
            int Sent = 0;
            Process[] Processes = Process.GetProcessesByName("RobloxPlayerBeta");

            try
            {
                foreach (Process Process in Processes)
                {
                    try
                    {
                        if (Process.HasExited) continue;

                        IntPtr Window = Process.MainWindowHandle;
                        if (Window == IntPtr.Zero) continue;

                        int VirtualKey = (int)Key;
                        Utilities.PostMessage(Window, WM_KEYDOWN, VirtualKey, 1);
                        Utilities.PostMessage(Window, WM_KEYUP, VirtualKey, unchecked((int)0xC0000001));
                        Sent++;

                        if (InterWindowDelayMs > 0) await Task.Delay(InterWindowDelayMs).ConfigureAwait(false);
                    }
                    catch (Exception x)
                    {
                        Program.Logger.Warn($"[AntiAfk] PID {SafePid(Process)}: {x.Message}");
                    }
                }
            }
            finally
            {
                foreach (Process Process in Processes) Process.Dispose();
            }

            return Sent;
        }

        private static async Task Tick()
        {
            if (!Enabled || Interlocked.Exchange(ref Running, 1) != 0) return;

            try
            {
                int Sent = await Pulse().ConfigureAwait(false);
                if (Sent > 0) Program.Logger.Info($"[AntiAfk] sent {Key} to {Sent} client(s)");
            }
            catch (Exception x) { Program.Logger.Error($"[AntiAfk] {x.Message}"); }
            finally { Volatile.Write(ref Running, 0); }
        }

        private static int SafePid(Process Process)
        {
            try { return Process.Id; }
            catch { return 0; }
        }

        private static bool Bool(string Name, bool Default)
        {
            try { return AccountManager.General.Exists(Name) && bool.TryParse(AccountManager.General.Get(Name), out bool Value) ? Value : Default; }
            catch { return Default; }
        }

        private static int Int(string Name, int Default, int Min, int Max)
        {
            try { return AccountManager.General.Exists(Name) && int.TryParse(AccountManager.General.Get(Name), out int Value) ? Math.Max(Min, Math.Min(Max, Value)) : Default; }
            catch { return Default; }
        }

        private static string Text(string Name, string Default)
        {
            try { return AccountManager.General.Exists(Name) ? (AccountManager.General.Get(Name) ?? Default) : Default; }
            catch { return Default; }
        }
    }
}

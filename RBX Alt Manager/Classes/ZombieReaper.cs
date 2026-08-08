using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Kills Roblox clients that are dead but still running.
    ///
    /// When a client crashes, its window goes but the process does not: it sits there holding several hundred
    /// megabytes and a slot in the account's session, doing nothing, until the machine is rebooted. One is a
    /// curiosity; with multi-instance launching they pile up, and the first symptom is usually the machine
    /// running out of memory hours later.
    ///
    /// The rule is deliberately narrow, because getting it wrong kills a client someone is playing:
    ///   - the process is RobloxPlayerBeta,
    ///   - its command line is a real game launch (so Roblox's helper process is never touched),
    ///   - it owns no visible top-level window,
    ///   - and it has been alive longer than the grace period, so a client still drawing its first window is safe.
    ///
    /// Everything else is left alone. Nothing here guesses at "not responding" or memory thresholds — those
    /// belong to the watcher, and they have false positives this must not have.
    /// </summary>
    internal static class ZombieReaper
    {
        public static bool Enabled { get; private set; }
        public static bool DryRun { get; private set; }

        /// <summary>How long a process may live without a window before it counts as dead. Boot + login is slow on cold disks.</summary>
        public static int GraceSeconds { get; private set; } = 90;

        public static int IntervalSeconds { get; private set; } = 30;

        /// <summary>
        /// Roblox leaves a crash handler running per client; when the client is gone they pile up too, quieter
        /// and smaller than the clients themselves but just as pointless.
        /// </summary>
        public static bool KillOrphanCrashHandlers { get; private set; } = true;

        private static HashSet<int> Whitelist = new HashSet<int>();

        public static int KilledTotal;
        public static long FreedMegabytesTotal;

        private static System.Timers.Timer Timer;
        private static readonly object TimerLock = new object();

        /// <summary>
        /// Clients we deliberately put on another desktop. Their windows are real but live on a desktop this
        /// process cannot enumerate, so the no-window rule would call every one of them a corpse.
        /// </summary>
        private static readonly ConcurrentDictionary<int, byte> OffscreenClients = new ConcurrentDictionary<int, byte>();

        public static void MarkOffscreen(int ProcessId) => OffscreenClients[ProcessId] = 0;

        /// <summary>Raised for each kill: (processId, reason, megabytesFreed).</summary>
        public static event Action<int, string, long> Reaped;

        public static void LoadSettings()
        {
            Enabled = Bool("ReaperEnabled", false);
            DryRun = Bool("ReaperDryRun", false);
            GraceSeconds = Int("ReaperGraceSeconds", 90, 20, 3600);
            IntervalSeconds = Int("ReaperIntervalSeconds", 30, 5, 600);
            KillOrphanCrashHandlers = Bool("ReaperKillOrphanCrashHandlers", true);
            Whitelist = StuckDetector.ParseWhitelist();

            if (Enabled) Start(); else Stop();
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

        public static void Start()
        {
            lock (TimerLock)
            {
                if (Timer != null)
                {
                    Timer.Interval = IntervalSeconds * 1000;

                    return;
                }

                Timer = new System.Timers.Timer(IntervalSeconds * 1000) { AutoReset = true };
                Timer.Elapsed += (s, e) =>
                {
                    try { Sweep(); }
                    catch (Exception x) { Program.Logger.Error($"[Reaper] sweep failed: {x}"); }
                };
                Timer.Start();

                Program.Logger.Info($"[Reaper] on — every {IntervalSeconds}s, grace {GraceSeconds}s{(DryRun ? ", dry run" : string.Empty)}");
            }
        }

        public static void Stop()
        {
            lock (TimerLock)
            {
                if (Timer == null) return;

                Timer.Stop();
                Timer.Dispose();
                Timer = null;

                Program.Logger.Info("[Reaper] off");
            }
        }

        /// <summary>One pass. Returns what it killed (or would have killed, in dry run).</summary>
        public static List<string> Sweep()
        {
            List<string> Killed = new List<string>();

            // One window enumeration for the whole sweep: a per-process EnumWindows would walk the desktop once
            // per client, and this runs on a timer forever.
            HashSet<int> WithWindows = new HashSet<int>(ClientWindows.ProcessesWithVisibleWindows());

            foreach (Process proc in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                using (proc)
                {
                    try
                    {
                        if (WithWindows.Contains(proc.Id)) continue;
                        if (Whitelist.Contains(proc.Id)) continue;           // the user said never
                        if (OffscreenClients.ContainsKey(proc.Id)) continue; // its windows are on another desktop

                        // Roblox's helper process has no command line (or the \??\ form) and killing it earns the
                        // account a "268 unexpected client behavior" kick. Only real game clients are candidates.
                        string CommandLine = proc.GetCommandLine();

                        if (string.IsNullOrEmpty(CommandLine) || CommandLine.StartsWith("\\??\\")) continue;
                        if (!ClientLauncher.IsClientCommandLine(CommandLine)) continue;

                        double Age = (DateTime.Now - proc.StartTime).TotalSeconds;

                        if (Age < GraceSeconds) continue; // still booting: no window yet is normal

                        long Megabytes = proc.WorkingSet64 / 1024 / 1024;
                        string Tracker = ClientLauncher.TrackerOf(CommandLine);
                        string Who = OwnerOf(Tracker) ?? $"tracker {Tracker}";

                        string Line = $"pid {proc.Id} ({Who}) — no window for {Age:0}s, {Megabytes} MB";

                        if (DryRun)
                        {
                            Program.Logger.Info($"[Reaper] would kill {Line}");

                            Killed.Add(Line);

                            continue;
                        }

                        proc.Kill();

                        KilledTotal++;
                        FreedMegabytesTotal += Megabytes;

                        Program.Logger.Info($"[Reaper] killed {Line}");

                        Killed.Add(Line);

                        try { Reaped?.Invoke(proc.Id, Who, Megabytes); }
                        catch (Exception x) { Program.Logger.Error($"[Reaper] handler threw: {x.Message}"); }
                    }
                    catch (InvalidOperationException) { } // exited between the listing and the check
                    catch (System.ComponentModel.Win32Exception x) { Program.Logger.Debug($"[Reaper] no access to {proc.Id}: {x.Message}"); }
                    catch (Exception x) { Program.Logger.Error($"[Reaper] {proc.Id}: {x.Message}"); }
                }
            }

            if (KillOrphanCrashHandlers) Killed.AddRange(SweepCrashHandlers());

            // Forget clients that are gone, so the exclusion set does not grow for the life of the app.
            foreach (int ProcessId in OffscreenClients.Keys.ToArray())
                try { using (Process.GetProcessById(ProcessId)) { } }
                catch { OffscreenClients.TryRemove(ProcessId, out _); }

            return Killed;
        }

        /// <summary>
        /// Closes crash handlers whose client is gone. Roblox starts one per client and it should exit with it;
        /// when it does not, it sits there holding a handle to a process that no longer exists.
        /// </summary>
        private static List<string> SweepCrashHandlers()
        {
            List<string> Killed = new List<string>();

            HashSet<int> LiveClients = new HashSet<int>();

            foreach (Process client in Process.GetProcessesByName("RobloxPlayerBeta"))
                using (client) LiveClients.Add(client.Id);

            if (LiveClients.Count > 0) return Killed; // a handler may belong to any of them; only sweep when none are left

            foreach (Process handler in Process.GetProcessesByName("RobloxCrashHandler"))
                using (handler)
                    try
                    {
                        if (Whitelist.Contains(handler.Id)) continue;
                        if ((DateTime.Now - handler.StartTime).TotalSeconds < GraceSeconds) continue;

                        long Megabytes = handler.WorkingSet64 / 1024 / 1024;

                        if (DryRun)
                        {
                            Program.Logger.Info($"[Reaper] would kill orphan crash handler pid {handler.Id}, {Megabytes} MB");
                            Killed.Add($"crash handler {handler.Id}");

                            continue;
                        }

                        handler.Kill();

                        KilledTotal++;
                        FreedMegabytesTotal += Megabytes;

                        Program.Logger.Info($"[Reaper] killed orphan crash handler pid {handler.Id}, {Megabytes} MB");

                        Killed.Add($"crash handler {handler.Id}");
                    }
                    catch (Exception x) { Program.Logger.Debug($"[Reaper] crash handler {handler.Id}: {x.Message}"); }

            return Killed;
        }

        private static string OwnerOf(string Tracker)
        {
            if (string.IsNullOrEmpty(Tracker)) return null;

            lock (AccountManager.AccountsLock)
                return AccountManager.AccountsList?.FirstOrDefault(account => account.BrowserTrackerID == Tracker)?.Username;
        }
    }
}

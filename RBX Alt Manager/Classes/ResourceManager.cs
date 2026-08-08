using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Windows-level, per-instance resource control for running many simultaneous Roblox clients.
    ///
    /// Why the OS and not FastFlags: since Roblox's FastFlag allowlist, the framerate-cap flag
    /// (DFIntTaskSchedulerTargetFps) and most others are silently ignored, and Roblox no longer auto-pauses
    /// minimized clients. That leaves the OS as the only real CPU/RAM lever:
    ///   * SetPriorityClass  — BelowNormal/Idle on background clients (never High/Realtime: those starve the OS)
    ///   * EmptyWorkingSet   — trim resident pages of idle/minimized clients (they fault back in on next use)
    ///   * ProcessorAffinity — optional CCD pinning (e.g. keep alts off the V-Cache CCD on X3D parts)
    ///
    /// Everything is opt-in via [General] Perf* keys and safe for single-client users: with dynamic priority the
    /// focused client always stays at Normal, so one lone client is never throttled. Best-effort throughout —
    /// Roblox's elevated second process and update races are skipped, never crash the manager.
    /// </summary>
    public static class ResourceManager
    {
        [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_MINIMIZE = 6;
        private const int JobObjectExtendedLimitInformation = 9;
        private const int JobObjectCpuRateControlInformation = 15;
        private const uint JOB_OBJECT_LIMIT_WORKINGSET = 0x00000001;
        private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;
        private const int ProcessMemoryPriority = 0;
        private const int ProcessPowerThrottling = 4;
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
        private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        // Shared matcher: recognises both `-b 123` and the modern `browsertrackerid:123` URI form. See ClientLauncher.
        private static readonly Regex TrackerRegex = ClientLauncher.TrackerRegex;

        private class Sample { public TimeSpan Cpu; public DateTime When; }

        private static readonly Dictionary<int, Sample> Samples = new Dictionary<int, Sample>();
        private static readonly object SamplesLock = new object();
        private static readonly Dictionary<int, IntPtr> Jobs = new Dictionary<int, IntPtr>();
        private static readonly object JobsLock = new object();

        public struct Stat { public double Cpu; public long RamMB; public bool Minimized; public int Pid; }

        // ---- setting accessors (never throw; safe defaults) ----
        private static string S(string Key, string Default) { try { var g = AccountManager.General; return g != null && g.Exists(Key) ? g.Get(Key) : Default; } catch { return Default; } }
        private static bool B(string Key, bool Default) { try { var g = AccountManager.General; return g != null && g.Exists(Key) ? g.Get<bool>(Key) : Default; } catch { return Default; } }
        private static int I(string Key, int Default, int Min, int Max)
        {
            try { return AccountManager.General != null && AccountManager.General.Exists(Key) && int.TryParse(AccountManager.General.Get(Key), out int Value) ? Math.Max(Min, Math.Min(Max, Value)) : Default; }
            catch { return Default; }
        }

        public static bool Enabled => B("PerfManage", true);

        private static ProcessPriorityClass BackgroundPriority()
        {
            switch (S("PerfBackgroundPriority", "below").ToLowerInvariant())
            {
                case "idle": return ProcessPriorityClass.Idle;
                case "normal": return ProcessPriorityClass.Normal;
                default: return ProcessPriorityClass.BelowNormal;
            }
        }

        /// <summary>
        /// Affinity mask for the chosen mode, or 0 to leave affinity alone. Derived from the logical-processor
        /// count so it works on any CPU. The CCD modes assume a symmetric two-CCD layout, which is what X3D
        /// parts look like: the V-Cache CCD is the low half of the logical processors, the high-frequency CCD
        /// the high half. 64-bit mask — the manager is an x64 process now, so all processors are addressable.
        /// </summary>
        private static long AffinityMask()
        {
            int n = Math.Min(64, Environment.ProcessorCount);

            if (n < 4) return 0;

            long All = (n >= 64) ? unchecked((long)0xFFFFFFFFFFFFFFFF) : ((1L << n) - 1);
            int Half = n / 2;
            long Low = (1L << Half) - 1;   // V-Cache CCD on X3D
            long High = All & ~Low;        // high-frequency CCD

            switch (S("PerfAffinityMode", "all").ToLowerInvariant())
            {
                case "vcache": return Low;
                case "nonvcache": return High;
                default: return 0;
            }
        }

        /// <summary>
        /// Maps each launched Roblox client to its owning account via the browser tracker id in its command line
        /// (the same trick the window-mover and close-client paths use). Returns tracker -> Process.
        /// </summary>
        private static Dictionary<string, Process> MapProcesses()
        {
            var Map = new Dictionary<string, Process>();

            foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
                try
                {
                    var m = TrackerRegex.Match(p.GetCommandLine() ?? "");

                    if (m.Success && !Map.ContainsKey(m.Groups[1].Value)) Map[m.Groups[1].Value] = p;
                }
                catch { }

            return Map;
        }

        private static void SetAffinity(Process p, long Mask)
        {
            try { p.ProcessorAffinity = (IntPtr)Mask; } catch { }
        }

        /// <summary>
        /// Fire-and-forget from JoinServer right after a launch. Waits for the client to appear, then applies
        /// affinity, an initial priority, and optional auto-minimize/trim. The poll loop takes over from there.
        /// </summary>
        public static async Task OnLaunched(Account acc)
        {
            if (!Enabled || acc == null || string.IsNullOrEmpty(acc.BrowserTrackerID)) return;

            try
            {
                DateTime Ends = DateTime.Now.AddSeconds(40);
                Process proc = null;

                while (DateTime.Now < Ends)
                {
                    await Task.Delay(600);

                    if (MapProcesses().TryGetValue(acc.BrowserTrackerID, out proc) && proc != null && !proc.HasExited) break;

                    proc = null;
                }

                if (proc == null) return;

                ApplyJobControls(proc);

                long Mask = AffinityMask(); if (Mask != 0) SetAffinity(proc, Mask);

                if (!B("PerfDynamicPriority", true))
                    try { proc.PriorityClass = BackgroundPriority(); } catch { }

                if (B("PerfAutoMinimizeAlts", false))
                    try { if (proc.MainWindowHandle != IntPtr.Zero) ShowWindow(proc.MainWindowHandle, SW_MINIMIZE); } catch { }

                if (B("PerfTrimOnMinimize", false))
                    try { await Task.Delay(2000); EmptyWorkingSet(proc.Handle); } catch { }
            }
            catch (Exception ex) { Program.Logger.Error($"[ResourceManager] OnLaunched: {ex.Message}"); }
        }

        /// <summary>
        /// Samples CPU/RAM per client, applies dynamic priority (focused client -> Normal, others -> the
        /// background class) and affinity, and returns per-tracker stats. Call on a ~3-4s timer: CPU percentage
        /// needs a couple of seconds between samples to mean anything.
        /// </summary>
        public static Dictionary<string, Stat> Poll()
        {
            var Output = new Dictionary<string, Stat>();

            if (!Enabled)
            {
                // Dropping our handles removes the opt-in Job limits without terminating Roblox: this manager
                // never sets JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. It also prevents stale handles if PerfManage is
                // switched off while clients are still alive.
                ReleaseDeadJobs(new HashSet<int>());
                lock (SamplesLock) Samples.Clear();
                return Output;
            }

            int ForegroundPid = 0; try { GetWindowThreadProcessId(GetForegroundWindow(), out ForegroundPid); } catch { }

            bool Dynamic = B("PerfDynamicPriority", true);
            ProcessPriorityClass Background = BackgroundPriority();
            long Affinity = AffinityMask();
            var Alive = new HashSet<int>();

            foreach (var kv in MapProcesses())
            {
                Process p = kv.Value;

                try
                {
                    if (p.HasExited) continue;

                    Alive.Add(p.Id);

                    double Cpu = 0;
                    TimeSpan Now = p.TotalProcessorTime;
                    DateTime At = DateTime.UtcNow;

                    lock (SamplesLock)
                    {
                        if (Samples.TryGetValue(p.Id, out Sample Previous))
                        {
                            double Wall = (At - Previous.When).TotalMilliseconds;

                            if (Wall > 0) Cpu = Math.Max(0, (Now - Previous.Cpu).TotalMilliseconds / Wall / Environment.ProcessorCount * 100.0);
                        }

                        Samples[p.Id] = new Sample { Cpu = Now, When = At };
                    }

                    bool Minimized = false;
                    try { Minimized = p.MainWindowHandle != IntPtr.Zero && IsIconic(p.MainWindowHandle); } catch { }

                    if (Dynamic) { try { p.PriorityClass = (p.Id == ForegroundPid) ? ProcessPriorityClass.Normal : Background; } catch { } }
                    if (Affinity != 0) SetAffinity(p, Affinity);

                    Output[kv.Key] = new Stat { Cpu = Math.Round(Cpu, 0), RamMB = p.WorkingSet64 / 1024 / 1024, Minimized = Minimized, Pid = p.Id };
                }
                catch { }
            }

            lock (SamplesLock)
                foreach (int Id in Samples.Keys.Where(k => !Alive.Contains(k)).ToList()) Samples.Remove(Id);

            ReleaseDeadJobs(Alive);

            return Output;
        }

        /// <summary>
        /// Applies the opt-in Job/process controls once to the real RobloxPlayerBeta resolved by tracker id.
        /// A separate Job Object per client makes a configured CPU 15% cap a true per-client hard cap. The
        /// working-set setting is deliberately different: it limits resident pages and may cause paging, but is
        /// not a commit/allocation ceiling. We never set KILL_ON_JOB_CLOSE, so closing the manager cannot close
        /// Roblox.
        /// </summary>
        private static void ApplyJobControls(Process Process)
        {
            try
            {
                if (Process == null || Process.HasExited) return;

                int MemoryPriority = MemoryPriorityValue(S("PerfMemoryPriority", "normal"));
                if (MemoryPriority != 5) SetMemoryPriority(Process, (uint)MemoryPriority);
                if (B("PerfPowerThrottle", false)) SetPowerThrottle(Process);

                if (!B("PerfJobEnabled", false)) return;

                int CpuPercent = I("PerfJobCpuPercent", 0, 0, 100);
                int MemoryMB = I("PerfJobMemoryMB", 0, 0, 1024 * 1024);
                if (CpuPercent > 0 && CpuPercent < 5) CpuPercent = 5;
                if (CpuPercent == 0 && MemoryMB == 0) return;

                lock (JobsLock)
                    if (Jobs.ContainsKey(Process.Id)) return;

                IntPtr Job = CreateJobObjectW(IntPtr.Zero, null);
                if (Job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

                try
                {
                    if (CpuPercent > 0)
                    {
                        var Cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                        {
                            ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                            CpuRate = (uint)(CpuPercent * 100)
                        };
                        if (!SetJobInfo(Job, JobObjectCpuRateControlInformation, Cpu))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CPU hard cap was rejected");
                    }

                    if (MemoryMB > 0)
                    {
                        var Limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                        Limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_WORKINGSET;
                        // Windows rejects a non-zero maximum with a zero minimum. One page is deliberately tiny:
                        // this setting is a ceiling, not a request to reserve a large resident minimum.
                        Limits.BasicLimitInformation.MinimumWorkingSetSize = (UIntPtr)4096UL;
                        Limits.BasicLimitInformation.MaximumWorkingSetSize = (UIntPtr)((ulong)MemoryMB * 1024UL * 1024UL);
                        if (!SetJobInfo(Job, JobObjectExtendedLimitInformation, Limits))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "working-set limit was rejected");
                    }

                    if (!AssignProcessToJobObject(Job, Process.Handle))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "client is already in an incompatible Job Object");

                    lock (JobsLock) Jobs[Process.Id] = Job;
                    Job = IntPtr.Zero; // ownership moved to Jobs
                    Program.Logger.Info($"[ResourceManager] PID {Process.Id}: job CPU={(CpuPercent > 0 ? CpuPercent + "%" : "unlimited")}, working-set={(MemoryMB > 0 ? MemoryMB + " MB" : "unlimited")}");
                }
                finally { if (Job != IntPtr.Zero) CloseHandle(Job); }
            }
            catch (Exception x) { Program.Logger.Warn($"[ResourceManager] hard limits: {x.Message}"); }
        }

        private static bool SetJobInfo<T>(IntPtr Job, int Class, T Value) where T : struct
        {
            int Size = Marshal.SizeOf<T>();
            IntPtr Pointer = Marshal.AllocHGlobal(Size);
            try
            {
                Marshal.StructureToPtr(Value, Pointer, false);
                return SetInformationJobObject(Job, Class, Pointer, (uint)Size);
            }
            finally { Marshal.FreeHGlobal(Pointer); }
        }

        private static void SetMemoryPriority(Process Process, uint Priority)
        {
            var Info = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = Priority };
            SetProcessInfo(Process.Handle, ProcessMemoryPriority, Info);
        }

        private static void SetPowerThrottle(Process Process)
        {
            uint Flags = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
            var Info = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION, ControlMask = Flags, StateMask = Flags };
            SetProcessInfo(Process.Handle, ProcessPowerThrottling, Info);
        }

        private static bool SetProcessInfo<T>(IntPtr Process, int Class, T Value) where T : struct
        {
            int Size = Marshal.SizeOf<T>();
            IntPtr Pointer = Marshal.AllocHGlobal(Size);
            try
            {
                Marshal.StructureToPtr(Value, Pointer, false);
                return SetProcessInformation(Process, Class, Pointer, (uint)Size);
            }
            finally { Marshal.FreeHGlobal(Pointer); }
        }

        private static int MemoryPriorityValue(string Value)
        {
            switch ((Value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "verylow": return 1;
                case "low": return 3;
                default: return 5;
            }
        }

        private static void ReleaseDeadJobs(HashSet<int> Alive)
        {
            lock (JobsLock)
            {
                foreach (int Id in Jobs.Keys.Where(Id => !Alive.Contains(Id)).ToList())
                {
                    try { CloseHandle(Jobs[Id]); } catch { }
                    Jobs.Remove(Id);
                }
            }
        }

        /// <summary>Trims the working set of every running client. Pages fault back in on next use.</summary>
        public static int TrimAll()
        {
            int n = 0;

            foreach (var kv in MapProcesses())
                try { if (!kv.Value.HasExited) { EmptyWorkingSet(kv.Value.Handle); n++; } } catch { }

            return n;
        }

        public static bool TrimOne(Account acc)
        {
            if (acc == null || string.IsNullOrEmpty(acc.BrowserTrackerID)) return false;

            try { if (MapProcesses().TryGetValue(acc.BrowserTrackerID, out Process p) && !p.HasExited) { EmptyWorkingSet(p.Handle); return true; } } catch { }

            return false;
        }

        /// <summary>Applies priority + affinity (and optionally a trim) to every running client right now.</summary>
        public static int OptimizeAll()
        {
            int n = 0;
            ProcessPriorityClass Background = BackgroundPriority();
            long Affinity = AffinityMask();
            int ForegroundPid = 0; try { GetWindowThreadProcessId(GetForegroundWindow(), out ForegroundPid); } catch { }

            bool Dynamic = B("PerfDynamicPriority", true);
            bool Trim = B("PerfTrimOnMinimize", false);

            foreach (var kv in MapProcesses())
            {
                Process p = kv.Value;

                try
                {
                    if (p.HasExited) continue;

                    p.PriorityClass = (Dynamic && p.Id == ForegroundPid) ? ProcessPriorityClass.Normal : Background;

                    if (Affinity != 0) SetAffinity(p, Affinity);
                    if (Trim) EmptyWorkingSet(p.Handle);

                    n++;
                }
                catch { }
            }

            return n;
        }
    }
}

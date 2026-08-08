using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RBX_Alt_Manager.Classes
{
    public enum ClientState
    {
        /// <summary>Young, no verdict yet — a cold start takes a while.</summary>
        Launching,

        /// <summary>Reached a server.</summary>
        Joined,

        /// <summary>Roblox refused the ticket. The client keeps its window and its memory forever.</summary>
        AuthError,

        /// <summary>Old enough that it should have joined, and it never did. No explicit error to point at.</summary>
        Stuck,

        /// <summary>
        /// Kept for compatibility with older verdict logs; never produced. The marker it used to rely on
        /// (waitForNewPlayerProcess) turned out to appear tens of thousands of times in the log of a perfectly
        /// healthy client — 34682 times in one 11 MB sample here — because every client that waits on the
        /// multi-instance mutex writes it. Roblox's real helper process is already excluded by its command line.
        /// </summary>
        Shim,

        /// <summary>
        /// RobloxPlayerBeta running as Roblox's own systray app, not as a game client. It owns a window and
        /// never joins anything, which is indistinguishable from "stuck" by age alone — and killing it is
        /// wrong. Six of the fourteen Player logs on this machine were this.
        /// </summary>
        TrayApp,

        /// <summary>No log could be attributed to this process.</summary>
        Unknown
    }

    public class ClientVerdict
    {
        public int ProcessId;
        public ClientState State;
        public string Reason;
        public double AgeSeconds;
        public long MemoryMB;
        public string Account;
        public string Tracker;
        public string LogFile;

        /// <summary>Holding memory and going nowhere. What a supervisor would act on.</summary>
        public bool DeadWeight => State == ClientState.AuthError || State == ClientState.Stuck;

        public override string ToString() => $"pid {ProcessId} ({Account ?? Tracker ?? "?"}) {State}: {Reason}";
    }

    /// <summary>
    /// Classifies every running client by reading its own log.
    ///
    /// The zombie reaper only sees clients with no window. A client sitting on Roblox's authentication error
    /// owns a perfectly ordinary window, so it is invisible to that rule and sits there for hours holding a few
    /// hundred megabytes without ever joining anything. Its log says exactly what happened.
    ///
    /// Attribution is exact and costs nothing external: the browser tracker id RAM launches with appears both in
    /// the process command line (<c>browsertrackerid:123</c>) and inside the client's own log
    /// (<c>BrowserTrackerId=123</c>), so the two can be matched directly — no Sysinternals handle.exe, no EULA
    /// registry flag, no process spawn per probe, and no guessing by timestamp.
    ///
    /// Logs are read incrementally. A live client writes tens of thousands of lines an hour (13 MB in a couple
    /// of hours on this machine), so re-reading from the start every sweep would be pointless I/O.
    ///
    /// This class only forms opinions. Whether a verdict gets a client killed is someone else's decision.
    /// </summary>
    internal static class StuckDetector
    {
        // Markers. RE_JOIN and the tray marker are confirmed against the 14 real Player logs on this machine;
        // the two auth markers are structurally sound but unconfirmed here, because no login on this machine has
        // ever actually been refused. Deliberately NOT used: a bare "429", which appears 1148 times across logs
        // whose clients joined perfectly (rate limits on users/economy/thumbnails are routine), and
        // "Sending disconnect with reason: 285", which is ordinary in-game churn.
        private static readonly Regex JoiningGame = new Regex(@"! Joining game '([0-9a-fA-F\-]{36})' place (\d+)", RegexOptions.Compiled);
        private static readonly Regex JoinFinished = new Regex(@"Report game_join_loadtime: placeid:(\d+)", RegexOptions.Compiled);
        private static readonly Regex AuthError = new Regex(@"WebLogin http error:.*?statusCode:\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex AuthRedeem = new Regex(@"status:(\d{3})[^\n]*?url:\{\s*""https://auth\.roblox\.com/v1/authentication-ticket/redeem""", RegexOptions.Compiled);
        private static readonly Regex Disconnect = new Regex(@"Sending disconnect with reason: (\d+)", RegexOptions.Compiled);
        private static readonly Regex TrackerInLog = new Regex(@"BrowserTrackerId=(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Written on the fourth line of the log, 42 ms in, by a client that is the tray app rather than a game client.</summary>
        private static readonly Regex TrayApp = new Regex(@"AppState/TrayMode", RegexOptions.Compiled);

        public static bool Enabled { get; private set; } = true;

        /// <summary>Below this age a client with no join is simply still starting.</summary>
        public static int LaunchGraceSeconds { get; private set; } = 75;

        /// <summary>Above this age, a client that never joined is stuck rather than slow.</summary>
        public static int StuckAfterSeconds { get; private set; } = 240;

        public static int IntervalSeconds { get; private set; } = 30;

        /// <summary>
        /// Killing is split by cause, because the causes are not alike. A client whose ticket was refused is
        /// certainly dead and safe to close; one that simply has not joined yet might be on a slow machine or in
        /// a long queue, and closing it is a judgement call. Both off by default.
        /// </summary>
        public static bool KillAuthErrors { get; private set; }

        public static bool KillStuck { get; private set; }

        /// <summary>An auth error is unambiguous the moment it appears; it does not need the long grace.</summary>
        public static int AuthErrorGraceSeconds { get; private set; } = 10;

        /// <summary>Processes the user asked never to touch.</summary>
        private static HashSet<int> Whitelist = new HashSet<int>();

        /// <summary>Raised for each client whose state changed since the last sweep.</summary>
        public static event Action<ClientVerdict> StateChanged;

        private class Tail
        {
            public string Path;
            public long Offset;
            public bool Primed;
            public bool Joined;
            public bool Tray;
            public bool JoinFinished;
            public string AuthStatus;
            public string LastDisconnect;
        }

        private static readonly Dictionary<string, Tail> Tails = new Dictionary<string, Tail>();   // tracker -> log tail
        private static readonly Dictionary<int, ClientState> LastState = new Dictionary<int, ClientState>();

        /// <summary>When a client was last started for this tracker. Logs older than that belong to a past session.</summary>
        private static readonly Dictionary<string, DateTime> Launches = new Dictionary<string, DateTime>();

        /// <summary>
        /// How far before the recorded launch a log may still count as this session's. The log appears a moment
        /// either side of the process start, and clocks are not exact, so a strict comparison would reject it.
        /// </summary>
        private static readonly TimeSpan LaunchSkew = TimeSpan.FromSeconds(45);
        private static readonly object Lock = new object();

        private static System.Timers.Timer Timer;

        private static string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

        public static void LoadSettings()
        {
            Enabled = Bool("StuckDetectorEnabled", true);

            // StuckKill stays honoured as the old blanket switch, so a config written before the split keeps working.
            bool Blanket = Bool("StuckKill", false);

            KillAuthErrors = Blanket || Bool("StuckKillAuthErrors", false);
            KillStuck = Blanket || Bool("StuckKillStuck", false);
            AuthErrorGraceSeconds = Int("StuckAuthErrorGraceSeconds", 10, 0, 600);
            Whitelist = ParseWhitelist();
            LaunchGraceSeconds = Int("StuckLaunchGraceSeconds", 75, 20, 3600);
            StuckAfterSeconds = Int("StuckAfterSeconds", 240, 60, 24 * 3600);
            IntervalSeconds = Int("StuckIntervalSeconds", 30, 5, 600);

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
            lock (Lock)
            {
                if (Timer != null) { Timer.Interval = IntervalSeconds * 1000; return; }

                Timer = new System.Timers.Timer(IntervalSeconds * 1000) { AutoReset = true };
                Timer.Elapsed += (s, e) =>
                {
                    try { Sweep(); }
                    catch (Exception x) { Program.Logger.Error($"[Stuck] sweep failed: {x}"); }
                };
                Timer.Start();

                string Killing = KillAuthErrors && KillStuck ? "killing refused and stuck clients"
                    : KillAuthErrors ? "killing refused clients"
                    : KillStuck ? "killing stuck clients" : "reporting only";

                Program.Logger.Info($"[Stuck] on — every {IntervalSeconds}s, grace {LaunchGraceSeconds}s, stuck after {StuckAfterSeconds}s, {Killing}");
            }
        }

        public static void Stop()
        {
            lock (Lock)
            {
                if (Timer == null) return;

                Timer.Stop();
                Timer.Dispose();
                Timer = null;

                Program.Logger.Info("[Stuck] off");
            }
        }

        /// <summary>One pass over every running client. Returns a verdict for each.</summary>
        public static List<ClientVerdict> Sweep()
        {
            List<ClientVerdict> Verdicts = new List<ClientVerdict>();

            foreach (Process proc in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                using (proc)
                {
                    ClientVerdict Verdict;

                    try { Verdict = Classify(proc); }
                    catch (InvalidOperationException) { continue; } // exited mid-sweep
                    catch (Exception x)
                    {
                        Program.Logger.Debug($"[Stuck] {proc.Id}: {x.Message}");

                        continue;
                    }

                    if (Verdict == null) continue;

                    Verdicts.Add(Verdict);

                    bool Changed;

                    lock (Lock)
                    {
                        Changed = !LastState.TryGetValue(Verdict.ProcessId, out ClientState Previous) || Previous != Verdict.State;
                        LastState[Verdict.ProcessId] = Verdict.State;
                    }

                    if (!Changed) continue;

                    if (Verdict.DeadWeight) Program.Logger.Warn($"[Stuck] {Verdict} ({Verdict.MemoryMB} MB)");
                    else Program.Logger.Info($"[Stuck] {Verdict}");

                    try { StateChanged?.Invoke(Verdict); }
                    catch (Exception x) { Program.Logger.Error($"[Stuck] handler threw: {x.Message}"); }

                    bool ShouldKill = (Verdict.State == ClientState.AuthError && KillAuthErrors && Verdict.AgeSeconds >= AuthErrorGraceSeconds)
                        || (Verdict.State == ClientState.Stuck && KillStuck);

                    if (ShouldKill && !Whitelist.Contains(Verdict.ProcessId))
                        try
                        {
                            proc.Kill();

                            Program.Logger.Info($"[Stuck] killed pid {Verdict.ProcessId} ({Verdict.Account ?? Verdict.Tracker}) — {Verdict.Reason}, {Verdict.MemoryMB} MB freed");
                        }
                        catch (Exception x) { Program.Logger.Error($"[Stuck] could not kill {Verdict.ProcessId}: {x.Message}"); }
                }
            }

            Forget(Verdicts.Select(Verdict => Verdict.ProcessId));

            return Verdicts;
        }

        private static ClientVerdict Classify(Process proc)
        {
            string CommandLine = proc.GetCommandLine();

            if (string.IsNullOrEmpty(CommandLine) || CommandLine.StartsWith("\\??\\")) return null; // Roblox's helper process
            if (!ClientLauncher.IsClientCommandLine(CommandLine)) return null;

            double Age = (DateTime.Now - proc.StartTime).TotalSeconds;
            long Memory = proc.WorkingSet64 / 1024 / 1024;
            string Tracker = ClientLauncher.TrackerOf(CommandLine);

            ClientVerdict Verdict = new ClientVerdict
            {
                ProcessId = proc.Id,
                AgeSeconds = Age,
                MemoryMB = Memory,
                Tracker = Tracker,
                Account = AccountFor(Tracker)
            };

            Tail Tail = TailFor(Tracker);

            if (Tail == null)
            {
                Verdict.State = Age < LaunchGraceSeconds ? ClientState.Launching : ClientState.Unknown;
                Verdict.Reason = Age < LaunchGraceSeconds ? $"starting, {Age:0}s old" : "no log could be matched to this process";

                return Verdict;
            }

            Verdict.LogFile = Tail.Path;

            ReadNew(Tail);

            // Roblox's own tray app: a window, no joins, forever. Never dead weight, whatever its age.
            if (Tail.Tray && !Tail.Joined)
            {
                Verdict.State = ClientState.TrayApp;
                Verdict.Reason = "Roblox's tray app, not a game client";

                return Verdict;
            }

            // An auth error found AFTER a successful join belongs to a later teleport, not to this session's
            // start; only treat it as fatal while the client has never joined anything.
            if (!string.IsNullOrEmpty(Tail.AuthStatus) && !Tail.Joined)
            {
                Verdict.State = ClientState.AuthError;
                Verdict.Reason = $"Roblox refused the ticket (status {Tail.AuthStatus})";

                return Verdict;
            }

            if (Tail.Joined)
            {
                Verdict.State = ClientState.Joined;
                Verdict.Reason = Tail.JoinFinished ? "in game" : "joining";

                if (!string.IsNullOrEmpty(Tail.LastDisconnect)) Verdict.Reason += $", last disconnect reason {Tail.LastDisconnect}";

                return Verdict;
            }

            if (Age >= StuckAfterSeconds)
            {
                Verdict.State = ClientState.Stuck;
                Verdict.Reason = $"no join in {Age / 60:0.#} minutes";

                return Verdict;
            }

            Verdict.State = ClientState.Launching;
            Verdict.Reason = $"no join yet, {Age:0}s old";

            return Verdict;
        }

        /// <summary>Finds (and remembers) the log this tracker id writes to.</summary>
        private static Tail TailFor(string Tracker)
        {
            if (string.IsNullOrEmpty(Tracker)) return null;

            lock (Lock)
                if (Tails.TryGetValue(Tracker, out Tail Known) && File.Exists(Known.Path)) return Known;

            try
            {
                if (!Directory.Exists(LogFolder)) return null;

                // Newest first, and only recent files: a client's log is written the moment it starts, and old
                // logs from previous sessions carry stale tracker ids that must not be matched.
                foreach (FileInfo File_ in new DirectoryInfo(LogFolder)
                    .GetFiles("*_Player_*_last.log")
                    .Where(File_ => (DateTime.Now - File_.LastWriteTime).TotalHours < 12)
                    .OrderByDescending(File_ => File_.LastWriteTime)
                    .Take(40))
                {
                    string Found = TrackerOfLog(File_.FullName);

                    if (Found == null) continue;

                    lock (Lock)
                    {
                        // A log older than this tracker's most recent launch is a past session of the same account:
                        // the tracker id is reused for life, so the file matches but the session it describes is
                        // over. Skipping it leaves the tracker without a tail until its real log appears, which
                        // Classify reports as "starting" rather than acting on a stale verdict.
                        if (Launches.TryGetValue(Found, out DateTime Started) && File_.LastWriteTime < Started - LaunchSkew)
                            continue;

                        // Newest first, so the first file seen for a tracker is its current log. Older ones must not
                        // overwrite it — and repointing an existing tail at a different file would keep that tail's
                        // sticky verdict while reading a different session.
                        if (!Tails.ContainsKey(Found)) Tails[Found] = new Tail { Path = File_.FullName };
                    }

                    if (Found == Tracker)
                        lock (Lock)
                            if (Tails.TryGetValue(Tracker, out Tail Fresh)) return Fresh;
                }
            }
            catch (Exception x) { Program.Logger.Debug($"[Stuck] log scan failed: {x.Message}"); }

            return null;
        }

        /// <summary>Reads the tracker id out of a log's opening lines. Roblox writes it once, near the top.</summary>
        private static string TrackerOfLog(string Path_)
        {
            try
            {
                using (FileStream Stream = File.Open(Path_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] Head = new byte[Math.Min(Stream.Length, 256 * 1024)];
                    int Read = Stream.Read(Head, 0, Head.Length);

                    Match Found = TrackerInLog.Match(Encoding.UTF8.GetString(Head, 0, Read));

                    return Found.Success ? Found.Groups[1].Value : null;
                }
            }
            catch { return null; }
        }

        /// <summary>Consumes whatever the client appended since the last sweep.</summary>
        private static void ReadNew(Tail Tail)
        {
            try
            {
                using (FileStream Stream = File.Open(Tail.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (Stream.Length < Tail.Offset) Tail.Offset = 0; // rotated or replaced

                    if (Stream.Length == Tail.Offset) return;

                    // Everything that classifies a client happens in its first seconds: the tray marker is on
                    // line 4, and the join line sat at byte 28k of an 11 MB log here. So the first pass reads the
                    // HEAD and then skips to the end — reading all 25 MB of a long-running client's log would buy
                    // nothing but I/O. After that each pass takes only what was appended (~13 KB per sweep at the
                    // ~500 lines/min a live client writes), capped so a client left overnight cannot stall the timer.
                    bool First = !Tail.Primed;

                    long Start = First ? 0 : Math.Max(Tail.Offset, Stream.Length - 4 * 1024 * 1024);
                    long Length = First ? Math.Min(Stream.Length, 2 * 1024 * 1024) : Stream.Length - Start;

                    Tail.Primed = true;

                    Stream.Seek(Start, SeekOrigin.Begin);

                    byte[] Buffer = new byte[Length];
                    int Read = Stream.Read(Buffer, 0, Buffer.Length);

                    Tail.Offset = Stream.Length;

                    string Text = Encoding.UTF8.GetString(Buffer, 0, Read);

                    if (TrayApp.IsMatch(Text)) Tail.Tray = true;
                    if (JoiningGame.IsMatch(Text)) Tail.Joined = true;
                    if (JoinFinished.IsMatch(Text)) { Tail.Joined = true; Tail.JoinFinished = true; }
        
                    Match Auth = AuthError.Match(Text);

                    if (!Auth.Success) Auth = AuthRedeem.Match(Text);
                    if (Auth.Success) Tail.AuthStatus = Auth.Groups[1].Value;

                    Match Gone = Disconnect.Match(Text);

                    if (Gone.Success) Tail.LastDisconnect = Gone.Groups[1].Value;
                }
            }
            catch (Exception x) { Program.Logger.Debug($"[Stuck] reading {Tail.Path}: {x.Message}"); }
        }

        /// <summary>PIDs the user listed as untouchable, in either sweeper.</summary>
        internal static HashSet<int> ParseWhitelist()
        {
            HashSet<int> Result = new HashSet<int>();

            try
            {
                string Value = AccountManager.General.Exists("ReaperWhitelist") ? AccountManager.General.Get("ReaperWhitelist") : null;

                if (string.IsNullOrWhiteSpace(Value)) return Result;

                foreach (string Part in Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(Part.Trim(), out int ProcessId)) Result.Add(ProcessId);
            }
            catch { }

            return Result;
        }

        private static string AccountFor(string Tracker)
        {
            if (string.IsNullOrEmpty(Tracker)) return null;

            lock (AccountManager.AccountsLock)
                return AccountManager.AccountsList?.FirstOrDefault(account => account.BrowserTrackerID == Tracker)?.Username;
        }

        /// <summary>
        /// Records that a client is starting for this tracker id, and forgets what was read for the previous one.
        ///
        /// This has to be called when an account launches, and the reason is subtle. A Tail is cached per tracker
        /// id, and an account's BrowserTrackerID is generated once and then reused for every launch it ever makes
        /// (Account.cs, GenerateTrackerId). Roblox writes a NEW log file per session and leaves the old ones in
        /// place, so the cached Tail keeps pointing at the first session's file — which still exists, so the cache
        /// never misses — and the sticky verdict flags (Joined, AuthStatus, Tray) keep the first session's answer
        /// forever. The account would be reported as "joined" for the rest of the app's life even while a later
        /// client sat refused at the login screen, and with StuckKillAuthErrors on, the opposite: every later
        /// client killed ten seconds in for a refusal that happened once, hours ago.
        ///
        /// Dropping the entry makes the next sweep re-scan the log folder, find the newest file for this tracker,
        /// and read it fresh (Primed false, so it reads the head where the join line lives, then the tail).
        ///
        /// Dropping it is not enough on its own: for the first seconds the new client has not written its log yet,
        /// so a re-scan would find the PREVIOUS session's file, cache that, and the replay would be back — this
        /// time permanently, because nothing invalidates it again. Hence the timestamp: until a log at least as new
        /// as this launch exists, the tracker has no tail, and Classify reports "starting" and then "no log could
        /// be matched", neither of which kills anything.
        /// </summary>
        public static void NoteLaunch(string Tracker)
        {
            if (string.IsNullOrEmpty(Tracker)) return;

            lock (Lock)
            {
                Tails.Remove(Tracker);
                Launches[Tracker] = DateTime.Now;
            }
        }

        /// <summary>Drops state for clients that are gone, so the dictionaries do not grow for the life of the app.</summary>
        private static void Forget(IEnumerable<int> Alive)
        {
            HashSet<int> Live = new HashSet<int>(Alive);

            lock (Lock)
                foreach (int ProcessId in LastState.Keys.Where(Key => !Live.Contains(Key)).ToArray())
                    LastState.Remove(ProcessId);
        }
    }
}

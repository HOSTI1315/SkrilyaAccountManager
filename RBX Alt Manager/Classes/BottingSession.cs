using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Scheduled bot-account cycles.  This is intentionally not a third relauncher: it owns only the schedule
    /// and the bot/player roles. Once a client has been launched, Relauncher remains the only health supervisor.
    /// Per-account LaunchLock still protects manual/batch/Nexus launches from overlapping a scheduled cycle.
    /// </summary>
    internal static class BottingSession
    {
        public const string RoleField = "BottingRole";

        private static readonly object Gate = new object();
        private static CancellationTokenSource Cancellation;
        private static string[] BotNames = Array.Empty<string>();
        private static string[] PlayerNames = Array.Empty<string>();
        private static long PlaceId;
        private static int IntervalMinutes;
        private static bool CloseOnStop;
        private static DateTime? LastCycle;
        private static DateTime? NextCycle;

        public static object State()
        {
            lock (Gate)
                return new
                {
                    running = Cancellation != null,
                    bots = BotNames,
                    players = PlayerNames,
                    placeId = PlaceId,
                    intervalMinutes = IntervalMinutes,
                    closeOnStop = CloseOnStop,
                    lastCycle = LastCycle,
                    nextCycle = NextCycle
                };
        }

        public static object SetRole(IEnumerable<Account> Accounts, string Role)
        {
            string Normalized = (Role ?? string.Empty).Trim().ToLowerInvariant();
            if (Normalized != "bot" && Normalized != "player" && Normalized != "none")
                throw new ArgumentException("Role must be bot, player or none.");

            int Changed = 0;
            foreach (Account Account in Accounts ?? Enumerable.Empty<Account>())
            {
                if (Account == null) continue;
                if (Normalized == "none") Account.RemoveField(RoleField);
                else Account.SetField(RoleField, Normalized);
                Changed++;
            }

            if (Changed > 0) AccountManager.SaveAccounts();
            return new { changed = Changed, role = Normalized };
        }

        public static object Start(IEnumerable<Account> Bots, IEnumerable<Account> Players, long Destination, int Minutes, bool StopClients)
        {
            List<Account> BotAccounts = (Bots ?? Enumerable.Empty<Account>()).Where(Account => Account != null).Distinct().ToList();
            List<Account> PlayerAccounts = (Players ?? Enumerable.Empty<Account>()).Where(Account => Account != null).Distinct().ToList();

            if (BotAccounts.Count == 0) throw new ArgumentException("Choose at least one bot account.");
            if (PlayerAccounts.Count == 0 && Destination <= 0) throw new ArgumentException("Choose player accounts to follow, or enter a place id.");
            if (Minutes < 10 || Minutes > 480) throw new ArgumentOutOfRangeException(nameof(Minutes), "Interval must be between 10 and 480 minutes.");

            CancellationTokenSource Source = new CancellationTokenSource();

            lock (Gate)
            {
                if (Cancellation != null)
                {
                    Source.Dispose();
                    throw new InvalidOperationException("A botting session is already running.");
                }

                Cancellation = Source;
                BotNames = BotAccounts.Select(Account => Account.Username).ToArray();
                PlayerNames = PlayerAccounts.Select(Account => Account.Username).ToArray();
                PlaceId = Destination;
                IntervalMinutes = Minutes;
                CloseOnStop = StopClients;
                LastCycle = null;
                NextCycle = DateTime.Now;
            }

            _ = Task.Run(() => Loop(Source, BotAccounts, PlayerAccounts));
            Program.Logger.Info($"[Botting] started: {BotAccounts.Count} bot(s), {PlayerAccounts.Count} player target(s), every {Minutes}m");

            return State();
        }

        public static object StartSaved(long Destination, int Minutes, bool StopClients)
        {
            List<Account> Snapshot = AccountManager.GetAccountsSnapshot();
            return Start(
                Snapshot.Where(Account => Account.GetField(RoleField) == "bot"),
                Snapshot.Where(Account => Account.GetField(RoleField) == "player"),
                Destination, Minutes, StopClients);
        }

        public static object Stop(bool? CloseClients = null)
        {
            CancellationTokenSource Source;
            string[] Bots;
            bool ShouldClose;

            lock (Gate)
            {
                Source = Cancellation;
                Bots = BotNames.ToArray();
                ShouldClose = CloseClients ?? CloseOnStop;
                Cancellation = null;
                NextCycle = null;
            }

            Source?.Cancel();
            if (ShouldClose) CloseBotClients(Bots);

            Program.Logger.Info($"[Botting] stopped{(ShouldClose ? "; bot clients closed" : string.Empty)}");
            return new { stopped = Source != null, closed = ShouldClose };
        }

        private static async Task Loop(CancellationTokenSource Source, List<Account> Bots, List<Account> Players)
        {
            CancellationToken Token = Source.Token;

            try
            {
                while (!Token.IsCancellationRequested)
                {
                    lock (Gate)
                    {
                        if (!ReferenceEquals(Cancellation, Source)) return;
                        LastCycle = DateTime.Now;
                        NextCycle = DateTime.Now.AddMinutes(IntervalMinutes);
                    }

                    await RunCycle(Bots, Players, Token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMinutes(IntervalMinutes), Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception x) { Program.Logger.Error($"[Botting] {x}"); }
            finally
            {
                lock (Gate)
                {
                    if (ReferenceEquals(Cancellation, Source))
                    {
                        Cancellation = null;
                        NextCycle = null;
                    }
                }

                Source.Dispose();
            }
        }

        private static async Task RunCycle(List<Account> Bots, List<Account> Players, CancellationToken Token)
        {
            int DelaySeconds = IntSetting("AccountJoinDelay", 8, 0, 300);

            for (int Index = 0; Index < Bots.Count; Index++)
            {
                if (Token.IsCancellationRequested) return;

                Account Bot = Bots[Index];

                try
                {
                    string Result;

                    if (Players.Count > 0)
                    {
                        Account Player = Players[Index % Players.Count];
                        Result = await Bot.JoinServer(Player.UserID, string.Empty, FollowUser: true).ConfigureAwait(false);
                    }
                    else
                    {
                        Result = await Bot.JoinServer(PlaceId).ConfigureAwait(false);
                    }

                    Program.Logger.Info($"[Botting] {Bot.Username}: {Result}");
                }
                catch (Exception x) { Program.Logger.Warn($"[Botting] {Bot.Username}: {x.Message}"); }

                if (DelaySeconds > 0 && Index < Bots.Count - 1)
                    await Task.Delay(TimeSpan.FromSeconds(DelaySeconds), Token).ConfigureAwait(false);
            }
        }

        private static int CloseBotClients(IEnumerable<string> Names)
        {
            HashSet<string> Trackers = AccountManager.GetAccountsSnapshot()
                .Where(Account => Names.Contains(Account.Username) && !string.IsNullOrEmpty(Account.BrowserTrackerID))
                .Select(Account => Account.BrowserTrackerID)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int Closed = 0;
            foreach (Process Process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try
                {
                    var Match = ClientLauncher.TrackerRegex.Match(Process.GetCommandLine() ?? string.Empty);
                    if (!Match.Success || !Trackers.Contains(Match.Groups[1].Value)) continue;

                    Process.CloseMainWindow();
                    if (!Process.WaitForExit(1500)) Process.Kill();
                    Closed++;
                }
                catch { }
                finally { Process.Dispose(); }
            }

            return Closed;
        }

        private static int IntSetting(string Name, int Default, int Min, int Max)
        {
            try { return int.TryParse(AccountManager.General.Get(Name), out int Value) ? Math.Max(Min, Math.Min(Max, Value)) : Default; }
            catch { return Default; }
        }
    }
}

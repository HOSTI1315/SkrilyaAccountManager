using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    internal enum LaunchOutcome
    {
        /// <summary>The client logged that it joined a server.</summary>
        Joined,

        /// <summary>The client logged a disconnect before (or after) joining.</summary>
        Disconnected,

        /// <summary>The process died without ever reaching a server — almost always a rejected ticket.</summary>
        DiedEarly,

        /// <summary>Nothing conclusive within the watch window.</summary>
        Unknown
    }

    /// <summary>
    /// Reads one client's own log file to find out whether the launch actually worked.
    ///
    /// "Success" from JoinServer only means a process started. The client can still be rejected — a ticket
    /// redeemed from a different IP than it was issued to, a rate limit, a moderated account — and the usual
    /// symptom is a window that closes again a few seconds later with nothing written anywhere. The client's log
    /// says exactly which of those happened, so it is worth the read.
    ///
    /// Attribution is by PID via that process's open handles (handle.exe, already shipped for the watcher). When
    /// that is unavailable it falls back to time-window matching confirmed by the place id in the log, which is
    /// weaker: two launches into the same place within the same second can be swapped.
    /// </summary>
    internal static class ClientLogWatcher
    {
        private const string TimestampRegex = @"[\d+\-]+T[\d+:]+\.\w+Z,[\d.+]+,\w+,\d+[\s+]?";

        private static readonly Regex JoiningGameRegex = new Regex($@"^{TimestampRegex}\[FLog::Output\] ! Joining game '([\w+\-]{{36}})' place (\d+)", RegexOptions.Compiled);
        private static readonly Regex DisconnectRegex = new Regex($@"^{TimestampRegex}\[FLog::Network\] Sending disconnect with reason: (\d+)", RegexOptions.Compiled);
        private static readonly Regex LogFileRegex = new Regex(@"\w+: File.+(\w+:.+\\logs\\)([\d+.]+_\w+_Player_\w+_last\.log)", RegexOptions.Compiled);

        public static bool Enabled = true;

        /// <summary>Raised when a watched launch resolves. (account, outcome, detail)</summary>
        public static event Action<Account, LaunchOutcome, string> LaunchResolved;

        public static async Task Watch(Account account, Process Client, long PlaceID)
        {
            if (!Enabled || account == null || Client == null) return;

            try
            {
                DateTime Started = DateTime.Now;
                DateTime Deadline = Started.AddSeconds(120);

                FileInfo Log = null;

                while (Log == null && DateTime.Now < Deadline)
                {
                    if (SafeHasExited(Client))
                    {
                        Report(account, LaunchOutcome.DiedEarly, $"client {Client.Id} exited after {(DateTime.Now - Started).TotalSeconds:0.#}s without reaching a server (usually a rejected authentication ticket)");

                        return;
                    }

                    Log = FindLogFile(Client, PlaceID);

                    if (Log == null) await Task.Delay(1500).ConfigureAwait(false);
                }

                if (Log == null)
                {
                    Report(account, LaunchOutcome.Unknown, "could not locate the client's log file");

                    return;
                }

                using (FileStream Stream = File.Open(Log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long Position = 0;
                    StringBuilder Pending = new StringBuilder();

                    while (DateTime.Now < Deadline)
                    {
                        if (Stream.Length > Position)
                        {
                            int Length = (int)Math.Min(Stream.Length - Position, 512 * 1024);

                            Stream.Seek(Position, SeekOrigin.Begin);

                            byte[] Bytes = new byte[Length];
                            int Read = Stream.Read(Bytes, 0, Length);

                            Position += Read;

                            Pending.Append(Encoding.UTF8.GetString(Bytes, 0, Read));

                            string Buffered = Pending.ToString();
                            string[] Lines = Buffered.Split('\n');

                            // Keep the trailing partial line for the next pass.
                            Pending.Clear();
                            Pending.Append(Lines[Lines.Length - 1]);

                            for (int i = 0; i < Lines.Length - 1; i++)
                            {
                                Match Joining = JoiningGameRegex.Match(Lines[i]);

                                if (Joining.Success)
                                {
                                    Report(account, LaunchOutcome.Joined, $"joined job {Joining.Groups[1].Value} in place {Joining.Groups[2].Value} after {(DateTime.Now - Started).TotalSeconds:0.#}s");

                                    return;
                                }

                                Match Disconnect = DisconnectRegex.Match(Lines[i]);

                                if (Disconnect.Success)
                                {
                                    Report(account, LaunchOutcome.Disconnected, $"disconnected before joining, reason {Disconnect.Groups[1].Value}");

                                    return;
                                }
                            }
                        }

                        if (SafeHasExited(Client))
                        {
                            Report(account, LaunchOutcome.DiedEarly, $"client {Client.Id} exited after {(DateTime.Now - Started).TotalSeconds:0.#}s without reaching a server");

                            return;
                        }

                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }

                Report(account, LaunchOutcome.Unknown, "no join or disconnect within the watch window");
            }
            catch (Exception x) { Program.Logger.Error($"[LaunchWatch] {account?.Username}: {x.Message}"); }
        }

        private static bool SafeHasExited(Process Client)
        {
            try { return Client.HasExited; }
            catch { return true; }
        }

        private static void Report(Account account, LaunchOutcome Outcome, string Detail)
        {
            string Message = $"[LaunchWatch] {account?.Username}: {Outcome} — {Detail}";

            if (Outcome == LaunchOutcome.Joined) Program.Logger.Info(Message);
            else Program.Logger.Warn(Message);

            try { LaunchResolved?.Invoke(account, Outcome, Detail); }
            catch (Exception x) { Program.Logger.Error($"[LaunchWatch] handler threw: {x.Message}"); }
        }

        /// <summary>Exact attribution through the process's open handles; falls back to a time+place match.</summary>
        private static FileInfo FindLogFile(Process Client, long PlaceID)
        {
            FileInfo Exact = FindLogFileByHandle(Client);

            if (Exact != null) return Exact;

            return FindLogFileByTime(Client, PlaceID);
        }

        private static FileInfo FindLogFileByHandle(Process Client)
        {
            try
            {
                if (!File.Exists(RobloxWatcher.HandlePath) || !RobloxWatcher.IsHandleEulaAccepted()) return null;

                string Output;

                using (Process Handle = Process.Start(new ProcessStartInfo(RobloxWatcher.HandlePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    Arguments = "-p " + Client.Id
                }))
                {
                    Handle.WaitForExit(6000);

                    Output = Handle.StandardOutput.ReadToEnd();
                }

                Match Found = LogFileRegex.Match(Output);

                if (!Found.Success || Found.Groups.Count != 3) return null;

                string Directory = Found.Groups[1].Value;

                // handle.exe renders non-ASCII user names as question marks; the logs folder is always the same place.
                if (Directory.Contains("?"))
                    Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

                string Full = Path.Combine(Directory, Found.Groups[2].Value);

                return File.Exists(Full) ? new FileInfo(Full) : null;
            }
            catch (Exception x)
            {
                Program.Logger.Debug($"[LaunchWatch] handle lookup failed: {x.Message}");

                return null;
            }
        }

        private static FileInfo FindLogFileByTime(Process Client, long PlaceID)
        {
            try
            {
                string Logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");

                if (!Directory.Exists(Logs)) return null;

                DateTime Start;

                try { Start = Client.StartTime.AddSeconds(-5); }
                catch { return null; }

                List<FileInfo> Candidates = new DirectoryInfo(Logs)
                    .GetFiles("*_Player_*_last.log")
                    .Where(File => File.CreationTime >= Start)
                    .OrderByDescending(File => File.CreationTime)
                    .Take(8)
                    .ToList();

                if (Candidates.Count == 0) return null;

                // One candidate in the window is unambiguous. With several, only a log that already names our
                // place can be attributed — anything else would risk reporting another account's outcome.
                if (Candidates.Count == 1) return Candidates[0];

                foreach (FileInfo Candidate in Candidates)
                    if (Mentions(Candidate, PlaceID)) return Candidate;

                return null;
            }
            catch { return null; }
        }

        private static bool Mentions(FileInfo Log, long PlaceID)
        {
            try
            {
                using (FileStream Stream = File.Open(Log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader Reader = new StreamReader(Stream))
                {
                    string Content = Reader.ReadToEnd();

                    return Content.Contains($"place {PlaceID}") || Content.Contains($"placeId={PlaceID}") || Content.Contains($"placeId%3d{PlaceID}");
                }
            }
            catch { return false; }
        }
    }
}

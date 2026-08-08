using System;
using System.Collections.Generic;
using System.Linq;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>Why a relaunch was refused, in words worth showing.</summary>
    public enum BudgetVerdict
    {
        Allowed,

        /// <summary>This account was relaunched too recently.</summary>
        Cooldown,

        /// <summary>The hourly ceiling for all accounts is spent.</summary>
        HourlyCap,

        /// <summary>This account has failed enough times in a row that retrying is no longer a guess worth making.</summary>
        GivenUp
    }

    /// <summary>
    /// The rate limiter for automatic relaunching.
    ///
    /// Without one, a failure that is not transient — a dead cookie, a challenge, a Roblox outage, a wrong place
    /// id — turns the supervisor into a loop: launch, fail in twenty seconds, launch again. Each attempt is two
    /// requests to auth.roblox.com and a process start; across ten accounts that is hundreds of attempts an hour
    /// from one address, which earns 429s for the healthy accounts too and looks exactly like an attack.
    ///
    /// Three rules, and the third is the one that matters most: a per-account cooldown, an hourly ceiling across
    /// all accounts, and giving up on an account that keeps failing rather than hammering it forever.
    /// </summary>
    public class LaunchBudget
    {
        public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(10);

        public int MaxPerHour { get; set; } = 12;

        /// <summary>Consecutive failures after which an account is left alone until something changes.</summary>
        public int GiveUpAfter { get; set; } = 3;

        private readonly Dictionary<string, DateTime> LastAttempt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> ConsecutiveFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DateTime> Attempts = new List<DateTime>();
        private readonly object Lock = new object();

        /// <summary>May this account be relaunched right now?</summary>
        public BudgetVerdict Check(string Account, DateTime? Now = null)
        {
            DateTime At = Now ?? DateTime.Now;

            lock (Lock)
            {
                Prune(At);

                if (ConsecutiveFailures.TryGetValue(Account, out int Failures) && Failures >= GiveUpAfter) return BudgetVerdict.GivenUp;
                if (LastAttempt.TryGetValue(Account, out DateTime Last) && At - Last < Cooldown) return BudgetVerdict.Cooldown;
                if (Attempts.Count >= MaxPerHour) return BudgetVerdict.HourlyCap;

                return BudgetVerdict.Allowed;
            }
        }

        /// <summary>Records that a relaunch was started. Spends both the cooldown and one of the hourly slots.</summary>
        public void Note(string Account, DateTime? Now = null)
        {
            DateTime At = Now ?? DateTime.Now;

            lock (Lock)
            {
                Prune(At);

                LastAttempt[Account] = At;
                Attempts.Add(At);
            }
        }

        /// <summary>The relaunch worked: the account is healthy again and its failure streak is over.</summary>
        public void Succeeded(string Account)
        {
            lock (Lock) ConsecutiveFailures.Remove(Account);
        }

        /// <summary>The relaunch failed. Returns true when this was the failure that made us give up.</summary>
        public bool Failed(string Account)
        {
            lock (Lock)
            {
                int Failures = ConsecutiveFailures.TryGetValue(Account, out int Seen) ? Seen + 1 : 1;

                ConsecutiveFailures[Account] = Failures;

                return Failures >= GiveUpAfter;
            }
        }

        /// <summary>Clears the streak so a given-up account gets another chance — after the user changed something.</summary>
        public void Forgive(string Account)
        {
            lock (Lock)
            {
                ConsecutiveFailures.Remove(Account);
                LastAttempt.Remove(Account);
            }
        }

        public int LaunchedLastHour(DateTime? Now = null)
        {
            lock (Lock)
            {
                Prune(Now ?? DateTime.Now);

                return Attempts.Count;
            }
        }

        public int FailuresOf(string Account)
        {
            lock (Lock) return ConsecutiveFailures.TryGetValue(Account, out int Failures) ? Failures : 0;
        }

        public string Explain(BudgetVerdict Verdict, string Account) => Verdict switch
        {
            BudgetVerdict.Cooldown => $"{Account} was relaunched less than {Cooldown.TotalMinutes:0} minutes ago",
            BudgetVerdict.HourlyCap => $"the hourly limit of {MaxPerHour} relaunches is spent",
            BudgetVerdict.GivenUp => $"{Account} failed {GiveUpAfter} times in a row and needs a look",
            _ => "allowed"
        };

        private void Prune(DateTime At)
        {
            DateTime Since = At - TimeSpan.FromHours(1);

            Attempts.RemoveAll(When => When < Since);
        }
    }
}

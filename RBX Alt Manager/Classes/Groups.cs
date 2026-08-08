using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>One account group, as the interface should show it.</summary>
    public class GroupInfo
    {
        /// <summary>The value stored on accounts. Never shown as-is.</summary>
        public string Name;

        /// <summary>What the user reads: the numeric ordering prefix stripped off.</summary>
        public string Title;

        public int Accounts;
    }

    /// <summary>
    /// The order groups appear in, and what their names look like once shown.
    ///
    /// The old window has no manual ordering: it sorts groups by name and hides a leading number, so "001 Farm"
    /// sorts first and reads as "Farm". That convention is load-bearing for anyone who already named their
    /// groups that way, so it stays — a manual order is layered on top of it rather than replacing it, and the
    /// stored group names are never rewritten.
    /// </summary>
    internal static class Groups
    {
        /// <summary>
        /// The ordering prefix the old window hides: up to three digits and an optional space.
        ///
        /// The lookahead is the one deliberate difference from the old window, whose pattern has none: there,
        /// a group called "2024 Season" reads as "4 Season", because the first three digits are eaten and the
        /// fourth is left behind. A prefix is only a prefix when the digits end.
        /// </summary>
        private static readonly Regex OrderingPrefix = new Regex(@"^\d{1,3}(?!\d)\s?", RegexOptions.Compiled);

        /// <summary>Group names may contain commas, so the stored order is split on something a user cannot type.</summary>
        private const char Separator = '\u001F';

        public const string OrderSetting = "GroupOrder";
        public const string DefaultGroupSetting = "DefaultGroup";

        public static string TitleOf(string Name)
        {
            if (string.IsNullOrEmpty(Name)) return "Default";

            string Title = OrderingPrefix.Replace(Name, string.Empty);

            // "007" alone would render as nothing; keep the original rather than an empty chip.
            return Title.Length > 0 ? Title : Name;
        }

        /// <summary>The group new accounts land in when the user did not pick one.</summary>
        public static string DefaultGroup
        {
            get
            {
                try
                {
                    string Value = AccountManager.General.Exists(DefaultGroupSetting) ? AccountManager.General.Get(DefaultGroupSetting) : null;

                    return string.IsNullOrWhiteSpace(Value) ? "Default" : Value.Trim();
                }
                catch { return "Default"; }
            }
        }

        /// <summary>The saved manual order, most-recently-arranged first. Empty when the user never arranged anything.</summary>
        public static List<string> SavedOrder()
        {
            try
            {
                string Value = AccountManager.General.Exists(OrderSetting) ? AccountManager.General.Get(OrderSetting) : null;

                if (string.IsNullOrWhiteSpace(Value)) return new List<string>();

                return Value.Split(Separator).Select(Name => Name.Trim()).Where(Name => Name.Length > 0).ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>
        /// Records a new order. Group names can contain commas, so the separator is a unit separator rather than
        /// something a user could type.
        /// </summary>
        public static void SaveOrder(IEnumerable<string> Order)
        {
            string Value = string.Join(Separator.ToString(), Order.Where(Name => !string.IsNullOrWhiteSpace(Name)));

            AccountManager.General.Set(OrderSetting, Value);
            AccountManager.IniSettings.Save("RAMSettings.ini");
        }

        /// <summary>
        /// Arranges the groups that actually exist: the ones the user placed by hand first, in their order, then
        /// everything else by name — which is where the numeric prefix still does its work. A group that was
        /// arranged once and later emptied simply disappears; its position is remembered for when it returns.
        /// </summary>
        public static List<GroupInfo> Arrange(IEnumerable<string> ExistingNames, List<string> Order = null)
        {
            Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string Raw in ExistingNames)
            {
                string Name = string.IsNullOrWhiteSpace(Raw) ? "Default" : Raw;

                Counts[Name] = Counts.TryGetValue(Name, out int Seen) ? Seen + 1 : 1;
            }

            List<string> Manual = Order ?? SavedOrder();
            List<GroupInfo> Result = new List<GroupInfo>();

            foreach (string Name in Manual)
                if (Counts.ContainsKey(Name))
                {
                    Result.Add(new GroupInfo { Name = Name, Title = TitleOf(Name), Accounts = Counts[Name] });

                    Counts.Remove(Name);
                }

            foreach (string Name in Counts.Keys.OrderBy(Name => Name, StringComparer.OrdinalIgnoreCase))
                Result.Add(new GroupInfo { Name = Name, Title = TitleOf(Name), Accounts = Counts[Name] });

            return Result;
        }
    }
}

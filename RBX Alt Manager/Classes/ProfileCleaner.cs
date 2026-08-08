using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RBX_Alt_Manager.Classes
{
    public sealed class ProfileCleanupItem
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
        public bool Registry { get; set; }
    }

    public sealed class ProfileCleanupPreview
    {
        public List<ProfileCleanupItem> Items { get; set; } = new List<ProfileCleanupItem>();
        public long TotalBytes => Items.Sum(Item => Item.Bytes);
    }

    /// <summary>
    /// Removes disposable Roblox per-user state without touching the installed Versions directory, account-manager
    /// data, machine identifiers or network adapters.  Cleanup is deliberately a two-step operation: Preview is
    /// cheap and side-effect free; Clean refuses to run while a Roblox client is alive and backs up the few user
    /// settings worth preserving before deleting anything.
    /// </summary>
    public static class ProfileCleaner
    {
        private const string RobloxRegistry = @"Software\ROBLOX Corporation";

        private static string LocalRoblox => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");
        private static string RoamingRoblox => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roblox");
        private static string ProgramDataRoblox => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roblox");

        /// <summary>
        /// Intentionally excludes LocalAppData\Roblox\Versions: that directory is the actual installed client,
        /// not profile/cache state. Deleting it would turn a cleanup button into an implicit reinstall.
        /// </summary>
        private static IEnumerable<string> DisposablePaths()
        {
            yield return Path.Combine(LocalRoblox, "logs");
            yield return Path.Combine(LocalRoblox, "LocalStorage");
            yield return Path.Combine(LocalRoblox, "Downloads");
            yield return Path.Combine(LocalRoblox, "Cache");
            yield return Path.Combine(LocalRoblox, "Browser");
            yield return Path.Combine(LocalRoblox, "Cookies");
            yield return Path.Combine(LocalRoblox, "GlobalBasicSettings_13.xml");
            yield return RoamingRoblox;
            yield return ProgramDataRoblox;
        }

        public static ProfileCleanupPreview Preview()
        {
            var Preview = new ProfileCleanupPreview();

            foreach (string PathValue in DisposablePaths().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(PathValue) && !Directory.Exists(PathValue)) continue;

                Preview.Items.Add(new ProfileCleanupItem { Path = PathValue, Bytes = SizeOf(PathValue) });
            }

            // Prefetch files are files rather than a directory target, so enumerate them individually. Access to
            // this directory is commonly denied to a non-elevated process; that simply means there is no item.
            string Prefetch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

            try
            {
                foreach (string FileName in Directory.EnumerateFiles(Prefetch, "ROBLOX*.pf", SearchOption.TopDirectoryOnly))
                    Preview.Items.Add(new ProfileCleanupItem { Path = FileName, Bytes = SizeOf(FileName) });
            }
            catch { }

            try
            {
                using RegistryKey Key = Registry.CurrentUser.OpenSubKey(RobloxRegistry, false);
                if (Key != null) Preview.Items.Add(new ProfileCleanupItem { Path = @"HKCU\" + RobloxRegistry, Registry = true });
            }
            catch { }

            return Preview;
        }

        public static ProfileCleanupPreview Clean()
        {
            Process[] Clients = Process.GetProcessesByName("RobloxPlayerBeta");
            bool Running = false;
            try
            {
                foreach (Process Client in Clients)
                    try { if (!Client.HasExited) { Running = true; break; } } catch { }
            }
            finally
            {
                foreach (Process Client in Clients) Client.Dispose();
            }

            if (Running) throw new InvalidOperationException("Close every Roblox client before cleaning its profile.");

            ProfileCleanupPreview Plan = Preview();
            if (Plan.Items.Count == 0) return Plan;

            string BackupRoot = Path.Combine(Path.GetTempPath(), "SkrilyaProfileCleaner_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(BackupRoot);

            List<(string Source, string Backup)> Preserved = BackupSettings(BackupRoot);

            try
            {
                foreach (ProfileCleanupItem Item in Plan.Items)
                {
                    try
                    {
                        if (Item.Registry)
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(RobloxRegistry, false);
                        }
                        else if (File.Exists(Item.Path))
                        {
                            File.SetAttributes(Item.Path, FileAttributes.Normal);
                            File.Delete(Item.Path);
                        }
                        else if (Directory.Exists(Item.Path))
                        {
                            ClearReadOnly(Item.Path);
                            Directory.Delete(Item.Path, true);
                        }
                    }
                    catch (Exception x)
                    {
                        Program.Logger.Warn($"[ProfileCleaner] {Item.Path}: {x.Message}");
                    }
                }

                RestoreSettings(Preserved);
            }
            finally
            {
                try { Directory.Delete(BackupRoot, true); } catch { }
            }

            Program.Logger.Info($"[ProfileCleaner] cleaned {Plan.Items.Count} target(s), {FormatBytes(Plan.TotalBytes)} disposable data");
            return Plan;
        }

        /// <summary>
        /// Preserve only the basic-settings file that is itself a cleanup target. The Versions tree is excluded
        /// from cleanup and is not even enumerated here, so profile maintenance cannot read/write installed
        /// clients or their ClientSettings.
        /// </summary>
        private static List<(string Source, string Backup)> BackupSettings(string BackupRoot)
        {
            var Files = new List<string>();

            string Basic = Path.Combine(LocalRoblox, "GlobalBasicSettings_13.xml");
            if (File.Exists(Basic)) Files.Add(Basic);

            var Output = new List<(string Source, string Backup)>();
            int Index = 0;

            foreach (string Source in Files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string Backup = Path.Combine(BackupRoot, (Index++).ToString("D4") + Path.GetExtension(Source));
                    File.Copy(Source, Backup, true);
                    Output.Add((Source, Backup));
                }
                catch (Exception x) { Program.Logger.Warn($"[ProfileCleaner] could not back up {Source}: {x.Message}"); }
            }

            return Output;
        }

        private static void RestoreSettings(IEnumerable<(string Source, string Backup)> Files)
        {
            foreach ((string Source, string Backup) in Files)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Source));
                    File.Copy(Backup, Source, true);
                }
                catch (Exception x) { Program.Logger.Warn($"[ProfileCleaner] could not restore {Source}: {x.Message}"); }
            }
        }

        private static long SizeOf(string PathValue)
        {
            try
            {
                if (File.Exists(PathValue)) return new FileInfo(PathValue).Length;
                if (!Directory.Exists(PathValue)) return 0;

                long Total = 0;
                var Pending = new Stack<string>();
                Pending.Push(PathValue);

                while (Pending.Count > 0)
                {
                    string Current = Pending.Pop();
                    try
                    {
                        foreach (string FileName in Directory.EnumerateFiles(Current))
                            try { Total += new FileInfo(FileName).Length; } catch { }

                        foreach (string DirectoryName in Directory.EnumerateDirectories(Current)) Pending.Push(DirectoryName);
                    }
                    catch { }
                }

                return Total;
            }
            catch { return 0; }
        }

        private static void ClearReadOnly(string Root)
        {
            try
            {
                foreach (string FileName in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(FileName, FileAttributes.Normal); } catch { }
            }
            catch { }
        }

        public static string FormatBytes(long Bytes)
        {
            string[] Units = { "B", "KB", "MB", "GB", "TB" };
            double Value = Math.Max(0, Bytes);
            int Unit = 0;
            while (Value >= 1024 && Unit < Units.Length - 1) { Value /= 1024; Unit++; }
            return $"{Value:0.##} {Units[Unit]}";
        }
    }
}

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    public sealed class RobloxVersionInfo
    {
        public string Version { get; set; }
        public string Channel { get; set; } = "LIVE";
        public DateTime? Released { get; set; }
        public bool Installed { get; set; }
        public bool Pinned { get; set; }
    }

    internal sealed class RobloxPackage
    {
        public string Name;
        public string Hash;
        public long Compressed;
        public long Uncompressed;
    }

    /// <summary>
    /// Installs immutable Roblox builds beside Skrilya's own data and optionally pins the launcher to one of
    /// them.  A pinned build is started by RobloxPlayerBeta.exe directly, so Roblox's normal bootstrapper never
    /// gets a chance to replace it. The standard auto-updating installation is left untouched and becomes active
    /// again immediately when the pin is cleared.
    /// </summary>
    internal static class VersionManager
    {
        private const string CurrentApi = "https://weao.xyz/api/versions/current";
        private const string PastApi = "https://weao.xyz/api/versions/past";
        private const string DefaultCdn = "https://setup.rbxcdn.com";
        private const string AppSettingsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Settings><ContentFolder>content</ContentFolder><BaseUrl>https://www.roblox.com</BaseUrl></Settings>";
        private const string CompleteMarker = ".complete";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private static readonly Regex VersionPattern = new Regex(@"^version-[0-9a-f]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly object InstallGate = new object();
        private static readonly object FallbackGate = new object();
        private static CancellationTokenSource InstallCancellation;

        private static readonly Dictionary<string, string> ExtractRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shaders.zip"] = "shaders",
            ["ssl.zip"] = "ssl",
            ["content-avatar.zip"] = @"content\avatar",
            ["content-configs.zip"] = @"content\configs",
            ["content-fonts.zip"] = @"content\fonts",
            ["content-sky.zip"] = @"content\sky",
            ["content-sounds.zip"] = @"content\sounds",
            ["content-textures2.zip"] = @"content\textures",
            ["content-models.zip"] = @"content\models",
            ["content-platform-fonts.zip"] = @"PlatformContent\pc\fonts",
            ["content-platform-dictionaries.zip"] = @"PlatformContent\pc\shared_compression_dictionaries",
            ["content-terrain.zip"] = @"PlatformContent\pc\terrain",
            ["extracontent-luapackages.zip"] = @"ExtraContent\LuaPackages",
            ["extracontent-translations.zip"] = @"ExtraContent\translations",
            ["extracontent-models.zip"] = @"ExtraContent\models",
            ["extracontent-textures.zip"] = @"ExtraContent\textures",
            ["extracontent-places.zip"] = @"ExtraContent\places"
        };

        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkrilyaAccountManager", "Versions");

        public static string PinnedVersion => Setting("PinnedRobloxVersion");
        public static string PinnedChannel => string.IsNullOrWhiteSpace(Setting("PinnedRobloxChannel")) ? "LIVE" : Setting("PinnedRobloxChannel");
        public static bool Installing { get { lock (InstallGate) return InstallCancellation != null; } }

        public static string DirectoryOf(string Version, string Channel = "LIVE") =>
            Path.Combine(Root, SafeChannel(Channel), Version ?? string.Empty);

        public static string GetPinnedExecutable()
        {
            string Version = PinnedVersion;
            if (!VersionPattern.IsMatch(Version ?? string.Empty)) return null;

            string Directory = DirectoryOf(Version, PinnedChannel);
            string Candidate = Path.Combine(Directory, "RobloxPlayerBeta.exe");
            return IsInstalled(Directory) ? Candidate : null;
        }

        /// <summary>Folder ClientSettings should be patched in for the launch that will actually happen.</summary>
        public static string EffectiveVersionDirectory()
        {
            string Pinned = GetPinnedExecutable();
            return Pinned == null ? null : Path.GetDirectoryName(Pinned);
        }

        public static async Task<List<RobloxVersionInfo>> CatalogAsync(CancellationToken Token = default)
        {
            var Versions = new List<RobloxVersionInfo>();

            foreach (string Url in new[] { CurrentApi, PastApi })
            {
                try
                {
                    string Json = await Http.GetStringAsync(Url, Token).ConfigureAwait(false);
                    JToken RootToken = JToken.Parse(Json);
                    foreach (JObject Object in RootToken.DescendantsAndSelf().OfType<JObject>())
                    {
                        string Version = Value(Object, "version", "clientVersionUpload", "versionGuid", "hash");
                        if (!VersionPattern.IsMatch(Version ?? string.Empty)) continue;

                        string Channel = Value(Object, "channel", "channelName") ?? "LIVE";
                        DateTime? Released = Date(Value(Object, "date", "released", "releasedAt", "timestamp"));

                        Versions.Add(Describe(Version, Channel, Released));
                    }
                }
                catch (Exception x) { Program.Logger.Warn($"[Versions] catalog {Url}: {x.Message}"); }
            }

            // The official client-version endpoint is already queried at startup. If the optional historical
            // catalog is unavailable, the currently supported build still remains installable/pinnable.
            if (VersionPattern.IsMatch(AccountManager.CurrentVersion ?? string.Empty))
                Versions.Add(Describe(AccountManager.CurrentVersion, "LIVE", null));

            return Versions
                .GroupBy(Item => Item.Channel + "|" + Item.Version, StringComparer.OrdinalIgnoreCase)
                .Select(Group => Group.OrderByDescending(Item => Item.Released).First())
                .OrderByDescending(Item => Item.Released ?? DateTime.MinValue)
                .ThenByDescending(Item => Item.Version)
                .ToList();
        }

        public static object State()
        {
            string Version = PinnedVersion;
            string Channel = PinnedChannel;
            return new
            {
                pinned = string.IsNullOrEmpty(Version) ? null : Version,
                channel = Channel,
                installed = InstalledVersions().ToArray(),
                installing = Installing,
                fallbackToLive = BoolSetting("PinnedFallbackToLive", true)
            };
        }

        /// <summary>
        /// A pinned build is expected to become obsolete eventually. Roblox does not expose a stable structured
        /// "client too old" result in the launcher contract, so a non-current pin that fails before joining is
        /// treated as unsafe: atomically clear the global pin and retry this launch once through the normal live
        /// installation. Clearing the pin first is the retry guard — concurrent failing pinned clients see no pin
        /// and cannot each schedule another fallback.
        /// </summary>
        public static bool TryFallbackToLive(Account Account, LaunchOutcome Outcome, string Detail)
        {
            if (Account == null || !BoolSetting("PinnedFallbackToLive", true)) return false;
            if (Outcome != LaunchOutcome.DiedEarly && Outcome != LaunchOutcome.Disconnected) return false;

            string OldPin;
            string OldChannel;

            lock (FallbackGate)
            {
                OldPin = PinnedVersion;
                OldChannel = PinnedChannel;
                if (!VersionPattern.IsMatch(OldPin ?? string.Empty)) return false;
                if (!string.IsNullOrEmpty(AccountManager.CurrentVersion) &&
                    OldPin.Equals(AccountManager.CurrentVersion, StringComparison.OrdinalIgnoreCase)) return false;

                Unpin();
            }

            string Message = $"Pinned Roblox {OldPin} ({OldChannel}) failed before joining ({Detail}). Pin cleared; retrying with the standard live client.";
            Program.Logger.Warn("[Versions] " + Message);
            Forms.WebShell.NotifyVersions("fallback", new { message = Message, previous = OldPin, channel = OldChannel, live = AccountManager.CurrentVersion });

            if (!Relauncher.Destination(Account, out long PlaceId, out string JobId))
            {
                Program.Logger.Warn($"[Versions] {Account.Username}: live fallback could not retry because the original destination is unknown");
                return true;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000).ConfigureAwait(false);
                bool Started = await Relauncher.Request(Account, PlaceId, JobId, "pinned build failed; live fallback").ConfigureAwait(false);
                if (!Started)
                {
                    string Failure = $"{Account.Username}: pin was cleared, but the automatic live retry was blocked or failed. Launch the account again manually.";
                    Program.Logger.Warn("[Versions] " + Failure);
                    Forms.WebShell.NotifyVersions("fallback", new { message = Failure, previous = OldPin, live = AccountManager.CurrentVersion });
                }
            });

            return true;
        }

        public static IEnumerable<RobloxVersionInfo> InstalledVersions()
        {
            if (!Directory.Exists(Root)) yield break;

            foreach (string ChannelDirectory in Directory.EnumerateDirectories(Root))
            {
                string Channel = Path.GetFileName(ChannelDirectory);
                foreach (string VersionDirectory in Directory.EnumerateDirectories(ChannelDirectory))
                {
                    string Version = Path.GetFileName(VersionDirectory);
                    if (!VersionPattern.IsMatch(Version) || !IsInstalled(VersionDirectory)) continue;
                    yield return Describe(Version, Channel, null);
                }
            }
        }

        public static async Task<RobloxVersionInfo> InstallAsync(string Version, string Channel, IProgress<string> Progress = null)
        {
            if (!VersionPattern.IsMatch(Version ?? string.Empty)) throw new ArgumentException("Roblox version must look like version-<hash>.");
            Channel = SafeChannel(Channel);

            CancellationTokenSource Source = new CancellationTokenSource();
            lock (InstallGate)
            {
                if (InstallCancellation != null) { Source.Dispose(); throw new InvalidOperationException("Another Roblox version is already being installed."); }
                InstallCancellation = Source;
            }

            string Target = DirectoryOf(Version, Channel);
            string Packages = Path.Combine(Target, ".packages");
            if (IsInstalled(Target))
            {
                lock (InstallGate) if (ReferenceEquals(InstallCancellation, Source)) InstallCancellation = null;
                Source.Dispose();
                return Describe(Version, Channel, null);
            }
            Directory.CreateDirectory(Packages);

            try
            {
                CancellationToken Token = Source.Token;
                string Cdn = CdnFor(Channel);
                Say(Progress, $"Reading package manifest for {Version}…");

                string Manifest = await Http.GetStringAsync($"{Cdn}/{Version}-rbxPkgManifest.txt", Token).ConfigureAwait(false);
                List<RobloxPackage> ManifestPackages = ParseManifest(Manifest);
                if (ManifestPackages.Count == 0) throw new InvalidDataException("Roblox returned an empty or unknown package manifest.");

                long Total = ManifestPackages.Sum(Package => Math.Max(0, Package.Compressed));
                long Complete = 0;

                for (int Index = 0; Index < ManifestPackages.Count; Index++)
                {
                    Token.ThrowIfCancellationRequested();
                    RobloxPackage Package = ManifestPackages[Index];
                    string Archive = Path.Combine(Packages, Package.Name);

                    Say(Progress, $"[{Index + 1}/{ManifestPackages.Count}] {Package.Name}");
                    await DownloadPackage(Cdn, Version, Package, Archive, Complete, Total, Progress, Token).ConfigureAwait(false);

                    if (!Verify(Archive, Package.Hash))
                    {
                        File.Delete(Archive);
                        Say(Progress, $"Hash mismatch for {Package.Name}; downloading it once more…");
                        await DownloadPackage(Cdn, Version, Package, Archive, Complete, Total, Progress, Token, Resume: false).ConfigureAwait(false);
                        if (!Verify(Archive, Package.Hash)) throw new InvalidDataException($"Hash verification failed for {Package.Name}.");
                    }

                    Extract(Package, Archive, Target);
                    Complete += Math.Max(Package.Compressed, new FileInfo(Archive).Length);
                }

                File.WriteAllText(Path.Combine(Target, "AppSettings.xml"), AppSettingsXml, Encoding.UTF8);
                string Player = Path.Combine(Target, "RobloxPlayerBeta.exe");
                if (!File.Exists(Player)) throw new InvalidDataException("The package set did not contain RobloxPlayerBeta.exe.");
                File.WriteAllText(Path.Combine(Target, CompleteMarker), DateTime.UtcNow.ToString("O"), Encoding.UTF8);

                Say(Progress, $"Installed {Version} ({Channel}).");
                return Describe(Version, Channel, null);
            }
            catch
            {
                // Leave downloaded archives in .packages so the next attempt can resume. The completion marker is
                // written last, so even a failure after extracting RobloxPlayerBeta.exe can never be pinned.
                throw;
            }
            finally
            {
                lock (InstallGate) if (ReferenceEquals(InstallCancellation, Source)) InstallCancellation = null;
                Source.Dispose();
            }
        }

        public static bool CancelInstall()
        {
            lock (InstallGate)
            {
                if (InstallCancellation == null) return false;
                InstallCancellation.Cancel();
                return true;
            }
        }

        public static void Pin(string Version, string Channel = "LIVE")
        {
            if (!VersionPattern.IsMatch(Version ?? string.Empty)) throw new ArgumentException("Roblox version must look like version-<hash>.");
            Channel = SafeChannel(Channel);
            string Directory = DirectoryOf(Version, Channel);
            string Player = Path.Combine(Directory, "RobloxPlayerBeta.exe");
            if (!IsInstalled(Directory)) throw new FileNotFoundException("Install that version completely before pinning it.", Player);

            AccountManager.General.Set("PinnedRobloxVersion", Version);
            AccountManager.General.Set("PinnedRobloxChannel", Channel);
            AccountManager.IniSettings.Save("RAMSettings.ini");
            ClientLauncher.ForgetExecutable();
            Program.Logger.Info($"[Versions] pinned {Version} ({Channel})");
        }

        public static void Unpin()
        {
            AccountManager.General.RemoveProperty("PinnedRobloxVersion");
            AccountManager.General.RemoveProperty("PinnedRobloxChannel");
            AccountManager.IniSettings.Save("RAMSettings.ini");
            ClientLauncher.ForgetExecutable();
            Program.Logger.Info("[Versions] returned to Roblox's standard auto-updating installation");
        }

        private static async Task DownloadPackage(string Cdn, string Version, RobloxPackage Package, string Destination,
            long Complete, long Total, IProgress<string> Progress, CancellationToken Token, bool Resume = true)
        {
            long Existing = Resume && File.Exists(Destination) ? new FileInfo(Destination).Length : 0;
            if (Package.Compressed > 0 && Existing == Package.Compressed) return;
            if (Package.Compressed > 0 && Existing > Package.Compressed) { File.Delete(Destination); Existing = 0; }

            using HttpRequestMessage Request = new HttpRequestMessage(HttpMethod.Get, $"{Cdn}/{Version}-{Package.Name}");
            if (Existing > 0) Request.Headers.Range = new RangeHeaderValue(Existing, null);

            using HttpResponseMessage Response = await Http.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, Token).ConfigureAwait(false);
            if (Existing > 0 && Response.StatusCode == HttpStatusCode.OK)
            {
                // CDN ignored Range. Starting from zero avoids appending a full archive to a partial one.
                Existing = 0;
            }

            Response.EnsureSuccessStatusCode();
            Directory.CreateDirectory(Path.GetDirectoryName(Destination));

            using Stream Input = await Response.Content.ReadAsStreamAsync(Token).ConfigureAwait(false);
            using FileStream Output = new FileStream(Destination, Existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            byte[] Buffer = new byte[81920];
            long Written = Existing;

            while (true)
            {
                int Read = await Input.ReadAsync(Buffer.AsMemory(0, Buffer.Length), Token).ConfigureAwait(false);
                if (Read <= 0) break;
                await Output.WriteAsync(Buffer.AsMemory(0, Read), Token).ConfigureAwait(false);
                Written += Read;

                if (Total > 0)
                {
                    double Percent = Math.Min(100, (Complete + Written) * 100.0 / Total);
                    Say(Progress, $"{Package.Name}: {Percent:0.0}% of download");
                }
            }
        }

        internal static List<RobloxPackage> ParseManifest(string Text)
        {
            string[] Lines = (Text ?? string.Empty).Replace("\r", string.Empty).Split('\n')
                .Select(Line => Line.Trim()).Where(Line => Line.Length > 0).ToArray();
            int Start = Lines.Length > 0 && Lines[0].StartsWith("v", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var Packages = new List<RobloxPackage>();

            for (int Index = Start; Index + 3 < Lines.Length; Index += 4)
            {
                string Name = Lines[Index];
                string Hash = Lines[Index + 1];
                if (!Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(Name) != Name || Name.Contains('/') || Name.Contains('\\'))
                    throw new InvalidDataException($"Unsafe or unexpected package name '{Name}'.");
                if (!Regex.IsMatch(Hash, "^(?:[0-9a-fA-F]{32}|[0-9a-fA-F]{64})$"))
                    throw new InvalidDataException($"Package '{Name}' has an unsupported hash.");
                if (!long.TryParse(Lines[Index + 2], out long Compressed) || Compressed < 0 ||
                    !long.TryParse(Lines[Index + 3], out long Uncompressed) || Uncompressed < 0)
                    throw new InvalidDataException($"Package '{Name}' has invalid sizes.");
                Packages.Add(new RobloxPackage
                {
                    Name = Name,
                    Hash = Hash,
                    Compressed = Compressed,
                    Uncompressed = Uncompressed
                });
            }

            if ((Lines.Length - Start) % 4 != 0)
                throw new InvalidDataException("Roblox package manifest has an incomplete record.");

            return Packages;
        }

        private static void Extract(RobloxPackage Package, string Archive, string Target)
        {
            string Relative = ExtractRoots.TryGetValue(Package.Name, out string RootName) ? RootName : string.Empty;
            string Destination = Path.Combine(Target, Relative);
            Directory.CreateDirectory(Destination);
            ZipFile.ExtractToDirectory(Archive, Destination, true);
        }

        private static bool Verify(string FileName, string Expected)
        {
            if (string.IsNullOrWhiteSpace(Expected) || !File.Exists(FileName)) return false;
            Expected = Expected.Trim();

            try
            {
                using HashAlgorithm Algorithm = Expected.Length >= 64 ? SHA256.Create() : MD5.Create();
                using FileStream File = System.IO.File.OpenRead(FileName);
                string Actual = Convert.ToHexString(Algorithm.ComputeHash(File));
                return Actual.Equals(Expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static RobloxVersionInfo Describe(string Version, string Channel, DateTime? Released)
        {
            Channel = SafeChannel(Channel);
            return new RobloxVersionInfo
            {
                Version = Version,
                Channel = Channel,
                Released = Released,
                Installed = IsInstalled(DirectoryOf(Version, Channel)),
                Pinned = Version.Equals(PinnedVersion, StringComparison.OrdinalIgnoreCase) && Channel.Equals(PinnedChannel, StringComparison.OrdinalIgnoreCase)
            };
        }

        private static string CdnFor(string Channel) =>
            Channel.Equals("LIVE", StringComparison.OrdinalIgnoreCase) ? DefaultCdn : $"{DefaultCdn}/channel/{Uri.EscapeDataString(Channel)}";

        private static string SafeChannel(string Channel)
        {
            string Value = string.IsNullOrWhiteSpace(Channel) ? "LIVE" : Channel.Trim();
            foreach (char Character in Path.GetInvalidFileNameChars()) Value = Value.Replace(Character, '_');
            return Value;
        }

        private static bool IsInstalled(string Directory) =>
            File.Exists(Path.Combine(Directory, "RobloxPlayerBeta.exe")) &&
            File.Exists(Path.Combine(Directory, CompleteMarker));

        private static string Setting(string Name)
        {
            try { return AccountManager.General != null && AccountManager.General.Exists(Name) ? AccountManager.General.Get(Name) : string.Empty; }
            catch { return string.Empty; }
        }

        private static bool BoolSetting(string Name, bool Default)
        {
            try
            {
                if (AccountManager.General == null || !AccountManager.General.Exists(Name)) return Default;
                return bool.TryParse(AccountManager.General.Get(Name), out bool Value) ? Value : Default;
            }
            catch { return Default; }
        }

        private static string Value(JObject Object, params string[] Names)
        {
            foreach (string Name in Names)
            {
                JProperty Property = Object.Properties().FirstOrDefault(Candidate => Candidate.Name.Equals(Name, StringComparison.OrdinalIgnoreCase));
                if (Property?.Value.Type == JTokenType.String) return Property.Value.Value<string>();
            }
            return null;
        }

        private static DateTime? Date(string Value) => DateTime.TryParse(Value, out DateTime Parsed) ? Parsed : null;

        private static void Say(IProgress<string> Progress, string Message)
        {
            Progress?.Report(Message);
            Forms.WebShell.NotifyVersions("progress", new { message = Message });
            Program.Logger.Info("[Versions] " + Message);
        }
    }
}

using BrightIdeasSoftware;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
// WinForms gained its own TaskDialog/TaskDialogButton in .NET Core; alias to the WindowsAPICodePack ones
// these call sites were written against (they use Caption/InstructionText and custom button objects).
using TaskDialog = Microsoft.WindowsAPICodePack.Dialogs.TaskDialog;
using TaskDialogButton = Microsoft.WindowsAPICodePack.Dialogs.TaskDialogButton;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PuppeteerSharp;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Forms;
using RBX_Alt_Manager.Properties;
using RestSharp;
using Sodium;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebSocketSharp;

#pragma warning disable CS0618 // parameter warnings

namespace RBX_Alt_Manager
{
    public partial class AccountManager : Form
    {
        public static AccountManager Instance;
        public static List<Account> AccountsList;
        public static List<Account> SelectedAccounts;
        public static List<Game> RecentGames;
        public static Account SelectedAccount;
        public static Account LastValidAccount; // this is used for the Batch class since getting place details requires authorization, auto updates whenever an account is used
        public static RestClient MainClient;
        public static RestClient AvatarClient;
        public static RestClient FriendsClient;
        public static RestClient UsersClient;
        public static RestClient AuthClient;
        public static RestClient EconClient;
        public static RestClient AccountClient;
        public static RestClient GameJoinClient;
        public static RestClient Web13Client;
        public static RestClient CatalogClient;
        public static RestClient ApisClient;
        public static RestClient InventoryClient;
        public static RestClient GamesClient;
        public static string CurrentPlaceId { get => Instance.PlaceID.Text; }
        public static string CurrentJobId { get => Instance.JobID.Text; }
        private ArgumentsForm afform;
        private ServerList ServerListForm;
        private AccountUtils UtilsForm;
        private ImportForm ImportAccountsForm;
        private AccountFields FieldsForm;
        private ThemeEditor ThemeForm;
        private AccountControl ControlForm;
        private SettingsForm SettingsForm;
        private RecentGamesForm RGForm;
        private readonly static DateTime startTime = DateTime.Now;
        public static bool IsTeleport = false;
        public static bool UseOldJoin = false;
        public static bool ShuffleJobID = false;
        public static string CurrentVersion;
        public OLVListItem SelectedAccountItem { get; private set; }
        private WebServer AltManagerWS;
        private string WSPassword { get; set; }
        public System.Timers.Timer AutoCookieRefresh { get; private set; }

        public static IniFile IniSettings;
        public static IniSection General;
        public static IniSection Developer;
        public static IniSection WebServer;
        public static IniSection AccountControl;
        public static IniSection Watcher;
        public static IniSection Prompts;

        private static Mutex rbxMultiMutex;
        private readonly static object saveLock = new object();
        private readonly static object rgSaveLock = new object();
        internal readonly static object AccountsLock = new object(); // guards all access to AccountsList across UI/timer/webserver threads
        private static System.Threading.Timer SaveDebounceTimer; // coalesces bursts of saves into a single background write
        private static volatile bool SavePending;
        private static DateTime LastBackupTime = DateTime.MinValue;
        private const int SaveDebounceMs = 1500;
        public event EventHandler<GameArgs> RecentGameAdded;

        private bool IsResettingPassword;
        private bool IsDownloadingChromium;
        private bool LaunchNext;
        private CancellationTokenSource LauncherToken;

        // The old hardcoded DPAPI entropy ("ROBLOX ACCOUNT MANAGER | :) | BROUGHT TO YOU BUY ic3w0lf"). This was
        // never a secret — it is a constant baked into every copy of the binary, so it added no protection at all.
        // Kept ONLY so stores written by older builds still decrypt; the next save re-protects with the real key.
        private static readonly byte[] LegacyEntropy = new byte[] { 0x52, 0x4f, 0x42, 0x4c, 0x4f, 0x58, 0x20, 0x41, 0x43, 0x43, 0x4f, 0x55, 0x4e, 0x54, 0x20, 0x4d, 0x41, 0x4e, 0x41, 0x47, 0x45, 0x52, 0x20, 0x7c, 0x20, 0x3a, 0x29, 0x20, 0x7c, 0x20, 0x42, 0x52, 0x4f, 0x55, 0x47, 0x48, 0x54, 0x20, 0x54, 0x4f, 0x20, 0x59, 0x4f, 0x55, 0x20, 0x42, 0x55, 0x59, 0x20, 0x69, 0x63, 0x33, 0x77, 0x30, 0x6c, 0x66 };

        [DllImport("DwmApi")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, int[] attrValue, int attrSize);

        public static void SetDarkBar(IntPtr Handle)
        {
            if (ThemeEditor.UseDarkTopBar && DwmSetWindowAttribute(Handle, 19, new[] { 1 }, 4) != 0)
                DwmSetWindowAttribute(Handle, 20, new[] { 1 }, 4);
        }

        public AccountManager()
        {
            Instance = this;

            ThemeEditor.LoadTheme();

            SetDarkBar(Handle);

            IniSettings = File.Exists(Path.Combine(Environment.CurrentDirectory, "RAMSettings.ini")) ? new IniFile("RAMSettings.ini") : new IniFile();

            General = IniSettings.Section("General");
            Developer = IniSettings.Section("Developer");
            WebServer = IniSettings.Section("WebServer");
            AccountControl = IniSettings.Section("AccountControl");
            Watcher = IniSettings.Section("Watcher");
            Prompts = IniSettings.Section("Prompts");

            General.Seed("EnableMultiRbx", "false", "Allow more than one Roblox client at a time. Roblox itself refuses a second client, so the manager holds the lock it checks. Turn it on before Roblox is open, not after.");
            General.Seed("AccountJoinDelay", "8", "Seconds between launches when several accounts start at once. Counted per exit address, so accounts on different proxies do not wait for each other.");
            General.Seed("ImportDelay", "1000", "Delay in milliseconds between validating each cookie during bulk import. Raise it if you still hit rate limits, lower it (e.g. 0) to import faster.");
            General.Seed("AsyncJoin", "false", "Start the next client as soon as the previous one's process is up, instead of waiting the delay. Faster, and far more likely to be rate limited.");
            General.Seed("DisableAgingAlert", "false");
            General.Seed("SavePasswords", "true");
            General.Seed("ServerRegionFormat", "<city>, <countryCode>", "Visit http://ip-api.com/json/1.1.1.1 to see available format options");
            General.Seed("MaxRecentGames", "8");
            General.Seed("ShuffleChoosesLowestServer", "false");
            General.Seed("ShufflePageCount", "5");
            General.Seed("IPApiLink", "http://ip-api.com/json/<ip>");
            if (!General.Exists("WindowScale"))
            {
                General.Set("WindowScale", Screen.PrimaryScreen.Bounds.Height <= Screen.PrimaryScreen.Bounds.Width /*scuffed*/ ? Math.Max(Math.Min(Screen.PrimaryScreen.Bounds.Height / 1080f, 2f), 1f).ToString(".0#", CultureInfo.InvariantCulture) : "1.0");

                if (Program.Scale > 1)
                    if (!Utilities.YesNoPrompt("SkrilyaAccountManager", "SkrilyaAccountManager has detected you have a monitor larger than average", $"Would you like to keep the WindowScale setting of {Program.Scale:F2}?", false))
                        General.Set("WindowScale", "1.0");
                    else
                        MessageBox.Show("In case the font scaling is incorrect, open RAMSettings.ini and change \"ScaleFonts=true\" to \"ScaleFonts=false\" without the quotes.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            General.Seed("ScaleFonts", "true");
            General.Seed("AutoCookieRefresh", "true");
            General.Seed("AutoCloseLastProcess", "true");
            General.Seed("ShowPresence", "true");
            General.Seed("PresenceUpdateRate", "5", "Minutes between checks of what each account is doing. Every account is asked about, in batches, not just the rows on screen.");
            General.Seed("UnlockFPS", "false");
            General.Seed("MaxFPSValue", "120");

            // Per-instance resource control (Classes/ResourceManager.cs). Defaults are safe for a single client:
            // with PerfDynamicPriority the focused window always stays at Normal, so nothing is ever throttled
            // unless there are background clients to throttle. The rest is opt-in.
            General.Seed("PerfManage", "true");
            General.Seed("PerfDynamicPriority", "true");
            General.Seed("PerfBackgroundPriority", "below"); // below | idle | normal
            General.Seed("PerfAffinityMode", "all"); // all | vcache | nonvcache
            General.Seed("PerfTrimOnMinimize", "false");
            General.Seed("PerfAutoMinimizeAlts", "false");
            General.Seed("PerfLowGraphics", "false");

            // Free item collection. Defaults are deliberately slow: the catalog endpoints 429 far earlier than
            // their documented limits, and a limit earned here applies to the whole IP, including running clients.
            General.Seed("FreeItemsDelay", "1500", "Delay in milliseconds between purchases within one account.");
            General.Seed("FreeItemsSearchDelay", "1200", "Delay in milliseconds between catalog lookups during discovery.");
            General.Seed("FreeItemsMaxConcurrentAccounts", "3");
            General.Seed("FreeItemsMaxPerRun", "50", "Maximum items collected per account in a single run.");
            General.Seed("FreeItemsSkipOwned", "true", "Check ownership before buying. Costs one request per item but avoids pointless purchase calls.");

            // Account warmer. Off by default: it acts on accounts on a timer without you watching, so opting in
            // should be a deliberate choice rather than something that starts happening after an update.
            General.Seed("WarmerEnabled", "false", "Periodically build ordinary activity (favouriting games from the public charts) on your accounts.");
            General.Seed("WarmerGroup", "", "Only warm accounts in this group. Empty means every valid account.");
            General.Seed("WarmerIntervalMinutes", "180", "Minutes between scheduled warming passes. Minimum 15.");
            General.Seed("WarmerFavoritesPerPass", "2", "Games favourited per account per pass. Keep it small - the point is a gradual history, not a burst.");
            General.Seed("WarmerDelay", "2500", "Delay in milliseconds between warming actions within one account.");
            General.Seed("WarmerMaxConcurrentAccounts", "2");

            // Per-account proxies and per-instance client control. Everything here is off by default except the
            // direct launch, which replaces a protocol-handler launch that cannot work on this framework
            // (ProcessStartInfo defaults to UseShellExecute = false on .NET, so Process.Start on a
            // "roblox-player:" URI throws) and gives the launch its PID, its own environment and its own window.
            General.Seed("DirectLaunch", "true", "Start RobloxPlayerBeta.exe directly instead of through the roblox-player: protocol. Required for per-account proxies, window titles and launch tracking.");
            General.Seed("UseAccountProxies", "false", "Route an account's login AND its client through the proxy in that account's Proxy field. Accepts host:port, host:port:user:pass, user:pass@host:port or scheme://... (http, socks4, socks5).");
            General.Seed("ProxySplitTunnel", "true", "Send only identity traffic (login/join) through the proxy and fetch game assets directly. Much faster and far less proxy traffic; the exit IP that matters is still the proxy's.");
            General.Seed("ProxyIdentityHosts", "auth.roblox.com,gamejoin.roblox.com,assetgame.roblox.com,apis.roblox.com,users.roblox.com,www.roblox.com,web.roblox.com,presence.roblox.com", "Hosts that must go through the proxy when split tunnelling. Subdomains included automatically.");
            General.Seed("ProxyVerifyExitIP", "true", "Check the proxy's exit IP before spending an authentication ticket on it. Catches dead proxies early and warns when a proxy rotates (a rotating IP makes Roblox reject the ticket with 403).");
            General.Seed("RenameClientWindows", "false", "Rename each client's window after the account running in it.");
            General.Seed("ClientWindowTitle", "{name} - Roblox", "Window title template. Placeholders: {name} {username} {alias} {userid} {place} {pid}");
            General.Seed("UseSeparateDesktop", "false", "Launch clients on a separate named desktop: their windows are not on screen at all. Window positioning and window titles cannot be seen there.");
            General.Seed("ClientDesktopName", "RAMClients", "Name of the desktop used by UseSeparateDesktop.");
            General.Seed("CloseRobloxErrorDialogs", "false", "Automatically close Roblox's Win32 error message boxes (the modals left behind by a failed launch).");
            General.Seed("WatchLaunchResult", "true", "Read each launched client's log to record whether it actually joined, disconnected, or died with a rejected ticket.");
            General.Seed("DefaultGroup", "Default", "The group new accounts are put in when none is picked while adding them.");

            General.Seed("NewUI", "true", "Open the new interface on startup. It is the main window now; the old one stays open alongside it because a few screens (Nexus, account utilities, themes) have not moved yet.");
            General.Seed("UiLanguage", "en", "Language of the new HTML interface: en or ru. Changed from the language chip in its title bar. The old window is English only.");

            General.Seed("ReaperEnabled", "false", "Kill Roblox clients that crashed but whose process is still running (no window, hundreds of MB held). Only touches real game clients that have been window-less past the grace period.");
            General.Seed("ReaperDryRun", "false", "Report what the reaper would kill without killing anything. Use this for a day before trusting it.");
            General.Seed("ReaperGraceSeconds", "90", "How long a client may run without a window before it counts as dead. Lower it only if your machine boots clients fast.");
            General.Seed("ReaperIntervalSeconds", "30", "Seconds between sweeps.");

            General.Seed("StuckDetectorEnabled", "true", "Read each running client's own log and classify it: joining, in game, refused by Roblox, or stuck without ever joining. Diagnostic unless StuckKill is on.");
            General.Seed("StuckKill", "false", "Close clients the detector calls dead weight (refused ticket, or never joined). Off by default: a wrong verdict costs a session.");
            General.Seed("StuckLaunchGraceSeconds", "75", "Below this age a client that has not joined is simply still starting.");
            General.Seed("StuckAfterSeconds", "240", "Above this age, a client that never joined counts as stuck.");
            General.Seed("RelaunchEnabled", "false", "Put an account back where it was when its client dies. Uses the launch watcher and the stuck detector, so it does not need Nexus.");
            General.Seed("RelaunchOnCrash", "true", "Relaunch when a client died before joining or dropped straight out.");
            General.Seed("RelaunchOnStuck", "false", "Also relaunch clients the detector calls stuck or refused. Off by default: those causes often repeat.");
            General.Seed("RelaunchCooldownSeconds", "600", "The same account is never relaunched more often than this.");
            General.Seed("RelaunchMaxPerHour", "12", "Ceiling across all accounts. Stops a Roblox outage from turning into hundreds of login attempts from one address.");
            General.Seed("RelaunchGiveUpAfter", "3", "Consecutive failures after which an account is left alone until you look at it.");

            General.Seed("StuckKillAuthErrors", "false", "Close clients Roblox refused a ticket to. They keep a window and a few hundred MB and will never join.");
            General.Seed("StuckKillStuck", "false", "Close clients that never joined anything. Less certain than a refused ticket - a slow machine or a long queue looks the same.");
            General.Seed("StuckAuthErrorGraceSeconds", "10", "A refused ticket is unambiguous immediately; it does not need the full grace period.");
            General.Seed("ReaperWhitelist", "0", "Process ids neither sweeper may ever touch, comma separated. 0 is a placeholder and matches nothing.");
            General.Seed("ReaperKillOrphanCrashHandlers", "true", "Close RobloxCrashHandler processes left behind once no client is running.");

            General.Seed("StuckIntervalSeconds", "30", "Seconds between classification passes.");

            Developer.Seed("DevMode", "false");
            Developer.Seed("EnableWebServer", "false");

            WebServer.Seed("WebServerPort", "7963");
            WebServer.Seed("AllowGetCookie", "false");
            WebServer.Seed("AllowGetAccounts", "false");
            WebServer.Seed("AllowLaunchAccount", "false");
            WebServer.Seed("AllowAccountEditing", "false");
            WebServer.Seed("Password", "");
            WSPassword = WebServer.Get("Password") ?? ""; // never leave WSPassword null (was NRE on fresh config)
            WebServer.Seed("EveryRequestRequiresPassword", "false");
            WebServer.Seed("AllowExternalConnections", "false");

            AccountControl.Seed("AllowExternalConnections", "false");
            AccountControl.Seed("RelaunchDelay", "60");
            AccountControl.Seed("LauncherDelayNumber", "9");
            AccountControl.Seed("NexusPort", "5242");

            Classes.AccountProxies.LoadSettings();

            // From here on the logic layer has a user interface to talk to. Until this line (and in any host
            // that never sets it) Shell stays headless: messages are logged and questions answer themselves
            // with "no", instead of a background thread blocking on a modal nobody can see.
            Classes.Shell.Current = new Classes.WinFormsShell();

            InitializeComponent();
            this.Rescale();

            AccountsList = new List<Account>();
            SelectedAccounts = new List<Account>();

            AccountsView.SetObjects(AccountsList);

            if (ThemeEditor.UseDarkTopBar) Icon = Properties.Resources.team_KX4_icon_white; // this has to go after or icon wont actually change

            AccountsView.UnfocusedHighlightBackgroundColor = Color.FromArgb(0, 150, 215);
            AccountsView.UnfocusedHighlightForegroundColor = Color.FromArgb(240, 240, 240);

            SimpleDropSink sink = AccountsView.DropSink as SimpleDropSink;
            sink.CanDropBetween = true;
            sink.CanDropOnItem = true;
            sink.CanDropOnBackground = false;
            sink.CanDropOnSubItem = false;
            sink.CanDrop += Sink_CanDrop;
            sink.Dropped += Sink_Dropped;
            sink.FeedbackColor = Color.FromArgb(33, 33, 33);

            AccountsView.AlwaysGroupByColumn = Group;

            Group.GroupKeyGetter = delegate (object account)
            {
                return ((Account)account).Group;
            };

            Group.GroupKeyToTitleConverter = delegate (object Key)
            {
                string GroupName = Key as string;
                Match match = Regex.Match(GroupName, @"\d{1,3}\s?");

                if (match.Success)
                    return GroupName.Substring(match.Length);
                else
                    return GroupName;
            };

            var VCKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86");

            if (!Prompts.Exists("VCPrompted") && (VCKey == null || (VCKey is RegistryKey && VCKey.GetValue("Bld") is int VCVersion && VCVersion < 32532)))
                Task.Run(async () => // Make sure the user has the latest 2015-2022 vcredist installed
                {
                    using HttpClient Client = new HttpClient();
                    byte[] bs = await Client.GetByteArrayAsync("https://aka.ms/vs/17/release/vc_redist.x86.exe");
                    string FN = Path.Combine(Path.GetTempPath(), "vcredist.tmp");

                    File.WriteAllBytes(FN, bs);

                    Process.Start(new ProcessStartInfo(FN) { UseShellExecute = false, Arguments = "/q /norestart" }).WaitForExit();

                    Prompts.Set("VCPrompted", "1");
                });
        }

        private void Sink_CanDrop(object sender, OlvDropEventArgs e)
        {
            if (e.DataObject is OLVDataObject) return; // let the model-drop handlers deal with account reordering

            if (e.DragEventArgs.Data.GetDataPresent(DataFormats.FileDrop) || e.DragEventArgs.Data.GetDataPresent(DataFormats.Text))
                e.Effect = DragDropEffects.Copy;
        }

        private void Sink_Dropped(object sender, OlvDropEventArgs e)
        {
            if (e.Effect != DragDropEffects.Copy) return;

            // Drop a text selection OR one/more files (e.g. an export from a farmer panel) straight onto the list.
            if (e.DragEventArgs.Data.GetDataPresent(DataFormats.FileDrop) && e.DragEventArgs.Data.GetData(DataFormats.FileDrop) is string[] Files)
                _ = ImportCookiesFromFiles(Files);
            else if (e.DragEventArgs.Data.GetDataPresent(DataFormats.Text) && e.DragEventArgs.Data.GetData(DataFormats.Text) is string Text)
                _ = ImportCookiesFromText(Text);
        }

        // The account store lives in a per-user, ACL'd directory (%LOCALAPPDATA%\SkrilyaAccountManager) instead of
        // the working directory, so a file full of full-takeover cookies can't end up synced into OneDrive, zipped
        // up with the app folder, or dropped on a network share. Legacy files in the CWD are migrated on first run.
        private readonly static string DataDirectory = ResolveDataDirectory();

        // Per-install random DPAPI entropy, generated once and stored DPAPI-wrapped. Replaces the public constant
        // above, so knowing a value from the binary is no longer enough to decrypt another install's store.
        private static readonly byte[] Entropy = LoadOrCreateEntropy();

        private readonly static string SaveFilePath = Path.Combine(DataDirectory, "AccountData.json");
        private readonly static string RecentGamesFilePath = Path.Combine(DataDirectory, "RecentGames.json"); // i shouldve combined everything that isnt accountdata into one file but oh well im too lazy : |

        private static string ResolveDataDirectory()
        {
            try
            {
                string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkrilyaAccountManager");

                Directory.CreateDirectory(Dir); // LocalAppData is already ACL'd to the current user — that's the point

                // One-time migration of a legacy working-directory store (plus its rolling backups). Move rather
                // than copy, so the sensitive file doesn't linger where it was. Failures are non-fatal.
                string LegacyAccount = Path.Combine(Environment.CurrentDirectory, "AccountData.json");
                string TargetAccount = Path.Combine(Dir, "AccountData.json");

                if (!File.Exists(TargetAccount) && File.Exists(LegacyAccount))
                    foreach (string Suffix in new[] { "", ".backup", ".bak", ".old" })
                    {
                        string Src = LegacyAccount + Suffix, Dst = TargetAccount + Suffix;

                        if (File.Exists(Src) && !File.Exists(Dst))
                            try { File.Move(Src, Dst); } catch (Exception ex) { Program.Logger.Warn($"Could not migrate {Src}: {ex.Message}"); }
                    }

                string LegacyRecent = Path.Combine(Environment.CurrentDirectory, "RecentGames.json");
                string TargetRecent = Path.Combine(Dir, "RecentGames.json");

                if (File.Exists(LegacyRecent) && !File.Exists(TargetRecent))
                    try { File.Move(LegacyRecent, TargetRecent); } catch { }

                return Dir;
            }
            catch (Exception ex)
            {
                Program.Logger.Error($"Could not use %LOCALAPPDATA%\\SkrilyaAccountManager, falling back to the working directory: {ex}");

                return Environment.CurrentDirectory;
            }
        }

        private static byte[] LoadOrCreateEntropy()
        {
            try
            {
                string EntropyPath = Path.Combine(DataDirectory, "entropy.bin");

                if (File.Exists(EntropyPath))
                    return ProtectedData.Unprotect(File.ReadAllBytes(EntropyPath), null, DataProtectionScope.CurrentUser);

                byte[] Key = new byte[32];

                using (var Rng = RandomNumberGenerator.Create()) Rng.GetBytes(Key);

                File.WriteAllBytes(EntropyPath, ProtectedData.Protect(Key, null, DataProtectionScope.CurrentUser));

                return Key;
            }
            catch (Exception ex)
            {
                // If the per-install key can't be persisted, fall back to the legacy entropy so the app still
                // works — no worse than before, and logged so it's diagnosable.
                Program.Logger.Error($"Could not create/load per-install entropy, using legacy entropy: {ex}");

                return LegacyEntropy;
            }
        }

        /// <summary>
        /// DPAPI-decrypt with the current per-install entropy, falling back to the old public constant so a store
        /// written by an older build still loads. It gets re-protected with the new key on the next save.
        /// </summary>
        private static bool TryUnprotect(byte[] Data, out byte[] Plain)
        {
            foreach (byte[] Ent in new[] { Entropy, LegacyEntropy })
                try { Plain = ProtectedData.Unprotect(Data, Ent, DataProtectionScope.CurrentUser); return true; }
                catch (CryptographicException) { }

            Plain = null;

            return false;
        }

        private void RefreshView(object obj = null)
        {
            AccountsView.InvokeIfRequired(() =>
            {
                AccountsView.BuildList();
                if (AccountsView.ShowGroups) AccountsView.BuildGroups();

                if (obj != null)
                {
                    AccountsView.RefreshObject(obj);
                    AccountsView.EnsureModelVisible(obj);
                }
            });
        }

        private static ReadOnlyMemory<byte> PasswordHash; // Store the hash after the data is successfully decrypted so we can encrypt again.

        /// <summary>
        /// Whether a store starts with the password-locked header. The length check is the point: the header is
        /// 64 bytes, so any shorter file — a plain "[]", or one truncated by a write that never finished — used to
        /// throw out of the loader and take the rest of startup with it, including the new interface.
        /// </summary>
        private static bool IsPasswordLocked(byte[] Data) =>
            Data != null && Data.Length >= Cryptography.RAMHeader.Length &&
            new ReadOnlySpan<byte>(Data, 0, Cryptography.RAMHeader.Length).SequenceEqual(Cryptography.RAMHeader);

        /// <summary>
        /// A store holding an empty list and nothing else. It cannot be truncated ciphertext — that would start
        /// with the 64-byte header — and it holds no secret, so reading it needs neither a key nor the opt-out
        /// file. Without this, an empty store greets the user with "Failed to load accounts".
        /// </summary>
        private static bool IsEmptyStore(byte[] Data)
        {
            if (Data == null || Data.Length > 8) return false;

            string Text = Encoding.UTF8.GetString(Data).Trim().TrimStart('﻿');

            return Text.Length == 0 || Text == "[]";
        }

        private void LoadAccounts(byte[] Hash = null)
        {
            bool EnteredPassword = false;
            byte[] Data = File.Exists(SaveFilePath) ? File.ReadAllBytes(SaveFilePath) : Array.Empty<byte>();

            if (Data.Length > 0)
            {
                if (IsPasswordLocked(Data))
                {
                    if (Hash == null)
                    {
                        EncryptionSelectionPanel.Visible = false;
                        PasswordSelectionPanel.Visible = false;
                        PasswordLayoutPanel.Visible = true;
                        PasswordPanel.Visible = true;
                        PasswordPanel.BringToFront();
                        PasswordTextBox.Focus();

                        SyncSecurity();

                        return;
                    }

                    Data = Cryptography.Decrypt(Data, Hash);
                    AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(Data));
                    PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(Hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser));

                    PasswordPanel.Visible = false;
                    EnteredPassword = true;
                }
                else if (TryUnprotect(Data, out byte[] Plain))
                    AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(Plain));
                else if (IsEmptyStore(Data))
                    AccountsList = new List<Account>();      // nothing stored is not a decryption failure
                else
                {
                    // Neither the per-install nor the legacy entropy could decrypt this. Only treat it as raw
                    // JSON if the user explicitly opted out of encryption via the sentinel file — otherwise it is
                    // corrupt or from another machine, and must never be silently trusted as plaintext.
                    bool NoEncryptionOptOut = File.Exists(Path.Combine(Environment.CurrentDirectory, "NoEncryption.IUnderstandTheRisks.iautamor"));

                    try
                    {
                        if (!NoEncryptionOptOut) throw new CryptographicException("Encrypted store failed to decrypt and no NoEncryption opt-out file is present.");

                        AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(Data));
                    }
                    catch (Exception e)
                    {
                        File.WriteAllBytes(SaveFilePath + ".bak", Data);

                        MessageBox.Show($"Failed to load accounts!\nA backup file was created in case the data can be recovered.\n\n{e.Message}", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }

            AccountsList ??= new List<Account>();

            if (!EnteredPassword && AccountsList.Count == 0 && File.Exists($"{SaveFilePath}.backup") && File.ReadAllBytes($"{SaveFilePath}.backup") is byte[] BackupData && BackupData.Length > 0)
            {
                if (IsPasswordLocked(BackupData) && MessageBox.Show("The existing backup file is password-locked, would you like to attempt to load it?", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    if (File.Exists(SaveFilePath))
                    {
                        if (File.Exists($"{SaveFilePath}.old")) File.Delete($"{SaveFilePath}.old");

                        File.Move(SaveFilePath, $"{SaveFilePath}.old");
                    }

                    File.Move($"{SaveFilePath}.backup", SaveFilePath);

                    LoadAccounts();

                    return;
                }

                if (MessageBox.Show("No accounts were loaded but there is a backup file, would you like to load the backup file?", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.Yes)
                {
                    try
                    {
                        if (!TryUnprotect(BackupData, out byte[] BackupPlain)) throw new CryptographicException("Backup could not be decrypted with either the per-install or the legacy key.");

                        AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(BackupPlain));
                    }
                    catch
                    {
                        try { AccountsList = JsonConvert.DeserializeObject<List<Account>>(Encoding.UTF8.GetString(BackupData)); }
                        catch { MessageBox.Show("Failed to load backup file!", "SkrilyaAccountManager", MessageBoxButtons.OKCancel, MessageBoxIcon.Error); }
                    }
                }
            }

            AccountsView.SetObjects(AccountsList);
            RefreshView();

            if (AccountsList.Count > 0)
            {
                LastValidAccount = AccountsList[0];

                foreach (Account account in AccountsList)
                    if (account.LastUse > LastValidAccount.LastUse)
                        LastValidAccount = account;
            }
        }

        /// <summary>
        /// Requests that accounts be persisted. Calls are coalesced (debounced) into a single background
        /// write, so bursts (bulk import, per-field edits, CSRF refreshes across thousands of accounts) no
        /// longer each trigger a full serialize+encrypt+disk-write - the main cause of freezing at scale.
        /// </summary>
        public static void SaveAccounts(bool BypassRateLimit = false, bool BypassCountCheck = false)
        {
            if ((!BypassRateLimit && (DateTime.Now - startTime).TotalSeconds < 2) || (!BypassCountCheck && (AccountsList == null || AccountsList.Count == 0))) return;

            lock (saveLock)
            {
                SavePending = true;

                if (SaveDebounceTimer == null)
                    SaveDebounceTimer = new System.Threading.Timer(_ => FlushPendingSave(), null, Timeout.Infinite, Timeout.Infinite);

                SaveDebounceTimer.Change(SaveDebounceMs, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Immediately writes any pending account changes to disk. Safe to call from any thread; used on
        /// shutdown and after critical operations (encryption changes) so nothing is lost.
        /// </summary>
        public static void FlushPendingSave()
        {
            lock (saveLock)
            {
                if (!SavePending) return;
                SavePending = false;
            }

            WriteAccountsToDisk();
        }

        private static void WriteAccountsToDisk()
        {
            lock (saveLock)
            {
                try
                {
                    List<Account> Snapshot;
                    lock (AccountsLock) Snapshot = new List<Account>(AccountsList);

                    string SaveData = JsonConvert.SerializeObject(Snapshot);

                    // Refresh the backup from the last known-good on-disk copy, at most every 8 hours.
                    if (File.Exists(SaveFilePath) && (DateTime.Now - LastBackupTime).TotalMinutes > 60 * 8)
                        try { File.Copy(SaveFilePath, $"{SaveFilePath}.backup", true); LastBackupTime = DateTime.Now; } catch { }

                    byte[] Output;

                    if (!PasswordHash.IsEmpty)
                        Output = Cryptography.Encrypt(SaveData, ProtectedData.Unprotect(PasswordHash.ToArray(), Array.Empty<byte>(), DataProtectionScope.CurrentUser));
                    else if (File.Exists(Path.Combine(Environment.CurrentDirectory, "NoEncryption.IUnderstandTheRisks.iautamor")))
                        Output = Encoding.UTF8.GetBytes(SaveData);
                    else
                        // CurrentUser, not LocalMachine. A LocalMachine blob is decryptable by ANY user or process
                        // on the box — which for a file full of .ROBLOSECURITY cookies defeats the point of
                        // encrypting it. (Reads already used CurrentUser, so this was also a scope mismatch.)
                        Output = ProtectedData.Protect(Encoding.UTF8.GetBytes(SaveData), Entropy, DataProtectionScope.CurrentUser);

                    // Atomic write: write a temp file then swap it in, so a crash mid-write can't corrupt AccountData.json.
                    string TempFile = SaveFilePath + ".tmp";

                    File.WriteAllBytes(TempFile, Output);

                    if (File.Exists(SaveFilePath))
                        File.Replace(TempFile, SaveFilePath, null);
                    else
                        File.Move(TempFile, SaveFilePath);
                }
                catch (Exception x) { Program.Logger.Error($"Failed to save accounts: {x}"); }
            }
        }

        /// <summary>Returns a thread-safe snapshot of the accounts list for enumeration off the UI thread.</summary>
        public static List<Account> GetAccountsSnapshot()
        {
            lock (AccountsLock) return new List<Account>(AccountsList);
        }

        public void ResetEncryption(bool ManualReset = false)
        {
            foreach (var Form in Application.OpenForms.OfType<Form>())
                if (Form != this)
                    Form.Hide();

            IsResettingPassword = true;

            PasswordLayoutPanel.Visible = !PasswordHash.IsEmpty && ManualReset;
            PasswordSelectionPanel.Visible = false;
            EncryptionSelectionPanel.Visible = PasswordHash.IsEmpty || !ManualReset;

            PasswordPanel.Visible = true;
            PasswordPanel.BringToFront();

            SyncSecurity();
        }

        private void PasswordTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == (char)Keys.Return)
            {
                UnlockButton.PerformClick();

                e.Handled = true;
            }
        }

        private void Error(string Message)
        {
            Program.Logger.Error(Message);

            throw new Exception(Message);
        }

        private void UnlockButton_Click(object sender, EventArgs e)
        {
            try
            {
                byte[] Hash = CryptoHash.Hash(PasswordTextBox.Text);

                if (PasswordTextBox.Text.Length < 4)
                    Error("Invalid password, your password must contain 4 or more characters");

                if (IsResettingPassword)
                {
                    byte[] Data = File.Exists(SaveFilePath) ? File.ReadAllBytes(SaveFilePath) : Array.Empty<byte>();

                    if (Data.Length > 0)
                    {
                        if (IsPasswordLocked(Data))
                        {
                            if (Hash == null)
                            {
                                EncryptionSelectionPanel.Visible = false;
                                PasswordSelectionPanel.Visible = false;
                                PasswordLayoutPanel.Visible = true;
                                PasswordPanel.Visible = true;
                                PasswordPanel.BringToFront();
                                PasswordTextBox.Focus();

                                return;
                            }

                            Cryptography.Decrypt(Data, Hash);

                            PasswordLayoutPanel.Visible = false;
                            EncryptionSelectionPanel.Visible = true;
                            IsResettingPassword = false;

                            SyncSecurity();
                        }
                    }
                }
                else
                    LoadAccounts(Hash);
            }
            catch (Exception exception)
            {
                MessageBox.Show($"Incorrect Password!\n\n{exception.Message}", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { PasswordTextBox.Text = string.Empty; PasswordTextBox.Focus(); }
        }

        private void DefaultEncryptionButton_Click(object sender, EventArgs e)
        {
            PasswordHash = Array.Empty<byte>();
            SaveAccounts(true, true);
            FlushPendingSave();

            PasswordPanel.Visible = false;
        }

        private void PasswordEncryptionButton_Click(object sender, EventArgs e)
        {
            EncryptionSelectionPanel.Visible = false;
            PasswordLayoutPanel.Visible = false;
            PasswordSelectionPanel.Visible = true;

            SyncSecurity();
        }

        private ReadOnlyMemory<byte> LastHash = null;

        private void SetPasswordButton_Click(object sender, EventArgs e)
        {
            if (PasswordSelectionTB.Text.Length < 4)
            {
                MessageBox.Show("Invalid password, your password must contain 4 or more characters", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            byte[] Hash = CryptoHash.Hash(PasswordSelectionTB.Text);

            PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(Hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser));

            if (LastHash.IsEmpty)
            {
                LastHash = new ReadOnlyMemory<byte>(PasswordHash.ToArray());
                PasswordSelectionTB.Text = string.Empty;
                MessageBox.Show("Please confirm your password.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
            }
            else
            {
                if (ProtectedData.Unprotect(LastHash.ToArray(), Array.Empty<byte>(), DataProtectionScope.CurrentUser).SequenceEqual(Hash.ToArray()))
                {
                    SaveAccounts(true, true);
                    FlushPendingSave();

                    PasswordSelectionTB.Text = string.Empty;
                    PasswordPanel.Visible = false;

                    LastHash = null;
                }
                else
                {
                    // Reset so the next attempt starts a fresh set/confirm pair. Without this the flow kept
                    // comparing every new confirmation against the stale first-attempt hash, so once the two
                    // entries diverged the user could never complete the dialog.
                    LastHash = null;

                    MessageBox.Show("Those passwords didn't match. Please re-enter your new password.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        CancellationTokenSource PasswordSelectionCancellationToken;

        private void PasswordSelectionTB_TextChanged(object sender, EventArgs e)
        {
            PasswordSelectionCancellationToken?.Cancel();

            SetPasswordButton.Enabled = false;

            PasswordSelectionCancellationToken = new CancellationTokenSource();
            var Token = PasswordSelectionCancellationToken.Token;

            Task.Run(async () =>
            {
                await Task.Delay(500); // Wait until the user has stopped typing to enable the continue button

                if (Token.IsCancellationRequested)
                    return;

                AccountsView.InvokeIfRequired(() => SetPasswordButton.Enabled = true);
            }, PasswordSelectionCancellationToken.Token);
        }

        private void PasswordPanel_VisibleChanged(object sender, EventArgs e)
        {
            foreach (Control Control in Controls)
                if (Control != PasswordPanel)
                    Control.Enabled = !PasswordPanel.Visible;
        }

        public static bool GetUserID(string Username, out long UserId, out RestResponse response)
        {
            RestRequest request = LastValidAccount?.MakeRequest("v1/usernames/users", Method.Post) ?? new RestRequest("v1/usernames/users", Method.Post);
            request.AddJsonBody(new { usernames = new string[] { Username } });

            response = UsersClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.OK && response.Content.TryParseJson(out JObject UserData) && UserData.ContainsKey("data") && UserData["data"].Count() >= 1)
            {
                UserId = UserData["data"]?[0]?["id"].Value<long>() ?? -1;

                return true;
            }

            UserId = -1;

            return false;
        }

        public void UpdateAccountView(Account account) =>
            AccountsView.InvokeIfRequired(() => AccountsView.UpdateObject(account));

        public static Account AddAccount(string SecurityToken, string Password = "", string AccountJSON = null, bool Save = true)
        {
            // Strip farmer-panel junk ("Username: X | Playtime: ... | Cookie: ...") down to the real .ROBLOSECURITY cookie.
            if (!string.IsNullOrWhiteSpace(SecurityToken))
            {
                List<string> Extracted = Utilities.ExtractCookies(SecurityToken);

                if (Extracted.Count > 0) SecurityToken = Extracted[0];
            }

            Account account = new Account(SecurityToken, AccountJSON);

            if (account.Valid)
            {
                account.Password = Password;

                Account exists;

                lock (AccountsLock)
                    exists = AccountsList.FirstOrDefault(acc => acc.UserID == account.UserID);

                if (exists != null)
                {
                    account = exists;

                    exists.SecurityToken = SecurityToken;
                    exists.Password = Password;
                    exists.LastUse = DateTime.Now;
                }
                else
                    lock (AccountsLock)
                        AccountsList.Add(account);

                if (Save) // batch callers defer the (expensive) refresh + save and do it once at the end
                {
                    Instance.RefreshView(account);
                    SaveAccounts(true);
                }

                return account;
            }

            return null;
        }

        /// <summary>
        /// Imports any .ROBLOSECURITY cookies found in <paramref name="Text"/> off the UI thread, with a single
        /// refresh + save at the end. Accepts raw tokens, messy farmer-panel lines, or whole pasted blobs.
        /// </summary>
        public Task<(int Added, int Failed)> ImportCookiesFromText(string Text) => ImportCookiesAsync(Utilities.ExtractCookies(Text));

        /// <summary>
        /// Reads each dropped path, extracts every cookie inside, and imports them in one batch.
        /// Dropping a folder scans all .txt files inside it recursively (like a "sort from dir" pass).
        /// </summary>
        public Task<(int Added, int Failed)> ImportCookiesFromFiles(string[] Files)
        {
            List<string> Cookies = new List<string>();

            void ReadFile(string Path)
            {
                try { Cookies.AddRange(Utilities.ExtractCookies(File.ReadAllText(Path))); }
                catch (Exception x) { Program.Logger.Error($"Failed to read '{Path}': {x.Message}"); }
            }

            foreach (string DroppedPath in Files ?? Array.Empty<string>())
                try
                {
                    if (Directory.Exists(DroppedPath)) // a folder was dropped -> scan every .txt inside it
                        foreach (string Txt in Directory.EnumerateFiles(DroppedPath, "*.txt", SearchOption.AllDirectories))
                            ReadFile(Txt);
                    else if (File.Exists(DroppedPath))
                        ReadFile(DroppedPath);
                }
                catch (Exception x) { Program.Logger.Error($"Failed to process '{DroppedPath}': {x.Message}"); }

            return ImportCookiesAsync(Cookies.Distinct().ToList());
        }

        private async Task<(int Added, int Failed)> ImportCookiesAsync(List<string> Cookies)
        {
            if (Cookies == null || Cookies.Count == 0)
            {
                this.InvokeIfRequired(() => MessageBox.Show("No valid Roblox cookies were found in the data.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Information));
                return (0, 0);
            }

            int DelayMs = 1000;
            if (General.Exists("ImportDelay") && !int.TryParse(General.Get("ImportDelay"), out DelayMs)) DelayMs = 1000;
            if (DelayMs < 0) DelayMs = 0;

            int Added = 0;
            List<string> Failed = new List<string>();

            await Task.Run(async () =>
            {
                // Sequential with a delay between each validation so big batches don't trip Roblox's rate limit.
                for (int i = 0; i < Cookies.Count; i++)
                {
                    try { if (AddAccount(Cookies[i], Save: false) != null) Added++; else Failed.Add(Cookies[i]); }
                    catch (Exception x) { Failed.Add(Cookies[i]); Program.Logger.Error($"Failed to import a cookie: {x.Message}"); }

                    if (DelayMs > 0 && i < Cookies.Count - 1) await Task.Delay(DelayMs);
                }

                // Retry pass(es): a failed cookie may have only been rate-limited (429), not dead. Wait out the
                // limit window and retry; stop as soon as a round recovers nothing (those that remain are dead).
                int Round = 0;

                while (Failed.Count > 0 && Round < 2)
                {
                    Round++;

                    await Task.Delay(Math.Max(DelayMs * 3, 3000));

                    List<string> StillFailed = new List<string>();

                    foreach (string Cookie in Failed)
                    {
                        try { if (AddAccount(Cookie, Save: false) != null) Added++; else StillFailed.Add(Cookie); }
                        catch { StillFailed.Add(Cookie); }

                        if (DelayMs > 0) await Task.Delay(DelayMs);
                    }

                    bool NoProgress = StillFailed.Count == Failed.Count;
                    Failed = StillFailed;

                    if (NoProgress) break;
                }
            });

            RefreshView();
            SaveAccounts(true);
            FlushPendingSave();

            Program.Logger.Info($"Cookie import finished: {Added} added/updated, {Failed.Count} failed");

            return (Added, Failed.Count);
        }

        public static string ShowDialog(string text, string caption, string defaultText = "", bool big = false) // tbh pasted from stackoverflow
        {
            Form prompt = new Form()
            {
                Width = 340,
                Height = big ? 420 : 125,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                Text = caption,
                StartPosition = FormStartPosition.CenterScreen
            };

            Label textLabel = new Label() { Left = 15, Top = 10, Text = text, AutoSize = true };
            Control textBox;
            Button confirmation = new Button() { Text = "OK", Left = 15, Width = 100, Top = big ? 350 : 50, DialogResult = DialogResult.OK };

            if (big) textBox = new RichTextBox() { Left = 15, Top = 15 + textLabel.Size.Height, Width = 295, Height = 330 - textLabel.Size.Height, Text = defaultText };
            else textBox = new TextBox() { Left = 15, Top = 25, Width = 295, Text = defaultText };

            confirmation.Click += (sender, e) => { prompt.Close(); };
            prompt.Controls.Add(textBox);
            prompt.Controls.Add(confirmation);
            prompt.Controls.Add(textLabel);
            if (!big) prompt.AcceptButton = confirmation;

            prompt.Rescale();

            return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "/UC";
        }

        private void AccountManager_Load(object sender, EventArgs e)
        {
            PasswordPanel.Dock = DockStyle.Fill;

            // Leftovers from the auto-updater this fork no longer has; delete them if an old install left one.
            foreach (string Stale in new[] { "Auto Update.exe", "AU.exe" })
                try { string Path_ = Path.Combine(Directory.GetCurrentDirectory(), Stale); if (File.Exists(Path_)) File.Delete(Path_); } catch { }

            DirectoryInfo UpdateDir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "Update"));

            if (UpdateDir.Exists)
                UpdateDir.RecursiveDelete();

            afform = new ArgumentsForm();
            ServerListForm = new ServerList();
            UtilsForm = new AccountUtils();
            ImportAccountsForm = new ImportForm();
            FieldsForm = new AccountFields();
            ThemeForm = new ThemeEditor();
            RGForm = new RecentGamesForm();

            MainClient = new RestClient(new RestClientOptions("https://www.roblox.com/") { MaxTimeout = 15000 });
            AvatarClient = new RestClient(new RestClientOptions("https://avatar.roblox.com/") { MaxTimeout = 15000 });
            AuthClient = new RestClient(new RestClientOptions("https://auth.roblox.com/") { MaxTimeout = 15000 });
            EconClient = new RestClient(new RestClientOptions("https://economy.roblox.com/") { MaxTimeout = 15000 });
            AccountClient = new RestClient(new RestClientOptions("https://accountsettings.roblox.com/") { MaxTimeout = 15000 });
            GameJoinClient = new RestClient(new RestClientOptions("https://gamejoin.roblox.com/") { UserAgent = "Roblox/WinInet", MaxTimeout = 15000 });
            UsersClient = new RestClient(new RestClientOptions("https://users.roblox.com") { MaxTimeout = 15000 });
            FriendsClient = new RestClient(new RestClientOptions("https://friends.roblox.com") { MaxTimeout = 15000 });
            Web13Client = new RestClient(new RestClientOptions("https://web.roblox.com/") { MaxTimeout = 15000 });
            CatalogClient = new RestClient(new RestClientOptions("https://catalog.roblox.com/") { MaxTimeout = 15000 });
            ApisClient = new RestClient(new RestClientOptions("https://apis.roblox.com/") { MaxTimeout = 15000 });
            InventoryClient = new RestClient(new RestClientOptions("https://inventory.roblox.com/") { MaxTimeout = 15000 });
            GamesClient = new RestClient(new RestClientOptions("https://games.roblox.com/") { MaxTimeout = 15000 });

            if (File.Exists(SaveFilePath))
                LoadAccounts();
            else
                ResetEncryption();

            ApplyTheme();

            RGForm.RecentGameSelected += (sender, e) => { PlaceID.Text = e.Game.Details?.placeId.ToString(); };

            PlaceID.Text = General.Exists("SavedPlaceId") ? General.Get("SavedPlaceId") : "5315046213";
            UserID.Text = General.Exists("SavedFollowUser") ? General.Get("SavedFollowUser") : string.Empty;

            // Added at runtime rather than in the designer: the designer file carries hand-maintained resource
            // names, and this needs no designer state beyond one menu entry.
            AccountsStrip.Items.Add(new ToolStripMenuItem("Assign Proxies", null, (s, e) => ShowProxyAssignment()));

            if (!Developer.Get<bool>("DevMode"))
            {
                AccountsStrip.Items.Remove(viewFieldsToolStripMenuItem);
                AccountsStrip.Items.Remove(getAuthenticationTicketToolStripMenuItem);
                AccountsStrip.Items.Remove(copyRbxplayerLinkToolStripMenuItem);
                AccountsStrip.Items.Remove(copySecurityTokenToolStripMenuItem);
                AccountsStrip.Items.Remove(copyAppLinkToolStripMenuItem);
            }
            else
                ArgumentsB.Visible = true;

            if (General.Get<bool>("HideUsernames"))
                HideUsernamesCheckbox.Checked = true;

            if (!General.Get<bool>("DisableAgingAlert"))
                Username.Renderer = new AccountRenderer();

            try
            {
                if (Developer.Get<bool>("EnableWebServer"))
                {
                    string Port = WebServer.Exists("WebServerPort") ? WebServer.Get("WebServerPort") : "7963";

                    List<string> Prefixes = new List<string>() { $"http://localhost:{Port}/" };

                    if (WebServer.Get<bool>("AllowExternalConnections"))
                        if (Program.Elevated)
                            Prefixes.Add($"http://*:{Port}/");
                        else
                            using (Process proc = new Process() { StartInfo = new ProcessStartInfo(AppDomain.CurrentDomain.FriendlyName, "-adminRequested") { Verb = "runas" } })
                                try
                                {
                                    proc.Start();
                                    Environment.Exit(1);
                                }
                                catch { }


                    AltManagerWS = new WebServer(SendResponse, Prefixes.ToArray());
                    AltManagerWS.Run();
                }
            }
            catch (Exception x) { MessageBox.Show($"Failed to start webserver!\n\n{x}", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Error); }

            Task.Run(() =>
            {
                WebClient WC = new WebClient();
                string VersionJSON = WC.DownloadString("https://clientsettings.roblox.com/v1/client-version/WindowsPlayer");

                if (JObject.Parse(VersionJSON).TryGetValue("clientVersionUpload", out JToken token))
                    CurrentVersion = token.Value<string>();
            });

            IniSettings.Save("RAMSettings.ini");

            PlaceID.AutoCompleteCustomSource = new AutoCompleteStringCollection();
            PlaceID.AutoCompleteMode = AutoCompleteMode.Suggest;
            PlaceID.AutoCompleteSource = AutoCompleteSource.CustomSource;

            Task.Run(LoadRecentGames);
            Task.Run(RobloxProcess.UpdateMatches);

            if (General.Get<bool>("ShuffleJobId"))
                ShuffleIcon_Click(null, EventArgs.Empty);

            if (General.Get<bool>("AutoCookieRefresh"))
            {
                // AutoReset=false + re-arm in finally guarantees passes never overlap (a full pass over thousands
                // of accounts can take far longer than the interval). Enumerate a snapshot to avoid "Collection was modified".
                AutoCookieRefresh = new System.Timers.Timer(60000 * 5) { AutoReset = false, Enabled = true };
                AutoCookieRefresh.Elapsed += async (s, e) =>
                {
                    try
                    {
                        foreach (var Account in GetAccountsSnapshot())
                        {
                            if (Account.GetField("NoCookieRefresh") != "true" && (DateTime.Now - Account.LastUse).TotalDays > 20 && (DateTime.Now - Account.LastAttemptedRefresh).TotalDays >= 7)
                            {
                                Program.Logger.Info($"Attempting to refresh {Account.Username} | Last Use: {Account.LastUse}");

                                Account.LastAttemptedRefresh = DateTime.Now;

                                Account.LogOutOfOtherSessions(true);

                                await Task.Delay(5000);
                            }
                        }
                    }
                    catch (Exception x) { Program.Logger.Error($"AutoCookieRefresh error: {x.Message}"); }
                    finally { try { AutoCookieRefresh.Start(); } catch { } }
                };
            }

            // Keeps dynamic priority tracking the focused window (and refreshes CPU/RAM samples). ~3.5s because
            // CPU percentage is derived from the delta between two samples and needs seconds to be meaningful.
            // AutoReset=false + re-arm so a slow pass over many clients can never overlap itself.
            var ResourceTimer = new System.Timers.Timer(3500) { AutoReset = false, Enabled = ResourceManager.Enabled };
            ResourceTimer.Elapsed += (s, e) =>
            {
                try { ResourceManager.Poll(); }
                catch (Exception x) { Program.Logger.Error($"ResourceManager poll error: {x.Message}"); }
                finally { try { if (ResourceManager.Enabled) ResourceTimer.Start(); } catch { } }
            };

            // The rate was hard-coded to two minutes while PresenceUpdateRate sat in the settings doing nothing.
            var PresenceTimer = new System.Timers.Timer(Math.Max(1, PresenceMinutes) * 60000) { Enabled = true };
            PresenceTimer.Elapsed += (s, e) => AccountsView.InvokeIfRequired(async () => await UpdatePresence());

            Warmer.StartScheduler(); // no-op unless WarmerEnabled
        }

        public void ApplyTheme()
        {
            BackColor = ThemeEditor.FormsBackground;
            ForeColor = ThemeEditor.FormsForeground;

            if (AccountsView.BackColor != ThemeEditor.AccountBackground || AccountsView.ForeColor != ThemeEditor.AccountForeground)
            {
                AccountsView.BackColor = ThemeEditor.AccountBackground;
                AccountsView.ForeColor = ThemeEditor.AccountForeground;

                RefreshView();
            }

            AccountsView.HeaderStyle = ThemeEditor.ShowHeaders ? (AccountsView.ShowGroups ? ColumnHeaderStyle.Nonclickable : ColumnHeaderStyle.Clickable) : ColumnHeaderStyle.None;
            AccountsView.CellEditActivation = ObjectListView.CellEditActivateMode.DoubleClick;

            Controls.ApplyTheme();

            afform.ApplyTheme();
            ServerListForm.ApplyTheme();
            UtilsForm.ApplyTheme();
            ImportAccountsForm.ApplyTheme();
            FieldsForm.ApplyTheme();
            ThemeForm.ApplyTheme();
            RGForm.ApplyTheme();

            ControlForm?.ApplyTheme();
            SettingsForm?.ApplyTheme();
        }

        private async void LoadRecentGames()
        {
            RecentGames = new List<Game>();

            if (!File.Exists(RecentGamesFilePath)) return;

            List<Game> Games;

            // A non-atomic writer (see AddRecentGame) plus an unguarded deserialize on this async void used
            // to turn a truncated/hand-edited RecentGames.json into a permanent crash-on-startup loop.
            try { Games = JsonConvert.DeserializeObject<List<Game>>(File.ReadAllText(RecentGamesFilePath)); }
            catch (Exception x)
            {
                Program.Logger.Error($"Failed to load RecentGames.json, starting fresh: {x.Message}");

                try { File.Copy(RecentGamesFilePath, $"{RecentGamesFilePath}.bak", true); File.Delete(RecentGamesFilePath); } catch { }

                return;
            }

            if (Games == null) return;

            RGForm.InvokeIfRequired(() => RGForm.LoadGames(Games));

            foreach (Game RG in Games)
                await AddRecentGame(RG, true);
        }

        private async Task AddRecentGame(Game RG, bool Loading = false)
        {
            await RG.WaitForDetails();

            RecentGames.RemoveAll(g => g?.Details?.placeId == RG.Details?.placeId);

            while (RecentGames.Count > General.Get<int>("MaxRecentGames"))
            {
                this.InvokeIfRequired(() => PlaceID.AutoCompleteCustomSource.Remove(RecentGames[0].Details?.filteredName));
                RecentGames.RemoveAt(0);
            }

            RecentGames.Add(RG);

            this.InvokeIfRequired(() => PlaceID.AutoCompleteCustomSource.Add(RG.Details.filteredName));

            if (!Loading)
            {
                this.InvokeIfRequired(() => RecentGameAdded?.Invoke(this, new GameArgs(RG)));

                lock (rgSaveLock)
                {
                    // Atomic write: temp then swap, so a crash mid-write can't corrupt RecentGames.json.
                    string TempFile = RecentGamesFilePath + ".tmp";

                    File.WriteAllText(TempFile, JsonConvert.SerializeObject(RecentGames));

                    if (File.Exists(RecentGamesFilePath))
                        File.Replace(TempFile, RecentGamesFilePath, null);
                    else
                        File.Move(TempFile, RecentGamesFilePath);
                }
            }
        }

        private readonly List<ServerData> AttemptedJoins = new List<ServerData>();
        private readonly object AttemptedJoinsLock = new object(); // SetRecommendedServer runs on concurrent HttpListener threads

        private string WebServerResponse(object Message, bool Success) => JsonConvert.SerializeObject(new { Success, Message });

        /// <summary>
        /// Deny-by-default authorization for the web API. Reads the password live (settings can change at
        /// runtime), demands a configured password of at least 6 characters, and compares in constant time.
        /// A missing/short configured password or a missing/wrong supplied one is never authorized.
        /// </summary>
        private static bool PasswordOK(string Provided)
        {
            string Current = WebServer.Get("Password") ?? "";

            if (Current.Length < 6) return false;

            return ConstantTimeEquals(Provided ?? "", Current);
        }

        /// <summary>Length-and-content comparison that does not short-circuit, so the password can't be timed out byte by byte.</summary>
        private static bool ConstantTimeEquals(string a, string b)
        {
            byte[] x = Encoding.UTF8.GetBytes(a);
            byte[] y = Encoding.UTF8.GetBytes(b);

            int Diff = x.Length ^ y.Length;

            for (int i = 0; i < y.Length; i++)
                Diff |= (i < x.Length ? x[i] : (byte)0) ^ y[i];

            return Diff == 0;
        }

        private string SendResponse(HttpListenerContext Context)
        {
            HttpListenerRequest request = Context.Request;

            bool V2 = request.Url.AbsolutePath.StartsWith("/v2/");
            string AbsolutePath = V2 ? request.Url.AbsolutePath.Substring(3) : request.Url.AbsolutePath;

            string Reply(string Response, bool Success = false, int Code = -1, string Raw = null)
            {
                Context.Response.StatusCode = Code > 0 ? Code : (Success ? 200 : 400);

                return V2 ? WebServerResponse(Response, Success) : (Raw ?? Response);
            }

            if (!request.IsLocal && !WebServer.Get<bool>("AllowExternalConnections")) return Reply("External connections are not allowed", false, 401, string.Empty);

            // A cross-site page in a browser can reach the loopback listener. Browser-driven requests (CSRF)
            // always carry an Origin header; legitimate script/CLI callers never do.
            if (!string.IsNullOrEmpty(request.Headers["Origin"])) return Reply("Cross-origin requests are not allowed", false, 403, string.Empty);

            // Defeats DNS rebinding: an attacker-controlled hostname resolving to 127.0.0.1 still sends its own
            // Host header, so reject anything that isn't addressed to loopback when we're loopback-only.
            if (!WebServer.Get<bool>("AllowExternalConnections"))
            {
                string Host = request.Headers["Host"] ?? "";

                if (Host.Length > 0 && !Host.StartsWith("localhost") && !Host.StartsWith("127.0.0.1") && !Host.StartsWith("[::1]"))
                    return Reply("Invalid Host", false, 403, string.Empty);
            }

            if (AbsolutePath == "/favicon.ico") return ""; // always return nothing

            if (AbsolutePath == "/Running") return Reply("SkrilyaAccountManager is running", true, Raw: "true");

            string Body = new StreamReader(request.InputStream).ReadToEnd();
            string Method = AbsolutePath.Substring(1);
            string Account = request.QueryString["Account"];
            string Password = request.QueryString["Password"];

            // Anything arriving from off-box must ALWAYS present the password, whatever endpoint it hits.
            // Previously a whole class of state-changing endpoints (SetServer, Block/Unblock, GetCSRFToken, ...)
            // had no password gate at all once external connections were enabled.
            if (!request.IsLocal && !PasswordOK(Password)) return Reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");

            if (WebServer.Get<bool>("EveryRequestRequiresPassword") && !PasswordOK(Password)) return Reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");

            // Sensitive / state-changing methods need the password even over loopback. The Origin check above
            // only stops CSRF that carries an Origin header; a no-cors GET from <img>/<script> sends none, so
            // without this a malicious page open on this machine could drive these endpoints directly.
            //
            // The old condition was `Password != null && Password != WSPassword`, which did not fire at all when
            // the parameter was simply omitted — i.e. the gate was bypassed by not sending a password.
            if ((Method == "GetCookie" || Method == "GetAccounts" || Method == "GetAccountsJson" || Method == "LaunchAccount" || Method == "FollowUser"
                || Method == "BlockUser" || Method == "UnblockUser" || Method == "UnblockEveryone" || Method == "GetBlockedList"
                || Method == "SetServer" || Method == "SetRecommendedServer")
                && !PasswordOK(Password)) return Reply("Invalid Password, make sure your password contains 6 or more characters", false, 401, "Invalid Password");

            if (Method == "GetAccounts")
            {
                if (!WebServer.Get<bool>("AllowGetAccounts")) return Reply("Method `GetAccounts` not allowed", false, 401, "Method not allowed");

                string GroupFilter = request.QueryString["Group"];

                // One pass and a single join instead of O(n^2) string concatenation.
                string Names = string.Join(",", GetAccountsSnapshot().Where(acc => string.IsNullOrEmpty(GroupFilter) || acc.Group == GroupFilter).Select(acc => acc.Username));

                return Reply(Names, true, Raw: Names);
            }

            if (Method == "GetAccountsJson")
            {
                if (!WebServer.Get<bool>("AllowGetAccounts")) return Reply("Method `GetAccountsJson` not allowed", false, 401, "Method not allowed");

                string GroupFilter = request.QueryString["Group"];
                // The old condition was `Password != WSPassword` — inverted, so cookies were handed out precisely
                // when the password did NOT match (including when none was sent). Combined with GetAccountsJson
                // having no password gate, that dumped every .ROBLOSECURITY over a single unauthenticated GET.
                //
                // Now: correct password required, AND never over a non-loopback connection — the transport is
                // cleartext HTTP, so a takeover cookie must not cross the wire even with the flag enabled.
                bool ShowCookies = request.IsLocal && PasswordOK(Password) && request.QueryString["IncludeCookies"] == "true" && WebServer.Get<bool>("AllowGetCookie");

                List<object> Objects = new List<object>();

                foreach (Account acc in GetAccountsSnapshot())
                {
                    if (!string.IsNullOrEmpty(GroupFilter) && acc.Group != GroupFilter) continue;

                    object AccountObject = new
                    {
                        acc.Username,
                        acc.UserID,
                        acc.Alias,
                        acc.Description,
                        acc.Group,
                        acc.CSRFToken,
                        LastUsed = acc.LastUse.ToRobloxTick(),
                        Cookie = ShowCookies ? acc.SecurityToken : null,
                        acc.Fields,
                    };

                    Objects.Add(AccountObject);
                }

                return Reply(JsonConvert.SerializeObject(Objects), true);
            }

            if (Method == "ImportCookie")
            {
                Account New = AddAccount(request.QueryString["Cookie"]);

                bool Success = New != null;

                return Reply(Success ? "Cookie successfully imported" : "[ImportCookie] An error was encountered importing the cookie", Success, Raw: Success ? "true" : "false");
            }

            if (string.IsNullOrEmpty(Account)) return Reply("Empty Account", false);

            Account account = GetAccountsSnapshot().FirstOrDefault(x => x.Username == Account || x.UserID.ToString() == Account);

            if (account == null || !account.GetCSRFToken(out string Token)) return Reply("Invalid Account, the account's cookie may have expired and resulted in the account being logged out", false, Raw: "Invalid Account");

            if (Method == "GetCookie")
            {
                if (!WebServer.Get<bool>("AllowGetCookie")) return Reply("Method `GetCookie` not allowed", false, 401, "Method not allowed");

                return Reply(account.SecurityToken, true);
            }

            if (Method == "LaunchAccount")
            {
                if (!WebServer.Get<bool>("AllowLaunchAccount")) return Reply("Method `LaunchAccount` not allowed", false, 401, "Method not allowed");

                bool ValidPlaceId = long.TryParse(request.QueryString["PlaceId"], out long PlaceId); if (!ValidPlaceId) return Reply("Invalid PlaceId provided", false, Raw: "Invalid PlaceId");

                string JobID = !string.IsNullOrEmpty(request.QueryString["JobId"]) ? request.QueryString["JobId"] : "";
                string FollowUser = request.QueryString["FollowUser"];
                string JoinVIP = request.QueryString["JoinVIP"];

                account.JoinServer(PlaceId, JobID, FollowUser == "true", JoinVIP == "true");

                return Reply($"Launched {Account} to {PlaceId}", true);
            }

            if (Method == "FollowUser") // https://github.com/ic3w0lf22/Roblox-Account-Manager/pull/52
            {
                if (!WebServer.Get<bool>("AllowLaunchAccount")) return Reply("Method `FollowUser` not allowed", false, 401, "Method not allowed");

                string User = request.QueryString["Username"]; if (string.IsNullOrEmpty(User)) return Reply("Invalid Username Parameter", false);

                if (!GetUserID(User, out long UserId, out var Response))
                    return Reply($"[{Response.StatusCode} {Response.StatusDescription}] Failed to get UserId: {Response.Content}", false);

                account.JoinServer(UserId, "", true);

                return Reply($"Joining {User}'s game on {Account}", true);
            }

            if (Method == "GetCSRFToken") return Reply(Token, true);
            if (Method == "GetAlias") return Reply(account.Alias, true);
            if (Method == "GetDescription") return Reply(account.Description, true);

            if (Method == "BlockUser" && !string.IsNullOrEmpty(request.QueryString["UserId"]))
                try
                {
                    var Res = account.BlockUserId(request.QueryString["UserId"], Context: Context);

                    return Reply(Res.Content, Res.IsSuccessful, (int)Res.StatusCode);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "UnblockUser" && !string.IsNullOrEmpty(request.QueryString["UserId"]))
                try
                {
                    var Res = account.UnblockUserId(request.QueryString["UserId"], Context: Context);

                    return Reply(Res.Content, Res.IsSuccessful, (int)Res.StatusCode);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "GetBlockedList") try
                {
                    var Res = account.GetBlockedList(Context);

                    return Reply(Res.Content, Res.IsSuccessful, (int)Res.StatusCode);
                }
                catch (Exception x) { return Reply(x.Message, false, 500); }
            if (Method == "UnblockEveryone" && account.UnblockEveryone(out string UbRes) is bool UbSuccess) return Reply(UbRes, UbSuccess);

            if (Method == "SetServer" && !string.IsNullOrEmpty(request.QueryString["PlaceId"]) && !string.IsNullOrEmpty(request.QueryString["JobId"]))
            {
                string RSP = account.SetServer(Convert.ToInt64(request.QueryString["PlaceId"]), request.QueryString["JobId"], out bool Success);

                return Reply(RSP, Success);
            }

            if (Method == "SetRecommendedServer")
            {
                int attempts = 0;
                string res = "-1";

                // Snapshot the live server list (it is refreshed on another thread; index-walking it directly
                // races into ArgumentOutOfRangeException).
                List<ServerData> Servers = RBX_Alt_Manager.ServerList.servers.ToList();

                for (int i = Servers.Count - 1; i > 0; i--)
                {
                    if (attempts > 10)
                        return Reply("Too many failed attempts", false);

                    ServerData server = Servers[i];

                    // AttemptedJoins is shared across concurrent request threads; guard every access.
                    lock (AttemptedJoinsLock)
                    {
                        if (AttemptedJoins.FirstOrDefault(x => x.id == server.id) != null) continue;
                        if (AttemptedJoins.Count > 100) AttemptedJoins.Clear();

                        AttemptedJoins.Add(server);
                    }

                    attempts++;

                    res = account.SetServer(!string.IsNullOrEmpty(request.QueryString["PlaceId"]) ? Convert.ToInt64(request.QueryString["PlaceId"]) : RBX_Alt_Manager.ServerList.CurrentPlaceID, server.id, out bool iSuccess);

                    if (iSuccess)
                        return Reply(res, iSuccess);
                }

                bool Success = !string.IsNullOrEmpty(res);

                return Reply(Success ? "Failed" : res, Success);
            }

            if (Method == "GetField" && !string.IsNullOrEmpty(request.QueryString["Field"])) return Reply(account.GetField(request.QueryString["Field"]), true);

            if (Method == "SetField" && !string.IsNullOrEmpty(request.QueryString["Field"]) && !string.IsNullOrEmpty(request.QueryString["Value"]))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetField` not allowed", false, 401, "Method not allowed");

                account.SetField(request.QueryString["Field"], request.QueryString["Value"]);

                return Reply($"Set Field {request.QueryString["Field"]} to {request.QueryString["Value"]} for {account.Username}", true);
            }
            if (Method == "RemoveField" && !string.IsNullOrEmpty(request.QueryString["Field"]))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `RemoveField` not allowed", false, 401, "Method not allowed");

                account.RemoveField(request.QueryString["Field"]);

                return Reply($"Removed Field {request.QueryString["Field"]} from {account.Username}", true);
            }

            if (Method == "SetAvatar" && Body.TryParseJson(out object _))
            {
                account.SetAvatar(Body);

                return Reply($"Attempting to set avatar of {account.Username} to {Body}", true);
            }

            if (Method == "SetAlias" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return Reply("Method `SetAlias` not allowed", false, Raw: "Method not allowed");

                account.Alias = Body;
                UpdateAccountView(account);

                return Reply($"Set Alias of {account.Username} to {Body}", true);
            }
            if (Method == "SetDescription" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) Reply("Method `SetDescription` not allowed", false, Raw: "Method not allowed");

                account.Description = Body;
                UpdateAccountView(account);

                return Reply($"Set Description of {account.Username} to {Body}", true);
            }
            if (Method == "AppendDescription" && !string.IsNullOrEmpty(Body))
            {
                if (!WebServer.Get<bool>("AllowAccountEditing")) return V2 ? WebServerResponse("Method `AppendDescription` not allowed", false) : "Method not allowed";

                account.Description += Body;
                UpdateAccountView(account);

                return Reply($"Appended Description of {account.Username} with {Body}", true);
            }

            return Reply("404 not found", false, 404);
        }

        private void AccountManager_Shown(object sender, EventArgs e)
        {
            if (!UpdateMultiRoblox() && !General.Get<bool>("HideRbxAlert"))
                MessageBox.Show("WARNING: Roblox is currently running, multi roblox will not work until you restart the account manager with roblox closed.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            // Chromium (PuppeteerSharp) is fetched on first run. CefSharp used to be the Vista/Win7 fallback;
            // the app targets Windows 10+ now, where Puppeteer always works, so that whole path is gone.
            if (!Directory.Exists(AccountBrowser.Fetcher.DownloadsFolder) || Directory.GetDirectories(AccountBrowser.Fetcher.DownloadsFolder).Length == 0)
            {
                Add.Visible = false;
                Remove.Visible = false;
                DownloadProgressBar.Visible = true;
                DLChromiumLabel.Visible = true;

                Task.Run(async () =>
                {
                    IsDownloadingChromium = true;

                    void DownloadProgressChanged(object s, DownloadProgressChangedEventArgs e) => DownloadProgressBar.InvokeIfRequired(() => { DownloadProgressBar.Value = e.ProgressPercentage; });

                    AccountBrowser.Fetcher.DownloadProgressChanged += DownloadProgressChanged;
                    await AccountBrowser.Fetcher.DownloadAsync(BrowserFetcher.DefaultChromiumRevision);
                    AccountBrowser.Fetcher.DownloadProgressChanged -= DownloadProgressChanged;

                    IsDownloadingChromium = false;

                    this.InvokeIfRequired(() =>
                    {
                        Add.Visible = true;
                        Remove.Visible = true;
                        DownloadProgressBar.Visible = false;
                        DLChromiumLabel.Visible = false;
                    });
                });
            }

            if (AccountControl.Get<bool>("StartOnLaunch"))
                LaunchNexus.PerformClick();

            // The HTML interface opens alongside this window rather than replacing it: while it is being built,
            // every screen that has not moved yet still has to be reachable.
            if (General.Get<bool>("NewUI"))
                try { Forms.WebShell.ShowShell(); }
                catch (Exception x)
                {
                    Program.Logger.Error($"[WebShell] could not open on startup: {x}");

                    // Nothing is asking the security question now; give the old panels back rather than leaving
                    // a window that cannot reach its own accounts.
                    RestoreSecurityPanels();
                }
        }

        /// <summary>The accounts currently selected in the list, falling back to the single selected account.</summary>
        public List<Account> CurrentSelection()
        {
            if (AccountsView.SelectedObjects.Count > 0) return AccountsView.SelectedObjects.Cast<Account>().ToList();

            return SelectedAccount != null ? new List<Account> { SelectedAccount } : new List<Account>();
        }

        /// <summary>Opens the proxy assignment dialog for the current selection. Shared by the context menu and the settings tab.</summary>
        public void ShowProxyAssignment()
        {
            List<Account> Selection = CurrentSelection();

            if (Selection.Count == 0)
            {
                MessageBox.Show("Select one or more accounts in the list first.", "Assign Proxies", MessageBoxButtons.OK, MessageBoxIcon.Information);

                return;
            }

            using (Forms.ProxyAssignForm Form = new Forms.ProxyAssignForm(Selection))
                Form.ShowDialog(this);
        }

        public bool UpdateMultiRoblox()
        {
            bool Enabled = General.Get<bool>("EnableMultiRbx");

            if (Enabled && rbxMultiMutex == null)
                try
                {
                    rbxMultiMutex = new Mutex(true, "ROBLOX_singletonMutex");

                    if (!rbxMultiMutex.WaitOne(TimeSpan.Zero, true))
                        return false;
                }
                catch { return false; }
            else if (!Enabled && rbxMultiMutex != null)
            {
                rbxMultiMutex.Close();
                rbxMultiMutex = null;
            }

            return true;
        }

        private void Remove_Click(object sender, EventArgs e)
        {
            if (AccountsView.SelectedObjects.Count > 1)
            {
                DialogResult result = MessageBox.Show($"Are you sure you want to remove {AccountsView.SelectedObjects.Count} accounts?", "Remove Accounts", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    lock (AccountsLock)
                        foreach (Account acc in AccountsView.SelectedObjects)
                            AccountsList.Remove(acc);

                    RefreshView();

                    SaveAccounts();
                }
            }
            else if (SelectedAccount != null)
            {
                DialogResult result = MessageBox.Show($"Are you sure you want to remove {SelectedAccount.Username}?", "Remove Account", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    lock (AccountsLock)
                        AccountsList.RemoveAll(x => x == SelectedAccount);

                    RefreshView();

                    SaveAccounts();
                }
            }
        }

        private async void Add_Click(object sender, EventArgs e)
        {
            Add.Enabled = false;

            try { await new AccountBrowser().Login(); }
            catch (Exception x)
            {
                Program.Logger.Error($"[Add_Click] An error was encountered attempting to login: {x}");

                if (Utilities.YesNoPrompt($"An error was encountered attempting to login", "You may have a corrupted chromium installation", "Would you like to re-install chromium?", false))
                {
                    MessageBox.Show("SkrilyaAccountManager will now close since it can't delete the folder while it's in use.", "", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    if (Directory.GetFiles(AccountBrowser.Fetcher.DownloadsFolder).Length <= 1 && Directory.GetDirectories(AccountBrowser.Fetcher.DownloadsFolder).Length <= 1)
                        Process.Start("cmd.exe", $"/c rmdir /s /q \"{AccountBrowser.Fetcher.DownloadsFolder}\"");
                    else
                        Process.Start("explorer.exe", "/select, " + AccountBrowser.Fetcher.DownloadsFolder);

                    Environment.Exit(0);
                }
            }

            Add.Enabled = true;
        }

        private void DownloadProgressBar_Click(object sender, EventArgs e)
        {
            static void ShowManualInstallInstructions()
            {
                string Temp = Path.Combine(Path.GetTempPath(), "manual install instructions.html");

                string DownloadLink = (string)typeof(BrowserFetcher).GetMethod("GetDownloadURL", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { AccountBrowser.Fetcher.Product, AccountBrowser.Fetcher.Platform, AccountBrowser.Fetcher.DownloadHost, BrowserFetcher.DefaultChromiumRevision });
                string Directory = Path.Combine(AccountBrowser.Fetcher.DownloadsFolder, $"{AccountBrowser.Fetcher.Platform}-{BrowserFetcher.DefaultChromiumRevision}");

                File.WriteAllText(Temp, string.Format(Resources.ManualInstallHTML, "Chromium", DownloadLink, "chrome-win", Directory));

                Process.Start(new ProcessStartInfo(Temp) { UseShellExecute = true });
                Process.Start(new ProcessStartInfo("cmd") { Arguments = $"/c mkdir \"{Directory}\"", CreateNoWindow = true });
            }

            if (TaskDialog.IsPlatformSupported)
            {
                TaskDialog Dialog = new TaskDialog()
                {
                    Caption = "Add Account",
                    InstructionText = "Chromium is still being downloaded",
                    Text = "If this is not working for you, you can choose to manually install",
                    Icon = TaskDialogStandardIcon.Information
                };

                TaskDialogButton Manual = new TaskDialogButton("Manual", "Download Manually");
                TaskDialogButton Wait = new TaskDialogButton("Wait", "Wait");

                Wait.Click += (s, e) => Dialog.Close();
                Manual.Click += (s, e) =>
                {
                    Dialog.Close();

                    ShowManualInstallInstructions();
                };

                Dialog.Controls.Add(Manual);
                Dialog.Controls.Add(Wait);
                Wait.Default = true;

                Dialog.Show();
            }
            else if (MessageBox.Show("Chromium is still downloading, you may have to wait a while before adding an account.\n\nNot working? You can choose to manually install by pressing \"Yes\"", "SkrilyaAccountManager", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information) == DialogResult.Yes)
                ShowManualInstallInstructions();
        }

        private void DLChromiumLabel_Click(object sender, EventArgs e) => DownloadProgressBar_Click(sender, e);

        private void manualToolStripMenuItem_Click(object sender, EventArgs e) => Add.PerformClick();

        private void addAccountsToolStripMenuItem_Click(object sender, EventArgs e) => Add.PerformClick();

        private void byCookieToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ImportAccountsForm.Show();
            ImportAccountsForm.WindowState = FormWindowState.Normal;
            ImportAccountsForm.BringToFront();
        }

        private async void bulkUserPassToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string Combos = ShowDialog("Separate the accounts with new lines\nMust be in user:pass form", "Import by User:Pass", big: true);

            if (Combos == "/UC") return;

            List<string> ComboList = new List<string>(Combos.Split('\n'));

            var Size = new System.Numerics.Vector2(455, 485);
            AccountBrowser.CreateGrid(Size);

            for (int i = 0; i < ComboList.Count; i++)
            {
                string Combo = ComboList[i];

                if (!Combo.Contains(':')) continue;

                var LoginTask = new AccountBrowser() { Index = i, Size = Size }.Login(Combo.Substring(0, Combo.IndexOf(':')), Combo.Substring(Combo.IndexOf(":") + 1));

                if ((i + 1) % 2 == 0) await LoginTask;
            }
        }

        private void AccountsView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (AccountsView.SelectedItems.Count != 1)
            {
                SelectedAccount = null;
                SelectedAccountItem = null;

                if (AccountsView.SelectedObjects.Count > 1)
                    SelectedAccounts = AccountsView.SelectedObjects.Cast<Account>().ToList();

                return;
            }

            SelectedAccount = AccountsView.SelectedObject as Account;
            SelectedAccountItem = AccountsView.SelectedItem;

            if (SelectedAccount == null) return;

            AccountsView.HideSelection = false;

            Alias.Text = SelectedAccount.Alias;
            DescriptionBox.Text = SelectedAccount.Description;

            if (!string.IsNullOrEmpty(SelectedAccount.GetField("SavedPlaceId"))) PlaceID.Text = SelectedAccount.GetField("SavedPlaceId");
            if (!string.IsNullOrEmpty(SelectedAccount.GetField("SavedJobId"))) JobID.Text = SelectedAccount.GetField("SavedJobId");
        }

        private void SetAlias_Click(object sender, EventArgs e)
        {
            foreach (Account account in AccountsView.SelectedObjects)
                account.Alias = Alias.Text;

            RefreshView();
        }

        private void SetDescription_Click(object sender, EventArgs e)
        {
            foreach (Account account in AccountsView.SelectedObjects)
                account.Description = DescriptionBox.Text;

            RefreshView();
        }

        private void JoinServer_Click(object sender, EventArgs e)
        {
            Match IDMatch = Regex.Match(PlaceID.Text, @"\/games\/(\d+)[\/|\?]?"); // idiotproofing

            if (PlaceID.Text.Contains("privateServerLinkCode") && IDMatch.Success)
                JobID.Text = PlaceID.Text;

            Game G = RecentGames.FirstOrDefault(RG => RG.Details.filteredName == PlaceID.Text);

            if (G != null)
                PlaceID.Text = G.Details.placeId.ToString();

            PlaceID.Text = IDMatch.Success ? IDMatch.Groups[1].Value : Regex.Replace(PlaceID.Text, "[^0-9]", "");

            bool VIPServer = JobID.TextLength > 4 && JobID.Text.Substring(0, 4) == "VIP:";

            if (!long.TryParse(PlaceID.Text, out long PlaceId)) return;

            if (!PlaceTimer.Enabled)
                _ = Task.Run(() => AddRecentGame(new Game(PlaceId)));

            CancelLaunching();

            bool LaunchMultiple = AccountsView.SelectedObjects.Count > 1;

            string jobId = VIPServer ? JobID.Text.Substring(4) : JobID.Text; // capture on the UI thread; reading JobID.Text from the worker thread is a cross-thread access and can tear

            new Thread(async () => // finally fixing an ancient bug in a dumb way, p.s. i do not condone this.
            {
                // Contain exceptions: an escape from this async void on a raw thread would crash the process.
                try
                {
                    if (LaunchMultiple)
                    {
                        LauncherToken = new CancellationTokenSource();

                        await LaunchAccounts(SelectedAccounts, PlaceId, jobId, false, VIPServer);
                    }
                    else if (SelectedAccount != null)
                    {
                        string res = await SelectedAccount.JoinServer(PlaceId, jobId, false, VIPServer);

                        if (!res.Contains("Success"))
                            this.InvokeIfRequired(() => MessageBox.Show(res));
                    }
                }
                catch (Exception x) { Program.Logger.Error($"Launch failed: {x}"); }
            }).Start();
        }

        private async void Follow_Click(object sender, EventArgs e)
        {
            if (!GetUserID(UserID.Text, out long UserId, out var Response))
            {
                MessageBox.Show($"[{Response.StatusCode} {Response.StatusDescription}] Failed to get UserId: {Response.Content}");
                return;
            }
    
            if (!(await Presence.GetPresenceSingular(UserId) is UserPresence Status && Status.userPresenceType == UserPresenceType.InGame && Status.placeId is long FollowPlaceID && FollowPlaceID > 0) &&
                !Utilities.YesNoPrompt("Warning", "The user you are trying to follow is not in game or has their joins off", "Do you want to attempt to join anyways?")) return;

            CancelLaunching();

            if (AccountsView.SelectedObjects.Count > 1)
            {
                LauncherToken = new CancellationTokenSource();

                await LaunchAccounts(SelectedAccounts, UserId, "", true);
            }
            else if (SelectedAccount != null)
            {
                string res = await SelectedAccount.JoinServer(UserId, "", true);

                if (!res.Contains("Success"))
                    MessageBox.Show(res);
            }
        }

        private void ServerList_Click(object sender, EventArgs e)
        {
            if (AccountsList.Count == 0 || LastValidAccount == null)
                MessageBox.Show("Some features may not work unless there is a valid account", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            if (ServerListForm.Visible)
            {
                ServerListForm.WindowState = FormWindowState.Normal;
                ServerListForm.BringToFront();
            }
            else
                ServerListForm.Show();

            ServerListForm.Busy = false; // incase it somehow bugs out

            ServerListForm.StartPosition = FormStartPosition.Manual;
            ServerListForm.Top = Top;
            ServerListForm.Left = Right;
        }

        private void HideUsernamesCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            General.Set("HideUsernames", HideUsernamesCheckbox.Checked ? "true" : "false");

            AccountsView.BeginUpdate();

            Username.Width = HideUsernamesCheckbox.Checked ? 0 : (int)(120 * Program.Scale);

            AccountsView.EndUpdate();
        }

        private void removeAccountToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (AccountsView.SelectedObjects.Count > 1)
            {
                DialogResult result = MessageBox.Show($"Are you sure you want to remove {AccountsView.SelectedObjects.Count} accounts?", "Remove Accounts", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    lock (AccountsLock)
                        foreach (Account acc in AccountsView.SelectedObjects)
                            AccountsList.Remove(acc);

                    RefreshView();
                    SaveAccounts();
                }
            }
            else if (SelectedAccount != null)
            {
                DialogResult result = MessageBox.Show($"Are you sure you want to remove {SelectedAccount.Username}?", "Remove Account", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    lock (AccountsLock)
                        AccountsList.Remove(SelectedAccount);

                    RefreshView();
                    SaveAccounts();
                }
            }
        }

        private void AccountManager_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsDownloadingChromium && !Utilities.YesNoPrompt("SkrilyaAccountManager", "Chromium is still being downloaded, exiting may corrupt your chromium installation and prevent account manager from working", "Exit anyways?", false))
            {
                e.Cancel = true;

                return;
            }

            AltManagerWS?.Stop();

            FlushPendingSave(); // ensure any debounced account changes are written before exit

            if (PlaceID == null || string.IsNullOrEmpty(PlaceID.Text)) return;

            General.Set("SavedPlaceId", PlaceID.Text);
            General.Set("SavedFollowUser", UserID.Text);
            IniSettings.Save("RAMSettings.ini");
        }

        private void BrowserButton_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null)
            {
                MessageBox.Show("No Account Selected!");
                return;
            }

            UtilsForm.Show();
            UtilsForm.WindowState = FormWindowState.Normal;
            UtilsForm.BringToFront();
        }

        private static System.Windows.Forms.Timer ClipboardClearTimer;

        /// <summary>
        /// Copies a secret (auth ticket, cookie, password, or a launch link that embeds a ticket) to the
        /// clipboard and clears it after a delay, so it doesn't sit there — and in clipboard history — for any
        /// process on the machine to read. Only clears if the clipboard still holds exactly what we put there,
        /// so a later unrelated copy is never destroyed.
        /// </summary>
        /// <summary>
        /// Copies plain (non-secret) text to the clipboard.
        ///
        /// Clipboard.SetText throws ArgumentNullException("text") on an empty string, so every "copy the selected
        /// accounts' usernames" handler crashed the app outright when nothing was selected — string.Join over an
        /// empty list is "". It can also throw when another process holds the clipboard open, which is transient
        /// and not worth a crash dialog either. Nothing to copy means nothing happens, and the user is told why.
        /// </summary>
        private static void CopyPlain(string Text, string NothingToCopy = null)
        {
            if (string.IsNullOrEmpty(Text))
            {
                if (!string.IsNullOrEmpty(NothingToCopy)) Classes.Shell.Info(NothingToCopy);

                return;
            }

            try { Clipboard.SetText(Text); }
            catch (Exception x) { Program.Logger.Warn($"[Clipboard] could not copy: {x.Message}"); }
        }

        private static void CopySensitive(string Text)
        {
            if (string.IsNullOrEmpty(Text)) return;

            try { Clipboard.SetText(Text); } catch { return; }

            ClipboardClearTimer?.Stop();
            ClipboardClearTimer?.Dispose();

            ClipboardClearTimer = new System.Windows.Forms.Timer { Interval = 45000 };
            ClipboardClearTimer.Tick += (s, e) =>
            {
                ClipboardClearTimer.Stop();

                try { if (Clipboard.ContainsText() && Clipboard.GetText() == Text) Clipboard.Clear(); } catch { }
            };
            ClipboardClearTimer.Start();
        }

        private async void getAuthenticationTicketToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount != null)
            {
                if (SelectedAccount.GetAuthTicket(out string STicket))
                    CopySensitive(STicket);

                return;
            }

            if (SelectedAccounts.Count < 1) return;

            List<Account> Accounts = new List<Account>(SelectedAccounts); // snapshot for the background loop

            // Each GetAuthTicket makes two blocking HTTP calls (up to ~30s). Running the loop on the UI thread
            // froze the whole app for minutes when many accounts were selected; offload it and marshal the
            // Clipboard write (which must run on the STA UI thread) back via the async continuation.
            List<string> Tickets = await Task.Run(() =>
            {
                List<string> Result = new List<string>();

                foreach (Account acc in Accounts)
                {
                    try { if (acc.GetAuthTicket(out string Ticket)) Result.Add($"{acc.Username}:{Ticket}"); }
                    catch (Exception x) { Program.Logger.Error($"GetAuthTicket failed for {acc.Username}: {x.Message}"); }
                }

                return Result;
            });

            if (Tickets.Count > 0)
                CopySensitive(string.Join("\n", Tickets));
        }

        private void copyRbxplayerLinkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null) return;

            if (SelectedAccount.GetAuthTicket(out string Ticket))
            {
                bool HasJobId = string.IsNullOrEmpty(JobID.Text);
                double LaunchTime = Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds * 1000);

                Random r = new Random();
                CopySensitive(string.Format("<roblox-player://1/1+launchmode:play+gameinfo:{0}+launchtime:{4}+browsertrackerid:{5}+placelauncherurl:https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame{3}&placeId={1}{2}+robloxLocale:en_us+gameLocale:en_us>", Ticket, PlaceID.Text, HasJobId ? "" : ("&gameId=" + JobID.Text), HasJobId ? "" : "Job", LaunchTime, r.Next(100000, 130000).ToString() + r.Next(100000, 900000).ToString()));
            }
        }

        private void ArgumentsB_Click(object sender, EventArgs e)
        {
            if (afform != null)
                if (afform.Visible)
                    afform.HideForm();
                else
                    afform.ShowForm();
        }

        private void copySecurityTokenToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Tokens = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Tokens.Add(account.SecurityToken);

            CopySensitive(string.Join("\n", Tokens));
        }

        private void copyUsernameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Usernames = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Usernames.Add(account.Username);

            CopyPlain(string.Join("\n", Usernames), "Select an account first.");
        }

        private void copyPasswordToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Passwords = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Passwords.Add($"{account.Password}");

            CopySensitive(string.Join("\n", Passwords));
        }

        private void copyUserPassComboToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Combos = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Combos.Add($"{account.Username}:{account.Password}");

            CopySensitive(string.Join("\n", Combos));
        }

        private void copyUserIdToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> UserIds = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                UserIds.Add(account.UserID.ToString());

            CopyPlain(string.Join("\n", UserIds), "Select an account first.");
        }

        private void PlaceID_TextChanged(object sender, EventArgs e)
        {
            if (PlaceTimer.Enabled) PlaceTimer.Stop();

            PlaceTimer.Start();
        }

        private async void PlaceTimer_Tick(object sender, EventArgs e)
        {
            if (EconClient == null) return;

            PlaceTimer.Stop();

            RestRequest request = new RestRequest($"v2/assets/{PlaceID.Text}/details", Method.Get);
            request.AddHeader("Accept", "application/json");
            RestResponse response = await EconClient.ExecuteAsync(request);

            if (response.IsSuccessful && response.StatusCode == HttpStatusCode.OK && response.Content.StartsWith("{") && response.Content.EndsWith("}"))
            {
                // A brace-wrapped-but-invalid body (rate-limit interstitial, partial response) would throw
                // JsonReaderException out of this async void and crash the app; contain it.
                try
                {
                    ProductInfo placeInfo = JsonConvert.DeserializeObject<ProductInfo>(response.Content);

                    if (placeInfo != null)
                        Utilities.InvokeIfRequired(this, () => CurrentPlace.Text = placeInfo.Name);
                }
                catch (Exception x) { Program.Logger.Error($"PlaceTimer_Tick failed to parse place details: {x.Message}"); }
            }
        }

        private void moveToToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (AccountsView.SelectedObjects.Count == 0) return;

            string GroupName = ShowDialog("Group Name", "Move Account to Group", SelectedAccount != null ? SelectedAccount.Group : string.Empty);

            if (GroupName == "/UC") return; // User Cancelled
            if (string.IsNullOrEmpty(GroupName)) GroupName = "Default";

            foreach (Account acc in AccountsView.SelectedObjects)
                acc.Group = GroupName;

            RefreshView();
            SaveAccounts();
        }

        private void copyGroupToolStripMenuItem_Click(object sender, EventArgs e) => CopyPlain(SelectedAccount?.Group, "Select an account first.");

        private void copyAppLinkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null) return;

            if (SelectedAccount.GetAuthTicket(out string Ticket))
            {
                double LaunchTime = Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds * 1000);

                Random r = new Random();
                CopySensitive(string.Format("<roblox-player://1/1+launchmode:app+gameinfo:{0}+launchtime:{1}+browsertrackerid:{2}+robloxLocale:en_us+gameLocale:en_us>", Ticket, LaunchTime, r.Next(500000, 600000).ToString() + r.Next(10000, 90000).ToString()));
            }
        }

        private void JoinDiscord_Click(object sender, EventArgs e) => Process.Start("https://discord.gg/MsEH7smXY8");

        // Guard against accidentally opening a huge number of browser windows at once (each Chromium is
        // ~150-250MB + hundreds of GDI handles). AccountBrowser also throttles the launch rate.
        private bool ConfirmBulkBrowser(int count)
        {
            if (count <= 10) return true;

            return MessageBox.Show($"You are about to open {count} browser windows. This can use a very large amount of memory and may freeze your PC.\n\nContinue?", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        private void OpenBrowser_Click(object sender, EventArgs e)
        {
            if (!ConfirmBulkBrowser(AccountsView.SelectedObjects.Count)) return;

            foreach (Account account in AccountsView.SelectedObjects)
                new AccountBrowser(account);
        }

        private void customURLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (Uri.TryCreate(ShowDialog("URL", "Open Browser"), UriKind.Absolute, out Uri Link))
            {
                if (!ConfirmBulkBrowser(AccountsView.SelectedObjects.Count)) return;

                foreach (Account account in AccountsView.SelectedObjects)
                    new AccountBrowser(account, Link.ToString(), string.Empty);
            }
        }

        private void URLJSToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!Utilities.YesNoPrompt("Warning", "Your accounts may be at risk using this feature", "Do not paste in javascript unless you know what it does, your account's cookies can easily be logged through javascript.\n\nPress Yes to continue", true)) return;

            if (Uri.TryCreate(ShowDialog("URL", "Open Browser"), UriKind.Absolute, out Uri Link))
            {
                string Script = ShowDialog("Javascript", "Open Browser", big: true);

                if (!ConfirmBulkBrowser(AccountsView.SelectedObjects.Count)) return;

                foreach (Account account in AccountsView.SelectedObjects)
                    new AccountBrowser(account, Link.ToString(), Script);
            }
        }

        private void joinGroupToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (Uri.TryCreate(ShowDialog("Group Link", "Open Browser"), UriKind.Absolute, out Uri Link))
            {
                if (!ConfirmBulkBrowser(AccountsView.SelectedObjects.Count)) return;

                foreach (Account account in AccountsView.SelectedObjects)
                    new AccountBrowser(account, Link.ToString(), PostNavigation: async (page) =>
                    {
                        await (await page.WaitForSelectorAsync("#group-join-button", new WaitForSelectorOptions() { Timeout = 12000 })).ClickAsync();
                    });
            }
        }

        private void customURLJSToolStripMenuItem_Click(object sender, EventArgs e)
        {
            int Count = 1;

            if (ModifierKeys == Keys.Shift)
                int.TryParse(ShowDialog("Amount (Limited to 15)", "Launch Browser", "1"), out Count);

            if (Uri.TryCreate(ShowDialog("URL", "Launch Browser", "https://roblox.com/"), UriKind.Absolute, out Uri Link))
            {
                string Script = ShowDialog("Javascript", "Launch Browser", big: true);

                var Size = new System.Numerics.Vector2(550, 440);
                AccountBrowser.CreateGrid(Size);

                for (int i = 0; i < Math.Min(Count, 15); i++) {
                    var Browser = new AccountBrowser() { Size = Size, Index = i };

                    _ = Browser.LaunchBrowser(Url: Link.ToString(), Script: Script, PostNavigation: async (p) => await Browser.LoginTask(p));
                }
            }
        }

        /// <summary>Guards against a second collection run while one is in flight; both would hit the same rate limit.</summary>
        private CancellationTokenSource FreeItemsRun;

        private async void collectFreeItemsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (FreeItemsRun != null)
            {
                if (MessageBox.Show("A free item collection is already running.\n\nStop it?", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    FreeItemsRun.Cancel();

                return;
            }

            List<Account> Selected = AccountsView.SelectedObjects.Cast<Account>().ToList();

            if (Selected.Count == 0) return;

            int MaxPerRun = Math.Max(1, General.Get<int>("FreeItemsMaxPerRun"));

            if (MessageBox.Show($"Collect up to {MaxPerRun} free catalog items on {Selected.Count} account(s)?\n\n" +
                "Only zero-price items are bought — no Robux is ever spent. This can take a while: the catalog is rate limited, so requests are deliberately slow.\n\n" +
                "Right-click and choose this again to stop.", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            FreeItemsRun = new CancellationTokenSource();

            string Original = collectFreeItemsToolStripMenuItem.Text;

            var Report = new Progress<string>(Line => collectFreeItemsToolStripMenuItem.Text = $"Collecting: {Line}");

            try
            {
                // Discovery is account independent, so it runs once and every account shares the result.
                List<CatalogItem> Items = await FreeItems.DiscoverAsync(MaxPerRun, Report, FreeItemsRun.Token);

                if (Items.Count == 0)
                {
                    MessageBox.Show("No purchasable free items were found. Roblox may be rate limiting — try again in a few minutes.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    return;
                }

                FreeItemsSummary Summary = await FreeItems.RunAsync(Selected, Items, Report, FreeItemsRun.Token);

                MessageBox.Show($"{Summary}\n\nItems considered: {Items.Count}", "Free Items", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { Program.Logger.Info("[FreeItems] run cancelled"); }
            catch (Exception Ex)
            {
                Program.Logger.Error($"[FreeItems] run failed: {Ex}");

                MessageBox.Show($"Free item collection failed:\n\n{Ex.Message}", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                collectFreeItemsToolStripMenuItem.Text = Original;

                FreeItemsRun.Dispose();
                FreeItemsRun = null;
            }
        }

        private CancellationTokenSource WarmerRun;

        private async void warmAccountsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (WarmerRun != null)
            {
                if (MessageBox.Show("A warming pass is already running.\n\nStop it?", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    WarmerRun.Cancel();

                return;
            }

            List<Account> Selected = AccountsView.SelectedObjects.Cast<Account>().ToList();

            if (Selected.Count == 0) return;

            int PerAccount = Math.Max(1, General.Get<int>("WarmerFavoritesPerPass"));

            if (MessageBox.Show($"Favorite {PerAccount} game(s) from the public charts on {Selected.Count} account(s)?\n\n" +
                "This only calls the same endpoints the website does when you click the favourite button.\n\n" +
                "Right-click and choose this again to stop.", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            WarmerRun = new CancellationTokenSource();

            string Original = warmAccountsToolStripMenuItem.Text;

            var Report = new Progress<string>(Line => warmAccountsToolStripMenuItem.Text = $"Warming: {Line}");

            try
            {
                List<WarmGame> Games = await Warmer.DiscoverGamesAsync(WarmerRun.Token);

                if (Games.Count == 0)
                {
                    MessageBox.Show("Could not load the game charts. Roblox may be rate limiting - try again in a few minutes.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    return;
                }

                WarmerSummary Summary = await Warmer.RunAsync(Selected, Games, PerAccount, Report, WarmerRun.Token);

                MessageBox.Show($"{Summary}\n\nGames available: {Games.Count}", "Warm Accounts", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { Program.Logger.Info("[Warmer] run cancelled"); }
            catch (Exception Ex)
            {
                Program.Logger.Error($"[Warmer] run failed: {Ex}");

                MessageBox.Show($"Warming failed:\n\n{Ex.Message}", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                warmAccountsToolStripMenuItem.Text = Original;

                WarmerRun.Dispose();
                WarmerRun = null;
            }
        }

        private void copyProfileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            List<string> Profiles = new List<string>();

            foreach (Account account in AccountsView.SelectedObjects)
                Profiles.Add($"https://www.roblox.com/users/{account.UserID}/profile");

            CopyPlain(string.Join("\n", Profiles), "Select an account first.");
        }

        private void viewFieldsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null) return;

            FieldsForm.View(SelectedAccount);
        }

        private void SaveToAccount_Click(object sender, EventArgs e)
        {
            if (ModifierKeys == Keys.Shift)
            {
                List<Account> HasSaved = new List<Account>();

                foreach (Account account in GetAccountsSnapshot())
                    if (account.Fields.ContainsKey("SavedPlaceId") || account.Fields.ContainsKey("SavedJobId"))
                        HasSaved.Add(account);

                if (HasSaved.Count > 0 && MessageBox.Show($"Are you sure you want to remove {HasSaved.Count} saved Place Ids?", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.OK)
                    foreach (Account account in HasSaved)
                    {
                        account.RemoveField("SavedPlaceId");
                        account.RemoveField("SavedJobId");
                    }
            }

            foreach (Account account in AccountsView.SelectedObjects)
            {
                if (string.IsNullOrEmpty(PlaceID.Text) && string.IsNullOrEmpty(JobID.Text))
                {
                    account.RemoveField("SavedPlaceId");
                    account.RemoveField("SavedJobId");

                    continue;
                }

                string PlaceId = CurrentPlaceId;

                if (JobID.Text.Contains("privateServerLinkCode") && Regex.IsMatch(JobID.Text, @"\/games\/(\d+)\/"))
                    PlaceId = Regex.Match(CurrentJobId, @"\/games\/(\d+)\/").Groups[1].Value;

                account.SetField("SavedPlaceId", PlaceId);
                account.SetField("SavedJobId", JobID.Text);
            }
        }

        private void AccountsView_ModelCanDrop(object sender, ModelDropEventArgs e)
        {
            if (e.SourceModels[0] != null && e.SourceModels[0] is Account) e.Effect = DragDropEffects.Move;
        }

        private void AccountsView_ModelDropped(object sender, ModelDropEventArgs e)
        {
            if (e.TargetModel == null || e.SourceModels.Count == 0) return;

            Account droppedOn = e.TargetModel as Account;

            int Index = e.DropTargetIndex;

            for (int i = e.SourceModels.Count; i > 0; i--)
            {
                if (!(e.SourceModels[i - 1] is Account dragged)) continue;

                dragged.Group = droppedOn.Group;

                lock (AccountsLock)
                {
                    AccountsList.Remove(dragged);
                    AccountsList.Insert(Math.Min(Index, AccountsList.Count), dragged);
                }
            }

            RefreshView(e.SourceModels[e.SourceModels.Count - 1]);
            SaveAccounts();
        }

        private void sortAlphabeticallyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show($"Are you sure you want to sort every account alphabetically?", "SkrilyaAccountManager", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                lock (AccountsLock)
                {
                    List<Account> Sorted = AccountsList.OrderByDescending(x => x.Username.All(char.IsDigit)).ThenByDescending(x => x.Username.Any(char.IsLetter)).ThenBy(x => x.Username).ToList();

                    AccountsList.Clear();
                    AccountsList.AddRange(Sorted);
                }

                AccountsView.SetObjects(AccountsList);
                AccountsView.BuildGroups();
            }
        }

        private async void quickLogInToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SelectedAccount == null) return;

            if (!Utilities.YesNoPrompt("Quick Log In", "Only enter codes that you requested\nNever enter another user's code", $"Do you understand?", SaveIfNo: false))
                return;

            if (Clipboard.ContainsText() && Clipboard.GetText() is string ClipCode && ClipCode.Length == 6 && await SelectedAccount.QuickLogIn(ClipCode))
                return;

            string Code = ShowDialog("Code", "Quick Log In");

            if (Code.Length != 6) { MessageBox.Show("Quick Log In codes requires 6 characters", "Quick Log In", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            await SelectedAccount.QuickLogIn(Code);
        }

        private void toggleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AccountsView.ShowGroups = !AccountsView.ShowGroups;

            if (AccountsView.HeaderStyle != ColumnHeaderStyle.None) AccountsView.HeaderStyle = AccountsView.ShowGroups ? ColumnHeaderStyle.Nonclickable : ColumnHeaderStyle.Clickable;

            AccountsView.BuildGroups();
        }

        private void EditTheme_Click(object sender, EventArgs e)
        {
            if (ThemeForm != null && ThemeForm.Visible)
            {
                ThemeForm.Hide();
                return;
            }

            ThemeForm.Show();
        }

        private void LaunchNexus_Click(object sender, EventArgs e)
        {
            if (ControlForm != null)
            {
                ControlForm.Top = Bottom;
                ControlForm.Left = Left;
                ControlForm.Show();
                ControlForm.BringToFront();
            }
            else
            {
                ControlForm = new AccountControl
                {
                    StartPosition = FormStartPosition.Manual,
                    Top = Bottom,
                    Left = Left
                };
                ControlForm.Show();
                ControlForm.ApplyTheme();
            }
        }

        private async Task LaunchAccounts(List<Account> Accounts, long PlaceID, string JobID, bool FollowUser = false, bool VIPServer = false)
        {
            int Delay = General.Exists("AccountJoinDelay") ? General.Get<int>("AccountJoinDelay") : 8;

            bool AsyncJoin = General.Get<bool>("AsyncJoin");
            CancellationTokenSource Token = LauncherToken;

            foreach (Account account in Accounts)
            {
                if (Token.IsCancellationRequested) break;

                long PlaceId = PlaceID;
                string JobId = JobID;

                if (!FollowUser)
                {
                    if (!string.IsNullOrEmpty(account.GetField("SavedPlaceId")) && long.TryParse(account.GetField("SavedPlaceId"), out long PID)) PlaceId = PID;
                    if (!string.IsNullOrEmpty(account.GetField("SavedJobId"))) JobId = account.GetField("SavedJobId");
                }

                await account.JoinServer(PlaceId, JobId, FollowUser, VIPServer);

                if (AsyncJoin)
                {
                    while (!LaunchNext)
                        await Task.Delay(50);
                }
                else
                    await Task.Delay(Delay * 1000);

                LaunchNext = false;
            }

            LaunchNext = false;

            Token.Cancel();
            Token.Dispose();
        }

        public void NextAccount() => LaunchNext = true;
        public void CancelLaunching()
        {
            if (LauncherToken != null && !LauncherToken.IsCancellationRequested)
                LauncherToken.Cancel();
        }

        private void infoToolStripMenuItem1_Click(object sender, EventArgs e) =>
            MessageBox.Show("SkrilyaAccountManager created by ic3w0lf under the GNU GPLv3 license.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Information);

        private void groupsToolStripMenuItem_Click(object sender, EventArgs e) =>
            MessageBox.Show("Groups can be sorted by naming them a number then whatever you want.\nFor example: You can put Group Apple on top by naming it '001 Apple' or '1Apple'.\nThe numbers will be hidden from the name but will be correctly sorted depending on the number.\nAccounts can also be dragged into groups.", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Information);

        private void DonateButton_Click(object sender, EventArgs e) =>
            Process.Start("https://ic3w0lf22.github.io/donate.html");

        private void ConfigButton_Click(object sender, EventArgs e)
        {
            SettingsForm ??= new SettingsForm();

            if (SettingsForm.Visible)
            {
                SettingsForm.WindowState = FormWindowState.Normal;
                SettingsForm.BringToFront();
            }
            else
                SettingsForm.Show();

            SettingsForm.StartPosition = FormStartPosition.Manual;
            SettingsForm.Top = Top;
            SettingsForm.Left = Right;
        }

        private void HistoryIcon_MouseHover(object sender, EventArgs e) => RGForm.ShowForm();

        private void ShuffleIcon_Click(object sender, EventArgs e)
        {
            ShuffleJobID = !ShuffleJobID;

            if (sender != null)
            {
                General.Set("ShuffleJobId", ShuffleJobID ? "true" : "false");
                IniSettings.Save("RAMSettings.ini");
            }

            if (ShuffleJobID)
                if (ThemeEditor.LightImages)
                    ShuffleIcon.ColorImage(87, 245, 102);
                else
                    ShuffleIcon.ColorImage(57, 152, 22);
            else
            {
                if (BackColor.GetBrightness() < 0.5)
                    ShuffleIcon.ColorImage(255, 255, 255);
                else
                    ShuffleIcon.ColorImage(0, 0, 0);
            }
        }

        private async void ShowDetailsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string DumpDir = Path.Combine(Environment.CurrentDirectory, "AccountDumps");

            if (!Directory.Exists(DumpDir))
                Directory.CreateDirectory(DumpDir);

            List<Account> Accounts = new List<Account>(AccountsView.SelectedObjects.Cast<Account>());

            if (Accounts.Count == 0) return;

            bool OpenEach = Accounts.Count <= 5; // avoid spawning hundreds of editor windows at scale

            // Bound concurrent HTTP: fanning out one unthrottled Task per account fired thousands of requests
            // at once (socket exhaustion / Roblox 429 storm) and, with no try/catch, an error crashed the app.
            using SemaphoreSlim Gate = new SemaphoreSlim(4);

            IEnumerable<Task> Dumps = Accounts.Select(async Account =>
            {
                await Gate.WaitAsync();

                try
                {
                    var UserInfo = await Account.GetUserInfo();
                    double AccountAge = -1;

                    if (UserInfo != null && UserInfo["created"] != null && DateTime.TryParse(UserInfo["created"].Value<string>(), out DateTime CreationTime))
                        AccountAge = (DateTime.UtcNow - CreationTime).TotalDays;

                    StringBuilder builder = new StringBuilder();

                    builder.AppendLine($"Username: {Account.Username}");
                    builder.AppendLine($"UserId: {Account.UserID}");
                    builder.AppendLine($"Robux: {await Account.GetRobux()}");
                    builder.AppendLine($"Account Age: {(AccountAge >= 0 ? $"{AccountAge:F1}" : "UNKNOWN")}");
                    builder.AppendLine($"Email Status: {await Account.GetEmailJSON()}");
                    builder.AppendLine($"User Info: {UserInfo}");
                    builder.AppendLine($"Other: {await Account.GetMobileInfo()}");
                    builder.AppendLine($"Fields: {JsonConvert.SerializeObject(Account.Fields)}");

                    string FileName = Path.Combine(DumpDir, Account.Username + ".txt");

                    File.WriteAllText(FileName, builder.ToString());

                    if (OpenEach)
                        Process.Start(FileName);
                }
                catch (Exception x) { Program.Logger.Error($"ShowDetails failed for {Account.Username}: {x.Message}"); }
                finally { Gate.Release(); }
            });

            await Task.WhenAll(Dumps);

            if (!OpenEach)
                try { Process.Start(DumpDir); } catch { }
        }

        CancellationTokenSource PresenceCancellationToken;

        private void AccountsView_Scroll(object sender, ScrollEventArgs e)
        {
            // Cancel any pending presence update; only touch the token when it actually exists (guarding
            // against a NullReferenceException on the first scroll when ShowPresence is disabled).
            if (PresenceCancellationToken != null)
                PresenceCancellationToken.Cancel();

            if (!General.Get<bool>("ShowPresence")) return;

            PresenceCancellationToken = new CancellationTokenSource();
            var Token = PresenceCancellationToken.Token;

            Task.Run(async () =>
            {
                await Task.Delay(3500); // Wait until the user has stopped scrolling before updating account presence

                if (Token.IsCancellationRequested)
                    return;

                AccountsView.InvokeIfRequired(async () => await UpdatePresence());
            }, PresenceCancellationToken.Token);
        }

        /// <summary>
        /// Refreshes what every account is doing.
        ///
        /// This used to ask only about the rows hit-tested as visible in THIS window's list. That was a sensible
        /// saving when this window was the only one — you cannot read a row you cannot see — but the HTML
        /// interface shows its own list, and for anything below this window's fold, or whenever this window is
        /// minimised, presence simply never arrived: those rows sat on "Offline" forever.
        ///
        /// Asking about everyone is not the extravagance it looks like: Roblox takes a list per request, so a
        /// hundred accounts cost one request rather than a hundred.
        /// </summary>
        private async Task UpdatePresence()
        {
            if (!General.Get<bool>("ShowPresence")) return;

            long[] Ids = GetAccountsSnapshot()
                .Where(account => account.UserID > 0)
                .Select(account => account.UserID)
                .Distinct()
                .ToArray();

            // Roblox caps the list at 100 per request and rejects anything longer outright, so a big store goes
            // up in batches, spaced out rather than fired together.
            for (int At = 0; At < Ids.Length; At += PresenceBatch)
            {
                try { await Presence.UpdatePresence(Ids.Skip(At).Take(PresenceBatch).ToArray()); }
                catch { break; } // one refused batch means the next will be refused too

                if (At + PresenceBatch < Ids.Length) await Task.Delay(700);
            }
        }

        /// <summary>How many user ids go into one presence request.</summary>
        private const int PresenceBatch = 100;

        /// <summary>Minutes between presence sweeps. The setting existed and was read by nobody.</summary>
        private static double PresenceMinutes =>
            int.TryParse(General.Get("PresenceUpdateRate"), out int Minutes) && Minutes > 0 ? Minutes : 5;

        private void JobID_Click( object sender, EventArgs e )
        {
            JobID.SelectAll(); // Allows quick replacing of the JobID with a click and ctrl-v.
        }

        private void PlaceID_Click( object sender, EventArgs e )
        {
            PlaceID.SelectAll(); // Allows quick replacing of the PlaceID with a click and ctrl-v.
        }
    }
}

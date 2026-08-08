using RestSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Binds one proxy to one account, for the whole of that account's launch.
    ///
    /// The reason a per-account proxy is not just "nice to have": Roblox rate-limits the authentication-ticket
    /// endpoint per IP, so launching a dozen accounts from one address earns 429s partway through the batch. And
    /// the ticket is bound to the IP that requested it, so the manager's request and the client's redeem must
    /// leave through the same exit — which is why the proxy is resolved once here and used on both sides.
    ///
    /// Everything is opt-in: an account with no <c>Proxy</c> field behaves exactly as before.
    /// </summary>
    internal static class AccountProxies
    {
        /// <summary>Per-account field holding the proxy string. Lives in AccountData.json's Fields, so stock RAM ignores it harmlessly.</summary>
        public const string ProxyField = "Proxy";

        /// <summary>Per-account field recording the exit IP seen on the last launch, so rotation is visible.</summary>
        public const string ExitIPField = "ProxyExitIP";

        public static bool Enabled { get; private set; }
        public static bool SplitTunnel { get; private set; }
        public static bool VerifyExitIP { get; private set; }
        public static bool DirectLaunch { get; private set; }
        public static bool ReapDialogs { get; private set; }
        public static string TitleTemplate { get; private set; } = string.Empty;
        public static string DesktopName { get; private set; } = string.Empty;

        private static readonly Dictionary<string, ProxyConfig> ParseCache = new Dictionary<string, ProxyConfig>();
        private static readonly Dictionary<string, RestClient> Clients = new Dictionary<string, RestClient>();
        private static readonly object CacheLock = new object();

        /// <summary>
        /// Reads the settings block. Called at startup and whenever the settings form writes; every value is read
        /// defensively because IniSection.Get&lt;T&gt; throws on a malformed value instead of falling back.
        /// </summary>
        public static void LoadSettings()
        {
            Enabled = Bool("UseAccountProxies", false);
            SplitTunnel = Bool("ProxySplitTunnel", true);
            VerifyExitIP = Bool("ProxyVerifyExitIP", true);
            DirectLaunch = Bool("DirectLaunch", true);
            ReapDialogs = Bool("CloseRobloxErrorDialogs", false);

            // The template and the desktop name are separate from their on/off switches because IniFile.Set
            // deletes a key whose value is empty — an "off means empty string" setting could never be persisted.
            TitleTemplate = Bool("RenameClientWindows", false) ? Text("ClientWindowTitle", "{name} - Roblox") : string.Empty;
            DesktopName = Bool("UseSeparateDesktop", false) ? Text("ClientDesktopName", "RAMClients") : string.Empty;

            ClientLogWatcher.Enabled = Bool("WatchLaunchResult", true);
            ClientWindows.ReapDialogs = ReapDialogs;

            ZombieReaper.LoadSettings();
            StuckDetector.LoadSettings();
            Relauncher.LoadSettings();

            if (ReapDialogs) ClientWindows.EnsureTimer();

            SlotProxy.LoadIdentityHosts();
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

        private static string Text(string Name, string Default)
        {
            try { return AccountManager.General.Exists(Name) ? (AccountManager.General.Get(Name) ?? Default) : Default; }
            catch { return Default; }
        }

        /// <summary>The proxy assigned to an account, or null when the feature is off or the account has none.</summary>
        public static ProxyConfig For(Account account)
        {
            if (!Enabled || account == null) return null;

            string Raw = account.GetField(ProxyField);

            if (string.IsNullOrWhiteSpace(Raw)) return null;

            lock (CacheLock)
            {
                if (ParseCache.TryGetValue(Raw, out ProxyConfig Cached)) return Cached;

                if (!ProxyConfig.TryParse(Raw, out ProxyConfig Parsed))
                {
                    Program.Logger.Warn($"[Proxy] {account.Username}: unparseable proxy \"{Raw}\" — launching without a proxy");

                    ParseCache[Raw] = null; // remember the failure too, so it is logged once and not per launch

                    return null;
                }

                ParseCache[Raw] = Parsed;

                return Parsed;
            }
        }

        public static void ForgetCaches()
        {
            lock (CacheLock)
            {
                ParseCache.Clear();

                foreach (RestClient Client in Clients.Values)
                    try { Client.Dispose(); } catch { }

                Clients.Clear();
            }
        }

        /// <summary>
        /// A RestClient for <paramref name="BaseUrl"/> that goes through <paramref name="Proxy"/>. Cached per
        /// (host, proxy): RestClient owns an HttpClient, and building one per request exhausts sockets.
        /// Passing a null proxy returns null so callers fall back to the shared clients.
        /// </summary>
        public static RestClient ClientFor(string BaseUrl, ProxyConfig Proxy)
        {
            if (Proxy == null) return null;

            string Key = BaseUrl + "|" + Proxy.Raw;

            lock (CacheLock)
            {
                if (Clients.TryGetValue(Key, out RestClient Existing)) return Existing;

                RestClientOptions Options = new RestClientOptions(BaseUrl)
                {
                    MaxTimeout = 25000, // a residential exit is slower than a direct connection; 15s times out honest proxies
                    Proxy = Proxy.ToWebProxy()
                };

                RestClient Client = new RestClient(Options);

                Clients[Key] = Client;

                return Client;
            }
        }

        public static RestClient AuthClientFor(ProxyConfig Proxy) => ClientFor("https://auth.roblox.com/", Proxy);

        /// <summary>
        /// Resolves the proxy's current exit IP and records it on the account. Returns null when the lookup fails,
        /// which is itself the useful signal: a proxy that cannot be reached from here will not serve the client
        /// either, and failing before the ticket is spent is much cheaper than failing after.
        /// </summary>
        public static async Task<string> CheckExitIP(Account account, ProxyConfig Proxy)
        {
            if (Proxy == null || !VerifyExitIP) return null;

            string IP = await ProxyExitIP.GetAsync(Proxy).ConfigureAwait(false);

            if (string.IsNullOrEmpty(IP)) return null;

            string Previous = account?.GetField(ExitIPField);

            if (!string.IsNullOrEmpty(Previous) && Previous != IP)
                Program.Logger.Warn($"[Proxy] {account?.Username}: exit IP changed {Previous} -> {IP}. If this happens every launch the proxy is rotating, and Roblox will reject tickets with 403.");

            if (account != null && Previous != IP) account.SetField(ExitIPField, IP);

            return IP;
        }

        /// <summary>
        /// Starts the loopback proxy the client will be pointed at, or returns null when the client can talk to
        /// the upstream directly. A slot is needed when only identity traffic should be tunnelled, and always for
        /// SOCKS (the client's env-var proxy support is HTTP-oriented, and split-tunnelling is not expressible there).
        /// </summary>
        public static SlotProxy StartSlot(Account account, ProxyConfig Proxy)
        {
            if (Proxy == null || !SplitTunnel) return null;

            try
            {
                SlotProxy Slot = new SlotProxy(Proxy, TunnelMode.IdentityOnly, account?.Username ?? "slot");

                Slot.Start();

                return Slot;
            }
            catch (Exception x)
            {
                Program.Logger.Error($"[Proxy] failed to start slot proxy for {account?.Username}: {x.Message}");

                return null;
            }
        }

        /// <summary>Ties a slot proxy's lifetime to the client process it serves, so it is not leaked when the client exits.</summary>
        public static void BindToProcess(SlotProxy Slot, Process Client)
        {
            if (Slot == null) return;

            if (Client == null)
            {
                Slot.Dispose();
                return;
            }

            try
            {
                Client.EnableRaisingEvents = true;
                Client.Exited += (s, e) => { try { Slot.Dispose(); } catch { } };

                // EnableRaisingEvents does not fire for a process that already exited before we subscribed.
                if (Client.HasExited) Slot.Dispose();

                // A client that ignored the proxy environment looks exactly like a working launch — it just
                // silently uses the machine's own IP. Zero connections after 45 seconds is that symptom, and it
                // is worth saying out loud rather than leaving someone to wonder why the proxy changed nothing.
                _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ =>
                {
                    if (!Slot.Running) return;

                    if (Slot.TunnelledCount + Slot.DirectCount + Slot.FailedCount == 0)
                        Program.Logger.Warn("[Proxy] the client has not used its proxy slot at all — this build of Roblox may be ignoring the http_proxy environment variables. The manager's own login still went through the proxy.");
                    else
                        Program.Logger.Info($"[Proxy] slot in use: {Slot.TunnelledCount} tunnelled, {Slot.DirectCount} direct, {Slot.FailedCount} failed");
                });
            }
            catch (Exception x)
            {
                Program.Logger.Warn($"[Proxy] could not bind slot lifetime to process: {x.Message}");

                // A slot with no owner would listen forever; a 10 minute leash is better than a leak.
                _ = Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => { try { Slot.Dispose(); } catch { } });
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// A single proxy endpoint assigned to an account.
    ///
    /// Accepted text forms (the ones people actually paste out of proxy panels):
    ///   host:port
    ///   host:port:user:pass
    ///   user:pass@host:port
    ///   scheme://host:port
    ///   scheme://user:pass@host:port
    ///   scheme://host:port:user:pass
    /// where scheme is http, https, socks4, socks4a or socks5. No scheme means http.
    /// </summary>
    public class ProxyConfig // public: it appears in Account's (public) method signatures
    {
        public string Scheme { get; private set; } = "http";
        public string Host { get; private set; }
        public int Port { get; private set; }
        public string Username { get; private set; } = string.Empty;
        public string Password { get; private set; } = string.Empty;

        /// <summary>Exactly what the user typed. Used as the cache key everywhere, so the same text always maps to the same sticky session.</summary>
        public string Raw { get; private set; }

        public bool IsSocks => Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
        public bool HasCredentials => !string.IsNullOrEmpty(Username);

        public static bool TryParse(string Text, out ProxyConfig Config)
        {
            Config = null;

            if (string.IsNullOrWhiteSpace(Text)) return false;

            string Raw = Text.Trim();
            string Rest = Raw;
            string Scheme = "http";

            int SchemeEnd = Rest.IndexOf("://", StringComparison.Ordinal);

            if (SchemeEnd > 0)
            {
                Scheme = Rest.Substring(0, SchemeEnd).ToLowerInvariant();
                Rest = Rest.Substring(SchemeEnd + 3);

                if (Scheme != "http" && Scheme != "https" && Scheme != "socks4" && Scheme != "socks4a" && Scheme != "socks5")
                    return false;
            }

            // https:// upstreams are still spoken to as ordinary HTTP proxies; the difference would be TLS to the
            // proxy itself, which no consumer proxy panel actually hands out. Normalise so downstream code has two cases.
            if (Scheme == "https") Scheme = "http";

            string Username = string.Empty, Password = string.Empty;

            int At = Rest.LastIndexOf('@');

            if (At >= 0)
            {
                string Credentials = Rest.Substring(0, At);
                Rest = Rest.Substring(At + 1);

                int Split = Credentials.IndexOf(':');

                if (Split < 0) return false;

                Username = Credentials.Substring(0, Split);
                Password = Credentials.Substring(Split + 1);
            }

            string[] Parts = Rest.Split(':');

            // host:port  |  host:port:user:pass
            if (Parts.Length != 2 && Parts.Length != 4) return false;
            if (!int.TryParse(Parts[1], out int Port) || Port <= 0 || Port > 65535) return false;
            if (string.IsNullOrWhiteSpace(Parts[0])) return false;

            if (Parts.Length == 4)
            {
                Username = Parts[2];
                Password = Parts[3];
            }

            Config = new ProxyConfig
            {
                Scheme = Scheme,
                Host = Parts[0].Trim(),
                Port = Port,
                Username = Username ?? string.Empty,
                Password = Password ?? string.Empty,
                Raw = Raw
            };

            return true;
        }

        /// <summary>For RestSharp / HttpClient, so the auth ticket is requested from the same exit IP the client will use.</summary>
        public IWebProxy ToWebProxy()
        {
            // WebProxy speaks HTTP proxies only. A SOCKS upstream is fronted by the loopback bridge, which
            // converts HTTP-proxy semantics into a SOCKS handshake.
            if (IsSocks) return new WebProxy(SocksBridge.GetOrCreate(this).Address);

            WebProxy Proxy = new WebProxy(Host, Port);

            if (HasCredentials) Proxy.Credentials = new NetworkCredential(Username, Password);

            return Proxy;
        }

        /// <summary>The value handed to the client process through http_proxy / https_proxy / all_proxy.</summary>
        public string ToEnvUrl()
        {
            string Credentials = HasCredentials ? $"{Uri.EscapeDataString(Username)}:{Uri.EscapeDataString(Password)}@" : string.Empty;

            return $"{(Scheme == "socks4a" ? "socks4a" : Scheme)}://{Credentials}{Host}:{Port}";
        }

        /// <summary>Password-free rendering, for logs and the UI.</summary>
        public override string ToString() => $"{Scheme}://{(HasCredentials ? Username + ":***@" : string.Empty)}{Host}:{Port}";

        public string ProxyAuthorizationHeader()
        {
            if (!HasCredentials) return null;

            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        }
    }

    /// <summary>
    /// Exit-IP lookups, cached per proxy string.
    ///
    /// Why this exists: an authentication ticket is bound to the IP that requested it. If the upstream rotates
    /// between the ticket call and the client's redeem, Roblox answers 403 "Authentication ticket was invalid"
    /// and the launch dies with no useful message. Resolving the exit IP on both sides turns that into a
    /// diagnosable "your proxy is rotating" instead.
    /// </summary>
    internal static class ProxyExitIP
    {
        private class Entry
        {
            public string IP;
            public DateTime Fetched;
        }

        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>();
        private static readonly object CacheLock = new object();

        /// <summary>How long a resolved exit IP is trusted before we ask again.</summary>
        public static TimeSpan CacheDuration = TimeSpan.FromMinutes(2);

        public static void Forget(ProxyConfig Proxy)
        {
            if (Proxy == null) return;

            lock (CacheLock) Cache.Remove(Proxy.Raw);
        }

        public static async Task<string> GetAsync(ProxyConfig Proxy, bool Refresh = false)
        {
            if (Proxy == null) return null;

            lock (CacheLock)
                if (!Refresh && Cache.TryGetValue(Proxy.Raw, out Entry Cached) && (DateTime.Now - Cached.Fetched) < CacheDuration)
                    return Cached.IP;

            string IP = null;

            try
            {
                using (HttpClientHandler Handler = new HttpClientHandler { Proxy = Proxy.ToWebProxy(), UseProxy = true })
                using (HttpClient Client = new HttpClient(Handler) { Timeout = TimeSpan.FromSeconds(12) })
                    IP = (await Client.GetStringAsync("https://api.ipify.org").ConfigureAwait(false))?.Trim();
            }
            catch (Exception x) { Program.Logger.Warn($"[Proxy] exit IP lookup failed for {Proxy}: {x.Message}"); }

            if (string.IsNullOrEmpty(IP) || IP.Length > 45) return null;

            lock (CacheLock) Cache[Proxy.Raw] = new Entry { IP = IP, Fetched = DateTime.Now };

            return IP;
        }
    }

    /// <summary>
    /// Loopback HTTP-proxy front end for a SOCKS upstream, one per distinct proxy string.
    ///
    /// .NET's WebProxy cannot speak SOCKS, and the whole point of the per-account proxy is that the manager's
    /// ticket request and the client leave through the same exit. Rather than special-case SOCKS in every
    /// caller, a SOCKS proxy gets a tiny loopback listener that accepts CONNECT and performs the SOCKS
    /// handshake itself; from the outside it is an ordinary HTTP proxy.
    /// </summary>
    internal static class SocksBridge
    {
        private static readonly Dictionary<string, SlotProxy> Bridges = new Dictionary<string, SlotProxy>();
        private static readonly object BridgesLock = new object();

        public static SlotProxy GetOrCreate(ProxyConfig Proxy)
        {
            lock (BridgesLock)
            {
                if (Bridges.TryGetValue(Proxy.Raw, out SlotProxy Existing) && Existing.Running) return Existing;

                // Everything goes upstream: this bridge exists purely to reach the SOCKS exit, so a direct
                // fallback here would silently change the IP — the exact failure the design is avoiding.
                SlotProxy Bridge = new SlotProxy(Proxy, TunnelMode.Everything, $"socks-bridge:{Proxy.Host}:{Proxy.Port}");

                Bridge.Start();

                Bridges[Proxy.Raw] = Bridge;

                return Bridge;
            }
        }

        public static void StopAll()
        {
            lock (BridgesLock)
            {
                foreach (SlotProxy Bridge in Bridges.Values)
                    try { Bridge.Dispose(); } catch { }

                Bridges.Clear();
            }
        }
    }
}

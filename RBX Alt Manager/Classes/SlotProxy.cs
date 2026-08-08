using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    internal enum TunnelMode
    {
        /// <summary>Every connection is routed through the upstream proxy.</summary>
        Everything,

        /// <summary>Only identity-bearing hosts go through the upstream; assets, CDN and telemetry go direct.</summary>
        IdentityOnly
    }

    /// <summary>
    /// A loopback HTTP proxy handed to exactly one Roblox client (or to one SOCKS upstream, see <see cref="SocksBridge"/>).
    ///
    /// Why not point the client straight at the upstream: the client pulls tens of megabytes of assets per join.
    /// Sending all of it through a residential/mobile exit makes joins crawl and burns the proxy's traffic quota,
    /// while none of that traffic carries the account's identity. In <see cref="TunnelMode.IdentityOnly"/> the
    /// hosts that Roblox ties to the session (auth, gamejoin, assetgame's PlaceLauncher, apis, users) are
    /// tunnelled through the sticky upstream and everything else goes direct.
    ///
    /// The one rule that must not bend: an identity host never falls back to a direct connection. The auth
    /// ticket is bound to the IP that requested it, so a "helpful" fallback changes the IP mid-launch and Roblox
    /// answers 403 with a message that looks like a bad cookie. Failing loudly is correct here.
    /// </summary>
    internal class SlotProxy : IDisposable
    {
        /// <summary>Hosts whose traffic identifies the account. Suffix-matched, so subdomains are covered.</summary>
        private static readonly string[] DefaultIdentityHosts =
        {
            "auth.roblox.com",
            "gamejoin.roblox.com",
            "assetgame.roblox.com",
            "apis.roblox.com",
            "users.roblox.com",
            "www.roblox.com",
            "web.roblox.com",
            "presence.roblox.com"
        };

        public static string[] IdentityHosts = DefaultIdentityHosts;

        /// <summary>Re-reads the identity host list from settings. Empty/absent setting keeps the built-in list.</summary>
        public static void LoadIdentityHosts()
        {
            try
            {
                string Configured = AccountManager.General.Exists("ProxyIdentityHosts") ? AccountManager.General.Get<string>("ProxyIdentityHosts") : string.Empty;

                string[] Hosts = (Configured ?? string.Empty)
                    .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Host => Host.Trim().ToLowerInvariant())
                    .Where(Host => Host.Length > 0)
                    .ToArray();

                IdentityHosts = Hosts.Length > 0 ? Hosts : DefaultIdentityHosts;
            }
            catch { IdentityHosts = DefaultIdentityHosts; }
        }

        private readonly ProxyConfig Upstream;
        private readonly TunnelMode Mode;
        private readonly string Label;

        private TcpListener Listener;
        private CancellationTokenSource Cancellation;

        public bool Running { get; private set; }
        public int Port { get; private set; }
        public string Address => $"http://127.0.0.1:{Port}";

        public int TunnelledCount;
        public int DirectCount;
        public int FailedCount;

        public SlotProxy(ProxyConfig Upstream, TunnelMode Mode, string Label)
        {
            this.Upstream = Upstream ?? throw new ArgumentNullException(nameof(Upstream));
            this.Mode = Mode;
            this.Label = Label ?? "slot";
        }

        public void Start()
        {
            if (Running) return;

            Listener = new TcpListener(IPAddress.Loopback, 0); // ephemeral port: no config, no collisions between slots
            Listener.Start();

            Port = ((IPEndPoint)Listener.LocalEndpoint).Port;
            Cancellation = new CancellationTokenSource();
            Running = true;

            Program.Logger.Info($"[SlotProxy {Label}] listening on 127.0.0.1:{Port} -> {Upstream} ({Mode})");

            _ = Task.Run(() => AcceptLoop(Cancellation.Token));
        }

        private async Task AcceptLoop(CancellationToken Token)
        {
            while (!Token.IsCancellationRequested)
            {
                TcpClient Client;

                try { Client = await Listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception x) { Program.Logger.Error($"[SlotProxy {Label}] accept failed: {x.Message}"); break; }

                // Each client connection is independent; one failing must not take the listener down.
                _ = Task.Run(async () =>
                {
                    try { await HandleClient(Client, Token).ConfigureAwait(false); }
                    catch (Exception x) { Program.Logger.Debug($"[SlotProxy {Label}] connection error: {x.Message}"); }
                    finally { try { Client.Close(); } catch { } }
                });
            }

            Running = false;
        }

        private async Task HandleClient(TcpClient Client, CancellationToken Token)
        {
            Client.NoDelay = true;

            NetworkStream ClientStream = Client.GetStream();

            HttpHead Head = await HttpHead.ReadAsync(ClientStream, Token).ConfigureAwait(false);

            if (Head == null) return;

            bool IsConnect = Head.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);

            if (!Head.TryGetTarget(out string Host, out int Port))
            {
                await WriteStatus(ClientStream, "400 Bad Request", Token).ConfigureAwait(false);
                return;
            }

            bool Identity = Mode == TunnelMode.Everything || IsIdentityHost(Host);

            // Plain HTTP through an HTTP upstream is forwarded in absolute form, the way ordinary browsers talk to
            // a proxy. Tunnelling it with CONNECT would also work, but plenty of upstreams only allow CONNECT to 443.
            bool ForwardAbsolute = Identity && !IsConnect && !Upstream.IsSocks;

            TcpClient Remote = null;

            try
            {
                if (Identity)
                {
                    try
                    {
                        Remote = ForwardAbsolute ? await ConnectToUpstream(Token).ConfigureAwait(false) : await ConnectThroughUpstream(Host, Port, Token).ConfigureAwait(false);
                        Interlocked.Increment(ref TunnelledCount);
                    }
                    catch (Exception x)
                    {
                        // Deliberately no direct fallback — see the class comment.
                        Interlocked.Increment(ref FailedCount);
                        Program.Logger.Warn($"[SlotProxy {Label}] upstream failed for identity host {Host}:{Port}: {x.Message}");

                        await WriteStatus(ClientStream, "502 Bad Gateway", Token).ConfigureAwait(false);
                        return;
                    }
                }
                else
                {
                    try
                    {
                        Remote = new TcpClient { NoDelay = true };
                        await Remote.ConnectAsync(Host, Port).ConfigureAwait(false);
                        Interlocked.Increment(ref DirectCount);
                    }
                    catch (Exception x)
                    {
                        Interlocked.Increment(ref FailedCount);
                        Program.Logger.Debug($"[SlotProxy {Label}] direct connect failed for {Host}:{Port}: {x.Message}");

                        await WriteStatus(ClientStream, "502 Bad Gateway", Token).ConfigureAwait(false);
                        return;
                    }
                }

                NetworkStream RemoteStream = Remote.GetStream();

                if (IsConnect)
                {
                    await WriteStatus(ClientStream, "200 Connection established", Token).ConfigureAwait(false);
                }
                else
                {
                    // Plain HTTP. A proxy receives absolute-form request lines ("GET http://host/path"); an origin
                    // server expects origin-form ("GET /path"), so the line is rewritten when we connected direct.
                    byte[] Request = Encoding.ASCII.GetBytes(Head.BuildForwardHead(OriginForm: !ForwardAbsolute, ProxyAuthorization: ForwardAbsolute ? Upstream.ProxyAuthorizationHeader() : null));

                    await RemoteStream.WriteAsync(Request, 0, Request.Length, Token).ConfigureAwait(false);

                    if (Head.Leftover.Length > 0)
                        await RemoteStream.WriteAsync(Head.Leftover, 0, Head.Leftover.Length, Token).ConfigureAwait(false);
                }

                await Pump(ClientStream, RemoteStream, Token).ConfigureAwait(false);
            }
            finally { try { Remote?.Close(); } catch { } }
        }

        private static bool IsIdentityHost(string Host)
        {
            if (string.IsNullOrEmpty(Host)) return false;

            string Lower = Host.ToLowerInvariant();

            foreach (string Identity in IdentityHosts)
                if (Lower == Identity || Lower.EndsWith("." + Identity, StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>Raw TCP to the upstream proxy, for absolute-form HTTP forwarding (no tunnel).</summary>
        private async Task<TcpClient> ConnectToUpstream(CancellationToken Token)
        {
            TcpClient Remote = new TcpClient { NoDelay = true };

            try
            {
                await Remote.ConnectAsync(Upstream.Host, Upstream.Port).ConfigureAwait(false);

                return Remote;
            }
            catch
            {
                try { Remote.Close(); } catch { }
                throw;
            }
        }

        private async Task<TcpClient> ConnectThroughUpstream(string Host, int Port, CancellationToken Token)
        {
            TcpClient Remote = new TcpClient { NoDelay = true };

            try
            {
                await Remote.ConnectAsync(Upstream.Host, Upstream.Port).ConfigureAwait(false);

                NetworkStream Stream = Remote.GetStream();

                if (Upstream.IsSocks)
                    await SocksHandshake(Stream, Host, Port, Token).ConfigureAwait(false);
                else
                    await HttpConnectHandshake(Stream, Host, Port, Token).ConfigureAwait(false);

                return Remote;
            }
            catch
            {
                try { Remote.Close(); } catch { }
                throw;
            }
        }

        private async Task HttpConnectHandshake(NetworkStream Stream, string Host, int Port, CancellationToken Token)
        {
            StringBuilder Request = new StringBuilder();

            Request.Append($"CONNECT {Host}:{Port} HTTP/1.1\r\n");
            Request.Append($"Host: {Host}:{Port}\r\n");

            string Authorization = Upstream.ProxyAuthorizationHeader();

            if (Authorization != null) Request.Append($"Proxy-Authorization: {Authorization}\r\n");

            Request.Append("Proxy-Connection: Keep-Alive\r\n\r\n");

            byte[] Bytes = Encoding.ASCII.GetBytes(Request.ToString());

            await Stream.WriteAsync(Bytes, 0, Bytes.Length, Token).ConfigureAwait(false);

            HttpHead Response = await HttpHead.ReadAsync(Stream, Token).ConfigureAwait(false);

            if (Response == null) throw new IOException("upstream closed during CONNECT");

            // "HTTP/1.1 200 Connection established" — anything but 2xx means the proxy refused (407 = bad credentials).
            string[] Parts = Response.StartLine.Split(' ');

            if (Parts.Length < 2 || !int.TryParse(Parts[1], out int Status) || Status < 200 || Status > 299)
                throw new IOException($"upstream refused CONNECT: {Response.StartLine}");
        }

        private async Task SocksHandshake(NetworkStream Stream, string Host, int Port, CancellationToken Token)
        {
            if (Upstream.Scheme == "socks5")
            {
                byte[] Greeting = Upstream.HasCredentials
                    ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                    : new byte[] { 0x05, 0x01, 0x00 };

                await Stream.WriteAsync(Greeting, 0, Greeting.Length, Token).ConfigureAwait(false);

                byte[] Choice = await ReadExactly(Stream, 2, Token).ConfigureAwait(false);

                if (Choice[0] != 0x05) throw new IOException("bad SOCKS5 greeting reply");

                if (Choice[1] == 0x02)
                {
                    if (!Upstream.HasCredentials) throw new IOException("SOCKS5 proxy wants credentials, none configured");

                    byte[] User = Encoding.UTF8.GetBytes(Upstream.Username);
                    byte[] Pass = Encoding.UTF8.GetBytes(Upstream.Password);

                    byte[] Auth = new byte[3 + User.Length + Pass.Length];
                    Auth[0] = 0x01;
                    Auth[1] = (byte)User.Length;
                    Buffer.BlockCopy(User, 0, Auth, 2, User.Length);
                    Auth[2 + User.Length] = (byte)Pass.Length;
                    Buffer.BlockCopy(Pass, 0, Auth, 3 + User.Length, Pass.Length);

                    await Stream.WriteAsync(Auth, 0, Auth.Length, Token).ConfigureAwait(false);

                    byte[] AuthReply = await ReadExactly(Stream, 2, Token).ConfigureAwait(false);

                    if (AuthReply[1] != 0x00) throw new IOException("SOCKS5 authentication rejected");
                }
                else if (Choice[1] != 0x00) throw new IOException($"SOCKS5 proxy chose unsupported auth method {Choice[1]}");

                byte[] HostBytes = Encoding.ASCII.GetBytes(Host);

                if (HostBytes.Length > 255) throw new IOException("hostname too long for SOCKS5");

                byte[] Request = new byte[7 + HostBytes.Length];
                Request[0] = 0x05; // version
                Request[1] = 0x01; // CONNECT
                Request[2] = 0x00; // reserved
                Request[3] = 0x03; // address type: domain name (let the proxy resolve, so DNS also leaves from the exit)
                Request[4] = (byte)HostBytes.Length;
                Buffer.BlockCopy(HostBytes, 0, Request, 5, HostBytes.Length);
                Request[5 + HostBytes.Length] = (byte)(Port >> 8);
                Request[6 + HostBytes.Length] = (byte)(Port & 0xFF);

                await Stream.WriteAsync(Request, 0, Request.Length, Token).ConfigureAwait(false);

                byte[] Reply = await ReadExactly(Stream, 4, Token).ConfigureAwait(false);

                if (Reply[1] != 0x00) throw new IOException($"SOCKS5 connect refused (code {Reply[1]})");

                // Drain the bound address so the tunnel starts at the first payload byte.
                switch (Reply[3])
                {
                    case 0x01: await ReadExactly(Stream, 4 + 2, Token).ConfigureAwait(false); break;
                    case 0x04: await ReadExactly(Stream, 16 + 2, Token).ConfigureAwait(false); break;
                    case 0x03:
                        byte[] Length = await ReadExactly(Stream, 1, Token).ConfigureAwait(false);
                        await ReadExactly(Stream, Length[0] + 2, Token).ConfigureAwait(false);
                        break;
                    default: throw new IOException("bad SOCKS5 reply address type");
                }
            }
            else // socks4 / socks4a — hostname form, so DNS still resolves at the exit
            {
                byte[] User = Encoding.ASCII.GetBytes(Upstream.Username ?? string.Empty);
                byte[] HostBytes = Encoding.ASCII.GetBytes(Host);

                byte[] Request = new byte[9 + User.Length + HostBytes.Length + 1];
                int Index = 0;

                Request[Index++] = 0x04;
                Request[Index++] = 0x01;
                Request[Index++] = (byte)(Port >> 8);
                Request[Index++] = (byte)(Port & 0xFF);
                Request[Index++] = 0x00; Request[Index++] = 0x00; Request[Index++] = 0x00; Request[Index++] = 0x01; // 0.0.0.x => socks4a
                Buffer.BlockCopy(User, 0, Request, Index, User.Length); Index += User.Length;
                Request[Index++] = 0x00;
                Buffer.BlockCopy(HostBytes, 0, Request, Index, HostBytes.Length); Index += HostBytes.Length;
                Request[Index++] = 0x00;

                await Stream.WriteAsync(Request, 0, Index, Token).ConfigureAwait(false);

                byte[] Reply = await ReadExactly(Stream, 8, Token).ConfigureAwait(false);

                if (Reply[1] != 0x5A) throw new IOException($"SOCKS4 connect refused (code 0x{Reply[1]:X2})");
            }
        }

        private static async Task<byte[]> ReadExactly(NetworkStream Stream, int Count, CancellationToken Token)
        {
            byte[] Buffer = new byte[Count];
            int Read = 0;

            while (Read < Count)
            {
                int Got = await Stream.ReadAsync(Buffer, Read, Count - Read, Token).ConfigureAwait(false);

                if (Got <= 0) throw new IOException("connection closed mid-handshake");

                Read += Got;
            }

            return Buffer;
        }

        private static async Task WriteStatus(NetworkStream Stream, string Status, CancellationToken Token)
        {
            try
            {
                byte[] Bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {Status}\r\nProxy-Agent: SkrilyaAccountManager\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

                await Stream.WriteAsync(Bytes, 0, Bytes.Length, Token).ConfigureAwait(false);
            }
            catch { }
        }

        private static async Task Pump(NetworkStream A, NetworkStream B, CancellationToken Token)
        {
            Task ToRemote = Copy(A, B, Token);
            Task ToClient = Copy(B, A, Token);

            // One direction closing ends the tunnel; waiting for both would hold sockets open on half-closes.
            await Task.WhenAny(ToRemote, ToClient).ConfigureAwait(false);
        }

        private static async Task Copy(NetworkStream From, NetworkStream To, CancellationToken Token)
        {
            byte[] Buffer = new byte[16 * 1024];

            try
            {
                while (!Token.IsCancellationRequested)
                {
                    int Read = await From.ReadAsync(Buffer, 0, Buffer.Length, Token).ConfigureAwait(false);

                    if (Read <= 0) break;

                    await To.WriteAsync(Buffer, 0, Read, Token).ConfigureAwait(false);
                    await To.FlushAsync(Token).ConfigureAwait(false);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (!Running && Listener == null) return;

            Running = false;

            try { Cancellation?.Cancel(); } catch { }
            try { Listener?.Stop(); } catch { }
            try { Cancellation?.Dispose(); } catch { }

            Listener = null;
            Cancellation = null;

            Program.Logger.Info($"[SlotProxy {Label}] stopped (tunnelled: {TunnelledCount}, direct: {DirectCount}, failed: {FailedCount})");
        }
    }

    /// <summary>Request/response head reader that keeps whatever it over-read, so the body can be forwarded intact.</summary>
    internal class HttpHead
    {
        public string StartLine = string.Empty;
        public List<string> Headers = new List<string>();
        public byte[] Leftover = Array.Empty<byte>();

        public string Method => StartLine.Split(' ').FirstOrDefault() ?? string.Empty;

        private const int MaxHeadSize = 32 * 1024;

        public static async Task<HttpHead> ReadAsync(NetworkStream Stream, CancellationToken Token)
        {
            byte[] Buffer = new byte[MaxHeadSize];
            int Length = 0;
            int Terminator = -1;

            while (Length < Buffer.Length)
            {
                int Read = await Stream.ReadAsync(Buffer, Length, Buffer.Length - Length, Token).ConfigureAwait(false);

                if (Read <= 0) return null;

                Length += Read;

                for (int i = 3; i < Length; i++)
                    if (Buffer[i] == '\n' && Buffer[i - 1] == '\r' && Buffer[i - 2] == '\n' && Buffer[i - 3] == '\r')
                    {
                        Terminator = i;
                        break;
                    }

                if (Terminator >= 0) break;
            }

            if (Terminator < 0) return null;

            string Head = Encoding.ASCII.GetString(Buffer, 0, Terminator + 1);
            string[] Lines = Head.Split(new[] { "\r\n" }, StringSplitOptions.None);

            HttpHead Result = new HttpHead { StartLine = Lines.Length > 0 ? Lines[0] : string.Empty };

            for (int i = 1; i < Lines.Length; i++)
                if (!string.IsNullOrEmpty(Lines[i]))
                    Result.Headers.Add(Lines[i]);

            int Remaining = Length - (Terminator + 1);

            if (Remaining > 0)
            {
                Result.Leftover = new byte[Remaining];
                Array.Copy(Buffer, Terminator + 1, Result.Leftover, 0, Remaining);
            }

            return Result;
        }

        public string GetHeader(string Name)
        {
            foreach (string Header in Headers)
            {
                int Split = Header.IndexOf(':');

                if (Split > 0 && Header.Substring(0, Split).Trim().Equals(Name, StringComparison.OrdinalIgnoreCase))
                    return Header.Substring(Split + 1).Trim();
            }

            return null;
        }

        /// <summary>Resolves the destination from a CONNECT authority, an absolute request URI, or the Host header.</summary>
        public bool TryGetTarget(out string Host, out int Port)
        {
            Host = null;
            Port = 0;

            string[] Parts = StartLine.Split(' ');

            if (Parts.Length < 2) return false;

            bool IsConnect = Parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
            string Target = Parts[1];

            if (IsConnect)
            {
                int Split = Target.LastIndexOf(':');

                if (Split <= 0 || !int.TryParse(Target.Substring(Split + 1), out Port)) return false;

                Host = Target.Substring(0, Split);

                return Host.Length > 0 && Port > 0;
            }

            if (Uri.TryCreate(Target, UriKind.Absolute, out Uri Absolute))
            {
                Host = Absolute.Host;
                Port = Absolute.Port;

                return true;
            }

            string HostHeader = GetHeader("Host");

            if (string.IsNullOrEmpty(HostHeader)) return false;

            int HeaderSplit = HostHeader.LastIndexOf(':');

            if (HeaderSplit > 0 && int.TryParse(HostHeader.Substring(HeaderSplit + 1), out Port))
                Host = HostHeader.Substring(0, HeaderSplit);
            else
            {
                Host = HostHeader;
                Port = 80;
            }

            return Host.Length > 0;
        }

        /// <summary>Rebuilds the head for forwarding, optionally converting an absolute request URI to origin form.</summary>
        public string BuildForwardHead(bool OriginForm, string ProxyAuthorization = null)
        {
            string Line = StartLine;

            if (OriginForm)
            {
                string[] Parts = StartLine.Split(' ');

                if (Parts.Length >= 3 && Uri.TryCreate(Parts[1], UriKind.Absolute, out Uri Absolute))
                    Line = $"{Parts[0]} {Absolute.PathAndQuery} {Parts[2]}";
            }

            StringBuilder Builder = new StringBuilder();

            Builder.Append(Line).Append("\r\n");

            foreach (string Header in Headers)
            {
                // Hop-by-hop headers meant for us, not for the origin server.
                if (Header.StartsWith("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (Header.StartsWith("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;

                Builder.Append(Header).Append("\r\n");
            }

            if (!string.IsNullOrEmpty(ProxyAuthorization))
                Builder.Append("Proxy-Authorization: ").Append(ProxyAuthorization).Append("\r\n");

            Builder.Append("\r\n");

            return Builder.ToString();
        }
    }
}

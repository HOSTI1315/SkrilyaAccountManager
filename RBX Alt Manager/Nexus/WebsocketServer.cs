using RBX_Alt_Manager.Forms;
using System;
using System.Linq;
using System.Threading;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace RBX_Alt_Manager.Nexus
{
    public class WebsocketServer : WebSocketBehavior
    {
        private static int _num = 0;

        private string _name;
        private string _prefix;

        public WebsocketServer() : this("anon#")
        {
        }

        public WebsocketServer(string prefix) => _prefix = prefix;

        private string getName() => Context.QueryString["name"] ?? (_prefix + getNum());
        private int getNum() => Interlocked.Increment(ref _num);

        /// <summary>Constant-time compare so the shared secret can't be probed byte by byte over the network.</summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            byte[] x = System.Text.Encoding.UTF8.GetBytes(a ?? "");
            byte[] y = System.Text.Encoding.UTF8.GetBytes(b ?? "");

            int Diff = x.Length ^ y.Length;

            for (int i = 0; i < y.Length; i++) Diff |= (i < x.Length ? x[i] : (byte)0) ^ y[i];

            return Diff == 0;
        }

        protected override void OnOpen()
        {
            // The in-game Lua client sends no Origin; a browser page trying to drive this control socket does.
            if (!string.IsNullOrEmpty(Context.Origin)) { Context.WebSocket.Close(); return; }

            // A non-loopback client (only reachable when AllowExternalConnections binds every interface) must
            // present the per-session shared secret. Before this, the ONLY barrier was knowing a username —
            // which is public information, so any LAN/internet peer could drive the control channel.
            if (!Context.IsLocal)
            {
                string Provided = Context.QueryString["token"] ?? "";

                if (string.IsNullOrEmpty(AccountControl.NexusToken) || !FixedTimeEquals(Provided, AccountControl.NexusToken)) { Context.WebSocket.Close(); return; }
            }

            if (string.IsNullOrEmpty(Context.QueryString["name"]) || string.IsNullOrEmpty(Context.QueryString["id"])) { Context.WebSocket.Close(); return; }

            string jobID = string.IsNullOrEmpty(Context.QueryString["jobId"]) ? "UNKNOWN" : Context.QueryString["jobId"];

            long.TryParse(Context.QueryString["id"], out long UserId);

            _name = getName();

            // OnOpen runs on a websocket-sharp thread. Accounts is mutated on the UI thread, so enumerate it
            // there to avoid "Collection was modified", and marshal the ObjectListView refresh too.
            ControlledAccount Account = null;

            AccountControl.Instance.InvokeIfRequired(() =>
                Account = AccountControl.Instance.Accounts.FirstOrDefault(x => x.Username == Context.QueryString["name"]));

            if (Account != null)
            {
                Account.Connect(Context);
                Account.InGameJobId = jobID;
                AccountControl.Instance.InvokeIfRequired(() => AccountControl.Instance.AccountsView.RefreshObject(Account));
            }
            else
                Context.WebSocket.Close();
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (AccountControl.Instance.ContextList.TryGetValue(Context, out ControlledAccount account))
                account.HandleMessage(e.Data);
        }

        protected override void OnClose(CloseEventArgs e)
        {
            if (AccountControl.Instance.ContextList.TryGetValue(Context, out ControlledAccount Account))
                // Only flip the account Offline if this socket is still its current one. A late OnClose from a
                // superseded socket (duplicate-name reconnect / teleport) must not disconnect an account that
                // has already reconnected on a newer socket — that left it stuck Offline with a live client.
                Account.CloseIfCurrent(Context);
        }

        protected override void OnError(ErrorEventArgs e) => Program.Logger.Error($"WebsocketServer Error {_name}: {e.Message} {e.Exception}");
    }
}
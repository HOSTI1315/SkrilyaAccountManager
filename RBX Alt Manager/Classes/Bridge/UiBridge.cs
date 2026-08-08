using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes.Bridge
{
    /// <summary>
    /// The only channel between the HTML interface and the manager.
    ///
    /// Shape of a call, page -> app:      { "id": 7, "method": "accounts.list", "params": { ... } }
    /// Shape of the answer, app -> page:  { "id": 7, "ok": true, "result": ... }  |  { "id": 7, "ok": false, "error": "..." }
    /// Unsolicited pushes, app -> page:   { "event": "accounts.changed", "data": ... }
    ///
    /// Why message passing and not AddHostObjectToScript: host objects expose real C# objects to the page and
    /// need COM visibility on everything they touch, which turns every model change into a marshalling problem
    /// and hands the page more reach than it should have. A method table is a flat, auditable surface — the page
    /// can call exactly what is registered here and nothing else.
    /// </summary>
    public class UiBridge
    {
        private readonly Dictionary<string, Func<JObject, Task<object>>> Handlers = new Dictionary<string, Func<JObject, Task<object>>>(StringComparer.OrdinalIgnoreCase);

        private CoreWebView2 Core;
        private Control Owner;

        /// <summary>
        /// Serialization has to match what the page expects: camelCase names, no type metadata.
        ///
        /// ProcessDictionaryKeys is off deliberately. Newtonsoft's CamelCasePropertyNamesContractResolver renames
        /// dictionary KEYS as well as property names by default, and several methods return a dictionary whose keys
        /// are data rather than field names — settings.describe and settings.get are keyed by ini key. Left on, it
        /// silently turned "DirectLaunch" into "directLaunch", every lookup in the page missed, and the settings
        /// screen showed a store full of values as if every one of them were unset.
        /// </summary>
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy
                {
                    ProcessDictionaryKeys = false,
                    OverrideSpecifiedNames = true
                }
            },
            NullValueHandling = NullValueHandling.Include,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        public void Register(string Method, Func<JObject, Task<object>> Handler) => Handlers[Method] = Handler;

        /// <summary>Convenience for handlers with nothing to await.</summary>
        public void Register(string Method, Func<JObject, object> Handler) => Handlers[Method] = Parameters => Task.FromResult(Handler(Parameters));

        public IEnumerable<string> Methods => Handlers.Keys;

        public void Attach(CoreWebView2 core, Control owner)
        {
            Core = core;
            Owner = owner;

            Core.WebMessageReceived += OnMessage;
        }

        public void Detach()
        {
            if (Core != null) Core.WebMessageReceived -= OnMessage;

            Core = null;
        }

        private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject Message;

            try { Message = JObject.Parse(e.WebMessageAsJson); }
            catch (Exception x)
            {
                Program.Logger.Warn($"[Bridge] unparseable message: {x.Message}");

                return;
            }

            int Id = Message.Value<int?>("id") ?? 0;
            string Method = Message.Value<string>("method") ?? string.Empty;
            JObject Parameters = Message["params"] as JObject ?? new JObject();

            if (!Handlers.TryGetValue(Method, out Func<JObject, Task<object>> Handler))
            {
                Reply(Id, false, null, $"Unknown method \"{Method}\"");

                return;
            }

            try
            {
                object Result = await Handler(Parameters).ConfigureAwait(true); // back on the UI thread: WebView2 requires it

                Reply(Id, true, Result, null);
            }
            catch (Exception x)
            {
                // The page gets the message, the log gets the stack trace. A failed call must never take the
                // manager down — the UI is a guest here.
                Program.Logger.Error($"[Bridge] {Method} failed: {x}");

                Reply(Id, false, null, x.Message);
            }
        }

        private void Reply(int Id, bool Ok, object Result, string Error)
        {
            Post(new { id = Id, ok = Ok, result = Result, error = Error });
        }

        /// <summary>Pushes an event to the page. Safe to call from any thread; ignored when no page is loaded.</summary>
        public void Emit(string Event, object Data) => Post(new { @event = Event, data = Data });

        private void Post(object Payload)
        {
            if (Core == null || Owner == null || Owner.IsDisposed) return;

            string Text;

            try { Text = JsonConvert.SerializeObject(Payload, Json); }
            catch (Exception x)
            {
                Program.Logger.Error($"[Bridge] could not serialize a message: {x.Message}");

                return;
            }

            Utilities.InvokeIfRequired(Owner, () =>
            {
                // The page can navigate or close between the check and the call.
                try { Core?.PostWebMessageAsJson(Text); }
                catch (Exception x) { Program.Logger.Debug($"[Bridge] post failed: {x.Message}"); }
            });
        }
    }
}

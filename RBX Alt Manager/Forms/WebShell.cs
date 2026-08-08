using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Classes.Bridge;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Forms
{
    /// <summary>
    /// The window that hosts the HTML interface.
    ///
    /// Everything visible lives in the <c>ui</c> folder next to the executable and is served through a virtual
    /// host (<c>https://ram.local/</c>) rather than file:// — the same-origin rules browsers apply to file URLs
    /// break fetch, modules and anything else the design might use, and a designer should not have to know that.
    /// Dropping in a new index.html/styles.css is enough; nothing here needs rebuilding.
    /// </summary>
    public class WebShell : Form
    {
        public const string VirtualHost = "ram.local";

        private readonly WebView2 View = new WebView2 { Dock = DockStyle.Fill };
        private readonly UiBridge Bridge = new UiBridge();
        private readonly Label Status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(7, 9, 13),
            Font = new Font("Segoe UI", 10f),
            Visible = false
        };

        public static WebShell Instance { get; private set; }

        public WebShell()
        {
            Text = "SkrilyaAccountManager";
            ClientSize = new Size(1320, 840); // the size the design is laid out for
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(7, 9, 13);
            MinimumSize = new Size(900, 600);

            // No native frame: a light Windows title bar above a dark interface looks like two applications
            // stacked on top of each other. The page draws its own title bar; resizing is put back by
            // WndProc below, and dragging comes from the page's CSS drag regions.
            FormBorderStyle = FormBorderStyle.None;

            Controls.Add(View);
            Controls.Add(Status);

            Instance = this;

            _ = Initialize();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Windows 11 rounds framed windows for free but leaves borderless ones square; ask explicitly.
            try
            {
                int Round = DWMWCP_ROUND;

                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref Round, sizeof(int));
            }
            catch { }
        }

        #region borderless resizing

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        /// <summary>How far from an edge still counts as "grab to resize".</summary>
        private const int Grip = 6;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // Only the resize edges are handled here. Dragging and the caption buttons belong to the page —
            // WebView2's non-client region support turns its CSS drag regions into a real caption.
            if (m.Msg != WM_NCHITTEST || (int)m.Result != HTCLIENT || WindowState == FormWindowState.Maximized) return;

            Point Cursor = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));

            bool Left = Cursor.X <= Grip, Right = Cursor.X >= ClientSize.Width - Grip;
            bool Top = Cursor.Y <= Grip, Bottom = Cursor.Y >= ClientSize.Height - Grip;

            int Hit =
                Top && Left ? HTTOPLEFT :
                Top && Right ? HTTOPRIGHT :
                Bottom && Left ? HTBOTTOMLEFT :
                Bottom && Right ? HTBOTTOMRIGHT :
                Left ? HTLEFT : Right ? HTRIGHT : Top ? HTTOP : Bottom ? HTBOTTOM : HTCLIENT;

            if (Hit != HTCLIENT) m.Result = (IntPtr)Hit;
        }

        #endregion

        /// <summary>
        /// Where the interface's files live, in the order they are looked for:
        ///
        ///   1. the UiFolder setting, so a designer can point at their working copy;
        ///   2. an "ui" folder beside the executable, which is what a normal build produces;
        ///   3. the copy embedded in the assembly, unpacked once — this is the published single file's case.
        ///
        /// The order matters: a loose folder must keep beating the embedded copy, or editing index.html next to
        /// the exe would silently do nothing.
        /// </summary>
        public static string UiFolder
        {
            get
            {
                string Configured = AccountManager.General.Exists("UiFolder") ? AccountManager.General.Get("UiFolder") : null;

                if (!string.IsNullOrWhiteSpace(Configured) && Directory.Exists(Configured)) return Configured;

                string Beside = Path.Combine(AppContext.BaseDirectory, "ui");

                if (File.Exists(Path.Combine(Beside, "index.html"))) return Beside;

                return UnpackedUiFolder ?? Beside;
            }
        }

        private static string UnpackedUi;

        /// <summary>
        /// Writes the embedded interface out once per build and returns where.
        ///
        /// The folder is named after the module version id rather than the assembly version: AssemblyInfo.cs is
        /// kept by hand here, so two different builds routinely carry the same version number and a newer
        /// executable would go on serving the previous build's interface. The MVID changes whenever the compiled
        /// output does, embedded resources included. Older folders are swept so this cannot grow forever.
        /// </summary>
        private static string UnpackedUiFolder
        {
            get
            {
                if (UnpackedUi != null) return UnpackedUi;

                try
                {
                    System.Reflection.Assembly Self = typeof(WebShell).Assembly;

                    string[] Names = Array.FindAll(Self.GetManifestResourceNames(), Name => Name.StartsWith("ui/", StringComparison.Ordinal));

                    if (Names.Length == 0) return null;

                    string Home = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkrilyaAccountManager", "ui");

                    string Build = Self.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);
                    string Root = Path.Combine(Home, Build);

                    // Another copy of the app may be running out of one of these; whatever is in use simply stays.
                    if (Directory.Exists(Home))
                        foreach (string Old in Directory.GetDirectories(Home))
                            if (!string.Equals(Path.GetFileName(Old), Build, StringComparison.OrdinalIgnoreCase))
                                try { Directory.Delete(Old, true); } catch { }

                    // The marker is written last and is what "already unpacked" means: a run killed halfway
                    // through leaves files but no marker, so the next one writes them again instead of
                    // serving a folder that is missing half the interface.
                    string Marker = Path.Combine(Root, ".unpacked");

                    if (!File.Exists(Marker))
                    {
                        foreach (string Name in Names)
                        {
                            // "ui/icons\Phosphor.woff2" -> icons\Phosphor.woff2
                            string Relative = Name.Substring(3).Replace('/', Path.DirectorySeparatorChar);
                            string Target = Path.Combine(Root, Relative);

                            Directory.CreateDirectory(Path.GetDirectoryName(Target));

                            using (Stream From = Self.GetManifestResourceStream(Name))
                            using (FileStream To = File.Create(Target))
                                From.CopyTo(To);
                        }

                        File.WriteAllText(Marker, Names.Length.ToString());
                    }

                    UnpackedUi = Root;
                }
                catch (Exception x)
                {
                    Program.Logger.Error($"[WebShell] could not unpack the interface: {x.Message}");

                    return null;
                }

                return UnpackedUi;
            }
        }

        private async Task Initialize()
        {
            try
            {
                if (!Directory.Exists(UiFolder) || !File.Exists(Path.Combine(UiFolder, "index.html")))
                {
                    Fail($"The interface files are missing.\n\nExpected index.html in:\n{UiFolder}");

                    return;
                }

                // Keeping the browser profile beside the app matters for a portable install: the default location
                // is under %LOCALAPPDATA%, which a copied folder would silently share with another copy.
                string Profile = Path.Combine(AppContext.BaseDirectory, "WebView2");

                Directory.CreateDirectory(Profile);

                CoreWebView2Environment Environment = await CoreWebView2Environment.CreateAsync(null, Profile);

                await View.EnsureCoreWebView2Async(Environment);

                CoreWebView2 Core = View.CoreWebView2;

                Core.SetVirtualHostNameToFolderMapping(VirtualHost, UiFolder, CoreWebView2HostResourceAccessKind.Allow);

                Core.Settings.AreDefaultContextMenusEnabled = AccountManager.Developer.Get<bool>("DevMode");
                Core.Settings.AreDevToolsEnabled = AccountManager.Developer.Get<bool>("DevMode");
                Core.Settings.IsStatusBarEnabled = false;
                Core.Settings.IsSwipeNavigationEnabled = false;

                // A link in the interface must not open a second, chrome-less app window: send it to the user's
                // browser instead, which is what "open this page" means to them.
                Core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;

                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                };

                // The interface is local. Anything trying to navigate the shell elsewhere is either a mistake or
                // something worse; either way it does not get to replace the app's UI.
                Core.NavigationStarting += (s, e) =>
                {
                    if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri Target) || Target.Host != VirtualHost)
                    {
                        e.Cancel = true;

                        Program.Logger.Warn($"[WebShell] blocked navigation to {e.Uri}");
                    }
                };

                // Lets the page mark its own title bar with CSS `app-region: drag`, so dragging and
                // double-click-to-maximize behave natively without the window owning a caption.
                try { Core.Settings.IsNonClientRegionSupportEnabled = true; }
                catch (NotImplementedException) { Program.Logger.Warn("[WebShell] this WebView2 runtime has no non-client region support; the title bar will not drag"); }

                BridgeApi.RegisterAll(Bridge);
                RegisterWindowMethods();
                Bridge.Attach(Core, this);

                SubscribeToAppEvents();

                View.Source = new Uri($"https://{VirtualHost}/index.html");

                Program.Logger.Info($"[WebShell] serving {UiFolder} at https://{VirtualHost}/ (runtime {CoreWebView2Environment.GetAvailableBrowserVersionString()})");
            }
            catch (WebView2RuntimeNotFoundException)
            {
                Fail("The WebView2 runtime is not installed.\n\nIt ships with Windows 11 and current Windows 10;\ninstall it from Microsoft's Evergreen page and reopen this window.");
            }
            catch (Exception x)
            {
                Program.Logger.Error($"[WebShell] failed to start: {x}");

                Fail($"The interface failed to start.\n\n{x.Message}");
            }
        }

        private void Fail(string Message)
        {
            View.Visible = false;
            Status.Text = Message;
            Status.Visible = true;

            // The shell is how a NewUI user answers the security question. If it could not start and the store is
            // still locked, the old window's panels are the only way in — but they were taken down when the HTML
            // interface took ownership. Hand them back, or the user is left with a grey shell and a list they can
            // never unlock. Initialize() swallows its own failures, so this is the only place that can do it.
            try
            {
                AccountManager Window = AccountManager.Instance;

                if (Window != null && Window.CurrentSecurityStep != SecurityStep.Ready)
                {
                    Window.BeginInvoke((Action)(() =>
                    {
                        Window.RestoreSecurityPanels();
                        Window.BringToFront();
                        Window.Activate();
                    }));
                }
            }
            catch (Exception x) { Program.Logger.Warn($"[WebShell] could not restore the old unlock panel: {x.Message}"); }
        }

        /// <summary>
        /// The window buttons the page draws. They live here rather than in BridgeApi because they act on this
        /// window, not on the application — a second shell would control itself, not this one.
        /// </summary>
        private void RegisterWindowMethods()
        {
            Bridge.Register("window.minimize", _ => { WindowState = FormWindowState.Minimized; return true; });

            Bridge.Register("window.maximize", _ =>
            {
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

                return WindowState == FormWindowState.Maximized;
            });

            Bridge.Register("window.close", _ => { Close(); return true; });

            Bridge.Register("window.state", _ => new
            {
                maximized = WindowState == FormWindowState.Maximized,
                minimized = WindowState == FormWindowState.Minimized
            });
        }

        /// <summary>Pushes to the page the things it cannot know by asking: results that arrive later.</summary>
        private void SubscribeToAppEvents()
        {
            ClientLogWatcher.LaunchResolved += (account, Outcome, Detail) =>
                Bridge.Emit("launch.result", new
                {
                    username = account?.Username,
                    outcome = Outcome.ToString(),
                    detail = Detail
                });
        }

        /// <summary>Tells the page that account data changed underneath it.</summary>
        public static void NotifyAccountsChanged() => Instance?.Bridge?.Emit("accounts.changed", null);

        /// <summary>Tells the page that the store was locked, unlocked or re-protected.</summary>
        public static void NotifySecurityChanged() => Instance?.Bridge?.Emit("security.changed", null);

        /// <summary>Progress and the final tally of a free-item collection: "progress" then "done".</summary>
        public static void NotifyFreeItems(string What, object Data) => Instance?.Bridge?.Emit("freeitems." + What, Data);

        /// <summary>How a paced batch launch is going: "progress" per account, "batch" when it ends.</summary>
        public static void NotifyLaunch(string What, object Data) => Instance?.Bridge?.Emit("launch." + What, Data);

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Bridge.Detach();

            try { View.Dispose(); } catch { }

            Instance = null;

            base.OnFormClosed(e);
        }

        public static void ShowShell()
        {
            if (Instance != null && !Instance.IsDisposed)
            {
                Instance.WindowState = FormWindowState.Normal;
                Instance.BringToFront();
                Instance.Activate();

                return;
            }

            new WebShell().Show();
        }
    }
}

using RBX_Alt_Manager.Classes;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Forms
{
    /// <summary>
    /// The "Clients" settings tab: how the Roblox client is launched, how its window is labelled, and the
    /// per-account proxy switches.
    ///
    /// Built in code rather than in SettingsForm.Designer.cs deliberately. That file's controls are paired with
    /// hand-maintained resource names (see the EmbeddedResource block in the csproj), and this tab is a plain
    /// list of switches with no designer state worth keeping. It is constructed before Rescale() runs, so it
    /// scales with the rest of the form, and ApplyTheme walks into TabPages, so it themes with the rest of it.
    /// </summary>
    public partial class SettingsForm
    {
        private readonly ToolTip Tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 200 };

        private CheckBox DirectLaunchCB;
        private CheckBox WatchLaunchCB;
        private CheckBox CloseDialogsCB;
        private CheckBox RenameWindowsCB;
        private TextBox WindowTitleTB;
        private CheckBox SeparateDesktopCB;
        private TextBox DesktopNameTB;
        private CheckBox UseProxiesCB;
        private CheckBox SplitTunnelCB;
        private CheckBox VerifyExitIPCB;
        private TextBox IdentityHostsTB;
        private Button AssignProxiesButton;
        private CheckBox NewUiCB;
        private Button OpenWebUiButton;

        private const int RowWidth = 265;

        private void BuildClientsTab()
        {
            TabPage Tab = new TabPage("Clients") { Padding = new Padding(3), UseVisualStyleBackColor = true };

            FlowLayoutPanel Panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12)
            };

            Panel.Controls.Add(Section("Launch"));

            DirectLaunchCB = Check("Launch clients directly", "DirectLaunch", true,
                "Start RobloxPlayerBeta.exe ourselves instead of handing the launch URI to Windows.\r\n\r\n" +
                "Required for per-account proxies, window titles and launch tracking — those need the client's own environment and process id. " +
                "The resulting command line is identical to the one the protocol handler would have produced.");

            WatchLaunchCB = Check("Report whether the launch actually joined", "WatchLaunchResult", true,
                "Reads the client's own log after launching and records Joined / Disconnected / died-early (usually a rejected authentication ticket) in the manager's log.");

            CloseDialogsCB = Check("Close Roblox error dialogs automatically", "CloseRobloxErrorDialogs", false,
                "Closes the Win32 message boxes Roblox leaves behind when a launch fails, so they don't stack up during a mass launch.");

            Panel.Controls.Add(DirectLaunchCB);
            Panel.Controls.Add(WatchLaunchCB);
            Panel.Controls.Add(CloseDialogsCB);

            Panel.Controls.Add(Section("Client windows"));

            RenameWindowsCB = Check("Name each window after its account", "RenameClientWindows", false,
                "Renames the client's window so you can tell fifteen clients apart. Cosmetic — Roblox is not told anything.");

            WindowTitleTB = TextSetting("ClientWindowTitle", "{name} - Roblox",
                "Title template. Placeholders: {name} {username} {alias} {userid} {place} {pid}");

            SeparateDesktopCB = Check("Run clients on a separate desktop", "UseSeparateDesktop", false,
                "Clients draw on their own Windows desktop: their windows are not on screen at all and are not throttled for being inactive.\r\n\r\n" +
                "Trade-off: window positions, minimising and window titles cannot be seen or applied there.");

            DesktopNameTB = TextSetting("ClientDesktopName", "RAMClients", "Name of the desktop used for clients.");

            Panel.Controls.Add(RenameWindowsCB);
            Panel.Controls.Add(Row("Title", WindowTitleTB));
            Panel.Controls.Add(Hint("{name} {username} {alias} {userid} {place} {pid}"));
            Panel.Controls.Add(SeparateDesktopCB);
            Panel.Controls.Add(Row("Desktop", DesktopNameTB));

            Panel.Controls.Add(Section("Per-account proxies"));

            UseProxiesCB = Check("Use each account's own proxy", "UseAccountProxies", false,
                "The account's login AND its client go through the proxy stored in that account's Proxy field.\r\n\r\n" +
                "Roblox rate-limits logins per IP and binds each authentication ticket to the IP that requested it, so both sides have to use the same exit.");

            SplitTunnelCB = Check("Only route login traffic (recommended)", "ProxySplitTunnel", true,
                "Sends login and join traffic through the proxy and fetches game assets directly.\r\n\r\n" +
                "The exit IP Roblox sees for the account is still the proxy's, but tens of megabytes of assets per join don't crawl through it.");

            VerifyExitIPCB = Check("Check the proxy before using it", "ProxyVerifyExitIP", true,
                "Resolves the proxy's exit IP before spending an authentication ticket on it. Catches dead proxies early and warns when a proxy rotates its IP, which makes Roblox reject the ticket with 403.");

            IdentityHostsTB = TextSetting("ProxyIdentityHosts",
                "auth.roblox.com,gamejoin.roblox.com,assetgame.roblox.com,apis.roblox.com,users.roblox.com,www.roblox.com,web.roblox.com,presence.roblox.com",
                "Comma-separated hosts that must go through the proxy when only login traffic is routed. Subdomains are included automatically.");

            AssignProxiesButton = new Button { Text = "Assign proxies to selection…", Width = RowWidth, Height = 26, AutoSize = false };
            AssignProxiesButton.Click += (s, e) => AccountManager.Instance?.ShowProxyAssignment();

            Tips.SetToolTip(AssignProxiesButton, "Hands a list of proxies to the accounts selected in the main list, in order, wrapping when there are more accounts than proxies.");

            Panel.Controls.Add(UseProxiesCB);
            Panel.Controls.Add(SplitTunnelCB);
            Panel.Controls.Add(VerifyExitIPCB);
            Panel.Controls.Add(Row("Hosts", IdentityHostsTB));
            Panel.Controls.Add(AssignProxiesButton);

            Panel.Controls.Add(Section("Interface (preview)"));

            NewUiCB = Check("Open the new interface on startup", "NewUI", false,
                "Opens the HTML interface when the app starts. It runs alongside this window rather than replacing it, so nothing is lost while it is still being built.");

            OpenWebUiButton = new Button { Text = "Open the new interface", Width = RowWidth, Height = 26, AutoSize = false };
            OpenWebUiButton.Click += (s, e) => Forms.WebShell.ShowShell();

            Tips.SetToolTip(OpenWebUiButton, "The interface is served from the 'ui' folder next to the executable. Point [General] UiFolder at another path to work against a design in progress.");

            Panel.Controls.Add(NewUiCB);
            Panel.Controls.Add(OpenWebUiButton);

            Tab.Controls.Add(Panel);
            SettingsTC.TabPages.Add(Tab);

            RefreshClientsTab();
        }

        /// <summary>Greys out the settings that only mean something while their parent switch is on.</summary>
        private void RefreshClientsTab()
        {
            WindowTitleTB.Enabled = RenameWindowsCB.Checked;
            DesktopNameTB.Enabled = SeparateDesktopCB.Checked;

            SplitTunnelCB.Enabled = UseProxiesCB.Checked;
            VerifyExitIPCB.Enabled = UseProxiesCB.Checked;
            IdentityHostsTB.Enabled = UseProxiesCB.Checked && SplitTunnelCB.Checked;
        }

        private Label Section(string Title) => new Label
        {
            Text = Title,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 10, 0, 4)
        };

        private Label Hint(string Caption) => new Label
        {
            Text = Caption,
            AutoSize = false,
            Width = RowWidth,
            Height = 15,
            Margin = new Padding(3, 0, 3, 6),
            Font = new Font(Font.FontFamily, Font.Size - 1f)
        };

        /// <summary>A label + control on one line, so the narrow settings window stays readable.</summary>
        private Panel Row(string Caption, Control Control)
        {
            Panel Holder = new Panel { Width = RowWidth, Height = 24, Margin = new Padding(3, 2, 3, 2) };

            Label Prefix = new Label { Text = Caption, AutoSize = false, Width = 52, Height = 18, Location = new Point(0, 3) };

            Control.Location = new Point(56, 0);
            Control.Width = RowWidth - 56;

            Holder.Controls.Add(Prefix);
            Holder.Controls.Add(Control);

            return Holder;
        }

        private CheckBox Check(string Caption, string Setting, bool Default, string Tooltip)
        {
            CheckBox Box = new CheckBox
            {
                Text = Caption,
                AutoSize = false,
                Width = RowWidth,
                Height = 20,
                Checked = ReadBool(Setting, Default)
            };

            Tips.SetToolTip(Box, Tooltip);

            Box.CheckedChanged += (s, e) =>
            {
                if (SettingsLoaded) Save(Setting, Box.Checked ? "true" : "false");

                RefreshClientsTab();
            };

            return Box;
        }

        private TextBox TextSetting(string Setting, string Default, string Tooltip)
        {
            TextBox Box = new TextBox { Text = ReadText(Setting, Default) };

            Tips.SetToolTip(Box, Tooltip);

            // Written on leave rather than on every keystroke: each write saves and re-reads the whole ini.
            Box.Leave += (s, e) =>
            {
                if (!SettingsLoaded) return;

                // An empty value would delete the key (IniFile.Set removes blanks), and the setting would
                // silently revert to its default on the next read — so put the default back visibly instead.
                if (string.IsNullOrWhiteSpace(Box.Text)) Box.Text = Default;

                Save(Setting, Box.Text.Trim());
            };

            return Box;
        }

        private static bool ReadBool(string Name, bool Default)
        {
            try
            {
                if (!AccountManager.General.Exists(Name)) return Default;

                return bool.TryParse(AccountManager.General.Get(Name), out bool Value) ? Value : Default;
            }
            catch { return Default; }
        }

        private static string ReadText(string Name, string Default)
        {
            try { return AccountManager.General.Exists(Name) ? (AccountManager.General.Get(Name) ?? Default) : Default; }
            catch { return Default; }
        }

        private static void Save(string Name, string Value)
        {
            AccountManager.General.Set(Name, Value);
            AccountManager.IniSettings.Save("RAMSettings.ini");

            // Applies immediately — these settings are read into static state, and a restart to pick up a
            // checkbox would be a poor trade.
            AccountProxies.LoadSettings();
        }
    }
}

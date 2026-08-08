using RBX_Alt_Manager.Classes;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Forms
{
    /// <summary>
    /// Assigns proxies to accounts, one line per proxy, handed out in selection order and wrapping when there are
    /// more accounts than proxies.
    ///
    /// Built in code rather than in the designer on purpose: the designer files here carry hand-maintained
    /// resource names (see the csproj comment on EmbeddedResource), and a form with four controls does not justify
    /// touching that.
    /// </summary>
    public class ProxyAssignForm : Form
    {
        private readonly List<Account> Accounts;

        private readonly TextBox ProxyBox;
        private readonly Label StatusLabel;
        private readonly Button AssignButton;
        private readonly Button TestButton;
        private readonly Button ClearButton;

        public ProxyAssignForm(List<Account> Accounts)
        {
            this.Accounts = Accounts ?? new List<Account>();

            Text = "Assign Proxies";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 360);

            Label Header = new Label
            {
                Text = $"{this.Accounts.Count} account(s) selected. One proxy per line — they are handed out in order and the list wraps.\r\n" +
                       "Formats: host:port · host:port:user:pass · user:pass@host:port · socks5://user:pass@host:port",
                Location = new Point(12, 10),
                Size = new Size(536, 46),
                AutoSize = false
            };

            ProxyBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Location = new Point(12, 62),
                Size = new Size(536, 200),
                Font = new Font(FontFamily.GenericMonospace, 9f)
            };

            StatusLabel = new Label
            {
                Location = new Point(12, 268),
                Size = new Size(536, 46),
                AutoSize = false
            };

            AssignButton = new Button { Text = "Assign", Location = new Point(12, 322), Size = new Size(110, 26) };
            TestButton = new Button { Text = "Test", Location = new Point(130, 322), Size = new Size(110, 26) };
            ClearButton = new Button { Text = "Clear proxies", Location = new Point(248, 322), Size = new Size(120, 26) };

            Button CloseButton = new Button { Text = "Close", Location = new Point(438, 322), Size = new Size(110, 26) };

            AssignButton.Click += Assign_Click;
            TestButton.Click += Test_Click;
            ClearButton.Click += Clear_Click;
            CloseButton.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { Header, ProxyBox, StatusLabel, AssignButton, TestButton, ClearButton, CloseButton });

            // Prefill with what these accounts already use, so the form doubles as a viewer.
            List<string> Existing = this.Accounts
                .Select(Account => Account.GetField(AccountProxies.ProxyField))
                .Where(Proxy => !string.IsNullOrWhiteSpace(Proxy))
                .Distinct()
                .ToList();

            ProxyBox.Text = string.Join("\r\n", Existing);

            ApplyTheme();
        }

        private void ApplyTheme()
        {
            BackColor = ThemeEditor.FormsBackground;
            ForeColor = ThemeEditor.FormsForeground;

            foreach (Control control in Controls)
            {
                if (control is Button Button)
                {
                    Button.FlatStyle = ThemeEditor.ButtonStyle;
                    Button.FlatAppearance.BorderColor = ThemeEditor.ButtonsBorder;
                    Button.BackColor = ThemeEditor.ButtonsBackground;
                    Button.ForeColor = ThemeEditor.ButtonsForeground;
                }
                else if (control is TextBox)
                {
                    control.BackColor = ThemeEditor.TextBoxesBackground;
                    control.ForeColor = ThemeEditor.TextBoxesForeground;
                }
                else
                {
                    control.BackColor = ThemeEditor.FormsBackground;
                    control.ForeColor = ThemeEditor.FormsForeground;
                }
            }
        }

        private List<string> ParseLines()
        {
            return ProxyBox.Lines
                .Select(Line => Line.Trim())
                .Where(Line => Line.Length > 0 && !Line.StartsWith("#"))
                .ToList();
        }

        private void Assign_Click(object sender, EventArgs e)
        {
            List<string> Proxies = ParseLines();

            if (Proxies.Count == 0)
            {
                StatusLabel.Text = "Nothing to assign — the list is empty. Use \"Clear proxies\" to remove existing assignments.";
                return;
            }

            List<string> Invalid = Proxies.Where(Proxy => !ProxyConfig.TryParse(Proxy, out _)).ToList();

            if (Invalid.Count > 0)
            {
                StatusLabel.Text = $"{Invalid.Count} line(s) could not be parsed, e.g. \"{Invalid[0]}\". Nothing was assigned.";
                return;
            }

            for (int i = 0; i < Accounts.Count; i++)
                Accounts[i].SetField(AccountProxies.ProxyField, Proxies[i % Proxies.Count]);

            AccountProxies.ForgetCaches();

            StatusLabel.Text = $"Assigned {Proxies.Count} proxy(ies) across {Accounts.Count} account(s)." +
                               (AccountProxies.Enabled ? string.Empty : " Note: UseAccountProxies is off in RAMSettings.ini, so they are not used yet.");
        }

        private void Clear_Click(object sender, EventArgs e)
        {
            foreach (Account account in Accounts)
            {
                account.RemoveField(AccountProxies.ProxyField);
                account.RemoveField(AccountProxies.ExitIPField);
            }

            AccountProxies.ForgetCaches();

            ProxyBox.Clear();

            StatusLabel.Text = $"Cleared the proxy assignment of {Accounts.Count} account(s).";
        }

        private async void Test_Click(object sender, EventArgs e)
        {
            List<string> Proxies = ParseLines();

            if (Proxies.Count == 0)
            {
                StatusLabel.Text = "Nothing to test.";
                return;
            }

            TestButton.Enabled = false;
            AssignButton.Enabled = false;

            try
            {
                int Working = 0;
                string FirstFailure = null;

                // Sequential on purpose: a dozen simultaneous checks through one provider looks like abuse and
                // some panels throttle it, which would report working proxies as dead.
                foreach (string Raw in Proxies)
                {
                    if (!ProxyConfig.TryParse(Raw, out ProxyConfig Proxy))
                    {
                        FirstFailure ??= $"{Raw} — unparseable";
                        continue;
                    }

                    StatusLabel.Text = $"Testing {Proxy}…";

                    string IP = await ProxyExitIP.GetAsync(Proxy, Refresh: true);

                    if (string.IsNullOrEmpty(IP)) FirstFailure ??= $"{Proxy} — no response";
                    else Working++;
                }

                StatusLabel.Text = $"{Working}/{Proxies.Count} proxy(ies) responded." + (FirstFailure != null ? $" First failure: {FirstFailure}" : string.Empty);
            }
            catch (Exception x) { StatusLabel.Text = $"Test failed: {x.Message}"; }
            finally
            {
                TestButton.Enabled = true;
                AssignButton.Enabled = true;
            }
        }
    }
}

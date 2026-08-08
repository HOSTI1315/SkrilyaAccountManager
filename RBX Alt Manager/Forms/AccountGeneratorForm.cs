using RBX_Alt_Manager.Classes;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Forms
{
    internal sealed class AccountGeneratorForm : Form
    {
        private readonly TextBox ApiKey = new TextBox { Width = 300, UseSystemPasswordChar = true };
        private readonly ComboBox Type = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox Region = new TextBox { Width = 55, MaxLength = 2 };
        private readonly Button Balance = new Button { Text = "Check balance", AutoSize = true };
        private readonly Button Generate = new Button { Text = "Generate account", AutoSize = true };
        private readonly Label Status = new Label { AutoSize = true, MaximumSize = new Size(500, 0) };

        public AccountGeneratorForm()
        {
            Text = "BloxGen account generator";
            Width = 570;
            Height = 280;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            Type.Items.AddRange(AccountGenerator.AccountTypes);
            Type.SelectedIndex = 0;

            var Panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14) };
            Panel.Controls.Add(new Label { Text = "BloxGen API key (not saved)", AutoSize = true });
            Panel.Controls.Add(ApiKey);
            Panel.Controls.Add(new Label { Text = "Account type", AutoSize = true, Margin = new Padding(3, 10, 3, 3) });
            Panel.Controls.Add(Type);
            Panel.Controls.Add(new Label { Text = "Region (optional, Ultra only; e.g. DE)", AutoSize = true, Margin = new Padding(3, 10, 3, 3) });
            Panel.Controls.Add(Region);

            var Buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            Buttons.Controls.Add(Balance);
            Buttons.Controls.Add(Generate);
            Panel.Controls.Add(Buttons);
            Panel.Controls.Add(Status);
            Controls.Add(Panel);

            Balance.Click += async (s, e) => await RunBalance();
            Generate.Click += async (s, e) => await RunGenerate();
        }

        private async Task RunBalance()
        {
            await Run(async () =>
            {
                GeneratorBalance Result = await AccountGenerator.GetBalanceAsync(ApiKey.Text);
                Status.Text = $"Balance: ${Result.Balance:0.####} · role: {Result.Role}";
            });
        }

        private async Task RunGenerate()
        {
            await Run(async () =>
            {
                GeneratedAccount Result = await AccountGenerator.GenerateAsync(ApiKey.Text, Type.SelectedItem?.ToString(), Region.Text);
                Status.Text = $"Added {Result.Username} · {Result.Type} · cost ${Result.Cost:0.####}";
            });
        }

        private async Task Run(Func<Task> Work)
        {
            Balance.Enabled = Generate.Enabled = false;
            Status.Text = "Working…";
            try { await Work(); }
            catch (Exception x) { Status.Text = x.Message; }
            finally { Balance.Enabled = Generate.Enabled = true; }
        }
    }
}

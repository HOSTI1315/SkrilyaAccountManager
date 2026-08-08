using RBX_Alt_Manager.Forms;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager
{
    public partial class ImportForm : Form
    {
        public ImportForm()
        {
            AccountManager.SetDarkBar(Handle);

            InitializeComponent();
            this.Rescale();

            // Allow dropping a file (e.g. a farmer-panel export) or selected text straight onto this window.
            AllowDrop = true;
            DragEnter += ImportForm_DragEnter;
            DragDrop += ImportForm_DragDrop;

            Accounts.AllowDrop = true;
            Accounts.DragEnter += ImportForm_DragEnter;
            Accounts.DragDrop += ImportForm_DragDrop;
        }

        private void ImportForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
                e.Effect = DragDropEffects.Copy;
        }

        private async void ImportForm_DragDrop(object sender, DragEventArgs e)
        {
            (int Added, int Failed) Result;

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] Files)
                Result = await AccountManager.Instance.ImportCookiesFromFiles(Files);
            else if (e.Data.GetDataPresent(DataFormats.Text) && e.Data.GetData(DataFormats.Text) is string Text)
                Result = await AccountManager.Instance.ImportCookiesFromText(Text);
            else
                return;

            if (Result.Added > 0 || Result.Failed > 0)
                Accounts.Text = $"Done. Added/updated {Result.Added}, failed {Result.Failed}.";
        }

        public void ApplyTheme()
        {
            BackColor = ThemeEditor.FormsBackground;
            ForeColor = ThemeEditor.FormsForeground;

            foreach (Control control in this.Controls)
            {
                if (control is Button || control is CheckBox)
                {
                    if (control is Button)
                    {
                        Button b = control as Button;
                        b.FlatStyle = ThemeEditor.ButtonStyle;
                        b.FlatAppearance.BorderColor = ThemeEditor.ButtonsBorder;
                    }

                    if (!(control is CheckBox)) control.BackColor = ThemeEditor.ButtonsBackground;
                    control.ForeColor = ThemeEditor.ButtonsForeground;
                }
                else if (control is TextBox || control is RichTextBox)
                {
                    if (control is Classes.BorderedTextBox)
                    {
                        Classes.BorderedTextBox b = control as Classes.BorderedTextBox;
                        b.BorderColor = ThemeEditor.TextBoxesBorder;
                    }

                    if (control is Classes.BorderedRichTextBox)
                    {
                        Classes.BorderedRichTextBox b = control as Classes.BorderedRichTextBox;
                        b.BorderColor = ThemeEditor.TextBoxesBorder;
                    }

                    control.BackColor = ThemeEditor.TextBoxesBackground;
                    control.ForeColor = ThemeEditor.TextBoxesForeground;
                }
                else if (control is Label)
                {
                    control.BackColor = ThemeEditor.LabelTransparent ? Color.Transparent : ThemeEditor.LabelBackground;
                    control.ForeColor = ThemeEditor.LabelForeground;
                }
                else if (control is ListBox)
                {
                    control.BackColor = ThemeEditor.ButtonsBackground;
                    control.ForeColor = ThemeEditor.ButtonsForeground;
                }
            }
        }

        private void ImportForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private async void ImportButton_Click(object sender, EventArgs e)
        {
            string Raw = Accounts.Text;

            ImportButton.Enabled = false;
            Accounts.ReadOnly = true;
            ImportButton.Text = "Importing...";

            try
            {
                // Off-thread, batched import (single save + refresh). Handles raw tokens and messy
                // farmer-panel lines; cleans the junk and keeps only the .ROBLOSECURITY cookie.
                var (Added, Failed) = await AccountManager.Instance.ImportCookiesFromText(Raw);

                if (Added > 0 || Failed > 0)
                    Accounts.Text = $"Done. Added/updated {Added}, failed {Failed}.";
            }
            finally
            {
                Accounts.ReadOnly = false;
                ImportButton.Enabled = true;
                ImportButton.Text = "Import";
            }
        }
    }
}
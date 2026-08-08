using RBX_Alt_Manager.Classes;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Forms
{
    /// <summary>A deliberately small two-step UI: inspect first, then explicitly clean that exact class of data.</summary>
    internal sealed class ProfileCleanerForm : Form
    {
        private readonly TextBox Details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill };
        private readonly Label Summary = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
        private readonly Button RefreshButton = new Button { Text = "Refresh dry-run", AutoSize = true };
        private readonly Button CleanButton = new Button { Text = "Clean shown data…", AutoSize = true };
        private ProfileCleanupPreview Current;

        public ProfileCleanerForm()
        {
            Text = "Roblox profile cleanup";
            Width = 720;
            Height = 460;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;

            var Buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Padding = new Padding(8) };
            Buttons.Controls.Add(RefreshButton);
            Buttons.Controls.Add(CleanButton);

            var Layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            Layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Layout.Controls.Add(Summary, 0, 0);
            Layout.Controls.Add(Details, 0, 1);
            Layout.Controls.Add(Buttons, 0, 2);
            Controls.Add(Layout);

            RefreshButton.Click += (s, e) => RefreshPreview();
            CleanButton.Click += (s, e) => Clean();
            Shown += (s, e) => RefreshPreview();
        }

        private void RefreshPreview()
        {
            Current = ProfileCleaner.Preview();
            Summary.Text = Current.Items.Count == 0
                ? "Nothing disposable was found. Roblox Versions are never touched."
                : $"Dry-run: {Current.Items.Count} target(s), {ProfileCleaner.FormatBytes(Current.TotalBytes)}. Roblox Versions and Skrilya data are excluded.";
            Details.Lines = Current.Items.Select(Item => $"{ProfileCleaner.FormatBytes(Item.Bytes),10}  {Item.Path}").ToArray();
            CleanButton.Enabled = Current.Items.Count > 0;
        }

        private void Clean()
        {
            if (Current == null || Current.Items.Count == 0) return;

            if (MessageBox.Show(
                    $"Delete the {Current.Items.Count} targets shown above ({ProfileCleaner.FormatBytes(Current.TotalBytes)})?\r\n\r\nBasic settings are backed up and restored. The Roblox Versions directory is never read, changed or deleted.",
                    "Clean Roblox profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            try
            {
                ProfileCleanupPreview Done = ProfileCleaner.Clean();
                MessageBox.Show($"Cleanup finished. Processed {Done.Items.Count} target(s).", "Roblox profile cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception x)
            {
                MessageBox.Show(x.Message, "Roblox profile cleanup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            RefreshPreview();
        }
    }
}

using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Forms;
using System.Windows.Forms;

namespace RBX_Alt_Manager
{
    public partial class AccountManager
    {
        private void InstallGeneratorMenu()
        {
            if (!AccountGenerator.Enabled) return;

            var Item = new ToolStripMenuItem("BloxGen…");
            Item.Click += (s, e) => new AccountGeneratorForm().ShowDialog(this);
            AddAccountsStrip.Items.Add(new ToolStripSeparator());
            AddAccountsStrip.Items.Add(Item);
        }
    }
}

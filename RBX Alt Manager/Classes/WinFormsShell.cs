using RBX_Alt_Manager.Forms;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// The current WinForms front end, expressed as an <see cref="IShell"/>.
    ///
    /// This is the only place in the logic path that is allowed to know about MessageBox, the main form, or the
    /// account list control. Replacing the UI means writing one more of these — the logic layer does not change.
    ///
    /// Every method marshals onto the UI thread itself: callers run on timers, the thread pool and websocket
    /// callbacks, and requiring each of them to remember that was exactly the bug this class removes.
    /// </summary>
    public class WinFormsShell : IShell
    {
        private static AccountManager Form => AccountManager.Instance;

        private void Show(string Message, string Title, MessageBoxIcon Icon)
        {
            // No window yet (or already gone): log rather than throw. A launch driven by the web API can reach
            // here before the form exists.
            if (Form == null || Form.IsDisposed)
            {
                Program.Logger.Warn($"[{Title}] {Message}");

                return;
            }

            Utilities.InvokeIfRequired(Form, () => MessageBox.Show(Message, Title, MessageBoxButtons.OK, Icon));
        }

        public void Info(string Message, string Title = "Account Manager") => Show(Message, Title, MessageBoxIcon.Information);
        public void Warn(string Message, string Title = "Account Manager") => Show(Message, Title, MessageBoxIcon.Warning);
        public void Error(string Message, string Title = "Account Manager") => Show(Message, Title, MessageBoxIcon.Error);

        public bool Confirm(string Caption, string Instruction, string Text, bool CanRemember = true)
        {
            if (Form == null || Form.IsDisposed)
            {
                Program.Logger.Warn($"[{Caption}] {Instruction} — {Text} (no window to ask; answering no)");

                return false;
            }

            bool Answer = false;

            // Blocking, like the code this replaced: the caller's next line depends on the answer.
            Utilities.InvokeIfRequired(Form, () => Answer = Utilities.YesNoPrompt(Caption, Instruction, Text, CanRemember));

            return Answer;
        }

        public void OnUiThread(Action Work)
        {
            if (Form == null || Form.IsDisposed)
            {
                try { Work(); } catch (Exception x) { Program.Logger.Error($"[Shell] work failed: {x}"); }

                return;
            }

            Utilities.InvokeIfRequired(Form, () => Work());
        }

        public void LaunchFinished() => Form?.NextAccount();

        public void CancelLaunching() => Form?.CancelLaunching();

        public void RefreshAccounts(IEnumerable<Account> Accounts)
        {
            if (Form == null || Form.IsDisposed || Accounts == null) return;

            // Materialise before crossing threads: the source is usually a LINQ query over the shared account
            // list, and enumerating it on the UI thread races the writers.
            List<Account> Snapshot = Accounts.ToList();

            if (Snapshot.Count == 0) return;

            Utilities.InvokeIfRequired(Form, () => Form.AccountsView.RefreshObjects(Snapshot));
        }

        public void ShowMissingAssets(Account account, long[] AssetIds)
        {
            if (Form == null || Form.IsDisposed) return;

            Utilities.InvokeIfRequired(Form, () => new MissingAssets(account, AssetIds).Show());
        }
    }
}

using RBX_Alt_Manager.Classes;
using Sodium;
using System;
using System.Security.Cryptography;

namespace RBX_Alt_Manager
{
    /// <summary>How the account store is protected, and what the app is waiting for right now.</summary>
    public enum SecurityStep
    {
        /// <summary>Nothing to ask: the store is open.</summary>
        Ready,

        /// <summary>First run (or a reset): the user has to choose between machine encryption and a password.</summary>
        ChooseProtection,

        /// <summary>They chose a password and now have to set it.</summary>
        SetPassword,

        /// <summary>The store is password-locked and needs the password before anything can be read.</summary>
        Unlock
    }

    /// <summary>
    /// The account store's protection, expressed without any window attached.
    ///
    /// The WinForms window drives this through three stacked panels, which means the first-run flow was
    /// unreachable from any other interface — including the HTML one. These methods do exactly what those
    /// buttons do, return a result instead of showing a message box, and leave the panels in sync so the two
    /// interfaces can be open at the same time without contradicting each other.
    /// </summary>
    public partial class AccountManager
    {
        private SecurityStep LastKnownStep = SecurityStep.Ready;

        /// <summary>
        /// True when the HTML interface is responsible for asking. The old window then never renders its
        /// encryption panels - two windows asking the same question, one of them a blank grey screen, is worse
        /// than either alone.
        /// </summary>
        private static bool NewUiOwnsSecurity
        {
            get
            {
                try { return General != null && General.Exists("NewUI") && bool.TryParse(General.Get("NewUI"), out bool Value) && Value; }
                catch { return false; }
            }
        }

        public SecurityStep CurrentSecurityStep => NewUiOwnsSecurity ? LastKnownStep : StepFromPanels();

        private SecurityStep StepFromPanels()
        {
            if (PasswordPanel == null || !PasswordPanel.Visible) return SecurityStep.Ready;
            if (PasswordSelectionPanel.Visible) return SecurityStep.SetPassword;
            if (PasswordLayoutPanel.Visible && !EncryptionSelectionPanel.Visible) return SecurityStep.Unlock;

            return SecurityStep.ChooseProtection;
        }

        /// <summary>
        /// Called after every step of the flow. Reads what the old code just decided, remembers it, and - when
        /// the HTML interface owns the question - takes the panels back down and tells the page instead.
        /// </summary>
        public void SyncSecurity()
        {
            LastKnownStep = StepFromPanels();

            if (!NewUiOwnsSecurity) return;

            HidePasswordPanels();

            Forms.WebShell.NotifySecurityChanged();
        }

        /// <summary>Puts the old panels back in charge - used when the HTML shell could not be opened.</summary>
        public void RestoreSecurityPanels()
        {
            if (LastKnownStep == SecurityStep.Ready) return;

            PasswordPanel.Visible = true;
            PasswordPanel.BringToFront();
            PasswordLayoutPanel.Visible = LastKnownStep == SecurityStep.Unlock;
            EncryptionSelectionPanel.Visible = LastKnownStep == SecurityStep.ChooseProtection;
            PasswordSelectionPanel.Visible = LastKnownStep == SecurityStep.SetPassword;
        }

        /// <summary>True when the store is protected by a password rather than by this Windows account alone.</summary>
        public static bool IsPasswordProtected => !PasswordHash.IsEmpty;

        /// <summary>Machine encryption (DPAPI): no password, readable only by this Windows user on this machine.</summary>
        public bool UseDefaultProtection()
        {
            DefaultEncryptionButton_Click(null, EventArgs.Empty);

            SyncSecurity();
            AfterSecurityChanged();

            return true;
        }

        /// <summary>
        /// Sets the password and re-encrypts the store with it. The confirmation step lives in the interface —
        /// it asks twice and calls this once — so this does not carry the WinForms flow's half-finished state.
        /// </summary>
        public bool SetProtectionPassword(string Password, out string Error)
        {
            Error = null;

            if (string.IsNullOrEmpty(Password) || Password.Length < 4)
            {
                Error = "The password must be at least 4 characters.";

                return false;
            }

            try
            {
                byte[] Hash = CryptoHash.Hash(Password);

                PasswordHash = new ReadOnlyMemory<byte>(ProtectedData.Protect(Hash, Array.Empty<byte>(), DataProtectionScope.CurrentUser));

                SaveAccounts(true, true);
                FlushPendingSave();

                LastHash = null;
                IsResettingPassword = false;

                HidePasswordPanels();
                SyncSecurity();
                AfterSecurityChanged();

                return true;
            }
            catch (Exception x)
            {
                Program.Logger.Error($"[Security] setting the password failed: {x}");

                Error = x.Message;

                return false;
            }
        }

        /// <summary>Unlocks a password-protected store. Returns false with a reason rather than throwing at the caller.</summary>
        public bool UnlockWithPassword(string Password, out string Error)
        {
            Error = null;

            if (string.IsNullOrEmpty(Password) || Password.Length < 4)
            {
                Error = "The password must be at least 4 characters.";

                return false;
            }

            try
            {
                LoadAccounts(CryptoHash.Hash(Password));

                // LoadAccounts leaves the password panel up when it could not read the store, and takes it down on
                // success. Read that panel state DIRECTLY: CurrentSecurityStep is a cache that LoadAccounts does not
                // refresh on the success path, and while the HTML interface owns security that cache is the only
                // thing CurrentSecurityStep returns — so a correct password would otherwise be rejected.
                if (StepFromPanels() == SecurityStep.Unlock)
                {
                    Error = "Incorrect password.";

                    return false;
                }

                HidePasswordPanels();
                SyncSecurity();
                AfterSecurityChanged();

                return true;
            }
            catch (Exception x)
            {
                Program.Logger.Warn($"[Security] unlock failed: {x.Message}");

                Error = "Incorrect password.";

                return false;
            }
        }

        /// <summary>Moves the flow from "choose protection" to "set a password", matching the window's own button.</summary>
        public bool ChoosePasswordProtection()
        {
            // Set the "set a password" configuration directly rather than through PasswordEncryptionButton_Click:
            // that handler calls SyncSecurity() itself, and while the HTML interface owns security the parent panel
            // is kept hidden, so its SyncSecurity would read "Ready", hide the sub-panels, and the page would close
            // the first-run dialog without ever asking. Put the whole state up first, then sync exactly once.
            EncryptionSelectionPanel.Visible = false;
            PasswordLayoutPanel.Visible = false;
            PasswordSelectionPanel.Visible = true;
            PasswordPanel.Visible = true;

            SyncSecurity();

            return true;
        }

        private void HidePasswordPanels()
        {
            PasswordPanel.Visible = false;
            PasswordSelectionPanel.Visible = false;
            EncryptionSelectionPanel.Visible = false;
            PasswordLayoutPanel.Visible = false;
        }

        /// <summary>
        /// Puts text on the clipboard on behalf of the HTML interface. Secrets take the path that wipes the
        /// clipboard again after a delay; the page itself never receives either kind of value.
        /// </summary>
        public static bool CopyForBridge(string Text, bool Secret)
        {
            if (string.IsNullOrEmpty(Text)) return false;

            Instance?.InvokeIfRequired(() =>
            {
                if (Secret) CopySensitive(Text);
                else CopyPlain(Text);
            });

            return true;
        }

        /// <summary>Redraws the old window's list after the HTML interface changed the accounts behind its back.</summary>
        public void RefreshFromBridge()
        {
            try
            {
                this.InvokeIfRequired(() =>
                {
                    AccountsView.SetObjects(AccountsList);

                    RefreshView();
                });
            }
            catch (Exception x) { Program.Logger.Error($"[Bridge] refreshing the old window failed: {x.Message}"); }
        }

        /// <summary>The account list is only meaningful once the store is open; refresh both interfaces.</summary>
        private void AfterSecurityChanged()
        {
            try
            {
                AccountsView.SetObjects(AccountsList);

                RefreshView();
            }
            catch (Exception x) { Program.Logger.Error($"[Security] refreshing the list failed: {x.Message}"); }

            Forms.WebShell.NotifyAccountsChanged();
            Forms.WebShell.NotifySecurityChanged();
        }
    }
}

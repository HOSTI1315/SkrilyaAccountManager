using System;
using System.Collections.Generic;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Everything the logic layer needs from whatever user interface is on top of it.
    ///
    /// Before this existed, <see cref="Account"/> and friends called MessageBox directly and reached into the
    /// main WinForms form by name. That is what made the UI unreplaceable — and it also meant a launch driven
    /// by the web API or a background timer could pop a modal on a machine nobody was looking at. Logic now
    /// states its intent ("tell the user this failed", "that launch is done") and the host decides how, or
    /// whether, to show it.
    ///
    /// Implementations must be callable from ANY thread: the logic layer runs on timers, the thread pool and
    /// websocket callbacks, and it is the host's job to marshal, not the caller's.
    /// </summary>
    public interface IShell
    {
        void Info(string Message, string Title = "Account Manager");
        void Warn(string Message, string Title = "Account Manager");
        void Error(string Message, string Title = "Account Manager");

        /// <summary>A yes/no question. Returns false when there is nobody to ask — never assume consent.</summary>
        bool Confirm(string Caption, string Instruction, string Text, bool CanRemember = true);

        /// <summary>Runs work on the UI thread. Blocks until it has run, matching the semantics callers relied on.</summary>
        void OnUiThread(Action Work);

        /// <summary>One account's launch has finished (successfully or not); the batch may advance.</summary>
        void LaunchFinished();

        /// <summary>Abort a running batch launch.</summary>
        void CancelLaunching();

        /// <summary>These accounts' displayed state changed.</summary>
        void RefreshAccounts(IEnumerable<Account> Accounts);

        /// <summary>The client refused to join because the account is missing assets it must own.</summary>
        void ShowMissingAssets(Account account, long[] AssetIds);
    }

    /// <summary>
    /// Static entry point for the logic layer. <see cref="Current"/> starts headless so that anything running
    /// before (or without) a window — the web API, a scheduled warm-up, a unit test — works instead of
    /// throwing, and never blocks on a dialog no one can answer.
    /// </summary>
    public static class Shell
    {
        private static IShell CurrentShell = new HeadlessShell();

        public static IShell Current
        {
            get => CurrentShell;
            set => CurrentShell = value ?? new HeadlessShell();
        }

        public static void Info(string Message, string Title = "Account Manager") => CurrentShell.Info(Message, Title);
        public static void Warn(string Message, string Title = "Account Manager") => CurrentShell.Warn(Message, Title);
        public static void Error(string Message, string Title = "Account Manager") => CurrentShell.Error(Message, Title);
        public static bool Confirm(string Caption, string Instruction, string Text, bool CanRemember = true) => CurrentShell.Confirm(Caption, Instruction, Text, CanRemember);
        public static void OnUiThread(Action Work) => CurrentShell.OnUiThread(Work);
        public static void LaunchFinished() => CurrentShell.LaunchFinished();
        public static void CancelLaunching() => CurrentShell.CancelLaunching();
        public static void RefreshAccounts(IEnumerable<Account> Accounts) => CurrentShell.RefreshAccounts(Accounts);
        public static void ShowMissingAssets(Account account, long[] AssetIds) => CurrentShell.ShowMissingAssets(account, AssetIds);
    }

    /// <summary>The no-window fallback: everything is logged, nothing is shown, and no question is answered "yes".</summary>
    public class HeadlessShell : IShell
    {
        public void Info(string Message, string Title = "Account Manager") => Program.Logger.Info($"[{Title}] {Message}");
        public void Warn(string Message, string Title = "Account Manager") => Program.Logger.Warn($"[{Title}] {Message}");
        public void Error(string Message, string Title = "Account Manager") => Program.Logger.Error($"[{Title}] {Message}");

        public bool Confirm(string Caption, string Instruction, string Text, bool CanRemember = true)
        {
            Program.Logger.Warn($"[{Caption}] {Instruction} — {Text} (no user interface to ask; answering no)");

            return false;
        }

        public void OnUiThread(Action Work)
        {
            try { Work(); }
            catch (Exception x) { Program.Logger.Error($"[Shell] work failed: {x}"); }
        }

        public void LaunchFinished() { }
        public void CancelLaunching() { }
        public void RefreshAccounts(IEnumerable<Account> Accounts) { }

        public void ShowMissingAssets(Account account, long[] AssetIds) =>
            Program.Logger.Warn($"[MissingAssets] {account?.Username} is missing {AssetIds?.Length ?? 0} asset(s): {string.Join(", ", AssetIds ?? new long[0])}");
    }
}

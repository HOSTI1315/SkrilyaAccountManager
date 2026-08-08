using log4net;
using Microsoft.Win32;
using RBX_Alt_Manager.Properties;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebSocketSharp;

namespace RBX_Alt_Manager
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>

        public static readonly ILog Logger = LogManager.GetLogger("Account Manager");
        public static bool Closed = false; // RobloxProcess.cs would cause the program to chill in the background as long roblox was also running
        public static bool Elevated;
        public static float Scale
        {
            get
            {
                float _Scale = (AccountManager.General != null && AccountManager.General.Exists("WindowScale")) ? AccountManager.General.Get<float>("WindowScale") : 0f;

                if (_Scale > 3f) AccountManager.General.RemoveProperty("WindowScale");

                if (_Scale > 0 && _Scale <= 3)
                    return AccountManager.General.Get<float>("WindowScale");

                return 1f;
            }
        }

        public static bool ScaleFonts
        {
            get
            {
                if (AccountManager.General != null && AccountManager.General.Exists("ScaleFonts"))
                    return AccountManager.General.Get<bool>("ScaleFonts");

                return true;
            }
        }

#if !DEBUG
        // Own single-instance GUID, deliberately different from upstream RAM's {93b3858f-...}: SAM is a separate
        // product, and sharing the mutex would make a stock Roblox Account Manager and SAM refuse to run together.
        private static readonly Mutex mutex = new Mutex(true, "{5A4D1C7E-2B94-4F30-9E61-7C8D0A5B3F12}");
#endif

        private static void TryFlushAccounts()
        {
            try { AccountManager.FlushPendingSave(); } catch { }
        }

        [STAThread]
        static void Main(params string[] Arguments)
        {
            int Stupid = 1337;

            // log4net used to configure itself from the <appSettings> "log4net.Config" key in App.config.
            // That auto-configuration path is .NET Framework only — on .NET it never runs, so the repository
            // stays empty and every Logger call silently goes nowhere. Configure it explicitly instead.
            try
            {
                FileInfo LogConfig = new FileInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log4.config"));

                if (LogConfig.Exists)
                    log4net.Config.XmlConfigurator.ConfigureAndWatch(LogConfig);
            }
            catch { }

            try
            {
                if (Directory.GetParent(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)).FullName.Contains(Path.GetTempPath().Remove(Path.GetTempPath().Length - 1)))
                {
                    MessageBox.Show("SkrilyaAccountManager must be extracted in order to function correctly!", "SkrilyaAccountManager", MessageBoxButtons.OK);
                    Environment.Exit(Stupid);
                }
            }
            catch { }

            try
            {
                if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    Elevated = true;

                // MessageBox.Show("Some features may not work properly if you ran the account manager as admin!", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Error); // I don't think this is an issue anymore
            }
            catch { }

            // "Is this a complete install?" used to be answered by looking for <exe>.exe.config. On .NET the
            // apphost's config sits next to the managed dll (<name>.dll.config), so that file never exists and
            // the "install me in my own folder" prompt fired on EVERY launch. runtimeconfig.json is the file
            // that is always emitted beside the apphost, so test for that instead.
            string InstallMarker = Path.Combine(AppContext.BaseDirectory, $"{Path.GetFileNameWithoutExtension(Application.ExecutablePath)}.runtimeconfig.json");

            // A single-file publish has no runtimeconfig.json on disk either — it is inside the bundle — so the
            // check above cannot tell "one self-contained exe" from "half an install", and the prompt would
            // greet the user on every launch. An assembly with no Location is exactly what single file means.
            bool SingleFile = string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location);

            if (!SingleFile && !File.Exists(InstallMarker))
            {
                var Parent = Directory.GetParent(Application.ExecutablePath);
                var Files = Parent.GetFiles();

                if (!File.Exists(Path.Combine(Parent.FullName, "AccountData.json")) && Files.Length > 1)
                {
                    if (!Utilities.YesNoPrompt("SkrilyaAccountManager", "It is recommended you install SkrilyaAccountManager to it's own folder", "Skip this check and install here anyways?", false))
                        Environment.Exit(4);
                }
            }

            Application.ApplicationExit += (s, e) =>
            {
                Closed = true;

                // Loopback listeners and the title/dialog timer outlive the forms otherwise, and would keep the
                // process alive with bound ports after the window is gone.
                try { Classes.SocksBridge.StopAll(); } catch { }
                try { Classes.ClientWindows.StopTimer(); } catch { }
            };

            // Global exception handling. A single unhandled throw on a background/timer/websocket thread or
            // inside an async void handler used to terminate the whole process (and silently lose unsaved
            // account changes). Log it and flush pending saves; for UI-thread exceptions keep the app alive.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                Logger.Error($"Unhandled UI thread exception: {e.Exception}");
                TryFlushAccounts();
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Logger.Error($"Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
                TryFlushAccounts();
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Logger.Error($"Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };

            if (!File.Exists(Path.Combine(Environment.CurrentDirectory, "RAMTheme.ini")))
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "RAMTheme.ini"), Resources.DefaultTheme);

            // Only log4.config is still repaired on startup. The App.config half of this check existed to
            // restore .NET Framework assembly binding redirects; on .NET those are ignored entirely, and the
            // embedded hash could never match a .NET-shaped config — it would have restart-looped forever.
            if (!(Arguments.Length == 1 && Arguments[0] == "-restart"))
                try
                {
                    string LogConfigPath = Path.Combine(Environment.CurrentDirectory, "log4.config");

                    if (!File.Exists(LogConfigPath) || Utilities.FileSHA256(LogConfigPath) != Resources.Log4ConfigHash)
                    {
                        Logger.Warn("Restoring log4.config (missing or modified)");

                        File.WriteAllBytes(LogConfigPath, Resources.log4);

                        // Used to relaunch itself so the fresh file would be read. It does not need to: log4net
                        // is pointed at the file right here. The restart was invisible on a normal install and
                        // looks like a crash on a first run from Downloads, which is how this now ships.
                        log4net.Config.XmlConfigurator.ConfigureAndWatch(new FileInfo(LogConfigPath));
                    }
                }
                catch (Exception x)
                {
                    // A read-only or write-protected folder must cost logging, not the whole app.
                    Logger.Warn($"Could not restore log4.config: {x.Message}");
                }

            // libsodium is no longer hand-dropped into the working directory: Sodium.Core ships the native
            // library per-architecture under runtimes\ and loads it from there. The old embedded copy was
            // 32-bit and would fail to load in this now-x64 process.

#if !DEBUG
            if (mutex.WaitOne(TimeSpan.Zero, true))
            {
#endif
                try
                {
                    string CookiesFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Roblox\LocalStorage\RobloxCookies.dat");
                    bool Apply773Fix = !(string.IsNullOrEmpty(CookiesFile) || !File.Exists(CookiesFile) || File.Exists(Path.Combine(Environment.CurrentDirectory, "no773fix.txt")));

                    if (!Apply773Fix) Logger.Error($"Not applying 773 error fix | Cookies File Exists: {File.Exists(CookiesFile)} | User No Fix File Exists: {File.Exists(Path.Combine(Environment.CurrentDirectory, "no773fix.txt"))}");

                    if (Apply773Fix) try { using (new FileStream(CookiesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } } catch { Apply773Fix = false; } // Check if the file is already locked by another program

                    using (Apply773Fix ? new FileStream(CookiesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
                    {
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new AccountManager());
                    }
                }
#if DEBUG
            finally { }
#else
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
            else
                MessageBox.Show("SkrilyaAccountManager is already running!", "SkrilyaAccountManager", MessageBoxButtons.OK, MessageBoxIcon.Error);
#endif
        }
    }
}
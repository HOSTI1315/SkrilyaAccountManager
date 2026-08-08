using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes.Bridge
{
    /// <summary>
    /// The method table the HTML interface is allowed to call, and the events it receives.
    ///
    /// This is the app's public surface for the new UI, so it is written as intent ("launch this account")
    /// rather than as remote control of internals — the page never gets an Account object, never sees a cookie,
    /// and cannot reach the WinForms tree. Adding a screen means adding methods here, not widening what the
    /// page can touch.
    /// </summary>
    public static class BridgeApi
    {
        public static void RegisterAll(UiBridge Bridge)
        {
            // ---------- app ----------

            Bridge.Register("app.info", _ => new
            {
                name = "SkrilyaAccountManager",
                version = System.Diagnostics.FileVersionInfo.GetVersionInfo(System.Windows.Forms.Application.ExecutablePath).FileVersion,
                accounts = AccountManager.GetAccountsSnapshot().Count,
                methods = Bridge.Methods.OrderBy(Name => Name).ToArray()
            });

            Bridge.Register("app.log", Parameters =>
            {
                string Level = Parameters.Value<string>("level") ?? "info";
                string Message = Parameters.Value<string>("message") ?? string.Empty;

                if (Level == "error") Program.Logger.Error($"[UI] {Message}");
                else if (Level == "warn") Program.Logger.Warn($"[UI] {Message}");
                else Program.Logger.Info($"[UI] {Message}");

                return true;
            });

            // ---------- protecting the account store ----------
            //
            // First run and unlocking used to be reachable only through the old window's stacked panels. The page
            // asks what is needed and answers it; the window stays in sync because both go through the same code.

            Bridge.Register("security.state", _ =>
            {
                AccountManager Window = AccountManager.Instance;

                return new
                {
                    step = (Window?.CurrentSecurityStep ?? SecurityStep.Ready).ToString(),
                    passwordProtected = AccountManager.IsPasswordProtected
                };
            });

            Bridge.Register("security.useDefault", _ =>
            {
                AccountManager Window = AccountManager.Instance ?? throw new Exception("The manager window is not ready yet");

                return Window.UseDefaultProtection();
            });

            Bridge.Register("security.choosePassword", _ =>
            {
                AccountManager Window = AccountManager.Instance ?? throw new Exception("The manager window is not ready yet");

                return Window.ChoosePasswordProtection();
            });

            Bridge.Register("security.setPassword", Parameters =>
            {
                AccountManager Window = AccountManager.Instance ?? throw new Exception("The manager window is not ready yet");

                if (!Window.SetProtectionPassword(Parameters.Value<string>("password"), out string Error)) throw new Exception(Error);

                return true;
            });

            Bridge.Register("security.unlock", Parameters =>
            {
                AccountManager Window = AccountManager.Instance ?? throw new Exception("The manager window is not ready yet");

                if (!Window.UnlockWithPassword(Parameters.Value<string>("password"), out string Error)) throw new Exception(Error);

                return true;
            });

            // ---------- accounts ----------

            Bridge.Register("accounts.list", Parameters =>
            {
                string Group = Parameters.Value<string>("group");
                string Search = (Parameters.Value<string>("search") ?? string.Empty).Trim();

                IEnumerable<Account> Accounts = AccountManager.GetAccountsSnapshot();

                if (!string.IsNullOrEmpty(Group) && Group != "All") Accounts = Accounts.Where(account => account.Group == Group);

                if (Search.Length > 0)
                    Accounts = Accounts.Where(account =>
                        (account.Username ?? string.Empty).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (account.Alias ?? string.Empty).IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        account.UserID.ToString().StartsWith(Search, StringComparison.Ordinal));

                return Accounts.Select(Describe).ToArray();
            });

            // Groups carry two names: what is stored on the account, and what the user reads. The old window
            // hides a leading number so "001 Farm" sorts first and reads as "Farm", and that convention is kept —
            // a hand-made order is layered on top of it, never instead of it.
            Bridge.Register("accounts.groups", _ => Groups
                .Arrange(AccountManager.GetAccountsSnapshot().Select(account => account.Group))
                .Select(Group => new { name = Group.Name, title = Group.Title, accounts = Group.Accounts })
                .ToArray());

            Bridge.Register("groups.reorder", Parameters =>
            {
                string[] Order = (Parameters["order"] as JArray)?.Select(Token => Token.Value<string>()).ToArray() ?? new string[0];

                if (Order.Length == 0) throw new Exception("No order given");

                Groups.SaveOrder(Order);

                return true;
            });

            // A group is not an object anywhere: it is a string carried by each account, with the order and the
            // default kept as settings. So renaming one means rewriting every account that carries it AND the two
            // settings that may still name it — miss either and the group reappears empty, or new accounts keep
            // landing in a group that no longer exists.
            Bridge.Register("groups.rename", Parameters =>
            {
                string From = (Parameters.Value<string>("from") ?? string.Empty).Trim();
                string To = (Parameters.Value<string>("to") ?? string.Empty).Trim();

                if (From.Length == 0 || To.Length == 0) throw new Exception("Both the old and the new name are needed");
                if (From == To) return new { renamed = 0 };

                // Both ends. Renaming a group TO "All" writes that literal onto its accounts, where it collides
                // with the filter chip that means "every account" — the group then cannot be reached by any
                // filter, nor renamed back, because the menu refuses to open on "All".
                if (From == ReservedGroup || To == ReservedGroup) throw new Exception(ReservedGroupMessage);

                List<Account> Moved = AccountManager.GetAccountsSnapshot()
                    .Where(account => (string.IsNullOrEmpty(account.Group) ? "Default" : account.Group) == From)
                    .ToList();

                foreach (Account account in Moved) account.Group = To;

                // The saved order names groups as strings too, and so does the default.
                List<string> Order = Groups.SavedOrder();

                // Distinct: renaming one group onto the name of another is a merge, and without it the order
                // would keep the surviving name twice.
                if (Order.Contains(From)) Groups.SaveOrder(Order.Select(Name => Name == From ? To : Name).Distinct());

                if (Groups.DefaultGroup == From)
                {
                    AccountManager.General.Set(Groups.DefaultGroupSetting, To);
                    AccountManager.IniSettings.Save("RAMSettings.ini");
                }

                if (Moved.Count > 0) AccountManager.SaveAccounts();

                AccountManager.Instance?.RefreshFromBridge();
                Forms.WebShell.NotifyAccountsChanged();

                return new { renamed = Moved.Count, from = From, to = To };
            });

            // Deleting a group cannot delete the accounts in it — they are moved to another group, which is the
            // only thing "delete" can honestly mean when a group is just a label.
            Bridge.Register("groups.delete", Parameters =>
            {
                string Name = (Parameters.Value<string>("name") ?? string.Empty).Trim();
                string Into = (Parameters.Value<string>("into") ?? "Default").Trim();

                if (Name.Length == 0) throw new Exception("Which group?");
                if (Into.Length == 0) Into = "Default";
                if (Name == ReservedGroup || Into == ReservedGroup) throw new Exception(ReservedGroupMessage);
                if (Name == Into) throw new Exception("That would move the accounts into the same group");

                List<Account> Moved = AccountManager.GetAccountsSnapshot()
                    .Where(account => (string.IsNullOrEmpty(account.Group) ? "Default" : account.Group) == Name)
                    .ToList();

                foreach (Account account in Moved) account.Group = Into;

                List<string> Order = Groups.SavedOrder();

                if (Order.Contains(Name)) Groups.SaveOrder(Order.Where(Group => Group != Name));

                // New accounts must not keep being sent to a group that is gone.
                if (Groups.DefaultGroup == Name)
                {
                    AccountManager.General.Set(Groups.DefaultGroupSetting, Into);
                    AccountManager.IniSettings.Save("RAMSettings.ini");
                }

                if (Moved.Count > 0) AccountManager.SaveAccounts();

                AccountManager.Instance?.RefreshFromBridge();
                Forms.WebShell.NotifyAccountsChanged();

                return new { moved = Moved.Count, name = Name, into = Into };
            });

            Bridge.Register("groups.defaultGroup", Parameters =>
            {
                string Name = Parameters.Value<string>("name");

                // Reading and writing share one method: the page asks with no name, sets with one.
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    if (Name.Trim() == ReservedGroup) throw new Exception(ReservedGroupMessage);

                    AccountManager.General.Set(Groups.DefaultGroupSetting, Name.Trim());
                    AccountManager.IniSettings.Save("RAMSettings.ini");
                }

                return Groups.DefaultGroup;
            });

            Bridge.Register("accounts.launch", async Parameters =>
            {
                Account account = Find(Parameters);

                if (account == null) throw new Exception("Account not found");
                if (!long.TryParse(Parameters.Value<string>("placeId") ?? Parameters.Value<long?>("placeId")?.ToString(), out long PlaceId) || PlaceId <= 0)
                    throw new Exception("A place id is required");

                string Result = await account.JoinServer(PlaceId,
                    Parameters.Value<string>("jobId") ?? string.Empty,
                    Parameters.Value<bool?>("followUser") ?? false,
                    Parameters.Value<bool?>("joinVip") ?? false);

                // JoinServer reports failure in its return string rather than by throwing, so translate here —
                // the page should not have to string-match "ERROR:".
                if (Result != null && Result.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) throw new Exception(Result.Substring(Result.IndexOf(':') + 1).Trim());

                return Result;
            });

            // ---------- clipboard and links ----------
            //
            // Copying happens in C#, not in the page: a cookie must never be handed to the interface just so the
            // interface can hand it to the clipboard, and the app already clears secrets from the clipboard after
            // a delay. The page asks for "the cookie of this account" and never sees it.

            Bridge.Register("accounts.copy", Parameters =>
            {
                string What = (Parameters.Value<string>("what") ?? string.Empty).ToLowerInvariant();

                // Copying a selection puts one value per line on the clipboard. Looping instead would leave only
                // the last account's value there, silently.
                List<Account> Targets = FindMany(Parameters);

                bool Secret = What == "cookie" || What == "password";

                List<string> Values = Targets.Select(account =>
                {
                    switch (What)
                    {
                        case "username": return account.Username;
                        case "userid": return account.UserID.ToString();
                        case "profile": return $"https://www.roblox.com/users/{account.UserID}/profile";
                        case "alias": return account.Alias;
                        case "description": return account.Description;
                        case "group": return account.Group;
                        case "proxy": return account.GetField(AccountProxies.ProxyField);
                        case "cookie": return account.SecurityToken;
                        case "password": return account.Password;

                        default: throw new Exception($"Nothing called \"{What}\" can be copied");
                    }
                })
                .Where(Value => !string.IsNullOrEmpty(Value))
                .ToList();

                // Secrets go through the path that wipes the clipboard again after 45 seconds.
                return AccountManager.CopyForBridge(string.Join(Environment.NewLine, Values), Secret);
            });

            Bridge.Register("app.openUrl", Parameters =>
            {
                string Url = Parameters.Value<string>("url");

                // Only http(s), and only to the default browser: this must never become a way for the page to
                // start arbitrary programs.
                if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri Target) || (Target.Scheme != Uri.UriSchemeHttp && Target.Scheme != Uri.UriSchemeHttps))
                    throw new Exception("Only http and https links can be opened");

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Target.AbsoluteUri) { UseShellExecute = true });

                return true;
            });

            // Async on purpose. GetCSRFToken blocks for up to 15 seconds (25 through a proxy), and a handler
            // registered with the synchronous overload runs inline on the UI thread — so checking a selection of
            // twenty from the page froze the window for minutes. The work goes to a thread, the accounts are
            // paced so twenty checks are not twenty requests at once from one address, and the store is saved and
            // the interfaces told once at the end rather than per account.
            Bridge.Register("accounts.revalidate", async Parameters =>
            {
                List<Account> Targets = FindMany(Parameters);

                var Results = new List<object>();

                await Task.Run(async () =>
                {
                    for (int i = 0; i < Targets.Count; i++)
                    {
                        Account account = Targets[i];

                        // A CSRF token is the cheapest proof that the cookie still works; it is what every other
                        // operation starts with anyway.
                        bool Alive = account.GetCSRFToken(out _);

                        account.Valid = Alive;

                        Results.Add(new
                        {
                            username = account.Username,
                            valid = Alive,
                            reason = Alive ? "the cookie still works" : (account.LastAuthFailure ?? "Roblox rejected the cookie")
                        });

                        if (i < Targets.Count - 1) await Task.Delay(600);
                    }
                });

                AccountManager.SaveAccounts();
                AccountManager.Instance?.RefreshFromBridge();

                Forms.WebShell.NotifyAccountsChanged();

                // One account still answers in the shape the page has always read.
                dynamic First = Results.Count > 0 ? Results[0] : null;

                return Targets.Count == 1 && First != null
                    ? (object)new { valid = First.valid, reason = First.reason }
                    : new { checked_ = Results.Count, results = Results.ToArray() };
            });

            // ---------- adding accounts ----------
            //
            // All three routes go through the paths the old window already uses, so an account added from the new
            // interface is validated, de-duplicated and rate-limited exactly like one added from the old one.

            Bridge.Register("accounts.add", Parameters =>
            {
                string Cookie = Parameters.Value<string>("cookie");

                if (string.IsNullOrWhiteSpace(Cookie)) throw new Exception("Paste a .ROBLOSECURITY cookie first");

                Account Added = AccountManager.AddAccount(Cookie);

                if (Added == null) throw new Exception("Roblox did not accept that cookie — it is expired, incomplete, or from a signed-out session");

                // No explicit choice means the configured default, not whatever the model happens to start with.
                string Group = Parameters.Value<string>("group");

                if (string.IsNullOrWhiteSpace(Group) || Group == "All") Group = Groups.DefaultGroup;

                if (!string.IsNullOrWhiteSpace(Group) && Group != Added.Group)
                {
                    Added.Group = Group;

                    AccountManager.SaveAccounts();
                }

                Forms.WebShell.NotifyAccountsChanged();

                return Describe(Added);
            });

            Bridge.Register("accounts.addBulk", async Parameters =>
            {
                string Text = Parameters.Value<string>("text") ?? string.Empty;

                List<string> Cookies = Utilities.ExtractCookies(Text);

                if (Cookies.Count == 0) throw new Exception("No Roblox cookies found in that text");

                string Group = Parameters.Value<string>("group");

                if (string.IsNullOrWhiteSpace(Group) || Group == "All") Group = Groups.DefaultGroup;

                // Which accounts existed before, so the group can be applied to exactly the new ones.
                HashSet<long> Before = new HashSet<long>(AccountManager.GetAccountsSnapshot().Select(account => account.UserID));

                // The shared importer validates each cookie with a delay between them and retries the ones that
                // only hit a rate limit, which is the difference between importing 50 cookies and losing half.
                (int AddedCount, int FailedCount) = await AccountManager.Instance.ImportCookiesFromText(Text);

                if (!string.IsNullOrWhiteSpace(Group))
                {
                    foreach (Account account in AccountManager.GetAccountsSnapshot().Where(account => !Before.Contains(account.UserID)))
                        account.Group = Group;

                    AccountManager.SaveAccounts();
                }

                Forms.WebShell.NotifyAccountsChanged();

                return new { found = Cookies.Count, added = AddedCount, failed = FailedCount };
            });

            Bridge.Register("accounts.addBrowser", Parameters =>
            {
                // Opens the same Chromium window the old "Add account" button uses. It harvests the cookie once
                // the user finishes signing in, so there is nothing to await here.
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await new AccountBrowser().Login(); }
                    catch (Exception x) { Program.Logger.Error($"[Bridge] browser login failed: {x}"); }
                });

                return true;
            });

            // The old window's Tools menu is six variations on one thing: open the signed-in browser somewhere.
            // One method covers them all; the page decides the destination.
            Bridge.Register("accounts.openBrowser", Parameters =>
            {
                Account account = Find(Parameters) ?? throw new Exception("Account not found");
                string Url = Parameters.Value<string>("url");

                // The browser opens and lives on its own; awaiting it here would hold the page's call open for as
                // long as the user keeps the window.
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { _ = new AccountBrowser(account, string.IsNullOrWhiteSpace(Url) ? null : Url); }
                    catch (Exception x) { Program.Logger.Error($"[Bridge] browser for {account.Username} failed: {x}"); }
                });

                return true;
            });

            // "Follow" takes a username, not an id, and JoinServer carries the target's user id in its PlaceID
            // parameter when FollowUser is set — resolving here keeps that oddity out of the page.
            Bridge.Register("accounts.follow", async Parameters =>
            {
                Account account = Find(Parameters) ?? throw new Exception("Account not found");
                string Target = (Parameters.Value<string>("target") ?? string.Empty).Trim();

                if (Target.Length == 0) throw new Exception("Whose game should it join? Give a username.");

                if (!AccountManager.GetUserID(Target, out long UserId, out RestResponse Response))
                    throw new Exception($"No Roblox user called \"{Target}\" ({(int)Response.StatusCode})");

                string Result = await account.JoinServer(UserId, string.Empty, FollowUser: true);

                if (Result != null && Result.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    throw new Exception(Result.Substring(Result.IndexOf(':') + 1).Trim());

                return new { username = account.Username, target = Target, userId = UserId };
            });

            Bridge.Register("accounts.signOutOthers", Parameters =>
            {
                Account account = Find(Parameters) ?? throw new Exception("Account not found");

                // Internal: true keeps the old window's message boxes out of a call the page is already reporting.
                // It rotates the cookie, so the store is saved by the call itself.
                if (!account.LogOutOfOtherSessions(Internal: true))
                    throw new Exception("Roblox refused the sign-out. The account may need its PIN unlocked.");

                Forms.WebShell.NotifyAccountsChanged();

                return new { username = account.Username };
            });

            Bridge.Register("accounts.remove", Parameters =>
            {
                List<Account> Targets = FindMany(Parameters);

                lock (AccountManager.AccountsLock)
                    foreach (Account account in Targets) AccountManager.AccountsList.Remove(account);

                if (Targets.Count > 0) AccountManager.SaveAccounts();

                AccountManager.Instance?.RefreshFromBridge();

                Forms.WebShell.NotifyAccountsChanged();

                return new { removed = Targets.Count };
            });

            Bridge.Register("accounts.setAlias", Parameters =>
            {
                Account account = Find(Parameters) ?? throw new Exception("Account not found");

                account.Alias = Parameters.Value<string>("alias") ?? string.Empty;

                return Describe(account);
            });

            Bridge.Register("accounts.setDescription", Parameters =>
            {
                Account account = Find(Parameters) ?? throw new Exception("Account not found");

                account.Description = Parameters.Value<string>("description") ?? string.Empty;

                return Describe(account);
            });

            Bridge.Register("accounts.setGroup", Parameters =>
            {
                string Group = string.IsNullOrWhiteSpace(Parameters.Value<string>("group")) ? "Default" : Parameters.Value<string>("group").Trim();

                // The page lets the user type this one, so the reserved name has to be refused here rather than
                // only where groups are renamed.
                if (Group == ReservedGroup) throw new Exception(ReservedGroupMessage);

                // One or many. The page would otherwise loop, and every one of those calls writes the WHOLE
                // account store to disk — twenty accounts, twenty full saves.
                List<Account> Targets = FindMany(Parameters);

                foreach (Account account in Targets) account.Group = Group;

                if (Targets.Count > 0) AccountManager.SaveAccounts();

                AccountManager.Instance?.RefreshFromBridge();
                Forms.WebShell.NotifyAccountsChanged();

                return new { changed = Targets.Count, group = Group };
            });

            Bridge.Register("accounts.setField", Parameters =>
            {
                Account account = Find(Parameters) ?? throw new Exception("Account not found");

                string Name = Parameters.Value<string>("name");

                if (string.IsNullOrWhiteSpace(Name)) throw new Exception("A field name is required");

                string Value = Parameters.Value<string>("value");

                if (string.IsNullOrEmpty(Value)) account.RemoveField(Name);
                else account.SetField(Name, Value);

                return Describe(account);
            });

            // ---------- proxies ----------

            Bridge.Register("proxies.assign", Parameters =>
            {
                string[] Names = (Parameters["accounts"] as JArray)?.Select(Token => Token.Value<string>()).ToArray() ?? new string[0];
                string[] Proxies = (Parameters["proxies"] as JArray)?.Select(Token => Token.Value<string>()).Where(Text => !string.IsNullOrWhiteSpace(Text)).ToArray() ?? new string[0];

                if (Names.Length == 0) throw new Exception("No accounts given");

                string Invalid = Proxies.FirstOrDefault(Proxy => !ProxyConfig.TryParse(Proxy, out _));

                if (Invalid != null) throw new Exception($"Cannot parse proxy \"{Invalid}\"");

                List<Account> Accounts = AccountManager.GetAccountsSnapshot();
                int Assigned = 0;

                for (int i = 0; i < Names.Length; i++)
                {
                    Account account = Accounts.FirstOrDefault(Candidate => Candidate.Username == Names[i]);

                    if (account == null) continue;

                    // An empty proxy list means "clear", which is how the page asks to unassign.
                    if (Proxies.Length == 0)
                    {
                        account.RemoveField(AccountProxies.ProxyField);
                        account.RemoveField(AccountProxies.ExitIPField);
                    }
                    else
                        account.SetField(AccountProxies.ProxyField, Proxies[i % Proxies.Length]);

                    Assigned++;
                }

                AccountProxies.ForgetCaches();

                return new { assigned = Assigned, cleared = Proxies.Length == 0 };
            });

            // The proxies screen shows what the accounts are actually using. There is no separate proxy store:
            // a proxy exists because some account carries it, so the list is the accounts, grouped.
            Bridge.Register("proxies.list", Parameters =>
            {
                return AccountManager.GetAccountsSnapshot()
                    .Select(account => new { account, Raw = account.GetField(AccountProxies.ProxyField) })
                    .Where(Pair => !string.IsNullOrWhiteSpace(Pair.Raw))
                    .GroupBy(Pair => Pair.Raw)
                    .Select(Group =>
                    {
                        ProxyConfig.TryParse(Group.Key, out ProxyConfig Proxy);

                        // The exit address is remembered on the account, so a restart does not lose what was checked.
                        string IP = Group.Select(Pair => Pair.account.GetField(AccountProxies.ExitIPField))
                                         .FirstOrDefault(Value => !string.IsNullOrWhiteSpace(Value));

                        return new
                        {
                            proxy = Proxy?.ToString() ?? "invalid",
                            scheme = Proxy?.Scheme ?? "?",
                            host = Proxy != null ? $"{Proxy.Host}:{Proxy.Port}" : Group.Key,
                            accounts = Group.Count(),
                            exitIP = Proxy == null ? null : IP,
                            // never checked is not the same as dead, and the page says so
                            alive = Proxy == null ? (bool?)false : (string.IsNullOrEmpty(IP) ? (bool?)null : true)
                        };
                    })
                    .OrderBy(Row => Row.host)
                    .ToArray();
            });

            // Checking happens here rather than in the page: the raw proxy carries credentials and never leaves
            // the app, and the answer is written back onto the accounts using it.
            Bridge.Register("proxies.check", async Parameters =>
            {
                string Wanted = Parameters.Value<string>("proxy");

                if (string.IsNullOrWhiteSpace(Wanted)) throw new Exception("A proxy is required");

                List<Account> Using = AccountManager.GetAccountsSnapshot()
                    .Where(account =>
                    {
                        string Raw = account.GetField(AccountProxies.ProxyField);

                        return !string.IsNullOrWhiteSpace(Raw) && ProxyConfig.TryParse(Raw, out ProxyConfig Parsed) && Parsed.ToString() == Wanted;
                    }).ToList();

                if (Using.Count == 0) throw new Exception("No account uses that proxy any more");

                ProxyConfig.TryParse(Using[0].GetField(AccountProxies.ProxyField), out ProxyConfig Proxy);

                string IP = await ProxyExitIP.GetAsync(Proxy, Refresh: true);

                foreach (Account account in Using)
                    if (string.IsNullOrEmpty(IP)) account.RemoveField(AccountProxies.ExitIPField);
                    else account.SetField(AccountProxies.ExitIPField, IP);

                return new { proxy = Wanted, exitIP = IP, alive = !string.IsNullOrEmpty(IP), accounts = Using.Count };
            });

            Bridge.Register("proxies.test", async Parameters =>
            {
                string Raw = Parameters.Value<string>("proxy");

                if (!ProxyConfig.TryParse(Raw, out ProxyConfig Proxy)) throw new Exception("Cannot parse that proxy");

                string IP = await ProxyExitIP.GetAsync(Proxy, Refresh: true);

                return new { proxy = Proxy.ToString(), exitIP = IP, alive = !string.IsNullOrEmpty(IP) };
            });

            // Launching several accounts is not a loop the page should run: it has to be paced so Roblox is not
            // asked for a pile of tickets from one address at once, it has to be cancellable, and each account may
            // have a destination of its own. All of that lives in the app; the page starts it and follows the
            // launch.progress / launch.batch events.
            Bridge.Register("accounts.launchBatch", Parameters =>
            {
                string[] Names = (Parameters["accounts"] as JArray)?.Select(Token => Token.Value<string>()).ToArray() ?? new string[0];

                if (!long.TryParse(Parameters.Value<string>("placeId") ?? Parameters.Value<long?>("placeId")?.ToString(), out long PlaceId) || PlaceId <= 0)
                    throw new Exception("A place id is required");

                List<Account> Accounts = AccountManager.GetAccountsSnapshot()
                    .Where(account => Names.Contains(account.Username))
                    .ToList();

                if (Accounts.Count == 0) throw new Exception("None of those accounts are in the manager any more");

                return BatchLauncher.Start(Accounts, PlaceId,
                    Parameters.Value<string>("jobId") ?? string.Empty,
                    Parameters.Value<bool?>("joinVip") ?? false);
            });

            Bridge.Register("accounts.cancelLaunch", _ => BatchLauncher.Cancel());

            Bridge.Register("accounts.launchState", _ => BatchLauncher.State());

            // ---------- free items ----------
            //
            // The work is minutes long, so the page starts it and then follows the freeitems.progress and
            // freeitems.done events rather than waiting on this call.

            Bridge.Register("freeitems.start", Parameters =>
            {
                string[] Names = (Parameters["accounts"] as JArray)?.Select(Token => Token.Value<string>()).ToArray() ?? new string[0];

                List<Account> Accounts = AccountManager.GetAccountsSnapshot()
                    .Where(account => Names.Contains(account.Username))
                    .ToList();

                if (Accounts.Count == 0) throw new Exception("None of those accounts are in the manager any more");

                List<Account> Usable = Accounts.Where(account => account.Valid).ToList();

                if (Usable.Count == 0) throw new Exception("None of the picked accounts have a working cookie");

                return FreeItemsRunner.Start(Usable);
            });

            Bridge.Register("freeitems.stop", _ => FreeItemsRunner.Stop());

            Bridge.Register("freeitems.state", _ => FreeItemsRunner.State());

            // ---------- settings ----------

            Bridge.Register("settings.get", Parameters =>
            {
                string SectionName = Parameters.Value<string>("section") ?? "General";
                IniSection Section = SectionOf(SectionName);
                string Key = Parameters.Value<string>("key");

                if (!string.IsNullOrEmpty(Key)) return Section.Exists(Key) ? Section.Get(Key) : null;

                // Whole section: the settings screen renders whatever is there rather than a hard-coded list.
                return Section.Properties.ToDictionary(Property => Property.Name, Property => (object)Property.Value);
            });

            // The settings screen shows an explanation under every switch, and those explanations already exist:
            // they are the comments written into RAMSettings.ini beside each value. One source, not two.
            Bridge.Register("settings.describe", Parameters =>
            {
                IniSection Section = SectionOf(Parameters.Value<string>("section") ?? "General");

                return Section.Properties.ToDictionary(
                    Property => Property.Name,
                    Property => (object)new { value = Property.Value, comment = Property.Comment ?? string.Empty });
            });

            Bridge.Register("settings.set", Parameters =>
            {
                IniSection Section = SectionOf(Parameters.Value<string>("section") ?? "General");
                string Key = Parameters.Value<string>("key");

                if (string.IsNullOrWhiteSpace(Key)) throw new Exception("A key is required");

                Section.Set(Key, Parameters.Value<string>("value") ?? string.Empty);

                AccountManager.IniSettings.Save("RAMSettings.ini");
                AccountProxies.LoadSettings(); // applies the client/proxy switches without a restart

                // Multi Roblox is not a value anything reads later: it decides whether the manager holds
                // ROBLOX_singletonMutex, and that has to happen now. Roblox takes the mutex itself the moment a
                // client starts, so switching this on with one already open cannot work until everything is closed.
                if (Key.Equals("EnableMultiRbx", StringComparison.OrdinalIgnoreCase) && AccountManager.Instance != null)
                {
                    bool Applied = AccountManager.Instance.UpdateMultiRoblox();

                    // Saved either way — the switch stays where the user put it — but they are told why it is
                    // not in force yet instead of finding out when the second client silently refuses to start.
                    if (!Applied)
                        return new { ok = true, warning = "Saved, but Roblox is already running: close every client and restart the manager for multi Roblox to take effect" };
                }

                return new { ok = true };
            });

            // ---------- games ----------

            Bridge.Register("games.info", async Parameters =>
            {
                if (!long.TryParse(Parameters.Value<string>("placeId") ?? Parameters.Value<long?>("placeId")?.ToString(), out long PlaceId) || PlaceId <= 0)
                    throw new Exception("A place id is required");

                Game game = new Game(PlaceId);

                // Details arrive from a batched request; give it a moment rather than returning "Unknown".
                Task Ready = game.WaitForDetails();

                await Task.WhenAny(Ready, Task.Delay(3000));

                // That batch goes through multiget-place-details, which Roblox only answers with a signed-in
                // cookie — so with no valid account it never arrives and the card used to read "Unknown" for
                // every place. The universe endpoint is public, and everything the card shows hangs off the
                // universe, so it is asked directly when the batch has nothing.
                long Universe = game.Details?.universeId ?? 0;

                if (Universe <= 0)
                    try
                    {
                        RestResponse Lookup = await AccountManager.ApisClient.ExecuteAsync(
                            new RestRequest($"universes/v1/places/{PlaceId}/universe"));

                        if (Lookup.IsSuccessful && Lookup.Content.TryParseJson(out JObject Found))
                            Universe = Found.Value<long?>("universeId") ?? 0;
                    }
                    catch (Exception x) { Program.Logger.Debug($"[Bridge] universe of {PlaceId}: {x.Message}"); }

                // game.Details is never null — a batch that could not run (no cookie) leaves the "Unknown"
                // placeholder — so its placeholder name must be treated as absent, or the real name from the
                // public endpoint below never gets a chance to replace it.
                string Name = game.Details != null && game.Details.name != "Unknown" ? game.Details.name : null;
                string Creator = game.Details?.builder;
                long Playing = 0, Favorites = 0, Likes = 0;

                // Players / favourites / likes, and the name when the batch could not supply it. All optional:
                // a failure leaves what is already known rather than failing the whole lookup.
                if (Universe > 0)
                {
                    try
                    {
                        RestResponse Games = await AccountManager.GamesClient.ExecuteAsync(new RestRequest($"v1/games?universeIds={Universe}"));

                        if (Games.IsSuccessful && Games.Content.TryParseJson(out JObject Parsed) && Parsed["data"] is JArray Data && Data.Count > 0)
                        {
                            Playing = Data[0].Value<long?>("playing") ?? 0;
                            Favorites = Data[0].Value<long?>("favoritedCount") ?? 0;

                            if (string.IsNullOrEmpty(Name)) Name = Data[0].Value<string>("name");
                            if (string.IsNullOrEmpty(Creator)) Creator = Data[0]["creator"]?.Value<string>("name");
                        }

                        RestResponse Votes = await AccountManager.GamesClient.ExecuteAsync(new RestRequest($"v1/games/votes?universeIds={Universe}"));

                        if (Votes.IsSuccessful && Votes.Content.TryParseJson(out JObject ParsedVotes) && ParsedVotes["data"] is JArray VoteData && VoteData.Count > 0)
                            Likes = VoteData[0].Value<long?>("upVotes") ?? 0;
                    }
                    catch (Exception x) { Program.Logger.Debug($"[Bridge] game stats for {PlaceId}: {x.Message}"); }
                }

                return new
                {
                    placeId = PlaceId,
                    name = string.IsNullOrEmpty(Name) ? "Unknown place" : Name,
                    creator = Creator,
                    icon = game.ImageUrl,
                    universeId = Universe,
                    playing = Playing,
                    favorites = Favorites,
                    likes = Likes,
                    known = Universe > 0
                };
            });

            // ---------- the server list ----------

            Bridge.Register("servers.list", async Parameters =>
            {
                if (!long.TryParse(Parameters.Value<string>("placeId") ?? Parameters.Value<long?>("placeId")?.ToString(), out long PlaceId) || PlaceId <= 0)
                    throw new Exception("A place id is required");

                // One page is what a person can look at; the old window's endless paging is what gets rate limited.
                RestResponse Response = await AccountManager.GamesClient.ExecuteAsync(
                    new RestRequest($"v1/games/{PlaceId}/servers/public?sortOrder=Asc&limit=100"));

                if (!Response.IsSuccessful)
                    throw new Exception(Response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "Roblox is rate limiting the server list; try again in a moment"
                        : $"Roblox refused the server list ({(int)Response.StatusCode})");

                ServersInfo Info = JsonConvert.DeserializeObject<ServersInfo>(Response.Content) ?? new ServersInfo();

                return Info.data.Select(Server => new
                {
                    id = Server.id,
                    playing = Server.playing,
                    maxPlayers = Server.maxPlayers,
                    ping = Server.ping,
                    fps = Server.fps
                }).ToArray();
            });

            // ---------- games you come back to ----------

            Bridge.Register("games.recent", Parameters =>
                (AccountManager.RecentGames ?? new List<Game>())
                    .Where(Recent => Recent?.Details != null)
                    .Select(Recent => new { placeId = Recent.Details.placeId, name = Recent.Details.name, icon = Recent.ImageUrl })
                    .Reverse()
                    .ToArray());

            Bridge.Register("games.favourites", Parameters => Favourites()
                .Select(PlaceId =>
                {
                    Game Known = (AccountManager.RecentGames ?? new List<Game>())
                        .FirstOrDefault(Recent => Recent?.Details?.placeId == PlaceId);

                    return new { placeId = PlaceId, name = Known?.Details?.name ?? PlaceId.ToString(), icon = Known?.ImageUrl };
                })
                .ToArray());

            Bridge.Register("games.favourite", Parameters =>
            {
                if (!long.TryParse(Parameters.Value<string>("placeId") ?? Parameters.Value<long?>("placeId")?.ToString(), out long PlaceId) || PlaceId <= 0)
                    throw new Exception("A place id is required");

                List<long> Saved = Favourites();
                bool On = Parameters.Value<bool?>("on") ?? !Saved.Contains(PlaceId);

                if (On && !Saved.Contains(PlaceId)) Saved.Add(PlaceId);
                if (!On) Saved.Remove(PlaceId);

                AccountManager.General.Set("FavouriteGames", string.Join(",", Saved));
                AccountManager.IniSettings.Save("RAMSettings.ini");

                return new { placeId = PlaceId, favourite = On };
            });
        }

        /// <summary>Place ids the user pinned. Kept in the ini beside everything else rather than in a file of its own.</summary>
        private static List<long> Favourites()
        {
            string Raw = AccountManager.General.Exists("FavouriteGames") ? AccountManager.General.Get("FavouriteGames") : string.Empty;

            return (Raw ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Text => long.TryParse(Text.Trim(), out long Id) ? Id : 0)
                .Where(Id => Id > 0)
                .Distinct()
                .ToList();
        }

        /// <summary>What the page is allowed to know about an account. Deliberately no cookie, ever.</summary>
        private static object Describe(Account account) => new
        {
            username = account.Username,
            userId = account.UserID,
            alias = account.Alias,
            description = account.Description,
            group = string.IsNullOrEmpty(account.Group) ? "Default" : account.Group,
            valid = account.Valid,
            lastUse = account.LastUse,
            presence = account.Presence?.userPresenceType.ToString() ?? "Unknown",
            location = account.Presence?.lastLocation,
            proxy = MaskProxy(account.GetField(AccountProxies.ProxyField)),
            exitIP = account.GetField(AccountProxies.ExitIPField),
            fields = account.Fields
        };

        /// <summary>Proxy credentials are as sensitive as the proxy itself; the page only needs to identify it.</summary>
        private static string MaskProxy(string Raw) => string.IsNullOrWhiteSpace(Raw) ? null : (ProxyConfig.TryParse(Raw, out ProxyConfig Proxy) ? Proxy.ToString() : "invalid");

        /// <summary>
        /// "All" is the interface's filter for every account, not a group. A real group carrying that name would
        /// be unreachable by any filter and could never be renamed back, so no path may write it.
        /// </summary>
        private const string ReservedGroup = "All";

        private const string ReservedGroupMessage = "\"All\" is the filter for every account, so a group cannot be called that";

        /// <summary>
        /// The accounts a command applies to: an "accounts" array when the page acted on a multi-selection, and
        /// the single "username" otherwise. Unknown names are dropped rather than failing the whole call — an
        /// account removed a moment ago should not stop the other nineteen from moving.
        /// </summary>
        private static List<Account> FindMany(JObject Parameters)
        {
            string[] Names = (Parameters["accounts"] as JArray)?.Select(Token => Token.Value<string>()).ToArray();

            if (Names != null && Names.Length > 0)
            {
                List<Account> Found = AccountManager.GetAccountsSnapshot()
                    .Where(account => Names.Contains(account.Username))
                    .ToList();

                if (Found.Count == 0) throw new Exception("None of those accounts are in the manager any more");

                return Found;
            }

            return new List<Account> { Find(Parameters) ?? throw new Exception("Account not found") };
        }

        private static Account Find(JObject Parameters)
        {
            string Username = Parameters.Value<string>("username");
            long UserId = Parameters.Value<long?>("userId") ?? 0;

            return AccountManager.GetAccountsSnapshot().FirstOrDefault(account =>
                (!string.IsNullOrEmpty(Username) && account.Username == Username) || (UserId > 0 && account.UserID == UserId));
        }

        private static IniSection SectionOf(string Name)
        {
            switch ((Name ?? string.Empty).ToLowerInvariant())
            {
                case "developer": return AccountManager.Developer;
                case "webserver": return AccountManager.WebServer;
                case "watcher": return AccountManager.Watcher;
                case "accountcontrol": return AccountManager.AccountControl;
                default: return AccountManager.General;
            }
        }
    }
}

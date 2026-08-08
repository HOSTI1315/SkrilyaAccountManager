using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes.Android
{
    /// <summary>
    /// Injects .ROBLOSECURITY into Roblox's own Chromium WebView cookie database. The database schema is read
    /// from the device every time: Chromium adds/removes columns between Android/WebView releases, so shipping a
    /// fixed INSERT is intentionally avoided.
    /// </summary>
    public sealed class CookieInjector
    {
        private static readonly Regex SafePackage = new Regex(@"^[A-Za-z0-9_.]+$", RegexOptions.Compiled);
        private static readonly string ApplicationDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkrilyaAccountManager");
        private static readonly byte[] BackupEntropy = Encoding.UTF8.GetBytes(
            "SkrilyaAccountManager.AndroidCookieBackup.v1");
        private const int BackupRetentionPerSerial = 3;

        private readonly AdbClient Adb;
        private readonly string PackageName;

        public bool BackupCookieStore { get; set; } = true;
        public string BackupDirectory { get; set; } = Path.Combine(ApplicationDataDirectory, "android-backups");
        public string WorkingDirectory { get; set; } = Path.Combine(ApplicationDataDirectory, "android-staging");

        public CookieInjector(AdbClient adb, string packageName)
        {
            Adb = adb ?? throw new ArgumentNullException(nameof(adb));
            if (string.IsNullOrWhiteSpace(packageName) || !SafePackage.IsMatch(packageName))
                throw new ArgumentException("invalid Android package name", nameof(packageName));

            PackageName = packageName;
        }

        public string CookieDatabasePath => $"/data/data/{PackageName}/app_webview/Default/Cookies";

        public async Task InjectAsync(string securityToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(securityToken)) throw new ArgumentException("account has no .ROBLOSECURITY cookie", nameof(securityToken));
            if (await Adb.EnsureRootAsync(cancellationToken).ConfigureAwait(false) == AdbRootMode.None)
                throw new InvalidOperationException($"{Adb.Serial}: root is required for Android cookie injection");

            await Adb.ForceStopAsync(PackageName, cancellationToken).ConfigureAwait(false);
            await EnsureCookieStoreAsync(cancellationToken).ConfigureAwait(false);

            if (BackupCookieStore)
                await TryBackupAsync(cancellationToken).ConfigureAwait(false);

            Exception LastError = null;

            for (int Attempt = 1; Attempt <= 3; Attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Adb.ForceStopAsync(PackageName, cancellationToken).ConfigureAwait(false);

                try
                {
                    string Schema = await ReadSchemaAsync(cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<string> Columns = ParseCookieColumns(Schema);

                    if (Columns.Count == 0)
                        throw new InvalidOperationException("Chromium cookies table was not found in the device schema");

                    string Sql = BuildInjectSql(Columns, securityToken);
                    AdbCommandResult Result = await ExecuteSqlFileAsync(Sql, cancellationToken).ConfigureAwait(false);

                    if (Result.CombinedOutput.Contains("rows=1", StringComparison.OrdinalIgnoreCase)) return;

                    LastError = new InvalidOperationException($"sqlite did not confirm the cookie row (attempt {Attempt}): {TrimDiagnostic(Redact(Result.CombinedOutput, securityToken))}");
                }
                catch (Exception Ex) when (!(Ex is OperationCanceledException))
                {
                    LastError = Ex;
                }

                if (Attempt < 3) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Failed to inject .ROBLOSECURITY on {Adb.Serial} after 3 attempts", LastError);
        }

        /// <summary>Removes the secret when an emulator slot is handed to another account.</summary>
        public async Task ClearAsync(CancellationToken cancellationToken = default)
        {
            await Adb.ForceStopAsync(PackageName, cancellationToken).ConfigureAwait(false);
            if (!await Adb.RootFileExistsAsync(CookieDatabasePath, cancellationToken).ConfigureAwait(false)) return;

            const string Sql = "DELETE FROM cookies WHERE host_key='.roblox.com' AND name='.ROBLOSECURITY';\nSELECT 'rows='||count(*) FROM cookies WHERE host_key='.roblox.com' AND name='.ROBLOSECURITY';\n";
            await ExecuteSqlFileAsync(Sql, cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureCookieStoreAsync(CancellationToken cancellationToken)
        {
            if (await Adb.RootFileExistsAsync(CookieDatabasePath, cancellationToken).ConfigureAwait(false)) return;

            AdbCommandResult Warm = await Adb.ShellAsync(
                $"monkey -p {AdbClient.QuoteShell(PackageName)} -c android.intent.category.LAUNCHER 1",
                cancellationToken).ConfigureAwait(false);

            DateTime Until = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < Until)
            {
                if (await Adb.RootFileExistsAsync(CookieDatabasePath, cancellationToken).ConfigureAwait(false))
                {
                    await Adb.ForceStopAsync(PackageName, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }

            await Adb.ForceStopAsync(PackageName, cancellationToken).ConfigureAwait(false);
            throw new FileNotFoundException($"Roblox did not create its WebView cookie store within 45 seconds. monkey: {TrimDiagnostic(Warm.CombinedOutput)}", CookieDatabasePath);
        }

        private async Task<string> ReadSchemaAsync(CancellationToken cancellationToken)
        {
            AdbCommandResult Schema = await Adb.RootShellAsync(
                $"sqlite3 {AdbClient.QuoteShell(CookieDatabasePath)} {AdbClient.QuoteShell(".schema cookies")} 2>&1",
                cancellationToken).ConfigureAwait(false);

            if (!Schema.Success || string.IsNullOrWhiteSpace(Schema.StandardOutput) ||
                Schema.CombinedOutput.Contains("not found", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"sqlite3 could not read the cookies schema: {TrimDiagnostic(Schema.CombinedOutput)}");

            return Schema.StandardOutput;
        }

        private async Task<AdbCommandResult> ExecuteSqlFileAsync(string sql, CancellationToken cancellationToken)
        {
            string Id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(WorkingDirectory);
            CleanupStaleFiles(WorkingDirectory, "sam-android-cookie-*.sql", TimeSpan.FromMinutes(30));

            // Keep the plaintext token inside the app's user-local data directory rather than the shared %TEMP%.
            // The normal path deletes it in finally; stale cleanup removes a file left behind by a hard crash.
            string Local = Path.Combine(WorkingDirectory, $"sam-android-cookie-{Id}.sql");
            string Remote = $"/data/local/tmp/sam-android-cookie-{Id}.sql";

            try
            {
                await File.WriteAllTextAsync(Local, sql, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                AdbCommandResult Push = await Adb.PushAsync(Local, Remote, cancellationToken).ConfigureAwait(false);
                if (!Push.Success) throw new IOException($"adb push failed: {TrimDiagnostic(Push.CombinedOutput)}");

                return await Adb.RootShellAsync(
                    $"sqlite3 {AdbClient.QuoteShell(CookieDatabasePath)} < {AdbClient.QuoteShell(Remote)} 2>&1",
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(Local); } catch { }
                try { await Adb.RootShellAsync($"rm -f {AdbClient.QuoteShell(Remote)}", CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }

        private async Task TryBackupAsync(CancellationToken cancellationToken)
        {
            string Id = Guid.NewGuid().ToString("N");
            string Remote = $"/data/local/tmp/sam-cookies-backup-{Id}.db";
            string PlainLocal = null;

            try
            {
                Directory.CreateDirectory(BackupDirectory);
                Directory.CreateDirectory(WorkingDirectory);
                CleanupStaleFiles(WorkingDirectory, "sam-android-backup-*.db", TimeSpan.FromMinutes(30));
                CleanupStaleFiles(BackupDirectory, "*.dpapi.tmp-*", TimeSpan.FromMinutes(30));

                // Previous builds wrote raw SQLite databases here. Protect them before creating another backup;
                // a failed migration leaves the source in place rather than destroying the only copy.
                await ProtectLegacyBackupsAsync(cancellationToken).ConfigureAwait(false);

                string SafeSerial = Regex.Replace(Adb.Serial, @"[^A-Za-z0-9_.-]", "_");
                string Final = Path.Combine(
                    BackupDirectory,
                    $"Cookies-{SafeSerial}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Id.Substring(0, 8)}.db.dpapi");
                PlainLocal = Path.Combine(WorkingDirectory, $"sam-android-backup-{Id}.db");

                AdbCommandResult Copy = await Adb.RootShellAsync(
                    $"cp {AdbClient.QuoteShell(CookieDatabasePath)} {AdbClient.QuoteShell(Remote)} && chmod 644 {AdbClient.QuoteShell(Remote)}",
                    cancellationToken).ConfigureAwait(false);
                if (!Copy.Success) return;

                AdbCommandResult Pull = await Adb.PullAsync(Remote, PlainLocal, cancellationToken).ConfigureAwait(false);
                if (!Pull.Success)
                    throw new IOException($"adb pull failed: {TrimDiagnostic(Pull.CombinedOutput)}");

                await ProtectBackupFileAsync(PlainLocal, Final, cancellationToken).ConfigureAwait(false);
                RotateBackups(SafeSerial);
            }
            catch (Exception Ex) when (!(Ex is OperationCanceledException))
            {
                Program.Logger.Warn($"[Android] cookie-store backup failed on {Adb.Serial}: {Ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(PlainLocal))
                    try { File.Delete(PlainLocal); } catch { }

                try { await Adb.RootShellAsync($"rm -f {AdbClient.QuoteShell(Remote)}", CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }

        private async Task ProtectLegacyBackupsAsync(CancellationToken cancellationToken)
        {
            foreach (string Legacy in Directory.GetFiles(BackupDirectory, "Cookies-*.db", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    string Protected = Legacy + ".dpapi";
                    await ProtectBackupFileAsync(Legacy, Protected, cancellationToken).ConfigureAwait(false);
                    File.Delete(Legacy);
                }
                catch (Exception Ex) when (!(Ex is OperationCanceledException))
                {
                    Program.Logger.Warn($"[Android] could not DPAPI-protect legacy cookie backup {Path.GetFileName(Legacy)}: {Ex.Message}");
                }
            }
        }

        private static async Task ProtectBackupFileAsync(string plainPath, string protectedPath, CancellationToken cancellationToken)
        {
            byte[] Plain = await File.ReadAllBytesAsync(plainPath, cancellationToken).ConfigureAwait(false);
            string Temp = protectedPath + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                byte[] Protected = ProtectedData.Protect(Plain, BackupEntropy, DataProtectionScope.CurrentUser);
                await File.WriteAllBytesAsync(Temp, Protected, cancellationToken).ConfigureAwait(false);
                File.Move(Temp, protectedPath, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(Plain);
                try { File.Delete(Temp); } catch { }
            }
        }

        private void RotateBackups(string safeSerial)
        {
            try
            {
                FileInfo[] Backups = new DirectoryInfo(BackupDirectory)
                    .GetFiles($"Cookies-{safeSerial}-*.db.dpapi", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File => File.LastWriteTimeUtc)
                    .ThenByDescending(File => File.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (FileInfo Old in Backups.Skip(BackupRetentionPerSerial))
                    try { Old.Delete(); }
                    catch (Exception Ex) { Program.Logger.Warn($"[Android] could not rotate backup {Old.Name}: {Ex.Message}"); }
            }
            catch (Exception Ex)
            {
                Program.Logger.Warn($"[Android] backup rotation failed for {Adb.Serial}: {Ex.Message}");
            }
        }

        private static void CleanupStaleFiles(string directory, string pattern, TimeSpan olderThan)
        {
            DateTime Cutoff = DateTime.UtcNow - olderThan;

            try
            {
                foreach (string FilePath in Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                    try
                    {
                        if (File.GetLastWriteTimeUtc(FilePath) < Cutoff) File.Delete(FilePath);
                    }
                    catch { }
            }
            catch { }
        }

        internal static IReadOnlyList<string> ParseCookieColumns(string schema)
        {
            if (string.IsNullOrWhiteSpace(schema)) return Array.Empty<string>();

            Match Create = Regex.Match(schema, @"CREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+(?:[`""\[]?cookies[`""\]]?)\s*\(", RegexOptions.IgnoreCase);
            if (!Create.Success) return Array.Empty<string>();

            int Open = schema.IndexOf('(', Create.Index);
            int Close = FindClosingParen(schema, Open);
            if (Open < 0 || Close <= Open) return Array.Empty<string>();

            List<string> Columns = new List<string>();
            foreach (string Part in SplitTopLevel(schema.Substring(Open + 1, Close - Open - 1)))
            {
                string Definition = Part.Trim();
                if (Definition.Length == 0 || Regex.IsMatch(Definition, @"^(?:CONSTRAINT|PRIMARY|UNIQUE|CHECK|FOREIGN)\b", RegexOptions.IgnoreCase)) continue;

                string Name = ReadIdentifier(Definition);
                if (!string.IsNullOrWhiteSpace(Name)) Columns.Add(Name);
            }

            return Columns;
        }

        internal static string BuildInjectSql(IReadOnlyList<string> columns, string securityToken)
        {
            if (columns == null || columns.Count == 0) throw new ArgumentException("cookies schema has no columns", nameof(columns));

            long Now = ChromiumMicros(DateTimeOffset.UtcNow);
            long Expires = ChromiumMicros(DateTimeOffset.UtcNow.AddYears(10));

            string ColumnList = string.Join(",", columns.Select(QuoteIdentifier));
            string ValueList = string.Join(",", columns.Select(Column => SqlValue(Column, securityToken, Now, Expires)));

            return
                "BEGIN IMMEDIATE;\n" +
                "DELETE FROM cookies WHERE host_key='.roblox.com' AND name='.ROBLOSECURITY';\n" +
                $"INSERT INTO cookies ({ColumnList}) VALUES ({ValueList});\n" +
                "COMMIT;\n" +
                "SELECT 'rows='||count(*) FROM cookies WHERE host_key='.roblox.com' AND name='.ROBLOSECURITY';\n";
        }

        private static string SqlValue(string column, string token, long now, long expires)
        {
            switch ((column ?? string.Empty).ToLowerInvariant())
            {
                case "creation_utc":
                case "last_access_utc":
                case "last_update_utc": return now.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "expires_utc": return expires.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "host_key": return SqlString(".roblox.com");
                case "name": return SqlString(".ROBLOSECURITY");
                case "value": return SqlString(token);
                case "encrypted_value": return "X''";
                case "path": return SqlString("/");
                case "is_secure":
                case "is_httponly":
                case "is_persistent":
                case "has_expires": return "1";
                case "source_scheme": return "2";
                case "source_port": return "443";
                case "samesite": return "-1";
                default: return "0";
            }
        }

        private static long ChromiumMicros(DateTimeOffset time) => checked(time.ToUnixTimeMilliseconds() * 1000L + 11644473600000000L);
        private static string SqlString(string value) => "'" + (value ?? string.Empty).Replace("'", "''") + "'";
        private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        private static int FindClosingParen(string text, int open)
        {
            if (open < 0) return -1;

            int Depth = 0;
            char Quote = '\0';

            for (int i = open; i < text.Length; i++)
            {
                char C = text[i];

                if (Quote != '\0')
                {
                    if (C == Quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == Quote) { i++; continue; }
                        Quote = '\0';
                    }
                    continue;
                }

                if (C == '\'' || C == '"' || C == '`') { Quote = C; continue; }
                if (C == '(') Depth++;
                else if (C == ')' && --Depth == 0) return i;
            }

            return -1;
        }

        private static IEnumerable<string> SplitTopLevel(string body)
        {
            int Start = 0;
            int Depth = 0;
            char Quote = '\0';

            for (int i = 0; i < body.Length; i++)
            {
                char C = body[i];

                if (Quote != '\0')
                {
                    if (C == Quote)
                    {
                        if (i + 1 < body.Length && body[i + 1] == Quote) { i++; continue; }
                        Quote = '\0';
                    }
                    continue;
                }

                if (C == '\'' || C == '"' || C == '`') { Quote = C; continue; }
                if (C == '(') Depth++;
                else if (C == ')') Depth--;
                else if (C == ',' && Depth == 0)
                {
                    yield return body.Substring(Start, i - Start);
                    Start = i + 1;
                }
            }

            if (Start <= body.Length) yield return body.Substring(Start);
        }

        private static string ReadIdentifier(string definition)
        {
            if (definition.StartsWith("["))
            {
                int End = definition.IndexOf(']');
                return End > 1 ? definition.Substring(1, End - 1) : null;
            }

            if (definition.StartsWith("\"") || definition.StartsWith("`"))
            {
                char Quote = definition[0];
                int End = definition.IndexOf(Quote, 1);
                return End > 1 ? definition.Substring(1, End - 1) : null;
            }

            Match Name = Regex.Match(definition, @"^([A-Za-z_][A-Za-z0-9_]*)");
            return Name.Success ? Name.Groups[1].Value : null;
        }

        private static string TrimDiagnostic(string text)
        {
            string Clean = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return Clean.Length <= 500 ? Clean : Clean.Substring(0, 500) + "...";
        }

        private static string Redact(string text, string secret) =>
            string.IsNullOrEmpty(secret) ? text : (text ?? string.Empty).Replace(secret, "<redacted>", StringComparison.Ordinal);
    }
}

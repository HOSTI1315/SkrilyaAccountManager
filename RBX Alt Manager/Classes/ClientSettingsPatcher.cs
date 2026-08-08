using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System.IO;

namespace RBX_Alt_Manager.Classes
{
    public static class ClientSettingsPatcher
    {
        public static void PatchSettings()
        {
            DirectoryInfo VersionFolder = null;

            string PinnedDirectory = VersionManager.EffectiveVersionDirectory();
            if (!string.IsNullOrEmpty(PinnedDirectory) && Directory.Exists(PinnedDirectory))
                VersionFolder = new DirectoryInfo(PinnedDirectory);

            object RegistryValue = VersionFolder == null ? Registry.ClassesRoot.OpenSubKey(@"roblox\DefaultIcon")?.GetValue("") : null;

            if (VersionFolder == null && RegistryValue != null && RegistryValue is string RobloxPath)
                VersionFolder = Directory.GetParent(RobloxPath);

            if (VersionFolder == null || !VersionFolder.Exists) { Program.Logger.Error("Can't patch ClientAppSettings, folder doesn't exist"); return; }
            if (!VersionFolder.Name.StartsWith("version-")) { Program.Logger.Error("Can't patch ClientAppSettings, folder doesn't start with 'version-'"); return; }
            if (!File.Exists(Path.Combine(VersionFolder.FullName, "RobloxPlayerLauncher.exe"))) { Program.Logger.Error("Can't patch ClientAppSettings, RobloxPlayerBeta.exe not found"); return; }

            DirectoryInfo SettingsFolder = new DirectoryInfo(Path.Combine(VersionFolder.FullName, "ClientSettings"));

            if (!SettingsFolder.Exists) SettingsFolder.Create();

            string CustomFN = AccountManager.General.Exists("CustomClientSettings") ? AccountManager.General.Get<string>("CustomClientSettings") : string.Empty;
            string SettingsFN = Path.Combine(SettingsFolder.FullName, "ClientAppSettings.json");

            if (!string.IsNullOrEmpty(CustomFN) && File.Exists(CustomFN))
            {
                if (File.Exists(SettingsFN)) File.Delete(SettingsFN); // File.Copy throws if the destination exists

                File.Copy(CustomFN, SettingsFN);

                return;
            }

            bool UnlockFPS = AccountManager.General.Get<bool>("UnlockFPS");
            bool LowGraphics = AccountManager.General.Exists("PerfLowGraphics") && AccountManager.General.Get<bool>("PerfLowGraphics");

            if (!UnlockFPS && !LowGraphics) return;

            // Merge into any existing ClientAppSettings.json rather than clobbering it.
            JObject Settings = File.Exists(SettingsFN) && File.ReadAllText(SettingsFN).TryParseJson(out JObject Existing) ? Existing : new JObject();

            // NOTE: DFIntTaskSchedulerTargetFps is not on Roblox's current FastFlag allowlist, so the client
            // ignores it — the FPS unlock is effectively a no-op on recent versions. Kept for older clients and
            // in case the flag is re-allowed; non-allowlisted flags are simply ignored, so it is harmless.
            if (UnlockFPS)
                Settings["DFIntTaskSchedulerTargetFps"] = AccountManager.General.Exists("MaxFPSValue") ? AccountManager.General.Get<int>("MaxFPSValue") : 240;

            // Low-graphics profile for background alts — flags that DO survive the allowlist: lower texture
            // quality, no MSAA, floored FRM quality level, no grass geometry, no DPI render-scaling. All of
            // these reduce GPU/VRAM load only; none of them touch game logic.
            if (LowGraphics)
            {
                Settings["DFFlagTextureQualityOverrideEnabled"] = true;
                Settings["DFIntTextureQualityOverride"] = 0;
                Settings["FIntDebugForceMSAASamples"] = 0;
                Settings["DFIntDebugFRMQualityLevelOverride"] = 1;
                Settings["FIntFRMMaxGrassDistance"] = 0;
                Settings["FIntFRMMinGrassDistance"] = 0;
                Settings["DFFlagDisableDPIScale"] = true;
            }

            File.WriteAllText(SettingsFN, Settings.ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}

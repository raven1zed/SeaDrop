using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Security.Credentials;

namespace SeaDropWindows.SeaDrop.storage
{
    /// <summary>
    /// CredentialStore — v1.5
    /// Sensitive data (auth token, hotspot SSID, hotspot passphrase) lives in
    /// Windows Credential Manager via Windows.Security.Credentials.PasswordVault.
    /// Non-sensitive settings (device name, receive folder, toggle flags) remain
    /// in HKCU registry.
    ///
    /// Credential Manager targets:
    ///   "SeaDrop-Token-Windows"   resource / username=local, password=token-hex
    ///   "SeaDrop-Hotspot"         resource / username=SSID,  password=passphrase
    /// </summary>
    public class CredentialStore
    {
        // ── Credential Manager targets ─────────────────────────────────────────
        private const string VaultTargetToken   = "SeaDrop-Token-Windows";
        private const string VaultTargetHotspot = "SeaDrop-Hotspot";
        private const string VaultUser          = "local";

        // ── Registry path for non-sensitive settings ───────────────────────────
        private const string RegistryPath = @"Software\SeaDrop\Settings";

        // ══ Auth token ═════════════════════════════════════════════════════════

        public void SaveAuthToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            VaultSave(VaultTargetToken, VaultUser, token);
        }

        public string GetAuthToken() => VaultLoad(VaultTargetToken, VaultUser) ?? string.Empty;

        public void ClearAuthToken() => VaultDelete(VaultTargetToken, VaultUser);

        // ══ Hotspot credentials ═════════════════════════════════════════════════

        /// <summary>
        /// Stores hotspot credentials in Credential Manager.
        /// resource = "SeaDrop-Hotspot", userName = SSID, password = passphrase.
        /// </summary>
        public void SaveHotspotCredentials(string ssid, string passphrase)
        {
            if (string.IsNullOrEmpty(ssid)) return;
            // PasswordVault requires userName != resource, so we embed the SSID
            // as userName and the passphrase as password.
            VaultSave(VaultTargetHotspot, ssid, passphrase);
        }

        public string GetHotspotSsid()
        {
            // The userName IS the SSID for the hotspot entry.
            var cred = VaultFind(VaultTargetHotspot);
            return cred?.UserName ?? string.Empty;
        }

        public string GetHotspotPass()
        {
            var cred = VaultFind(VaultTargetHotspot);
            if (cred == null) return string.Empty;
            try { cred.RetrievePassword(); return cred.Password; }
            catch (Exception ex) { Debug.WriteLine($"[CredentialStore] GetHotspotPass: {ex.Message}"); return string.Empty; }
        }

        public void ClearHotspotCredentials() => VaultDelete(VaultTargetHotspot, null);

        // ══ Non-sensitive settings — registry ══════════════════════════════════

        public string GetDeviceName() => LoadPlain("DeviceName") ?? Environment.MachineName;
        public void SaveDeviceName(string name) => SavePlain("DeviceName", name ?? string.Empty);

        public string GetReceiveFolder() =>
            LoadPlain("ReceiveFolder") ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "SeaDrop");

        public void SaveReceiveFolder(string folder) =>
            SavePlain("ReceiveFolder", folder ?? string.Empty);

        public bool GetAutostartHotspot()
        {
            var v = LoadPlain("AutostartHotspot");
            return string.IsNullOrEmpty(v) || v == "1"; // default ON
        }

        public void SaveAutostartHotspot(bool enabled) =>
            SavePlain("AutostartHotspot", enabled ? "1" : "0");

        public bool GetContextMenuEnabled()
        {
            var v = LoadPlain("ContextMenuEnabled");
            return string.IsNullOrEmpty(v) || v == "1"; // default ON
        }

        public void SaveContextMenuEnabled(bool enabled) =>
            SavePlain("ContextMenuEnabled", enabled ? "1" : "0");

        public bool GetWizardCompleted()
        {
            var v = LoadPlain("WizardCompleted");
            return v == "1";
        }

        public void SaveWizardCompleted(bool done) =>
            SavePlain("WizardCompleted", done ? "1" : "0");

        // ══ Internal — PasswordVault helpers ═══════════════════════════════════

        private static void VaultSave(string resource, string userName, string password)
        {
            try
            {
                var vault = new PasswordVault();
                // Remove any pre-existing entry for this resource+userName pair
                try
                {
                    var old = vault.Retrieve(resource, userName);
                    vault.Remove(old);
                }
                catch { /* not present — fine */ }

                vault.Add(new PasswordCredential(resource, userName, password));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CredentialStore] VaultSave({resource}): {ex.Message}");
            }
        }

        private static string? VaultLoad(string resource, string userName)
        {
            try
            {
                var vault = new PasswordVault();
                var cred = vault.Retrieve(resource, userName);
                cred.RetrievePassword();
                return cred.Password;
            }
            catch
            {
                return null;
            }
        }

        private static PasswordCredential? VaultFind(string resource)
        {
            try
            {
                var vault = new PasswordVault();
                var list = vault.FindAllByResource(resource);
                if (list == null || list.Count == 0) return null;
                var cred = list[0];
                cred.RetrievePassword();
                return cred;
            }
            catch
            {
                return null;
            }
        }

        private static void VaultDelete(string resource, string? userName)
        {
            try
            {
                var vault = new PasswordVault();
                if (userName != null)
                {
                    try { vault.Remove(vault.Retrieve(resource, userName)); } catch { }
                }
                else
                {
                    try
                    {
                        foreach (var c in vault.FindAllByResource(resource))
                            vault.Remove(c);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CredentialStore] VaultDelete({resource}): {ex.Message}");
            }
        }

        // ══ Internal — Registry helpers ════════════════════════════════════════

        private void SavePlain(string name, string value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key?.SetValue(name, value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CredentialStore] SavePlain({name}): {ex.Message}");
            }
        }

        private string? LoadPlain(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                return key?.GetValue(name) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
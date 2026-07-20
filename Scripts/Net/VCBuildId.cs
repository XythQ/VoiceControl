using System;
using System.IO;
using System.Security.Cryptography;

namespace XNPCVoiceControl.Net
{
    /// <summary>
    /// Computes a cached build-identity string from the deployed DLL hash + ModInfo version.
    /// Used by NetPackageVCHandshake to detect client/server build mismatches on dedi.
    /// Example: "3.0.08+a3f9c2b1" (first 8 hex chars of SHA256)
    /// </summary>
    public static class VCBuildId
    {
        private static string _cached;

        /// <summary>
        /// Build identity string (computed once, cached).
        /// Format: "{ModInfoVersion}+{hash8}" from SHA256 of the deployed DLL.
        /// If modPath is not yet available (pre-init), returns "unknown+prepath" without caching —
        /// next access retries once _modPath is set.
        /// </summary>
        public static string Current
        {
            get
            {
                if (_cached != null) return _cached;

                string modPath = XNPCVoiceControlMod.GetModPath();
                if (string.IsNullOrEmpty(modPath))
                {
                    // Pre-init — don't cache, allow retry.
                    Log.Warning("[VC-NET] VCBuildId: modPath not available yet");
                    return "unknown+prepath";
                }

                try
                {
                    string version = "unknown";
                    string modInfoPath = Path.Combine(modPath, "ModInfo.xml");
                    if (File.Exists(modInfoPath))
                    {
                        var doc = new System.Xml.XmlDocument { XmlResolver = null };
                        doc.Load(modInfoPath);
                        // ModInfo.xml root is <xml>, not <ModInfo>.
                        var node = doc.SelectSingleNode("//Version");
                        if (node != null)
                            version = node.Attributes["value"]?.Value.Trim() ?? version;
                    }

                    string dllPath = Path.Combine(modPath, "1-XNPCVoiceControl.dll");
                    byte[] hash = ComputeSha256(dllPath);
                    // First 4 bytes = 8 hex chars (Mono compat — no Convert.ToHexString)
                    string hash8 = $"{hash[0]:x2}{hash[1]:x2}{hash[2]:x2}{hash[3]:x2}";
                    _cached = $"{version}+{hash8}";
                }
                catch (Exception ex)
                {
                    Log.Warning($"[VC-NET] VCBuildId failed: {ex}");
                    _cached = $"error-{ex.Message}";
                }

                return _cached;
            }
        }

        /// <summary>Read the file and return its full SHA256 hash.</summary>
        private static byte[] ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(stream);
            }
        }
    }
}

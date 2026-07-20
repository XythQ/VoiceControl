using System;
using UnityEngine;

namespace XNPCVoiceControl
{
    /// <summary>
    /// Helper for platform detection and cross-platform compatibility.
    /// </summary>
    public static class PlatformHelper
    {
        private static bool? _isWindows;
        private static bool? _isLinux;

        /// <summary>
        /// Returns true if running on Windows
        /// </summary>
        public static bool IsWindows
        {
            get
            {
                if (!_isWindows.HasValue)
                {
                    _isWindows = Application.platform == RuntimePlatform.WindowsPlayer ||
                                 Application.platform == RuntimePlatform.WindowsEditor ||
                                 Environment.OSVersion.Platform == PlatformID.Win32NT;
                }
                return _isWindows.Value;
            }
        }

        /// <summary>
        /// Returns true if running on Linux
        /// </summary>
        public static bool IsLinux
        {
            get
            {
                if (!_isLinux.HasValue)
                {
                    _isLinux = Application.platform == RuntimePlatform.LinuxPlayer ||
                               Application.platform == RuntimePlatform.LinuxEditor ||
                               Environment.OSVersion.Platform == PlatformID.Unix;
                }
                return _isLinux.Value;
            }
        }

        /// <summary>
        /// Get platform name for logging
        /// </summary>
        public static string PlatformName
        {
            get
            {
                if (IsWindows) return "Windows";
                if (IsLinux) return "Linux";
                return Application.platform.ToString();
            }
        }
    }
}

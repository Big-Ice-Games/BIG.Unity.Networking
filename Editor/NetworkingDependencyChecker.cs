using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BIG.Unity.Networking.Editor
{
    /// <summary>
    /// Always-compiling dependency checker. The runtime assembly is gated by defineConstraints (BIG_FISHNET),
    /// so a missing FishNet produces no compile errors — this class tells the developer what to install instead.
    /// </summary>
    [InitializeOnLoad]
    internal static class NetworkingDependencyChecker
    {
        private const string FISHNET_PACKAGE = "com.firstgeargames.fishnet";
        private const string FISHNET_URL = "https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet";

        static NetworkingDependencyChecker()
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            if (packages.Any(p => p.name == FISHNET_PACKAGE)) return;

            // FishNet imported from the Asset Store lands in Assets/ — versionDefines only see UPM packages.
            bool importedAsAsset = System.AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "FishNet.Runtime");

            Debug.LogError(importedAsAsset
                ? $"[BIG] BIG.Unity.Networking needs FishNet as a UPM package (found it imported into Assets/). Remove the Assets/ copy and install via Package Manager > Install package from git URL: {FISHNET_URL}"
                : $"[BIG] BIG.Unity.Networking requires FishNet. Install it via Package Manager > Install package from git URL: {FISHNET_URL}");
        }
    }
}

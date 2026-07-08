using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AreezKhan79.PackageHub.Editor
{
    [FilePath("ProjectSettings/PackageHubSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal class PackageHubSettings : ScriptableSingleton<PackageHubSettings>
    {
        [SerializeField] private List<string> registryUrls = new List<string>();

        public IReadOnlyList<string> RegistryUrls => registryUrls;

        public void AddRegistry(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || registryUrls.Contains(url)) return;
            registryUrls.Add(url);
            Save(true);
        }

        public void RemoveRegistry(string url)
        {
            if (registryUrls.Remove(url))
            {
                Save(true);
            }
        }
    }
}

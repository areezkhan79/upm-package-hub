using System;

namespace AreezKhan79.PackageHub.Editor
{
    [Serializable]
    internal class PresetEntry
    {
        public string packageName;
        public string version;
    }

    [Serializable]
    internal class Preset
    {
        public string presetName;
        public PresetEntry[] entries;
    }
}

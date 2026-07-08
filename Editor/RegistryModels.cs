using System;

namespace AreezKhan79.PackageHub.Editor
{
    [Serializable]
    internal class PackageEntry
    {
        public string name;
        public string displayName;
        public string description;
        public string repoUrl;
        public string category;
        public string[] dependencies;
    }

    [Serializable]
    internal class Registry
    {
        public PackageEntry[] packages;
    }

    [Serializable]
    internal class GitTagEntry
    {
        public string name;
    }

    [Serializable]
    internal class GitTagListWrapper
    {
        public GitTagEntry[] items;
    }
}

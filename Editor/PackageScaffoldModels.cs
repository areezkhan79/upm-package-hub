using System;

namespace AreezKhan79.PackageHub.Editor
{
    [Serializable]
    internal class NewPackageJson
    {
        public string name;
        public string version = "0.1.0";
        public string displayName;
        public string description;
        public string unity = "2021.3";
        public string license = "MIT";
    }

    [Serializable]
    internal class AsmdefJson
    {
        public string name;
        public string rootNamespace;
        public string[] references = Array.Empty<string>();
        public string[] includePlatforms = Array.Empty<string>();
        public string[] excludePlatforms = Array.Empty<string>();
        public bool allowUnsafeCode;
        public bool overrideReferences;
        public string[] precompiledReferences = Array.Empty<string>();
        public bool autoReferenced = true;
        public string[] defineConstraints = Array.Empty<string>();
        public string[] versionDefines = Array.Empty<string>();
        public bool noEngineReferences;
    }

    [Serializable]
    internal class ContentsGetResponse
    {
        public string content;
        public string sha;
    }

    [Serializable]
    internal class UpdateFileRequest
    {
        public string message;
        public string content;
        public string sha;
    }
}

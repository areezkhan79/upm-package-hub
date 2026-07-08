using System;

namespace AreezKhan79.PackageHub.Editor
{
    [Serializable]
    internal class CreateRepoRequest
    {
        public string name;
        public string description;
        public bool @private;
    }

    [Serializable]
    internal class OwnerInfo
    {
        public string login;
    }

    [Serializable]
    internal class CreateRepoResponse
    {
        public string name;
        public string full_name;
        public string default_branch;
        public OwnerInfo owner;
    }

    [Serializable]
    internal class CreateFileRequest
    {
        public string message;
        public string content;
    }
}

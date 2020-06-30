namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class DepotEncryptedManifest
    {
        public string BranchName { get; }
        public string EncryptedManifestId { get; }
        public EncryptionVersion Version { get; }

        public DepotEncryptedManifest(string branchName, string encryptedManifestId, EncryptionVersion version)
        {
            BranchName = branchName;
            EncryptedManifestId = encryptedManifestId;
            Version = version;
        }

        public enum EncryptionVersion
        {
            V1,
            V2
        }
    }
}

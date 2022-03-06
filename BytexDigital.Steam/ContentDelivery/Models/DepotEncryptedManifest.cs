using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class DepotEncryptedManifest
    {
        public enum EncryptionVersion
        {
            V1,
            V2
        }

        public AppId AppId { get; }
        public DepotId DepotId { get; set; }
        public string BranchName { get; }
        public string EncryptedManifestId { get; }
        public EncryptionVersion Version { get; }

        public DepotEncryptedManifest(AppId appId, DepotId depotId, string branchName, string encryptedManifestId,
            EncryptionVersion version)
        {
            BranchName = branchName;
            EncryptedManifestId = encryptedManifestId;
            Version = version;
            AppId = appId;
            DepotId = depotId;
        }
    }
}
namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class FileVerifiedArgs
    {
        public FileVerifiedArgs(ManifestFile manifestFile, bool requiresDownload)
        {
            ManifestFile = manifestFile;
            RequiresDownload = requiresDownload;
        }

        public ManifestFile ManifestFile { get; }
        public bool RequiresDownload { get; }
    }
}

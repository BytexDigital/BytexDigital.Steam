using System.Collections.Generic;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class VerificationCompletedArgs
    {
        /// <summary>
        ///     Files that are queued for download.
        /// </summary>
        public IReadOnlyList<ManifestFile> QueuedFiles { get; }

        public VerificationCompletedArgs(IReadOnlyList<ManifestFile> manifestFiles) => QueuedFiles = manifestFiles;
    }
}
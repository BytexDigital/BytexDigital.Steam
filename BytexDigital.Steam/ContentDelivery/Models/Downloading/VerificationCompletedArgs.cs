using System;
using System.Collections.Generic;
using System.Text;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class VerificationCompletedArgs
    {
        public VerificationCompletedArgs(IReadOnlyList<ManifestFile> manifestFiles)
        {
            QueuedFiles = manifestFiles;
        }

        /// <summary>
        /// Files that are queued for download.
        /// </summary>
        public IReadOnlyList<ManifestFile> QueuedFiles { get; }
    }
}

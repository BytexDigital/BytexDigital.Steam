using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamManifestDownloadException : Exception
    {
        public SteamManifestDownloadException(Exception innerException) : base(
            "Could not download the manifest file. View the inner exception for more information.", innerException)
        {
        }

        public SteamManifestDownloadException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public SteamManifestDownloadException(string message) : base(message)
        {
        }
    }
}
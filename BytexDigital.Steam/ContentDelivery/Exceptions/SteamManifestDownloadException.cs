using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamManifestDownloadException : Exception
    {
        public SteamManifestDownloadException(Exception innerException) : base($"Could not download the manifest file. View the inner exception for more information.", innerException)
        {
        }
    }
}

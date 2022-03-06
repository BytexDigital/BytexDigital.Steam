using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDownloadException : Exception
    {
        public SteamDownloadException(Exception innerException) : base(
            "Failed to download atleast one chunk repeatedly.", innerException)
        {
        }
    }
}
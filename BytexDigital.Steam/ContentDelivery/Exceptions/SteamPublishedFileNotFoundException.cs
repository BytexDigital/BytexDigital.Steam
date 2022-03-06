using System;
using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamPublishedFileNotFoundException : Exception
    {
        public SteamPublishedFileNotFoundException(PublishedFileId publishedFileId) : base(
            $"Workshop item {publishedFileId} not found.")
        {
        }
    }
}

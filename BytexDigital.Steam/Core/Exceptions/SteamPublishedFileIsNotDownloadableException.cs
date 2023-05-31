using System;
using BytexDigital.Steam.Core.Structs;
using SteamKit2;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamPublishedFileIsNotDownloadableException : Exception
    {
        public SteamPublishedFileIsNotDownloadableException(PublishedFileId id, EWorkshopFileType type) : base(
            $"Published file with id = {id} cannot be downloaded as it's type is {type}.")
        {
        }
    }
}
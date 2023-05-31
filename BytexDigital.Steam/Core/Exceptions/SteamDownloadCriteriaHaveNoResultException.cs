using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDownloadCriteriaHaveNoResultException : Exception
    {
        public SteamDownloadCriteriaHaveNoResultException(string message) : base(message)
        {
        }
    }
}
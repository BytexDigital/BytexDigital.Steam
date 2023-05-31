using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamGenericErrorException : Exception
    {
        public SteamGenericErrorException() : base("An error occurred.")
        {
        }
    }
}

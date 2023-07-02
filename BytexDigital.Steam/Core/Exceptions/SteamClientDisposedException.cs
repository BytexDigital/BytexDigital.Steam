using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamClientDisposedException : Exception
    {
        public SteamClientDisposedException() : base(
            "The client cannot be used anymore as it was disposed. Create a new client.")
        {
        }
    }
}

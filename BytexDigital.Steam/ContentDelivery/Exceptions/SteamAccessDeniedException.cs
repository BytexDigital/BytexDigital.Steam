using System;
using System.Runtime.Serialization;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamAccessDeniedException : Exception
    {
        public SteamAccessDeniedException()
        {
        }

        public SteamAccessDeniedException(string message) : base(message)
        {
        }

        public SteamAccessDeniedException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected SteamAccessDeniedException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}

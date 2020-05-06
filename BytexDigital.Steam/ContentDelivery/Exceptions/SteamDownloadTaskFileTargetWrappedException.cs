using BytexDigital.Steam.ContentDelivery.Models;

using System;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDownloadTaskFileTargetWrappedException : Exception
    {
        public DownloadFileTarget ExceptionProducer { get; }

        public SteamDownloadTaskFileTargetWrappedException(DownloadFileTarget exceptionProducer, Exception innerException)
            : base($"An error occurred while trying to write the provided file target handler. View the inner exception for more details.", innerException)
        {
            ExceptionProducer = exceptionProducer;
        }
    }
}

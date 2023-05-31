using System;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class SteamDownloadTaskFileTargetWrappedException : Exception
    {
        public FileTarget ExceptionProducer { get; }

        public SteamDownloadTaskFileTargetWrappedException(FileTarget exceptionProducer, Exception innerException)
            : base(
                "An error occurred while trying to write the provided file target handler. View the inner exception for more details.",
                innerException) =>
            ExceptionProducer = exceptionProducer;
    }
}

using System;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;

namespace BytexDigital.Steam.ContentDelivery.Exceptions
{
    public class DownloadHandlerStateMismatchException : Exception
    {
        public DownloadHandlerStateMismatchException(
            DownloadHandlerStateEnum expectedState,
            DownloadHandlerStateEnum actualState) : base(
            $"Action cannot be performed because download handlers current state '{actualState}' does not match expected state '{expectedState}'")
        {
        }
    }
}
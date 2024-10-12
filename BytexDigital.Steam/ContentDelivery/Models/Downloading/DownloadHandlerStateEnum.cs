namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public enum DownloadHandlerStateEnum
    {
        Created,
        SetUp,
        Verifying,
        Verified,
        Downloading,
        Downloaded,
        Failed
    }
}
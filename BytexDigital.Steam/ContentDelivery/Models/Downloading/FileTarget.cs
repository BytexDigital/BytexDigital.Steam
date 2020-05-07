using System.IO;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public abstract class FileTarget
    {
        public static FileTarget None = null;

        public double Progress => (double)WrittenBytes / (TotalBytes > 0 ? TotalBytes : 1);
        public ulong TotalBytes { get; internal set; }
        public ulong WrittenBytes { get; internal set; }

        public abstract void Write(ulong offset, byte[] data);
        public abstract void Completed();
    }
}

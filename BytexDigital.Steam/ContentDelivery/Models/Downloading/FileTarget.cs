using System.IO;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public abstract class FileTarget
    {
        public static FileTarget None = null;

        public double Progress => (double)WrittenBytes / (TotalBytes > 0 ? TotalBytes : 1);
        public ulong TotalBytes { get; internal set; }
        public ulong WrittenBytes { get; internal set; }

        public abstract Task WriteAsync(ulong offset, byte[] data);
        public abstract Task CompleteAsync();
        public abstract Task CancelAsync();
    }
}

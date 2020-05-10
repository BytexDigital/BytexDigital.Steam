using System.IO;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class FileStreamTarget : FileTarget
    {
        public FileStream FileStream { get; }

        public FileStreamTarget(FileStream fileStream)
        {
            FileStream = fileStream;
        }

        public override async Task CompleteAsync()
        {
            await FileStream.FlushAsync().ConfigureAwait(false);
            FileStream.Close();
        }

        public override async Task WriteAsync(ulong offset, byte[] data)
        {
            FileStream.Seek((long)offset, SeekOrigin.Begin);
            await FileStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
        }
    }
}

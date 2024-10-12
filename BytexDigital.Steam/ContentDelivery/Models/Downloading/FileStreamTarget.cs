using System.IO;
using System.Threading.Tasks;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class FileStreamTarget : FileTarget
    {
        private readonly string _filePath;
        private readonly ulong _fileSize;
        public FileStream FileStream { get; private set; }

        public FileStreamTarget(string filePath, ulong fileSize)
        {
            _filePath = filePath;
            _fileSize = fileSize;
        }
        
        public override async Task CompleteAsync()
        {
            await FileStream.FlushAsync().ConfigureAwait(false);
            FileStream.Close();
        }

        public override async Task WriteAsync(ulong offset, byte[] data)
        {
            if (FileStream == null)
            {
                FileStream = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
                FileStream.SetLength((long) _fileSize);
            }
            
            FileStream.Seek((long) offset, SeekOrigin.Begin);
            await FileStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
        }

        public override async Task CancelAsync()
        {
            try
            {
                await FileStream.FlushAsync().ConfigureAwait(false);
                FileStream.Close();
            }
            catch
            {
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await CancelAsync();
        }
    }
}

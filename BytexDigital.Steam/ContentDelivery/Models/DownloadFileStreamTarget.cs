using System.IO;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class DownloadFileStreamTarget : DownloadFileTarget
    {
        public FileStream FileStream { get; }

        public DownloadFileStreamTarget(FileStream fileStream)
        {
            FileStream = fileStream;
        }

        public override void Completed()
        {
            lock (FileStream)
            {
                FileStream.Flush();
                FileStream.Close();
            }
        }

        public override void Write(ulong offset, byte[] data)
        {
            lock (FileStream)
            {
                FileStream.Seek((long)offset, SeekOrigin.Begin);
                FileStream.Write(data, 0, data.Length);
            }
        }
    }
}

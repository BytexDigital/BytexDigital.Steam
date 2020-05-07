using System.IO;

namespace BytexDigital.Steam.ContentDelivery.Models.Downloading
{
    public class FileStreamTarget : FileTarget
    {
        public FileStream FileStream { get; }

        public FileStreamTarget(FileStream fileStream)
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

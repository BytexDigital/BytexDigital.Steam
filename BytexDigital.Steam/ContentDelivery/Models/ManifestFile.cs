using System.Collections.Generic;
using System.Linq;
using BytexDigital.Steam.ContentDelivery.Enumerations;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class ManifestFile
    {
        public string FileName { get; }
        public IReadOnlyList<ManifestFileChunkHeader> ChunkHeaders { get; }
        public ManifestFileFlag Flags { get; }
        public ulong TotalSize { get; }
        public byte[] FileHash { get; }

        public ManifestFile(string fileName, List<ManifestFileChunkHeader> chunkHeaders, ManifestFileFlag flags,
            ulong totalSize, byte[] fileHash)
        {
            FileName = fileName;
            ChunkHeaders = chunkHeaders;
            Flags = flags;
            TotalSize = totalSize;
            FileHash = fileHash;
        }

        public static implicit operator ManifestFile(SteamKit2.DepotManifest.FileData file)
        {
            return new ManifestFile(file.FileName, file.Chunks.Select(x => (ManifestFileChunkHeader) x).ToList(),
                (ManifestFileFlag) (int) file.Flags, file.TotalSize, file.FileHash);
        }
    }
}

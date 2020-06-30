using SteamKit2;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class ManifestFileChunkHeader
    {
        public ManifestFileChunkHeader(byte[] id, byte[] checksum, ulong offset, uint compressedLength, uint uncompressedLength)
        {
            Id = id;
            Checksum = checksum;
            Offset = offset;
            CompressedLength = compressedLength;
            UncompressedLength = uncompressedLength;
        }

        public byte[] Id { get; }
        public byte[] Checksum { get; }
        public ulong Offset { get; }
        public uint CompressedLength { get; }
        public uint UncompressedLength { get; }

        public static implicit operator ManifestFileChunkHeader(SteamKit2.DepotManifest.ChunkData chunk) =>
            new ManifestFileChunkHeader(chunk.ChunkID, chunk.Checksum, chunk.Offset, chunk.CompressedLength, chunk.UncompressedLength);
    }
}

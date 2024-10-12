namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class ManifestFileChunkHeader
    {
        public byte[] Id { get; }
        public uint Checksum { get; }
        public ulong Offset { get; }
        public uint CompressedLength { get; }
        public uint UncompressedLength { get; }

        public ManifestFileChunkHeader(byte[] id, uint checksum, ulong offset, uint compressedLength,
            uint uncompressedLength)
        {
            Id = id;
            Checksum = checksum;
            Offset = offset;
            CompressedLength = compressedLength;
            UncompressedLength = uncompressedLength;
        }

        public static implicit operator ManifestFileChunkHeader(SteamKit2.DepotManifest.ChunkData chunk) =>
            new ManifestFileChunkHeader(chunk.ChunkID, chunk.Checksum, chunk.Offset, chunk.CompressedLength,
                chunk.UncompressedLength);
    }
}

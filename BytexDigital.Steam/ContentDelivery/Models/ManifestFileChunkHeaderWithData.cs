namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class ManifestFileChunkHeaderWithData : ManifestFileChunkHeader
    {
        public byte[] Data { get; }

        public ManifestFileChunkHeaderWithData(
            byte[] id,
            byte[] checksum,
            ulong offset,
            uint compressedLength,
            uint uncompressedLength,
            byte[] data) : base(id, checksum, offset, compressedLength, uncompressedLength) =>
            Data = data;
    }
}
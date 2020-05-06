namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class ManifestFileChunkHeaderWithData : ManifestFileChunkHeader
    {
        public ManifestFileChunkHeaderWithData(
            byte[] id,
            byte[] checksum,
            ulong offset,
            uint compressedLength,
            uint uncompressedLength,
            byte[] data) : base(id, checksum, offset, compressedLength, uncompressedLength)
        {
            Data = data;
        }

        public byte[] Data { get; }
    }
}

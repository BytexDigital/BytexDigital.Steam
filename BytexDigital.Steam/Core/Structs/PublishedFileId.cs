namespace BytexDigital.Steam.Core.Structs
{
    public struct PublishedFileId
    {
        public ulong Id { get; }

        public PublishedFileId(ulong id) => Id = id;

        public static implicit operator PublishedFileId(ulong id) => new PublishedFileId(id);

        public static implicit operator ulong(PublishedFileId publishedFileId) => publishedFileId.Id;

        public override string ToString() => Id.ToString();
    }
}
namespace BytexDigital.Steam.Core.Structs
{
    public struct ManifestId
    {
        public ulong Id { get; }

        public ManifestId(ulong id)
        {
            Id = id;
        }

        public static implicit operator ManifestId(ulong id) => new ManifestId(id);
        public static implicit operator ulong(ManifestId manifestId) => manifestId.Id;

        public override string ToString() => Id.ToString();
    }
}

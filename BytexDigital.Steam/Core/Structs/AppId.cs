namespace BytexDigital.Steam.Core.Structs
{
    public struct AppId
    {
        public uint Id { get; }

        public AppId(uint id) => Id = id;

        public static implicit operator AppId(uint id) => new AppId(id);

        public static implicit operator uint(AppId appId) => appId.Id;

        public override string ToString() => Id.ToString();
    }
}

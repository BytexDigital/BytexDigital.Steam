namespace BytexDigital.Steam.Core.Structs
{
    public struct DepotId
    {
        public uint Id { get; }

        public DepotId(uint id)
        {
            Id = id;
        }

        public static implicit operator DepotId(uint id) => new DepotId(id);
        public static implicit operator uint(DepotId depotId) => depotId.Id;

        public static implicit operator DepotId(AppId appId) => new DepotId(appId.Id);

        public override string ToString() => Id.ToString();
    }
}

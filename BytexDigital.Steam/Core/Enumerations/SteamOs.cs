namespace BytexDigital.Steam.Core.Enumerations
{
    public class SteamOs
    {
        public static SteamOs Windows = new SteamOs("windows");
        public static SteamOs Linux = new SteamOs("linux");
        public static SteamOs MacOS = new SteamOs("macos");

        public string Identifier { get; }

        public SteamOs(string identifier)
        {
            Identifier = identifier ?? throw new System.ArgumentNullException(nameof(identifier), "The OS identifier is not valid.");
        }

        public static implicit operator SteamOs(string identifier) => new SteamOs(identifier);

        public override string ToString() => Identifier;
    }
}

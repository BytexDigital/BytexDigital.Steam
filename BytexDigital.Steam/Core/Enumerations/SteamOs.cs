using System;
using System.Runtime.InteropServices;

namespace BytexDigital.Steam.Core.Enumerations
{
    public class SteamOs
    {
        public static SteamOs Windows = new SteamOs("windows");
        public static SteamOs Linux = new SteamOs("linux");
        public static SteamOs MacOs = new SteamOs("macos");

        public string Identifier { get; }

        public SteamOs(string identifier)
        {
            Identifier = identifier ??
                         throw new ArgumentNullException(nameof(identifier), "The OS identifier is not valid.");
        }

        public static implicit operator SteamOs(string identifier)
        {
            return new SteamOs(identifier);
        }

        public static SteamOs GetCurrent()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Windows;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return Linux;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return MacOs;
            }

            return new SteamOs("unknown");
        }

        public override string ToString()
        {
            return Identifier;
        }
    }
}
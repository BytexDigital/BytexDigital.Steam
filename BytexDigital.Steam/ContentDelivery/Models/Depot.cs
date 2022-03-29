using System.Collections.Generic;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class Depot
    {
        public DepotId Id { get; }

        public string Name { get; }

        public ulong MaxSize { get; }

        /// <summary>
        ///     Marks if this depot is a shared install. Shared installs are actually part of a different app id. See
        ///     <see cref="DepotOfAppId" />.
        /// </summary>
        public bool IsSharedInstall { get; set; }

        /// <summary>
        ///     If this depot is a shared install, this is the <see cref="AppId" /> where to look up this depot.
        /// </summary>
        public AppId? DepotOfAppId { get; set; }

        public IReadOnlyList<SteamOs> OperatingSystems { get; }

        public IReadOnlyList<DepotManifest> Manifests { get; }

        public IReadOnlyList<DepotEncryptedManifest> EncryptedManifests { get; }

        public IReadOnlyList<string> ProcessorArchitectures { get; }
        public IReadOnlyList<string> Languages { get; }

        public IReadOnlyDictionary<string, string> Config { get; }

        public Depot(
            DepotId id,
            string name,
            ulong maxSize,
            IReadOnlyList<SteamOs> operatingSystems,
            IReadOnlyList<string> processorArchitectures,
            IReadOnlyList<string> languages,
            IReadOnlyList<DepotManifest> manifests,
            IReadOnlyList<DepotEncryptedManifest> encryptedManifests,
            IReadOnlyDictionary<string, string> config,
            bool isSharedInstall,
            AppId? parentAppId)
        {
            Id = id;
            Name = name;
            MaxSize = maxSize;
            OperatingSystems = operatingSystems;
            Manifests = manifests;
            EncryptedManifests = encryptedManifests;
            ProcessorArchitectures = processorArchitectures;
            Languages = languages;
            Config = config;
            IsSharedInstall = isSharedInstall;
            DepotOfAppId = parentAppId;
        }
    }
}
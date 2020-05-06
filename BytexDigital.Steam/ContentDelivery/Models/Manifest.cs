using BytexDigital.Steam.Core.Structs;

using SteamKit2;

using System;
using System.Collections.Generic;
using System.Linq;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class Manifest
    {
        public Manifest(List<ManifestFile> files, DepotId depotId, ManifestId manifestId, DateTime creationTime, ulong totalUncompressedSize, ulong totalCompressedSize)
        {
            Files = files;
            DepotId = depotId;
            ManifestId = manifestId;
            CreationTime = creationTime;
            TotalUncompressedSize = totalUncompressedSize;
            TotalCompressedSize = totalCompressedSize;
        }

        public IReadOnlyList<ManifestFile> Files { get; }
        public DepotId DepotId { get; }
        public ManifestId ManifestId { get; }
        public DateTime CreationTime { get; }
        public ulong TotalUncompressedSize { get; }
        public ulong TotalCompressedSize { get; }

        public static implicit operator Manifest(DepotManifest x) =>
            new Manifest(x.Files.Select(x => (ManifestFile)x).ToList(), x.DepotID, x.ManifestGID, x.CreationTime, x.TotalUncompressedSize, x.TotalCompressedSize);
    }
}

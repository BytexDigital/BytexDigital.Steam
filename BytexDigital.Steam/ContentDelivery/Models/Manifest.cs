using System;
using System.Collections.Generic;
using System.Linq;
using BytexDigital.Steam.Core.Structs;

namespace BytexDigital.Steam.ContentDelivery.Models
{
    public class Manifest
    {
        public IReadOnlyList<ManifestFile> Files { get; }
        public DepotId DepotId { get; }
        public ManifestId ManifestId { get; }
        public DateTime CreationTime { get; }
        public ulong TotalUncompressedSize { get; }
        public ulong TotalCompressedSize { get; }

        public Manifest(List<ManifestFile> files, DepotId depotId, ManifestId manifestId, DateTime creationTime,
            ulong totalUncompressedSize, ulong totalCompressedSize)
        {
            Files = files;
            DepotId = depotId;
            ManifestId = manifestId;
            CreationTime = creationTime;
            TotalUncompressedSize = totalUncompressedSize;
            TotalCompressedSize = totalCompressedSize;
        }

        public static implicit operator Manifest(SteamKit2.DepotManifest x)
        {
            return new Manifest(x.Files.Select(x => (ManifestFile) x).ToList(), x.DepotID, x.ManifestGID,
                x.CreationTime, x.TotalUncompressedSize, x.TotalCompressedSize);
        }
    }
}
using BytexDigital.Steam.ContentDelivery.Models;

using Newtonsoft.Json;

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BytexDigital.Steam.ContentDelivery
{
    public abstract class SteamContentServerQualityProvider
    {
        public abstract IList<SteamContentServerQuality> Load();

        public abstract void Save(IList<SteamContentServerQuality> servers);
    }

    public class SteamContentServerQualityNoMemoryProvider : SteamContentServerQualityProvider
    {
        public override IList<SteamContentServerQuality> Load() => new List<SteamContentServerQuality>();

        public override void Save(IList<SteamContentServerQuality> servers)
        {

        }
    }

    public class SteamContentServerQualityFileProvider : SteamContentServerQualityProvider
    {
        public string FileName { get; }

        public SteamContentServerQualityFileProvider(string fileName)
        {
            FileName = fileName;
        }

        public override IList<SteamContentServerQuality> Load()
        {
            try
            {
                return JsonConvert.DeserializeObject<List<SteamContentServerQuality>>(File.ReadAllText(FileName));
            }
            catch
            {
                return new List<SteamContentServerQuality>();
            }
        }

        public override void Save(IList<SteamContentServerQuality> servers)
        {
            try
            {
                File.WriteAllText(FileName, JsonConvert.SerializeObject(servers.OrderBy(x => x.Score), Formatting.Indented));
            }
            catch
            {
            }
        }
    }
}

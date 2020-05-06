using System.Collections.Generic;
using System.Linq;

namespace BytexDigital.Steam.Extensions
{
    public static class KeyValueEnumerableExtensions
    {
        public static bool ContainsKeyOrValue(this IEnumerable<SteamKit2.KeyValue> keyValues, string keyOrValue, out SteamKit2.KeyValue keyValue)
        {
            var kv = keyValues.FirstOrDefault(x => x.Name == keyOrValue || x.Value == keyOrValue);

            if (kv == null)
            {
                keyValue = SteamKit2.KeyValue.Invalid;
                return false;
            }
            else
            {
                keyValue = kv;
                return true;
            }
        }
    }
}

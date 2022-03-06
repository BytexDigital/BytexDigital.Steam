using System.Collections.Generic;
using System.Linq;
using SteamKit2;

namespace BytexDigital.Steam.Extensions
{
    public static class KeyValueEnumerableExtensions
    {
        public static bool ContainsKeyOrValue(this IEnumerable<KeyValue> keyValues, string keyOrValue,
            out KeyValue keyValue)
        {
            var kv = keyValues.FirstOrDefault(x => x.Name == keyOrValue || x.Value == keyOrValue);

            if (kv == null)
            {
                keyValue = KeyValue.Invalid;
                return false;
            }

            keyValue = kv;
            return true;
        }
    }
}
// Anonimizzazione dei dati mandati all'assistente AI: nome del PC, dominio, utente, reti Wi-Fi, MAC,
// IP pubblico e indirizzi IPv6 diventano segnaposti ([PC], [WIFI], [MAC_2]...). La risposta torna
// in chiaro solo sul PC, rimettendo i valori veri al posto dei segnaposti.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AppIP
{
    class Anonymizer
    {
        // valore vero -> segnaposto
        readonly List<KeyValuePair<string, string>> map = new List<KeyValuePair<string, string>>();
        readonly Dictionary<string, int> counts = new Dictionary<string, int>();

        // Indirizzi IPv6 (anche abbreviati con ::); gli indirizzi fe80:: locali non identificano nessuno ma
        // si nascondono comunque, per semplicità
        static readonly Regex Ipv6 = new Regex(@"(?<![\w:.])(?:[0-9a-fA-F]{1,4}:){2,7}(?::|[0-9a-fA-F]{1,4})(?:%\w+)?(?![\w:.])|(?<![\w:.])(?:[0-9a-fA-F]{1,4}:){1,6}:(?:[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{1,4}){0,5})?(?:%\w+)?(?![\w:.])");

        public void Add(string real, string kind)
        {
            if (string.IsNullOrEmpty(real)) return;
            real = real.Trim();
            if (real.Length < 3 || real == "-") return;
            if (map.Any(kv => string.Equals(kv.Key, real, StringComparison.OrdinalIgnoreCase))) return;
            int n;
            counts.TryGetValue(kind, out n);
            counts[kind] = ++n;
            map.Add(new KeyValuePair<string, string>(real, "[" + kind + (n > 1 ? "_" + n : "") + "]"));
        }

        public string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // prima i valori più lunghi (es. "PC.dominio" prima di "PC")
            foreach (var kv in map.OrderByDescending(k => k.Key.Length).ToList())
                text = Regex.Replace(text, Regex.Escape(kv.Key), kv.Value.Replace("$", "$$"), RegexOptions.IgnoreCase);
            return Ipv6.Replace(text, m =>
            {
                string v = m.Value;
                // solo indirizzi veri: abbreviati (::) o completi (8 gruppi); non orari come 10:30:00
                if (!v.Contains("::") && v.Split('%')[0].Count(c => c == ':') != 7) return v;
                Add(v, "IPV6");
                return map.First(kv => string.Equals(kv.Key, v, StringComparison.OrdinalIgnoreCase)).Value;
            });
        }

        public string Restore(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (var kv in map.OrderByDescending(k => k.Value.Length))
                text = text.Replace(kv.Value, kv.Key);
            return text;
        }
    }
}

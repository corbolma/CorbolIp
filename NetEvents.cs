// Registro degli eventi di rete: cadute, cambi di rete, IP, Wi-Fi, VPN, router e Internet che non rispondono
// (questi solo con il ping attivo), sospensione e ripresa del PC. Non genera traffico: annota quello che il
// widget vede già. File %APPDATA%\AppIP\events.log, una riga per evento, ultimi 30 giorni.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AppIP
{
    static class NetEvents
    {
        public class Entry
        {
            public DateTime Time;
            public string Kind, Text;
        }

        public const int KeepDays = 30;
        public static bool Enabled = true;
        static readonly object gate = new object();

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AppIP", "events.log"); }
        }

        // kind: START, STOP, SLEEP, RESUME, NET, IP, WIFI, GW, VPN, PUBIP, PING
        public static void Add(string kind, string text)
        {
            if (!Enabled) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\t" + kind + "\t"
                        + (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\r\n";
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.AppendAllText(FilePath, line, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        // Toglie le righe più vecchie di KeepDays (all'avvio)
        public static void Prune()
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return;
                    var lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                    var limit = DateTime.Now.AddDays(-KeepDays);
                    var keep = lines.Where(l => { var e = ParseLine(l); return e != null && e.Time >= limit; }).ToArray();
                    if (keep.Length != lines.Length) File.WriteAllLines(FilePath, keep, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        static Entry ParseLine(string l)
        {
            var p = l.Split('\t');
            if (p.Length < 3) return null;
            DateTime t;
            if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            return new Entry { Time = t, Kind = p[1], Text = p[2] };
        }

        // Eventi delle ultime ore, dal più vecchio
        public static List<Entry> Recent(double hours)
        {
            var res = new List<Entry>();
            lock (gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return res;
                    var limit = DateTime.Now.AddHours(-hours);
                    foreach (var l in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        var e = ParseLine(l);
                        if (e != null && e.Time >= limit) res.Add(e);
                    }
                }
                catch { }
            }
            return res;
        }

        // Testo per il report e per l'assistente: al massimo maxLines righe, le più recenti.
        // forAi: l'IP pubblico non si riporta (resta solo che è cambiato).
        public static string Text(double hours, int maxLines, bool forAi)
        {
            var list = Recent(hours);
            if (list.Count == 0) return "";
            var sb = new StringBuilder();
            if (list.Count > maxLines)
            {
                sb.AppendLine(forAi ? "(" + (list.Count - maxLines) + " older events omitted)"
                                    : L.F("({0} eventi precedenti non mostrati)", "({0} earlier events not shown)", list.Count - maxLines));
                list = list.Skip(list.Count - maxLines).ToList();
            }
            foreach (var e in list)
            {
                string text = forAi && e.Kind == "PUBIP" ? "public IP address changed" : e.Text;
                sb.AppendLine(e.Time.ToString(forAi ? "yyyy-MM-dd HH:mm" : "g") + "  " + text);
            }
            return sb.ToString();
        }
    }
}

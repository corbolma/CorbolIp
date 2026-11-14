// Qualità della rete nel tempo: ogni 5 minuti una riga con i valori della rete in uso (ping al router e a
// Internet se il ping è acceso, segnale Wi-Fi, velocità media). Non genera traffico: usa solo ciò che il widget
// misura già. File %APPDATA%\CorbolIp\quality.log, ultimi 30 giorni.
// Per ogni rete impara i valori normali (ultimi 14 giorni) e annota nel registro eventi quando la rete è molto
// peggio del solito, e quando torna normale. Lo storico serve anche all'assistente AI e alle segnalazioni.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CorbolIp
{
    // Valori di una rete in 5 minuti (-1 = non misurato)
    class QualitySample
    {
        public DateTime Time;
        public string Key, Label;
        public int GwMs = -1, GwOk, GwLost;     // router: mediana dei ping riusciti, riusciti, persi
        public int InMs = -1, InOk, InLost;     // Internet (1.1.1.1 / 8.8.8.8)
        public int Sig = -1, Rssi, Link = -1;   // Wi-Fi: segnale medio %, dBm medio, velocità del collegamento
        public long Rx = -1, Tx = -1;           // byte al secondo, media

        public bool HasIn { get { return InOk > 0; } }
        public bool HasGw { get { return GwOk > 0; } }
        public int InTot { get { return InOk + InLost; } }
        public int GwTot { get { return GwOk + GwLost; } }
    }

    // Valori normali di una rete
    class QualityBase
    {
        public int Buckets;                     // righe usate (5 minuti l'una)
        public int InN, GwN, SigN;              // righe con ping a Internet, al router, con segnale
        public int InMed = -1, InP90 = -1, GwMed = -1, GwP90 = -1, SigMed = -1;
        public double InLoss, GwLoss;           // frazione di ping persi
        public DateTime Built;
    }

    class QualityAlert
    {
        public string Key, Label, Text;
    }

    static class NetStats
    {
        public const int BucketMin = 5, KeepDays = 30, BaseDays = 14;
        const int MinBase = 24;                 // almeno 2 ore misurate prima di giudicare
        public static bool Enabled = true;      // registra la qualità (setting quality)
        public static bool Alerts = true;       // notifica quando la rete è peggio del solito (setting qualityalert)
        static readonly object gate = new object();

        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CorbolIp", "quality.log"); }
        }

        // Rete riconoscibile nel tempo: il nome per il Wi-Fi, scheda + gateway per il cavo
        public static string KeyFor(NetLine p)
        {
            if (p == null) return null;
            if (p.IsWifi && !string.IsNullOrEmpty(p.WifiSsid)) return "wifi:" + p.WifiSsid;
            return "lan:" + p.Id + "|" + p.Gateway;
        }

        public static string LabelFor(NetLine p)
        {
            if (p == null) return null;
            if (p.IsWifi && !string.IsNullOrEmpty(p.WifiSsid)) return "Wi-Fi \"" + p.WifiSsid + "\"";
            return p.Name;
        }

        // Testo nella lingua dell'utente, oppure in inglese per l'assistente (lang-check.ps1 riconosce Tr)
        static string Tr(bool ai, string it, string en, params object[] args)
        {
            return ai ? string.Format(CultureInfo.InvariantCulture, en, args) : L.F(it, en, args);
        }

        // ---------- raccolta (thread del ping e thread dell'interfaccia) ----------
        static readonly List<int> gwMs = new List<int>(), inMs = new List<int>(), sigs = new List<int>(), rssis = new List<int>();
        static int gwLost, inLost, link = -1, rateN;
        static double rxSum, txSum;
        static string accKey, accLabel;
        static DateTime accBucket = DateTime.MinValue;

        // g: ms del router (-1 perso, -3 nessun gateway); n: ms di Internet (-1 perso)
        public static void AddPing(int g, int n)
        {
            if (!Enabled) return;
            lock (gate)
            {
                if (g >= 0) gwMs.Add(g); else if (g == -1) gwLost++;
                if (g != -3) { if (n >= 0) inMs.Add(n); else inLost++; }
            }
        }

        public static void AddWifi(int signal, int rssi, int linkMbps)
        {
            if (!Enabled || signal < 0) return;
            lock (gate)
            {
                sigs.Add(signal);
                if (rssi < 0) rssis.Add(rssi);
                if (linkMbps > link) link = linkMbps;
            }
        }

        public static void AddRate(double rx, double tx)
        {
            if (!Enabled) return;
            lock (gate) { rxSum += rx; txSum += tx; rateN++; }
        }

        static DateTime BucketOf(DateTime t)
        {
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute - t.Minute % BucketMin, 0);
        }

        static int Median(List<int> v)
        {
            if (v.Count == 0) return -1;
            var s = v.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }

        static int Pct(List<int> v, double p)
        {
            if (v.Count == 0) return -1;
            var s = v.OrderBy(x => x).ToList();
            return s[Math.Min(s.Count - 1, (int)(s.Count * p))];
        }

        // Ogni 10 secondi dal widget, con la rete in uso (key null = nessuna rete): chiude i 5 minuti quando
        // cambiano l'orario o la rete, li scrive e li confronta con il solito. Restituisce un avviso da mostrare, o null.
        public static QualityAlert Tick(string key, string label)
        {
            if (!Enabled) return null;
            var bucket = BucketOf(DateTime.Now);
            QualitySample s = null;
            lock (gate)
            {
                if (accBucket != DateTime.MinValue && bucket == accBucket && key == accKey) return null;
                if (accBucket != DateTime.MinValue && accKey != null && (gwMs.Count + gwLost + inMs.Count + inLost + sigs.Count + rateN) > 0)
                {
                    s = new QualitySample
                    {
                        Time = accBucket, Key = accKey, Label = accLabel,
                        GwMs = Median(gwMs), GwOk = gwMs.Count, GwLost = gwLost,
                        InMs = Median(inMs), InOk = inMs.Count, InLost = inLost,
                        Sig = sigs.Count > 0 ? (int)Math.Round(sigs.Average()) : -1,
                        Rssi = rssis.Count > 0 ? (int)Math.Round(rssis.Average()) : 0,
                        Link = link,
                        Rx = rateN > 0 ? (long)(rxSum / rateN) : -1, Tx = rateN > 0 ? (long)(txSum / rateN) : -1
                    };
                }
                gwMs.Clear(); inMs.Clear(); sigs.Clear(); rssis.Clear();
                gwLost = inLost = rateN = 0; link = -1; rxSum = txSum = 0;
                accBucket = bucket; accKey = key; accLabel = label;
            }
            if (s == null) return null;
            Append(s);
            return Evaluate(s);
        }

        // ---------- file ----------
        static string Line(QualitySample s)
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join("\t", new[]
            {
                s.Time.ToString("yyyy-MM-dd HH:mm", c), Clean(s.Key), Clean(s.Label),
                s.GwMs.ToString(c), s.GwOk.ToString(c), s.GwLost.ToString(c),
                s.InMs.ToString(c), s.InOk.ToString(c), s.InLost.ToString(c),
                s.Sig.ToString(c), s.Rssi.ToString(c), s.Link.ToString(c),
                s.Rx.ToString(c), s.Tx.ToString(c)
            }) + "\r\n";
        }

        static string Clean(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        static QualitySample Parse(string l)
        {
            var p = l.Split('\t');
            if (p.Length < 14) return null;
            DateTime t;
            if (!DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            Func<int, int> I = i => { int v; return int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1; };
            Func<int, long> Lg = i => { long v; return long.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1; };
            return new QualitySample
            {
                Time = t, Key = p[1], Label = p[2],
                GwMs = I(3), GwOk = Math.Max(0, I(4)), GwLost = Math.Max(0, I(5)),
                InMs = I(6), InOk = Math.Max(0, I(7)), InLost = Math.Max(0, I(8)),
                Sig = I(9), Rssi = I(10), Link = I(11), Rx = Lg(12), Tx = Lg(13)
            };
        }

        static void Append(QualitySample s)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.AppendAllText(FilePath, Line(s), new UTF8Encoding(false));
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
                    var keep = lines.Where(l => { var s = Parse(l); return s != null && s.Time >= limit; }).ToArray();
                    if (keep.Length != lines.Length) File.WriteAllLines(FilePath, keep, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        // Righe dal più vecchio, nell'intervallo [from, to), di una rete (key null = tutte)
        public static List<QualitySample> Read(DateTime from, DateTime to, string key = null)
        {
            var res = new List<QualitySample>();
            lock (gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return res;
                    foreach (var l in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        var s = Parse(l);
                        if (s != null && s.Time >= from && s.Time < to && (key == null || s.Key == key)) res.Add(s);
                    }
                }
                catch { }
            }
            return res;
        }

        // ---------- valori normali ----------
        static readonly Dictionary<string, QualityBase> baseCache = new Dictionary<string, QualityBase>();

        public static QualityBase BaseFrom(List<QualitySample> list)
        {
            var b = new QualityBase { Buckets = list.Count, Built = DateTime.Now };
            var inV = list.Where(s => s.HasIn).Select(s => s.InMs).ToList();
            var gwV = list.Where(s => s.HasGw).Select(s => s.GwMs).ToList();
            var sgV = list.Where(s => s.Sig >= 0).Select(s => s.Sig).ToList();
            b.InN = inV.Count; b.GwN = gwV.Count; b.SigN = sgV.Count;
            b.InMed = Median(inV); b.InP90 = Pct(inV, 0.9);
            b.GwMed = Median(gwV); b.GwP90 = Pct(gwV, 0.9);
            b.SigMed = Median(sgV);
            int inTot = list.Sum(s => s.InTot), gwTot = list.Sum(s => s.GwTot);
            b.InLoss = inTot > 0 ? list.Sum(s => s.InLost) / (double)inTot : 0;
            b.GwLoss = gwTot > 0 ? list.Sum(s => s.GwLost) / (double)gwTot : 0;
            return b;
        }

        // Valori normali di una rete negli ultimi 14 giorni (esclusa l'ultima ora); ricalcolati ogni ora
        public static QualityBase Usual(string key)
        {
            QualityBase b;
            lock (baseCache)
                if (baseCache.TryGetValue(key, out b) && DateTime.Now - b.Built < TimeSpan.FromHours(1)) return b;
            var now = DateTime.Now;
            b = BaseFrom(Read(now.AddDays(-BaseDays), now.AddHours(-1), key));
            lock (baseCache) baseCache[key] = b;
            return b;
        }

        // ---------- confronto con il solito ----------
        class State
        {
            public bool Bad;
            public int GoodStreak;
            public DateTime BadSince, LastAlert = DateTime.MinValue;
        }
        static readonly Dictionary<string, State> states = new Dictionary<string, State>();
        static readonly Dictionary<string, List<QualitySample>> recent = new Dictionary<string, List<QualitySample>>();

        // Cosa c'è di peggio del solito in queste righe (vuoto = niente)
        public static List<string> Problems(List<QualitySample> cur, QualityBase b, bool ai)
        {
            var res = new List<string>();
            var inCur = cur.Where(s => s.HasIn).Select(s => s.InMs).ToList();
            var gwCur = cur.Where(s => s.HasGw).Select(s => s.GwMs).ToList();
            var sgCur = cur.Where(s => s.Sig >= 0).Select(s => s.Sig).ToList();
            int gwTot = cur.Sum(s => s.GwTot), gwLost = cur.Sum(s => s.GwLost);
            int inTot = cur.Sum(s => s.InTot), inLost = cur.Sum(s => s.InLost);

            bool gwLossy = b.GwN >= MinBase && gwTot >= 4 && gwLost >= 2 && gwLost / (double)gwTot >= 0.15 && b.GwLoss < 0.05;
            bool inLossy = b.InN >= MinBase && inTot >= 4 && inLost >= 2 && inLost / (double)inTot >= 0.15 && b.InLoss < 0.05;
            if (gwLossy)
                res.Add(Tr(ai, "il router perde il {0}% dei ping (di solito {1}%)", "the router loses {0}% of pings (usually {1}%)",
                           (int)Math.Round(100.0 * gwLost / gwTot), Math.Round(100 * b.GwLoss, 1)));
            else if (inLossy)
                res.Add(Tr(ai, "Internet perde il {0}% dei ping (di solito {1}%)", "the Internet loses {0}% of pings (usually {1}%)",
                           (int)Math.Round(100.0 * inLost / inTot), Math.Round(100 * b.InLoss, 1)));

            if (b.InN >= MinBase && inCur.Count >= 2)
            {
                int m = Median(inCur);
                if (m > Math.Max(Math.Max(b.InMed * 3, b.InMed + 40), b.InP90 * 3 / 2))
                    res.Add(Tr(ai, "ping Internet {0} ms (di solito {1} ms)", "Internet ping {0} ms (usually {1} ms)", m, b.InMed));
            }
            if (b.GwN >= MinBase && gwCur.Count >= 2)
            {
                int m = Median(gwCur);
                if (m > Math.Max(Math.Max(b.GwMed * 3, b.GwMed + 15), b.GwP90 * 3 / 2))
                    res.Add(Tr(ai, "ping del router {0} ms (di solito {1} ms)", "router ping {0} ms (usually {1} ms)", m, b.GwMed));
            }
            if (b.SigN >= MinBase && sgCur.Count >= 2)
            {
                int sg = (int)Math.Round(sgCur.Average());
                if (sg < b.SigMed - 25 && sg < 50)
                    res.Add(Tr(ai, "segnale Wi-Fi {0}% (di solito {1}%)", "Wi-Fi signal {0}% (usually {1}%)", sg, b.SigMed));
            }
            return res;
        }

        static QualityAlert Evaluate(QualitySample s)
        {
            List<QualitySample> cur;
            lock (recent)
            {
                if (!recent.TryGetValue(s.Key, out cur)) recent[s.Key] = cur = new List<QualitySample>();
                cur.Add(s);
                cur.RemoveAll(x => x.Time < s.Time.AddMinutes(-2 * BucketMin));   // ultimi 15 minuti
                cur = cur.ToList();
            }
            var problems = Problems(cur, Usual(s.Key), false);
            State st;
            if (!states.TryGetValue(s.Key, out st)) states[s.Key] = st = new State();
            if (problems.Count > 0)
            {
                st.GoodStreak = 0;
                if (st.Bad) return null;
                st.Bad = true;
                st.BadSince = cur.Min(x => x.Time);   // dal primo quarto d'ora peggiore
                string text = L.F("{0} peggio del solito: {1}", "{0} worse than usual: {1}", s.Label, string.Join(", ", problems));
                NetEvents.Add("QUALITY", text);
                if (!Alerts || DateTime.Now - st.LastAlert < TimeSpan.FromHours(2)) return null;
                st.LastAlert = DateTime.Now;
                return new QualityAlert { Key = s.Key, Label = s.Label, Text = string.Join(", ", problems) };
            }
            if (st.Bad && ++st.GoodStreak >= 2)
            {
                st.Bad = false;
                var d = s.Time.AddMinutes(BucketMin) - st.BadSince;
                NetEvents.Add("QUALITYOK", L.F("{0} di nuovo nella norma (dopo {1} min)", "{0} back to normal (after {1} min)", s.Label, (int)d.TotalMinutes));
            }
            return null;
        }

        // ---------- testi per lo storico, il report e l'assistente ----------
        static string Rate(long b, bool ai)
        {
            if (b < 0) return "-";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0", ai ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture) + " KB/s";
            return (b / 1048576.0).ToString("0.0", ai ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture) + " MB/s";
        }

        static string Hours(int buckets, bool ai)
        {
            double h = buckets * BucketMin / 60.0;
            return h < 1 ? (int)(h * 60) + " min" : Math.Round(h, h < 10 ? 1 : 0).ToString(ai ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture) + " h";
        }

        // Riassunto di un gruppo di righe: "ping Internet 16 ms, perdite 0,4%, router 3 ms, segnale 70% (min 35%), ↓ 1,2 MB/s"
        public static string Summary(List<QualitySample> list, bool ai)
        {
            var parts = new List<string>();
            var inV = list.Where(s => s.HasIn).Select(s => s.InMs).ToList();
            var gwV = list.Where(s => s.HasGw).Select(s => s.GwMs).ToList();
            int inTot = list.Sum(s => s.InTot), gwTot = list.Sum(s => s.GwTot);
            if (inV.Count > 0) parts.Add(Tr(ai, "ping Internet {0} ms", "Internet ping {0} ms", Median(inV)));
            if (inTot > 0 && list.Sum(s => s.InLost) > 0)
                parts.Add(Tr(ai, "ping persi {0}%", "pings lost {0}%", Math.Round(100.0 * list.Sum(s => s.InLost) / inTot, 1)));
            if (gwV.Count > 0) parts.Add(Tr(ai, "router {0} ms", "router {0} ms", Median(gwV)));
            if (gwTot > 0 && list.Sum(s => s.GwLost) > 0)
                parts.Add(Tr(ai, "ping al router persi {0}%", "router pings lost {0}%", Math.Round(100.0 * list.Sum(s => s.GwLost) / gwTot, 1)));
            var sg = list.Where(s => s.Sig >= 0).Select(s => s.Sig).ToList();
            if (sg.Count > 0) parts.Add(Tr(ai, "segnale Wi-Fi {0}% (minimo {1}%)", "Wi-Fi signal {0}% (lowest {1}%)", (int)Math.Round(sg.Average()), sg.Min()));
            var rx = list.Where(s => s.Rx >= 0).ToList();
            if (rx.Count > 0) parts.Add("↓ " + Rate((long)rx.Average(s => s.Rx), ai) + " ↑ " + Rate((long)rx.Average(s => s.Tx), ai));
            if (inV.Count == 0 && gwV.Count == 0) parts.Add(Tr(ai, "latenza non misurata (ping spento)", "latency not measured (ping was off)"));
            return string.Join(", ", parts);
        }

        public static string UsualText(string key, string label, bool ai)
        {
            if (key == null) return "";
            var b = Usual(key);
            if (b.Buckets == 0)
                return Tr(ai, "Valori normali di {0}: nessun dato finora.", "Usual values of {0}: no data yet.", label);
            var parts = new List<string>();
            if (b.InN >= MinBase) parts.Add(Tr(ai, "ping Internet {0} ms (perdite {1}%)", "Internet ping {0} ms (loss {1}%)", b.InMed, Math.Round(100 * b.InLoss, 1)));
            if (b.GwN >= MinBase) parts.Add(Tr(ai, "router {0} ms", "router {0} ms", b.GwMed));
            if (b.SigN >= MinBase) parts.Add(Tr(ai, "segnale Wi-Fi {0}%", "Wi-Fi signal {0}%", b.SigMed));
            if (b.InN < MinBase) parts.Add(Tr(ai, "latenza non ancora imparata (serve il ping acceso)", "latency not learned yet (needs ping turned on)"));
            return Tr(ai, "Valori normali di {0} (ultimi {1} giorni, {2} di dati): {3}.", "Usual values of {0} (last {1} days, {2} of data): {3}.",
                      label, BaseDays, Hours(b.Buckets, ai), string.Join(", ", parts));
        }

        // Una riga per giorno e per rete, con i peggiori momenti e gli eventi del giorno
        public static string DailyText(int days, bool ai)
        {
            var from = DateTime.Today.AddDays(-(days - 1));
            var all = Read(from, DateTime.MaxValue);
            var events = NetEvents.Between(from, DateTime.MaxValue);
            var sb = new StringBuilder();
            for (var d = from; d <= DateTime.Today; d = d.AddDays(1))
            {
                var day = all.Where(s => s.Time.Date == d).ToList();
                var ev = events.Where(e => e.Time.Date == d).ToList();
                if (day.Count == 0 && ev.Count == 0) continue;
                string date = ai ? d.ToString("yyyy-MM-dd (ddd)", CultureInfo.InvariantCulture) : d.ToString("ddd d MMM", CultureInfo.CurrentCulture);
                var counts = new List<string>();
                int downs = ev.Count(e => e.Kind == "NETDOWN"), pings = ev.Count(e => e.Kind == "GWDOWN" || e.Kind == "INETDOWN");
                int worse = ev.Count(e => e.Kind == "QUALITY");
                counts.AddRange(EventCounts(downs, pings, worse, ai));
                if (day.Count == 0) { sb.AppendLine("- " + date + ": " + (counts.Count > 0 ? string.Join(", ", counts) : Tr(ai, "nessuna misura", "no measurements"))); continue; }
                foreach (var g in day.GroupBy(s => s.Key))
                {
                    var l = g.ToList();
                    string line = "- " + date + ", " + l[l.Count - 1].Label + " (" + Hours(l.Count, ai) + "): " + Summary(l, ai);
                    // ore peggiori del solito (solo se ce ne sono)
                    var b = Usual(g.Key);
                    var bad = l.GroupBy(s => s.Time.Hour).Where(h => Problems(h.ToList(), b, ai).Count > 0).Select(h => h.Key.ToString("00") + ":00").ToList();
                    if (bad.Count > 0)
                        line += Tr(ai, "; peggio del solito alle {0}", "; worse than usual at {0}", string.Join(", ", bad.Take(6)));
                    sb.AppendLine(line);
                }
                if (counts.Count > 0) sb.AppendLine("  " + string.Join(", ", counts));
            }
            return sb.ToString();
        }

        // "rete scollegata 2 volte, router o Internet senza risposta una volta, ..."
        public static List<string> EventCounts(int downs, int pings, int worse, bool ai)
        {
            var res = new List<string>();
            if (downs == 1) res.Add(Tr(ai, "rete scollegata una volta", "network disconnected once"));
            else if (downs > 1) res.Add(Tr(ai, "rete scollegata {0} volte", "network disconnected {0} times", downs));
            if (pings == 1) res.Add(Tr(ai, "router o Internet senza risposta una volta", "router or Internet not answering once"));
            else if (pings > 1) res.Add(Tr(ai, "router o Internet senza risposta {0} volte", "router or Internet not answering {0} times", pings));
            if (worse == 1) res.Add(Tr(ai, "peggio del solito una volta", "worse than usual once"));
            else if (worse > 1) res.Add(Tr(ai, "peggio del solito {0} volte", "worse than usual {0} times", worse));
            return res;
        }

        // Una riga per ora e per rete; se sono troppe restano solo le ore peggiori del solito
        public static string HourlyText(DateTime from, DateTime to, bool ai, int maxLines)
        {
            var all = Read(from, to);
            var rows = new List<KeyValuePair<bool, string>>();
            foreach (var g in all.GroupBy(s => new { s.Key, H = new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, 0, 0) }).OrderBy(g => g.Key.H))
            {
                var l = g.ToList();
                bool bad = Problems(l, Usual(g.Key.Key), ai).Count > 0;
                string when = ai ? g.Key.H.ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture) : g.Key.H.ToString("ddd HH:00", CultureInfo.CurrentCulture);
                rows.Add(new KeyValuePair<bool, string>(bad, "- " + when + ", " + l[l.Count - 1].Label + (l.Count < 12 ? " (" + Hours(l.Count, ai) + ")" : "") + ": "
                                                            + Summary(l, ai) + (bad ? Tr(ai, " · PEGGIO DEL SOLITO", " · WORSE THAN USUAL") : "")));
            }
            var sb = new StringBuilder();
            if (rows.Count > maxLines)
            {
                var badRows = rows.Where(r => r.Key).ToList();
                sb.AppendLine(Tr(ai, "({0} ore misurate: qui solo le {1} peggiori del solito)", "({0} hours measured: only the {1} worse than usual are listed)", rows.Count, Math.Min(badRows.Count, maxLines)));
                rows = badRows.Skip(Math.Max(0, badRows.Count - maxLines)).ToList();
            }
            foreach (var r in rows) sb.AppendLine(r.Value);
            return sb.ToString();
        }

        // Nomi di reti Wi-Fi negli eventi e nello storico, per l'anonimizzazione
        public static void AddPrivateNames(Anonymizer a)
        {
            foreach (var e in NetEvents.Recent(KeepDays * 24))
                foreach (Match m in Regex.Matches(e.Text, "\"([^\"]+)\""))
                    a.Add(m.Groups[1].Value, "WIFI");
            foreach (var key in Read(DateTime.Now.AddDays(-KeepDays), DateTime.MaxValue).Select(s => s.Key).Distinct())
                if (key.StartsWith("wifi:")) a.Add(key.Substring(5), "WIFI");
        }
    }
}

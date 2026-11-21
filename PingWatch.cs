// Ping dall'esterno: chi ha fatto ping a questo PC.
// - Senza permessi di amministratore: se il firewall lascia arrivare i ping sulle reti attive, quanti ping hanno
//   avuto risposta dall'avvio di Windows (contatori ICMP) e, mentre l'app è aperta, quando sono arrivati
//   (il contatore si legge ogni 30 secondi).
// - Chi li ha fatti: Windows lo sa solo se il registro del firewall è acceso. Con una conferma di amministratore
//   (<app>.exe /pinglog on <SID>) l'app lo accende e lo fa scrivere in %ProgramData%\<app>\Firewall, leggibile
//   dall'utente: da lì in poi lo legge senza altri permessi. /pinglog off lo spegne e cancella il registro.
// Il registro del firewall è uno solo per tutto il PC: CorbolSys e CorbolIp usano questo stesso file (cambiano solo
// namespace e App) e ognuna riconosce come proprio anche il registro acceso dall'altra.
// Nessun traffico di rete: gli indirizzi non vengono risolti in nomi.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace CorbolIp
{
    class PingEvent
    {
        public DateTime Time;
        public string From = "";     // vuoto = non noto (dal contatore)
        public bool Allowed = true;  // false = bloccato dal firewall
        public int Count = 1;
    }

    // Ping di uno stesso indirizzo, con lo stesso esito
    class PingSource
    {
        public string From = "";
        public bool Allowed;
        public int Count;
        public DateTime First, Last;

        public string Where
        {
            get
            {
                IPAddress a;
                if (!IPAddress.TryParse(From, out a)) return "";
                return PingWatch.IsLocal(a) ? L.T("rete locale", "local network") : "Internet";
            }
        }
    }

    static class PingWatch
    {
        const string App = "CorbolIp";
        static readonly string[] Apps = { "CorbolSys", "CorbolIp" };   // le app che sanno leggere il registro

        // Chiamato (da un thread in background) con una riga di testo per ogni gruppo di ping nuovi mentre l'app è
        // aperta: CorbolIp li scrive nel registro eventi (in CorbolSys resta null).
        public static Action<string> OnPings = null;

        // ---------- contatori di Windows (dall'avvio) ----------
        [DllImport("iphlpapi.dll")] static extern uint GetIcmpStatisticsEx(IntPtr stats, uint family);

        // MIB_ICMP_EX: ingresso e uscita, ognuno con messaggi, errori e 256 contatori per tipo
        const int StatsSize = 8 + 256 * 4;

        // Ping a cui il PC ha risposto (risposte di eco inviate, IPv4 + IPv6); -1 = non leggibile
        public static long Answered()
        {
            long n = -1;
            IntPtr buf = Marshal.AllocHGlobal(2 * StatsSize);
            try
            {
                foreach (var f in new[] { Tuple.Create(2u, 0), Tuple.Create(23u, 129) })   // IPv4: risposta tipo 0; IPv6: tipo 129
                {
                    if (GetIcmpStatisticsEx(buf, f.Item1) != 0) continue;
                    if (n < 0) n = 0;
                    n += (uint)Marshal.ReadInt32(buf, StatsSize + 8 + f.Item2 * 4);
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
            return n;
        }

        // ---------- firewall: lascia arrivare i ping? ----------
        public static int Profiles;   // reti attive: 1 dominio, 2 privata, 4 pubblica (dopo AnswersPing)

        static object Get(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args.Length > 0 ? args : null);
        }

        // Il firewall lascia arrivare i ping (richiesta di eco ICMPv4) sulle reti attive? null = non noto
        public static bool? AnswersPing()
        {
            try
            {
                var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (t == null) return null;
                object pol = Activator.CreateInstance(t);
                int profiles = Convert.ToInt32(Get(pol, "CurrentProfileTypes"));
                Profiles = profiles;
                bool on = false, block = true;
                foreach (int p in new[] { 1, 2, 4 })
                {
                    if ((profiles & p) == 0) continue;
                    if (Convert.ToBoolean(Get(pol, "FirewallEnabled", p))) on = true;
                    if (Convert.ToInt32(Get(pol, "DefaultInboundAction", p)) == 1) block = false;
                }
                if (!on) return true;
                bool allow = false;
                foreach (object r in (IEnumerable)Get(pol, "Rules"))
                {
                    try
                    {
                        if (Convert.ToInt32(Get(r, "Direction")) != 1 || !Convert.ToBoolean(Get(r, "Enabled"))) continue;
                        if ((Convert.ToInt32(Get(r, "Profiles")) & profiles) == 0) continue;
                        // regole di un'app dello Store (con pacchetto o utente proprietario): valgono solo per quell'app
                        string package = "", owner = "";
                        try { package = Convert.ToString(Get(r, "LocalAppPackageId") ?? ""); } catch { }
                        try { owner = Convert.ToString(Get(r, "LocalUserOwner") ?? ""); } catch { }
                        if (package.Length > 0 || owner.Length > 0) continue;
                        int protocol = Convert.ToInt32(Get(r, "Protocol"));
                        string app = Convert.ToString(Get(r, "ApplicationName") ?? ""), service = Convert.ToString(Get(r, "ServiceName") ?? "");
                        bool icmp = protocol == 1 && IcmpMatches(Convert.ToString(Get(r, "IcmpTypesAndCodes") ?? ""), 8);
                        bool any = protocol == 256 && app.Length == 0 && (service.Length == 0 || service == "*");   // i ping li riceve Windows, non un programma
                        if (!icmp && !any) continue;
                        if (Convert.ToInt32(Get(r, "Action")) != 1) return false;   // le regole che bloccano vincono
                        allow = true;
                    }
                    catch { }
                    finally { if (Marshal.IsComObject(r)) Marshal.ReleaseComObject(r); }
                }
                return allow || !block;
            }
            catch { return null; }
        }

        // "8:*", "8:0,0:*", "*" o vuoto (= tutti i tipi)
        static bool IcmpMatches(string spec, int type)
        {
            if (string.IsNullOrEmpty(spec) || spec.Trim() == "*") return true;
            foreach (var raw in spec.Split(','))
            {
                string t = raw.Split(':')[0].Trim();
                int n;
                if (t == "*" || (int.TryParse(t, out n) && n == type)) return true;
            }
            return false;
        }

        // ---------- mentre l'app è aperta: quando arrivano ----------
        static readonly List<PingEvent> seen = new List<PingEvent>();
        static long last = -1;
        static string tailFile;   // registro letto fin qui (per i ping nuovi)
        static long tailPos = -1;
        static Timer timer;

        public static void Start()
        {
            if (timer == null) timer = new Timer(delegate { Sample(); }, null, 0, 30000);
        }

        static void Sample()
        {
            try
            {
                long n = Answered();
                long delta = 0;
                lock (seen)
                {
                    if (n >= 0 && last >= 0 && n > last)
                    {
                        delta = n - last;
                        seen.Add(new PingEvent { Time = DateTime.Now, Count = (int)Math.Min(delta, int.MaxValue) });
                        if (seen.Count > 500) seen.RemoveAt(0);
                    }
                    if (n >= 0) last = n;
                }
                if (OnPings == null) return;
                // col registro acceso si sa anche chi (e si vedono anche i ping bloccati); altrimenti solo quanti
                var news = NewFromLog();
                if (news != null)
                {
                    foreach (var s in Group(news).OrderBy(x => x.First))
                        OnPings(s.Allowed
                            ? L.F("Ping ricevuti da {0} ({1}): {2}, con risposta", "Pings received from {0} ({1}): {2}, answered", s.From, s.Where, s.Count)
                            : L.F("Ping ricevuti da {0} ({1}): {2}, bloccati dal firewall", "Pings received from {0} ({1}): {2}, blocked by the firewall", s.From, s.Where, s.Count));
                }
                else if (delta > 0)
                    OnPings(L.F("Ping ricevuti: {0}, con risposta (chi li ha fatti si vede solo col registro del firewall acceso)",
                                "Pings received: {0}, answered (who sent them shows only with the firewall log on)", delta));
            }
            catch { }
        }

        public static List<PingEvent> SeenWhileOpen()
        {
            lock (seen) return seen.ToList();
        }

        // Ping scritti nel registro dall'ultima lettura; null = registro spento o non leggibile.
        // La prima volta si parte dalla fine: niente ping vecchi.
        static List<PingEvent> NewFromLog()
        {
            string file = CurrentLogFile();
            if (file == null || !File.Exists(file)) { tailFile = null; return null; }
            long len;
            try { len = new FileInfo(file).Length; } catch { return null; }
            if (!string.Equals(file, tailFile, StringComparison.OrdinalIgnoreCase) || tailPos < 0) { tailFile = file; tailPos = len; return new List<PingEvent>(); }
            if (len < tailPos) tailPos = 0;   // il registro è ricominciato (il vecchio è diventato .old)
            if (len == tailPos) return new List<PingEvent>();
            bool readable;
            var res = Parse(file, tailPos, out readable);
            tailPos = len;
            return readable ? res : null;
        }

        // ---------- registro del firewall ----------
        static string AppLogDir(string app)
        {
            return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), app), "Firewall");
        }
        public static string LogDir { get { return AppLogDir(App); } }
        const int LogSizeKb = 16384;

        const string PolicyKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
        static readonly string[] ProfileKeys = { "DomainProfile", "StandardProfile", "PublicProfile" };

        public enum LogState { Off, Ours, Elsewhere }

        // Registro acceso su tutte le reti: il file in cui scrive (null se spento o diverso tra le reti)
        static string ActiveLogFile(out bool any)
        {
            any = false;
            string file = null;
            bool same = true;
            try
            {
                foreach (var p in ProfileKeys)
                    using (var k = Registry.LocalMachine.OpenSubKey(PolicyKey + "\\" + p + "\\Logging"))
                    {
                        if (k == null) { same = false; continue; }
                        bool on = Convert.ToInt32(k.GetValue("LogDroppedPackets", 0)) != 0 || Convert.ToInt32(k.GetValue("LogSuccessfulConnections", 0)) != 0;
                        string path = Environment.ExpandEnvironmentVariables(Convert.ToString(k.GetValue("LogFilePath", "")));
                        if (on) any = true;
                        if (!on) same = false;
                        else if (file == null) file = path;
                        else if (!string.Equals(file, path, StringComparison.OrdinalIgnoreCase)) same = false;
                    }
            }
            catch { return null; }
            return same ? file : null;
        }

        // Il registro acceso da CorbolSys o da CorbolIp (leggibile dall'utente); null = nessuno dei due
        public static string CurrentLogFile()
        {
            bool any;
            string file = ActiveLogFile(out any);
            if (file == null) return null;
            foreach (var app in Apps)
                if (string.Equals(Path.GetDirectoryName(file), AppLogDir(app), StringComparison.OrdinalIgnoreCase)) return file;
            return null;
        }

        // Ours = registro acceso su tutte le reti, nella cartella di CorbolSys o di CorbolIp; Elsewhere = acceso da
        // qualcun altro in un'altra cartella (di solito non leggibile senza permessi di amministratore)
        public static LogState Logging()
        {
            bool any;
            ActiveLogFile(out any);
            return CurrentLogFile() != null ? LogState.Ours : any ? LogState.Elsewhere : LogState.Off;
        }

        // Accende o spegne il registro chiedendo la conferma di amministratore. Chiamare fuori dal thread
        // dell'interfaccia. false = annullato o non riuscito.
        public static bool SetLoggingElevated(bool on)
        {
            try
            {
                var psi = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath,
                    "/pinglog " + (on ? "on" : "off") + " " + WindowsIdentity.GetCurrent().User.Value)
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(60000)) return false;
                    return p.ExitCode == 0;
                }
            }
            catch (Win32Exception) { return false; }   // conferma annullata
            catch { return false; }
        }

        // Eseguito come amministratore (<app>.exe /pinglog on|off <SID dell'utente>)
        public static int SetLogging(bool on, string userSid)
        {
            try
            {
                if (on)
                {
                    Directory.CreateDirectory(LogDir);
                    // la cartella la scrive il servizio del firewall e la legge l'utente che l'ha chiesto, nessun altro
                    var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                    var sec = new DirectorySecurity();
                    sec.SetAccessRuleProtection(true, false);
                    sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                    sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                    sec.AddAccessRule(new FileSystemAccessRule(new NTAccount("NT SERVICE", "MpsSvc"), FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
                    if (!string.IsNullOrEmpty(userSid))
                        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(userSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                    Directory.SetAccessControl(LogDir, sec);

                    bool ok = Netsh("logging filename \"" + Path.Combine(LogDir, "pfirewall.log") + "\"");
                    ok &= Netsh("logging maxfilesize " + LogSizeKb);
                    ok &= Netsh("logging droppedconnections enable");
                    ok &= Netsh("logging allowedconnections enable");
                    return ok ? 0 : 1;
                }
                else
                {
                    string file = CurrentLogFile();   // anche quello acceso dall'altra app
                    bool ok = Netsh("logging allowedconnections disable");
                    ok &= Netsh("logging droppedconnections disable");
                    ok &= Netsh("logging filename \"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"LogFiles\Firewall\pfirewall.log") + "\"");
                    ok &= Netsh("logging maxfilesize 4096");
                    // il servizio chiude il file poco dopo: qualche tentativo
                    string dir = file != null ? Path.GetDirectoryName(file) : LogDir;
                    for (int i = 0; i < 10 && Directory.Exists(dir); i++)
                    {
                        try { Directory.Delete(dir, true); } catch { Thread.Sleep(500); }
                    }
                    return ok ? 0 : 1;
                }
            }
            catch { return 1; }
        }

        static bool Netsh(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "advfirewall set allprofiles " + args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // Ping ricevuti scritti nel registro (anche nel file precedente, pfirewall.log.old), dal più vecchio.
        // readable = false se il registro non c'è o non si legge.
        public static List<PingEvent> FromLog(out bool readable)
        {
            readable = false;
            string file = CurrentLogFile();
            if (file == null) return new List<PingEvent>();
            return ParseLog(new[] { file + ".old", file }, out readable);
        }

        public static List<PingEvent> ParseLog(IEnumerable<string> files, out bool readable)
        {
            var res = new List<PingEvent>();
            readable = false;
            foreach (var file in files)
            {
                if (!File.Exists(file)) continue;
                bool ok;
                res.AddRange(Parse(file, 0, out ok));
                readable |= ok;
            }
            return res;
        }

        // Le righe da 'from' in poi (byte) di un file del registro
        static List<PingEvent> Parse(string file, long from, out bool readable)
        {
            var res = new List<PingEvent>();
            readable = false;
            var mine = LocalAddresses();
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (from > 0 && from < fs.Length) fs.Seek(from, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs))
                    {
                        readable = true;
                        // campi standard; la riga "#Fields:" del file li conferma
                        var idx = Fields("date time action protocol src-ip dst-ip src-port dst-port size tcpflags tcpsyn tcpack tcpwin icmptype icmpcode info path pid");
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line.StartsWith("#"))
                            {
                                if (line.StartsWith("#Fields:")) idx = Fields(line.Substring(8));
                                continue;
                            }
                            var f = line.Split(' ');
                            Func<string, string> get = name => { int i; return idx.TryGetValue(name, out i) && i < f.Length ? f[i] : ""; };
                            string proto = get("protocol").ToUpperInvariant(), type = get("icmptype");
                            bool echo = (proto == "ICMP" || proto == "1") && type == "8" || (proto == "ICMPV6" || proto == "58") && type == "128";
                            if (!echo) continue;
                            string dir = get("path");
                            if (dir.Length > 0 && !dir.Equals("RECEIVE", StringComparison.OrdinalIgnoreCase)) continue;
                            IPAddress a;
                            if (!IPAddress.TryParse(get("src-ip"), out a) || IPAddress.IsLoopback(a) || mine.Contains(a)) continue;   // ping fatti da questo PC
                            DateTime t;
                            if (!DateTime.TryParseExact(get("date") + " " + get("time"), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                                                        System.Globalization.DateTimeStyles.AssumeLocal, out t)) continue;
                            res.Add(new PingEvent { Time = t, From = a.ToString(), Allowed = get("action").Equals("ALLOW", StringComparison.OrdinalIgnoreCase) });
                        }
                    }
                }
            }
            catch { }
            return res;
        }

        static Dictionary<string, int> Fields(string header)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var parts = header.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) d[parts[i]] = i;
            return d;
        }

        static HashSet<IPAddress> LocalAddresses()
        {
            var res = new HashSet<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var u in ni.GetIPProperties().UnicastAddresses) res.Add(u.Address);
            }
            catch { }
            return res;
        }

        // Rete locale: indirizzi privati, link-local e locali IPv6
        public static bool IsLocal(IPAddress a)
        {
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (a.IsIPv4MappedToIPv6) return IsLocal(a.MapToIPv4());
                byte b0 = a.GetAddressBytes()[0];
                return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b0 & 0xFE) == 0xFC;
            }
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // anche la rete condivisa dei provider (CGNAT)
        }

        public static List<PingSource> Group(IEnumerable<PingEvent> events)
        {
            return events.Where(e => e.From.Length > 0)
                .GroupBy(e => e.From + "|" + e.Allowed)
                .Select(g => new PingSource
                {
                    From = g.First().From, Allowed = g.First().Allowed, Count = g.Sum(e => e.Count),
                    First = g.Min(e => e.Time), Last = g.Max(e => e.Time)
                })
                .OrderByDescending(s => s.Last).ToList();
        }

        // ---------- riepilogo ----------
        public static string NetworkText(int profiles)
        {
            var n = new List<string>();
            if ((profiles & 1) != 0) n.Add(L.T("di dominio", "domain"));
            if ((profiles & 2) != 0) n.Add(L.T("privata", "private"));
            if ((profiles & 4) != 0) n.Add(L.T("pubblica", "public"));
            return string.Join(", ", n);
        }

        // Una riga: risponde ai ping? quanti dall'avvio? registro acceso?
        public static string Summary()
        {
            bool? answers = AnswersPing();
            string net = NetworkText(Profiles);
            string s = !answers.HasValue ? L.T("Ping: non so se il firewall li lascia arrivare", "Ping: unknown whether the firewall lets them in")
                     : answers.Value ? L.F("Ping: il PC risponde su questa rete ({0})", "Ping: the PC answers on this network ({0})", net)
                     : L.F("Ping: il PC non risponde su questa rete ({0}), il firewall li blocca", "Ping: the PC doesn't answer on this network ({0}), the firewall blocks them", net);
            long n = Answered();
            if (n >= 0) s += " · " + L.F("{0} ping con risposta dall'avvio di Windows", "{0} pings answered since Windows started", n);
            if (Logging() == LogState.Ours)
            {
                bool readable;
                var ev = FromLog(out readable);
                if (readable)
                {
                    int ips = ev.Select(e => e.From).Distinct().Count();
                    s += " · " + (ev.Count == 0 ? L.T("nessuno nel registro", "none in the log")
                                 : L.F("nel registro: {0} da {1} indirizzi", "in the log: {0} from {1} addresses", ev.Count, ips));
                }
            }
            return s;
        }
    }
}

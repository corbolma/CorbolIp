// Controlli che l'assistente AI può chiedere durante la diagnosi, da una lista chiusa: ping, percorso
// (traceroute), porta TCP, risoluzione DNS, risposta di un indirizzo web, eventi di rete recenti.
// Niente comandi di sistema: tutto con le API di .NET, argomenti controllati, tempi limitati.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AppIP
{
    static class DiagTools
    {
        public const int MaxCalls = 5;  // controlli al massimo per ogni risposta dell'assistente

        static readonly Regex HostRx = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9.\-:]{0,252})$");

        static Dictionary<string, object> Prop(string type, string desc)
        {
            return new Dictionary<string, object> { { "type", type }, { "description", desc } };
        }

        static Dictionary<string, object> Schema(Dictionary<string, object> props, params string[] required)
        {
            return new Dictionary<string, object>
            {
                { "type", "object" }, { "properties", props }, { "required", required }, { "additionalProperties", false }
            };
        }

        public static List<AiTool> Definitions()
        {
            var host = Prop("string", "Host name or IP address, e.g. 8.8.8.8 or teams.microsoft.com");
            return new List<AiTool>
            {
                new AiTool { Name = "ping", Description = "Sends 4 ICMP echo requests to a host and reports replies and round-trip times. Note that many company networks and some routers block ping.",
                             Schema = Schema(new Dictionary<string, object> { { "host", host } }, "host") },
                new AiTool { Name = "traceroute", Description = "Shows the route (hops) to a host, up to 20 hops, to see where the connection stops or slows down.",
                             Schema = Schema(new Dictionary<string, object> { { "host", host } }, "host") },
                new AiTool { Name = "tcp_port", Description = "Tries to open a TCP connection to a host and port (e.g. 443 for HTTPS, 3389 for Remote Desktop) and reports whether it is reachable.",
                             Schema = Schema(new Dictionary<string, object> { { "host", host }, { "port", Prop("integer", "TCP port number, 1-65535") } }, "host", "port") },
                new AiTool { Name = "dns_lookup", Description = "Resolves a host name with the PC's DNS servers and reports the addresses and the time taken.",
                             Schema = Schema(new Dictionary<string, object> { { "name", Prop("string", "Host name to resolve, e.g. outlook.office365.com") } }, "name") },
                new AiTool { Name = "http_check", Description = "Requests a web address (http or https) through the system proxy and reports the HTTP status and response time, or the error.",
                             Schema = Schema(new Dictionary<string, object> { { "url", Prop("string", "Full address, e.g. https://teams.microsoft.com") } }, "url") },
                new AiTool { Name = "recent_events", Description = "Returns the network events recorded on this PC (disconnections, network or IP changes, VPN on/off, router or Internet not answering, sleep/resume) in the last hours.",
                             Schema = Schema(new Dictionary<string, object> { { "hours", Prop("integer", "How many hours back, 1-168") } }, "hours") }
            };
        }

        static string Arg(AiToolCall c, string key)
        {
            object v;
            return c.Args != null && c.Args.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : "";
        }

        static int IntArg(AiToolCall c, string key, int def)
        {
            int v;
            return int.TryParse(Arg(c, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        // Descrizione breve per l'utente: "ping 8.8.8.8"
        public static string Describe(AiToolCall c)
        {
            switch (c.Name)
            {
                case "ping": return "ping " + Arg(c, "host");
                case "traceroute": return "tracert " + Arg(c, "host");
                case "tcp_port": return L.F("porta {0} di {1}", "port {0} on {1}", Arg(c, "port"), Arg(c, "host"));
                case "dns_lookup": return "DNS " + Arg(c, "name");
                case "http_check": return Arg(c, "url");
                case "recent_events": return L.F("eventi delle ultime {0} ore", "events of the last {0} hours", Arg(c, "hours"));
            }
            return c.Name ?? "?";
        }

        public static string Execute(AiToolCall c, CancellationToken cancel)
        {
            switch (c.Name)
            {
                case "ping": return Ping(CheckHost(Arg(c, "host")), cancel);
                case "traceroute": return Trace(CheckHost(Arg(c, "host")), cancel);
                case "tcp_port":
                {
                    int port = IntArg(c, "port", 0);
                    if (port < 1 || port > 65535) return "Error: port must be 1-65535.";
                    return Tcp(CheckHost(Arg(c, "host")), port);
                }
                case "dns_lookup": return Lookup(CheckHost(Arg(c, "name")));
                case "http_check": return Http(Arg(c, "url"));
                case "recent_events":
                {
                    int h = Math.Max(1, Math.Min(168, IntArg(c, "hours", 24)));
                    string t = NetEvents.Text(h, 60, true);
                    return t.Length > 0 ? t : (NetEvents.Enabled ? "No events recorded in the last " + h + " hours." : "The event log is turned off.");
                }
            }
            return "Error: unknown tool " + c.Name + ".";
        }

        static readonly Regex UrlRx = new Regex(@"https?://[^\s""'<>()]+", RegexOptions.IgnoreCase);
        static readonly Regex DomainRx = new Regex(@"(?<![\w.@-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}(?![\w-])", RegexOptions.IgnoreCase);
        static readonly Regex Ipv4Rx = new Regex(@"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])");

        // Siti, indirizzi web e IP citati dall'utente nella descrizione del problema (al massimo 3)
        public static List<string> TargetsIn(string text)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(text)) return res;
            foreach (Match m in UrlRx.Matches(text)) res.Add(m.Value.TrimEnd('.', ',', ';', ':', '!', '?'));
            string rest = UrlRx.Replace(text, " ");
            foreach (Match m in Ipv4Rx.Matches(rest)) res.Add(m.Value);
            foreach (Match m in DomainRx.Matches(Ipv4Rx.Replace(rest, " ")))
            {
                string d = m.Value.TrimEnd('.');
                if (!res.Any(r => r.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0)) res.Add(d);
            }
            return res.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        }

        static AiToolCall Call(string name, string key, object value)
        {
            return new AiToolCall { Name = name, Args = new Dictionary<string, object> { { key, value } } };
        }

        // Controlli di AppIP su un sito, indirizzo web o IP: DNS e risposta web, oppure ping
        public static IEnumerable<AiToolCall> ChecksFor(string target)
        {
            if (Ipv4Rx.IsMatch(target) && Ipv4Rx.Match(target).Value == target)
                return new[] { Call("ping", "host", target) };
            Uri u;
            if (Uri.TryCreate(target, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https"))
                return new[] { Call("dns_lookup", "name", u.Host), Call("http_check", "url", target) };
            return new[] { Call("dns_lookup", "name", target), Call("http_check", "url", "https://" + target) };
        }

        static string CheckHost(string h)
        {
            if (!HostRx.IsMatch(h ?? "")) throw new ArgumentException("invalid host name: " + h);
            return h;
        }

        static IPAddress ResolveOne(string host)
        {
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return ip;
            var ar = Dns.BeginGetHostAddresses(host, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(8000)) throw new Exception("DNS: no answer for " + host);
            var all = Dns.EndGetHostAddresses(ar);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault();
        }

        static string Ping(string host, CancellationToken cancel)
        {
            IPAddress ip;
            try { ip = ResolveOne(host); } catch (Exception ex) { return "Could not resolve " + host + ": " + ex.Message; }
            var times = new List<long>();
            var statuses = new List<string>();
            using (var p = new System.Net.NetworkInformation.Ping())
                for (int i = 0; i < 4 && !cancel.IsCancellationRequested; i++)
                {
                    try
                    {
                        var r = p.Send(ip, 1000);
                        if (r.Status == IPStatus.Success) times.Add(r.RoundtripTime); else statuses.Add(r.Status.ToString());
                    }
                    catch (Exception ex) { statuses.Add(ex.Message); }
                    if (i < 3) Thread.Sleep(200);
                }
            string head = host + (host != ip.ToString() ? " (" + ip + ")" : "") + ": " + times.Count + "/4 replies";
            if (times.Count == 0) return head + (statuses.Count > 0 ? ", " + statuses.Distinct().First() : "");
            return head + ", min/avg/max " + times.Min() + "/" + (long)times.Average() + "/" + times.Max() + " ms";
        }

        static string Trace(string host, CancellationToken cancel)
        {
            IPAddress ip;
            try { ip = ResolveOne(host); } catch (Exception ex) { return "Could not resolve " + host + ": " + ex.Message; }
            var sb = new StringBuilder("Route to " + host + " (" + ip + "):\n");
            var buf = new byte[32];
            int silent = 0;
            using (var p = new System.Net.NetworkInformation.Ping())
                for (int ttl = 1; ttl <= 20 && !cancel.IsCancellationRequested; ttl++)
                {
                    var sw = Stopwatch.StartNew();
                    PingReply r = null;
                    try { r = p.Send(ip, 1000, buf, new PingOptions(ttl, true)); } catch { }
                    long ms = sw.ElapsedMilliseconds;
                    if (r == null || r.Status == IPStatus.TimedOut)
                    {
                        sb.AppendLine(ttl + "  *");
                        if (++silent >= 4) { sb.AppendLine("(stopped: 4 hops in a row did not answer)"); break; }
                        continue;
                    }
                    silent = 0;
                    sb.AppendLine(ttl + "  " + r.Address + "  " + ms + " ms");
                    if (r.Status == IPStatus.Success) { sb.AppendLine("(destination reached)"); break; }
                    if (r.Status != IPStatus.TtlExpired && r.Status != IPStatus.TimeExceeded) { sb.AppendLine("(" + r.Status + ")"); break; }
                }
            return sb.ToString();
        }

        static string Tcp(string host, int port)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using (var tc = new TcpClient())
                {
                    var ar = tc.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(4000)) return host + ":" + port + " no answer within 4 s (filtered or unreachable)";
                    tc.EndConnect(ar);
                    return host + ":" + port + " open (connected in " + sw.ElapsedMilliseconds + " ms)";
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.ConnectionRefused) return host + ":" + port + " closed (connection refused)";
                return host + ":" + port + " error: " + ex.SocketErrorCode;
            }
            catch (Exception ex) { return host + ":" + port + " error: " + ex.Message; }
        }

        static string Lookup(string name)
        {
            var sw = Stopwatch.StartNew();
            var ar = Dns.BeginGetHostAddresses(name, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(8000)) return name + ": no answer from the DNS server within 8 s";
            try
            {
                var all = Dns.EndGetHostAddresses(ar);
                return name + " -> " + string.Join(", ", all.Take(6).Select(a => a.ToString())) + " (" + sw.ElapsedMilliseconds + " ms)";
            }
            catch (SocketException ex) { return name + ": " + (ex.SocketErrorCode == SocketError.HostNotFound ? "name does not exist (NXDOMAIN)" : ex.SocketErrorCode.ToString()); }
        }

        static string Http(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return "Error: url must start with http:// or https://";
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var sw = Stopwatch.StartNew();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(u);
                req.UserAgent = "AppIP/" + AppVersion.Text;
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.AllowAutoRedirect = false;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    string loc = resp.Headers["Location"];
                    return u + ": HTTP " + (int)resp.StatusCode + " " + resp.StatusDescription + " in " + sw.ElapsedMilliseconds + " ms"
                        + (loc != null ? ", redirect to " + loc : "");
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null) return u + ": HTTP " + (int)r.StatusCode + " " + r.StatusDescription + " in " + sw.ElapsedMilliseconds + " ms";
                return u + ": " + ex.Status + " (" + ex.Message + ")";
            }
            catch (Exception ex) { return u + ": " + ex.Message; }
        }
    }
}

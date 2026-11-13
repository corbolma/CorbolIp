// Diagnosi della rete: una serie di controlli sempre uguali (scheda, IP, Wi-Fi, router, Internet, DNS,
// pagina di accesso, HTTPS, VPN) e una conclusione a regole in parole semplici.
// Parte solo su richiesta dal menu: nessun traffico automatico.
// "Copia report" prepara un testo da mandare all'assistenza.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CorbolIp
{
    enum DiagState { Wait, Running, Ok, Info, Warn, Error }

    // Dati della rete al momento della diagnosi (presi dal widget sul thread dell'interfaccia)
    class DiagSnapshot
    {
        public NetLine Primary, Vpn;
        public List<NetLine> All = new List<NetLine>();
        public string Host = "", Domain = "";
        public string Dns = "";
        public Func<string> NetText; // dettagli delle schede per il report ("Copia tutto")
    }

    class DiagCheck
    {
        public string Title, Detail = "";
        public DiagState State = DiagState.Wait;
        public ListViewItem Item;
        public bool TrustFailure; // HTTPS: certificato rifiutato
        public bool Captive;      // pagina di accesso (hotel, ospiti...)
        public int Ms = -1;       // tempo medio misurato (ping, DNS)
    }

    class DiagnosticsForm : Form
    {
        const string NcsiUrl = "http://www.msftconnecttest.com/connecttest.txt"; // lo stesso controllo di Windows
        const string NcsiText = "Microsoft Connect Test";
        const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";
        const string DnsDomain = "msftconnecttest.com";
        const int PingCount = 4;

        static DiagnosticsForm open;

        public static void ShowOnce(Icon icon, Func<DiagSnapshot> snapshot, bool demo)
        {
            // se l'assistente è stato acceso o spento, la finestra va ricostruita (pulsanti e riquadro)
            if (open != null && !open.IsDisposed && open.ai != AiSettings.Enabled && open.worker == null && open.aiCts == null) open.Close();
            if (open == null || open.IsDisposed) open = new DiagnosticsForm(icon, snapshot, demo);
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        readonly Func<DiagSnapshot> getSnapshot;
        readonly bool demo;
        readonly ListView list;
        readonly ImageList icons;
        readonly Label lblTitle, lblText;
        readonly Button btnStart, btnCopy;
        readonly DiagCheck cNet, cIp, cWifi, cGw, cInet, cDns, cPortal, cWeb, cVpn;
        readonly DiagCheck[] checks;

        volatile bool cancel;
        Thread worker;
        DiagSnapshot snap;
        DateTime runTime;
        DiagState conclState = DiagState.Wait;
        string conclTitle = "", conclText = "";
        string pubIp; // IP pubblico visto dal controllo HTTPS (per l'anonimizzazione)

        // assistente AI
        readonly bool ai;
        enum AiKind { Explain, Ticket, Question }
        readonly Button btnExplain, btnTicket, btnNew, btnAsk;
        readonly TextBox txtAi, txtQuestion;
        CancellationTokenSource aiCts;
        string lastSent;                                            // ultimo testo inviato
        // conversazione con l'assistente: si azzera con Nuova conversazione o con una nuova diagnosi
        readonly List<AiMessage> convo = new List<AiMessage>();     // messaggi inviati (anonimizzati se richiesto)
        readonly StringBuilder transcript = new StringBuilder();    // testo del riquadro (in chiaro)
        readonly StringBuilder chatLog = new StringBuilder();       // spiegazioni e risposte per Copia report
        string ticketText;                                          // ultima segnalazione: Copia report la mette in cima
        Anonymizer anon;

        DiagnosticsForm(Icon icon, Func<DiagSnapshot> snapshot, bool demo)
        {
            getSnapshot = snapshot;
            this.demo = demo;
            Text = L.T("Diagnosi rete - CorbolIp", "Network diagnostics - CorbolIp");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true; // il widget sta sotto le finestre: questa deve vedersi subito
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt); // stessa dimensione del menu
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            cNet = new DiagCheck { Title = L.T("Connessione", "Connection") };
            cIp = new DiagCheck { Title = L.T("Indirizzo IP", "IP address") };
            cWifi = new DiagCheck { Title = "Wi-Fi" };
            cGw = new DiagCheck { Title = L.T("Router (gateway)", "Router (gateway)") };
            cInet = new DiagCheck { Title = L.T("Ping Internet", "Internet ping") };
            cDns = new DiagCheck { Title = "DNS" };
            cPortal = new DiagCheck { Title = L.T("Accesso a Internet", "Internet access") };
            cWeb = new DiagCheck { Title = L.T("Navigazione HTTPS", "HTTPS browsing") };
            cVpn = new DiagCheck { Title = "VPN" };
            checks = new[] { cNet, cIp, cWifi, cGw, cInet, cDns, cPortal, cWeb, cVpn };

            float s = UiFont.Scale;
            int iconSize = (int)Math.Round(16 * s * ScreenDpi() / 96f);
            icons = new ImageList { ImageSize = new Size(iconSize, iconSize), ColorDepth = ColorDepth.Depth32Bit };
            foreach (DiagState st in Enum.GetValues(typeof(DiagState))) icons.Images.Add(st.ToString(), StateIcon(st, iconSize));

            list = new ListView
            {
                View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                MultiSelect = false, ShowItemToolTips = true, SmallImageList = icons,
                Location = new Point(16, 14), Size = new Size(508, 236)
            };
            list.Columns.Add(L.T("Controllo", "Check"));
            list.Columns.Add(L.T("Risultato", "Result"));
            foreach (var c in checks)
            {
                c.Item = new ListViewItem(new[] { c.Title, "" }) { ImageKey = DiagState.Wait.ToString() };
                list.Items.Add(c.Item);
            }

            lblTitle = new Label { Text = "", AutoSize = true, Location = new Point(16, 262), Font = new Font("Segoe UI Semibold", UiFont.Pt + 1f) };
            lblText = new Label { Text = "", AutoSize = false, Location = new Point(16, 286), Size = new Size(508, 72) };

            ai = AiSettings.Enabled;
            int by = 366; // riga dei pulsanti in basso
            var aiCtl = new List<Control>();
            if (ai)
            {
                btnExplain = new Button { Text = L.T("Spiega", "Explain"), Size = new Size(100, 30), Location = new Point(16, 362), Enabled = false };
                btnTicket = new Button { Text = L.T("Prepara segnalazione", "Write support request"), Size = new Size(170, 30), Location = new Point(124, 362), Enabled = false };
                btnNew = new Button { Text = L.T("Nuova conversazione", "New conversation"), Size = new Size(160, 30), Location = new Point(302, 362) };
                txtAi = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
                    Location = new Point(16, 400), Size = new Size(508, 160), ForeColor = Color.DimGray
                };
                txtQuestion = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(16, 568), Size = new Size(412, 52) };
                btnAsk = new Button { Text = L.T("Chiedi", "Ask"), Size = new Size(90, 52), Location = new Point(434, 568), Enabled = false };
                by = 632;
                var lnkSent = new LinkLabel { Text = L.T("Cosa viene inviato", "What is sent"), AutoSize = false, Size = new Size(170, 24), Location = new Point(256, by + 5), LinkBehavior = LinkBehavior.AlwaysUnderline };
                btnExplain.Click += delegate { RunAi(AiKind.Explain, null, btnExplain); };
                btnTicket.Click += delegate { RunAi(AiKind.Ticket, null, btnTicket); };
                btnAsk.Click += delegate { if (aiCts != null) aiCts.Cancel(); else Ask(); };
                btnNew.Click += delegate { if (aiCts == null && worker == null) ResetConversation(true); };
                txtQuestion.KeyDown += (o, e) =>
                {
                    if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Ask(); }   // Maiusc+Invio va a capo
                };
                lnkSent.LinkClicked += delegate { ShowSent(); };
                aiCtl.AddRange(new Control[] { btnExplain, btnTicket, btnNew, txtAi, txtQuestion, btnAsk, lnkSent });
            }

            btnStart = new Button { Text = L.T("Ripeti", "Run again"), Size = new Size(100, 30), Location = new Point(16, by) };
            btnCopy = new Button { Text = L.T("Copia report", "Copy report"), Size = new Size(120, 30), Location = new Point(124, by), Enabled = false };
            var btnClose = new Button { Text = L.T("Chiudi", "Close"), Size = new Size(90, 30), Location = new Point(434, by) };
            btnStart.Click += delegate { if (worker != null) Stop(); else if (aiCts == null) Start(); };
            btnCopy.Click += delegate
            {
                try { Clipboard.SetText(CopyText()); lblTitle.Text = L.T("Report copiato negli appunti.", "Report copied to the clipboard."); lblTitle.ForeColor = Color.DimGray; }
                catch { }
            };
            btnClose.Click += delegate { Close(); };
            CancelButton = btnClose;

            Controls.AddRange(new Control[] { list, lblTitle, lblText, btnStart, btnCopy, btnClose });
            Controls.AddRange(aiCtl.ToArray());
            ClientSize = new Size(540, by + 44);
            Scale(new SizeF(s, s)); // coordinate pensate per 9 pt
            Shown += delegate { FitColumns(); Start(); };
        }

        static float ScreenDpi()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX; } catch { return 96f; }
        }

        // Le colonne non seguono il ridimensionamento automatico per DPI: si adattano alla lista.
        // L'altezza delle righe dipende da font e DPI: la lista cresce fino a mostrarle tutte senza barra.
        void FitColumns()
        {
            int need = list.Items[list.Items.Count - 1].Bounds.Bottom + 4 - list.ClientSize.Height;
            if (need > 0)
            {
                list.Height += need;
                foreach (Control c in Controls) if (c != list) c.Top += need;
                ClientSize = new Size(ClientSize.Width, ClientSize.Height + need);
            }
            int w = list.ClientSize.Width;
            list.Columns[0].Width = (int)(w * 0.34);
            list.Columns[1].Width = w - list.Columns[0].Width - 2;
        }

        static Bitmap StateIcon(DiagState st, int size)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color c;
                switch (st)
                {
                    case DiagState.Ok: c = Color.FromArgb(46, 160, 80); break;
                    case DiagState.Warn: c = Color.FromArgb(230, 145, 0); break;
                    case DiagState.Error: c = Color.FromArgb(210, 55, 55); break;
                    case DiagState.Info: c = Color.FromArgb(120, 140, 170); break;
                    case DiagState.Running: c = Color.FromArgb(60, 130, 220); break;
                    default: c = Color.FromArgb(205, 205, 205); break;
                }
                float m = size * 0.15f, d = size - 2 * m;
                if (st == DiagState.Running || st == DiagState.Wait)
                    using (var pen = new Pen(c, Math.Max(1.5f, size / 9f))) g.DrawEllipse(pen, m, m, d, d);
                else
                    using (var br = new SolidBrush(c)) g.FillEllipse(br, m, m, d, d);
            }
            return bmp;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Stop();
            if (aiCts != null) aiCts.Cancel();
            base.OnFormClosing(e);
        }

        // ---------- avvio / arresto ----------
        void Start()
        {
            try { snap = getSnapshot(); } catch { snap = new DiagSnapshot(); }
            cancel = false;
            runTime = DateTime.Now;
            foreach (var c in checks) { c.State = DiagState.Wait; c.Detail = ""; c.TrustFailure = false; c.Ms = -1; c.Captive = false; ShowCheck(c); }
            conclState = DiagState.Wait;
            conclTitle = conclText = "";
            pubIp = null;
            if (ai)
            {
                btnExplain.Enabled = btnTicket.Enabled = btnAsk.Enabled = false;
                ResetConversation(false);   // i risultati cambiano: la conversazione riparte
                ShowAi(L.T("Al termine dei controlli, Spiega chiede all'assistente cosa significano i risultati e cosa fare; Prepara segnalazione scrive il testo per l'assistenza. " +
                           "Puoi anche fare una domanda qui sotto, per esempio \"perché Teams si blocca?\" o \"il problema è il Wi-Fi o il provider?\".",
                           "When the checks are done, Explain asks the assistant what the results mean and what to do; Write support request drafts the text for IT support. " +
                           "You can also ask a question below, for example \"why does Teams freeze?\" or \"is the problem the Wi-Fi or the provider?\"."), true);
            }
            lblTitle.Text = L.T("Diagnosi in corso...", "Running diagnostics...");
            lblTitle.ForeColor = Color.DimGray;
            lblText.Text = "";
            btnStart.Text = L.T("Interrompi", "Stop");
            btnCopy.Enabled = false;

            var s = snap;
            worker = new Thread(delegate () { if (demo) RunDemo(); else Run(s); });
            worker.IsBackground = true;
            worker.Start();
        }

        void Stop()
        {
            if (worker != null) cancel = true;
        }

        void Finished()
        {
            worker = null;
            btnStart.Text = L.T("Ripeti", "Run again");
            if (cancel)
            {
                foreach (var c in checks) if (c.State == DiagState.Wait || c.State == DiagState.Running) { c.State = DiagState.Wait; c.Detail = L.T("interrotto", "stopped"); ShowCheck(c); }
                lblTitle.Text = L.T("Interrotto.", "Stopped.");
                lblTitle.ForeColor = Color.DimGray;
                return;
            }
            Conclude();
            lblTitle.Text = conclTitle;
            lblTitle.ForeColor = conclState == DiagState.Ok ? Color.FromArgb(30, 130, 60)
                : conclState == DiagState.Error ? Color.FromArgb(190, 40, 40) : Color.FromArgb(190, 110, 0);
            lblText.Text = conclText;
            btnCopy.Enabled = true;
            if (ai) btnExplain.Enabled = btnTicket.Enabled = btnAsk.Enabled = true;
        }

        void Ui(Action a)
        {
            try { if (!IsDisposed) BeginInvoke(a); } catch { }
        }

        void ShowCheck(DiagCheck c)
        {
            c.Item.ImageKey = c.State.ToString();
            c.Item.SubItems[1].Text = c.Detail;
            c.Item.ToolTipText = c.Title + ": " + c.Detail;
        }

        // Aggiorna un controllo dal thread di lavoro
        void Set(DiagCheck c, DiagState st, string detail)
        {
            c.State = st;
            c.Detail = detail ?? "";
            Ui(delegate { ShowCheck(c); });
        }

        // ---------- controlli ----------
        void Run(DiagSnapshot s)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try
            {
                var p = s.Primary;
                CheckNet(p);
                CheckIp(p);
                CheckWifi(p);
                if (!cancel) CheckGateway(p);
                if (!cancel) CheckInternet();
                if (!cancel) CheckDns(s);
                if (!cancel) CheckPortal();
                if (!cancel) CheckWeb();
                CheckVpn(s.Vpn);
                Adjust();
            }
            catch (Exception ex)
            {
                foreach (var c in checks) if (c.State == DiagState.Running) Set(c, DiagState.Error, ex.Message);
            }
            Ui(Finished);
        }

        void CheckNet(NetLine p)
        {
            if (p == null) { Set(cNet, DiagState.Error, L.T("nessuna scheda di rete attiva", "no active network adapter")); return; }
            string what = p.IsWifi
                ? "Wi-Fi" + (!string.IsNullOrEmpty(p.WifiSsid) ? " \"" + p.WifiSsid + "\"" : "")
                : p.Name;
            if (!p.HasGateway) { Set(cNet, DiagState.Error, L.F("{0}: collegata, ma senza gateway", "{0}: connected, but no gateway", what)); return; }
            Set(cNet, DiagState.Ok, what + (p.IsWifi ? "" : " (" + L.T("cavo", "cable") + ")"));
        }

        static bool IsApipa(NetLine p)
        {
            return p != null && p.IPv4Plain.Split(',').Any(a => a.Trim().StartsWith("169.254."));
        }

        void CheckIp(NetLine p)
        {
            if (p == null) { Set(cIp, DiagState.Info, "-"); return; }
            if (IsApipa(p) && p.IPv4Plain.Split(',').All(a => a.Trim().StartsWith("169.254.")))
            {
                Set(cIp, DiagState.Error, L.F("{0}: indirizzo automatico, il DHCP non ha risposto", "{0}: automatic address, DHCP did not answer", p.IPv4Plain));
                return;
            }
            if (p.IPv4Plain == "-") { Set(cIp, DiagState.Error, L.T("nessun indirizzo IPv4", "no IPv4 address")); return; }
            Set(cIp, DiagState.Ok, p.IPv4Plain + " · " + (p.Dhcp ? "DHCP" : L.T("statico", "static")));
        }

        void CheckWifi(NetLine p)
        {
            if (p == null || !p.IsWifi) { Set(cWifi, DiagState.Info, L.T("non in uso", "not in use")); return; }
            var parts = new List<string>();
            var w = p.Wi;
            if (w != null)
            {
                if (w.Band().Length > 0) parts.Add(w.Band());
                if (w.Chan() > 0) parts.Add(L.T("canale", "channel") + " " + w.Chan());
                if (w.Rssi < 0) parts.Add(w.Rssi + " dBm");
            }
            string extra = parts.Count > 0 ? " · " + string.Join(" · ", parts) : "";
            int q = p.WifiSignal;
            if (q < 0) { Set(cWifi, DiagState.Info, L.T("segnale non disponibile", "signal not available") + extra); return; }
            if (q < 25) Set(cWifi, DiagState.Error, L.F("segnale molto debole ({0}%)", "very weak signal ({0}%)", q) + extra);
            else if (q < 50) Set(cWifi, DiagState.Warn, L.F("segnale debole ({0}%)", "weak signal ({0}%)", q) + extra);
            else Set(cWifi, DiagState.Ok, L.F("segnale {0}%", "signal {0}%", q) + extra);
        }

        // Ping ripetuto: restituisce risposte ricevute e tempo medio
        int PingMany(string host, out int avg)
        {
            int ok = 0; long sum = 0;
            using (var ping = new Ping())
            {
                for (int i = 0; i < PingCount && !cancel; i++)
                {
                    try
                    {
                        var r = ping.Send(host, 1000);
                        if (r.Status == IPStatus.Success) { ok++; sum += r.RoundtripTime; }
                    }
                    catch { }
                    if (i < PingCount - 1) Thread.Sleep(200);
                }
            }
            avg = ok > 0 ? (int)(sum / ok) : -1;
            return ok;
        }

        static string Ms(int ms)
        {
            return ms < 1 ? "<1 ms" : ms + " ms";
        }

        void CheckGateway(NetLine p)
        {
            if (p == null || !p.HasGateway) { Set(cGw, DiagState.Error, L.T("nessun gateway", "no gateway")); return; }
            string gw = p.Gateway.Split(',')[0].Trim();
            Set(cGw, DiagState.Running, gw + "...");
            int avg, ok = PingMany(gw, out avg);
            cGw.Ms = avg;
            if (ok == 0) Set(cGw, DiagState.Error, L.F("{0} non risponde", "{0} does not answer", gw));
            else if (ok < PingCount) Set(cGw, DiagState.Warn, L.F("{0}: {1} risposte perse su {2}", "{0}: {1} of {2} replies lost", gw, PingCount - ok, PingCount));
            else if (avg >= 50) Set(cGw, DiagState.Warn, L.F("{0}: risposta lenta ({1})", "{0}: slow reply ({1})", gw, Ms(avg)));
            else Set(cGw, DiagState.Ok, gw + " · " + Ms(avg));
        }

        void CheckInternet()
        {
            Set(cInet, DiagState.Running, "1.1.1.1...");
            string host = "1.1.1.1";
            int avg, ok = PingMany(host, out avg);
            if (ok == 0 && !cancel)
            {
                host = "8.8.8.8";
                Set(cInet, DiagState.Running, host + "...");
                ok = PingMany(host, out avg);
            }
            cInet.Ms = avg;
            if (ok == 0) Set(cInet, DiagState.Error, L.T("1.1.1.1 e 8.8.8.8 non rispondono", "1.1.1.1 and 8.8.8.8 do not answer"));
            else if (ok < PingCount) Set(cInet, DiagState.Warn, L.F("{0}: {1} risposte perse su {2}", "{0}: {1} of {2} replies lost", host, PingCount - ok, PingCount));
            else if (avg >= 150) Set(cInet, DiagState.Warn, L.F("{0}: risposta lenta ({1})", "{0}: slow reply ({1})", host, Ms(avg)));
            else Set(cInet, DiagState.Ok, host + " · " + Ms(avg));
        }

        // Risoluzione con limite di tempo (Dns.GetHostAddresses da solo può restare bloccato a lungo)
        static bool Resolve(string name, int timeoutMs, out SocketError error, out int ms)
        {
            error = SocketError.Success;
            var sw = Stopwatch.StartNew();
            var ar = Dns.BeginGetHostAddresses(name, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { ms = (int)sw.ElapsedMilliseconds; error = SocketError.TimedOut; return false; }
            try { Dns.EndGetHostAddresses(ar); }
            catch (SocketException ex) { error = ex.SocketErrorCode; }
            catch { error = SocketError.SocketError; }
            ms = (int)sw.ElapsedMilliseconds;
            return error == SocketError.Success;
        }

        void CheckDns(DiagSnapshot s)
        {
            Set(cDns, DiagState.Running, s.Dns.Length > 0 ? s.Dns + "..." : "...");
            // Nome casuale: non può essere nella cache di Windows, quindi il server DNS deve rispondere davvero.
            // "Nome inesistente" va bene: è una risposta del server.
            string rnd = "corbolip-" + Guid.NewGuid().ToString("N").Substring(0, 10) + "." + DnsDomain;
            SocketError err; int ms;
            Resolve(rnd, 8000, out err, out ms);
            bool answered = (err == SocketError.Success || err == SocketError.HostNotFound) && ms < 5000;
            SocketError err2; int ms2;
            bool real = Resolve("www." + DnsDomain, 8000, out err2, out ms2);
            string srv = s.Dns.Length > 0 ? " (" + s.Dns + ")" : "";
            if (!answered && !real) { Set(cDns, DiagState.Error, L.T("il server DNS non risponde", "the DNS server does not answer") + srv); return; }
            if (!answered) { Set(cDns, DiagState.Error, L.T("il server DNS non risponde (nomi solo dalla cache)", "the DNS server does not answer (names only from cache)") + srv); return; }
            cDns.Ms = ms;
            if (!real) Set(cDns, DiagState.Warn, L.T("risponde, ma non risolve i nomi dei siti", "answers, but does not resolve site names") + srv);
            else if (ms >= 1000) Set(cDns, DiagState.Warn, L.F("risposta lenta ({0})", "slow reply ({0})", Ms(ms)) + srv);
            else Set(cDns, DiagState.Ok, L.F("risponde in {0}", "answers in {0}", Ms(ms)) + srv);
        }

        static HttpWebRequest NewRequest(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "CorbolIp/" + AppVersion.Text;
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            req.AllowAutoRedirect = false;
            req.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            return req;
        }

        static string ReadAll(HttpWebResponse resp)
        {
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                var buf = new char[4096];
                int n = sr.ReadBlock(buf, 0, buf.Length);
                return new string(buf, 0, n);
            }
        }

        static bool ViaProxy(string url)
        {
            try
            {
                var u = new Uri(url);
                var px = WebRequest.DefaultWebProxy;
                return px != null && !px.IsBypassed(u) && px.GetProxy(u) != u;
            }
            catch { return false; }
        }

        // Pagina di accesso (hotel, ospiti...): Windows usa lo stesso indirizzo per accorgersene
        void CheckPortal()
        {
            Set(cPortal, DiagState.Running, "...");
            try
            {
                using (var resp = (HttpWebResponse)NewRequest(NcsiUrl).GetResponse())
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code < 400)
                    {
                        string loc = resp.Headers["Location"] ?? "";
                        Uri lu;
                        if (Uri.TryCreate(loc, UriKind.Absolute, out lu)) loc = lu.Host;
                        cPortal.Captive = true;
                        Set(cPortal, DiagState.Error, L.T("serve una pagina di accesso", "a sign-in page is required") + (loc.Length > 0 ? " (" + loc + ")" : ""));
                        return;
                    }
                    string body = ReadAll(resp).Trim();
                    if (body.StartsWith(NcsiText)) Set(cPortal, DiagState.Ok, L.T("libero", "open") + (ViaProxy(NcsiUrl) ? " · " + L.T("tramite proxy", "through a proxy") : ""));
                    else { cPortal.Captive = true; Set(cPortal, DiagState.Error, L.T("risposta diversa dal previsto: pagina di accesso o proxy", "unexpected reply: sign-in page or proxy")); }
                }
            }
            catch (Exception ex) { Set(cPortal, DiagState.Error, ErrorText(ex, cPortal)); }
        }

        void CheckWeb()
        {
            Set(cWeb, DiagState.Running, "...");
            try
            {
                var sw = Stopwatch.StartNew();
                using (var resp = (HttpWebResponse)NewRequest(TraceUrl).GetResponse())
                {
                    string body = ReadAll(resp);
                    sw.Stop();
                    if ((int)resp.StatusCode != 200) { Set(cWeb, DiagState.Error, L.F("il server ha risposto {0}", "the server replied {0}", (int)resp.StatusCode)); return; }
                    var ip = Regex.Match(body, @"(?m)^ip=(\S+)");
                    var loc = Regex.Match(body, @"(?m)^loc=(\S+)");
                    string d = L.T("funziona", "works") + " · " + Ms((int)sw.ElapsedMilliseconds);
                    if (ip.Success) pubIp = ip.Groups[1].Value;
                    if (ip.Success) d += " · " + L.T("IP pubblico", "public IP") + " " + ip.Groups[1].Value + (loc.Success ? " (" + loc.Groups[1].Value + ")" : "");
                    Set(cWeb, DiagState.Ok, d);
                }
            }
            catch (Exception ex) { Set(cWeb, DiagState.Error, ErrorText(ex, cWeb)); }
        }

        static string ErrorText(Exception ex, DiagCheck c)
        {
            var we = ex as WebException;
            if (we == null) return ex.Message;
            switch (we.Status)
            {
                case WebExceptionStatus.NameResolutionFailure: return L.T("nome del sito non trovato (DNS)", "site name not found (DNS)");
                case WebExceptionStatus.Timeout: return L.T("nessuna risposta", "no response");
                case WebExceptionStatus.ConnectFailure: return L.T("connessione rifiutata o bloccata", "connection refused or blocked");
                case WebExceptionStatus.ProxyNameResolutionFailure: return L.T("proxy non trovato", "proxy not found");
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    c.TrustFailure = true;
                    return L.T("certificato non accettato", "certificate not accepted");
            }
            var resp = we.Response as HttpWebResponse;
            if (resp != null)
            {
                if ((int)resp.StatusCode == 407) return L.T("il proxy chiede le credenziali", "the proxy requires credentials");
                return L.F("il server ha risposto {0}", "the server replied {0}", (int)resp.StatusCode + " " + resp.StatusDescription);
            }
            return we.Message;
        }

        void CheckVpn(NetLine v)
        {
            if (v == null) Set(cVpn, DiagState.Info, L.T("nessuna VPN attiva", "no active VPN"));
            else Set(cVpn, DiagState.Info, L.F("attiva: {0}", "active: {0}", v.Name + (v.IPv4Plain != "-" ? " (" + v.IPv4Plain + ")" : "")));
        }

        // Ping bloccati ma navigazione funzionante: non è un problema (molti router e reti aziendali bloccano il ping)
        void Adjust()
        {
            bool webOk = cWeb.State == DiagState.Ok;
            bool inetOk = cInet.State == DiagState.Ok || cInet.State == DiagState.Warn;
            if (cGw.State == DiagState.Error && cNet.State == DiagState.Ok && (webOk || inetOk))
                Set(cGw, DiagState.Info, L.T("non risponde al ping (alcuni router lo bloccano), ma Internet funziona", "does not answer ping (some routers block it), but the Internet works"));
            if (cInet.State == DiagState.Error && webOk)
                Set(cInet, DiagState.Info, L.T("ping bloccato (normale in molte reti aziendali), ma la navigazione funziona", "ping blocked (common on company networks), but browsing works"));
        }

        // ---------- conclusione a regole ----------
        static bool Bad(DiagCheck c) { return c.State == DiagState.Error; }

        void Conclude()
        {
            var p = snap != null ? snap.Primary : null;
            bool weakWifi = cWifi.State == DiagState.Error || cWifi.State == DiagState.Warn;
            string dnsSrv = snap != null && snap.Dns.Length > 0 ? snap.Dns : "-";

            if (p == null && Bad(cNet))
                C(DiagState.Error, L.T("Nessuna rete", "No network"),
                  L.T("Il PC non è collegato a nessuna rete. Controlla il cavo, oppure che il Wi-Fi sia acceso e collegato.",
                      "This PC is not connected to any network. Check the cable, or that Wi-Fi is on and connected."));
            else if (Bad(cIp) && IsApipa(p))
                C(DiagState.Error, L.T("Indirizzo IP non ricevuto", "No IP address received"),
                  L.T("Il PC è collegato ma il router non gli ha dato un indirizzo (DHCP). Prova a scollegarti e ricollegarti alla rete; se non basta, riavvia il router o avvisa l'assistenza.",
                      "The PC is connected but the router did not give it an address (DHCP). Disconnect and reconnect to the network; if that does not help, restart the router or contact IT support."));
            else if (Bad(cNet) || Bad(cIp))
                C(DiagState.Error, L.T("Rete senza uscita", "Network with no way out"),
                  L.T("La scheda è collegata, ma senza gateway non può uscire dalla rete locale. Prova a scollegarti e ricollegarti; se il problema resta, avvisa l'assistenza.",
                      "The adapter is connected, but without a gateway it cannot leave the local network. Disconnect and reconnect; if the problem persists, contact IT support."));
            else if (cWeb.State == DiagState.Ok && !Bad(cDns) && !Bad(cPortal))
                Working();
            else if (cPortal.Captive)
                C(DiagState.Warn, L.T("Serve un accesso dal browser", "Browser sign-in required"),
                  L.T("Questa rete chiede di accedere da una pagina web (tipico di hotel, treni e reti ospiti). Apri il browser e completa l'accesso, poi ripeti la diagnosi.",
                      "This network asks you to sign in on a web page (typical of hotels, trains and guest networks). Open the browser, complete the sign-in, then run the diagnostics again."));
            else if (Bad(cGw))
                C(DiagState.Error, weakWifi ? L.T("Segnale Wi-Fi troppo debole", "Wi-Fi signal too weak") : L.T("Il router non risponde", "The router does not answer"),
                  weakWifi
                    ? L.T("Il PC non riesce a parlare con il router e il segnale è debole. Avvicinati al router, oppure usa il cavo.",
                          "The PC cannot reach the router and the signal is weak. Move closer to the router, or use a cable.")
                    : L.T("Il PC non riesce a parlare con il router. Prova a scollegarti e ricollegarti (Wi-Fi o cavo). Se capita anche ai colleghi, il router potrebbe essere spento o bloccato.",
                          "The PC cannot reach the router. Disconnect and reconnect (Wi-Fi or cable). If colleagues have the same problem, the router may be off or stuck."));
            else if (Bad(cInet) && cWeb.State != DiagState.Ok)
                C(DiagState.Error, L.T("Nessuna uscita su Internet", "No Internet connection"),
                  L.T("La rete locale funziona, ma non si esce su Internet: il problema è nel router o nella linea del fornitore. Se capita anche ai colleghi, avvisa l'assistenza.",
                      "The local network works, but there is no way out to the Internet: the problem is in the router or in the provider's line. If colleagues have the same problem, contact IT support."));
            else if (Bad(cDns))
                C(DiagState.Error, L.T("Problema DNS", "DNS problem"),
                  L.F("Internet è raggiungibile, ma i nomi dei siti non vengono tradotti in indirizzi (DNS: {0}). Prova a scollegarti e ricollegarti; se resta, avvisa l'assistenza.",
                      "The Internet is reachable, but site names are not translated into addresses (DNS: {0}). Disconnect and reconnect; if it persists, contact IT support.", dnsSrv));
            else if (Bad(cWeb) && cWeb.TrustFailure)
                C(DiagState.Error, L.T("Connessioni sicure rifiutate", "Secure connections rejected"),
                  L.T("La rete funziona, ma i certificati dei siti non vengono accettati. Controlla che data e ora del PC siano giuste; in azienda può dipendere dal proxy o dall'antivirus: avvisa l'assistenza.",
                      "The network works, but site certificates are not accepted. Check that the PC date and time are correct; at work it may depend on the proxy or antivirus: contact IT support."));
            else if (Bad(cWeb) || Bad(cPortal))
                C(DiagState.Error, L.T("Navigazione bloccata", "Browsing blocked"),
                  L.F("La rete funziona, ma la navigazione web non passa ({0}). Di solito dipende da proxy, firewall o antivirus: avvisa l'assistenza.",
                      "The network works, but web browsing does not get through ({0}). Usually it depends on a proxy, firewall or antivirus: contact IT support.",
                      Bad(cWeb) ? cWeb.Detail : cPortal.Detail));
            else
                C(DiagState.Warn, L.T("Risultato non chiaro", "Unclear result"),
                  L.T("I controlli non indicano una causa precisa. Copia il report e mandalo all'assistenza.",
                      "The checks do not point to a clear cause. Copy the report and send it to IT support."));
        }

        void Working()
        {
            var notes = new List<string>();
            if (cWifi.State == DiagState.Error || cWifi.State == DiagState.Warn)
                notes.Add(L.T("il segnale Wi-Fi è debole: avvicinati al router o usa il cavo", "the Wi-Fi signal is weak: move closer to the router or use a cable"));
            if (cGw.State == DiagState.Warn)
                notes.Add(L.T("il collegamento con il router perde pacchetti o è lento (spesso è il Wi-Fi)", "the link to the router loses packets or is slow (often Wi-Fi)"));
            if (cInet.State == DiagState.Warn)
                notes.Add(L.T("la linea verso Internet perde pacchetti o è lenta", "the Internet line loses packets or is slow"));
            if (cDns.State == DiagState.Warn)
                notes.Add(L.T("il DNS risponde lentamente o in modo incompleto", "DNS replies slowly or incompletely"));
            if (notes.Count == 0)
                C(DiagState.Ok, L.T("Nessun problema trovato", "No problems found"),
                  L.T("La connessione funziona. Se un solo sito o programma non va, il problema è probabilmente lì (o nella VPN, se serve).",
                      "The connection works. If a single site or program does not work, the problem is probably there (or in the VPN, if needed)."));
            else
                C(DiagState.Warn, L.T("Internet funziona, con qualche problema", "The Internet works, with some issues"),
                  L.T("Attenzione: ", "Note: ") + string.Join("; ", notes) + ".");
        }

        void C(DiagState st, string title, string text)
        {
            conclState = st; conclTitle = title; conclText = text;
        }

        // ---------- report ----------
        static string StateTag(DiagState s)
        {
            switch (s)
            {
                case DiagState.Ok: return "[OK]  ";
                case DiagState.Warn: return "[!]   ";
                case DiagState.Error: return "[X]   ";
                default: return "[-]   ";
            }
        }

        string ReportText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.T("Diagnosi rete", "Network diagnostics") + " - CorbolIp " + AppVersion.Text + " - " + runTime.ToString("g"));
            sb.AppendLine(L.T("Esito", "Result") + ": " + conclTitle);
            sb.AppendLine(conclText);
            sb.AppendLine();
            int pad = checks.Max(c => c.Title.Length) + 2;
            foreach (var c in checks) sb.AppendLine(StateTag(c.State) + (c.Title + ":").PadRight(pad) + c.Detail);
            if (snap != null && snap.NetText != null)
            {
                sb.AppendLine();
                try { sb.Append(snap.NetText()); } catch { }
            }
            string ev = NetEvents.Text(24, 30, false);
            if (ev.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine(L.T("Eventi di rete delle ultime 24 ore:", "Network events in the last 24 hours:"));
                sb.Append(ev);
            }
            return sb.ToString();
        }

        // Testo di "Copia report": la segnalazione dell'assistente va in cima, la spiegazione in fondo
        string CopyText()
        {
            var sb = new StringBuilder();
            if (ticketText != null) sb.Append(Crlf(ticketText.Trim())).Append("\r\n\r\n----------\r\n");
            sb.Append(ReportText());
            if (chatLog.Length > 0) sb.Append("\r\n").Append(Crlf(chatLog.ToString().Trim())).Append("\r\n");
            return sb.ToString();
        }

        static string Crlf(string s)
        {
            return Regex.Replace(s ?? "", "\r?\n", "\r\n");
        }

        // ---------- assistente AI ----------
        void ShowAi(string text, bool hint = false)
        {
            if (txtAi == null) return;
            var color = hint ? Color.DimGray : SystemColors.WindowText;
            bool sameColor = txtAi.ForeColor == color;
            if (!sameColor) txtAi.ForeColor = color;
            string s = Crlf(text), cur = txtAi.Text;
            if (s == cur) return;
            // durante lo streaming il testo cresce: si aggiunge solo la parte nuova, senza ridisegnare tutto (niente sfarfallio)
            if (sameColor && cur.Length > 0 && s.Length > cur.Length && s.StartsWith(cur, StringComparison.Ordinal))
            {
                txtAi.AppendText(s.Substring(cur.Length));
                return;
            }
            txtAi.Text = s;
            txtAi.SelectionStart = txtAi.TextLength;
            txtAi.ScrollToCaret();
        }

        static string LangName()
        {
            try { return CultureInfo.GetCultureInfo(L.Code).EnglishName; } catch { return "English"; }
        }

        const string Common =
            "Words in square brackets in the data (for example [WIFI] or [PUBLIC_IP]) replace private data: copy them exactly when you need " +
            "them and never use one that does not appear in the data. Do not mention the computer, user or network name unless it helps the user. " +
            "Rely only on the data provided and never invent values; if the data is not " +
            "enough to be sure, say so and suggest what to check. Write plain text for a small window: no Markdown headings, bold or tables; " +
            "use short paragraphs and lists with \"- \". Never say that you checked or tested something unless its result is in the data.";

        const string ToolsHelp =
            "You can run extra network checks with the tools (at most " + "5" + " in total) when they help to find or confirm the cause, " +
            "for example a ping or traceroute when the router or the Internet does not answer, a DNS lookup or http_check for a site the " +
            "user mentions, tcp_port for a service, recent_events to see whether the connection dropped earlier. If the user mentions a " +
            "specific site, service or address, check it with the tools before answering. Otherwise do not run checks when the results " +
            "are already clear. After the checks, write only the final answer for the user and mention what the extra checks showed.";

        static string SystemPrompt(bool tools)
        {
            return "You are the network assistant built into CorbolIp, a small network widget for Windows. The first message contains the " +
                   "results of an automatic network diagnosis run on the user's PC. Help a non-technical office worker understand what the " +
                   "results mean, fix network problems starting from the most likely fix, and answer their questions about the network. " +
                   "Keep answers short (under about 150 words) unless the user asks for more. " + Common + (tools ? " " + ToolsHelp : "") +
                   " Answer in " + LangName() + ".";
        }

        const string ExplainRequest = "Explain what these results mean for me and what I can do about it, step by step, starting from the most " +
                                      "likely fix. If everything works, say so briefly. Keep it under about 150 words.";

        static string TicketRequest(string note)
        {
            return "Write a support request that I will send to the IT help desk. Start with a one-line subject, then describe the problem " +
                   "from my point of view" + (note.Length > 0 ? " (my description: " + note + ")" : "") + ", taking into account what we " +
                   "discussed, what the checks found and the most likely cause. Keep it under about 150 words and do not list all the " +
                   "technical details: the full diagnostic report is attached automatically below your text. Write only the request, ready to send.";
        }

        static string StateWord(DiagState s)
        {
            switch (s)
            {
                case DiagState.Ok: return "OK";
                case DiagState.Warn: return "WARNING";
                case DiagState.Error: return "ERROR";
                default: return "INFO";
            }
        }

        // Dati della diagnosi per l'assistente (in chiaro: l'anonimizzazione si applica dopo)
        string AiInput()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Automatic network diagnostics on a Windows PC (CorbolIp " + AppVersion.Text + "), " + runTime.ToString("yyyy-MM-dd HH:mm") + ".");
            sb.AppendLine("Rule-based conclusion shown to the user: " + conclTitle + " - " + conclText);
            sb.AppendLine();
            sb.AppendLine("Checks:");
            foreach (var c in checks) sb.AppendLine("- " + c.Title + " [" + StateWord(c.State) + "]: " + c.Detail);
            var p = snap != null ? snap.Primary : null;
            if (p != null)
            {
                sb.AppendLine();
                sb.AppendLine("Main network adapter:");
                sb.AppendLine("- " + p.Name + " (" + p.Description + "), " + (p.IsWifi ? "Wi-Fi" : "wired or other"));
                sb.AppendLine("- IPv4 " + p.IPv4 + ", DHCP " + (p.Dhcp ? "yes" + (p.DhcpServer.Length > 0 ? " (server " + p.DhcpServer + ")" : "") : "no (static)"));
                sb.AppendLine("- gateway " + p.Gateway + ", DNS " + p.Dns);
                if (p.IPv6All.Length > 0) sb.AppendLine("- IPv6 " + p.IPv6All);
                if (p.IsWifi)
                {
                    var w = p.Wi;
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(p.WifiSsid)) parts.Add("network " + p.WifiSsid);
                    if (p.WifiSignal >= 0) parts.Add("signal " + p.WifiSignal + "%");
                    if (w != null)
                    {
                        if (w.Rssi < 0) parts.Add(w.Rssi + " dBm");
                        if (w.Band().Length > 0) parts.Add(w.Band());
                        if (w.Chan() > 0) parts.Add("channel " + w.Chan());
                        if (w.Gen().Length > 0) parts.Add(w.Gen());
                        if (w.RxMbps > 0) parts.Add("link " + w.RxMbps + "/" + Math.Max(0, w.TxMbps) + " Mbps");
                    }
                    if (parts.Count > 0) sb.AppendLine("- Wi-Fi: " + string.Join(", ", parts));
                }
            }
            if (snap != null && snap.Vpn != null)
                sb.AppendLine("- VPN adapter: " + snap.Vpn.Name + " (" + snap.Vpn.Description + "), IP " + snap.Vpn.IPv4Plain + ", DNS " + snap.Vpn.Dns);
            string ev = NetEvents.Text(24, 25, true);
            sb.AppendLine();
            if (ev.Length > 0) { sb.AppendLine("Network events recorded on this PC in the last 24 hours (local time):"); sb.Append(ev); }
            else sb.AppendLine(NetEvents.Enabled ? "No network events recorded in the last 24 hours." : "The network event log is turned off.");
            return sb.ToString();
        }

        Anonymizer BuildAnon()
        {
            var a = new Anonymizer();
            if (snap == null) return a;
            a.Add(snap.Host, "PC");
            a.Add(snap.Domain, "DOMAIN");
            try { a.Add(Environment.UserName, "USER"); } catch { }
            a.Add(pubIp, "PUBLIC_IP");
            foreach (var l in snap.All)
            {
                if (!string.IsNullOrEmpty(l.WifiSsid)) a.Add(l.WifiSsid, "WIFI");
                a.Add(l.Mac, "MAC");
            }
            // gli eventi scrivono i nomi delle reti Wi-Fi tra virgolette
            foreach (var e in NetEvents.Recent(168))
                if (e.Kind == "WIFI" || e.Kind == "NET" || e.Kind == "START")
                    foreach (Match m in Regex.Matches(e.Text, "\"([^\"]+)\""))
                        a.Add(m.Groups[1].Value, "WIFI");
            return a;
        }

        // L'assistente locale (modello piccolo) non usa gli strumenti: nelle prove diceva di aver controllato senza farlo.
        // I siti citati dall'utente li controlla comunque CorbolIp (vedi RunAi).
        // Gli strumenti passano solo dalle API (Claude, compatibili OpenAI); Claude Code parte senza strumenti.
        static bool UsesTools(IAiProvider prov) { return prov is AiHttpProvider; }

        // Primo messaggio della conversazione: i dati della diagnosi e la prima richiesta (in chiaro)
        string FirstMessage(string request)
        {
            return "Network diagnosis of my PC:\n\n" + AiInput() + "\n\n" + request;
        }

        void ResetConversation(bool showHint)
        {
            convo.Clear();
            transcript.Length = 0;
            chatLog.Length = 0;
            ticketText = null;
            lastSent = null;
            anon = null;
            if (showHint && ai)
                ShowAi(L.T("Nuova conversazione: premi Spiega o scrivi una domanda.", "New conversation: press Explain or type a question."), true);
        }

        void Ask()
        {
            if (aiCts != null || btnAsk == null || !btnAsk.Enabled) return;
            string q = txtQuestion.Text.Trim();
            if (q.Length == 0) { txtQuestion.Focus(); return; }
            txtQuestion.Text = "";
            RunAi(AiKind.Question, q, btnAsk);
        }

        // text: la domanda (Chiedi); per la segnalazione la descrizione arriva dalla casella o da AskNote
        void RunAi(AiKind kind, string text, Button btn)
        {
            if (aiCts != null) { aiCts.Cancel(); return; }
            if (worker != null) return;
            var prov = AiSettings.CurrentProvider();
            if (prov == null)
            {
                ShowAi(L.T("L'assistente non è configurato: apri Assistente AI... dal menu di CorbolIp.", "The assistant is not set up: open AI assistant... from the CorbolIp menu."));
                return;
            }
            string note = (text ?? "").Trim();   // testo dell'utente: domanda o descrizione del problema
            if (kind == AiKind.Ticket)
            {
                // la descrizione si può scrivere nella casella delle domande; a conversazione vuota, se manca, la si chiede
                note = txtQuestion.Text.Trim();
                if (note.Length > 0) txtQuestion.Text = "";
                else if (convo.Count == 0)
                {
                    note = AskNote();
                    if (note == null) return;
                    note = note.Trim();
                }
            }
            string request = kind == AiKind.Explain ? ExplainRequest : kind == AiKind.Ticket ? TicketRequest(note) : "My question: " + note;
            if (convo.Count == 0)
            {
                anon = AiSettings.Anonymize ? BuildAnon() : null;
                request = FirstMessage(request);
            }
            var a = anon;
            if (a != null) request = a.Apply(request);
            var msg = new AiMessage("user", request);
            convo.Add(msg);

            bool tools = UsesTools(prov);
            var req = new AiRequest { System = SystemPrompt(tools) };
            req.Messages.AddRange(convo);
            if (tools) req.Tools = DiagTools.Definitions();
            lastSent = SentText(prov, req, a != null);

            string header = kind == AiKind.Explain ? "» " + L.T("Spiegazione dei risultati", "Explanation of the results")
                          : kind == AiKind.Ticket ? "» " + L.T("Segnalazione per l'assistenza", "Support request") + (note.Length > 0 ? "\n" + L.T("Tu", "You") + ": " + note : "")
                          : L.T("Tu", "You") + ": " + note;
            int answerStart = transcript.Length;   // a risposta finita si legge da qui
            if (transcript.Length > 0) { transcript.Append("\n\n"); answerStart = transcript.Length; }
            transcript.Append(header).Append("\n\n");
            string head = transcript.ToString();

            var cts = aiCts = new CancellationTokenSource();
            string idle = btn.Text;
            btn.Text = L.T("Interrompi", "Stop");
            foreach (var b in new[] { btnExplain, btnTicket, btnAsk }) if (b != btn) b.Enabled = false;
            btnStart.Enabled = btnNew.Enabled = false;
            ShowAi(head + (prov == LocalAi.Instance && !LocalAi.Instance.Running
                ? L.T("Avvio dell'assistente locale: la prima volta può richiedere fino a un minuto...", "Starting the local assistant: the first time it can take up to a minute...")
                : L.F("{0} sta scrivendo...", "{0} is writing...", prov.Name)));

            // Nel riquadro: il testo dell'assistente e, man mano, i controlli che esegue ("» ping 8.8.8.8 → ...")
            var acc = new StringBuilder();
            var checksDone = new List<string>();
            var sentLog = new StringBuilder();
            int pending = 0;   // al massimo un aggiornamento in coda: i pezzi arrivati nel frattempo si mostrano insieme
            Action refresh = delegate
            {
                if (Interlocked.Exchange(ref pending, 1) == 1) return;
                Ui(delegate
                {
                    Interlocked.Exchange(ref pending, 0);
                    string shown;
                    lock (acc) shown = acc.ToString();
                    if (a != null) shown = a.Restore(shown);
                    if (aiCts == cts) ShowAi(head + shown);
                });
            };
            int calls = 0;
            req.RunTool = delegate (AiToolCall c)
            {
                // gli argomenti possono contenere segnaposti ([PC]...): si eseguono con i valori veri
                if (a != null && c.Args != null)
                    foreach (var k in c.Args.Keys.ToList())
                        if (c.Args[k] is string) c.Args[k] = a.Restore((string)c.Args[k]);
                string label = DiagTools.Describe(c);
                if (++calls > DiagTools.MaxCalls) return "Error: the limit of " + DiagTools.MaxCalls + " checks is reached. Answer now with what you have.";
                lock (acc) acc.Append((acc.Length > 0 && acc[acc.Length - 1] != '\n' ? "\n" : "") + "» " + label + " ...\n");
                refresh();
                string outp = DiagTools.Execute(c, cts.Token);
                string first = outp.Split('\n')[0].Trim();
                lock (acc)
                {
                    acc.Length -= 5; // toglie " ...\n"
                    acc.Append(" → " + (first.Length > 120 ? first.Substring(0, 120) + "…" : first) + "\n\n");
                }
                checksDone.Add(label + ": " + first);
                refresh();
                string sentOut = a != null ? a.Apply(outp) : outp;
                lock (sentLog) sentLog.AppendLine().AppendLine("--- " + L.F("Controllo: {0}", "Check: {0}", label) + " ---").Append(sentOut);
                return sentOut;
            };
            var t = new Thread(delegate ()
            {
                var targets = DiagTools.TargetsIn(note);
                if (targets.Count > 0)
                {
                    var pre = new StringBuilder();
                    foreach (var c in targets.SelectMany(DiagTools.ChecksFor))
                    {
                        if (cts.IsCancellationRequested) break;
                        string label = DiagTools.Describe(c);
                        lock (acc) acc.Append("» " + label + " ...\n");
                        refresh();
                        string outp;
                        try { outp = DiagTools.Execute(c, cts.Token); } catch (Exception ex) { outp = "Error: " + ex.Message; }
                        string first = outp.Split('\n')[0].Trim();
                        lock (acc) { acc.Length -= 5; acc.Append(" → " + (first.Length > 120 ? first.Substring(0, 120) + "…" : first) + "\n"); }
                        refresh();
                        checksDone.Add(label + ": " + first);
                        pre.AppendLine("- " + label + ": " + outp.Trim());
                    }
                    lock (acc) acc.Append("\n");
                    string extra = "\n\nChecks that CorbolIp ran on what the user mentioned:\n" + pre;
                    if (a != null) extra = a.Apply(extra);
                    msg.Text += extra;   // resta nella conversazione anche per le domande successive
                    lock (sentLog) sentLog.Append(extra);
                }
                int keep;
                lock (acc) keep = acc.Length; // fin qui: i controlli di CorbolIp, da tenere se l'assistente ricomincia
                req.OnRestart = delegate
                {
                    lock (acc) { acc.Length = keep; acc.Append(L.T("(l'assistente locale si è chiuso: riparte e ricomincia la risposta...)", "(the local assistant closed: restarting the reply...)") + "\n"); }
                    calls = 0;
                    refresh();
                };
                var res = prov.Run(req, delegate (string chunk)
                {
                    lock (acc) acc.Append(chunk);
                    refresh();
                }, cts.Token);
                string raw;
                lock (acc) raw = acc.ToString();
                Ui(delegate
                {
                    lock (sentLog) if (sentLog.Length > 0) lastSent += "\n" + sentLog;
                    aiCts = null;
                    btn.Text = idle;
                    btnExplain.Enabled = btnTicket.Enabled = btnAsk.Enabled = btnStart.Enabled = btnNew.Enabled = true;
                    // la risposta (senza le righe dei controlli) entra nella conversazione per le domande successive
                    string answer = string.Join("\n", raw.Split('\n').Where(l => !l.StartsWith("» "))).Trim();
                    string final = (a != null ? a.Restore(raw) : raw).Trim();
                    string shown = final;
                    if (res.Cancelled) shown += (shown.Length > 0 ? "\n\n" : "") + L.T("(interrotto)", "(stopped)");
                    else if (!res.Ok) shown = res.Error + (shown.Length > 0 ? "\n\n" + shown : "");
                    else if (res.Truncated) shown += "\n\n" + L.T("(risposta troncata)", "(reply cut short)");
                    bool ok = res.Ok && answer.Length > 0;
                    if (ok) convo.Add(new AiMessage("assistant", answer));
                    else convo.Remove(msg);   // domanda senza risposta: si può ripetere
                    if (convo.Count == 0) anon = null;
                    transcript.Append(shown);
                    ShowAi(transcript.ToString());
                    txtAi.SelectionStart = Crlf(transcript.ToString(0, answerStart)).Length; // si legge dall'inizio della risposta
                    txtAi.ScrollToCaret();
                    txtQuestion.Focus();
                    if (!ok) return;

                    // per "Copia report": risposta in chiaro senza le righe dei controlli, che vanno in un elenco a parte
                    string clean = a != null ? a.Restore(answer) : answer;
                    if (checksDone.Count > 0)
                        clean += "\n\n" + L.T("Controlli aggiuntivi dell'assistente:", "Extra checks by the assistant:") + "\n- " + string.Join("\n- ", checksDone);
                    if (kind == AiKind.Ticket)
                    {
                        ticketText = clean;
                        lblTitle.Text = L.T("Segnalazione pronta: Copia report la copia insieme ai dati tecnici.", "Support request ready: Copy report copies it together with the technical details.");
                        lblTitle.ForeColor = Color.DimGray;
                    }
                    else
                    {
                        if (kind == AiKind.Question) chatLog.Append(L.T("Domanda", "Question") + ": " + note + "\n");
                        chatLog.Append(kind == AiKind.Explain ? L.T("Spiegazione dell'assistente", "Assistant's explanation") : L.T("Risposta dell'assistente", "Assistant's answer"))
                               .Append(":\n").Append(clean).Append("\n\n");
                    }
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        static string SentText(IAiProvider prov, AiRequest req, bool anon)
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.F("Servizio: {0}", "Service: {0}", prov.Name) + (prov.SendsTextOutside ? "" : " " + L.T("(sul PC, i dati non escono)", "(on this PC, data does not leave it)")));
            sb.AppendLine(anon ? L.T("Anonimizzazione: attiva", "Anonymization: on") : L.T("Anonimizzazione: spenta", "Anonymization: off"));
            sb.AppendLine();
            sb.AppendLine("--- " + L.T("Istruzioni", "Instructions") + " ---");
            sb.AppendLine(req.System);
            foreach (var m in req.Messages)
            {
                sb.AppendLine();
                sb.AppendLine("--- " + (m.Role == "user" ? L.T("Messaggio", "Message") : L.T("Risposta precedente", "Previous reply")) + " ---");
                sb.AppendLine(m.Text);
            }
            return sb.ToString();
        }

        // Mostra il testo esatto inviato (o quello che invierebbe Spiega)
        void ShowSent()
        {
            string text = lastSent;
            if (text == null)
            {
                var prov = AiSettings.CurrentProvider();
                if (prov == null) { ShowAi(L.T("L'assistente non è configurato: apri Assistente AI... dal menu di CorbolIp.", "The assistant is not set up: open AI assistant... from the CorbolIp menu.")); return; }
                var a = AiSettings.Anonymize ? BuildAnon() : null;
                string first = FirstMessage(ExplainRequest);
                if (a != null) first = a.Apply(first);
                text = SentText(prov, AiRequest.Simple(SystemPrompt(UsesTools(prov)), first), a != null);
            }
            using (var f = new Form
            {
                Text = L.T("Cosa viene inviato - CorbolIp", "What is sent - CorbolIp"), StartPosition = FormStartPosition.CenterParent,
                Font = Font, Size = new Size((int)(760 * UiFont.Scale * ScreenDpi() / 96f), (int)(600 * UiFont.Scale * ScreenDpi() / 96f)), MinimizeBox = false, ShowInTaskbar = false, TopMost = true, Icon = Icon
            })
            {
                var tb = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill, Text = Crlf(text), BackColor = Color.White, Font = new Font("Segoe UI", UiFont.Pt + 1f) };
                f.Controls.Add(tb);
                tb.SelectionStart = 0;
                f.ShowDialog(this);
            }
        }

        // Descrizione facoltativa del problema per la segnalazione; null = annullato
        string AskNote()
        {
            using (var f = new Form
            {
                Text = L.T("Prepara segnalazione - CorbolIp", "Write support request - CorbolIp"), StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, TopMost = true,
                AutoScaleDimensions = new SizeF(96f, 96f), AutoScaleMode = AutoScaleMode.Dpi, Font = Font, BackColor = Color.White
            })
            {
                var lbl = new Label { Text = L.T("Descrivi il problema con parole tue (facoltativo), ad esempio cosa non funziona e da quando:", "Describe the problem in your own words (optional), for example what does not work and since when:"), AutoSize = false, Location = new Point(16, 12), Size = new Size(440, 40) };
                var tb = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(16, 56), Size = new Size(440, 100) };
                var ok = new Button { Text = L.T("Prepara", "Write"), DialogResult = DialogResult.OK, Location = new Point(260, 168), Size = new Size(96, 30) };
                var cancel = new Button { Text = L.T("Annulla", "Cancel"), DialogResult = DialogResult.Cancel, Location = new Point(360, 168), Size = new Size(96, 30) };
                f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                f.CancelButton = cancel; // Invio nella descrizione va a capo
                f.ClientSize = new Size(472, 212);
                f.Scale(new SizeF(UiFont.Scale, UiFont.Scale));
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        // ---------- dati fittizi per gli screenshot (/demo) ----------
        void RunDemo()
        {
            var steps = new List<KeyValuePair<DiagCheck, KeyValuePair<DiagState, string>>>();
            Action<DiagCheck, DiagState, string> add = (c, st, d) => steps.Add(new KeyValuePair<DiagCheck, KeyValuePair<DiagState, string>>(c, new KeyValuePair<DiagState, string>(st, d)));
            add(cNet, DiagState.Ok, "Wi-Fi \"" + L.T("Ufficio-WiFi", "Office-WiFi") + "\"");
            add(cIp, DiagState.Ok, "192.168.1.50 · DHCP");
            add(cWifi, DiagState.Ok, L.F("segnale {0}%", "signal {0}%", 82) + " · 5 GHz · " + L.T("canale", "channel") + " 36 · -54 dBm");
            add(cGw, DiagState.Ok, "192.168.1.1 · 3 ms");
            add(cInet, DiagState.Ok, "1.1.1.1 · 14 ms");
            add(cDns, DiagState.Ok, L.F("risponde in {0}", "answers in {0}", "21 ms") + " (192.168.1.1, 1.1.1.1)");
            add(cPortal, DiagState.Ok, L.T("libero", "open"));
            add(cWeb, DiagState.Ok, L.T("funziona", "works") + " · 48 ms · " + L.T("IP pubblico", "public IP") + " 203.0.113.42 (IT)");
            add(cVpn, DiagState.Info, L.T("nessuna VPN attiva", "no active VPN"));
            foreach (var st in steps)
            {
                if (cancel) break;
                Set(st.Key, DiagState.Running, "...");
                Thread.Sleep(120);
                Set(st.Key, st.Value.Key, st.Value.Value);
            }
            Ui(Finished);
        }
    }
}

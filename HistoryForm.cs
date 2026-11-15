// Storico della rete: grafico della qualità (ping, segnale Wi-Fi, eventi) e registro eventi di un periodo,
// con l'assistente AI che risponde a domande come "stamattina alle 10 Teams si bloccava, cosa è successo?".
// Legge solo i file locali (events.log, quality.log): aprire la finestra non genera traffico.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CorbolIp
{
    class HistoryForm : AiChatForm
    {
        static HistoryForm open;

        public static void ShowOnce(Icon icon, Func<DiagSnapshot> snapshot, Func<int> pingInterval)
        {
            if (open != null && !open.IsDisposed && open.ai != AiSettings.Enabled && open.aiCts == null) open.Close();
            if (open == null || open.IsDisposed) open = new HistoryForm(icon, snapshot, pingInterval);
            else if (open.aiCts == null) open.LoadPeriod();   // dati aggiornati
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        readonly Func<DiagSnapshot> getSnapshot;
        readonly Func<int> getPing;
        readonly ComboBox cboPeriod;
        readonly Label lblSummary, lblSummary2, lblStatus;   // riepilogo: una riga per rete (le due più usate)
        readonly ToolTip tips = new ToolTip();
        readonly QualityChart chart;
        readonly ListView list;
        readonly Button btnCopy;

        DateTime from, to;
        List<QualitySample> samples = new List<QualitySample>();
        List<NetEvents.Entry> events = new List<NetEvents.Entry>();
        DiagSnapshot snap;
        int lastPeriod = -1;

        static readonly string[][] Periods =
        {
            new[] { "Ultime 24 ore", "Last 24 hours" }, new[] { "Oggi", "Today" }, new[] { "Ieri", "Yesterday" },
            new[] { "Ultimi 7 giorni", "Last 7 days" }, new[] { "Ultimi 30 giorni", "Last 30 days" }
        };

        HistoryForm(Icon icon, Func<DiagSnapshot> snapshot, Func<int> pingInterval)
        {
            getSnapshot = snapshot;
            getPing = pingInterval;
            Text = L.T("Storico rete - CorbolIp", "Network history - CorbolIp");
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true; // il widget sta sotto le finestre: questa deve vedersi subito
            // scala calcolata qui (font e DPI insieme): con la finestra ridimensionabile la scalatura automatica
            // allargava i controlli ancorati oltre il bordo
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            const int W = 700;   // larghezza del contenuto (coordinate a 9 pt)
            var lblPeriod = new Label { Text = L.T("Periodo:", "Period:"), AutoSize = true, Location = new Point(16, 18) };
            cboPeriod = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(90, 14), Size = new Size(170, 26) };
            cboPeriod.Items.AddRange(new object[]
            {
                L.T("Ultime 24 ore", "Last 24 hours"), L.T("Oggi", "Today"), L.T("Ieri", "Yesterday"),
                L.T("Ultimi 7 giorni", "Last 7 days"), L.T("Ultimi 30 giorni", "Last 30 days")
            });
            cboPeriod.SelectedIndex = 0;
            lblStatus = new Label { Text = "", AutoSize = false, AutoEllipsis = true, Location = new Point(276, 18), Size = new Size(W - 260, 22), ForeColor = Color.DimGray,
                                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lblSummary = new Label { Text = "", AutoSize = false, AutoEllipsis = true, Location = new Point(16, 46), Size = new Size(W, 19),
                                     Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            lblSummary2 = new Label { Text = "", AutoSize = false, AutoEllipsis = true, Location = new Point(16, 65), Size = new Size(W, 19),
                                      Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            chart = new QualityChart { Location = new Point(16, 88), Size = new Size(W, 116), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            list = new ListView
            {
                View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false,
                ShowItemToolTips = true, Location = new Point(16, 212), Size = new Size(W, 104),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            list.Columns.Add(L.T("Quando", "When"));
            list.Columns.Add(L.T("Evento", "Event"));
            list.SelectedIndexChanged += delegate
            {
                chart.Mark = list.SelectedItems.Count > 0 ? (DateTime?)((NetEvents.Entry)list.SelectedItems[0].Tag).Time : null;
            };
            Controls.AddRange(new Control[] { lblPeriod, cboPeriod, lblStatus, lblSummary, lblSummary2, chart, list });

            ai = AiSettings.Enabled;
            int by = 326;
            if (ai)
            {
                by = AddAiControls(326, W, L.T("Cosa è successo?", "What happened?"), 150, 116);
                txtAi.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                txtQuestion.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                btnAsk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                lnkSent.Location = new Point(256, by + 5);
                lnkSent.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            }
            else
            {
                // senza assistente la lista degli eventi prende lo spazio libero
                list.Anchor |= AnchorStyles.Bottom;
            }

            btnCopy = new Button { Text = L.T("Copia report", "Copy report"), Size = new Size(120, 30), Location = new Point(124, by), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var btnReload = new Button { Text = L.T("Aggiorna", "Refresh"), Size = new Size(100, 30), Location = new Point(16, by), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var btnClose = new Button { Text = L.T("Chiudi", "Close"), Size = new Size(90, 30), Location = new Point(16 + W - 90, by), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnCopy.Click += delegate
            {
                try { Clipboard.SetText(CopyText()); lblStatus.Text = L.T("Report copiato negli appunti.", "Report copied to the clipboard."); }
                catch { }
            };
            btnReload.Click += delegate { if (aiCts == null) LoadPeriod(); };
            btnClose.Click += delegate { Close(); };
            CancelButton = btnClose;
            var btnHelp = Help.Button(this, Help.History, new Point(16 + W - 128, by));
            btnHelp.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.AddRange(new Control[] { btnReload, btnCopy, btnClose, btnHelp });
            cboPeriod.SelectedIndexChanged += delegate { if (aiCts == null) LoadPeriod(); else cboPeriod.SelectedIndex = lastPeriod; };


            // coordinate pensate per 9 pt a 96 DPI; gli ancoraggi si rimettono dopo, altrimenti la finestra che cresce
            // allungherebbe i controlli prima che vengano scalati (larghi il doppio, oltre il bordo)
            // (Scale sulla finestra intera allargava comunque i controlli ancorati: si scalano uno per uno)
            var anchors = Controls.Cast<Control>().ToDictionary(c => c, c => c.Anchor);
            float k = UiFont.Scale * ScreenDpi() / 96f;
            foreach (Control c in Controls) { c.Anchor = AnchorStyles.Top | AnchorStyles.Left; c.Scale(new SizeF(k, k)); }
            ClientSize = new Size((int)Math.Round((W + 32) * k), (int)Math.Round((by + 44) * k));
            foreach (var kv in anchors) kv.Key.Anchor = kv.Value;
            MinimumSize = Size;
            // non più alta dello schermo (1080p al 125%): il riquadro dell'assistente si accorcia
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (Height > wa.Height) { MinimumSize = new Size(Width, wa.Height); Height = wa.Height; }
            Shown += delegate { FitColumns(); LoadPeriod(); ActiveControl = ai ? (Control)txtQuestion : list; };
            Resize += delegate { FitColumns(); };
        }

        void FitColumns()
        {
            int w = list.ClientSize.Width;
            list.Columns[0].Width = (int)(w * 0.22);
            list.Columns[1].Width = w - list.Columns[0].Width - 2;
        }

        // ---------- dati del periodo ----------
        void Range(int i, out DateTime f, out DateTime t)
        {
            var now = DateTime.Now;
            switch (i)
            {
                case 1: f = DateTime.Today; t = now; break;
                case 2: f = DateTime.Today.AddDays(-1); t = DateTime.Today; break;
                case 3: f = DateTime.Today.AddDays(-6); t = now; break;
                case 4: f = DateTime.Today.AddDays(-29); t = now; break;
                default: f = now.AddHours(-24); t = now; break;
            }
        }

        bool LongPeriod { get { return to - from > TimeSpan.FromHours(49); } }

        void LoadPeriod()
        {
            int idx = cboPeriod.SelectedIndex;
            Range(idx, out from, out to);
            try { snap = getSnapshot(); } catch { snap = new DiagSnapshot(); }
            samples = NetStats.Read(from, to);
            events = NetEvents.Between(from, to);
            chart.SetData(from, to, samples, events);

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var e in Enumerable.Reverse(events))   // dal più recente
            {
                string text = e.Text;
                var it = new ListViewItem(new[] { e.Time.ToString(LongPeriod || e.Time.Date != DateTime.Today ? "ddd d MMM HH:mm" : "HH:mm"), text })
                { Tag = e, ToolTipText = text };
                if (e.Kind == "NETDOWN" || e.Kind == "GWDOWN" || e.Kind == "INETDOWN") it.ForeColor = Color.FromArgb(190, 40, 40);
                else if (e.Kind == "QUALITY") it.ForeColor = Color.FromArgb(190, 110, 0);
                else if (e.Kind == "QUALITYOK") it.ForeColor = Color.FromArgb(30, 130, 60);
                list.Items.Add(it);
            }
            list.EndUpdate();

            // riassunto: una riga per rete (le due più usate nel periodo)
            var lines = new List<string>();
            foreach (var g in samples.GroupBy(s => s.Key).OrderByDescending(g => g.Count()).Take(2))
            {
                var l = g.ToList();
                lines.Add(l[l.Count - 1].Label + ": " + NetStats.Summary(l, false));
            }
            if (lines.Count == 0)
                lines.Add(NetStats.Enabled ? L.T("Nessuna misura in questo periodo (PC spento, in sospensione o CorbolIp chiuso).", "No measurements in this period (PC off, asleep or CorbolIp closed).")
                                           : L.T("La registrazione della qualità è spenta (menu Registro eventi).", "Quality recording is turned off (Event log menu)."));
            lblSummary.Text = lines[0];
            lblSummary2.Text = lines.Count > 1 ? lines[1] : "";
            tips.SetToolTip(lblSummary, lblSummary.Text);
            tips.SetToolTip(lblSummary2, lblSummary2.Text);
            int downs = events.Count(e => e.Kind == "NETDOWN"), pings = events.Count(e => e.Kind == "GWDOWN" || e.Kind == "INETDOWN"), worse = events.Count(e => e.Kind == "QUALITY");
            var st = NetStats.EventCounts(downs, pings, worse, false);
            if (getPing() <= 0) st.Add(L.T("ping spento: la latenza non si misura", "ping off: latency is not measured"));
            lblStatus.Text = string.Join(" · ", st);
            tips.SetToolTip(lblStatus, lblStatus.Text);

            if (ai && idx != lastPeriod)
            {
                ResetConversation(false);   // altri dati: la conversazione riparte
                ShowAi(L.T("Cosa è successo? riassume il periodo scelto. Puoi anche chiedere, per esempio \"ieri alle 10 Teams si bloccava, perché?\" " +
                           "o \"la rete di casa è peggiorata?\". Prepara segnalazione scrive il testo per l'assistenza, con la storia del problema.",
                           "What happened? summarizes the chosen period. You can also ask, for example \"yesterday at 10 Teams froze, why?\" " +
                           "or \"has my home network got worse?\". Write support request drafts the text for IT support, with the history of the problem."), true);
            }
            lastPeriod = idx;
            SetAiButtons(true);
        }

        string PeriodName()
        {
            return L.T(Periods[Math.Max(0, cboPeriod.SelectedIndex)][0], Periods[Math.Max(0, cboPeriod.SelectedIndex)][1]);
        }

        // Testo del periodo: localizzato per il report, in inglese per l'assistente
        string PeriodText(bool forAi)
        {
            var sb = new StringBuilder();
            var nets = samples.GroupBy(s => s.Key).OrderByDescending(g => g.Count()).ToList();
            foreach (var g in nets.Take(4))
            {
                var l = g.ToList();
                sb.AppendLine(NetStats.UsualText(g.Key, l[l.Count - 1].Label, forAi));
            }
            if (nets.Count > 0) sb.AppendLine();
            if (LongPeriod)
            {
                sb.AppendLine(forAi ? "Day by day:" : L.T("Giorno per giorno:", "Day by day:"));
                sb.Append(NetStats.DailyText((int)Math.Ceiling((to - from).TotalDays), forAi));
                sb.AppendLine();
            }
            string hours = NetStats.HourlyText(from, to, forAi, LongPeriod ? 24 : 48);
            if (hours.Length > 0)
            {
                sb.AppendLine(forAi ? "Connection quality by hour (5-minute measurements summarized):" : L.T("Qualità ora per ora:", "Quality hour by hour:"));
                sb.Append(hours);
                sb.AppendLine();
            }
            string ev = NetEvents.Text(events, LongPeriod ? 40 : 60, forAi);
            if (ev.Length > 0)
            {
                sb.AppendLine(forAi ? "Network events in this period:" : L.T("Eventi di rete del periodo:", "Network events in this period:"));
                sb.Append(ev);
            }
            else sb.AppendLine(forAi ? (NetEvents.Enabled ? "No network events recorded in this period." : "The network event log is turned off.")
                                     : L.T("Nessun evento di rete nel periodo.", "No network events in this period."));
            return sb.ToString();
        }

        string ReportText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.T("Storico rete", "Network history") + " - CorbolIp " + AppVersion.Text + " - " + PeriodName()
                          + " (" + from.ToString("g") + " - " + to.ToString("g") + ")");
            if (lblStatus.Text.Length > 0) sb.AppendLine(lblStatus.Text);
            sb.AppendLine();
            sb.Append(PeriodText(false));
            return sb.ToString();
        }

        string CopyText()
        {
            var sb = new StringBuilder();
            if (ticketText != null) sb.Append(Crlf(ticketText.Trim())).Append("\r\n\r\n----------\r\n");
            sb.Append(Crlf(ReportText()));
            if (chatLog.Length > 0) sb.Append("\r\n").Append(Crlf(chatLog.ToString().Trim())).Append("\r\n");
            return sb.ToString();
        }

        // ---------- assistente AI ----------
        protected override void SetHostEnabled(bool on) { cboPeriod.Enabled = on; }

        protected override void OnTicketReady()
        {
            lblStatus.Text = L.T("Segnalazione pronta: Copia report la copia insieme allo storico.", "Support request ready: Copy report copies it together with the history.");
        }

        protected override string DataIntro { get { return "Network history of my PC:"; } }
        protected override string ExplainHeader { get { return L.T("Cosa è successo", "What happened"); } }
        protected override string ExplainLogTitle { get { return L.T("Riassunto dell'assistente", "Assistant's summary"); } }
        protected override string ReportName { get { return "the network history report"; } }

        protected override string SystemPrompt(bool tools)
        {
            return "You are the network assistant built into CorbolIp, a small network widget for Windows. The first message contains the " +
                   "network history recorded on the user's PC for a period: network events (disconnections, network changes, router or Internet " +
                   "not answering ping, sleep and resume, times when the connection was worse than usual) and the connection quality measured " +
                   "every 5 minutes and summarized by hour, with the usual values of each network. Help a non-technical office worker understand " +
                   "what happened to their connection at the times they ask about (for example why a video call froze yesterday at 10). Match the " +
                   "time the user mentions with the data. Say clearly when the data shows nothing unusual at that time, and when there is no data " +
                   "(PC off or asleep, CorbolIp closed, or latency not measured because ping was off): in that case the problem may have been in " +
                   "the program or the remote service rather than the network. Point out patterns (same network, same time of day, after sleep, " +
                   "weak Wi-Fi) and give the most likely cause and what to do. " +
                   "Keep answers short (under about 150 words) unless the user asks for more. " + Common + (tools ? " " + ToolsHelp : "") +
                   " Answer in " + LangName() + ".";
        }

        protected override string ExplainRequest
        {
            get
            {
                return "Summarize what happened to my connection in this period: disconnections and times when it was worse than usual, how " +
                       "long they lasted, any pattern, and the most likely cause. If nothing unusual happened, say so briefly. Keep it under about 150 words.";
            }
        }

        protected override string AiData()
        {
            var sb = new StringBuilder();
            var c = CultureInfo.InvariantCulture;
            sb.AppendLine("Period: " + PeriodName() + ", from " + from.ToString("yyyy-MM-dd HH:mm (ddd)", c) + " to " + to.ToString("yyyy-MM-dd HH:mm (ddd)", c)
                          + " (local time). Now it is " + DateTime.Now.ToString("yyyy-MM-dd HH:mm (dddd)", c) + ".");
            int ping = getPing();
            sb.AppendLine(ping > 0 ? "Ping is on (every " + ping + " seconds): router and Internet latency are measured."
                                   : "Ping is currently off: latency is only measured while the user turns ping on in the CorbolIp menu.");
            var p = snap != null ? snap.Primary : null;
            if (p != null) sb.AppendLine("Network in use now: " + NetStats.LabelFor(p) + ".");
            sb.AppendLine();
            sb.Append(PeriodText(true));
            return sb.ToString();
        }

        protected override Anonymizer BuildAnon()
        {
            var a = new Anonymizer();
            if (snap != null)
            {
                a.Add(snap.Host, "PC");
                a.Add(snap.Domain, "DOMAIN");
                foreach (var l in snap.All)
                {
                    if (!string.IsNullOrEmpty(l.WifiSsid)) a.Add(l.WifiSsid, "WIFI");
                    a.Add(l.Mac, "MAC");
                }
            }
            try { a.Add(Environment.UserName, "USER"); } catch { }
            NetStats.AddPrivateNames(a);
            return a;
        }
    }

    // Grafico del periodo: ping a Internet (o al router), segnale Wi-Fi e eventi, con i valori al passaggio del mouse
    class QualityChart : Control
    {
        DateTime from, to;
        List<QualitySample> samples = new List<QualitySample>();
        List<NetEvents.Entry> events = new List<NetEvents.Entry>();
        DateTime? mark;
        int hoverX = -1;
        readonly ToolTip tip = new ToolTip { InitialDelay = 0, ReshowDelay = 0, AutoPopDelay = 30000 };
        string tipText;

        static readonly Color Grid = Color.FromArgb(225, 228, 232), Axis = Color.FromArgb(110, 115, 120);
        static readonly Color PingC = Color.FromArgb(40, 110, 200), SigC = Color.FromArgb(46, 150, 80);
        static readonly Color BadC = Color.FromArgb(210, 55, 55), WarnC = Color.FromArgb(230, 145, 0), InfoC = Color.FromArgb(150, 155, 165);

        public QualityChart()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.White;
        }

        public DateTime? Mark { set { mark = value; Invalidate(); } }

        public void SetData(DateTime f, DateTime t, List<QualitySample> s, List<NetEvents.Entry> e)
        {
            from = f; to = t; samples = s; events = e; mark = null;
            Invalidate();
        }

        int LabelW { get { return (int)(Font.Size * 9.5f); } }

        float X(DateTime t, Rectangle r)
        {
            double span = Math.Max(1, (to - from).TotalSeconds);
            return r.Left + (float)((t - from).TotalSeconds / span * r.Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var small = new Font(Font.FontFamily, Font.Size - 1.5f))
            using (var axisBr = new SolidBrush(Axis))
            using (var gridPen = new Pen(Grid))
            {
                int lh = small.Height;
                var plot = new Rectangle(LabelW, 2, Width - LabelW - 4, Height - lh - 6);
                int laneGap = 4;
                int evH = Math.Max(10, lh);
                int laneH = (plot.Height - evH - 2 * laneGap) / 2;
                var lPing = new Rectangle(plot.Left, plot.Top, plot.Width, laneH);
                var lSig = new Rectangle(plot.Left, lPing.Bottom + laneGap, plot.Width, laneH);
                var lEv = new Rectangle(plot.Left, lSig.Bottom + laneGap, plot.Width, evH);

                bool hasIn = samples.Any(s => s.HasIn), hasGw = samples.Any(s => s.HasGw);
                string pingName = hasIn || !hasGw ? L.T("Ping Internet", "Internet ping") : L.T("Ping router", "Router ping");
                g.DrawString(pingName, small, axisBr, 0, lPing.Top + (lPing.Height - lh) / 2);
                g.DrawString(L.T("Segnale Wi-Fi", "Wi-Fi signal"), small, axisBr, 0, lSig.Top + (lSig.Height - lh) / 2);
                g.DrawString(L.T("Eventi", "Events"), small, axisBr, 0, lEv.Top + (lEv.Height - lh) / 2);
                foreach (var r in new[] { lPing, lSig, lEv }) g.DrawRectangle(gridPen, r);

                // asse del tempo: ore o giorni
                double hours = (to - from).TotalHours;
                int stepH = hours <= 26 ? 3 : hours <= 50 ? 6 : hours <= 8 * 24 ? 24 : 24 * 5;
                var t0 = new DateTime(from.Year, from.Month, from.Day, from.Hour - from.Hour % Math.Min(stepH, 24), 0, 0);
                for (var t = t0; t <= to; t = t.AddHours(stepH))
                {
                    if (t < from) continue;
                    float x = X(t, plot);
                    g.DrawLine(gridPen, x, plot.Top, x, lEv.Bottom);
                    string lbl = stepH < 24 ? t.ToString("HH:mm") : t.ToString("d MMM");
                    var sz = g.MeasureString(lbl, small);
                    if (x + sz.Width / 2 < Width && x - sz.Width / 2 > LabelW - 10)
                        g.DrawString(lbl, small, axisBr, x - sz.Width / 2, lEv.Bottom + 2);
                }

                if (samples.Count == 0 && events.Count == 0)
                {
                    string none = L.T("Nessun dato in questo periodo", "No data in this period");
                    var sz = g.MeasureString(none, Font);
                    g.DrawString(none, Font, axisBr, plot.Left + (plot.Width - sz.Width) / 2, plot.Top + (lSig.Bottom - plot.Top - sz.Height) / 2);
                    return;
                }

                // ping: scala fino al 95° percentile (minimo 50 ms); oltre, il punto resta in cima in rosso
                Func<QualitySample, bool> has = s => hasIn || !hasGw ? s.HasIn : s.HasGw;
                Func<QualitySample, int> ms = s => hasIn || !hasGw ? s.InMs : s.GwMs;
                Func<QualitySample, int> lost = s => hasIn || !hasGw ? s.InLost : s.GwLost;
                Func<QualitySample, int> okN = s => hasIn || !hasGw ? s.InOk : s.GwOk;
                var pv = samples.Where(has).Select(ms).OrderBy(v => v).ToList();
                if (pv.Count == 0)
                    g.DrawString(L.T("non misurato (ping spento)", "not measured (ping off)"), small, axisBr, lPing.Left + 6, lPing.Top + (lPing.Height - lh) / 2);
                else
                {
                    int max = Math.Max(50, pv[Math.Min(pv.Count - 1, (int)(pv.Count * 0.95))] * 5 / 4);
                    g.DrawString(max + " ms", small, axisBr, lPing.Right - g.MeasureString(max + " ms", small).Width - 2, lPing.Top + 1);
                    DrawSeries(g, lPing, PingC, s => has(s) ? Math.Min(1f, ms(s) / (float)max) : -1f, s => has(s) && ms(s) > max);
                    using (var br = new SolidBrush(BadC))
                        foreach (var s in samples.Where(s => lost(s) > 0))
                        {
                            float x = X(s.Time, lPing), w = Math.Max(2f, X(s.Time.AddMinutes(NetStats.BucketMin), lPing) - x);
                            float h = Math.Max(3f, lPing.Height * 0.5f * lost(s) / (float)(lost(s) + okN(s)));   // altezza = quota di ping persi
                            g.FillRectangle(br, x, lPing.Bottom - h, w, h);
                        }
                }

                if (samples.Any(s => s.Sig >= 0))
                    DrawSeries(g, lSig, SigC, s => s.Sig >= 0 ? s.Sig / 100f : -1f, s => false);
                else
                    g.DrawString(L.T("cavo o nessun dato", "cable or no data"), small, axisBr, lSig.Left + 6, lSig.Top + (lSig.Height - lh) / 2);

                foreach (var ev in events)
                {
                    Color c = ev.Kind == "NETDOWN" || ev.Kind == "GWDOWN" || ev.Kind == "INETDOWN" ? BadC
                            : ev.Kind == "QUALITY" ? WarnC : ev.Kind == "QUALITYOK" ? SigC : InfoC;
                    float x = X(ev.Time, lEv);
                    using (var pen = new Pen(c, c == InfoC ? 1f : 2.5f)) g.DrawLine(pen, x, lEv.Top + 2, x, lEv.Bottom - 2);
                }

                if (mark.HasValue)
                    using (var pen = new Pen(Color.FromArgb(120, 60, 60, 60)) { DashStyle = DashStyle.Dash })
                    {
                        float x = X(mark.Value, plot);
                        g.DrawLine(pen, x, plot.Top, x, lEv.Bottom);
                    }
                if (hoverX >= plot.Left && hoverX <= plot.Right)
                    using (var pen = new Pen(Color.FromArgb(90, 0, 0, 0))) g.DrawLine(pen, hoverX, plot.Top, hoverX, lEv.Bottom);
            }
        }

        // Linea con interruzioni dove mancano i dati (più di 10 minuti) e punti rossi per i valori fuori scala
        void DrawSeries(Graphics g, Rectangle r, Color c, Func<QualitySample, float> val, Func<QualitySample, bool> over)
        {
            using (var pen = new Pen(c, 1.6f))
            using (var dot = new SolidBrush(c))
            using (var bad = new SolidBrush(BadC))
            {
                PointF? prev = null;
                DateTime prevT = DateTime.MinValue;
                foreach (var s in samples)
                {
                    float v = val(s);
                    if (v < 0) { prev = null; continue; }
                    var pt = new PointF(X(s.Time.AddMinutes(NetStats.BucketMin / 2.0), r), r.Bottom - 2 - v * (r.Height - 4));
                    if (prev.HasValue && s.Time - prevT <= TimeSpan.FromMinutes(2 * NetStats.BucketMin)) g.DrawLine(pen, prev.Value, pt);
                    else g.FillEllipse(dot, pt.X - 1.2f, pt.Y - 1.2f, 2.4f, 2.4f);
                    if (over(s)) g.FillEllipse(bad, pt.X - 2.5f, pt.Y - 2.5f, 5f, 5f);
                    prev = pt; prevT = s.Time;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var plotW = Width - LabelW - 4;
            if (e.X < LabelW || plotW <= 0 || to <= from) { HideTip(); return; }
            var t = from.AddSeconds((e.X - LabelW) / (double)plotW * (to - from).TotalSeconds);
            hoverX = e.X;
            Invalidate();
            var near = samples.Where(s => Math.Abs((s.Time.AddMinutes(NetStats.BucketMin / 2.0) - t).TotalMinutes) <= Math.Max(NetStats.BucketMin, (to - from).TotalMinutes / Math.Max(1, plotW) * 3))
                              .OrderBy(s => Math.Abs((s.Time - t).TotalSeconds)).FirstOrDefault();
            double tol = Math.Max(5, (to - from).TotalMinutes / Math.Max(1, plotW) * 4);
            var evs = events.Where(ev => Math.Abs((ev.Time - t).TotalMinutes) <= tol).ToList();
            var sb = new StringBuilder();
            if (near != null)
                sb.AppendLine(near.Time.ToString("ddd d MMM HH:mm") + " · " + near.Label).AppendLine(NetStats.Summary(new List<QualitySample> { near }, false));
            foreach (var ev in evs.Take(4)) sb.AppendLine(ev.Time.ToString("HH:mm") + "  " + ev.Text);
            string text = sb.ToString().Trim();
            if (text.Length == 0) { HideTip(); return; }
            if (text != tipText) { tipText = text; tip.Show(text, this, e.X + 14, e.Y + 18); }
        }

        void HideTip()
        {
            if (tipText != null) { tip.Hide(this); tipText = null; }
            if (hoverX != -1) { hoverX = -1; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            HideTip();
        }
    }
}

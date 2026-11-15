// Presentazione per chi usa CorbolIp per la prima volta: 4 schede (il widget, il menu, la diagnosi, lo storico).
// Compare una volta sola al primo avvio (setting welcome=1) e si riapre dal menu (Benvenuto...).
// Le illustrazioni sono disegnate qui, con i testi nella lingua dell'app; il widget è un'immagine incorporata
// (res\welcome-widget-it.png / -en.png, fatte con /demo).
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CorbolIp
{
    class WelcomeForm : Form
    {
        static WelcomeForm open;

        public static void ShowOnce(Icon icon, Func<int> getPing, Action<int> setPing)
        {
            if (open == null || open.IsDisposed) open = new WelcomeForm(icon, getPing, setPing);
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        const int Pages = 4;
        readonly Func<int> getPing;
        readonly Action<int> setPing;
        readonly Panel art;
        readonly Label lblTitle, lblText;
        readonly Button btnBack, btnNext, btnPing;
        readonly LinkLabel lnkSkip, lnkGuide;
        int page;
        Image widgetImg;

        static readonly Color ArtBack = Color.FromArgb(243, 245, 248), Accent = Color.FromArgb(40, 110, 200);
        static readonly Color Good = Color.FromArgb(46, 160, 80), Bad = Color.FromArgb(210, 55, 55), Warn = Color.FromArgb(230, 145, 0), Info = Color.FromArgb(120, 140, 170);

        WelcomeForm(Icon icon, Func<int> getPing, Action<int> setPing)
        {
            this.getPing = getPing;
            this.setPing = setPing;
            Text = L.T("Benvenuto in CorbolIp", "Welcome to CorbolIp");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true; // il widget sta sotto le finestre: questa deve vedersi subito
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;
            try
            {
                using (var st = typeof(WelcomeForm).Assembly.GetManifestResourceStream(L.Code == "it" ? "CorbolIp.welcome-widget-it.png" : "CorbolIp.welcome-widget-en.png"))
                    if (st != null) widgetImg = Image.FromStream(st);
            }
            catch { }

            art = new DoubleBufferedPanel { Location = new Point(0, 0), Size = new Size(560, 230), BackColor = ArtBack };
            art.Paint += (s, e) => DrawArt(e.Graphics, art.ClientSize);
            lblTitle = new Label { AutoSize = true, Location = new Point(24, 246), Font = new Font("Segoe UI Semibold", UiFont.Pt + 3f) };
            // spazio per 6-7 righe: lo stesso testo occupa più o meno righe secondo schermo e impostazioni di Windows
            lblText = new Label { AutoSize = false, Location = new Point(24, 282), Size = new Size(512, 148), Font = new Font("Segoe UI", UiFont.Pt + 1.5f) };
            btnPing = new Button { Location = new Point(24, 436), Size = new Size(260, 30), Visible = false };
            btnPing.Click += delegate { setPing(60); ShowPage(); };
            // dimensioni fisse e larghe: con AutoSize e la scalatura il testo dei collegamenti veniva tagliato
            lnkGuide = new LinkLabel { Text = L.T("Apri la guida completa", "Open the full guide"), AutoSize = false, Location = new Point(300, 438), Size = new Size(236, 26),
                                       TextAlign = ContentAlignment.MiddleLeft, LinkBehavior = LinkBehavior.AlwaysUnderline, Visible = false };
            lnkGuide.LinkClicked += delegate { Help.Open(Help.Guide); };
            lnkSkip = new LinkLabel { Text = L.T("Salta", "Skip"), AutoSize = false, Location = new Point(24, 485), Size = new Size(200, 26),
                                      TextAlign = ContentAlignment.MiddleLeft, LinkBehavior = LinkBehavior.AlwaysUnderline };
            lnkSkip.LinkClicked += delegate { Close(); };
            btnBack = new Button { Text = L.T("Indietro", "Back"), Location = new Point(332, 482), Size = new Size(100, 32) };
            btnNext = new Button { Location = new Point(440, 482), Size = new Size(100, 32) };
            btnBack.Click += delegate { if (page > 0) { page--; ShowPage(); } };
            btnNext.Click += delegate { if (page < Pages - 1) { page++; ShowPage(); } else Close(); };
            AcceptButton = btnNext;
            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.Right && page < Pages - 1) { page++; ShowPage(); }
                else if (e.KeyCode == Keys.Left && page > 0) { page--; ShowPage(); }
            };
            Controls.AddRange(new Control[] { art, lblTitle, lblText, btnPing, lnkGuide, lnkSkip, btnBack, btnNext });
            ClientSize = new Size(560, 528);
            Scale(new SizeF(UiFont.Scale, UiFont.Scale)); // coordinate pensate per 9 pt
            ShowPage();
            Shown += delegate { btnNext.Focus(); };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (widgetImg != null) widgetImg.Dispose();
            base.OnFormClosed(e);
        }

        void ShowPage()
        {
            btnBack.Enabled = page > 0;
            btnNext.Text = page < Pages - 1 ? L.T("Avanti", "Next") : L.T("Inizia", "Start");
            lnkSkip.Visible = page < Pages - 1;
            btnPing.Visible = lnkGuide.Visible = page == Pages - 1;
            switch (page)
            {
                case 0:
                    lblTitle.Text = L.T("Il widget", "The widget");
                    lblText.Text = L.T("CorbolIp mostra sul desktop nome del PC, indirizzi IP, IP pubblico, Wi-Fi, VPN e velocità della rete. " +
                                       "Resta sotto le finestre e lo vedi con Win+D. Trascinalo dove vuoi; un clic su un valore lo copia negli appunti.",
                                       "CorbolIp shows on the desktop the PC name, IP addresses, public IP, Wi-Fi, VPN and network speed. " +
                                       "It stays below the windows and you see it with Win+D. Drag it anywhere; click a value to copy it to the clipboard.");
                    break;
                case 1:
                    lblTitle.Text = L.T("Tutto dal tasto destro", "Everything from the right click");
                    lblText.Text = L.T("Tasto destro sul widget o sull'icona vicino all'orologio: lì ci sono tutte le funzioni, dal test velocità " +
                                       "a cosa mostrare nel widget. Doppio clic sull'icona mostra o nasconde il widget.",
                                       "Right-click the widget or the icon next to the clock: all the features are there, from the speed test " +
                                       "to what the widget shows. Double-click the icon to show or hide the widget.");
                    break;
                case 2:
                    lblTitle.Text = L.T("Internet non va?", "The Internet is down?");
                    lblText.Text = L.T("Apri Diagnosi rete: in pochi secondi controlla Wi-Fi, router, Internet e DNS e ti dice in parole semplici " +
                                       "dov'è il problema e cosa fare. Copia report prepara il testo da mandare all'assistenza; con l'assistente AI " +
                                       "(facoltativo) puoi anche fare domande.",
                                       "Open Network diagnostics: in a few seconds it checks Wi-Fi, router, Internet and DNS and tells you in plain " +
                                       "words where the problem is and what to do. Copy report prepares the text for IT support; with the AI " +
                                       "assistant (optional) you can also ask questions.");
                    break;
                default:
                    lblTitle.Text = L.T("Cosa è successo?", "What happened?");
                    lblText.Text = L.T("CorbolIp ricorda come va la tua rete e ti avvisa quando va molto peggio del solito. In Storico rete vedi " +
                                       "cosa è successo in un certo momento, per esempio durante una videochiamata che si bloccava. " +
                                       "La latenza si misura solo con il ping, spento per non generare traffico.",
                                       "CorbolIp remembers how your network behaves and notifies you when it is much worse than usual. In Network " +
                                       "history you see what happened at a given time, for example during a video call that kept freezing. " +
                                       "Latency is measured only with ping, which is off so as not to generate traffic.");
                    int ping = getPing();
                    btnPing.Enabled = ping <= 0;
                    btnPing.Text = ping > 0 ? L.F("Ping acceso (ogni {0} s) ✓", "Ping on (every {0} s) ✓", ping)
                                            : L.T("Accendi il ping ogni 60 secondi", "Turn on ping every 60 seconds");
                    break;
            }
            art.Invalidate();
        }

        // ---------- illustrazioni ----------
        void DrawArt(Graphics g, Size sz)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float u = sz.Height / 230f;   // unità: l'altezza del riquadro è 230 a 9 pt
            switch (page)
            {
                case 0: DrawWidget(g, sz, u); break;
                case 1: DrawMenu(g, sz, u); break;
                case 2: DrawDiagnostics(g, sz, u); break;
                default: DrawHistory(g, sz, u); break;
            }
            // pallini delle schede
            float d = 7 * u, gap = 8 * u, w = Pages * d + (Pages - 1) * gap, x = (sz.Width - w) / 2, y = sz.Height - 14 * u;
            for (int i = 0; i < Pages; i++)
                using (var br = new SolidBrush(i == page ? Accent : Color.FromArgb(200, 205, 212)))
                    g.FillEllipse(br, x + i * (d + gap), y, d, d);
        }

        void DrawWidget(Graphics g, Size sz, float u)
        {
            if (widgetImg == null) return;
            float maxH = sz.Height - 34 * u, maxW = sz.Width - 40 * u;
            float k = Math.Min(1f, Math.Min(maxH / widgetImg.Height, maxW / widgetImg.Width));
            float w = widgetImg.Width * k, h = widgetImg.Height * k;
            var r = new RectangleF((sz.Width - w) / 2, 10 * u, w, h);
            using (var sh = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillRectangle(sh, r.X + 4 * u, r.Y + 4 * u, w, h);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(widgetImg, r);
        }

        void Card(Graphics g, RectangleF r, float u)
        {
            using (var sh = new SolidBrush(Color.FromArgb(30, 0, 0, 0))) g.FillRectangle(sh, r.X + 3 * u, r.Y + 3 * u, r.Width, r.Height);
            g.FillRectangle(Brushes.White, r);
            using (var pen = new Pen(Color.FromArgb(200, 205, 212))) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        void DrawMenu(Graphics g, Size sz, float u)
        {
            var items = new List<string>
            {
                "CorbolIp " + AppVersion.Text, "-", L.T("Mostra widget", "Show widget"), L.T("Copia tutto", "Copy all"), "-",
                L.T("Test velocità...", "Speed test..."), L.T("Diagnosi rete...", "Network diagnostics..."),
                L.T("Storico rete...", "Network history..."), L.T("Assistente AI...", "AI assistant..."), "-", "Ping  ▸"
            };
            using (var f = new Font(Font.FontFamily, Font.Size * 0.9f))
            {
                float lh = f.GetHeight(g) * 1.12f, sepH = 7 * u;
                float h = 10 * u, w = 0;
                foreach (var it in items) { h += it == "-" ? sepH : lh; if (it != "-") w = Math.Max(w, g.MeasureString(it, f).Width); }
                w += 40 * u;
                var r = new RectangleF(sz.Width / 2 - w / 2 + 50 * u, 10 * u, w, h);
                Card(g, r, u);
                float y = r.Y + 5 * u;
                foreach (var it in items)
                {
                    if (it == "-")
                    {
                        using (var pen = new Pen(Color.FromArgb(225, 228, 232))) g.DrawLine(pen, r.X + 8 * u, y + sepH / 2, r.Right - 8 * u, y + sepH / 2);
                        y += sepH;
                        continue;
                    }
                    bool hot = it == L.T("Diagnosi rete...", "Network diagnostics...");
                    if (hot) using (var hb = new SolidBrush(Color.FromArgb(220, 232, 248))) g.FillRectangle(hb, r.X + 3 * u, y, r.Width - 6 * u, lh);
                    using (var tb = new SolidBrush(it.StartsWith("CorbolIp") ? Color.Gray : Color.FromArgb(30, 30, 30)))
                        g.DrawString(it, f, tb, r.X + 24 * u, y + (lh - f.GetHeight(g)) / 2);
                    y += lh;
                }
                // mouse con il tasto destro evidenziato, a sinistra del menu
                float mx = r.X - 95 * u, my = r.Y + 30 * u, mw = 54 * u, mh = 80 * u;
                using (var path = Rounded(new RectangleF(mx, my, mw, mh), 26 * u))
                {
                    g.FillPath(Brushes.White, path);
                    using (var pen = new Pen(Color.FromArgb(120, 125, 135), 1.6f * u)) g.DrawPath(pen, path);
                }
                using (var right = new GraphicsPath())
                {
                    right.AddArc(mx, my, mw, 52 * u, 270, 90);
                    right.AddLine(mx + mw, my + 30 * u, mx + mw / 2, my + 30 * u);
                    right.AddLine(mx + mw / 2, my + 30 * u, mx + mw / 2, my);
                    right.CloseFigure();
                    using (var br = new SolidBrush(Accent)) g.FillPath(br, right);
                }
                using (var pen = new Pen(Color.FromArgb(120, 125, 135), 1.2f * u))
                {
                    g.DrawLine(pen, mx, my + 30 * u, mx + mw, my + 30 * u);
                    g.DrawLine(pen, mx + mw / 2, my, mx + mw / 2, my + 30 * u);
                }
                using (var pen = new Pen(Accent, 2f * u) { DashStyle = DashStyle.Dot, EndCap = LineCap.ArrowAnchor })
                    g.DrawLine(pen, mx + mw + 6 * u, my + 14 * u, r.X - 8 * u, my + 14 * u);
            }
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void DrawDiagnostics(Graphics g, Size sz, float u)
        {
            var rows = new[]
            {
                new KeyValuePair<string, Color>(L.T("Connessione", "Connection") + "  ·  Wi-Fi", Good),
                new KeyValuePair<string, Color>(L.T("Indirizzo IP", "IP address") + "  ·  192.168.1.50", Good),
                new KeyValuePair<string, Color>(L.T("Router (gateway)", "Router (gateway)") + "  ·  3 ms", Good),
                new KeyValuePair<string, Color>(L.T("Ping Internet", "Internet ping") + "  ·  " + L.T("1.1.1.1 e 8.8.8.8 non rispondono", "1.1.1.1 and 8.8.8.8 do not answer"), Bad),
                new KeyValuePair<string, Color>("DNS", Info),
            };
            using (var f = new Font(Font.FontFamily, Font.Size * 0.9f))
            using (var fb = new Font("Segoe UI Semibold", Font.Size * 0.95f))
            {
                float lh = f.GetHeight(g) * 1.35f;
                float w = 0;
                foreach (var r0 in rows) w = Math.Max(w, g.MeasureString(r0.Key, f).Width);
                w += 50 * u;
                float h = rows.Length * lh + 2.4f * lh + 12 * u;
                var r = new RectangleF((sz.Width - w) / 2, 10 * u, w, h);
                Card(g, r, u);
                float y = r.Y + 8 * u, dot = 10 * u;
                foreach (var row in rows)
                {
                    using (var br = new SolidBrush(row.Value)) g.FillEllipse(br, r.X + 14 * u, y + (lh - dot) / 2, dot, dot);
                    g.DrawString(row.Key, f, Brushes.Black, r.X + 32 * u, y + (lh - f.GetHeight(g)) / 2);
                    y += lh;
                }
                y += 6 * u;
                using (var br = new SolidBrush(Color.FromArgb(190, 40, 40)))
                    g.DrawString(L.T("Nessuna uscita su Internet", "No Internet connection"), fb, br, r.X + 14 * u, y);
                y += fb.GetHeight(g) * 1.1f;
                g.DrawString(L.T("Il problema è nel router o nella linea del fornitore.", "The problem is in the router or the provider's line."), f, Brushes.DimGray, r.X + 14 * u, y);
            }
        }

        void DrawHistory(Graphics g, Size sz, float u)
        {
            var r = new RectangleF(40 * u, 14 * u, sz.Width - 80 * u, 130 * u);
            Card(g, r, u);
            using (var f = new Font(Font.FontFamily, Font.Size * 0.85f))
            {
                g.DrawString(L.T("Ping Internet", "Internet ping"), f, Brushes.DimGray, r.X + 10 * u, r.Y + 8 * u);
                var pts = new List<PointF>();
                var rnd = new Random(5);
                float x0 = r.X + 14 * u, x1 = r.Right - 14 * u, baseY = r.Bottom - 34 * u;
                int n = 60;
                for (int i = 0; i <= n; i++)
                {
                    float x = x0 + (x1 - x0) * i / n;
                    bool spike = i >= 16 && i <= 24;   // a sinistra: la notifica sta a destra
                    float v = spike ? 62 + rnd.Next(14) : 4 + rnd.Next(6);
                    pts.Add(new PointF(x, baseY - v * u));
                }
                using (var pen = new Pen(Accent, 2f * u)) g.DrawLines(pen, pts.ToArray());
                using (var pen = new Pen(Color.FromArgb(225, 228, 232))) g.DrawLine(pen, x0, baseY + 2 * u, x1, baseY + 2 * u);
                float ex = x0 + (x1 - x0) * 16 / n, ox = x0 + (x1 - x0) * 25 / n;
                using (var pen = new Pen(Warn, 3f * u)) g.DrawLine(pen, ex, baseY + 8 * u, ex, baseY + 22 * u);
                using (var pen = new Pen(Good, 3f * u)) g.DrawLine(pen, ox, baseY + 8 * u, ox, baseY + 22 * u);
                g.DrawString("10:00", f, Brushes.DimGray, ex - 16 * u, baseY + 22 * u - 1);

                // notifica come quelle di Windows, in basso a destra
                string title = L.F("{0}: rete peggio del solito", "{0}: network worse than usual", "Wi-Fi \"" + L.T("Ufficio", "Office") + "\"");
                string body = L.F("ping Internet {0} ms (di solito {1} ms)", "Internet ping {0} ms (usually {1} ms)", 185, 15);
                using (var fb = new Font("Segoe UI Semibold", Font.Size * 0.85f))
                {
                    float w = Math.Max(g.MeasureString(title, fb).Width, g.MeasureString(body, f).Width) + 24 * u;
                    float h = fb.GetHeight(g) + f.GetHeight(g) + 16 * u;
                    var t = new RectangleF(sz.Width - w - 24 * u, r.Bottom - 6 * u, w, h);
                    using (var sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillRectangle(sh, t.X + 3 * u, t.Y + 3 * u, w, h);
                    using (var br = new SolidBrush(Color.FromArgb(40, 44, 52))) g.FillRectangle(br, t);
                    using (var br = new SolidBrush(Warn)) g.FillRectangle(br, t.X, t.Y, 4 * u, h);
                    g.DrawString(title, fb, Brushes.White, t.X + 12 * u, t.Y + 7 * u);
                    using (var br = new SolidBrush(Color.FromArgb(210, 214, 220))) g.DrawString(body, f, br, t.X + 12 * u, t.Y + 7 * u + fb.GetHeight(g));
                }
            }
        }

        class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        }
    }
}

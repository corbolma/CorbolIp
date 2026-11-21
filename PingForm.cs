// Finestra "Ping ricevuti": chi ha fatto ping a questo PC (vedi PingWatch.cs). Si apre dal menu; i ping ricevuti
// finiscono anche nel registro eventi (Storico rete).
// Senza registro del firewall mostra solo quando sono arrivati (mentre CorbolIp era aperta); con il registro
// acceso mostra indirizzo, esito (risposto o bloccato) e quante volte.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CorbolIp
{
    class PingForm : Form
    {
        static PingForm open;
        public static PingForm Current { get { return open != null && !open.IsDisposed ? open : null; } }
        public bool Busy { get { return loading; } }

        public static void ShowOnce(Icon icon)
        {
            if (open == null || open.IsDisposed) open = new PingForm(icon);
            else open.Reload();
            open.Show();
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        readonly Label lblStatus, lblLog, lblEmpty;
        readonly ListView list;
        readonly Button btnLog, btnRefresh, btnCopy;
        volatile bool loading;
        PingWatch.LogState logState;
        List<PingSource> sources = new List<PingSource>();
        List<PingEvent> counted = new List<PingEvent>();
        bool logReadable;

        PingForm(Icon icon)
        {
            Text = L.T("Ping ricevuti - CorbolIp", "Pings received - CorbolIp");
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            var top = new Panel { Dock = DockStyle.Top, Height = 112, Padding = new Padding(12, 8, 12, 4) };
            lblStatus = new Label { Dock = DockStyle.Top, Height = 44, AutoEllipsis = true };
            lblLog = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(80, 80, 80) };
            top.Controls.Add(lblLog);
            top.Controls.Add(lblStatus);

            list = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
                Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            list.Columns.Add(L.T("Da", "From"));
            list.Columns.Add(L.T("Dove", "Where"));
            list.Columns.Add(L.T("Esito", "Result"));
            list.Columns.Add(L.T("Ping", "Pings"));
            list.Columns.Add(L.T("Primo", "First"));
            list.Columns.Add(L.T("Ultimo", "Last"));
            list.Resize += delegate { FitColumns(); };
            // messaggio quando l'elenco è vuoto, sotto le intestazioni delle colonne
            lblEmpty = new Label { AutoSize = false, Height = 60, Dock = DockStyle.Bottom, TextAlign = ContentAlignment.TopCenter, ForeColor = Color.FromArgb(110, 110, 110), Visible = false };
            list.Controls.Add(lblEmpty);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10, 6, 10, 6) };
            btnLog = new Button { Size = new Size(260, 30), Enabled = false };
            btnRefresh = new Button { Text = L.T("Aggiorna", "Refresh"), Size = new Size(100, 30) };
            btnCopy = new Button { Text = L.T("Copia elenco", "Copy list"), Size = new Size(120, 30), Enabled = false };
            var btnClose = new Button { Text = L.T("Chiudi", "Close"), Size = new Size(100, 30) };
            btnLog.Click += delegate { ToggleLog(); };
            btnRefresh.Click += delegate { Reload(); };
            btnCopy.Click += delegate { try { Clipboard.SetText(ListText()); } catch { } };
            btnClose.Click += delegate { Close(); };
            CancelButton = btnClose;
            bar.Controls.AddRange(new Control[] { btnLog, btnRefresh, btnCopy, btnClose, Help.Button(this, Help.History, Point.Empty) });

            Controls.Add(list);
            Controls.Add(top);
            Controls.Add(bar);
            ClientSize = new Size(760, 460);
            MinimumSize = new Size(560, 340);
            Scale(new SizeF(UiFont.Scale, UiFont.Scale));
            Shown += delegate { FitColumns(); Reload(); };
        }

        void FitColumns()
        {
            int w = list.Width - SystemInformation.VerticalScrollBarWidth - 2 * SystemInformation.Border3DSize.Width - 2;
            if (w <= 0) return;
            int[] pct = { 26, 14, 14, 10, 18 };
            int used = 0;
            for (int i = 0; i < pct.Length; i++) { list.Columns[i].Width = w * pct[i] / 100; used += list.Columns[i].Width; }
            list.Columns[5].Width = w - used - 4;
        }

        public void Reload()
        {
            if (loading) return;
            loading = true;
            btnRefresh.Enabled = btnCopy.Enabled = btnLog.Enabled = false;
            lblStatus.Text = L.T("Lettura in corso...", "Reading...");
            var t = new Thread(delegate ()
            {
                string status = "";
                var state = PingWatch.LogState.Off;
                bool readable = false;
                var src = new List<PingSource>();
                var cnt = new List<PingEvent>();
                try
                {
                    status = PingWatch.Summary();
                    state = PingWatch.Logging();
                    if (state == PingWatch.LogState.Ours) src = PingWatch.Group(PingWatch.FromLog(out readable));
                    cnt = PingWatch.SeenWhileOpen();
                }
                catch { }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        lblStatus.Text = status;
                        logState = state; logReadable = readable; sources = src; counted = cnt;
                        loading = false;
                        btnRefresh.Enabled = btnCopy.Enabled = btnLog.Enabled = true;
                        Fill();
                    }));
                }
                catch { loading = false; }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Fill()
        {
            btnLog.Text = logState == PingWatch.LogState.Ours ? L.T("Smetti di registrare chi fa ping", "Stop recording who pings")
                                                              : L.T("Registra chi fa ping...", "Record who pings...");
            switch (logState)
            {
                case PingWatch.LogState.Ours:
                    lblLog.Text = logReadable
                        ? L.F("Il registro del firewall di Windows è acceso: qui sotto chi ha fatto ping (anche quelli bloccati), dal più recente. Il registro tiene fino a {0} MB, poi ricomincia.",
                              "The Windows firewall log is on: below, who pinged this PC (blocked pings too), most recent first. The log keeps up to {0} MB, then starts over.", 16 * 2)
                        : L.T("Il registro del firewall è acceso ma non c'è ancora o non si legge: riprova tra qualche minuto.",
                              "The firewall log is on but not there yet or not readable: try again in a few minutes.");
                    break;
                case PingWatch.LogState.Elsewhere:
                    lblLog.Text = L.T("Il registro del firewall è già acceso da qualcun altro, in una cartella che CorbolIp non legge. Con \"Registra chi fa ping...\" lo sposti nella cartella di CorbolIp.",
                                      "The firewall log was already turned on by someone else, in a folder CorbolIp can't read. \"Record who pings...\" moves it to the CorbolIp folder.");
                    break;
                default:
                    lblLog.Text = L.T("Chi ha fatto ping lo sa solo il registro del firewall di Windows, che ora è spento: qui sotto vedi solo quando sono arrivati i ping, mentre CorbolIp era aperta. \"Registra chi fa ping...\" lo accende (serve la conferma di amministratore).",
                                      "Only the Windows firewall log knows who pinged, and it's off now: below you only see when pings arrived while CorbolIp was open. \"Record who pings...\" turns it on (administrator confirmation needed).");
                    break;
            }

            list.BeginUpdate();
            list.Items.Clear();
            if (logState == PingWatch.LogState.Ours && logReadable)
            {
                foreach (var s in sources)
                    list.Items.Add(new ListViewItem(new[]
                    {
                        s.From, s.Where, s.Allowed ? L.T("risposto", "answered") : L.T("bloccato", "blocked"),
                        s.Count.ToString(), s.First.ToString("g"), s.Last.ToString("g")
                    }) { ForeColor = s.Allowed ? list.ForeColor : Color.FromArgb(120, 120, 120) });
                lblEmpty.Text = L.T("Nessun ping nel registro", "No pings in the log");
            }
            else
            {
                foreach (var e in counted.OrderByDescending(x => x.Time))
                    list.Items.Add(new ListViewItem(new[] { L.T("(non noto)", "(unknown)"), "", L.T("risposto", "answered"), e.Count.ToString(), e.Time.ToString("g"), e.Time.ToString("g") }));
                lblEmpty.Text = L.T("Nessun ping da quando CorbolIp è aperta", "No pings since CorbolIp was opened");
            }
            list.EndUpdate();
            lblEmpty.Visible = list.Items.Count == 0;
            if (lblEmpty.Visible) lblEmpty.Height = Math.Max(40, list.ClientSize.Height - 40);
            FitColumns();
        }

        void ToggleLog()
        {
            bool on = logState != PingWatch.LogState.Ours;
            if (on && MessageBox.Show(this,
                    L.F("Per sapere chi fa ping, CorbolIp accende il registro del firewall di Windows: Windows scriverà le connessioni lasciate entrare e quelle bloccate in {0} (al massimo {1} MB, poi ricomincia). Lo legge solo il tuo utente e gli amministratori.\n\nServe la conferma di amministratore. Lo spegni quando vuoi da questa finestra.",
                        "To know who pings, CorbolIp turns on the Windows firewall log: Windows will write the connections it lets in and the ones it blocks to {0} (at most {1} MB, then it starts over). Only your user and administrators can read it.\n\nAdministrator confirmation is needed. Turn it off any time from this window.",
                        PingWatch.LogDir, 16 * 2),
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            btnLog.Enabled = btnRefresh.Enabled = false;
            var t = new Thread(delegate ()
            {
                bool ok = PingWatch.SetLoggingElevated(on);
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (!ok) MessageBox.Show(this, L.T("Non è stato possibile cambiare il registro del firewall (conferma annullata o permessi negati).",
                                                           "The firewall log could not be changed (confirmation cancelled or permission denied)."),
                                                 Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Reload();
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        string ListText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(L.F("Ping ricevuti (CorbolIp {0}) - {1}", "Pings received (CorbolIp {0}) - {1}", AppVersion.Text, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
            sb.AppendLine(lblStatus.Text);
            sb.AppendLine();
            foreach (ListViewItem it in list.Items)
                sb.AppendLine(string.Join(" - ", it.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).Where(s => s.Length > 0)));
            return sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
    }
}

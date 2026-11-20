// Conversazione con l'assistente AI dentro una finestra (diagnosi rete, storico della rete).
// La finestra fornisce i dati (primo messaggio), le istruzioni e l'anonimizzazione; qui ci sono i pulsanti
// Spiega / Prepara segnalazione / Nuova conversazione, la casella Chiedi, lo streaming della risposta,
// i controlli che l'assistente esegue e "Cosa viene inviato".
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CorbolIp
{
    abstract class AiChatForm : Form
    {
        protected enum AiKind { Explain, Ticket, Question }

        protected bool ai;   // assistente acceso quando la finestra è stata costruita
        protected Button btnExplain, btnTicket, btnNew, btnAsk;
        protected TextBox txtAi, txtQuestion;
        protected LinkLabel lnkSent;
        protected Label lblService;   // servizio e account in uso, sempre visibile sopra il riquadro delle risposte
        protected CancellationTokenSource aiCts;
        string lastSent;                                            // ultimo testo inviato
        // conversazione con l'assistente: si azzera con Nuova conversazione o quando cambiano i dati
        readonly List<AiMessage> convo = new List<AiMessage>();     // messaggi inviati (anonimizzati se richiesto)
        readonly StringBuilder transcript = new StringBuilder();    // testo del riquadro (in chiaro)
        protected readonly StringBuilder chatLog = new StringBuilder(); // spiegazioni e risposte per Copia report
        protected string ticketText;                                // ultima segnalazione: Copia report la mette in cima
        Anonymizer anon;

        // ---------- da fornire nella finestra ----------
        protected abstract string AiData();                         // dati per il primo messaggio (in chiaro)
        protected abstract string DataIntro { get; }                // es. "Network diagnosis of my PC:"
        protected abstract string SystemPrompt(bool tools);
        protected abstract string ExplainRequest { get; }
        protected abstract string ExplainHeader { get; }            // titolo nel riquadro
        protected abstract string ExplainLogTitle { get; }          // titolo in Copia report
        protected abstract string ReportName { get; }               // es. "the full diagnostic report"
        protected abstract Anonymizer BuildAnon();
        protected virtual bool HostBusy { get { return false; } }  // es. diagnosi in corso
        protected virtual void SetHostEnabled(bool on) { }
        protected virtual void OnTicketReady() { }

        // Crea pulsanti, riquadro (alto boxH) e casella delle domande da 'top' (coordinate a 9 pt); restituisce la y successiva
        protected int AddAiControls(int top, int width, string explainText, int explainW, int boxH = 160)
        {
            btnExplain = new Button { Text = explainText, Size = new Size(explainW, 30), Location = new Point(16, top), Enabled = false };
            btnTicket = new Button { Text = L.T("Prepara segnalazione", "Write support request"), Size = new Size(170, 30), Location = new Point(24 + explainW, top), Enabled = false };
            btnNew = new Button { Text = L.T("Nuova conversazione", "New conversation"), Size = new Size(160, 30), Location = new Point(202 + explainW, top) };
            // riga con il servizio e l'account: il riquadro delle risposte si accorcia di quanto serve, la finestra non cambia
            lblService = new Label { AutoSize = false, AutoEllipsis = true, Location = new Point(16, top + 37), Size = new Size(width, 18), ForeColor = Color.DimGray };
            txtAi = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
                Location = new Point(16, top + 57), Size = new Size(width, boxH - 19), ForeColor = Color.DimGray
            };
            txtQuestion = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Location = new Point(16, top + 46 + boxH), Size = new Size(width - 96, 52) };
            btnAsk = new Button { Text = L.T("Chiedi", "Ask"), Size = new Size(90, 52), Location = new Point(16 + width - 90, top + 46 + boxH), Enabled = false };
            lnkSent = new LinkLabel { Text = L.T("Cosa viene inviato", "What is sent"), AutoSize = false, Size = new Size(170, 24), LinkBehavior = LinkBehavior.AlwaysUnderline };
            btnExplain.Click += delegate { RunAi(AiKind.Explain, null, btnExplain); };
            btnTicket.Click += delegate { RunAi(AiKind.Ticket, null, btnTicket); };
            btnAsk.Click += delegate { if (aiCts != null) aiCts.Cancel(); else Ask(); };
            btnNew.Click += delegate { if (aiCts == null && !HostBusy) ResetConversation(true); };
            txtQuestion.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Ask(); }   // Maiusc+Invio va a capo
            };
            lnkSent.LinkClicked += delegate { ShowSent(); };
            Controls.AddRange(new Control[] { btnExplain, btnTicket, btnNew, lblService, txtAi, txtQuestion, btnAsk, lnkSent });
            UpdateService();
            Activated += delegate { UpdateService(); };   // per esempio dopo un cambio di account nelle impostazioni
            return top + 110 + boxH;
        }

        protected void UpdateService()
        {
            if (lblService == null) return;
            var p = AiSettings.CurrentProvider();
            string s = p != null ? ClaudeCodeProvider.DisplayNameOf(p) : L.T("non configurato", "not set up");
            lblService.Text = L.F("Servizio: {0}", "Service: {0}", s);
        }

        protected void SetAiButtons(bool on)
        {
            if (!ai) return;
            btnExplain.Enabled = btnTicket.Enabled = btnAsk.Enabled = on;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (aiCts != null) aiCts.Cancel();
            base.OnFormClosing(e);
        }

        protected void Ui(Action a)
        {
            try { if (!IsDisposed) BeginInvoke(a); } catch { }
        }

        protected static float ScreenDpi()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX; } catch { return 96f; }
        }

        protected static string Crlf(string s)
        {
            return Regex.Replace(s ?? "", "\r?\n", "\r\n");
        }

        protected void ShowAi(string text, bool hint = false)
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

        protected static string LangName()
        {
            try { return CultureInfo.GetCultureInfo(L.Code).EnglishName; } catch { return "English"; }
        }

        protected const string Common =
            "Words in square brackets in the data (for example [WIFI] or [PUBLIC_IP]) replace private data: copy them exactly when you need " +
            "them and never use one that does not appear in the data. Do not mention the computer, user or network name unless it helps the user. " +
            "Rely only on the data provided and never invent values; if the data is not " +
            "enough to be sure, say so and suggest what to check. Write plain text for a small window: no Markdown headings, bold or tables; " +
            "use short paragraphs and lists with \"- \". Never say that you checked or tested something unless its result is in the data.";

        protected const string ToolsHelp =
            "You can run extra network checks with the tools (at most " + "5" + " in total) when they help to find or confirm the cause, " +
            "for example a ping or traceroute when the router or the Internet does not answer, a DNS lookup or http_check for a site the " +
            "user mentions, tcp_port for a service, recent_events to see whether the connection dropped earlier. If the user mentions a " +
            "specific site, service or address, check it with the tools before answering. Otherwise do not run checks when the results " +
            "are already clear. After the checks, write only the final answer for the user and mention what the extra checks showed.";

        string TicketRequest(string note)
        {
            return "Write a support request that I will send to the IT help desk. Start with a one-line subject, then describe the problem " +
                   "from my point of view" + (note.Length > 0 ? " (my description: " + note + ")" : "") + ", taking into account what we " +
                   "discussed, what the data shows and the most likely cause. If the history in the data shows since when or how often the " +
                   "problem happens (for example always on the same network or at the same time of day), say so in one or two sentences. " +
                   "Keep it under about 150 words and do not list all the technical details: " + ReportName + " is attached automatically " +
                   "below your text. Write only the request, ready to send.";
        }

        // L'assistente locale (modello piccolo) non usa gli strumenti: nelle prove diceva di aver controllato senza farlo.
        // I siti citati dall'utente li controlla comunque CorbolIp (vedi RunAi).
        // Gli strumenti passano solo dalle API (Claude, compatibili OpenAI); Claude Code parte senza strumenti.
        protected static bool UsesTools(IAiProvider prov) { return prov is AiHttpProvider; }

        // Primo messaggio della conversazione: i dati e la prima richiesta (in chiaro)
        string FirstMessage(string request)
        {
            return DataIntro + "\n\n" + AiData() + "\n\n" + request;
        }

        protected void ResetConversation(bool showHint)
        {
            convo.Clear();
            transcript.Length = 0;
            chatLog.Length = 0;
            ticketText = null;
            lastSent = null;
            anon = null;
            if (showHint && ai)
                ShowAi(L.F("Nuova conversazione: premi {0} o scrivi una domanda.", "New conversation: press {0} or type a question.", btnExplain.Text), true);
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
            if (HostBusy) return;
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

            string header = kind == AiKind.Explain ? "» " + ExplainHeader
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
            btnNew.Enabled = false;
            SetHostEnabled(false);
            ShowAi(head + (prov == LocalAi.Instance && !LocalAi.Instance.Running
                ? L.T("Avvio dell'assistente locale: la prima volta può richiedere fino a un minuto...", "Starting the local assistant: the first time it can take up to a minute...")
                : L.F("{0} sta scrivendo...", "{0} is writing...", ClaudeCodeProvider.DisplayNameOf(prov))));

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
                    string extra = "\n\nChecks that CorbolIp ran just now on what the user mentioned:\n" + pre;
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
                    btnExplain.Enabled = btnTicket.Enabled = btnAsk.Enabled = btnNew.Enabled = true;
                    SetHostEnabled(true);
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
                        OnTicketReady();
                    }
                    else
                    {
                        if (kind == AiKind.Question) chatLog.Append(L.T("Domanda", "Question") + ": " + note + "\n");
                        chatLog.Append(kind == AiKind.Explain ? ExplainLogTitle : L.T("Risposta dell'assistente", "Assistant's answer"))
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

        // Mostra il testo esatto inviato (o quello che invierebbe il primo pulsante)
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
    }
}

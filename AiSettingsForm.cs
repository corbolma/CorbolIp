// Impostazioni dell'assistente AI: servizio (assistente locale, Claude o compatibile OpenAI), modello,
// download dell'assistente locale, anonimizzazione dei dati e prova della connessione.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace AppIP
{
    class AiSettingsForm : Form
    {
        static AiSettingsForm open;

        public static void ShowOnce(Icon icon, Action changed)
        {
            if (open == null || open.IsDisposed) open = new AiSettingsForm(icon, changed);
            open.Show();
            open.WindowState = FormWindowState.Normal;
            open.Activate();
        }

        class Item
        {
            public string Id, Text;
            public Item(string id, string text) { Id = id; Text = text; }
            public override string ToString() { return Text; }
        }

        readonly Action changed;
        readonly CheckBox chkOn, chkAnon, chkGpu;
        readonly RadioButton rLocal, rClaude, rOpenAi;
        readonly TextBox txtClaudeKey, txtOaKey;
        readonly ComboBox cbLocalModel, cbClaudeModel, cbEffort, cbUrl, cbOaModel;
        readonly Button btnList, btnTest, btnDownload;
        readonly ProgressBar bar;
        readonly LinkLabel lnkRemove;
        readonly Label lblTest, lblLocal;
        readonly List<Control> localCtl = new List<Control>(), claudeCtl = new List<Control>(), oaCtl = new List<Control>();
        readonly AiDownloader downloader = new AiDownloader();
        readonly Dictionary<string, string> snapshot; // impostazioni all'apertura: si ripristinano con Annulla
        bool saved;
        CancellationTokenSource testCts;

        static readonly Color Grey = Color.DimGray, ErrColor = Color.FromArgb(190, 40, 40), OkColor = Color.FromArgb(30, 130, 60);

        AiSettingsForm(Icon icon, Action changed)
        {
            this.changed = changed;
            snapshot = new Dictionary<string, string>(Settings.Values);
            Text = L.T("Assistente AI - AppIP", "AI assistant - AppIP");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", UiFont.Pt);
            BackColor = Color.White;
            if (icon != null) Icon = icon;

            const int L0 = 16, L1 = 150, W = 330;
            int y = 14;
            chkOn = new CheckBox { Text = L.T("Usa l'assistente AI nella diagnosi rete", "Use the AI assistant in network diagnostics"), AutoSize = true, Location = new Point(L0, y) };
            y += 26;
            Controls.Add(Note(L.T("Spiega i risultati della diagnosi e prepara le segnalazioni per l'assistenza. I dati vengono inviati solo quando premi un pulsante.",
                                  "Explains the diagnostics and writes support requests. Data is sent only when you press a button."), L0, y, 470, 46));
            y += 54;

            // ---- assistente locale ----
            rLocal = new RadioButton { Text = L.T("Assistente locale sul PC (Qwen): nessuna chiave, i dati non escono", "Local assistant on this PC (Qwen): no key, data stays here"), AutoSize = true, Location = new Point(L0, y) };
            y += 26;
            cbLocalModel = new ComboBox { Location = new Point(L1, y), Width = W, DropDownStyle = ComboBoxStyle.DropDownList };
            var rec = AiCatalog.Recommended();
            foreach (var m in AiCatalog.Models)
                cbLocalModel.Items.Add(new Item(m.Id, m.Name + " · " + AiDownloader.FormatBytes(m.Size) + (m == rec ? " · " + L.T("consigliato", "recommended") : "")));
            Row(localCtl, L.T("Modello:", "Model:"), cbLocalModel, L0 + 18, y);
            y += 30;
            chkGpu = new CheckBox { Text = L.T("Usa la scheda video (GPU)", "Use the graphics card (GPU)"), AutoSize = true, Location = new Point(L1, y) };
            localCtl.Add(chkGpu);
            y += 26;
            lblLocal = Note("", L1, y, W, 40);
            localCtl.Add(lblLocal);
            y += 42;
            btnDownload = new Button { Text = L.T("Scarica", "Download"), Location = new Point(L1, y), Size = new Size(110, 28) };
            bar = new ProgressBar { Location = new Point(L1 + 118, y + 9), Size = new Size(W - 118, 10), Maximum = 1000, Visible = false };
            btnDownload.Click += delegate { if (downloader.Busy) downloader.Cancel(); else StartDownload(); };
            localCtl.Add(btnDownload);
            localCtl.Add(bar);
            y += 32;
            lnkRemove = new LinkLabel { Text = "", AutoSize = false, Size = new Size(W, 24), Location = new Point(L1, y), LinkBehavior = LinkBehavior.HoverUnderline };
            lnkRemove.LinkClicked += delegate { RemoveFiles(); };
            localCtl.Add(lnkRemove);
            y += 32;
            downloader.Progress += st => Post(delegate { OnProgress(st); });
            cbLocalModel.SelectedIndexChanged += delegate { UpdateLocal(); };
            chkGpu.CheckedChanged += delegate { UpdateLocal(); };

            // ---- Claude ----
            rClaude = new RadioButton { Text = L.T("Claude (Anthropic): serve una chiave API", "Claude (Anthropic): needs an API key"), AutoSize = true, Location = new Point(L0, y) };
            y += 26;
            txtClaudeKey = new TextBox { Location = new Point(L1, y), Width = W, UseSystemPasswordChar = true };
            Row(claudeCtl, L.T("Chiave API:", "API key:"), txtClaudeKey, L0 + 18, y);
            y += 26;
            var lnkKey = new LinkLabel { Text = L.T("Crea una chiave su console.anthropic.com", "Create a key at console.anthropic.com"), AutoSize = false, Size = new Size(W, 24), Location = new Point(L1, y), LinkBehavior = LinkBehavior.HoverUnderline };
            lnkKey.LinkClicked += delegate { try { System.Diagnostics.Process.Start("https://console.anthropic.com/settings/keys"); } catch { } };
            claudeCtl.Add(lnkKey);
            y += 26;
            cbClaudeModel = new ComboBox { Location = new Point(L1, y), Width = W, DropDownStyle = ComboBoxStyle.DropDown };
            cbClaudeModel.Items.AddRange(ClaudeProvider.Models);
            Row(claudeCtl, L.T("Modello:", "Model:"), cbClaudeModel, L0 + 18, y);
            y += 30;
            cbEffort = new ComboBox { Location = new Point(L1, y), Width = W, DropDownStyle = ComboBoxStyle.DropDownList };
            cbEffort.Items.AddRange(new object[]
            {
                new Item("low", L.T("Basso (più rapido ed economico)", "Low (faster, cheaper)")),
                new Item("medium", L.T("Medio", "Medium")),
                new Item("high", L.T("Alto", "High"))
            });
            Row(claudeCtl, L.T("Impegno:", "Effort:"), cbEffort, L0 + 18, y);
            y += 36;

            // ---- compatibile OpenAI ----
            rOpenAi = new RadioButton { Text = L.T("Servizio compatibile OpenAI (OpenAI, Ollama, LM Studio...)", "OpenAI-compatible service (OpenAI, Ollama, LM Studio...)"), AutoSize = true, Location = new Point(L0, y) };
            y += 26;
            cbUrl = new ComboBox { Location = new Point(L1, y), Width = W, DropDownStyle = ComboBoxStyle.DropDown };
            cbUrl.Items.AddRange(new object[] { "https://api.openai.com/v1", "https://openrouter.ai/api/v1", "http://localhost:11434/v1", "http://localhost:1234/v1" });
            Row(oaCtl, L.T("Indirizzo:", "Address:"), cbUrl, L0 + 18, y);
            y += 30;
            txtOaKey = new TextBox { Location = new Point(L1, y), Width = W, UseSystemPasswordChar = true };
            Row(oaCtl, L.T("Chiave API:", "API key:"), txtOaKey, L0 + 18, y);
            y += 30;
            cbOaModel = new ComboBox { Location = new Point(L1, y), Width = W - 96, DropDownStyle = ComboBoxStyle.DropDown };
            btnList = new Button { Text = L.T("Elenca", "List"), Location = new Point(L1 + W - 90, y - 1), Size = new Size(90, 26) };
            btnList.Click += delegate { ListModels(); };
            Row(oaCtl, L.T("Modello:", "Model:"), cbOaModel, L0 + 18, y);
            oaCtl.Add(btnList);
            y += 36;

            chkAnon = new CheckBox
            {
                Text = L.T("Nascondi nome del PC, dominio, utente, rete Wi-Fi, MAC e IP pubblico prima dell'invio",
                           "Hide computer name, domain, user, Wi-Fi network, MAC and public IP before sending"),
                AutoSize = false, Size = new Size(470, 50), Location = new Point(L0, y), CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft
            };
            y += 58;

            btnTest = new Button { Text = L.T("Prova connessione", "Test connection"), Location = new Point(L0, y), Size = new Size(150, 30) };
            btnTest.Click += delegate { Test(); };
            lblTest = new Label { AutoSize = false, Location = new Point(L0 + 158, y + 1), Size = new Size(470 - 158, 46), ForeColor = Grey };
            y += 54;
            var btnOk = new Button { Text = "OK", Location = new Point(L0 + 470 - 196, y), Size = new Size(96, 30) };
            var btnCancel = new Button { Text = L.T("Annulla", "Cancel"), Location = new Point(L0 + 470 - 96, y), Size = new Size(96, 30) };
            btnOk.Click += delegate { string err = CheckFields(); if (err != null) { ShowTest(err, ErrColor); return; } SaveAll(); Close(); };
            btnCancel.Click += delegate { Close(); };
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.AddRange(new Control[] { chkOn, rLocal, rClaude, rOpenAi, chkAnon, btnTest, lblTest, btnOk, btnCancel });
            Controls.AddRange(localCtl.ToArray());
            Controls.AddRange(claudeCtl.ToArray());
            Controls.AddRange(oaCtl.ToArray());
            ClientSize = new Size(L0 * 2 + 470, y + 44);

            // valori salvati
            chkOn.Checked = AiSettings.Enabled;
            chkAnon.Checked = AiSettings.Anonymize;
            string prov = AiSettings.Provider;
            rOpenAi.Checked = prov == AiSettings.OpenAi;
            rClaude.Checked = prov == AiSettings.Claude;
            rLocal.Checked = !rOpenAi.Checked && !rClaude.Checked;
            string lm = AiSettings.LocalModel;
            cbLocalModel.SelectedIndex = Math.Max(0, cbLocalModel.Items.Cast<Item>().ToList().FindIndex(i => i.Id == lm));
            chkGpu.Checked = AiSettings.LocalVariant == "vulkan";
            txtClaudeKey.Text = AiSettings.ClaudeKey;
            cbClaudeModel.Text = AiSettings.ClaudeModel;
            string eff = AiSettings.ClaudeEffort;
            cbEffort.SelectedIndex = Math.Max(0, cbEffort.Items.Cast<Item>().ToList().FindIndex(i => i.Id == eff));
            cbUrl.Text = AiSettings.OpenAiUrl;
            txtOaKey.Text = AiSettings.OpenAiKey;
            cbOaModel.Text = AiSettings.OpenAiModel;
            rLocal.CheckedChanged += delegate { UpdateEnabled(); };
            rClaude.CheckedChanged += delegate { UpdateEnabled(); };
            chkOn.CheckedChanged += delegate { UpdateEnabled(); };
            UpdateEnabled();
            UpdateLocal();
            Scale(new SizeF(UiFont.Scale, UiFont.Scale)); // coordinate pensate per 9 pt
        }

        void Row(List<Control> group, string label, Control input, int x, int y)
        {
            group.Add(new Label { Text = label, AutoSize = true, Location = new Point(x, y + 3) });
            group.Add(input);
        }

        static Label Note(string text, int x, int y, int w, int h)
        {
            return new Label { Text = text, AutoSize = false, Location = new Point(x, y), Size = new Size(w, h), ForeColor = Grey };
        }

        void UpdateEnabled()
        {
            bool on = chkOn.Checked;
            rLocal.Enabled = rClaude.Enabled = rOpenAi.Enabled = chkAnon.Enabled = btnTest.Enabled = on;
            foreach (var c in localCtl) c.Enabled = on && rLocal.Checked;
            foreach (var c in claudeCtl) c.Enabled = on && rClaude.Checked;
            foreach (var c in oaCtl) c.Enabled = on && rOpenAi.Checked;
            if (downloader.Busy) btnDownload.Enabled = true; // si può sempre interrompere
            else if (lblLocal != null) UpdateLocal();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (testCts != null) testCts.Cancel();
            // il download si ferma: il file a metà resta e la prossima volta riprende da lì
            if (downloader.Busy) downloader.Cancel();
            if (!saved)
            {
                // Annulla: le prove possono aver cambiato modello o variante solo in memoria
                Settings.Values.Clear();
                foreach (var kv in snapshot) Settings.Values[kv.Key] = kv.Value;
            }
            base.OnFormClosing(e);
        }

        // ---------- assistente locale ----------
        AiModel SelectedModel()
        {
            var it = cbLocalModel.SelectedItem as Item;
            return AiCatalog.Find(it != null ? it.Id : AiCatalog.Recommended().Id) ?? AiCatalog.Recommended();
        }

        string SelectedVariant() { return chkGpu.Checked ? "vulkan" : "cpu"; }

        void UpdateLocal()
        {
            if (downloader.Busy) return;
            var m = SelectedModel();
            string v = SelectedVariant();
            string mp = AiDownloader.ModelPath(m);
            bool llama = AiDownloader.LlamaReady(v);
            string why = AiCatalog.Unsuitable(m);
            bar.Visible = false;
            btnDownload.Text = L.T("Scarica", "Download");
            if (mp != null && llama)
            {
                lblLocal.Text = L.T("Pronto.", "Ready.") + (AiDownloader.FromAppPad(mp) ? " " + L.T("Usa i file già scaricati da AppPad.", "Uses the files already downloaded by AppPad.") : "");
                lblLocal.ForeColor = OkColor;
                btnDownload.Visible = false;
            }
            else
            {
                long need = (mp == null ? m.Size : 0) + (llama ? 0 : AiDownloader.LlamaSize(v));
                lblLocal.Text = why ?? L.F("Da scaricare: {0} (modello da Hugging Face, llama.cpp da GitHub). Si scarica una volta sola.",
                                           "To download: {0} (model from Hugging Face, llama.cpp from GitHub). Downloaded only once.", AiDownloader.FormatBytes(need));
                lblLocal.ForeColor = why != null ? ErrColor : Grey;
                btnDownload.Visible = true;
                btnDownload.Enabled = why == null && chkOn.Checked && rLocal.Checked;
            }
            long used = AiDownloader.UsedBytes();
            lnkRemove.Visible = used > (1L << 20); // non per il solo registro di llama-server
            lnkRemove.Text = L.F("Elimina i file scaricati da AppIP ({0})", "Delete the files downloaded by AppIP ({0})", AiDownloader.FormatBytes(used));
        }

        void StartDownload()
        {
            btnDownload.Text = L.T("Interrompi", "Stop");
            bar.Value = 0;
            bar.Visible = true;
            lblLocal.ForeColor = Grey;
            lblLocal.Text = L.T("Avvio dello scaricamento...", "Starting the download...");
            downloader.Start(SelectedModel(), SelectedVariant());
        }

        void OnProgress(AiDownloadStatus st)
        {
            if (IsDisposed) return;
            if (st.Finished)
            {
                UpdateLocal();
                if (st.Error != null) { lblLocal.Text = st.Error; lblLocal.ForeColor = ErrColor; }
                return;
            }
            string pct = st.Total > 0 ? " " + (st.Done * 100 / st.Total) + "% (" + AiDownloader.FormatBytes(st.Done) + " / " + AiDownloader.FormatBytes(st.Total) + ")" : "";
            lblLocal.Text = st.Text + pct;
            lblLocal.ForeColor = Grey;
            bar.Value = st.Total > 0 ? (int)Math.Min(1000, st.Done * 1000 / st.Total) : 0;
        }

        void RemoveFiles()
        {
            if (downloader.Busy) return;
            if (MessageBox.Show(this, L.T("Eliminare modello e llama-server scaricati da AppIP? Quelli di AppPad restano.", "Delete the model and llama-server downloaded by AppIP? AppPad's files stay."),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            LocalAi.Instance.Stop();
            AiDownloader.RemoveAll();
            UpdateLocal();
        }

        // ---------- salvataggio e prova ----------

        // Errore da mostrare, o null se i campi del servizio scelto vanno bene
        string CheckFields()
        {
            if (!chkOn.Checked || rLocal.Checked) return null;
            if (rClaude.Checked)
            {
                if (txtClaudeKey.Text.Trim().Length == 0) return L.T("Inserisci la chiave API di Claude.", "Enter the Claude API key.");
                if (cbClaudeModel.Text.Trim().Length == 0) return L.T("Scegli un modello.", "Choose a model.");
                return null;
            }
            Uri u;
            if (!Uri.TryCreate(cbUrl.Text.Trim(), UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))
                return L.T("L'indirizzo del servizio non è valido (esempio: https://api.openai.com/v1).", "The service address is not valid (example: https://api.openai.com/v1).");
            if (cbOaModel.Text.Trim().Length == 0) return L.T("Scegli un modello (il pulsante Elenca li mostra).", "Choose a model (the List button shows them).");
            return null;
        }

        IAiProvider FormProvider()
        {
            if (rLocal.Checked)
            {
                // l'assistente locale legge modello e variante dalle impostazioni (in memoria fino a OK)
                AiSettings.LocalModel = SelectedModel().Id;
                AiSettings.LocalVariant = SelectedVariant();
                return LocalAi.Instance;
            }
            if (rClaude.Checked)
                return new ClaudeProvider { ApiKey = txtClaudeKey.Text.Trim(), Model = cbClaudeModel.Text.Trim(), Effort = ((Item)cbEffort.SelectedItem).Id };
            string url = cbUrl.Text.Trim();
            return new OpenAiProvider { BaseUrl = url, ApiKey = txtOaKey.Text.Trim(), Model = cbOaModel.Text.Trim(), Local = AiSettings.IsLocalUrl(url) };
        }

        void SaveAll()
        {
            AiSettings.Enabled = chkOn.Checked;
            AiSettings.Anonymize = chkAnon.Checked;
            AiSettings.Provider = rOpenAi.Checked ? AiSettings.OpenAi : rClaude.Checked ? AiSettings.Claude : AiSettings.Local;
            AiSettings.LocalModel = SelectedModel().Id;
            AiSettings.LocalVariant = SelectedVariant();
            AiSettings.ClaudeKey = txtClaudeKey.Text.Trim();
            AiSettings.ClaudeModel = cbClaudeModel.Text.Trim().Length > 0 ? cbClaudeModel.Text.Trim() : ClaudeProvider.DefaultModel;
            AiSettings.ClaudeEffort = ((Item)cbEffort.SelectedItem).Id;
            AiSettings.OpenAiUrl = cbUrl.Text.Trim();
            AiSettings.OpenAiKey = txtOaKey.Text.Trim();
            AiSettings.OpenAiModel = cbOaModel.Text.Trim();
            if (!AiSettings.Enabled || AiSettings.Provider != AiSettings.Local) LocalAi.Instance.Stop(); // libera la memoria
            saved = true;
            Settings.Save();
            if (changed != null) changed();
        }

        void ShowTest(string text, Color c)
        {
            lblTest.Text = text;
            lblTest.ForeColor = c;
        }

        void Post(Action a)
        {
            try { if (!IsDisposed) BeginInvoke(a); } catch { }
        }

        void Test()
        {
            if (testCts != null) { testCts.Cancel(); return; }
            string err = CheckFields();
            if (err == null && !chkOn.Checked) err = L.T("Attiva prima l'assistente.", "Turn on the assistant first.");
            if (err == null && rLocal.Checked && downloader.Busy) err = L.T("Aspetta la fine dello scaricamento.", "Wait for the download to finish.");
            if (err != null) { ShowTest(err, ErrColor); return; }
            var prov = FormProvider();
            var cts = testCts = new CancellationTokenSource();
            btnTest.Text = L.T("Interrompi", "Stop");
            ShowTest(rLocal.Checked && !LocalAi.Instance.Running
                ? L.T("Avvio dell'assistente locale: la prima volta può richiedere fino a un minuto...", "Starting the local assistant: the first time it can take up to a minute...")
                : L.T("Prova in corso...", "Testing..."), Grey);
            var t = new Thread(delegate ()
            {
                var res = prov.Run(AiRequest.Simple(null, "Reply with just the word OK."), null, cts.Token);
                Post(delegate
                {
                    testCts = null;
                    btnTest.Text = L.T("Prova connessione", "Test connection");
                    if (res.Cancelled) ShowTest(L.T("Prova interrotta.", "Test stopped."), Grey);
                    else if (!res.Ok) ShowTest(res.Error, ErrColor);
                    else ShowTest(L.F("Funziona ({0}).", "It works ({0}).", prov == LocalAi.Instance ? SelectedModel().Name : res.Model ?? prov.Name), OkColor);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void ListModels()
        {
            Uri u;
            if (!Uri.TryCreate(cbUrl.Text.Trim(), UriKind.Absolute, out u)) { ShowTest(CheckFields() ?? "", ErrColor); return; }
            var prov = new OpenAiProvider { BaseUrl = cbUrl.Text.Trim(), ApiKey = txtOaKey.Text.Trim() };
            btnList.Enabled = false;
            ShowTest(L.T("Lettura dei modelli...", "Reading models..."), Grey);
            var t = new Thread(delegate ()
            {
                List<string> ids = null; string err = null;
                try { ids = prov.ListModels(); } catch (Exception ex) { err = ex.Message; }
                Post(delegate
                {
                    btnList.Enabled = true;
                    if (err != null) { ShowTest(err, ErrColor); return; }
                    string cur = cbOaModel.Text;
                    cbOaModel.Items.Clear();
                    cbOaModel.Items.AddRange(ids.Cast<object>().ToArray());
                    cbOaModel.Text = cur.Length > 0 ? cur : (ids.Count > 0 ? ids[0] : "");
                    ShowTest(L.F("{0} modelli disponibili.", "{0} models available.", ids.Count), ids.Count > 0 ? OkColor : Grey);
                    if (ids.Count > 0) cbOaModel.DroppedDown = true;
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}

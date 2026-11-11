// Assistente locale (ripreso da AppPad): Qwen3.5 con llama-server (llama.cpp) in un processo separato, sul PC.
// Niente chiave API e i dati non escono dal computer.
//   - Modello e llama-server si scaricano solo quando l'utente lo chiede, in %LOCALAPPDATA%\AppIP\ai;
//     se AppPad li ha già scaricati (%LOCALAPPDATA%\AppPad\ai) si usano quelli.
//   - Ogni file si controlla con il suo SHA-256; un download interrotto riprende da dove era arrivato.
//   - llama-server parte alla prima richiesta e si chiude dopo 5 minuti senza richieste (libera 2-4 GB di RAM).
//   - Ascolta solo su 127.0.0.1, su una porta libera scelta ogni volta, con una chiave casuale.
//   - Un "job object" di Windows lo chiude anche se AppIP si chiude male.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AppIP
{
    class AiModel
    {
        public string Id, Name, FileName, Url, Sha256, License;
        public long Size, MinRam;
    }

    // Modelli proposti per l'assistente locale. Solo licenze Apache 2.0 o MIT, scaricabili senza account.
    // Qwen3.5 (Alibaba) nella quantizzazione Q4_K_M di unsloth; l'URL punta a un commit preciso del repository,
    // così il file non può cambiare sotto i piedi e lo SHA-256 resta valido.
    static class AiCatalog
    {
        public const string Light = "qwen3.5-2b";
        public const string Better = "qwen3.5-4b";

        public static readonly AiModel[] Models =
        {
            new AiModel
            {
                Id = Light, Name = "Qwen3.5 2B", License = "Apache 2.0",
                FileName = "Qwen3.5-2B-Q4_K_M.gguf",
                Url = "https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Qwen3.5-2B-Q4_K_M.gguf",
                Size = 1280835840,
                Sha256 = "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
                MinRam = 8L << 30
            },
            new AiModel
            {
                Id = Better, Name = "Qwen3.5 4B", License = "Apache 2.0",
                FileName = "Qwen3.5-4B-Q4_K_M.gguf",
                Url = "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/e87f176479d0855a907a41277aca2f8ee7a09523/Qwen3.5-4B-Q4_K_M.gguf",
                Size = 2740937888,
                Sha256 = "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
                MinRam = 16L << 30
            }
        };

        public static AiModel Find(string id)
        {
            return Models.FirstOrDefault(m => m.Id == id);
        }

        // Il modello consigliato per questo PC: il migliore che la RAM regge
        public static AiModel Recommended()
        {
            long ram = TotalRam();
            return Models.LastOrDefault(m => ram <= 0 || ram >= m.MinRam * 15 / 16) ?? Models[0];
        }

        // Motivo per cui questo PC non è adatto al modello, o null. La RAM "da 16 GB" vista da Windows è un po' meno.
        public static string Unsuitable(AiModel m)
        {
            long ram = TotalRam();
            if (ram > 0 && ram < m.MinRam * 15 / 16)
                return L.F("RAM insufficiente: servono almeno {0} GB", "Not enough RAM: at least {0} GB needed", m.MinRam >> 30);
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        public static long TotalRam()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : 0;
        }
    }

    // Stato da mostrare nelle impostazioni
    class AiDownloadStatus
    {
        public string Text;          // cosa sta facendo, già tradotto
        public long Done, Total;     // byte, per la barra di avanzamento (Total = 0: indeterminato)
        public bool Finished;        // il lavoro è terminato (bene se Error == null)
        public string Error;         // motivo dell'errore, già tradotto
    }

    class AiDownloader
    {
        // Versione di llama.cpp provata con AppPad e AppIP, fissata con dimensione e SHA-256 dei pacchetti (presi dalla
        // pagina della release su GitHub). Un aggiornamento di llama.cpp non cambia nulla finché non si cambia qui.
        // Per aggiornare: https://github.com/ggml-org/llama.cpp/releases/tag/<tag> → asset "llama-<tag>-bin-win-<cpu|vulkan>-x64.zip".
        public const string LlamaTag = "b12110";
        class LlamaPackage { public string Variant; public long Size; public string Sha256; }
        static readonly LlamaPackage[] LlamaPackages =
        {
            new LlamaPackage { Variant = "cpu",    Size = 24708445, Sha256 = "98901e327c252875accaf2d1bf364e10e868843b0b63468baad1aa945cdc5da2" },
            new LlamaPackage { Variant = "vulkan", Size = 37661146, Sha256 = "9732a5e48887ca54d0bfe4af4324dc85fd56cd8cda19b52df3542fa6ebe6874f" }
        };

        const long MinFree = 2L << 30;     // spazio da lasciare libero sul disco dopo i download
        const int Retries = 5;             // tentativi per file, ognuno riprende da dove era arrivato

        // Cartella di AppIP (qui si scarica e si cancella) e cartella di AppPad (solo lettura: se AppPad ha già
        // scaricato modello e llama-server, AppIP li usa senza scaricarli di nuovo e non li cancella mai)
        public static string Root
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppIP", "ai"); }
        }
        public static string AppPadRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPad", "ai"); }
        }
        static string[] Roots { get { return new[] { Root, AppPadRoot }; } }
        public static string ModelsDir { get { return Path.Combine(Root, "models"); } }
        public static string LlamaDir { get { return Path.Combine(Root, "llama"); } }

        static string UserAgent { get { return "AppIP/" + AppVersion.Text; } }

        // Chiamato dal thread del download: chi aggiorna l'interfaccia deve usare BeginInvoke
        public event Action<AiDownloadStatus> Progress;

        Thread thread;
        volatile bool cancel;
        public bool Busy { get { return thread != null && thread.IsAlive; } }

        // ---- Cosa c'è già sul disco ----

        // Percorso del modello se scaricato e verificato, altrimenti null
        public static string ModelPath(AiModel m)
        {
            foreach (var root in Roots)
            {
                string f = Path.Combine(root, "models", m.FileName);
                try
                {
                    if (new FileInfo(f).Length == m.Size && File.ReadAllText(f + ".sha256").Trim() == m.Sha256) return f;
                }
                catch { }
            }
            return null;
        }

        // Il modello viene dalla cartella di AppPad
        public static bool FromAppPad(string path)
        {
            return path != null && path.StartsWith(AppPadRoot, StringComparison.OrdinalIgnoreCase);
        }

        // Percorso di llama-server.exe adatto alla variante ("cpu" o "vulkan"), da AppIP o da AppPad, o null.
        // Il pacchetto per GPU (Vulkan) contiene anche il motore per processore, quindi va bene pure per "cpu".
        public static string ServerExe(string variant)
        {
            foreach (var d in InstalledLlamaDirs())
            {
                string n = Path.GetFileName(d);
                int i = n.IndexOf('-');
                string v = i > 0 ? n.Substring(i + 1) : "";
                if (v != variant && !(variant == "cpu" && v == "vulkan")) continue;
                string exe = FindServer(d);
                if (exe != null) return exe;
            }
            return null;
        }

        public static bool LlamaReady(string variant) { return ServerExe(variant) != null; }

        // Cartelle llama\<tag>-<variante> complete di AppIP e di AppPad, la versione più recente per prima
        static IEnumerable<string> InstalledLlamaDirs()
        {
            var all = new List<string>();
            foreach (var root in Roots)
            {
                string dir = Path.Combine(root, "llama");
                try { if (Directory.Exists(dir)) all.AddRange(Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "ok.txt")))); }
                catch { }
            }
            return all.OrderByDescending(d => TagNumber(Path.GetFileName(d)));
        }

        static string FindServer(string dir)
        {
            try { return Directory.GetFiles(dir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault(); }
            catch { return null; }
        }

        static int TagNumber(string name)
        {
            var m = Regex.Match(name, "^b(\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        // Dimensione del pacchetto di llama.cpp da scaricare per la variante ("cpu" o "vulkan")
        public static long LlamaSize(string variant)
        {
            var p = LlamaPackages.FirstOrDefault(x => x.Variant == variant);
            return p != null ? p.Size : 0;
        }

        // Spazio occupato dai file scaricati da AppIP (quelli di AppPad non contano: sono suoi)
        public static long UsedBytes()
        {
            try
            {
                if (!Directory.Exists(Root)) return 0;
                return new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch { return 0; }
        }

        // ---- Pulizia ----

        // Cancella i file scaricati da AppIP (GB); quelli di AppPad restano. llama-server deve essere già chiuso.
        public static void RemoveAll()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }

        // Cancella i modelli del catalogo diversi da quello scelto (e i download a metà di quelli)
        public static void RemoveOtherModels(AiModel keep)
        {
            if (!Directory.Exists(ModelsDir)) return;
            foreach (var f in Directory.GetFiles(ModelsDir))
            {
                string n = Path.GetFileName(f);
                if (keep != null && n.StartsWith(keep.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
        }

        public static string FormatBytes(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0") + " MB";
            return Math.Max(1, b >> 10) + " KB";
        }

        // ---- Download ----

        // Scarica in background quel che manca: llama-server nella variante indicata ("cpu" o "vulkan") e,
        // se model != null, il modello. Se è già tutto a posto termina subito.
        public void Start(AiModel model, string variant)
        {
            if (Busy) return;
            cancel = false;
            thread = new Thread(delegate () { Run(model, variant); });
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Name = "AppIP AI download";
            thread.Start();
        }

        // Interrompe: il file a metà resta, il prossimo Start riprende da lì
        public void Cancel() { cancel = true; }

        void Report(string text, long done, long total)
        {
            var h = Progress;
            if (h != null) h(new AiDownloadStatus { Text = text, Done = done, Total = total });
        }

        void Run(AiModel model, string variant)
        {
            string error = null;
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                if (variant != "vulkan") variant = "cpu";
                if (model != null)
                {
                    string why = AiCatalog.Unsuitable(model);
                    if (why != null) throw new AiDownloadException(why);
                    RemoveOtherModels(model);
                }
                // Prima il modello (HuggingFace), poi llama.cpp (GitHub): se uno dei due siti non risponde,
                // intanto si porta a casa l'altro, che è il file più grande
                if (model != null && ModelPath(model) == null) DownloadModel(model);
                if (!LlamaReady(variant)) InstallLlama(variant);
            }
            catch (OperationCanceledException) { error = L.T("Scaricamento interrotto", "Download stopped"); }
            catch (AiDownloadException ex) { error = ex.Message; }
            catch (Exception ex) { error = ex.Message; }

            var h = Progress;
            if (h != null) h(new AiDownloadStatus
            {
                Finished = true, Error = error,
                Text = error ?? L.T("Assistente locale pronto", "Local assistant ready")
            });
        }

        void DownloadModel(AiModel m)
        {
            Directory.CreateDirectory(ModelsDir);
            string final = Path.Combine(ModelsDir, m.FileName);
            string part = final + ".part";
            try { File.Delete(final + ".sha256"); } catch { }
            if (File.Exists(final) && !File.Exists(part)) File.Move(final, part); // file presente ma non verificato: si ricontrolla
            string label = L.F("Scaricamento di {0}", "Downloading {0}", m.Name);
            Download(m.Url, part, m.Size, label);
            Verify(part, m.Sha256, m.Name);
            if (File.Exists(final)) File.Delete(final);
            File.Move(part, final);
            File.WriteAllText(final + ".sha256", m.Sha256);
        }

        // llama-server per Windows x64 dalla release ufficiale di llama.cpp indicata in LlamaTag
        void InstallLlama(string variant)
        {
            var pkg = LlamaPackages.First(p => p.Variant == variant);
            string tag = LlamaTag;
            string assetName = "llama-" + tag + "-bin-win-" + variant + "-x64.zip";
            string url = "https://github.com/ggml-org/llama.cpp/releases/download/" + tag + "/" + assetName;
            long size = pkg.Size;
            string sha = pkg.Sha256;

            Directory.CreateDirectory(LlamaDir);
            string zip = Path.Combine(LlamaDir, assetName + ".part");
            // Download a metà di un'altra release: non serve più
            foreach (var f in Directory.GetFiles(LlamaDir, "*.part"))
                if (!f.Equals(zip, StringComparison.OrdinalIgnoreCase)) try { File.Delete(f); } catch { }

            Download(url, zip, size, L.F("Scaricamento di llama.cpp {0}", "Downloading llama.cpp {0}", tag));
            Verify(zip, sha, "llama.cpp");

            Report(L.T("Estrazione di llama.cpp…", "Extracting llama.cpp…"), 0, 0);
            string dir = Path.Combine(LlamaDir, tag + "-" + variant);
            string tmp = dir + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp);
            if (FindServer(tmp) == null) throw new AiDownloadException(L.T("llama-server.exe non trovato nel pacchetto di llama.cpp", "llama-server.exe not found in the llama.cpp package"));
            File.WriteAllText(Path.Combine(tmp, "ok.txt"), assetName + "\r\nsha256:" + sha + "\r\n");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
            File.Delete(zip);

            // Le versioni precedenti non servono più (se una è ancora in uso resta lì fino alla prossima volta)
            foreach (var d in Directory.GetDirectories(LlamaDir))
                if (!d.Equals(dir, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(d, true); } catch { }
        }

        // Scarica url in part, riprendendo da quanto c'è già. Controlla prima lo spazio libero.
        void Download(string url, string part, long size, string label)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > size) { File.Delete(part); have = 0; }
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(part))).AvailableFreeSpace;
            if (free < size - have + MinFree)
                throw new AiDownloadException(L.F("Spazio insufficiente: servono {0} liberi sul disco",
                                                  "Not enough space: {0} free needed on the disk", FormatBytes(size - have + MinFree)));

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    DownloadOnce(url, part, size, label);
                    return;
                }
                catch (Exception ex)
                {
                    if (cancel || ex is AiDownloadException || attempt >= Retries) throw;
                    if (!(ex is WebException || ex is IOException)) throw;
                    Report(label + " — " + L.T("nuovo tentativo…", "retrying…"), File.Exists(part) ? new FileInfo(part).Length : 0, size);
                    for (int i = 0; i < 50 && !cancel; i++) Thread.Sleep(100);
                    if (cancel) throw new OperationCanceledException();
                }
            }
        }

        void DownloadOnce(string url, string part, long size, string label)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have == size) return;

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = 60000;   // GitHub a volte impiega decine di secondi a rispondere
            req.ReadWriteTimeout = 60000;
            req.AllowAutoRedirect = true; // HuggingFace e GitHub rimandano a una CDN
            if (have > 0) req.AddRange(have);

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                // 200 invece di 206: il server ricomincia da capo
                bool append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                if (!append) have = 0;
                using (var src = resp.GetResponseStream())
                using (var dst = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    var buf = new byte[1 << 20];
                    int lastTick = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancel) throw new OperationCanceledException();
                        if (have + n > size) throw new AiDownloadException(L.T("Il file scaricato è più grande del previsto", "The downloaded file is larger than expected"));
                        dst.Write(buf, 0, n);
                        have += n;
                        if (Environment.TickCount - lastTick > 250)
                        {
                            lastTick = Environment.TickCount;
                            Report(label, have, size);
                        }
                    }
                }
            }
            if (have != size) throw new IOException(L.T("Scaricamento incompleto", "Incomplete download"));
            Report(label, have, size);
        }

        // Confronta lo SHA-256 del file; se non torna lo cancella, così il prossimo tentativo riparte da zero
        void Verify(string file, string sha, string what)
        {
            long total = new FileInfo(file).Length, done = 0;
            string label = L.F("Verifica di {0}…", "Verifying {0}…", what);
            Report(label, 0, total);
            SHA256 h;
            try { h = new SHA256Cng(); } catch { h = SHA256.Create(); }
            using (h)
            using (var st = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var buf = new byte[1 << 20];
                int n, lastTick = 0;
                while ((n = st.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancel) throw new OperationCanceledException();
                    h.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (Environment.TickCount - lastTick > 250) { lastTick = Environment.TickCount; Report(label, done, total); }
                }
                h.TransformFinalBlock(buf, 0, 0);
                string got = BitConverter.ToString(h.Hash).Replace("-", "").ToLowerInvariant();
                if (got != sha)
                {
                    st.Close();
                    try { File.Delete(file); } catch { }
                    throw new AiDownloadException(L.F("{0}: il file scaricato non corrisponde a quello pubblicato (SHA-256 diverso)",
                                                      "{0}: the downloaded file does not match the published one (SHA-256 mismatch)", what));
                }
            }
        }
    }

    // Errore con un messaggio già pronto per l'utente: niente nuovi tentativi
    class AiDownloadException : Exception
    {
        public AiDownloadException(string msg) : base(msg) { }
    }

    class LocalAi : IAiProvider
    {
        public static readonly LocalAi Instance = new LocalAi();

        const int ContextTokens = 8192;             // domanda + risposta: la diagnosi è breve, meno memoria
        const int DefaultMaxTokens = 2048;
        const int StartTimeoutMs = 180000;          // caricamento del modello da disco lento
        public const int MaxCrashes = 2;
        public static int IdleMinutes = 5;

        readonly object gate = new object();
        Process proc;
        IntPtr job;
        int port;
        string key, modelPath, variant;
        volatile bool busy;
        DateTime lastUse;
        System.Threading.Timer idleTimer;
        readonly LinkedList<string> log = new LinkedList<string>();   // ultime righe di llama-server, per gli errori

        public string Name { get { return L.T("Assistente locale", "Local assistant"); } }
        public bool SendsTextOutside { get { return false; } }

        // Avvisi per l'interfaccia ("Avvio dell'assistente locale…", null = niente da dire); arrivano da altri thread
        public event Action<string> StatusChanged;

        public bool Running { get { lock (gate) return proc != null && !proc.HasExited; } }

        public static string LogPath { get { return Path.Combine(AiDownloader.Root, "llama-server.log"); } }

        // Chiusure improvvise durante una risposta, contate in settings.txt
        public static int Crashes
        {
            get { return Settings.Get("ai.local.crashes", 0); }
            set { Settings.Set("ai.local.crashes", value); Settings.Save(); }
        }

        // Il modello scelto nelle impostazioni: del catalogo (scaricato) o un .gguf dell'utente
        public static string ConfiguredModelPath()
        {
            string id = AiSettings.LocalModel;
            if (id.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return File.Exists(id) ? id : null;
            var m = AiCatalog.Find(id);
            return m != null ? AiDownloader.ModelPath(m) : null;
        }

        // Motivo per cui l'assistente locale non può partire, o null
        public static string NotReadyReason()
        {
            if (Crashes >= MaxCrashes)
                return L.F("Assistente locale spento: si è chiuso all'improvviso {0} volte. Riattivalo dalle impostazioni, magari con un modello più piccolo o senza GPU.",
                           "Local assistant turned off: it closed unexpectedly {0} times. Turn it back on in the settings, perhaps with a smaller model or without the GPU.", MaxCrashes);
            if (ConfiguredModelPath() == null || !AiDownloader.LlamaReady(AiSettings.LocalVariant == "auto" ? "cpu" : AiSettings.LocalVariant))
                return L.T("L'assistente locale non è ancora scaricato: completa il download dalle impostazioni.",
                           "The local assistant is not downloaded yet: finish the download in the settings.");
            return null;
        }

        // "auto": scheda video (Vulkan) se il pacchetto c'è, processore se la GPU ha già fatto cadere llama-server
        // in questa sessione. Sulla scheda integrata di un portatile la GPU scrive circa 3 volte più in fretta.
        volatile bool gpuFailed;

        static string ResolveVariant(string setting, bool gpuFailed)
        {
            if (setting != "auto") return setting;
            return !gpuFailed && AiDownloader.LlamaReady("vulkan") ? "vulkan" : "cpu";
        }

        // ---- Richiesta ----

        public AiResult Run(AiRequest req, Action<string> onText, CancellationToken cancel)
        {
            string why = NotReadyReason();
            if (why != null) return new AiResult { Error = why };
            if (req.MaxTokens <= 0) req.MaxTokens = DefaultMaxTokens;

            AiResult res;
            for (int attempt = 0; ; attempt++)
            {
                OpenAiProvider p;
                try { p = EnsureRunning(cancel, ResolveVariant(AiSettings.LocalVariant, gpuFailed)); }
                catch (OperationCanceledException) { return new AiResult { Cancelled = true }; }
                catch (Exception ex) { return new AiResult { Error = ex.Message }; }

                busy = true;
                lastUse = DateTime.Now;
                try { res = p.Run(req, onText, cancel); }
                finally { busy = false; lastUse = DateTime.Now; }
                // Chiuso all'improvviso (capita di rado, a volte per il driver della scheda video): si riavvia e si
                // rifà la richiesta una volta, sul processore se stava usando la GPU in modalità automatica.
                // OnRestart permette a chi mostra il testo di cancellare la risposta interrotta.
                if (attempt == 0 && !res.Cancelled && res.Error != null && !Running)
                {
                    if (variant == "vulkan" && AiSettings.LocalVariant == "auto") gpuFailed = true;
                    if (req.OnRestart != null) req.OnRestart();
                    continue;
                }
                break;
            }

            // Il processo è morto mentre rispondeva: crash (memoria finita, driver della GPU...)
            if (!res.Cancelled && res.Error != null && !Running)
            {
                int n = Crashes + 1;
                Crashes = n;
                res.Error = L.T("L'assistente locale si è chiuso all'improvviso.", "The local assistant closed unexpectedly.")
                          + (n >= MaxCrashes ? "\n" + NotReadyReason() : "") + LastLog();
            }
            else if (res.Ok && Crashes > 0) Crashes = 0;   // le chiusure contano solo se consecutive
            return res;
        }

        OpenAiProvider Client()
        {
            return new OpenAiProvider
            {
                BaseUrl = "http://127.0.0.1:" + port + "/v1",
                ApiKey = key,
                Model = "local",
                DisplayName = Name,
                Local = true,
                TimeoutMs = 300000,   // su CPU lente la prima parola può tardare
                Extra = new Dictionary<string, object>
                {
                    // Qwen3.5 "pensa" prima di rispondere: per spiegare una diagnosi non serve e rallenta
                    { "chat_template_kwargs", new Dictionary<string, object> { { "enable_thinking", false } } },
                    // Temperatura bassa e niente "presence penalty": risposte fedeli ai dati (valori provati in AppPad)
                    { "temperature", 0.2 }, { "top_p", 0.8 }, { "top_k", 20 }, { "min_p", 0.0 }, { "presence_penalty", 0.0 }
                }
            };
        }

        // ---- Avvio e arresto ----

        OpenAiProvider EnsureRunning(CancellationToken cancel, string forceVariant = null)
        {
            lock (gate)
            {
                string v = forceVariant ?? AiSettings.LocalVariant, exe = AiDownloader.ServerExe(v), model = ConfiguredModelPath();
                if (proc != null && !proc.HasExited && model == modelPath && v == variant) return Client();
                StopLocked();

                Status(L.T("Avvio dell'assistente locale…", "Starting the local assistant…"));
                port = FreePort();
                key = RandomKey();
                modelPath = model;
                variant = v;
                lock (log) log.Clear();

                var psi = new ProcessStartInfo(exe)
                {
                    Arguments = Args(model, v, port, key),
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => AddLog(e.Data);
                p.ErrorDataReceived += (s, e) => AddLog(e.Data);
                try { p.Start(); }
                catch (Exception ex) { throw new Exception(L.T("Impossibile avviare llama-server:", "Could not start llama-server:") + " " + ex.Message); }
                proc = p;
                AttachToJob(p);
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // il PC resta reattivo
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // Pronto quando /health risponde 200 (503 mentre carica il modello)
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    if (cancel.IsCancellationRequested) { StopLocked(); throw new OperationCanceledException(); }
                    if (p.HasExited)
                    {
                        proc = null;
                        throw new Exception(L.F("llama-server si è chiuso durante l'avvio (codice {0}).", "llama-server closed while starting (code {0}).", p.ExitCode) + LastLog());
                    }
                    if (Healthy()) break;
                    if (sw.ElapsedMilliseconds > StartTimeoutMs)
                    {
                        StopLocked();
                        throw new Exception(L.T("L'assistente locale non è partito in tempo.", "The local assistant did not start in time.") + LastLog());
                    }
                    cancel.WaitHandle.WaitOne(250);
                }
                lastUse = DateTime.Now;
                if (idleTimer == null) idleTimer = new System.Threading.Timer(delegate { StopIfIdle(); }, null, 30000, 30000);
                Status(null);
                return Client();
            }
        }

        static string Args(string model, string variant, int port, string key)
        {
            var a = new StringBuilder();
            a.Append("-m \"").Append(model).Append("\"");
            a.Append(" --host 127.0.0.1 --port ").Append(port).Append(" --api-key ").Append(key);
            a.Append(" -c ").Append(ContextTokens);
            a.Append(" --jinja");                                        // modello di chat del file .gguf (serve per enable_thinking)
            a.Append(" --reasoning off");                                // niente "ragionamento": qui rallenta e basta
            a.Append(" -np 1");                                          // una richiesta alla volta: tutto il contesto per lei
            a.Append(variant == "vulkan" ? " -ngl 999" : " -ngl 0");     // strati del modello sulla GPU
            int threads = Math.Max(1, Environment.ProcessorCount / 2);   // circa i core fisici; il resto agli altri programmi
            a.Append(" -t ").Append(threads);
            return a.ToString();
        }

        bool Healthy()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
                req.Timeout = 2000;
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + key;
                req.Proxy = null;
                using (var resp = (HttpWebResponse)req.GetResponse()) return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        // Chiude llama-server (per esempio prima di cancellare i file o cambiare modello)
        public void Stop()
        {
            lock (gate) StopLocked();
        }

        void StopLocked()
        {
            var p = proc;
            proc = null;
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(5000); } } catch { }
            try { p.Dispose(); } catch { }
            Status(null);
        }

        void StopIfIdle()
        {
            if (busy || !Monitor.TryEnter(gate)) return;
            try
            {
                if (proc != null && !busy && DateTime.Now - lastUse > TimeSpan.FromMinutes(IdleMinutes)) StopLocked();
            }
            finally { Monitor.Exit(gate); }
        }

        void Status(string s)
        {
            var h = StatusChanged;
            if (h != null) try { h(s); } catch { }
        }

        // ---- Registro di llama-server ----

        void AddLog(string line)
        {
            if (line == null) return;
            lock (log)
            {
                log.AddLast(line);
                while (log.Count > 200) log.RemoveFirst();
            }
        }

        // Ultime righe utili del registro, da aggiungere a un messaggio di errore; le salva anche su file
        string LastLog()
        {
            string[] lines;
            lock (log) { lines = new string[log.Count]; log.CopyTo(lines, 0); }
            try { Directory.CreateDirectory(AiDownloader.Root); File.WriteAllLines(LogPath, lines, new UTF8Encoding(false)); } catch { }
            var tail = new List<string>();
            for (int i = lines.Length - 1; i >= 0 && tail.Count < 4; i--)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || tail.Count == 0)
                    tail.Insert(0, l.Length > 200 ? l.Substring(0, 200) + "…" : l);
            }
            return tail.Count == 0 ? "" : "\n\n" + string.Join("\n", tail);
        }

        // ---- Utilità ----

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        static string RandomKey()
        {
            var b = new byte[24];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        // Job object con "chiudi i processi quando si chiude il job": l'handle lo tiene AppIP, quindi
        // se AppIP termina (anche in crash) Windows chiude llama-server
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        void AttachToJob(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }
    }
}

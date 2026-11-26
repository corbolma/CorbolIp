// CorbolLink: le app Corbol si parlano. FILE CONDIVISO: è identico in tutte le app (corbol3D, corbolFile, corbolImg, corbolIp,
// corbolMusic, corbolPad, corbolPrompt, corbolSup, corbolSys, corbolZ); quando lo cambi, copialo uguale in tutte.
//
// - Trova le altre app (cartella accanto, come nei sorgenti, oppure %LOCALAPPDATA%\Programs\<App>) e le avvia con file o cartelle.
// - Ogni app aperta ascolta su una named pipe ("CorbolLink.<App>.<utente>.<sessione>", solo per l'utente, non dalla rete):
//   le altre le mandano un comando (verbo + argomenti) e ricevono una risposta di testo.
// - Menu "Apri in": le app Corbol installate che sanno aprire i file scelti.
//   All'avvio: Link.Start(...), poi Link.Handle(...) per ogni comando, infine Link.Ready().
// - Assistente IA: Link.AiPrompt() spiega al modello le altre app; nella risposta il modello scrive
//   <corbol app="CorbolPad" verb="text" arg="Titolo">testo</corbol> e l'app, dopo la conferma dell'utente, esegue il comando.
//
// Protocollo (UTF-8, una riga per campo, \ \r \n scritti come \\ \r \n):
//   richiesta: "CORBOL/1", verbo, numero di argomenti, un argomento per riga
//   risposta:  "ok" oppure "err", poi il testo
// C# 5 (csc di .NET Framework 4): niente interpolazione di stringhe né operatori ?. .
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Corbol
{
    // Un'app della famiglia e cosa sa fare: questa tabella è il "contratto" che le altre (e l'IA) usano anche quando l'app è chiusa.
    public sealed class LinkApp
    {
        public string Name;     // "CorbolPad": nome della cartella e dell'exe
        public string It, En;   // a cosa serve, per i menu
        public string[] Ext;    // file che apre: estensioni (".txt"), "*" = qualsiasi file, "dir" = cartelle
        public string Verbs;    // comandi oltre a show / open, spiegati all'IA (in inglese)
    }

    public sealed class LinkAction
    {
        public string App, Verb;
        public List<string> Args = new List<string>();
        public string Raw;      // il tag com'era nella risposta (per toglierlo da una chat già scritta mentre arrivava)

        public string Describe()
        {
            string a = string.Join(", ", Args.Select(x => Link.Short(x, 80)).ToArray());
            return App + " → " + Verb + (a.Length > 0 ? " (" + a + ")" : "");
        }
    }

    public class LinkException : Exception
    {
        public LinkException(string msg) : base(msg) { }
    }

    public static class Link
    {
        public static readonly LinkApp[] Apps =
        {
            new LinkApp { Name = "Corbol3D", It = "Disegno 3D per stampanti", En = "3D design for printers", Ext = new[] { ".c3d" },
                Verbs = "open <.c3d project files>; ask <request>: sends a design request to the Corbol3D assistant (e.g. \"a hexagonal pen holder 10 cm tall\")." },
            new LinkApp { Name = "CorbolFile", It = "Gestione dei file", En = "File manager", Ext = new[] { "dir", "*" },
                Verbs = "open <folder or file>: shows that folder (and selects the file)." },
            new LinkApp { Name = "CorbolImg", It = "Immagini", En = "Images",
                Ext = new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif", "dir" },
                Verbs = "open <image files or a folder>; convert <image files or folders>: opens the converter with them." },
            new LinkApp { Name = "CorbolIp", It = "Rete e connessione", En = "Network and connection", Ext = new string[0],
                Verbs = "status: returns the current network status as text (addresses, connection, speed)." },
            new LinkApp { Name = "CorbolMusic", It = "Spartiti e musica", En = "Scores and music", Ext = new[] { ".abc" },
                Verbs = "open <.abc files>; abc <title> <ABC notation text>: opens that tune in a new tab." },
            new LinkApp { Name = "CorbolPad", It = "Editor di testo", En = "Text editor", Ext = new[] { "*" },
                Verbs = "open <text files>; text <title> <text>: opens a new tab with that text (good for long texts, code, notes)." },
            new LinkApp { Name = "CorbolPrompt", It = "Terminale", En = "Terminal", Ext = new[] { "dir" },
                Verbs = "open <folder>: new terminal tab in that folder; paste <command> [folder]: new tab with the command typed but NOT run (the user presses Enter)." },
            new LinkApp { Name = "CorbolSup", It = "Richieste di assistenza", En = "Support requests", Ext = new string[0],
                Verbs = "request <text>: opens a new support request with that text as the description (the user reviews and sends it)." },
            new LinkApp { Name = "CorbolSys", It = "Stato del PC", En = "PC health", Ext = new string[0],
                Verbs = "status: returns a short status of the PC (CPU, memory, disks, temperature, battery); report: returns a full analysis of the PC (problems, programs, disks, updates, security); it takes about 10 seconds." },
            new LinkApp { Name = "CorbolZ", It = "Ipotesi di Riemann", En = "Riemann Hypothesis", Ext = new string[0],
                Verbs = "ask <question>: sends a question to the CorbolZ mathematical assistant." },
        };

        public static string Self { get; private set; }
        public static Func<string, string, string> Tr = delegate (string it, string en) { return it; };

        public static LinkApp Find(string name)
        {
            return Apps.FirstOrDefault(a => a.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));
        }

        public static string Short(string s, int max)
        {
            s = (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        // ---------- dove sono le app ----------

        // Prima la cartella accanto a questa app (sorgenti: C:\...\corbolPad\CorbolPad.exe; installate: Programs\CorbolPad\CorbolPad.exe),
        // poi la cartella d'installazione standard.
        public static string AppsRoot;   // cartella che contiene le cartelle delle app (prove): se c'è, si guarda prima lì

        public static string FindExe(string app)
        {
            var la = Find(app);
            if (la == null) return null;
            var dirs = new List<string>();
            if (AppsRoot != null) dirs.Add(Path.Combine(AppsRoot, la.Name));
            try
            {
                string mine = Path.GetDirectoryName(Application.ExecutablePath);
                string parent = Path.GetDirectoryName(mine);
                if (parent != null) dirs.Add(Path.Combine(parent, la.Name));
            }
            catch { }
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", la.Name));
            foreach (var d in dirs)
            {
                string exe = Path.Combine(d, la.Name + ".exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        public static List<LinkApp> Installed()
        {
            return Apps.Where(a => !a.Name.Equals(Self, StringComparison.OrdinalIgnoreCase) && FindExe(a.Name) != null).ToList();
        }

        public static bool Opens(LinkApp a, string path)
        {
            if (Directory.Exists(path)) return a.Ext.Contains("dir");
            if (a.Ext.Contains("*")) return true;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return a.Ext.Contains(ext);
        }

        // ---------- server: questa app riceve i comandi ----------

        class Verb { public Func<List<string>, string> Run; public bool Ui; }
        static readonly Dictionary<string, Verb> verbs = new Dictionary<string, Verb>(StringComparer.OrdinalIgnoreCase);
        static Control marshal;
        static readonly ManualResetEvent ready = new ManualResetEvent(false);

        // L'app ha registrato i suoi comandi: da qui in poi le richieste si eseguono (prima aspettano, al massimo 20 secondi).
        public static void Ready() { ready.Set(); }

        public static string PipeName(string app)
        {
            return "CorbolLink." + app + "." + Environment.UserName + "." + Process.GetCurrentProcess().SessionId;
        }

        // Da chiamare nel thread dell'interfaccia, prima di Application.Run. tr: la funzione L.T dell'app.
        public static void Start(string self, Func<string, string, string> tr)
        {
            Self = self;
            if (tr != null) Tr = tr;
            marshal = new Control();
            marshal.CreateControl();
            var h = marshal.Handle; // finestra nascosta creata in questo thread: i comandi "Ui" girano qui
            Handle("ping", false, delegate (List<string> a) { return Self + " " + Application.ProductVersion; });
            Handle("verbs", false, delegate (List<string> a) { lock (verbs) return string.Join(" ", verbs.Keys.ToArray()); });
            Handle("show", delegate (List<string> a) { ShowMain(); return "ok"; });
            var t = new Thread(Listen) { IsBackground = true, Name = "CorbolLink" };
            t.Start();
        }

        // Un comando: Run riceve gli argomenti e restituisce il testo della risposta (un'eccezione diventa "err").
        // ui = true: gira nel thread dell'interfaccia (per toccare le finestre).
        public static void Handle(string verb, Func<List<string>, string> run) { Handle(verb, true, run); }
        public static void Handle(string verb, bool ui, Func<List<string>, string> run)
        {
            lock (verbs) verbs[verb] = new Verb { Run = run, Ui = ui };
        }

        // Porta in primo piano la finestra principale (la prima aperta e visibile).
        public static Form ShowMain()
        {
            Form f = Application.OpenForms.Cast<Form>().FirstOrDefault(x => x.Visible && !x.IsDisposed);
            if (f == null) return null;
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            f.Activate();
            f.BringToFront();
            return f;
        }

        static PipeSecurity Security()
        {
            var ps = new PipeSecurity();
            ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return ps;
        }

        static void Listen()
        {
            int fails = 0;
            while (fails < 20)
            {
                NamedPipeServerStream s = null;
                try
                {
                    s = new NamedPipeServerStream(PipeName(Self), PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, Security());
                    s.WaitForConnection();
                    fails = 0;
                    var conn = s;
                    s = null;
                    new Thread(delegate () { Serve(conn); }) { IsBackground = true, Name = "CorbolLink client" }.Start();
                }
                catch
                {
                    if (s != null) s.Dispose();
                    fails++;
                    Thread.Sleep(500);
                }
            }
        }

        static void Serve(NamedPipeServerStream s)
        {
            using (s)
            {
                try
                {
                    var r = new StreamReader(s, new UTF8Encoding(false));
                    var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };
                    if (r.ReadLine() != "CORBOL/1") return;
                    string verb = Unesc(r.ReadLine() ?? "");
                    int n;
                    int.TryParse(r.ReadLine(), out n);
                    var args = new List<string>();
                    for (int i = 0; i < n && i < 1000; i++) args.Add(Unesc(r.ReadLine() ?? ""));
                    ready.WaitOne(20000);
                    bool ok = true;
                    string res;
                    try { res = Dispatch(verb, args); }
                    catch (Exception ex) { ok = false; res = ex.Message; }
                    w.WriteLine(ok ? "ok" : "err");
                    w.WriteLine(Esc(res ?? ""));
                }
                catch { }
            }
        }

        static string Dispatch(string verb, List<string> args)
        {
            Verb v;
            lock (verbs) verbs.TryGetValue(verb, out v);
            if (v == null) throw new LinkException(Self + ": " + Tr("comando sconosciuto", "unknown command") + " \"" + verb + "\"");
            if (!v.Ui) return v.Run(args);
            string res = null;
            Exception err = null;
            marshal.Invoke((MethodInvoker)delegate
            {
                try { res = v.Run(args); }
                catch (Exception ex) { err = ex; }
            });
            if (err != null) throw new LinkException(err.Message);
            return res;
        }

        static string Esc(string s) { return s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n"); }

        static string Unesc(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char d = s[++i];
                    sb.Append(d == 'n' ? '\n' : d == 'r' ? '\r' : d);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------- client: questa app chiama le altre ----------

        [DllImport("kernel32.dll")] static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);

        public static bool IsRunning(string app)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName(app), PipeDirection.InOut)) { c.Connect(150); return true; }
            }
            catch { return false; }
        }

        // Manda un comando e aspetta la risposta. Se l'app è chiusa la avvia (launch) e aspetta che sia pronta.
        // Blocca il thread: dall'interfaccia usa CallAsync.
        public static string Call(string app, string verb, IList<string> args, int timeoutMs = 30000, bool launch = true)
        {
            var la = Find(app);
            if (la == null) throw new LinkException(Tr("App sconosciuta: ", "Unknown app: ") + app);
            var c = new NamedPipeClientStream(".", PipeName(la.Name), PipeDirection.InOut);
            try
            {
                try { c.Connect(250); }
                catch (TimeoutException)
                {
                    if (!launch) throw new LinkException(Tr(la.Name + " non è aperta", la.Name + " is not running"));
                    string exe = FindExe(la.Name);
                    if (exe == null) throw new LinkException(Tr(la.Name + " non è installata", la.Name + " is not installed"));
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        try { c.Connect(500); break; }
                        catch (TimeoutException)
                        {
                            if (sw.ElapsedMilliseconds > 20000)
                                throw new LinkException(Tr(la.Name + " non risponde: forse è una versione vecchia, senza collegamento con le altre app",
                                    la.Name + " does not answer: maybe an old version without the link to the other apps"));
                        }
                    }
                }
                uint pid;
                if (GetNamedPipeServerProcessId(c.SafePipeHandle.DangerousGetHandle(), out pid)) AllowSetForegroundWindow(pid);

                var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
                var r = new StreamReader(c, new UTF8Encoding(false));
                w.WriteLine("CORBOL/1");
                w.WriteLine(Esc(verb));
                w.WriteLine((args == null ? 0 : args.Count).ToString());
                if (args != null) foreach (var a in args) w.WriteLine(Esc(a ?? ""));

                string status = null, text = null;
                var reader = new Thread(delegate () { try { status = r.ReadLine(); text = r.ReadLine(); } catch { } }) { IsBackground = true };
                reader.Start();
                if (!reader.Join(timeoutMs)) throw new LinkException(Tr(la.Name + " non ha risposto in tempo", la.Name + " did not answer in time"));
                if (status == null) throw new LinkException(Tr(la.Name + ": collegamento interrotto", la.Name + ": connection lost"));
                text = Unesc(text ?? "");
                if (status != "ok") throw new LinkException(text);
                return text;
            }
            finally { c.Dispose(); }
        }

        // Come Call, in un thread a parte; done(risposta, errore) gira nel thread di owner.
        public static void CallAsync(Control owner, string app, string verb, IList<string> args, Action<string, Exception> done)
        {
            var list = args == null ? new List<string>() : args.ToList();
            new Thread(delegate ()
            {
                string res = null;
                Exception err = null;
                try { res = Call(app, verb, list); }
                catch (Exception ex) { err = ex; }
                if (done == null) return;
                try
                {
                    if (owner != null && owner.IsHandleCreated && !owner.IsDisposed) owner.BeginInvoke((MethodInvoker)delegate { done(res, err); });
                    else done(res, err);
                }
                catch { }
            }) { IsBackground = true, Name = "CorbolLink call" }.Start();
        }

        // Apre file o cartelle in un'altra app: nella finestra già aperta se c'è, se no avviandola con i percorsi.
        public static void Open(string app, IList<string> paths)
        {
            var la = Find(app);
            if (la == null) throw new LinkException(Tr("App sconosciuta: ", "Unknown app: ") + app);
            if (IsRunning(la.Name))
            {
                try { Call(la.Name, "open", paths, 15000, false); return; }
                catch (LinkException) { if (paths == null || paths.Count == 0) throw; }
            }
            string exe = FindExe(la.Name);
            if (exe == null) throw new LinkException(Tr(la.Name + " non è installata", la.Name + " is not installed"));
            string argline = paths == null ? "" : string.Join(" ", paths.Select(p => "\"" + p.TrimEnd('\\') + "\"").ToArray());
            Process.Start(new ProcessStartInfo(exe, argline) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        }

        // ---------- menu "Apri in" ----------

        // Sottomenu con le app Corbol installate. paths: i file o le cartelle da aprire (null o vuoto: avvia l'app o la porta davanti).
        public static ToolStripMenuItem OpenInMenu(Func<IList<string>> paths) { return OpenInMenu(null, paths); }
        public static ToolStripMenuItem OpenInMenu(string title, Func<IList<string>> paths)
        {
            var root = new ToolStripMenuItem(title ?? Tr("Apri in", "Open in"));
            root.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            root.DropDownOpening += delegate { Fill(root, paths); };
            return root;
        }

        static void Fill(ToolStripMenuItem root, Func<IList<string>> paths)
        {
            root.DropDownItems.Clear();
            IList<string> p = null;
            try { p = paths == null ? null : paths(); } catch { }
            var list = p == null ? new List<string>() : p.Where(x => !string.IsNullOrEmpty(x)).ToList();
            foreach (var a in Installed())
            {
                var app = a;
                bool can = list.Count == 0 || list.All(x => Opens(app, x));
                if (!can) continue;
                var it = new ToolStripMenuItem(app.Name + "  —  " + Tr(app.It, app.En));
                it.Click += delegate
                {
                    var owner = root.Owner != null ? root.Owner.FindForm() : null;
                    new Thread(delegate ()
                    {
                        try { Open(app.Name, list); }
                        catch (Exception ex)
                        {
                            if (owner != null && owner.IsHandleCreated)
                                owner.BeginInvoke((MethodInvoker)delegate { MessageBox.Show(owner, ex.Message, Self, MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                        }
                    }) { IsBackground = true }.Start();
                };
                root.DropDownItems.Add(it);
            }
            if (root.DropDownItems.Count == 0)
                root.DropDownItems.Add(new ToolStripMenuItem(list.Count == 0 ? Tr("Nessun'altra app Corbol installata", "No other Corbol app installed")
                    : Tr("Nessuna app Corbol apre questo file", "No Corbol app opens this file")) { Enabled = false });
        }

        // ---------- assistente IA ----------

        // Da aggiungere alle istruzioni del modello: le altre app installate e come chiederle qualcosa.
        public static string AiPrompt()
        {
            var apps = Installed();
            if (apps.Count == 0) return "";
            var sb = new StringBuilder();
            sb.Append("\n\nOTHER CORBOL APPS. Other apps of the Corbol family are installed on this PC and you can use them for the user. ");
            sb.Append("To do so, write a tag in your reply, on its own line: <corbol app=\"APP\" verb=\"VERB\" arg=\"FIRST ARGUMENT\" arg2=\"SECOND\">LAST ARGUMENT (long text)</corbol> ");
            sb.Append("or, without a long text, <corbol app=\"APP\" verb=\"VERB\" arg=\"...\"/>. Every app also has the verb show (brings it to the front) and, where listed, open with one or more paths (arg, arg2, ...). ");
            sb.Append("The tag is not shown to the user: the app asks the user to confirm, runs the command and shows the answer. Use a tag only when the user asks for it or when it clearly helps, ");
            sb.Append("say in plain words what you are doing, and never invent paths. Escape \" & < > in attributes as &quot; &amp; &lt; &gt;. Apps:\n");
            foreach (var a in apps) sb.Append("- ").Append(a.Name).Append(" (").Append(a.En).Append("): ").Append(a.Verbs).Append('\n');
            return sb.ToString();
        }

        static readonly Regex TagRx = new Regex("<corbol\\b([^>]*?)(?:/>|>(.*?)</corbol\\s*>)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        static readonly Regex AttrRx = new Regex("(\\w+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')");

        // Toglie i tag <corbol> dalla risposta del modello e li restituisce come azioni.
        public static List<LinkAction> TakeActions(string reply, out string clean)
        {
            var acts = new List<LinkAction>();
            if (string.IsNullOrEmpty(reply) || reply.IndexOf("<corbol", StringComparison.OrdinalIgnoreCase) < 0) { clean = reply; return acts; }
            clean = TagRx.Replace(reply, delegate (Match m)
            {
                var a = new LinkAction { Raw = m.Value };
                var named = new SortedDictionary<int, string>();
                foreach (Match am in AttrRx.Matches(m.Groups[1].Value))
                {
                    string k = am.Groups[1].Value.ToLowerInvariant();
                    string v = Html(am.Groups[2].Success ? am.Groups[2].Value : am.Groups[3].Value);
                    if (k == "app") a.App = v;
                    else if (k == "verb") a.Verb = v;
                    else if (k == "arg") named[1] = v;
                    else if (k.StartsWith("arg")) { int i; if (int.TryParse(k.Substring(3), out i)) named[i] = v; }
                }
                a.Args.AddRange(named.Values);
                if (m.Groups[2].Success)
                {
                    string body = m.Groups[2].Value;
                    if (body.StartsWith("\r\n")) body = body.Substring(2); else if (body.StartsWith("\n")) body = body.Substring(1);
                    a.Args.Add(Html(body.TrimEnd()));
                }
                var la = Find(a.App);
                if (la != null && !string.IsNullOrEmpty(a.Verb)) { a.App = la.Name; acts.Add(a); }
                return "";
            });
            clean = Regex.Replace(clean, "```[a-z]*\\s*```", "");          // blocchi di codice rimasti vuoti
            clean = Regex.Replace(clean, "(\r?\n){3,}", "\n\n").Trim();
            return acts;
        }

        // Toglie dal riquadro della chat i tag già comparsi mentre la risposta arrivava a pezzi
        public static void StripFrom(RichTextBox box, List<LinkAction> acts)
        {
            if (box == null || acts == null) return;
            bool ro = box.ReadOnly;
            foreach (var a in acts)
            {
                if (string.IsNullOrEmpty(a.Raw)) continue;
                string raw = a.Raw.Replace("\r\n", "\n");
                int i = box.Text.LastIndexOf(raw, StringComparison.Ordinal);
                if (i < 0) continue;
                box.ReadOnly = false;
                box.Select(i, raw.Length);
                box.SelectedText = "";
            }
            box.ReadOnly = ro;
            box.SelectionStart = box.TextLength;
        }

        static string Html(string s)
        {
            return s.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        }

        // Chiede conferma all'utente ed esegue le azioni una dopo l'altra. done(azione, risposta o errore, riuscita) per ognuna,
        // nel thread di owner; alla fine, se c'è, finished(testo da dare al modello con le risposte, null se l'utente ha rinunciato).
        public static void RunActions(Control owner, List<LinkAction> acts, Action<LinkAction, string, bool> done, Action<string> finished = null)
        {
            if (acts == null || acts.Count == 0) return;
            var msg = new StringBuilder(Tr("L'assistente vuole usare un'altra app Corbol:", "The assistant wants to use another Corbol app:")).Append("\n\n");
            foreach (var a in acts) msg.Append("• ").Append(a.Describe()).Append('\n');
            msg.Append('\n').Append(Tr("Procedo?", "Go ahead?"));
            var form = owner == null ? null : owner.FindForm();
            if (MessageBox.Show(form, msg.ToString(), Self, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                if (finished != null) finished(null);
                return;
            }
            new Thread(delegate ()
            {
                var summary = new StringBuilder();
                foreach (var a in acts)
                {
                    string res;
                    bool ok = true;
                    try
                    {
                        if (a.Verb.Equals("open", StringComparison.OrdinalIgnoreCase)) { Open(a.App, a.Args); res = "ok"; }
                        else res = Call(a.App, a.Verb, a.Args);
                    }
                    catch (Exception ex) { ok = false; res = ex.Message; }
                    summary.Append("[").Append(a.App).Append(' ').Append(a.Verb).Append(ok ? "" : " FAILED").Append("]\n").Append(Short2(res, 20000)).Append("\n\n");
                    var act = a; var r = res; var k = ok;
                    Post(owner, delegate { if (done != null) done(act, r, k); });
                }
                Post(owner, delegate { if (finished != null) finished(summary.ToString().Trim()); });
            }) { IsBackground = true, Name = "CorbolLink actions" }.Start();
        }

        static string Short2(string s, int max) { s = s ?? ""; return s.Length <= max ? s : s.Substring(0, max) + "\n[...]"; }

        static void Post(Control owner, MethodInvoker m)
        {
            try
            {
                if (owner != null && owner.IsHandleCreated && !owner.IsDisposed) owner.BeginInvoke(m);
                else m();
            }
            catch { }
        }

        // Testo breve per la chat: cosa è stato fatto.
        public static string Note(LinkAction a, string res, bool ok)
        {
            if (!ok) return "⚠ " + a.Describe() + ": " + res;
            if (string.IsNullOrEmpty(res) || res == "ok") return "✓ " + a.Describe();
            return "✓ " + a.Describe() + "\n" + Short2(res, 4000);
        }
    }
}

// Assistente tramite Claude Code installato sul PC, con l'abbonamento Claude dell'utente (niente chiave API).
// Solo per uso personale: l'abbonamento è dell'utente che ha fatto il login in Claude Code.
// CorbolIp avvia "claude -p" senza strumenti (non può leggere file né eseguire comandi), senza server MCP e
// senza salvare la sessione, in una cartella vuota; manda il testo su stdin e legge la risposta in streaming
// (--output-format stream-json: gli stessi eventi dell'API di Anthropic).
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CorbolIp
{
    class ClaudeCodeProvider : IAiProvider
    {
        public static readonly string[] Models = { "opus", "sonnet", "haiku" };
        public string Model = "";   // vuoto = il modello predefinito di Claude Code per l'account
        // Cartella di configurazione di Claude Code (CLAUDE_CONFIG_DIR), cioè l'account con cui gira:
        // vuoto = quella predefinita (%USERPROFILE%\.claude), o quella già impostata nell'ambiente
        public string ConfigDir = "";

        // Nome da mostrare nell'app, con l'account (email) con cui gira Claude Code.
        // Non va nei testi che possono finire in segnalazioni o rapporti: lì resta Name.
        public string DisplayName { get { return Name + " · " + (EmailOf(ConfigDir ?? "") ?? L.T("login non fatto", "not logged in")); } }
        public static string DisplayNameOf(IAiProvider p) { var c = p as ClaudeCodeProvider; return c != null ? c.DisplayName : p == null ? "" : p.Name; }
        public static string AccountEmail(string dir) { return EmailOf(dir ?? ""); }

        public string Name { get { return "Claude Code"; } }

        // ---------- account (una cartella di configurazione per ogni login) ----------

        static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }

        public class Account
        {
            public string Dir;      // "" = predefinito
            public string Email;    // null = login non ancora fatto
            public string Label
            {
                get
                {
                    string who = Email ?? L.T("login non fatto", "not logged in");
                    return Dir.Length == 0 ? L.F("Predefinito ({0})", "Default ({0})", who) : who + "  ·  " + Path.GetFileName(Dir.TrimEnd('\\'));
                }
            }
        }

        static readonly Dictionary<string, KeyValuePair<DateTime, string>> emailCache = new Dictionary<string, KeyValuePair<DateTime, string>>(StringComparer.OrdinalIgnoreCase);

        // Email dell'account registrato in una cartella di configurazione, o null
        static string EmailOf(string dir)
        {
            try
            {
                // predefinito: %USERPROFILE%\.claude.json; con CLAUDE_CONFIG_DIR: <cartella>\.claude.json
                string env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                string f = dir.Length > 0 ? Path.Combine(dir, ".claude.json")
                         : !string.IsNullOrEmpty(env) ? Path.Combine(env, ".claude.json") : Path.Combine(Home, ".claude.json");
                if (!File.Exists(f)) return null;
                // .claude.json può essere grande: si rilegge solo quando cambia
                var t = File.GetLastWriteTimeUtc(f);
                lock (emailCache)
                {
                    KeyValuePair<DateTime, string> c;
                    if (emailCache.TryGetValue(f, out c) && c.Key == t) return c.Value;
                }
                string email = Str(Json.DeserializeObject(File.ReadAllText(f, Encoding.UTF8)), "oauthAccount", "emailAddress");
                lock (emailCache) emailCache[f] = new KeyValuePair<DateTime, string>(t, email);
                return email;
            }
            catch { return null; }
        }

        // Il predefinito più le cartelle %USERPROFILE%\.claude-* (create con AddAccount o a mano)
        public static List<Account> Accounts()
        {
            var list = new List<Account> { new Account { Dir = "", Email = EmailOf("") } };
            try
            {
                foreach (var d in Directory.GetDirectories(Home, ".claude-*").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    list.Add(new Account { Dir = d, Email = EmailOf(d) });
            }
            catch { }
            return list;
        }

        // Crea la cartella %USERPROFILE%\.claude-<nome> e apre Claude Code in una finestra per fare il login
        // con l'altro account. Restituisce la cartella, o null con l'errore in error.
        public static string AddAccount(string name, out string error)
        {
            error = null;
            name = new string((name ?? "").Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            if (name.Length == 0) { error = L.T("Scrivi un nome per l'account (es. lavoro).", "Enter a name for the account (e.g. work)."); return null; }
            string exe = FindExe();
            if (exe == null) { error = L.T("Claude Code non è installato su questo PC.", "Claude Code is not installed on this PC."); return null; }
            string dir = Path.Combine(Home, ".claude-" + name);
            try
            {
                Directory.CreateDirectory(dir);
                OpenLogin(exe, dir);
                return dir;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        // Finestra del prompt dei comandi con Claude Code sull'account della cartella: al primo avvio chiede il login
        public static void OpenLogin(string exe, string dir)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/k \"\"" + exe + "\"\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Home
            };
            psi.EnvironmentVariables.Remove("CLAUDECODE");
            psi.EnvironmentVariables.Remove("CLAUDE_CODE_ENTRYPOINT");
            if (dir.Length > 0) psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = dir;
            Process.Start(psi);
        }
        public bool SendsTextOutside { get { return true; } }

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        // claude.exe (installazione nativa) o claude.cmd (installazione con npm), o null
        public static string FindExe()
        {
            var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Trim().Length > 0));
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
                foreach (var d in dirs)
                {
                    try
                    {
                        string f = Path.Combine(d.Trim().Trim('"'), name);
                        if (File.Exists(f)) return f;
                    }
                    catch { }
                }
            return null;
        }

        static object Get(object o, params string[] path)
        {
            foreach (var p in path)
            {
                var d = o as IDictionary<string, object>;
                object v;
                if (d == null || !d.TryGetValue(p, out v)) return null;
                o = v;
            }
            return o;
        }

        static string Str(object o, params string[] path) { return Get(o, path) as string; }

        public AiResult Run(AiRequest r, Action<string> onText, CancellationToken cancel)
        {
            var res = new AiResult();
            string exe = FindExe();
            if (exe == null)
            {
                res.Error = L.T("Claude Code non è installato su questo PC (comando claude non trovato).", "Claude Code is not installed on this PC (claude command not found).");
                return res;
            }
            // Cartella vuota tutta per CorbolIp: niente CLAUDE.md o impostazioni di progetto di altre cartelle
            string dir = Path.Combine(Path.GetTempPath(), "CorbolIp-claudecode");
            string sysFile = Path.Combine(dir, "system-" + Guid.NewGuid().ToString("N") + ".txt");
            var err = new StringBuilder();
            Process p = null;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(sysFile, r.System ?? "", new UTF8Encoding(false));
                // una sola richiesta: claude -p non ha la conversazione, i turni precedenti vanno scritti con chi parla
                string user = r.Messages.Count == 1 ? r.Messages[0].Text
                    : string.Join("\n\n", r.Messages.Select((m, i) =>
                        (m.Role == "assistant" ? "[Your previous reply]" : i == r.Messages.Count - 1 ? "[User - answer this message]" : "[User]") + "\n" + m.Text));

                string args = "-p --output-format stream-json --verbose --include-partial-messages --tools \"\" --no-session-persistence"
                            + " --strict-mcp-config --system-prompt-file \"" + sysFile + "\""
                            + (string.IsNullOrEmpty(Model) ? "" : " --model " + Model);
                bool cmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
                var psi = new ProcessStartInfo(cmd ? "cmd.exe" : exe, cmd ? "/c \"\"" + exe + "\" " + args + "\"" : args)
                {
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                // se CorbolIp è stato avviato da una sessione di Claude Code, quella nuova deve essere indipendente
                psi.EnvironmentVariables.Remove("CLAUDECODE");
                psi.EnvironmentVariables.Remove("CLAUDE_CODE_ENTRYPOINT");
                // l'account scelto nelle impostazioni (altrimenti quello predefinito di Claude Code)
                if (!string.IsNullOrEmpty(ConfigDir)) psi.EnvironmentVariables["CLAUDE_CONFIG_DIR"] = ConfigDir;
                p = Process.Start(psi);
                var proc = p;
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                using (cancel.Register(delegate { try { if (!proc.HasExited) proc.Kill(); } catch { } }))
                {
                    var input = Encoding.UTF8.GetBytes(user);
                    p.StandardInput.BaseStream.Write(input, 0, input.Length);
                    p.StandardInput.Close();

                    string line;
                    bool gotResult = false;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        object o;
                        try { o = Json.DeserializeObject(line); } catch { continue; }
                        string type = Str(o, "type");
                        if (type == "stream_event")
                        {
                            var ev = Get(o, "event");
                            string et = Str(ev, "type");
                            if (et == "message_start" && res.Model == null) res.Model = Str(ev, "message", "model");
                            else if (et == "content_block_delta" && Str(ev, "delta", "type") == "text_delta")
                            {
                                string t = Str(ev, "delta", "text");
                                if (!string.IsNullOrEmpty(t)) { res.Text += t; if (onText != null) onText(t); }
                            }
                            else if (et == "message_delta" && Str(ev, "delta", "stop_reason") != null) res.StopReason = Str(ev, "delta", "stop_reason");
                        }
                        else if (type == "result")
                        {
                            gotResult = true;
                            bool isError = Get(o, "is_error") is bool && (bool)Get(o, "is_error");
                            string text = Str(o, "result");
                            if (isError || Str(o, "subtype") != "success")
                                res.Error = "Claude Code: " + (text ?? Str(o, "subtype") ?? L.T("errore", "error"));
                            else if (res.Text.Length == 0 && !string.IsNullOrEmpty(text))
                            {
                                res.Text = text;   // nessun pezzo in streaming (versioni vecchie): la risposta intera
                                if (onText != null) onText(text);
                            }
                            if (Str(o, "stop_reason") != null) res.StopReason = Str(o, "stop_reason");
                        }
                    }
                    p.WaitForExit(10000);
                    if (cancel.IsCancellationRequested) { res.Cancelled = true; return res; }
                    if (!gotResult && res.Error == null)
                    {
                        string tail;
                        lock (err) tail = err.ToString().Trim();
                        if (tail.Length > 300) tail = tail.Substring(tail.Length - 300);
                        res.Error = L.F("Claude Code si è chiuso senza rispondere (codice {0}). Hai fatto il login in Claude Code?",
                                        "Claude Code closed without answering (code {0}). Are you logged in to Claude Code?", p.HasExited ? p.ExitCode : -1)
                                  + (tail.Length > 0 ? "\n" + tail : "");
                    }
                }
            }
            catch (Exception ex)
            {
                if (cancel.IsCancellationRequested) res.Cancelled = true;
                else res.Error = L.T("Impossibile avviare Claude Code:", "Could not start Claude Code:") + " " + ex.Message;
            }
            finally
            {
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                try { File.Delete(sysFile); } catch { }
            }
            return res;
        }
    }
}

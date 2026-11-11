// Assistente tramite Claude Code installato sul PC, con l'abbonamento Claude dell'utente (niente chiave API).
// Solo per uso personale: l'abbonamento è dell'utente che ha fatto il login in Claude Code.
// AppIP avvia "claude -p" senza strumenti (non può leggere file né eseguire comandi), senza server MCP e
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

namespace AppIP
{
    class ClaudeCodeProvider : IAiProvider
    {
        public static readonly string[] Models = { "opus", "sonnet", "haiku" };
        public string Model = "";   // vuoto = il modello predefinito di Claude Code per l'account

        public string Name { get { return "Claude Code"; } }
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
            // Cartella vuota tutta per AppIP: niente CLAUDE.md o impostazioni di progetto di altre cartelle
            string dir = Path.Combine(Path.GetTempPath(), "AppIP-claudecode");
            string sysFile = Path.Combine(dir, "system-" + Guid.NewGuid().ToString("N") + ".txt");
            var err = new StringBuilder();
            Process p = null;
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(sysFile, r.System ?? "", new UTF8Encoding(false));
                string user = string.Join("\n\n", r.Messages.Select(m => m.Text));

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
                // se AppIP è stato avviato da una sessione di Claude Code, quella nuova deve essere indipendente
                psi.EnvironmentVariables.Remove("CLAUDECODE");
                psi.EnvironmentVariables.Remove("CLAUDE_CODE_ENTRYPOINT");
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

// Assistente AI: i "provider" che generano il testo (ripresi da AppPad). Si manda un'istruzione di
// sistema più i messaggi e il testo arriva un pezzo alla volta (streaming).
//   - Claude: API Anthropic (POST /v1/messages), con la chiave dell'utente.
//   - Compatibile OpenAI: POST <url>/chat/completions. Copre OpenAI, Mistral, OpenRouter, Groq, i
//     servizi sul PC come Ollama o LM Studio e l'assistente locale (llama-server, vedi AiLocal.cs).
// Niente SDK (non esistono per .NET Framework/C# 5): HTTP e JSON a mano, con JavaScriptSerializer.
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace AppIP
{
    class AiMessage
    {
        public string Role, Text;   // Role: "user" o "assistant"
        public AiMessage(string role, string text) { Role = role; Text = text; }
    }

    class AiRequest
    {
        public string System;                                       // istruzioni (può essere null)
        public List<AiMessage> Messages = new List<AiMessage>();
        public int MaxTokens;                                       // 0 = valore predefinito del provider
        // Strumenti che il modello può usare (null = nessuno): RunTool li esegue e restituisce il risultato come testo.
        // Viene chiamato sul thread della richiesta.
        public List<AiTool> Tools;
        public Func<AiToolCall, string> RunTool;
        public int MaxToolRounds = 6;                               // giri di strumenti al massimo, poi il modello deve rispondere
        public Action OnRestart;                                    // il servizio ricomincia la risposta da capo (assistente locale)

        public static AiRequest Simple(string system, string user)
        {
            var r = new AiRequest { System = system };
            r.Messages.Add(new AiMessage("user", user));
            return r;
        }
    }

    class AiTool
    {
        public string Name, Description;
        public Dictionary<string, object> Schema;                  // JSON Schema dei parametri
    }

    class AiToolCall
    {
        public string Id, Name, Json;                               // Json = argomenti come li ha scritti il modello
        public Dictionary<string, object> Args;                     // null se il JSON non è valido
    }

    // Un blocco della risposta di Claude, da rimandare uguale nel giro successivo (testo, ragionamento, strumento)
    class AiBlock
    {
        public string Type, Id, Name, Signature, Data;
        public readonly StringBuilder Text = new StringBuilder();   // testo, ragionamento o JSON degli argomenti
    }

    class AiResult
    {
        public string Text = "";        // tutto il testo ricevuto
        public string StopReason;       // "end_turn"/"stop", "max_tokens"/"length", "refusal"...
        public string Model;            // il modello che ha risposto davvero
        public string Error;            // messaggio per l'utente, già tradotto; null = tutto bene
        public bool Cancelled;
        public List<AiToolCall> ToolCalls = new List<AiToolCall>(); // strumenti chiesti in quest'ultima risposta
        public List<AiBlock> Blocks = new List<AiBlock>();          // blocchi della risposta (solo Claude)
        public bool Ok { get { return Error == null && !Cancelled; } }
        public bool Truncated { get { return StopReason == "max_tokens" || StopReason == "length"; } }
    }

    interface IAiProvider
    {
        string Name { get; }    // per i messaggi: "Claude", "OpenAI", "Assistente locale"...
        bool SendsTextOutside { get; }  // false solo per l'assistente locale
        // Bloccante: va chiamato da un thread in background. onText riceve i pezzi di testo su quel thread.
        AiResult Run(AiRequest req, Action<string> onText, CancellationToken cancel);
    }

    // Parte comune: richiesta HTTP con corpo JSON, risposta letta riga per riga (Server-Sent Events),
    // nuovi tentativi se il servizio è sovraccarico prima che arrivi del testo, Stop che chiude la connessione.
    abstract class AiHttpProvider : IAiProvider
    {
        public abstract string Name { get; }
        public virtual bool SendsTextOutside { get { return true; } }

        protected const int Retries = 2;
        public int TimeoutMs = 120000;    // attesa massima del primo byte e tra due pezzi (i modelli che "pensano" tacciono a lungo)

        protected static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };

        protected abstract string Url { get; }
        protected abstract void AddHeaders(HttpWebRequest req);
        // Conversazione nel formato del servizio: si allunga a ogni giro di strumenti
        protected abstract List<object> StartConversation(AiRequest r);
        protected abstract Dictionary<string, object> Body(AiRequest r, List<object> conv);
        // Aggiunge alla conversazione la risposta con le richieste di strumenti e i risultati
        protected abstract void AppendToolTurn(List<object> conv, AiResult res, List<string> results);
        // Una riga "data:" dello stream; restituisce false quando lo stream è finito
        protected abstract bool OnData(string data, AiResult res, Action<string> onText);
        protected abstract string ErrorFromBody(int status, string body);

        // Una richiesta; se il modello chiede strumenti si eseguono e si riparte, finché risponde con il testo
        public AiResult Run(AiRequest r, Action<string> onText, CancellationToken cancel)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var conv = StartConversation(r);
            var total = new AiResult();
            for (int round = 0; ; round++)
            {
                var res = RunOnce(r, conv, onText, cancel);
                foreach (var c in res.ToolCalls)
                    if (c.Args == null && c.Json != null)
                        c.Args = c.Json.Trim().Length == 0 ? new Dictionary<string, object>() : Parse(c.Json) as Dictionary<string, object>;
                total.Text += res.Text;
                if (res.Model != null) total.Model = res.Model;
                total.StopReason = res.StopReason;
                total.Error = res.Error;
                total.Cancelled = res.Cancelled;
                if (!res.Ok || r.Tools == null || r.RunTool == null || res.ToolCalls.Count == 0) return total;
                if (res.Truncated) { total.Error = Name + ": " + L.T("risposta troncata durante una richiesta di controllo.", "reply cut short during a check request."); return total; }
                var results = new List<string>();
                foreach (var c in res.ToolCalls)
                {
                    if (cancel.IsCancellationRequested) { total.Cancelled = true; return total; }
                    string outp;
                    if (round >= r.MaxToolRounds) outp = "Error: no more checks allowed. Answer now with what you have.";
                    else if (c.Args == null) outp = "Error: INVALID_JSON. The arguments were not valid JSON; send the call again.";
                    else
                    {
                        try { outp = r.RunTool(c); }
                        catch (Exception ex) { outp = "Error: " + ex.Message; }
                    }
                    results.Add(outp ?? "");
                }
                AppendToolTurn(conv, res, results);
            }
        }

        // Una sola chiamata al servizio, con nuovi tentativi se è sovraccarico
        AiResult RunOnce(AiRequest r, List<object> conv, Action<string> onText, CancellationToken cancel)
        {
            var res = new AiResult();
            byte[] body;
            try { body = Encoding.UTF8.GetBytes(Json.Serialize(Body(r, conv))); }
            catch (Exception ex) { res.Error = ex.Message; return res; }

            for (int attempt = 0; ; attempt++)
            {
                res.ToolCalls.Clear();
                res.Blocks.Clear();
                int retryAfter = 0;
                bool started = false;   // è già arrivato del testo: da qui in poi niente nuovi tentativi
                HttpWebRequest req = null;
                try
                {
                    req = (HttpWebRequest)WebRequest.Create(Url);
                    req.Method = "POST";
                    req.ContentType = "application/json";
                    req.Accept = "text/event-stream";
                    req.UserAgent = "AppIP/" + AppVersion.Text;
                    req.Timeout = TimeoutMs;
                    req.ReadWriteTimeout = TimeoutMs;
                    req.AllowWriteStreamBuffering = true;
                    req.ServicePoint.Expect100Continue = false;
                    AddHeaders(req);
                    using (cancel.Register(delegate { try { req.Abort(); } catch { } }))
                    {
                        using (var st = req.GetRequestStream()) st.Write(body, 0, body.Length);
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        using (var sr = new StreamReader(resp.GetResponseStream(), new UTF8Encoding(false)))
                        {
                            string line;
                            Action<string> emit = delegate (string t)
                            {
                                if (string.IsNullOrEmpty(t)) return;
                                started = true;
                                res.Text += t;
                                if (onText != null) onText(t);
                            };
                            while ((line = sr.ReadLine()) != null)
                            {
                                if (cancel.IsCancellationRequested) break;
                                if (!line.StartsWith("data:")) continue;   // "event:", commenti, righe vuote
                                string data = line.Substring(5).Trim();
                                if (data.Length == 0) continue;
                                if (!OnData(data, res, emit)) break;
                                if (res.Error != null) break;
                            }
                        }
                    }
                    if (cancel.IsCancellationRequested) res.Cancelled = true;
                    return res;
                }
                catch (WebException ex)
                {
                    if (cancel.IsCancellationRequested) { res.Cancelled = true; return res; }
                    var http = ex.Response as HttpWebResponse;
                    if (http == null)
                    {
                        // Solo gli errori di rete passeggeri; "connessione rifiutata" o "tempo scaduto" non cambiano riprovando
                        bool transient = ex.Status == WebExceptionStatus.ConnectionClosed || ex.Status == WebExceptionStatus.ReceiveFailure
                                      || ex.Status == WebExceptionStatus.SendFailure || ex.Status == WebExceptionStatus.KeepAliveFailure;
                        if (!started && attempt < Retries && transient) { Wait(2, cancel); continue; }
                        res.Error = ConnectionError(ex);
                        return res;
                    }
                    int status = (int)http.StatusCode;
                    string text = "";
                    try { using (var sr = new StreamReader(http.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd(); } catch { }
                    int.TryParse(http.Headers["retry-after"] ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out retryAfter);
                    http.Close();
                    // 429 troppe richieste, 5xx/529 servizio sovraccarico: si riprova, se non è ancora arrivato nulla
                    if ((status == 429 || status >= 500) && !started && attempt < Retries && retryAfter <= 20)
                    {
                        Wait(retryAfter > 0 ? retryAfter : 2 << attempt, cancel);
                        if (cancel.IsCancellationRequested) { res.Cancelled = true; return res; }
                        continue;
                    }
                    res.Error = ErrorFromBody(status, text);
                    return res;
                }
                catch (Exception ex)
                {
                    if (cancel.IsCancellationRequested) { res.Cancelled = true; return res; }
                    if (!started && attempt < Retries && ex is IOException) { Wait(2, cancel); continue; }
                    res.Error = ex.Message;
                    return res;
                }
            }
        }

        static void Wait(int seconds, CancellationToken cancel)
        {
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
        }

        protected string ConnectionError(WebException ex)
        {
            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return L.F("{0}: indirizzo non trovato. Controlla la connessione a Internet o l'indirizzo del servizio.",
                               "{0}: address not found. Check your Internet connection or the service address.", Name);
                case WebExceptionStatus.ConnectFailure:
                    return L.F("{0}: connessione rifiutata. Il servizio è avviato?", "{0}: connection refused. Is the service running?", Name);
                case WebExceptionStatus.Timeout:
                    return L.F("{0}: nessuna risposta (tempo scaduto).", "{0}: no response (timed out).", Name);
                default:
                    return Name + ": " + ex.Message;
            }
        }

        // Messaggio comune per gli errori HTTP più frequenti; detail è il testo dell'errore del servizio
        protected string HttpError(int status, string detail)
        {
            string what;
            if (status == 401) what = L.T("chiave API non valida o mancante", "invalid or missing API key");
            else if (status == 402) what = L.T("credito esaurito o problema di fatturazione", "out of credit or billing problem");
            else if (status == 403) what = L.T("accesso negato", "access denied");
            else if (status == 404) what = L.T("modello o indirizzo non trovato", "model or address not found");
            else if (status == 413) what = L.T("testo troppo lungo", "text too long");
            else if (status == 429) what = L.T("troppe richieste: riprova tra poco", "too many requests: try again shortly");
            else if (status == 529 || status == 503) what = L.T("servizio sovraccarico: riprova tra poco", "service overloaded: try again shortly");
            else if (status >= 500) what = L.T("errore del servizio: riprova tra poco", "service error: try again shortly");
            else what = L.F("errore {0}", "error {0}", status);
            if (!string.IsNullOrEmpty(detail) && detail.Length < 400) what += " (" + detail.Trim() + ")";
            return Name + ": " + what;
        }

        // ---- Lettura del JSON (JavaScriptSerializer dà Dictionary<string, object> e object[]) ----

        protected static object Parse(string json)
        {
            try { return Json.DeserializeObject(json); } catch { return null; }
        }

        // Get(obj, "a", "b", 0, "c") = obj.a.b[0].c, o null
        protected static object Get(object o, params object[] path)
        {
            foreach (var p in path)
            {
                if (o == null) return null;
                if (p is string)
                {
                    var d = o as IDictionary<string, object>;
                    object v;
                    o = d != null && d.TryGetValue((string)p, out v) ? v : null;
                }
                else
                {
                    var a = o as IList;
                    int i = (int)p;
                    o = a != null && i < a.Count ? a[i] : null;
                }
            }
            return o;
        }

        protected static string Str(object o, params object[] path)
        {
            return Get(o, path) as string;
        }

        // Errore nel formato comune {"error": {"message": "..."}} (Anthropic, OpenAI e quasi tutti i compatibili)
        protected static string ErrorMessage(string body)
        {
            var o = Parse(body);
            return Str(o, "error", "message") ?? Str(o, "error") ?? Str(o, "message")
                ?? (o == null && body != null && body.Length < 200 ? body : null);
        }
    }

    // API Anthropic: https://api.anthropic.com/v1/messages
    class ClaudeProvider : AiHttpProvider
    {
        public const string DefaultModel = "claude-opus-5-5";
        public static readonly string[] Models = { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };
        const string ApiVersion = "2026-10-21";
        const string FallbackBeta = "server-side-fallback-2026-07-01";

        public string ApiKey, Model = DefaultModel;
        public string Effort = "";      // "", "low", "medium", "high": vuoto = predefinito del modello
        public string BaseUrl = "https://api.anthropic.com";

        public override string Name { get { return "Claude"; } }
        protected override string Url { get { return BaseUrl.TrimEnd('/') + "/v1/messages"; } }

        // Se i filtri di sicurezza rifiutano una richiesta, l'API la ripete da sola su un altro modello
        // ("fallbacks": "default"). Esiste solo sull'API Anthropic e non per Haiku.
        bool UseFallbacks
        {
            get
            {
                return BaseUrl.IndexOf("api.anthropic.com", StringComparison.OrdinalIgnoreCase) >= 0
                    && !Model.StartsWith("claude-haiku", StringComparison.OrdinalIgnoreCase);
            }
        }

        protected override void AddHeaders(HttpWebRequest req)
        {
            req.Headers["x-api-key"] = ApiKey ?? "";
            req.Headers["anthropic-version"] = ApiVersion;
            if (UseFallbacks) req.Headers["anthropic-beta"] = FallbackBeta;
        }

        protected override List<object> StartConversation(AiRequest r)
        {
            return r.Messages.Select(m => (object)new Dictionary<string, object> { { "role", m.Role }, { "content", m.Text } }).ToList();
        }

        protected override Dictionary<string, object> Body(AiRequest r, List<object> conv)
        {
            var b = new Dictionary<string, object>
            {
                { "model", Model },
                // Con lo streaming non c'è rischio di timeout: margine ampio, anche per il "ragionamento" del modello
                { "max_tokens", r.MaxTokens > 0 ? r.MaxTokens : 64000 },
                { "stream", true },
                { "messages", conv.ToArray() }
            };
            if (!string.IsNullOrEmpty(r.System)) b["system"] = r.System;
            if (r.Tools != null && r.Tools.Count > 0)
                b["tools"] = r.Tools.Select(t => new Dictionary<string, object>
                {
                    { "name", t.Name }, { "description", t.Description }, { "input_schema", t.Schema },
                    { "eager_input_streaming", true }   // argomenti in streaming: si controllano prima di eseguire
                }).ToArray();
            if (!string.IsNullOrEmpty(Effort)) b["output_config"] = new Dictionary<string, object> { { "effort", Effort } };
            if (UseFallbacks) b["fallbacks"] = "default";
            return b;
        }

        // Eventi: message_start, content_block_start/delta/stop, message_delta (stop_reason), message_stop, ping, error.
        // Solo i "text_delta" sono testo della risposta; il ragionamento ("thinking") non si mostra, ma i blocchi
        // si conservano tutti (con la firma) per rimandarli uguali nel giro successivo degli strumenti.
        protected override bool OnData(string data, AiResult res, Action<string> onText)
        {
            var o = Parse(data);
            switch (Str(o, "type"))
            {
                case "message_start":
                    res.Model = Str(o, "message", "model");
                    break;
                case "content_block_start":
                {
                    var cb = Get(o, "content_block");
                    var blk = new AiBlock { Type = Str(cb, "type"), Id = Str(cb, "id"), Name = Str(cb, "name"), Data = Str(cb, "data"), Signature = Str(cb, "signature") };
                    if (blk.Type == "text") blk.Text.Append(Str(cb, "text") ?? "");
                    if (blk.Type == "thinking") blk.Text.Append(Str(cb, "thinking") ?? "");
                    res.Blocks.Add(blk);
                    break;
                }
                case "content_block_delta":
                {
                    var blk = res.Blocks.Count > 0 ? res.Blocks[res.Blocks.Count - 1] : null;
                    string dt = Str(o, "delta", "type");
                    if (dt == "text_delta")
                    {
                        string t = Str(o, "delta", "text");
                        if (blk != null) blk.Text.Append(t);
                        onText(t);
                    }
                    else if (blk != null && dt == "thinking_delta") blk.Text.Append(Str(o, "delta", "thinking"));
                    else if (blk != null && dt == "signature_delta") blk.Signature = (blk.Signature ?? "") + Str(o, "delta", "signature");
                    else if (blk != null && dt == "input_json_delta") blk.Text.Append(Str(o, "delta", "partial_json"));
                    break;
                }
                case "content_block_stop":
                {
                    var blk = res.Blocks.Count > 0 ? res.Blocks[res.Blocks.Count - 1] : null;
                    if (blk != null && blk.Type == "tool_use")
                    {
                        string json = blk.Text.ToString();
                        var args = json.Trim().Length == 0 ? new Dictionary<string, object>() : Parse(json) as Dictionary<string, object>;
                        res.ToolCalls.Add(new AiToolCall { Id = blk.Id, Name = blk.Name, Json = json, Args = args });
                    }
                    break;
                }
                case "message_delta":
                    res.StopReason = Str(o, "delta", "stop_reason") ?? res.StopReason;
                    break;
                case "message_stop":
                    if (res.StopReason == "refusal")
                        res.Error = L.T("Claude ha rifiutato la richiesta.", "Claude declined the request.");
                    return false;
                case "error":
                    string type = Str(o, "error", "type");
                    res.Error = type == "overloaded_error"
                        ? HttpError(529, null)
                        : Name + ": " + (Str(o, "error", "message") ?? type ?? L.T("errore", "error"));
                    return false;
            }
            return true;
        }

        protected override string ErrorFromBody(int status, string body)
        {
            return HttpError(status, ErrorMessage(body));
        }

        // Risposta dell'assistente con tutti i suoi blocchi (ragionamento compreso, invariato) e risultati degli strumenti
        protected override void AppendToolTurn(List<object> conv, AiResult res, List<string> results)
        {
            var content = new List<object>();
            foreach (var b in res.Blocks)
            {
                if (b.Type == "text") { if (b.Text.Length > 0) content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", b.Text.ToString() } }); }
                else if (b.Type == "thinking") content.Add(new Dictionary<string, object> { { "type", "thinking" }, { "thinking", b.Text.ToString() }, { "signature", b.Signature ?? "" } });
                else if (b.Type == "redacted_thinking") content.Add(new Dictionary<string, object> { { "type", "redacted_thinking" }, { "data", b.Data ?? "" } });
                else if (b.Type == "tool_use")
                {
                    var call = res.ToolCalls.FirstOrDefault(c => c.Id == b.Id);
                    content.Add(new Dictionary<string, object> { { "type", "tool_use" }, { "id", b.Id }, { "name", b.Name }, { "input", (call != null ? call.Args : null) ?? new Dictionary<string, object>() } });
                }
            }
            conv.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", content.ToArray() } });
            var outp = new List<object>();
            for (int i = 0; i < res.ToolCalls.Count; i++)
                outp.Add(new Dictionary<string, object> { { "type", "tool_result" }, { "tool_use_id", res.ToolCalls[i].Id }, { "content", results[i] } });
            conv.Add(new Dictionary<string, object> { { "role", "user" }, { "content", outp.ToArray() } });
        }
    }

    // API "chat completions" compatibile OpenAI: <BaseUrl>/chat/completions
    class OpenAiProvider : AiHttpProvider
    {
        public string BaseUrl = "https://api.openai.com/v1", ApiKey, Model;
        public string DisplayName;                                  // es. "Ollama"; vuoto = ricavato dall'indirizzo
        public bool Local;                                          // servizio sul PC (es. Ollama): il testo non esce
        public Dictionary<string, object> Extra;                    // campi aggiuntivi del corpo (es. per llama-server)

        public override string Name
        {
            get
            {
                if (!string.IsNullOrEmpty(DisplayName)) return DisplayName;
                try { return new Uri(BaseUrl).Host; } catch { return "OpenAI"; }
            }
        }
        public override bool SendsTextOutside { get { return !Local; } }
        protected override string Url { get { return BaseUrl.TrimEnd('/') + "/chat/completions"; } }

        protected override void AddHeaders(HttpWebRequest req)
        {
            if (!string.IsNullOrEmpty(ApiKey)) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + ApiKey;
        }

        protected override List<object> StartConversation(AiRequest r)
        {
            var msgs = new List<object>();
            if (!string.IsNullOrEmpty(r.System)) msgs.Add(new Dictionary<string, object> { { "role", "system" }, { "content", r.System } });
            foreach (var m in r.Messages) msgs.Add(new Dictionary<string, object> { { "role", m.Role }, { "content", m.Text } });
            return msgs;
        }

        protected override Dictionary<string, object> Body(AiRequest r, List<object> conv)
        {
            var b = new Dictionary<string, object>
            {
                { "model", Model ?? "" },
                { "stream", true },
                { "messages", conv.ToArray() }
            };
            if (r.Tools != null && r.Tools.Count > 0)
                b["tools"] = r.Tools.Select(t => new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "function", new Dictionary<string, object> { { "name", t.Name }, { "description", t.Description }, { "parameters", t.Schema } } }
                }).ToArray();
            // max_tokens solo se richiesto: i modelli recenti di OpenAI vogliono un altro nome per lo stesso campo
            if (r.MaxTokens > 0) b["max_tokens"] = r.MaxTokens;
            if (Extra != null) foreach (var kv in Extra) b[kv.Key] = kv.Value;
            return b;
        }

        // Righe "data: {...choices[0].delta.content...}", finite da "data: [DONE]". Il ragionamento
        // ("reasoning_content" o "reasoning") non si mostra.
        protected override bool OnData(string data, AiResult res, Action<string> onText)
        {
            if (data == "[DONE]") return false;
            var o = Parse(data);
            if (o == null) return true;
            if (Get(o, "error") != null)
            {
                res.Error = Name + ": " + (Str(o, "error", "message") ?? Str(o, "error") ?? L.T("errore", "error"));
                return false;
            }
            if (res.Model == null) res.Model = Str(o, "model");
            onText(Str(o, "choices", 0, "delta", "content"));
            // Strumenti: arrivano a pezzi, ognuno con il suo indice (id e nome nel primo pezzo, poi gli argomenti)
            var calls = Get(o, "choices", 0, "delta", "tool_calls") as IList;
            if (calls != null)
                foreach (var c in calls)
                {
                    int idx = 0;
                    var ix = Get(c, "index");
                    if (ix != null) idx = Convert.ToInt32(ix, CultureInfo.InvariantCulture);
                    while (res.ToolCalls.Count <= idx) res.ToolCalls.Add(new AiToolCall { Json = "" });
                    var tc = res.ToolCalls[idx];
                    if (Str(c, "id") != null) tc.Id = Str(c, "id");
                    if (Str(c, "function", "name") != null) tc.Name = (tc.Name ?? "") + Str(c, "function", "name");
                    tc.Json += Str(c, "function", "arguments") ?? "";
                }
            string finish = Str(o, "choices", 0, "finish_reason");
            if (finish != null) res.StopReason = finish;
            return true;
        }

        protected override void AppendToolTurn(List<object> conv, AiResult res, List<string> results)
        {
            var calls = new List<object>();
            for (int i = 0; i < res.ToolCalls.Count; i++)
            {
                var c = res.ToolCalls[i];
                if (string.IsNullOrEmpty(c.Id)) c.Id = "call_" + (conv.Count + i);
                calls.Add(new Dictionary<string, object>
                {
                    { "id", c.Id }, { "type", "function" },
                    { "function", new Dictionary<string, object> { { "name", c.Name ?? "" }, { "arguments", string.IsNullOrEmpty(c.Json) ? "{}" : c.Json } } }
                });
            }
            conv.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", res.Text ?? "" }, { "tool_calls", calls.ToArray() } });
            for (int i = 0; i < res.ToolCalls.Count; i++)
                conv.Add(new Dictionary<string, object> { { "role", "tool" }, { "tool_call_id", res.ToolCalls[i].Id }, { "content", results[i] } });
        }

        protected override string ErrorFromBody(int status, string body)
        {
            return HttpError(status, ErrorMessage(body));
        }

        // Modelli disponibili (GET <BaseUrl>/models): per proporli nelle impostazioni. Bloccante.
        public List<string> ListModels()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var req = (HttpWebRequest)WebRequest.Create(BaseUrl.TrimEnd('/') + "/models");
            req.UserAgent = "AppIP/" + AppVersion.Text;
            req.Timeout = 15000;
            AddHeaders(req);
            WebResponse resp;
            try { resp = req.GetResponse(); }
            catch (WebException ex)
            {
                // Stessi messaggi delle richieste: "chiave API non valida", "connessione rifiutata"...
                var http = ex.Response as HttpWebResponse;
                if (http == null) throw new Exception(ConnectionError(ex));
                string body = "";
                try { using (var r = new StreamReader(http.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd(); } catch { }
                throw new Exception(HttpError((int)http.StatusCode, ErrorMessage(body)));
            }
            using (resp)
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var list = Get(Parse(sr.ReadToEnd()), "data") as IList;
                var ids = new List<string>();
                if (list != null) foreach (var m in list) { string id = Str(m, "id"); if (id != null) ids.Add(id); }
                ids.Sort(StringComparer.OrdinalIgnoreCase);
                return ids;
            }
        }
    }

    // Chiavi API in settings.txt cifrate con DPAPI: le legge solo lo stesso utente Windows sullo stesso PC
    static class Secret
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AppIP.ai.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dpapi:")) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }   // copiata da un altro utente o PC: va reinserita
        }
    }

    // Impostazioni dell'assistente in settings.txt (chiavi "ai.*")
    static class AiSettings
    {
        public const string Local = "local", Claude = "claude", OpenAi = "openai";
        public static readonly string[] Efforts = { "low", "medium", "high" };

        public static bool Enabled { get { return Settings.Get("ai.enabled", false); } set { Settings.Set("ai.enabled", value); } }
        // Predefinito: l'assistente locale (niente chiave API, i dati non escono dal PC)
        public static string Provider { get { return Settings.Get("ai.provider", Local); } set { Settings.Set("ai.provider", value); } }
        // Anonimizzazione dei dati prima dell'invio: attiva di default
        public static bool Anonymize { get { return Settings.Get("ai.anon", true); } set { Settings.Set("ai.anon", value); } }

        public static string ClaudeKey { get { return Secret.Unprotect(Settings.Get("ai.claude.key", "")); } set { Settings.Set("ai.claude.key", Secret.Protect(value)); } }
        public static string ClaudeModel { get { return Settings.Get("ai.claude.model", ClaudeProvider.DefaultModel); } set { Settings.Set("ai.claude.model", value); } }
        // Spiegare una diagnosi è un compito semplice: impegno basso di default (risposte rapide ed economiche)
        public static string ClaudeEffort { get { return Settings.Get("ai.claude.effort", "low"); } set { Settings.Set("ai.claude.effort", value); } }

        public static string OpenAiUrl { get { return Settings.Get("ai.openai.url", "https://api.openai.com/v1"); } set { Settings.Set("ai.openai.url", value); } }
        public static string OpenAiKey { get { return Secret.Unprotect(Settings.Get("ai.openai.key", "")); } set { Settings.Set("ai.openai.key", Secret.Protect(value)); } }
        public static string OpenAiModel { get { return Settings.Get("ai.openai.model", ""); } set { Settings.Set("ai.openai.model", value); } }

        public static string LocalModel { get { return Settings.Get("ai.local.model", AiCatalog.Recommended().Id); } set { Settings.Set("ai.local.model", value); } }
        // "auto" (predefinito): scheda video con riserva sul processore; "cpu"; "vulkan" (solo GPU)
        public static string LocalVariant { get { return Settings.Get("ai.local.variant", "auto"); } set { Settings.Set("ai.local.variant", value); } }

        public static bool IsLocalUrl(string url)
        {
            try
            {
                var h = new Uri(url).Host;
                IPAddress ip;
                return h == "localhost" || (IPAddress.TryParse(h, out ip) && IPAddress.IsLoopback(ip));
            }
            catch { return false; }
        }

        // Provider configurato; null se manca qualcosa (chiave, modello, indirizzo). L'assistente locale c'è sempre:
        // se non è ancora scaricato lo dice lui alla richiesta.
        public static IAiProvider CurrentProvider()
        {
            if (Provider == Local) return LocalAi.Instance;
            if (Provider == OpenAi)
            {
                if (OpenAiModel.Length == 0 || OpenAiUrl.Length == 0) return null;
                return new OpenAiProvider { BaseUrl = OpenAiUrl, ApiKey = OpenAiKey, Model = OpenAiModel, Local = IsLocalUrl(OpenAiUrl) };
            }
            string key = ClaudeKey;
            if (key.Length == 0) return null;
            return new ClaudeProvider { ApiKey = key, Model = ClaudeModel, Effort = ClaudeEffort };
        }

        public static bool Ready { get { return Enabled && CurrentProvider() != null; } }
    }

    // Impostazioni dell'assistente: il widget le legge e le riscrive in settings.txt insieme alle sue
    // (righe "ai.*"); Save chiede al widget di salvare il file.
    static class Settings
    {
        public static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public static Action SaveHandler;

        public static void Save() { if (SaveHandler != null) SaveHandler(); }

        public static string Get(string key, string def) { string v; return Values.TryGetValue(key, out v) ? v : def; }
        public static bool Get(string key, bool def) { string v = Get(key, ""); return v == "1" ? true : v == "0" ? false : def; }
        public static int Get(string key, int def) { int v; return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def; }
        public static void Set(string key, int v) { Values[key] = v.ToString(CultureInfo.InvariantCulture); }
        public static void Set(string key, string v) { Values[key] = v ?? ""; }
        public static void Set(string key, bool v) { Values[key] = v ? "1" : "0"; }
    }
}

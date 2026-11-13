<h1 align="center">CorbolIp</h1>

<p align="center"><sub>Prima si chiamava <b>AppIP</b> (fino alla versione 1.7.2): chi ha già installato la versione vecchia riceve CorbolIp con l'aggiornamento automatico, con impostazioni e dati.<br>Formerly <b>AppIP</b> (up to version 1.7.2): existing installations get CorbolIp through the automatic update, keeping settings and data.</sub></p>

<p align="center">
  Widget per il desktop di Windows, in stile <i>conky</i>: nome macchina, IP, DNS, IP pubblico, velocità di rete, Wi-Fi, VPN.<br>
  <a href="#english">English below</a>
</p>

<p align="center">
  <a href="https://github.com/corbolma/CorbolIp/releases/latest"><b>⬇ Scarica l'ultima versione</b></a> ·
  <a href="https://github.com/corbolma/CorbolIp/wiki">Wiki</a> ·
  <a href="https://github.com/corbolma/CorbolIp/issues">Segnala un problema</a>
</p>

<p align="center">
  <img src="docs/widget-scuro.png" alt="CorbolIp, tema scuro" width="330">
  &nbsp;
  <img src="docs/widget-chiaro-vpn.png" alt="CorbolIp, tema chiaro con VPN attiva" width="330">
  &nbsp;
  <img src="docs/widget-compatto.png" alt="CorbolIp, modalità compatta" width="270">
</p>
<p align="center"><sub>Tema scuro · tema chiaro con VPN attiva · modalità compatta (dati di esempio)</sub></p>

---

## Italiano

### Cosa mostra
- **Nome macchina** e dominio
- Per ogni scheda di rete attiva: **IP**, **gateway**, **DNS**, **MAC address** e se l'IP è **DHCP o statico** (con la scadenza del lease)
- **IPv6** (opzionale): indirizzo di ogni scheda e IP pubblico IPv6
- **IP pubblico** (aggiornato ogni 5 minuti e quando cambia la rete)
- **Velocità** di download/upload con grafico degli ultimi 60 secondi
- **Wi-Fi**: nome della rete, qualità del segnale, banda (2.4 / 5 / 6 GHz), canale, standard (Wi-Fi 4/5/6/7) e velocità del collegamento
- **VPN** attiva, con badge e IP assegnato
- **Ping** verso router e Internet (disattivato di default, intervallo configurabile)
- **Test velocità** della linea (ADSL, fibra, 4G...): latenza, jitter, download e upload, avviato solo a richiesta dal menu, con un commento su cosa regge la linea (videochiamate, streaming 4K)
- **Registro eventi** di rete: cadute, cambi di rete, IP, Wi-Fi e VPN, router o Internet che non rispondono, sospensione del PC (file locale, ultimi 30 giorni)
- **Diagnosi rete**: controlla scheda, indirizzo IP, Wi-Fi, router, Internet, DNS, pagina di accesso (hotel, reti ospiti), HTTPS e VPN e spiega in parole semplici dov'è il problema, con un report da mandare all'assistenza
- **Assistente AI** (facoltativo, spento di default): nella diagnosi rete spiega i risultati, risponde alle tue domande e scrive la segnalazione per l'assistenza, con l'assistente locale (Qwen sul PC, predefinito: nessuna chiave e i dati non escono), con Claude oppure con un servizio compatibile OpenAI
- Interfaccia in **italiano e inglese** (segue la lingua di Windows, modificabile dal menu *Lingua / Language*); altre lingue si aggiungono con un semplice file di testo, vedi [lang/README.md](lang/README.md)

### Come si usa
- Il widget resta **incollato al desktop** (sotto le finestre, visibile anche con Win+D). Trascinalo dove vuoi.
- **Clic su un valore** per copiarlo negli appunti.
- **Tasto destro** sul widget o sull'icona "IP" vicino all'orologio per il menu: schede da mostrare, dettagli da mostrare (IPv6, MAC, DHCP, Wi-Fi), tema chiaro/scuro, opacità, modalità compatta, ping, lingua, sempre in primo piano, avvio con Windows, **aggiornamenti** e **Informazioni su CorbolIp** (versione, autore, licenza e link al progetto).
- **Test velocità**: *Test velocità...* nel menu apre una finestra; *Avvia* misura latenza (con jitter), download e upload in circa 20 secondi, *Copia risultato* copia i valori negli appunti. Usa `speed.cloudflare.com`; nel campo *Server* si può indicare un altro server compatibile (solo indirizzo base, test completo) oppure l'indirizzo di un file da scaricare, ad esempio su un server aziendale (solo latenza e download).
- **Diagnosi rete**: *Diagnosi rete...* nel menu esegue i controlli in pochi secondi e mostra la causa probabile con cosa fare (per esempio *Problema DNS*, *Il router non risponde*, *Serve un accesso dal browser*). *Copia report* copia esiti e dettagli delle schede negli appunti, pronti per una segnalazione all'assistenza, con gli eventi di rete delle ultime 24 ore.
- **Registro eventi**: acceso di default, si spegne da *Registro eventi* nel menu, dove *Apri il registro* lo mostra. Annota solo quello che il widget vede già, senza traffico in più; le cadute di router e Internet solo con il ping attivo. File `%APPDATA%\CorbolIp\events.log`.
- **Assistente AI**: in *Assistente AI...* attivalo e scegli il servizio: l'**assistente locale** (predefinito: Qwen3.5 con llama.cpp sul PC, scaricato una volta sola, 1,2 o 2,6 GB secondo la RAM; se CorbolPad lo ha già scaricato usa quei file), Claude con la tua chiave API, **Claude Code** con il tuo abbonamento Claude (solo uso personale: CorbolIp avvia `claude -p` senza strumenti, senza server MCP e senza salvare la sessione, e gli passa solo il testo della diagnosi), oppure un servizio compatibile OpenAI (OpenAI, OpenRouter, Ollama, LM Studio). *Prova connessione* verifica che risponda. Se in una domanda o nella descrizione della segnalazione citi un sito o un indirizzo, CorbolIp lo controlla prima (DNS e risposta web, oppure ping) e passa i risultati all'assistente; con Claude o un servizio compatibile OpenAI l'assistente può anche chiedere da sé fino a 5 controlli (ping, percorso, porta TCP, DNS, risposta web, eventi recenti), mostrati nel riquadro mentre li esegue. L'assistente locale si avvia quando serve e si chiude dopo 5 minuti di inattività; la disinstallazione cancella anche i file scaricati da CorbolIp. Nella diagnosi compaiono *Spiega* (cosa significano i risultati e cosa fare), *Prepara segnalazione* (un testo per l'assistenza, a cui *Copia report* aggiunge i dati tecnici) e la casella *Chiedi* per fare domande, per esempio "perché Teams si blocca?": l'assistente ricorda la conversazione finché non premi *Nuova conversazione* o ripeti la diagnosi, e *Copia report* include anche spiegazioni e risposte. Le chiavi sono cifrate per l'utente Windows (DPAPI).
- **Aggiornamenti**: *Aggiornamenti → Controlla ora* confronta la versione con l'ultima release su GitHub e, se ce n'è una nuova, la scarica, ne verifica lo SHA-256 e avvia l'installer. Il controllo automatico (una volta al giorno) è disattivato di default.
- Fissata alla barra delle applicazioni, il tasto destro sull'icona offre *Mostra in primo piano*, *Sempre in primo piano* ed *Esci* (solo mentre l'app è in esecuzione).

### Installazione
1. Scarica `CorbolIp-Setup-X.Y.Z.exe` dalle [Releases](https://github.com/corbolma/CorbolIp/releases/latest).
2. Avvialo. **Non servono permessi di amministratore**: installa solo per l'utente corrente.
3. Se compare *"Windows ha protetto il PC"*, clicca **Ulteriori informazioni → Esegui comunque** (l'eseguibile non è firmato digitalmente).
4. Per fissarla alla barra: **Start → cerca CorbolIp → tasto destro → Aggiungi alla barra delle applicazioni**.

Installazione silenziosa (per l'IT): `CorbolIp-Setup-X.Y.Z.exe /silent` · Disinstallazione: Impostazioni → App, oppure `"%LOCALAPPDATA%\Programs\CorbolIp\Uninstall.exe" /uninstall /silent`.

Guida completa e domande frequenti nella [Wiki](https://github.com/corbolma/CorbolIp/wiki).

### Compilare dal sorgente
Non serve installare nulla: si usa il compilatore C# incluso in Windows (.NET Framework 4.x).

```bat
build.bat                      :: crea CorbolIp.exe
build-installer.bat            :: crea dist\CorbolIp-Setup-X.Y.Z.exe
build-installer.bat patch      :: incrementa la versione (1.0.1 -> 1.0.2) e crea l'installer
```

### Privacy
CorbolIp non raccoglie dati. Le uniche connessioni esterne sono la richiesta dell'IP pubblico (`api.ipify.org`, con `icanhazip.com` e `checkip.amazonaws.com` come riserva) e, solo se li attivi tu: il ping verso `1.1.1.1` / `8.8.8.8`, l'IP pubblico IPv6 (`api6.ipify.org`, riserva `ipv6.icanhazip.com`) e il controllo degli aggiornamenti (`api.github.com`). Il test velocità contatta `speed.cloudflare.com` (o il server indicato) solo quando premi *Avvia*. La diagnosi rete, solo quando la apri, fa ping al router e a `1.1.1.1` / `8.8.8.8`, una richiesta DNS e due richieste web (`www.msftconnecttest.com`, lo stesso controllo usato da Windows, e `www.cloudflare.com/cdn-cgi/trace`). L'assistente locale scarica modello e llama.cpp da `huggingface.co` e `github.com` solo quando premi *Scarica*, e poi lavora senza rete. Un assistente remoto, solo se lo scegli e solo quando premi *Spiega*, *Prepara segnalazione* o *Chiedi*, riceve i risultati della diagnosi (`api.anthropic.com` o l'indirizzo configurato); di default nome del PC, dominio, utente, rete Wi-Fi, MAC, IP pubblico e indirizzi IPv6 vengono sostituiti da segnaposti, e *Cosa viene inviato* mostra il testo esatto.

---

## English

**CorbolIp** is a small *conky*-style desktop widget and tray icon for Windows 10/11 that shows the computer name, IP addresses, gateway, DNS servers, MAC address, DHCP/static with lease time, public IP, optional IPv6, live network speed with a 60-second graph, Wi-Fi SSID, signal, band, channel, standard and link rate, and active VPN. On-demand **speed test** (latency, jitter, download, upload) via `speed.cloudflare.com` or a custom server/file URL. On-demand **network diagnostics** (adapter, IP, Wi-Fi, router, Internet, DNS, captive portal, HTTPS, VPN) that explains the likely cause in plain words and copies a report for IT support. Optional **AI assistant** (off by default) that explains the diagnostics, answers your questions about them and drafts a support request, by default with a local model (Qwen3.5 via llama.cpp on the PC: no API key, data stays on the PC, downloaded once on request), or with Claude (API key), Claude Code (your own Claude subscription, personal use; run without tools) or any OpenAI-compatible service. Optional ping to the gateway and the Internet (off by default). Local **network event log** (disconnections, network/IP/Wi-Fi/VPN changes, sleep/resume; last 30 days, no extra traffic).

- The widget sticks to the desktop (below other windows, survives Win+D) and can be dragged anywhere.
- Click any value to copy it. Right-click for options: adapters and details to show (IPv6, MAC, DHCP, Wi-Fi), light/dark theme, opacity, compact mode, ping interval, language, always on top, start with Windows, **updates** (check now, or automatically once a day — off by default; the installer is downloaded from GitHub Releases and its SHA-256 verified), and **About CorbolIp** (version, author, license and project link).
- **Install:** download `CorbolIp-Setup-X.Y.Z.exe` from [Releases](https://github.com/corbolma/CorbolIp/releases/latest). Per-user install, no admin rights required. The executable is not code-signed, so SmartScreen may ask you to confirm (*More info → Run anyway*). Silent install: `/silent`.
- **Build:** run `build-installer.bat` — it uses the C# compiler bundled with Windows (.NET Framework 4.x), no SDK needed.
- **Privacy:** no telemetry. The only outbound requests are the public-IP lookup and, only if you enable them, ICMP pings, the public IPv6 lookup and the update check (`api.github.com`); the speed test runs only when you press *Start*, and the network diagnostics only when you open it (pings, one DNS query, `www.msftconnecttest.com` and `www.cloudflare.com/cdn-cgi/trace`). The local assistant downloads its model and llama.cpp only when you press *Download*; a remote AI service receives the diagnostic results only when you press *Explain*, *Write support request* or *Ask*; by default computer name, domain, user, Wi-Fi network, MAC, public IP and IPv6 addresses are replaced by placeholders, and *What is sent* shows the exact text.

The user interface is available in **English and Italian**: it follows the Windows display language and can be changed from the menu (*Lingua / Language*).
**Translations are welcome**: adding a language is just a text file, no coding required — see [lang/README.md](lang/README.md).

## Componenti di terze parti / Third-party components
CorbolIp non include componenti di terze parti. L'assistente locale, solo se lo attivi e premi *Scarica*, scarica sul PC:

| Componente | Licenza | Da dove |
|---|---|---|
| [llama.cpp](https://github.com/ggml-org/llama.cpp) (`llama-server`), di ggml-org | [MIT](https://github.com/ggml-org/llama.cpp/blob/master/LICENSE) | release ufficiali su GitHub |
| Modelli [Qwen3.5](https://huggingface.co/Qwen) 2B o 4B, di Alibaba (quantizzazione GGUF di unsloth) | [Apache 2.0](https://www.apache.org/licenses/LICENSE-2.0) | Hugging Face |

Ogni file è controllato con il suo SHA-256. Claude, OpenAI e gli altri servizi compatibili sono usati solo tramite rete, con la chiave e secondo i termini d'uso di chi li configura.

CorbolIp does not bundle third-party components. The local assistant downloads them only when you turn it on and press *Download*: **llama.cpp** (`llama-server`, ggml-org, MIT) from the official GitHub releases and the **Qwen3.5** 2B or 4B models (Alibaba, Apache 2.0, GGUF quantization by unsloth) from Hugging Face; each file is verified with its SHA-256. Claude, OpenAI and other compatible services are only called over the network, with the key and under the terms of whoever sets them up.

## Licenza / License
[MIT](LICENSE) © 2026 Mauro Corboli

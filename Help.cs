// Guida: apre la pagina della wiki su GitHub nella lingua dell'app (italiano, altrimenti inglese), alla sezione giusta.
// Il manuale è la wiki: qui ci sono solo i collegamenti. Si apre nel browser solo quando l'utente lo chiede
// (voce Guida del menu, pulsante "?" nelle finestre, tasto F1).
// Compatibile con il compilatore csc di .NET Framework 4.x (C# 5).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CorbolIp
{
    static class Help
    {
        public const string Guide = "", Diagnostics = "diag", History = "history", SpeedTest = "speed", Assistant = "ai";
        const string Wiki = "https://github.com/corbolma/CorbolIp/wiki/";

        // Sezione della guida utente (ancore generate da GitHub dai titoli della wiki)
        static string Anchor(string topic, bool it)
        {
            switch (topic)
            {
                case Diagnostics: return it ? "diagnosi-rete" : "network-diagnostics";
                case History: return it ? "storico-rete" : "network-history";
                case SpeedTest: return it ? "test-velocità" : "speed-test";
                case Assistant: return it ? "assistente-ai" : "ai-assistant";
                default: return "";
            }
        }

        public static string Url(string topic)
        {
            bool it = L.Code == "it";
            string a = Anchor(topic, it);
            return Wiki + (it ? "Guida-utente" : "User-guide") + (a.Length > 0 ? "#" + Uri.EscapeDataString(a) : "");
        }

        public static void Open(string topic)
        {
            try { Process.Start(new ProcessStartInfo(Url(topic)) { UseShellExecute = true }); } catch { }
        }

        // Pulsante "?" (30x30 a 9 pt) e tasto F1 nella finestra
        public static Button Button(Form f, string topic, Point location)
        {
            var b = new Button { Text = "?", Size = new Size(30, 30), Location = location, TabStop = false };
            new ToolTip().SetToolTip(b, L.T("Guida (F1)", "Help (F1)"));
            b.Click += delegate { Open(topic); };
            f.HelpRequested += (s, e) => { Open(topic); e.Handled = true; };
            return b;
        }
    }
}

// Cambio di nome: fino alla versione 1.7.2 l'app si chiamava AppIP. Qui c'è ciò che serve per passare al
// nome nuovo senza perdere nulla:
//   - l'installer chiude la vecchia app, ne sposta i dati nelle cartelle nuove e toglie la vecchia installazione
//     (cartella del programma, collegamenti, avvio automatico, voce in App installate);
//   - l'app, all'avvio, sposta ciò che l'installer non ha potuto spostare perché in uso.
// Restano col nome vecchio solo identificativi interni invisibili (AppUserModelID, cifratura delle chiavi API,
// tipo di file registrato), così icone fissate, chiavi salvate e app predefinite continuano a funzionare.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace CorbolIp
{
    static class OldApp
    {
        public const string Name = "AppIP";
        const string NewName = "CorbolIp";
        // Sottocartelle di %LOCALAPPDATA% da non spostare (si cancellano e basta)
        static readonly string[] Skip = {  };

        static string Local { get { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Roaming { get { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); } }

        public static string InstallDir { get { return Path.Combine(Local, "Programs", Name); } }
        public static string ExePath { get { return Path.Combine(InstallDir, Name + ".exe"); } }
        public const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Name;
        public static string RoamingDir { get { return Path.Combine(Roaming, Name); } }
        public static string LocalDir { get { return Path.Combine(Local, Name); } }
        public static string StartMenuLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); } }
        public static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); } }
        public static string SendToLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), Name + ".lnk"); } }

        public static bool Installed { get { return File.Exists(ExePath); } }

        public static Version InstalledVersion()
        {
            if (!Installed) return null;
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(ExePath);
                return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { return new Version(0, 0, 0); }
        }

        // Scelta salvata dal vecchio installer nella sua chiave di disinstallazione (DWORD), o null
        public static int? Choice(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstKey))
                    if (k != null && k.GetValue(name) is int) return (int)k.GetValue(name);
            }
            catch { }
            return null;
        }

        public static bool StartupEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return k != null && k.GetValue(Name) != null;
            }
            catch { return false; }
        }

        static bool Running()
        {
            int session = Process.GetCurrentProcess().SessionId;
            return Process.GetProcessesByName(Name).Any(p => p.SessionId == session);
        }

        // All'avvio dell'app: se la vecchia app non è aperta, sposta i dati rimasti col nome vecchio
        public static void MoveLeftovers()
        {
            try { if (!Running() && (Directory.Exists(RoamingDir) || Directory.Exists(LocalDir))) MoveData(); } catch { }
        }

        // Sposta i dati nelle cartelle con il nome nuovo (%APPDATA% e %LOCALAPPDATA%). Quello che è in uso resta
        // dov'è e si ritenta al prossimo avvio; un file già presente nella cartella nuova non viene sovrascritto.
        public static void MoveData()
        {
            MoveContents(RoamingDir, Path.Combine(Roaming, NewName), false);
            MoveContents(LocalDir, Path.Combine(Local, NewName), true);
        }

        static void MoveContents(string from, string to, bool top)
        {
            try
            {
                if (!Directory.Exists(from)) return;
                if (top)
                    foreach (var s in Skip)
                        try { if (Directory.Exists(Path.Combine(from, s))) Directory.Delete(Path.Combine(from, s), true); } catch { }
                if (!Directory.Exists(to))
                {
                    try { Directory.Move(from, to); return; } catch { }
                }
                Directory.CreateDirectory(to);
                foreach (var f in Directory.GetFiles(from))
                    try
                    {
                        string d = Path.Combine(to, Path.GetFileName(f));
                        if (!File.Exists(d)) File.Move(f, d);
                    }
                    catch { }
                foreach (var d in Directory.GetDirectories(from))
                {
                    if (top && Skip.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
                    MoveContents(d, Path.Combine(to, Path.GetFileName(d)), false);
                }
                if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
            }
            catch { }
        }

        // Chiude la vecchia app se è aperta: con il suo comando di uscita se ne ha uno (salva tutto), altrimenti
        // chiudendo la finestra; dopo 10 secondi la chiude forzando.
        public static void CloseRunning(string exitEvent)
        {
            int session = Process.GetCurrentProcess().SessionId;
            Func<Process[]> mine = () => Process.GetProcessesByName(Name).Where(p => p.SessionId == session).ToArray();
            if (mine().Length == 0) return;
            if (exitEvent != null)
                try { EventWaitHandle.OpenExisting(exitEvent).Set(); } catch { }
            else
                foreach (var p in mine())
                    try { p.CloseMainWindow(); } catch { }
            var until = DateTime.Now.AddSeconds(10);
            while (mine().Length > 0 && DateTime.Now < until) Thread.Sleep(200);
            foreach (var p in mine())
                try { p.Kill(); p.WaitForExit(3000); } catch { }
        }

        // Toglie la vecchia installazione: collegamenti, avvio automatico, voce in App installate e cartella del
        // programma. Le voci di registro proprie di un'app (associazioni, menu di Esplora file) le toglie l'installer.
        public static void RemoveInstall()
        {
            foreach (var f in new[] { StartMenuLnk, DesktopLnk, SendToLnk })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    if (k != null) k.DeleteValue(Name, false);
            }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
            if (!Directory.Exists(InstallDir)) return;
            try { Directory.Delete(InstallDir, true); }
            catch
            {
                // qualche file ancora in uso: ci riprova un cmd qualche secondo dopo
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
                    {
                        CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetTempPath()
                    });
                }
                catch { }
            }
        }
    }
}

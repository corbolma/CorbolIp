// Versione condivisa da CorbolIp.exe e dall'installer. Cambiala qui prima di una nuova distribuzione.
using System.Reflection;

[assembly: AssemblyProduct("CorbolIp")]
[assembly: AssemblyCopyright(CorbolIp.AppVersion.Copyright)]
[assembly: AssemblyVersion("1.8.1.0")]
[assembly: AssemblyFileVersion("1.8.1.0")]

namespace CorbolIp
{
    static class AppVersion
    {
        // Autore e licenza: compaiono nelle proprietà degli exe e nella finestra dell'installer
        public const string Author = "Mauro Corboli";
        public const string Copyright = "Copyright © 2026 " + Author + " - MIT License";
        public const string RepoUrl = "https://github.com/corbolma/CorbolIp";
        public const string Text = "1.8.1";
    }
}

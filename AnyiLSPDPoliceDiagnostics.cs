using System;
using System.Collections.Generic;
using System.IO;

namespace AnyiLSPD
{
    public static class AnyiLSPDPoliceDiagnostics
    {
        private static readonly object Sync = new object();

        public static string GetDiagnosticPath(string scriptsDirectory)
        {
            string root = string.IsNullOrWhiteSpace(scriptsDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : scriptsDirectory;

            return Path.Combine(
                root,
                "AnyiLSPD_PoliceAuthority_Diagnostic.log");
        }

        public static void WriteReport(
            string scriptsDirectory,
            string title,
            IList<string> lines)
        {
            string path = GetDiagnosticPath(scriptsDirectory);

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                lock (Sync)
                {
                    using (FileStream stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(
                            "============================================================");
                        writer.WriteLine(
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                        writer.WriteLine(title ?? "ANYI LSPD DIAGNOSTIC");

                        if (lines != null)
                        {
                            foreach (string line in lines)
                                writer.WriteLine(line ?? string.Empty);
                        }

                        writer.WriteLine();
                        writer.Flush();
                    }
                }

                LspdResponseLog.Write(
                    "POLICE_DIAGNOSTIC",
                    "Diagnostic report written | Path=" + path);
            }
            catch (Exception ex)
            {
                try
                {
                    LspdResponseLog.WriteException(
                        "POLICE_DIAGNOSTIC_WRITE_ERROR",
                        ex);
                }
                catch
                {
                }
            }
        }
    }
}

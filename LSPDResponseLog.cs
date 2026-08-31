using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    // Compatibility shim.
    // The real file I/O lives inside LSPDMainUI so the same loaded DLL owns
    // the logging path. Existing cores can keep calling LspdResponseLog.
    public static class LspdResponseLog
    {
        public static string ScriptDirectory
        {
            get { return LSPDMainUI.EmbeddedScriptsDirectory; }
        }

        public static string RuntimeLogPath
        {
            get { return LSPDMainUI.EmbeddedRuntimeLogPath; }
        }

        public static string HeartbeatLogPath
        {
            get { return LSPDMainUI.EmbeddedHeartbeatLogPath; }
        }

        public static string ReportPath
        {
            get { return System.IO.Path.Combine(
                LSPDMainUI.EmbeddedScriptsDirectory,
                "AnyiLSPD_DiagnosticReport.log"); }
        }

        public static void EnsureInitialized()
        {
            LSPDMainUI.EnsureEmbeddedLogger();
        }

        public static void Write(string category, string message)
        {
            LSPDMainUI.WriteEmbeddedLog(category, message);
        }

        public static void WriteHeartbeat(string message)
        {
            LSPDMainUI.WriteEmbeddedHeartbeat(message);
        }

        public static void WriteException(string category, Exception exception)
        {
            LSPDMainUI.WriteEmbeddedException(category, exception);
        }

        public static void WriteReport(string title, IEnumerable<string> lines)
        {
            try
            {
                LSPDMainUI.WriteEmbeddedLog(
                    "REPORT",
                    "Report requested | " + (title ?? "LSPD RESPONSE REPORT"));

                string path = ReportPath;
                using (System.IO.FileStream stream = new System.IO.FileStream(
                    path,
                    System.IO.FileMode.Append,
                    System.IO.FileAccess.Write,
                    System.IO.FileShare.ReadWrite))
                using (System.IO.StreamWriter writer = new System.IO.StreamWriter(stream))
                {
                    writer.WriteLine();
                    writer.WriteLine("============================================================");
                    writer.WriteLine(title ?? "LSPD RESPONSE REPORT");
                    writer.WriteLine("Generated: " +
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    writer.WriteLine("ScriptDirectory: " + ScriptDirectory);
                    writer.WriteLine();

                    if (lines != null)
                    {
                        foreach (string line in lines)
                            writer.WriteLine(line ?? string.Empty);
                    }

                    writer.WriteLine("============================================================");
                    writer.Flush();
                }

                LSPDMainUI.WriteEmbeddedLog(
                    "REPORT",
                    "Report written | Path=" + path);
            }
            catch (Exception ex)
            {
                LSPDMainUI.WriteEmbeddedException(
                    "REPORT_WRITE_ERROR",
                    ex);
            }
        }

        public static void ForceFlushMarker(string reason)
        {
            LSPDMainUI.WriteEmbeddedLog(
                "FLUSH_MARKER",
                reason ?? "manual");
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;

namespace AnyiLSPD
{
    /// <summary>
    /// Resolves the REAL GTA V Enhanced installation and scripts directory.
    /// Never uses Assembly.Location as the primary path because SHVDN shadow-copies
    /// managed script assemblies into AppData\Local\assembly\dl3.
    /// </summary>
    public static class AnyiLSPDPathProvider
    {
        private static readonly object Sync = new object();
        private static string _gameRoot;
        private static string _scriptsDirectory;
        private static bool _resolved;

        public static string GameRoot
        {
            get { EnsureResolved(); return _gameRoot; }
        }

        public static string ScriptsDirectory
        {
            get { EnsureResolved(); return _scriptsDirectory; }
        }

        public static string GangDataDirectory
        {
            get { return Path.Combine(GameRoot, "gangModData"); }
        }

        public static string ChaosActivityDirectory
        {
            get { return Path.Combine(ScriptsDirectory, "ChaosResponse.GangActivity"); }
        }

        public static string ChaosAudioDirectory
        {
            get { return Path.Combine(ScriptsDirectory, "ChaosResponse.Audio"); }
        }

        public static string NAudioPath
        {
            get { return Path.Combine(ScriptsDirectory, "NAudio.dll"); }
        }

        public static string RuntimeLogPath
        {
            get { return Path.Combine(ScriptsDirectory, "AnyiLSPD_Runtime.log"); }
        }

        public static string HeartbeatLogPath
        {
            get { return Path.Combine(ScriptsDirectory, "AnyiLSPD_Heartbeat.log"); }
        }

        public static bool IsShadowCopyPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return false;
            }

            return full.IndexOf(
                Path.DirectorySeparatorChar + "AppData" + Path.DirectorySeparatorChar + "Local" +
                Path.DirectorySeparatorChar + "assembly" + Path.DirectorySeparatorChar + "dl3" +
                Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string DescribeResolution()
        {
            EnsureResolved();
            return "GameRoot=" + GameRoot +
                   " | Scripts=" + ScriptsDirectory +
                   " | GangData=" + GangDataDirectory +
                   " | ChaosActivity=" + ChaosActivityDirectory +
                   " | ChaosAudio=" + ChaosAudioDirectory +
                   " | NAudio=" + NAudioPath +
                   " | ShadowCopyScripts=" + IsShadowCopyPath(ScriptsDirectory);
        }

        private static void EnsureResolved()
        {
            if (_resolved)
                return;

            lock (Sync)
            {
                if (_resolved)
                    return;

                string gameRoot = TryResolveFromProcess();
                if (string.IsNullOrWhiteSpace(gameRoot))
                    gameRoot = TryResolveFromCurrentDirectory();
                if (string.IsNullOrWhiteSpace(gameRoot))
                    gameRoot = TryResolveFromParentSearch(AppDomain.CurrentDomain.BaseDirectory);
                if (string.IsNullOrWhiteSpace(gameRoot))
                    gameRoot = TryResolveFromParentSearch(Environment.CurrentDirectory);

                if (string.IsNullOrWhiteSpace(gameRoot))
                {
                    // Last-resort fallback only. We deliberately do NOT use the
                    // loaded assembly location because SHVDN shadow-copy may point
                    // to AppData\Local\assembly\dl3.
                    gameRoot = Environment.CurrentDirectory;
                }

                _gameRoot = Path.GetFullPath(gameRoot);
                _scriptsDirectory = Path.Combine(_gameRoot, "scripts");
                _resolved = true;
            }
        }

        private static string TryResolveFromProcess()
        {
            try
            {
                Process process = Process.GetCurrentProcess();
                ProcessModule module = process.MainModule;
                if (module == null || string.IsNullOrWhiteSpace(module.FileName))
                    return null;

                string directory = Path.GetDirectoryName(module.FileName);
                if (LooksLikeGtaRoot(directory))
                    return directory;
            }
            catch { }

            return null;
        }

        private static string TryResolveFromCurrentDirectory()
        {
            try
            {
                string current = Environment.CurrentDirectory;
                if (LooksLikeGtaRoot(current))
                    return current;
            }
            catch { }

            return null;
        }

        private static string TryResolveFromParentSearch(string start)
        {
            try
            {
                DirectoryInfo directory = new DirectoryInfo(start);
                for (int i = 0; i < 12 && directory != null; i++)
                {
                    if (LooksLikeGtaRoot(directory.FullName))
                        return directory.FullName;
                    directory = directory.Parent;
                }
            }
            catch { }

            return null;
        }

        private static bool LooksLikeGtaRoot(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            try
            {
                if (IsShadowCopyPath(directory))
                    return false;

                string scripts = Path.Combine(directory, "scripts");
                if (!Directory.Exists(scripts))
                    return false;

                // Prefer a real SHVDN installation marker. The user's Enhanced
                // setup is known to use ScriptHookVDotNet3.dll in the root.
                if (File.Exists(Path.Combine(directory, "ScriptHookVDotNet.asi")) ||
                    File.Exists(Path.Combine(directory, "ScriptHookVDotNet3.dll")))
                    return true;

                // Also accept an already-populated scripts directory as a fallback.
                return File.Exists(Path.Combine(scripts, "ScriptHookVDotNet3.dll")) ||
                       File.Exists(Path.Combine(scripts, "AnyiLSPDImmersiveResponse.dll"));
            }
            catch
            {
                return false;
            }
        }
    }
}

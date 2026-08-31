using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDChaosAudio : IDisposable
    {
        private object _waveOut;
        private object _audioReader;
        private Assembly _naudioAssembly;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private readonly AnyiLSPDPoliceConfig _config;
        private readonly Random _random = new Random();
        private float _masterVolume = 0.35f;
        private float _dispatchVolume = 0.30f;
        private float _backupVolume = 0.30f;
        private float _pursuitVolume = 0.35f;
        private float _gangActivityVolume = 0.30f;
        private float _successVolume = 0.25f;
        private bool _muted;

        public AnyiLSPDChaosAudio(AnyiLSPDPoliceConfig config)
        {
            _config = config;
            LoadUserAudioSettings();
        }

        public int MasterVolumePercent { get { return (int)Math.Round(_masterVolume * 100.0f); } }
        public int DispatchVolumePercent { get { return (int)Math.Round(_dispatchVolume * 100.0f); } }
        public bool Muted { get { return _muted; } }

        public string AudioStatusLine
        {
            get
            {
                return "Master=" + MasterVolumePercent + "% | Dispatch=" +
                       DispatchVolumePercent + "% | " +
                       (_muted ? "MUTED" : "Audio On");
            }
        }

        public string IncreaseMasterVolume(int stepPercent)
        {
            _masterVolume = Clamp01(_masterVolume + Math.Max(1, stepPercent) / 100.0f);
            SaveUserAudioSetting("ChaosAudioMasterVolume", _masterVolume);
            return "Chaos master volume: " + MasterVolumePercent + "%.";
        }

        public string DecreaseMasterVolume(int stepPercent)
        {
            _masterVolume = Clamp01(_masterVolume - Math.Max(1, stepPercent) / 100.0f);
            SaveUserAudioSetting("ChaosAudioMasterVolume", _masterVolume);
            return "Chaos master volume: " + MasterVolumePercent + "%.";
        }

        public string IncreaseDispatchVolume(int stepPercent)
        {
            _dispatchVolume = Clamp01(_dispatchVolume + Math.Max(1, stepPercent) / 100.0f);
            SaveUserAudioSetting("ChaosDispatchVolume", _dispatchVolume);
            return "Chaos dispatch volume: " + DispatchVolumePercent + "%.";
        }

        public string DecreaseDispatchVolume(int stepPercent)
        {
            _dispatchVolume = Clamp01(_dispatchVolume - Math.Max(1, stepPercent) / 100.0f);
            SaveUserAudioSetting("ChaosDispatchVolume", _dispatchVolume);
            return "Chaos dispatch volume: " + DispatchVolumePercent + "%.";
        }

        public string ToggleMute()
        {
            _muted = !_muted;
            SaveUserAudioSetting("ChaosAudioMuted", _muted);
            return _muted ? "Chaos Response audio muted." : "Chaos Response audio unmuted.";
        }

        public string ResetVolumeSettings()
        {
            _masterVolume = 0.35f;
            _dispatchVolume = 0.30f;
            _backupVolume = 0.30f;
            _pursuitVolume = 0.35f;
            _gangActivityVolume = 0.30f;
            _successVolume = 0.25f;
            _muted = false;
            SaveAllUserAudioSettings();
            return "Chaos audio levels restored to safe defaults. Master=35% | Dispatch=30%.";
        }

        public bool Play(string category)
        {
            if (!_config.EnableChaosAudio || DateTime.UtcNow < _cooldownUntil)
                return false;

            string path = FindAudio(category);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                LspdResponseLog.Write("POLICE_AUDIO", "No matching ChaosResponse audio | Category=" + category);
                return false;
            }

            try
            {
                StopCurrent();
                string naudioPath = AnyiLSPDPathProvider.NAudioPath;
                if (!File.Exists(naudioPath))
                {
                    LspdResponseLog.Write("POLICE_AUDIO", "NAudio.dll not found in scripts; audio file located but playback skipped | " + path);
                    return false;
                }

                _naudioAssembly = Assembly.LoadFrom(naudioPath);
                Type readerType = _naudioAssembly.GetType("NAudio.Wave.AudioFileReader");
                Type outputType = _naudioAssembly.GetType("NAudio.Wave.WaveOutEvent");
                if (readerType == null || outputType == null)
                    return false;

                _audioReader = Activator.CreateInstance(readerType, path);
                _waveOut = Activator.CreateInstance(outputType);

                ApplyReaderVolume(_audioReader, category);

                MethodInfo init = outputType.GetMethod("Init");
                MethodInfo play = outputType.GetMethod("Play");
                if (init == null || play == null)
                    return false;
                init.Invoke(_waveOut, new object[] { _audioReader });
                play.Invoke(_waveOut, null);
                _cooldownUntil = DateTime.UtcNow.AddSeconds(_config.AudioCooldownSeconds);
                LspdResponseLog.Write("POLICE_AUDIO", "Played ChaosResponse audio | Category=" + category + " | File=" + path);
                return true;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_AUDIO_ERROR", ex);
                StopCurrent();
                return false;
            }
        }

        public bool PlayFirstAvailable(string categories, bool ignoreCooldown)
        {
            if (!_config.EnableChaosAudio)
                return false;

            if (!ignoreCooldown && DateTime.UtcNow < _cooldownUntil)
                return false;

            if (string.IsNullOrWhiteSpace(categories))
                return false;

            string[] values = categories.Split(new[] { '|', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string value in values)
            {
                string category = value.Trim();
                if (category.Length == 0)
                    continue;

                string path = FindAudio(category);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;

                if (PlayPath(path, category, ignoreCooldown))
                    return true;
            }

            LspdResponseLog.Write("POLICE_AUDIO", "No configured success audio category matched | Categories=" + categories);
            return false;
        }

        private bool PlayPath(string path, string category, bool ignoreCooldown)
        {
            if (!ignoreCooldown && DateTime.UtcNow < _cooldownUntil)
                return false;

            try
            {
                StopCurrent();
                string naudioPath = AnyiLSPDPathProvider.NAudioPath;
                if (!File.Exists(naudioPath))
                {
                    LspdResponseLog.Write("POLICE_AUDIO", "NAudio.dll not found in scripts | Expected=" + naudioPath);
                    return false;
                }

                _naudioAssembly = Assembly.LoadFrom(naudioPath);
                Type readerType = _naudioAssembly.GetType("NAudio.Wave.AudioFileReader");
                Type outputType = _naudioAssembly.GetType("NAudio.Wave.WaveOutEvent");
                if (readerType == null || outputType == null)
                    return false;

                _audioReader = Activator.CreateInstance(readerType, path);
                _waveOut = Activator.CreateInstance(outputType);

                ApplyReaderVolume(_audioReader, category);

                MethodInfo init = outputType.GetMethod("Init");
                MethodInfo play = outputType.GetMethod("Play");
                if (init == null || play == null)
                    return false;

                init.Invoke(_waveOut, new object[] { _audioReader });
                play.Invoke(_waveOut, null);
                _cooldownUntil = DateTime.UtcNow.AddSeconds(_config.AudioCooldownSeconds);
                LspdResponseLog.Write("POLICE_AUDIO", "Played success audio | Category=" + category + " | File=" + path);
                return true;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_AUDIO_SUCCESS_ERROR", ex);
                StopCurrent();
                return false;
            }
        }

        public string TestDispatchAudio()
        {
            if (!_config.EnableChaosAudio)
                return "ChaosResponse audio is disabled in AnyiLSPDPolice.ini.";

            return Play("ATTENTION_ALL_UNITS")
                ? "ChaosResponse dispatch audio test played successfully."
                : "ChaosResponse dispatch audio test failed. Check AnyiLSPD_Runtime.log and the ChaosResponse.Audio folder.";
        }

        public string TestAudioPathResolution()
        {
            string configured = _config.ChaosAudioRoot ?? string.Empty;
            string scripts = AnyiLSPDPathProvider.ScriptsDirectory ?? string.Empty;
            string authoritative = string.IsNullOrWhiteSpace(scripts)
                ? string.Empty
                : Path.Combine(scripts, "ChaosResponse.Audio");

            return "ChaosAudio configured=" + configured +
                   " | scripts=" + scripts +
                   " | authoritative=" + authoritative;
        }

        private string FindAudio(string category)
        {
            string requested = category ?? string.Empty;

            // The real GTA scripts directory is authoritative. If an older INI
            // accidentally preserved an AppData assembly path, fall back here.
            string scriptsRoot = AnyiLSPDPathProvider.ScriptsDirectory;
            string configuredRoot = _config.ChaosAudioRoot;

            string[] roots = new[]
            {
                configuredRoot,
                string.IsNullOrWhiteSpace(scriptsRoot)
                    ? null
                    : Path.Combine(scriptsRoot, "ChaosResponse.Audio")
            };

            List<string> categories = new List<string>();
            if (!string.IsNullOrWhiteSpace(requested))
                categories.Add(requested);

            // Semantic aliases used by the Police Authority layer.
            if (string.Equals(requested, "REPORT_SUSPECT_IN_CUSTODY", StringComparison.OrdinalIgnoreCase))
            {
                categories.Add("CRIME_OFFICER_REQUESTS_TRANSPORT");
                categories.Add("ATTENTION_ALL_UNITS");
            }
            else if (string.Equals(requested, "UNIT_CLEAR", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(requested, "CASE_CLOSED", StringComparison.OrdinalIgnoreCase))
            {
                categories.Add("ATTENTION_ALL_UNITS");
            }
            else if (string.Equals(requested, "PRISONER_TRANSFER", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(requested, "PRISONER_TRANSPORT", StringComparison.OrdinalIgnoreCase))
            {
                categories.Add("CRIME_OFFICER_REQUESTS_TRANSPORT");
                categories.Add("UNIT_RESPONDING_DISPATCH");
            }

            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) ||
                    !Directory.Exists(root))
                    continue;

                List<string> all = Directory.GetFiles(
                        root,
                        "*.wav",
                        SearchOption.AllDirectories)
                    .ToList();

                foreach (string wanted in categories)
                {
                    List<string> matches = all
                        .Where(p =>
                            Path.GetFileNameWithoutExtension(p)
                                .IndexOf(
                                    wanted,
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();

                    if (matches.Count > 0)
                    {
                        if (!string.Equals(
                                requested,
                                wanted,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            LspdResponseLog.Write(
                                "POLICE_AUDIO",
                                "Audio alias resolved | Requested=" +
                                requested +
                                " | Actual=" +
                                wanted);
                        }

                        if (!string.Equals(
                                root,
                                configuredRoot,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            LspdResponseLog.Write(
                                "POLICE_AUDIO_PATH_FALLBACK",
                                "Configured ChaosAudioRoot was unavailable/stale; " +
                                "using real scripts directory | Root=" +
                                root);
                        }

                        return matches[_random.Next(matches.Count)];
                    }
                }
            }

            LspdResponseLog.Write(
                "POLICE_AUDIO",
                "No WAV matched category | Category=" +
                requested +
                " | ConfiguredRoot=" +
                configuredRoot +
                " | ScriptsRoot=" +
                scriptsRoot);

            return null;
        }


        private void ApplyReaderVolume(object reader, string category)
        {
            if (reader == null) return;
            try
            {
                PropertyInfo volumeProperty = reader.GetType().GetProperty("Volume");
                if (volumeProperty == null || !volumeProperty.CanWrite) return;

                float categoryVolume = GetCategoryVolume(category);
                float effective = Clamp01(_muted ? 0.0f : _masterVolume * categoryVolume);
                volumeProperty.SetValue(reader, effective, null);

                LspdResponseLog.Write(
                    "POLICE_AUDIO_VOLUME",
                    "Category=" + category +
                    " | Master=" + MasterVolumePercent + "%" +
                    " | CategoryVolume=" + (int)Math.Round(categoryVolume * 100.0f) + "%" +
                    " | Effective=" + (int)Math.Round(effective * 100.0f) + "%" +
                    " | Muted=" + _muted);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_AUDIO_VOLUME_ERROR", ex);
            }
        }

        private float GetCategoryVolume(string category)
        {
            string value = category ?? string.Empty;
            if (value.IndexOf("REQUEST_BACKUP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("ASSISTANCE_REQUIRED", StringComparison.OrdinalIgnoreCase) >= 0)
                return _backupVolume;
            if (value.IndexOf("PURSUIT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("HELI", StringComparison.OrdinalIgnoreCase) >= 0)
                return _pursuitVolume;
            if (value.IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("UNIT_CLEAR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("CASE_CLOSED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("REPORT_SUSPECT_IN_CUSTODY", StringComparison.OrdinalIgnoreCase) >= 0)
                return _successVolume;
            if (value.IndexOf("GANG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("CRIME", StringComparison.OrdinalIgnoreCase) >= 0)
                return _gangActivityVolume;
            return _dispatchVolume;
        }

        private static float Clamp01(float value)
        {
            if (value < 0.0f) return 0.0f;
            if (value > 1.0f) return 1.0f;
            return value;
        }

        private string GetPoliceIniPath()
        {
            try
            {
                string scriptDirectory = AnyiLSPDPathProvider.ScriptsDirectory;

                if (!string.IsNullOrWhiteSpace(scriptDirectory))
                {
                    return Path.Combine(
                        scriptDirectory,
                        AnyiLSPDPoliceConfig.FileName);
                }
            }
            catch { }

            return null;
        }
        private void LoadUserAudioSettings()
        {
            string path = GetPoliceIniPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            try
            {
                bool inSection = false;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inSection = string.Equals(line.Trim('[', ']'), "ChaosResponseAudio", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection || line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    float f;
                    bool b;
                    if (string.Equals(key, "ChaosAudioMasterVolume", StringComparison.OrdinalIgnoreCase) &&
                        float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out f))
                        _masterVolume = Clamp01(f);
                    else if (string.Equals(key, "ChaosDispatchVolume", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out f))
                        _dispatchVolume = Clamp01(f);
                    else if (string.Equals(key, "ChaosBackupVolume", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out f))
                        _backupVolume = Clamp01(f);
                    else if (string.Equals(key, "ChaosPursuitVolume", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out f))
                        _pursuitVolume = Clamp01(f);
                    else if (string.Equals(key, "ChaosGangActivityVolume", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out f))
                        _gangActivityVolume = Clamp01(f);
                    else if (string.Equals(key, "ChaosSuccessVolume", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out f))
                        _successVolume = Clamp01(f);
                    else if (string.Equals(key, "ChaosAudioMuted", StringComparison.OrdinalIgnoreCase) &&
                             bool.TryParse(value, out b))
                        _muted = b;
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_AUDIO_CONFIG_LOAD_ERROR", ex);
            }
        }

        private void SaveAllUserAudioSettings()
        {
            SaveUserAudioSetting("ChaosAudioMasterVolume", _masterVolume);
            SaveUserAudioSetting("ChaosDispatchVolume", _dispatchVolume);
            SaveUserAudioSetting("ChaosBackupVolume", _backupVolume);
            SaveUserAudioSetting("ChaosPursuitVolume", _pursuitVolume);
            SaveUserAudioSetting("ChaosGangActivityVolume", _gangActivityVolume);
            SaveUserAudioSetting("ChaosSuccessVolume", _successVolume);
            SaveUserAudioSetting("ChaosAudioMuted", _muted);
        }

        private void SaveUserAudioSetting(string key, float value)
        {
            SaveUserAudioSetting(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private void SaveUserAudioSetting(string key, bool value)
        {
            SaveUserAudioSetting(key, value ? "true" : "false");
        }

        private void SaveUserAudioSetting(string key, string value)
        {
            string path = GetPoliceIniPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                List<string> lines = File.Exists(path)
                    ? File.ReadAllLines(path).ToList()
                    : new List<string>();

                int sectionStart = -1, sectionEnd = lines.Count;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = (lines[i] ?? string.Empty).Trim();
                    if (string.Equals(t, "[ChaosResponseAudio]", StringComparison.OrdinalIgnoreCase))
                    {
                        sectionStart = i;
                        for (int j = i + 1; j < lines.Count; j++)
                        {
                            string next = (lines[j] ?? string.Empty).Trim();
                            if (next.StartsWith("[") && next.EndsWith("]"))
                            {
                                sectionEnd = j;
                                break;
                            }
                        }
                        break;
                    }
                }

                if (sectionStart < 0)
                {
                    if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                        lines.Add(string.Empty);
                    lines.Add("[ChaosResponseAudio]");
                    lines.Add(key + "=" + value);
                }
                else
                {
                    bool replaced = false;
                    for (int i = sectionStart + 1; i < sectionEnd; i++)
                    {
                        string t = (lines[i] ?? string.Empty).Trim();
                        int eq = t.IndexOf('=');
                        if (eq <= 0) continue;
                        string existingKey = t.Substring(0, eq).Trim();
                        if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = existingKey + "=" + value;
                            replaced = true;
                            break;
                        }
                    }
                    if (!replaced)
                        lines.Insert(sectionEnd, key + "=" + value);
                }

                File.WriteAllLines(path, lines);
                LspdResponseLog.Write("POLICE_AUDIO_CONFIG", "Saved " + key + "=" + value + " | INI=" + path);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_AUDIO_CONFIG_SAVE_ERROR", ex);
            }
        }

        private void StopCurrent()
        {
            try
            {
                if (_waveOut != null)
                {
                    MethodInfo stop = _waveOut.GetType().GetMethod("Stop");
                    if (stop != null) stop.Invoke(_waveOut, null);
                    IDisposable d = _waveOut as IDisposable;
                    if (d != null) d.Dispose();
                }
            }
            catch { }
            try
            {
                IDisposable d = _audioReader as IDisposable;
                if (d != null) d.Dispose();
            }
            catch { }
            _waveOut = null;
            _audioReader = null;
        }

        public void Dispose()
        {
            StopCurrent();
        }
    }
}

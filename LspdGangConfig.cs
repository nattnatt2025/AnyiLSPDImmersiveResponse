using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace AnyiLSPD
{
    public sealed class LspdGangConfig
    {
        public const string FileName = "LSPDResponse.Gang.xml";

        public bool Enabled = true;
        public string GangRoot = "";
        public int CoreTickMs = 250;
        public int StateScanMs = 900;
        public int NearbyScanMs = 1500;
        public int PoliceReactionMs = 1800;
        public int DataRefreshSeconds = 30;
        public int AggressionMemorySeconds = 8;
        public int LowWantedMaximum = 2;
        public int PoliceWaryRadius = 200;
        public int TerritoryWaryRadius = 100;
        public int GangInteractionRadius = 35;
        public int GangProtectionRadius = 70;
        public int EnemyDetectionRadius = 65;
        public int CitizenWaryRadius = 35;
        public int MaxProtectorsPerThreat = 3;
        public int PoliceTasksPerScan = 3;
        public int GangTasksPerScan = 3;
        public int TaskCooldownSeconds = 8;
        public int CalmDeescalationCooldownSeconds = 6;
        public bool EnablePoliceWary = true;
        public bool EnableGangProtection = true;
        public bool EnableCitizenWary = true;
        public bool EnableMilitaryResponse = false;

        // Gang Leader stability / protection controls.
        public bool PreventGangLeaderArrest = true;
        public bool EnableGangSupportSpawn = true;
        public int GangSupportSpawnRadius = 35;
        public int GangSupportCooldownSeconds = 12;
        public int MaxSpawnedGangSupport = 2;
        public int GangSupportHealth = 180;
        public int GangSupportArmor = 100;
        public int GangSupportAccuracy = 85;
        public string GangSupportWeapon = "CarbineRifleMk2";
        public int GangPursuitSearchRadius = 100;

        // Gang Turf 1.1 stability / roleplay controls.
        public string PreferredTurfName = "Davis";
        public string PreferredPlayerGangName = "Anyiii\'s Gang";
        public int DefaultTurfRadius = 180;
        public int PursuitBreakDistance = 300;
        public int PursuitBreakDelaySeconds = 6;
        public bool KeepPoliceNeutralDuringGangConflict = true;
        public bool PoliceMayTargetLeaderAfterPersonalAggression = true;

        public static LspdGangConfig LoadOrCreate(string scriptsDirectory)
        {
            LspdGangConfig config = new LspdGangConfig();
            string path = Path.Combine(scriptsDirectory, FileName);

            try
            {
                if (!File.Exists(path))
                {
                    Save(path, config);
                    LspdResponseLog.Write("GANG_CONFIG", "Created " + FileName);
                    return config;
                }

                XDocument doc = XDocument.Load(path);
                XElement root = doc.Root;
                if (root == null || root.Name != "LspdGangProfile")
                {
                    LspdResponseLog.Write("GANG_CONFIG_WARNING", "Invalid root in " + FileName + "; using defaults.");
                    return config;
                }

                config.Enabled = ReadBool(root, "enabled", config.Enabled);
                XElement data = root.Element("Data");
                if (data != null)
                    config.GangRoot = ReadString(data, "gangRoot", config.GangRoot);
                XElement perf = root.Element("Performance");
                if (perf != null)
                {
                    config.CoreTickMs = ReadInt(perf, "coreTickMs", config.CoreTickMs);
                    config.StateScanMs = ReadInt(perf, "stateScanMs", config.StateScanMs);
                    config.NearbyScanMs = ReadInt(perf, "nearbyScanMs", config.NearbyScanMs);
                    config.PoliceReactionMs = ReadInt(perf, "policeReactionMs", config.PoliceReactionMs);
                    config.DataRefreshSeconds = ReadInt(perf, "dataRefreshSeconds", config.DataRefreshSeconds);
                }

                XElement behavior = root.Element("GangBehavior");
                if (behavior != null)
                {
                    config.AggressionMemorySeconds = ReadInt(behavior, "aggressionMemorySeconds", config.AggressionMemorySeconds);
                    config.LowWantedMaximum = ReadInt(behavior, "lowWantedMaximum", config.LowWantedMaximum);
                    config.PoliceWaryRadius = ReadInt(behavior, "policeWaryRadius", config.PoliceWaryRadius);
                    config.TerritoryWaryRadius = ReadInt(behavior, "territoryWaryRadius", config.TerritoryWaryRadius);
                    config.GangInteractionRadius = ReadInt(behavior, "gangInteractionRadius", config.GangInteractionRadius);
                    config.GangProtectionRadius = ReadInt(behavior, "gangProtectionRadius", config.GangProtectionRadius);
                    config.EnemyDetectionRadius = ReadInt(behavior, "enemyDetectionRadius", config.EnemyDetectionRadius);
                    config.CitizenWaryRadius = ReadInt(behavior, "citizenWaryRadius", config.CitizenWaryRadius);
                    config.MaxProtectorsPerThreat = ReadInt(behavior, "maxProtectorsPerThreat", config.MaxProtectorsPerThreat);
                    config.PoliceTasksPerScan = ReadInt(behavior, "policeTasksPerScan", config.PoliceTasksPerScan);
                    config.GangTasksPerScan = ReadInt(behavior, "gangTasksPerScan", config.GangTasksPerScan);
                    config.TaskCooldownSeconds = ReadInt(behavior, "taskCooldownSeconds", config.TaskCooldownSeconds);
                    config.CalmDeescalationCooldownSeconds = ReadInt(behavior, "calmDeescalationCooldownSeconds", config.CalmDeescalationCooldownSeconds);
                    config.EnablePoliceWary = ReadBool(behavior, "enablePoliceWary", config.EnablePoliceWary);
                    config.EnableGangProtection = ReadBool(behavior, "enableGangProtection", config.EnableGangProtection);
                    config.EnableCitizenWary = ReadBool(behavior, "enableCitizenWary", config.EnableCitizenWary);
                    config.EnableMilitaryResponse = ReadBool(behavior, "enableMilitaryResponse", config.EnableMilitaryResponse);
                    config.PreventGangLeaderArrest = ReadBool(behavior, "preventGangLeaderArrest", config.PreventGangLeaderArrest);
                    config.EnableGangSupportSpawn = ReadBool(behavior, "enableGangSupportSpawn", config.EnableGangSupportSpawn);
                    config.GangSupportSpawnRadius = ReadInt(behavior, "gangSupportSpawnRadius", config.GangSupportSpawnRadius);
                    config.GangSupportCooldownSeconds = ReadInt(behavior, "gangSupportCooldownSeconds", config.GangSupportCooldownSeconds);
                    config.MaxSpawnedGangSupport = ReadInt(behavior, "maxSpawnedGangSupport", config.MaxSpawnedGangSupport);
                    config.GangSupportHealth = ReadInt(behavior, "gangSupportHealth", config.GangSupportHealth);
                    config.GangSupportArmor = ReadInt(behavior, "gangSupportArmor", config.GangSupportArmor);
                    config.GangSupportAccuracy = ReadInt(behavior, "gangSupportAccuracy", config.GangSupportAccuracy);
                    config.GangSupportWeapon = ReadString(behavior, "gangSupportWeapon", config.GangSupportWeapon);
                    config.GangPursuitSearchRadius = ReadInt(behavior, "gangPursuitSearchRadius", config.GangPursuitSearchRadius);
                    config.PreferredTurfName = ReadString(behavior, "preferredTurfName", config.PreferredTurfName);
                    config.PreferredPlayerGangName = ReadString(behavior, "preferredPlayerGangName", config.PreferredPlayerGangName);
                    config.DefaultTurfRadius = ReadInt(behavior, "defaultTurfRadius", config.DefaultTurfRadius);
                    config.PursuitBreakDistance = ReadInt(behavior, "pursuitBreakDistance", config.PursuitBreakDistance);
                    config.PursuitBreakDelaySeconds = ReadInt(behavior, "pursuitBreakDelaySeconds", config.PursuitBreakDelaySeconds);
                    config.KeepPoliceNeutralDuringGangConflict = ReadBool(behavior, "keepPoliceNeutralDuringGangConflict", config.KeepPoliceNeutralDuringGangConflict);
                    config.PoliceMayTargetLeaderAfterPersonalAggression = ReadBool(behavior, "policeMayTargetLeaderAfterPersonalAggression", config.PoliceMayTargetLeaderAfterPersonalAggression);
                }

                Clamp(config);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("GANG_CONFIG_ERROR", ex);
            }

            return config;
        }

        public static void Save(string path, LspdGangConfig config)
        {
            XDocument doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("LspdGangProfile",
                    new XAttribute("version", "1.0"),
                    new XAttribute("enabled", config.Enabled),
                    new XElement("Performance",
                        new XAttribute("coreTickMs", config.CoreTickMs),
                        new XAttribute("stateScanMs", config.StateScanMs),
                        new XAttribute("nearbyScanMs", config.NearbyScanMs),
                        new XAttribute("policeReactionMs", config.PoliceReactionMs),
                        new XAttribute("dataRefreshSeconds", config.DataRefreshSeconds)),
                    new XElement("GangBehavior",
                        new XAttribute("aggressionMemorySeconds", config.AggressionMemorySeconds),
                        new XAttribute("lowWantedMaximum", config.LowWantedMaximum),
                        new XAttribute("policeWaryRadius", config.PoliceWaryRadius),
                        new XAttribute("territoryWaryRadius", config.TerritoryWaryRadius),
                        new XAttribute("gangInteractionRadius", config.GangInteractionRadius),
                        new XAttribute("gangProtectionRadius", config.GangProtectionRadius),
                        new XAttribute("enemyDetectionRadius", config.EnemyDetectionRadius),
                        new XAttribute("citizenWaryRadius", config.CitizenWaryRadius),
                        new XAttribute("maxProtectorsPerThreat", config.MaxProtectorsPerThreat),
                        new XAttribute("policeTasksPerScan", config.PoliceTasksPerScan),
                        new XAttribute("gangTasksPerScan", config.GangTasksPerScan),
                        new XAttribute("taskCooldownSeconds", config.TaskCooldownSeconds),
                        new XAttribute("calmDeescalationCooldownSeconds", config.CalmDeescalationCooldownSeconds),
                        new XAttribute("enablePoliceWary", config.EnablePoliceWary),
                        new XAttribute("enableGangProtection", config.EnableGangProtection),
                        new XAttribute("enableCitizenWary", config.EnableCitizenWary),
                        new XAttribute("enableMilitaryResponse", config.EnableMilitaryResponse),
                        new XAttribute("preventGangLeaderArrest", config.PreventGangLeaderArrest),
                        new XAttribute("enableGangSupportSpawn", config.EnableGangSupportSpawn),
                        new XAttribute("gangSupportSpawnRadius", config.GangSupportSpawnRadius),
                        new XAttribute("gangSupportCooldownSeconds", config.GangSupportCooldownSeconds),
                        new XAttribute("maxSpawnedGangSupport", config.MaxSpawnedGangSupport),
                        new XAttribute("gangSupportHealth", config.GangSupportHealth),
                        new XAttribute("gangSupportArmor", config.GangSupportArmor),
                        new XAttribute("gangSupportAccuracy", config.GangSupportAccuracy),
                        new XAttribute("gangSupportWeapon", config.GangSupportWeapon),
                        new XAttribute("gangPursuitSearchRadius", config.GangPursuitSearchRadius),
                        new XAttribute("preferredTurfName", config.PreferredTurfName),
                        new XAttribute("preferredPlayerGangName", config.PreferredPlayerGangName),
                        new XAttribute("defaultTurfRadius", config.DefaultTurfRadius),
                        new XAttribute("pursuitBreakDistance", config.PursuitBreakDistance),
                        new XAttribute("pursuitBreakDelaySeconds", config.PursuitBreakDelaySeconds),
                        new XAttribute("keepPoliceNeutralDuringGangConflict", config.KeepPoliceNeutralDuringGangConflict),
                        new XAttribute("policeMayTargetLeaderAfterPersonalAggression", config.PoliceMayTargetLeaderAfterPersonalAggression)),
                    new XElement("Data",
                        new XAttribute("readOnly", "true"),
                        new XAttribute("gangRoot", string.IsNullOrWhiteSpace(config.GangRoot) ?
                            "C:\\Users\\Nataniel\\STEAM\\steamapps\\common\\Grand Theft Auto V Enhanced\\gangModData" :
                            config.GangRoot))));

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            doc.Save(path);
        }

        private static void Clamp(LspdGangConfig c)
        {
            c.CoreTickMs = Math.Max(100, c.CoreTickMs);
            c.StateScanMs = Math.Max(300, c.StateScanMs);
            c.NearbyScanMs = Math.Max(500, c.NearbyScanMs);
            c.PoliceReactionMs = Math.Max(750, c.PoliceReactionMs);
            c.DataRefreshSeconds = Math.Max(5, c.DataRefreshSeconds);
            c.AggressionMemorySeconds = Math.Max(2, c.AggressionMemorySeconds);
            c.LowWantedMaximum = Math.Max(0, Math.Min(2, c.LowWantedMaximum));
            c.PoliceWaryRadius = Math.Max(50, Math.Min(350, c.PoliceWaryRadius));
            c.TerritoryWaryRadius = Math.Max(50, Math.Min(200, c.TerritoryWaryRadius));
            c.GangInteractionRadius = Math.Max(10, Math.Min(80, c.GangInteractionRadius));
            c.GangProtectionRadius = Math.Max(25, Math.Min(120, c.GangProtectionRadius));
            c.EnemyDetectionRadius = Math.Max(20, Math.Min(120, c.EnemyDetectionRadius));
            c.CitizenWaryRadius = Math.Max(15, Math.Min(70, c.CitizenWaryRadius));
            c.MaxProtectorsPerThreat = Math.Max(1, Math.Min(5, c.MaxProtectorsPerThreat));
            c.PoliceTasksPerScan = Math.Max(1, Math.Min(5, c.PoliceTasksPerScan));
            c.GangTasksPerScan = Math.Max(1, Math.Min(5, c.GangTasksPerScan));
            c.TaskCooldownSeconds = Math.Max(3, Math.Min(30, c.TaskCooldownSeconds));
            c.CalmDeescalationCooldownSeconds = Math.Max(2, Math.Min(20, c.CalmDeescalationCooldownSeconds));
            c.DefaultTurfRadius = Math.Max(100, Math.Min(300, c.DefaultTurfRadius));
            c.PursuitBreakDistance = Math.Max(100, Math.Min(500, c.PursuitBreakDistance));
            c.PursuitBreakDelaySeconds = Math.Max(2, Math.Min(15, c.PursuitBreakDelaySeconds));
            c.GangSupportSpawnRadius = Math.Max(15, Math.Min(60, c.GangSupportSpawnRadius));
            c.GangSupportCooldownSeconds = Math.Max(5, Math.Min(60, c.GangSupportCooldownSeconds));
            c.MaxSpawnedGangSupport = Math.Max(1, Math.Min(4, c.MaxSpawnedGangSupport));
            c.GangSupportHealth = Math.Max(100, Math.Min(300, c.GangSupportHealth));
            c.GangSupportArmor = Math.Max(0, Math.Min(200, c.GangSupportArmor));
            c.GangSupportAccuracy = Math.Max(25, Math.Min(100, c.GangSupportAccuracy));
            c.GangPursuitSearchRadius = Math.Max(50, Math.Min(200, c.GangPursuitSearchRadius));
            if (string.IsNullOrWhiteSpace(c.PreferredTurfName)) c.PreferredTurfName = "Davis";
            if (string.IsNullOrWhiteSpace(c.PreferredPlayerGangName)) c.PreferredPlayerGangName = "Anyiii's Gang";
        }

        private static bool ReadBool(XElement e, string name, bool fallback)
        {
            bool value;
            return bool.TryParse((string)e.Attribute(name), out value) ? value : fallback;
        }

        private static string ReadString(XElement e, string name, string fallback)
        {
            XAttribute value = e.Attribute(name);
            return value == null || string.IsNullOrWhiteSpace(value.Value) ? fallback : value.Value.Trim();
        }

        private static int ReadInt(XElement e, string name, int fallback)
        {
            int value;
            return int.TryParse((string)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }
    }
}

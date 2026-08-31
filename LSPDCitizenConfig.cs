using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace AnyiLSPD
{
    // Citizen-mode configuration. Every value is deliberately conservative:
    // it limits scans, support officers, and task refreshes from the start.
    public sealed class LspdCitizenConfig
    {
        public const string FileName = "LSPDResponse.Citizen.xml";

        public bool Enabled = true;
        public int CoreTickMs = 250;
        public int RoleRefreshMs = 1500;
        public int StateScanMs = 750;
        public int NearbyPedScanMs = 1500;
        public int PoliceReactionMs = 1800;
        public int ConfigReloadSeconds = 10;
        public int GangDataRefreshSeconds = 30;
        public int HeartbeatSeconds = 30;

        public int MildWantedMaximum = 2;
        // Continuous chaos is the deliberate escalation gate in Citizen mode.
        // Wanted levels below this threshold remain investigative/observational.
        public int ContinuousChaosWantedLevel = 3;
        public int AggressionMemorySeconds = 8;
        public int AssuranceSeconds = 22;
        public int AssuranceDismissalSeconds = 4;
        public int ThreatMemorySeconds = 14;
        public int SupportUnitCooldownSeconds = 30;
        public int SupportUnitLifetimeSeconds = 30;
        public int SupportUnitCount = 3;
        public int SupportOfficerHealth = 500;
        public int SupportOfficerArmor = 200;
        public int SupportOfficerAccuracy = 100;
        public string SupportOfficerWeapon = "HeavyRifle";

        public float ThreatRadius = 45.0f;
        public float PoliceAssistRadius = 90.0f;
        public float SupportSpawnDistance = 58.0f;
        public float RecklessImpactSpeed = 20.0f;

        public bool EnableMildWantedInvestigation = true;
        public bool SpawnSupportOfficerWhenThreatened = true;
        public string SupportOfficerModel = "s_m_y_cop_01";
        public string GangDataRoot =
            @"C:\Users\Nataniel\STEAM\steamapps\common\Grand Theft Auto V Enhanced\gangModData";

        public static LspdCitizenConfig LoadOrCreate(string scriptDirectory)
        {
            string path = Path.Combine(scriptDirectory, FileName);
            LspdCitizenConfig config = new LspdCitizenConfig();

            try
            {
                if (!File.Exists(path))
                {
                    SaveDefault(path, config);
                    LspdResponseLog.Write("CITIZEN_CONFIG", "Created " + FileName);
                    return config;
                }

                XDocument document = XDocument.Load(path);
                XElement root = document.Root;
                if (root == null || root.Name != "LspdCitizenProfile")
                {
                    LspdResponseLog.Write(
                        "CITIZEN_CONFIG_WARNING",
                        "Invalid root in " + FileName + "; safe defaults kept.");
                    return config;
                }

                config.Enabled = ReadBool(root, "enabled", config.Enabled);

                XElement performance = root.Element("Performance");
                if (performance != null)
                {
                    config.CoreTickMs = ReadInt(performance, "coreTickMs", config.CoreTickMs);
                    config.RoleRefreshMs = ReadInt(performance, "roleRefreshMs", config.RoleRefreshMs);
                    config.StateScanMs = ReadInt(performance, "stateScanMs", config.StateScanMs);
                    config.NearbyPedScanMs = ReadInt(performance, "nearbyPedScanMs", config.NearbyPedScanMs);
                    config.PoliceReactionMs = ReadInt(performance, "policeReactionMs", config.PoliceReactionMs);
                    config.ConfigReloadSeconds = ReadInt(performance, "configReloadSeconds", config.ConfigReloadSeconds);
                    config.GangDataRefreshSeconds = ReadInt(performance, "gangDataRefreshSeconds", config.GangDataRefreshSeconds);
                    config.HeartbeatSeconds = ReadInt(performance, "heartbeatSeconds", config.HeartbeatSeconds);
                }

                XElement investigation = root.Element("Investigation");
                if (investigation != null)
                {
                    config.MildWantedMaximum = ReadInt(investigation, "mildWantedMaximum", config.MildWantedMaximum);
                    config.ContinuousChaosWantedLevel = ReadInt(investigation, "continuousChaosWantedLevel", config.ContinuousChaosWantedLevel);
                    config.AggressionMemorySeconds = ReadInt(investigation, "aggressionMemorySeconds", config.AggressionMemorySeconds);
                    config.AssuranceSeconds = ReadInt(investigation, "assuranceSeconds", config.AssuranceSeconds);
                    config.AssuranceDismissalSeconds = ReadInt(investigation, "assuranceDismissalSeconds", config.AssuranceDismissalSeconds);
                    config.EnableMildWantedInvestigation = ReadBool(investigation, "enabled", config.EnableMildWantedInvestigation);
                }

                XElement protection = root.Element("Protection");
                if (protection != null)
                {
                    config.ThreatMemorySeconds = ReadInt(protection, "threatMemorySeconds", config.ThreatMemorySeconds);
                    config.SupportUnitCooldownSeconds = ReadInt(protection, "supportUnitCooldownSeconds", config.SupportUnitCooldownSeconds);
                    config.SupportUnitLifetimeSeconds = ReadInt(protection, "supportUnitLifetimeSeconds", config.SupportUnitLifetimeSeconds);
                    config.SupportUnitCount = ReadInt(protection, "supportUnitCount", config.SupportUnitCount);
                    config.SupportOfficerHealth = ReadInt(protection, "supportOfficerHealth", config.SupportOfficerHealth);
                    config.SupportOfficerArmor = ReadInt(protection, "supportOfficerArmor", config.SupportOfficerArmor);
                    config.SupportOfficerAccuracy = ReadInt(protection, "supportOfficerAccuracy", config.SupportOfficerAccuracy);
                    config.SupportOfficerWeapon = ReadString(protection, "supportOfficerWeapon", config.SupportOfficerWeapon);
                    config.ThreatRadius = ReadFloat(protection, "threatRadius", config.ThreatRadius);
                    config.PoliceAssistRadius = ReadFloat(protection, "policeAssistRadius", config.PoliceAssistRadius);
                    config.SupportSpawnDistance = ReadFloat(protection, "supportSpawnDistance", config.SupportSpawnDistance);
                    config.SpawnSupportOfficerWhenThreatened = ReadBool(protection, "spawnSupportOfficer", config.SpawnSupportOfficerWhenThreatened);
                    config.SupportOfficerModel = ReadString(protection, "supportOfficerModel", config.SupportOfficerModel);
                }

                XElement behavior = root.Element("CitizenBehavior");
                if (behavior != null)
                {
                    config.RecklessImpactSpeed = ReadFloat(behavior, "recklessImpactSpeed", config.RecklessImpactSpeed);
                }

                XElement gangData = root.Element("GangData");
                if (gangData != null)
                {
                    config.GangDataRoot = ReadString(gangData, "root", config.GangDataRoot);
                }

                Clamp(config);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("CITIZEN_CONFIG_ERROR", ex);
            }

            return config;
        }

        private static void SaveDefault(string path, LspdCitizenConfig config)
        {
            XDocument document = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("LspdCitizenProfile",
                    new XAttribute("version", "1.0"),
                    new XAttribute("enabled", config.Enabled),
                    new XComment("Citizen mode only. It never suppresses wanted stars, spawns military assets, or writes Gang & Turf data."),
                    new XElement("Performance",
                        new XAttribute("coreTickMs", config.CoreTickMs),
                        new XAttribute("roleRefreshMs", config.RoleRefreshMs),
                        new XAttribute("stateScanMs", config.StateScanMs),
                        new XAttribute("nearbyPedScanMs", config.NearbyPedScanMs),
                        new XAttribute("policeReactionMs", config.PoliceReactionMs),
                        new XAttribute("configReloadSeconds", config.ConfigReloadSeconds),
                        new XAttribute("gangDataRefreshSeconds", config.GangDataRefreshSeconds),
                        new XAttribute("heartbeatSeconds", config.HeartbeatSeconds)),
                    new XElement("Investigation",
                        new XAttribute("enabled", config.EnableMildWantedInvestigation),
                        new XAttribute("mildWantedMaximum", config.MildWantedMaximum),
                        new XAttribute("continuousChaosWantedLevel", config.ContinuousChaosWantedLevel),
                        new XAttribute("aggressionMemorySeconds", config.AggressionMemorySeconds),
                        new XAttribute("assuranceSeconds", config.AssuranceSeconds),
                        new XAttribute("assuranceDismissalSeconds", config.AssuranceDismissalSeconds)),
                    new XElement("Protection",
                        new XAttribute("threatRadius", config.ThreatRadius.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("policeAssistRadius", config.PoliceAssistRadius.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("threatMemorySeconds", config.ThreatMemorySeconds),
                        new XAttribute("spawnSupportOfficer", config.SpawnSupportOfficerWhenThreatened),
                        new XAttribute("supportOfficerModel", config.SupportOfficerModel),
                        new XAttribute("supportSpawnDistance", config.SupportSpawnDistance.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("supportUnitCount", config.SupportUnitCount),
                        new XAttribute("supportOfficerHealth", config.SupportOfficerHealth),
                        new XAttribute("supportOfficerArmor", config.SupportOfficerArmor),
                        new XAttribute("supportOfficerAccuracy", config.SupportOfficerAccuracy),
                        new XAttribute("supportOfficerWeapon", config.SupportOfficerWeapon),
                        new XAttribute("supportUnitCooldownSeconds", config.SupportUnitCooldownSeconds),
                        new XAttribute("supportUnitLifetimeSeconds", config.SupportUnitLifetimeSeconds)),
                    new XElement("CitizenBehavior",
                        new XAttribute("recklessImpactSpeed", config.RecklessImpactSpeed.ToString(CultureInfo.InvariantCulture))),
                    new XElement("GangData",
                        new XAttribute("root", config.GangDataRoot))));

            document.Save(path);
        }

        private static void Clamp(LspdCitizenConfig config)
        {
            config.CoreTickMs = Math.Max(100, config.CoreTickMs);
            config.RoleRefreshMs = Math.Max(500, config.RoleRefreshMs);
            config.StateScanMs = Math.Max(250, config.StateScanMs);
            config.NearbyPedScanMs = Math.Max(750, config.NearbyPedScanMs);
            config.PoliceReactionMs = Math.Max(1000, config.PoliceReactionMs);
            config.ConfigReloadSeconds = Math.Max(5, config.ConfigReloadSeconds);
            config.GangDataRefreshSeconds = Math.Max(10, config.GangDataRefreshSeconds);
            config.HeartbeatSeconds = Math.Max(10, config.HeartbeatSeconds);
            config.MildWantedMaximum = Math.Max(1, Math.Min(2, config.MildWantedMaximum));
            config.ContinuousChaosWantedLevel = Math.Max(
                config.MildWantedMaximum + 1,
                Math.Min(5, config.ContinuousChaosWantedLevel));
            config.AggressionMemorySeconds = Math.Max(3, config.AggressionMemorySeconds);
            config.AssuranceSeconds = Math.Max(5, config.AssuranceSeconds);
            config.AssuranceDismissalSeconds = Math.Max(1, Math.Min(15, config.AssuranceDismissalSeconds));
            config.ThreatMemorySeconds = Math.Max(5, config.ThreatMemorySeconds);
            config.ThreatRadius = Math.Max(15.0f, config.ThreatRadius);
            config.PoliceAssistRadius = Math.Max(config.ThreatRadius, config.PoliceAssistRadius);
            config.SupportSpawnDistance = Math.Max(20.0f, config.SupportSpawnDistance);
            config.SupportUnitCount = Math.Max(1, Math.Min(3, config.SupportUnitCount));
            config.SupportOfficerHealth = Math.Max(250, Math.Min(2000, config.SupportOfficerHealth));
            config.SupportOfficerArmor = Math.Max(100, Math.Min(300, config.SupportOfficerArmor));
            config.SupportOfficerAccuracy = Math.Max(50, Math.Min(100, config.SupportOfficerAccuracy));
            config.RecklessImpactSpeed = Math.Max(10.0f, config.RecklessImpactSpeed);
        }

        private static string ReadString(XElement element, string name, string fallback)
        {
            XAttribute attribute = element.Attribute(name);
            return attribute == null || string.IsNullOrWhiteSpace(attribute.Value)
                ? fallback
                : attribute.Value.Trim();
        }

        private static int ReadInt(XElement element, string name, int fallback)
        {
            int value;
            return int.TryParse(
                ReadString(element, name, fallback.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value)
                ? value
                : fallback;
        }

        private static float ReadFloat(XElement element, string name, float fallback)
        {
            float value;
            return float.TryParse(
                ReadString(element, name, fallback.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
                ? value
                : fallback;
        }

        private static bool ReadBool(XElement element, string name, bool fallback)
        {
            bool value;
            return bool.TryParse(ReadString(element, name, fallback.ToString()), out value)
                ? value
                : fallback;
        }
    }
}

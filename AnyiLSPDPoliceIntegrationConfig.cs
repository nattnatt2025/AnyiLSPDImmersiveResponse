using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDPoliceIntegrationConfig
    {
        public const string FileName = "AnyiLSPDPoliceIntegration.ini";

        public bool EnableGangAuthorityIntegration = true;
        public int GangDetectionRadius = 70;
        public int GangIncidentCooldownSeconds = 25;
        public int GangResolutionHoldSeconds = 5;
        public int GangCourtesyRadius = 35;
        public int GangCourtesyCooldownSeconds = 45;
        public int GangTerritorySearchRadius = 120;
        public bool EnableChaosGangActivityDiscovery = true;
        public int ChaosGangActivityScanSeconds = 15;
        public int ChaosGangActivityDiscoverRadius = 90;
        public int ChaosGangActivityInvestigationRadius = 35;
        public int ChaosGangActivityResolutionHoldSeconds = 8;
        public int ChaosGangActivityCooldownSeconds = 180;
        public int ChaosGangActivityStaleCleanupSeconds = 240;
        public bool ChaosGangActivityUseWaypoint = true;
        public bool ChaosGangActivityAllowClearSceneSuccess = true;
        public int ChaosGangActivityMaxScenePeds = 6;
        public int ChaosGangActivityMaxSceneVehicles = 2;
        public int ChaosGangActivitySceneSpawnRadius = 12;
        public string ChaosGangActivityFallbackPedModel = "g_m_y_ballasout_01";
        public string ChaosGangActivityFallbackVehicleModel = "baller";

        public static AnyiLSPDPoliceIntegrationConfig LoadOrCreate(string scriptsDirectory)
        {
            AnyiLSPDPoliceIntegrationConfig config = new AnyiLSPDPoliceIntegrationConfig();
            string path = Path.Combine(scriptsDirectory, FileName);

            try
            {
                if (!File.Exists(path))
                {
                    Save(path, config);
                    LspdResponseLog.Write("POLICE_INTEGRATION_CONFIG", "Created " + FileName + " in scripts directory.");
                    return config;
                }

                Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw == null ? string.Empty : raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("["))
                        continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    values[key] = value;
                }

                config.EnableGangAuthorityIntegration = ReadBool(values, "EnableGangAuthorityIntegration", config.EnableGangAuthorityIntegration);
                config.GangDetectionRadius = ReadInt(values, "GangDetectionRadius", config.GangDetectionRadius);
                config.GangIncidentCooldownSeconds = ReadInt(values, "GangIncidentCooldownSeconds", config.GangIncidentCooldownSeconds);
                config.GangResolutionHoldSeconds = ReadInt(values, "GangResolutionHoldSeconds", config.GangResolutionHoldSeconds);
                config.GangCourtesyRadius = ReadInt(values, "GangCourtesyRadius", config.GangCourtesyRadius);
                config.GangCourtesyCooldownSeconds = ReadInt(values, "GangCourtesyCooldownSeconds", config.GangCourtesyCooldownSeconds);
                config.GangTerritorySearchRadius = ReadInt(values, "GangTerritorySearchRadius", config.GangTerritorySearchRadius);
                config.EnableChaosGangActivityDiscovery = ReadBool(values, "EnableChaosGangActivityDiscovery", config.EnableChaosGangActivityDiscovery);
                config.ChaosGangActivityScanSeconds = ReadInt(values, "ChaosGangActivityScanSeconds", config.ChaosGangActivityScanSeconds);
                config.ChaosGangActivityDiscoverRadius = ReadInt(values, "ChaosGangActivityDiscoverRadius", config.ChaosGangActivityDiscoverRadius);
                config.ChaosGangActivityInvestigationRadius = ReadInt(values, "ChaosGangActivityInvestigationRadius", config.ChaosGangActivityInvestigationRadius);
                config.ChaosGangActivityResolutionHoldSeconds = ReadInt(values, "ChaosGangActivityResolutionHoldSeconds", config.ChaosGangActivityResolutionHoldSeconds);
                config.ChaosGangActivityCooldownSeconds = ReadInt(values, "ChaosGangActivityCooldownSeconds", config.ChaosGangActivityCooldownSeconds);
                config.ChaosGangActivityStaleCleanupSeconds = ReadInt(values, "ChaosGangActivityStaleCleanupSeconds", config.ChaosGangActivityStaleCleanupSeconds);
                config.ChaosGangActivityUseWaypoint = ReadBool(values, "ChaosGangActivityUseWaypoint", config.ChaosGangActivityUseWaypoint);
                config.ChaosGangActivityAllowClearSceneSuccess = ReadBool(values, "ChaosGangActivityAllowClearSceneSuccess", config.ChaosGangActivityAllowClearSceneSuccess);
                config.ChaosGangActivityMaxScenePeds = ReadInt(values, "ChaosGangActivityMaxScenePeds", config.ChaosGangActivityMaxScenePeds);
                config.ChaosGangActivityMaxSceneVehicles = ReadInt(values, "ChaosGangActivityMaxSceneVehicles", config.ChaosGangActivityMaxSceneVehicles);
                config.ChaosGangActivitySceneSpawnRadius = ReadInt(values, "ChaosGangActivitySceneSpawnRadius", config.ChaosGangActivitySceneSpawnRadius);
                if (values.ContainsKey("ChaosGangActivityFallbackPedModel")) config.ChaosGangActivityFallbackPedModel = values["ChaosGangActivityFallbackPedModel"];
                if (values.ContainsKey("ChaosGangActivityFallbackVehicleModel")) config.ChaosGangActivityFallbackVehicleModel = values["ChaosGangActivityFallbackVehicleModel"];
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_INTEGRATION_CONFIG_ERROR", ex);
            }

            config.Normalize();
            return config;
        }

        public static void Save(string path, AnyiLSPDPoliceIntegrationConfig config)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                List<string> lines = new List<string>
                {
                    "# Anyi LSPD Police Authority integration layer",
                    "# Gang Authority incidents and ChaosResponse GangActivity discovery only.",
                    "# Stable Dispatch / Arrest / Convoy / Transport systems are not configured here.",
                    "",
                    "EnableGangAuthorityIntegration=" + config.EnableGangAuthorityIntegration,
                    "GangDetectionRadius=" + config.GangDetectionRadius,
                    "GangIncidentCooldownSeconds=" + config.GangIncidentCooldownSeconds,
                    "GangResolutionHoldSeconds=" + config.GangResolutionHoldSeconds,
                    "GangCourtesyRadius=" + config.GangCourtesyRadius,
                    "GangCourtesyCooldownSeconds=" + config.GangCourtesyCooldownSeconds,
                    "GangTerritorySearchRadius=" + config.GangTerritorySearchRadius,
                    "",
                    "EnableChaosGangActivityDiscovery=" + config.EnableChaosGangActivityDiscovery,
                    "ChaosGangActivityScanSeconds=" + config.ChaosGangActivityScanSeconds,
                    "ChaosGangActivityDiscoverRadius=" + config.ChaosGangActivityDiscoverRadius,
                    "ChaosGangActivityInvestigationRadius=" + config.ChaosGangActivityInvestigationRadius,
                    "ChaosGangActivityResolutionHoldSeconds=" + config.ChaosGangActivityResolutionHoldSeconds,
                    "ChaosGangActivityCooldownSeconds=" + config.ChaosGangActivityCooldownSeconds,
                    "ChaosGangActivityStaleCleanupSeconds=" + config.ChaosGangActivityStaleCleanupSeconds,
                    "ChaosGangActivityUseWaypoint=" + config.ChaosGangActivityUseWaypoint,
                    "ChaosGangActivityAllowClearSceneSuccess=" + config.ChaosGangActivityAllowClearSceneSuccess,
                    "ChaosGangActivityMaxScenePeds=" + config.ChaosGangActivityMaxScenePeds,
                    "ChaosGangActivityMaxSceneVehicles=" + config.ChaosGangActivityMaxSceneVehicles,
                    "ChaosGangActivitySceneSpawnRadius=" + config.ChaosGangActivitySceneSpawnRadius,
                    "ChaosGangActivityFallbackPedModel=" + config.ChaosGangActivityFallbackPedModel,
                    "ChaosGangActivityFallbackVehicleModel=" + config.ChaosGangActivityFallbackVehicleModel
                };
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_INTEGRATION_CONFIG_SAVE_ERROR", ex);
            }
        }

        private void Normalize()
        {
            GangDetectionRadius = Math.Max(25, Math.Min(160, GangDetectionRadius));
            GangIncidentCooldownSeconds = Math.Max(10, GangIncidentCooldownSeconds);
            GangResolutionHoldSeconds = Math.Max(2, Math.Min(30, GangResolutionHoldSeconds));
            GangCourtesyRadius = Math.Max(10, Math.Min(80, GangCourtesyRadius));
            GangCourtesyCooldownSeconds = Math.Max(15, GangCourtesyCooldownSeconds);
            GangTerritorySearchRadius = Math.Max(50, Math.Min(250, GangTerritorySearchRadius));
            ChaosGangActivityScanSeconds = Math.Max(5, Math.Min(60, ChaosGangActivityScanSeconds));
            ChaosGangActivityDiscoverRadius = Math.Max(30, Math.Min(300, ChaosGangActivityDiscoverRadius));
            ChaosGangActivityInvestigationRadius = Math.Max(15, Math.Min(80, ChaosGangActivityInvestigationRadius));
            ChaosGangActivityResolutionHoldSeconds = Math.Max(3, Math.Min(30, ChaosGangActivityResolutionHoldSeconds));
            ChaosGangActivityCooldownSeconds = Math.Max(60, ChaosGangActivityCooldownSeconds);
            ChaosGangActivityStaleCleanupSeconds = Math.Max(60, ChaosGangActivityStaleCleanupSeconds);
            ChaosGangActivityMaxScenePeds = Math.Max(1, Math.Min(8, ChaosGangActivityMaxScenePeds));
            ChaosGangActivityMaxSceneVehicles = Math.Max(0, Math.Min(3, ChaosGangActivityMaxSceneVehicles));
            ChaosGangActivitySceneSpawnRadius = Math.Max(6, Math.Min(25, ChaosGangActivitySceneSpawnRadius));
            if (string.IsNullOrWhiteSpace(ChaosGangActivityFallbackPedModel)) ChaosGangActivityFallbackPedModel = "g_m_y_ballasout_01";
            if (string.IsNullOrWhiteSpace(ChaosGangActivityFallbackVehicleModel)) ChaosGangActivityFallbackVehicleModel = "baller";
        }

        private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
        {
            string value;
            bool result;
            return values.TryGetValue(key, out value) && bool.TryParse(value, out result) ? result : fallback;
        }

        private static int ReadInt(Dictionary<string, string> values, string key, int fallback)
        {
            string value;
            int result;
            return values.TryGetValue(key, out value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }
    }
}

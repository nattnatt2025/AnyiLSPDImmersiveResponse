using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDPoliceConfig
    {
        public const string FileName = "AnyiLSPDPolice.ini";

        public bool Enabled = true;
        public string ActiveProfileId = "LSPD";
        public string SelectedStationId = "MissionRow";
        public string Department = "LSPD";
        public string OfficerModel = "s_m_y_cop_01";
        public string VehicleModel = "police";
        public string TransportVehicleModel = "fbi2";
        public string DefaultStation = "MissionRow";
        public string PrisonStation = "Bolingbroke";
        public string GangDataRoot = "";
        public string ChaosActivityRoot = "";
        public string ChaosAudioRoot = "";
        public string PoliceEventsFile = "AnyiLSPDPoliceEvents.xml";

        // Persistent Anyi profile customization. These values are Police Authority-only.
        public string FavoriteOfficerModel = "venti";
        public string FavoritePoliceVehicleModel = "polignus";
        public string FavoriteWeaponHash = "0x83BF0278";
        public int FavoriteWeaponAmmo = 240;
        public int FavoriteWeaponTint = 2;
        public string PoliceModelsFile = "AnyiLSPDPoliceModels.xml";
        public string PoliceWeaponsFile = "AnyiLSPDPoliceWeapons.xml";

        public bool UseNativeSiren = true;
        public bool EmergencyLights = true;
        public bool RadioEnabled = true;
        public bool EnableChaosAudio = true;
        public bool EnableChaosGangActivities = true;
        public bool EnableRandomEvents = true;
        public bool EnableOrganicFallbackEvents = true;
        public bool EnableConvoy = true;
        public bool EnableNpcReaction = true;
        public bool EnablePoliceOfficerReaction = true;
        public bool EnablePoliceAwareCivilianReactions = true;
        public bool EnableActiveSceneCivilianFlee = true;
        public bool EnableTrafficCollisionAvoidance = true;
        public int NpcInteractionTimeoutSeconds = 25;
        public int NpcDocumentPresentationSeconds = 2;
        public int NpcFleeChancePercent = 35;
        public int NpcStationFollowUpChancePercent = 20;
        public int TrafficStopTimeoutSeconds = 30;
        public int TrafficStopFleeChancePercent = 40;
        public int TrafficStopSearchRadius = 35;
        public int CompletedEntityCleanupGraceSeconds = 12;
        public int CompletedEntityCleanupDistance = 65;
        public int CompletedEntityCleanupMaxSeconds = 120;
        public int ConvoyCleanupGraceSeconds = 12;
        public int ConvoyCleanupDistance = 60;
        public int ConvoyCleanupMaxSeconds = 120;
        public bool EnablePoliceResponse = true;
        public bool EnableAutoWantedReset = true;
        public bool EnableStationBlips = true;
        public bool EnableGangAttackDispatch = true;
        public bool EnableVanillaGangAttackDispatch = true;
        public bool AutoSpawnPlayerPatrol = false;
        public bool EnablePoliceShortcuts = true;
        public bool ShortcutKeysRequireMenuClosed = true;
        public Keys AcceptDispatchKey = Keys.None;
        public Keys RejectDispatchKey = Keys.None;
        public Keys SecureSuspectKey = Keys.None;
        public Keys RequestTransportKey = Keys.None;
        public Keys CompleteTransportKey = Keys.T;
        public Keys NPCInteractionKey = Keys.None;
        public Keys InvestigateSceneKey = Keys.None;
        public Keys CancelDispatchKey = Keys.None;
        public Keys PatrolKey = Keys.None;
        public Keys EmergencySignalsKey = Keys.None;
        public bool CompleteDispatchOnSuspectDeath = true;
        public bool PreferChaosActivities = true;
        public string ArrestSuccessAudioCategories = "REPORT_SUSPECT_IN_CUSTODY|UNIT_CLEAR|CASE_CLOSED";
        public string DispatchSuccessAudioCategories = "UNIT_CLEAR|CASE_CLOSED|ATTENTION_ALL_UNITS";
        public string TransportSuccessAudioCategories = "UNIT_CLEAR|CASE_CLOSED|ATTENTION_ALL_UNITS";

        public int CoreTickMs = 250;
        public int AuthorityRefreshMs = 900;
        public int NearbyPedScanMs = 1600;
        public int ReactionScanMs = 1800;
        public int DispatchCheckMs = 500;
        public int RandomEventCheckSeconds = 12;
        public int DispatchCooldownSeconds = 90;
        public int EventOfferRadius = 750;
        public int SceneArrivalRadius = 28;
        public int InteractionRadius = 5;
        public int ArrestRadius = 5;
        public int ResponseDriveSpeed = 32;
        public int ResponseDriveRadius = 18;
        public int MaxPoliceUnits = 2;
        public int MaxPatrolUnits = 1;
        public int PoliceReactionCooldownSeconds = 8;
        public int NpcReactionCooldownSeconds = 12;
        public int CustodyWatchdogSeconds = 3;
        public int ConvoyPickupRadius = 20;
        public int ConvoyArrivalRadius = 25;
        public int EventCooldownSeconds = 120;
        public int AudioCooldownSeconds = 8;
        public int InitialFallbackMinSeconds = 20;
        public int InitialFallbackMaxSeconds = 45;
        public int FallbackEventMinSeconds = 120;
        public int FallbackEventMaxSeconds = 240;
        public int GangAttackOfferCooldownSeconds = 25;
        public int ReportHeartbeatSeconds = 15;

        public int NpcActiveSceneRadius = 45;
        public int NpcTrafficSlowRadius = 35;
        public int NpcFleeReactionRadius = 45;
        public int NpcInteractionPreparationSeconds = 1;
        public int NpcTrafficStopTimeoutSeconds = 30;
        public int NpcTrafficFleeChancePercent = 35;
        public int NpcCitizenFleeChancePercent = 25;

        public string[] InvestigationSuccessAudioCategories =
{
    "CASE_CLOSED",
    "UNIT_CLEAR",
    "ATTENTION_ALL_UNITS"
};




        public static AnyiLSPDPoliceConfig LoadOrCreate(string scriptsDirectory)
        {
            AnyiLSPDPoliceConfig config = new AnyiLSPDPoliceConfig();
            string path = Path.Combine(scriptsDirectory, FileName);

            if (!File.Exists(path))
            {
                config.NormalizePaths(scriptsDirectory);
                Save(path, config);
                LspdResponseLog.Write("POLICE_CONFIG", "Created " + FileName);
                return config;
            }

            try
            {
                Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw == null ? "" : raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;
                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    values[key] = value;
                }



                config.Enabled = ReadBool(values, "Enabled", config.Enabled);
                config.ActiveProfileId = Read(values, "ActiveProfileId", config.ActiveProfileId);
                config.SelectedStationId = Read(values, "SelectedStationId", config.SelectedStationId);
                config.Department = Read(values, "Department", config.Department);
                config.OfficerModel = Read(values, "OfficerModel", config.OfficerModel);
                config.VehicleModel = Read(values, "VehicleModel", config.VehicleModel);
                config.TransportVehicleModel = Read(values, "TransportVehicleModel", config.TransportVehicleModel);
                config.DefaultStation = Read(values, "DefaultStation", config.DefaultStation);
                config.PrisonStation = Read(values, "PrisonStation", config.PrisonStation);
                config.GangDataRoot = Read(values, "GangDataRoot", config.GangDataRoot);
                config.ChaosActivityRoot = Read(values, "ChaosActivityRoot", config.ChaosActivityRoot);
                config.ChaosAudioRoot = Read(values, "ChaosAudioRoot", config.ChaosAudioRoot);
                config.PoliceEventsFile = Read(values, "PoliceEventsFile", config.PoliceEventsFile);
                config.FavoriteOfficerModel = Read(values, "FavoriteOfficerModel", config.FavoriteOfficerModel);
                config.FavoritePoliceVehicleModel = Read(values, "FavoritePoliceVehicleModel", config.FavoritePoliceVehicleModel);
                config.FavoriteWeaponHash = Read(values, "FavoriteWeaponHash", config.FavoriteWeaponHash);
                config.FavoriteWeaponAmmo = ReadInt(values, "FavoriteWeaponAmmo", config.FavoriteWeaponAmmo);
                config.FavoriteWeaponTint = ReadInt(values, "FavoriteWeaponTint", config.FavoriteWeaponTint);
                config.PoliceModelsFile = Read(values, "PoliceModelsFile", config.PoliceModelsFile);
                config.PoliceWeaponsFile = Read(values, "PoliceWeaponsFile", config.PoliceWeaponsFile);

                config.UseNativeSiren = ReadBool(values, "UseNativeSiren", config.UseNativeSiren);
                config.EmergencyLights = ReadBool(values, "EmergencyLights", config.EmergencyLights);
                config.RadioEnabled = ReadBool(values, "RadioEnabled", config.RadioEnabled);
                config.EnableChaosAudio = ReadBool(values, "EnableChaosAudio", config.EnableChaosAudio);
                config.EnableChaosGangActivities = ReadBool(values, "EnableChaosGangActivities", config.EnableChaosGangActivities);
                config.EnableRandomEvents = ReadBool(values, "EnableRandomEvents", config.EnableRandomEvents);
                config.EnableOrganicFallbackEvents = ReadBool(values, "EnableOrganicFallbackEvents", config.EnableOrganicFallbackEvents);
                config.EnableConvoy = ReadBool(values, "EnableConvoy", config.EnableConvoy);
                config.EnableNpcReaction = ReadBool(values, "EnableNpcReaction", config.EnableNpcReaction);
                config.EnablePoliceOfficerReaction = ReadBool(values, "EnablePoliceOfficerReaction", config.EnablePoliceOfficerReaction);
                config.EnablePoliceAwareCivilianReactions = ReadBool(values, "EnablePoliceAwareCivilianReactions", config.EnablePoliceAwareCivilianReactions);
                config.EnableActiveSceneCivilianFlee = ReadBool(values, "EnableActiveSceneCivilianFlee", config.EnableActiveSceneCivilianFlee);
                config.EnableTrafficCollisionAvoidance = ReadBool(values, "EnableTrafficCollisionAvoidance", config.EnableTrafficCollisionAvoidance);
                config.NpcInteractionTimeoutSeconds = ReadInt(values, "NpcInteractionTimeoutSeconds", config.NpcInteractionTimeoutSeconds);
                config.NpcDocumentPresentationSeconds = ReadInt(values, "NpcDocumentPresentationSeconds", config.NpcDocumentPresentationSeconds);
                config.NpcFleeChancePercent = ReadInt(values, "NpcFleeChancePercent", config.NpcFleeChancePercent);
                config.NpcStationFollowUpChancePercent = ReadInt(values, "NpcStationFollowUpChancePercent", config.NpcStationFollowUpChancePercent);
                config.TrafficStopTimeoutSeconds = ReadInt(values, "TrafficStopTimeoutSeconds", config.TrafficStopTimeoutSeconds);
                config.TrafficStopFleeChancePercent = ReadInt(values, "TrafficStopFleeChancePercent", config.TrafficStopFleeChancePercent);
                config.TrafficStopSearchRadius = ReadInt(values, "TrafficStopSearchRadius", config.TrafficStopSearchRadius);
                config.CompletedEntityCleanupGraceSeconds = ReadInt(values, "CompletedEntityCleanupGraceSeconds", config.CompletedEntityCleanupGraceSeconds);
                config.CompletedEntityCleanupDistance = ReadInt(values, "CompletedEntityCleanupDistance", config.CompletedEntityCleanupDistance);
                config.CompletedEntityCleanupMaxSeconds = ReadInt(values, "CompletedEntityCleanupMaxSeconds", config.CompletedEntityCleanupMaxSeconds);
                config.ConvoyCleanupGraceSeconds = ReadInt(values, "ConvoyCleanupGraceSeconds", config.ConvoyCleanupGraceSeconds);
                config.ConvoyCleanupDistance = ReadInt(values, "ConvoyCleanupDistance", config.ConvoyCleanupDistance);
                config.ConvoyCleanupMaxSeconds = ReadInt(values, "ConvoyCleanupMaxSeconds", config.ConvoyCleanupMaxSeconds);
                config.EnablePoliceResponse = ReadBool(values, "EnablePoliceResponse", config.EnablePoliceResponse);
                config.EnableAutoWantedReset = ReadBool(values, "EnableAutoWantedReset", config.EnableAutoWantedReset);
                config.EnableStationBlips = ReadBool(values, "EnableStationBlips", config.EnableStationBlips);
                config.EnableGangAttackDispatch = ReadBool(values, "EnableGangAttackDispatch", config.EnableGangAttackDispatch);
                config.EnableVanillaGangAttackDispatch = ReadBool(values, "EnableVanillaGangAttackDispatch", config.EnableVanillaGangAttackDispatch);
                config.AutoSpawnPlayerPatrol = ReadBool(values, "AutoSpawnPlayerPatrol", config.AutoSpawnPlayerPatrol);
                config.EnablePoliceShortcuts = ReadBool(values, "EnablePoliceShortcuts", config.EnablePoliceShortcuts);
                config.ShortcutKeysRequireMenuClosed = ReadBool(values, "ShortcutKeysRequireMenuClosed", config.ShortcutKeysRequireMenuClosed);
                config.AcceptDispatchKey = ReadKey(values, "AcceptDispatchKey", config.AcceptDispatchKey);
                config.RejectDispatchKey = ReadKey(values, "RejectDispatchKey", config.RejectDispatchKey);
                config.SecureSuspectKey = ReadKey(values, "SecureSuspectKey", config.SecureSuspectKey);
                config.RequestTransportKey = ReadKey(values, "RequestTransportKey", config.RequestTransportKey);
                config.CompleteTransportKey = ReadKey(values, "CompleteTransportKey", config.CompleteTransportKey);
                config.NPCInteractionKey = ReadKey(values, "NPCInteractionKey", config.NPCInteractionKey);
                config.InvestigateSceneKey = ReadKey(values, "InvestigateSceneKey", config.InvestigateSceneKey);
                config.CancelDispatchKey = ReadKey(values, "CancelDispatchKey", config.CancelDispatchKey);
                config.PatrolKey = ReadKey(values, "PatrolKey", config.PatrolKey);
                config.EmergencySignalsKey = ReadKey(values, "EmergencySignalsKey", config.EmergencySignalsKey);
                config.CompleteDispatchOnSuspectDeath = ReadBool(values, "CompleteDispatchOnSuspectDeath", config.CompleteDispatchOnSuspectDeath);
                config.PreferChaosActivities = ReadBool(values, "PreferChaosActivities", config.PreferChaosActivities);
                config.ArrestSuccessAudioCategories = Read(values, "ArrestSuccessAudioCategories", config.ArrestSuccessAudioCategories);
                config.DispatchSuccessAudioCategories = Read(values, "DispatchSuccessAudioCategories", config.DispatchSuccessAudioCategories);
                config.TransportSuccessAudioCategories = Read(values, "TransportSuccessAudioCategories", config.TransportSuccessAudioCategories);

                config.CoreTickMs = ReadInt(values, "CoreTickMs", config.CoreTickMs);
                config.AuthorityRefreshMs = ReadInt(values, "AuthorityRefreshMs", config.AuthorityRefreshMs);
                config.NearbyPedScanMs = ReadInt(values, "NearbyPedScanMs", config.NearbyPedScanMs);
                config.ReactionScanMs = ReadInt(values, "ReactionScanMs", config.ReactionScanMs);
                config.DispatchCheckMs = ReadInt(values, "DispatchCheckMs", config.DispatchCheckMs);
                config.RandomEventCheckSeconds = ReadInt(values, "RandomEventCheckSeconds", config.RandomEventCheckSeconds);
                config.DispatchCooldownSeconds = ReadInt(values, "DispatchCooldownSeconds", config.DispatchCooldownSeconds);
                config.EventOfferRadius = ReadInt(values, "EventOfferRadius", config.EventOfferRadius);
                config.SceneArrivalRadius = ReadInt(values, "SceneArrivalRadius", config.SceneArrivalRadius);
                config.InteractionRadius = ReadInt(values, "InteractionRadius", config.InteractionRadius);
                config.ArrestRadius = ReadInt(values, "ArrestRadius", config.ArrestRadius);
                config.ResponseDriveSpeed = ReadInt(values, "ResponseDriveSpeed", config.ResponseDriveSpeed);
                config.ResponseDriveRadius = ReadInt(values, "ResponseDriveRadius", config.ResponseDriveRadius);
                config.MaxPoliceUnits = ReadInt(values, "MaxPoliceUnits", config.MaxPoliceUnits);
                config.MaxPatrolUnits = ReadInt(values, "MaxPatrolUnits", config.MaxPatrolUnits);
                config.PoliceReactionCooldownSeconds = ReadInt(values, "PoliceReactionCooldownSeconds", config.PoliceReactionCooldownSeconds);
                config.NpcReactionCooldownSeconds = ReadInt(values, "NpcReactionCooldownSeconds", config.NpcReactionCooldownSeconds);
                config.CustodyWatchdogSeconds = ReadInt(values, "CustodyWatchdogSeconds", config.CustodyWatchdogSeconds);
                config.ConvoyPickupRadius = ReadInt(values, "ConvoyPickupRadius", config.ConvoyPickupRadius);
                config.ConvoyArrivalRadius = ReadInt(values, "ConvoyArrivalRadius", config.ConvoyArrivalRadius);
                config.EventCooldownSeconds = ReadInt(values, "EventCooldownSeconds", config.EventCooldownSeconds);
                config.AudioCooldownSeconds = ReadInt(values, "AudioCooldownSeconds", config.AudioCooldownSeconds);
                config.InitialFallbackMinSeconds = ReadInt(values, "InitialFallbackMinSeconds", config.InitialFallbackMinSeconds);
                config.InitialFallbackMaxSeconds = ReadInt(values, "InitialFallbackMaxSeconds", config.InitialFallbackMaxSeconds);
                config.FallbackEventMinSeconds = ReadInt(values, "FallbackEventMinSeconds", config.FallbackEventMinSeconds);
                config.FallbackEventMaxSeconds = ReadInt(values, "FallbackEventMaxSeconds", config.FallbackEventMaxSeconds);
                config.GangAttackOfferCooldownSeconds = ReadInt(values, "GangAttackOfferCooldownSeconds", config.GangAttackOfferCooldownSeconds);
                config.ReportHeartbeatSeconds = ReadInt(values, "ReportHeartbeatSeconds", config.ReportHeartbeatSeconds);
                config.NpcActiveSceneRadius = ReadInt(values, "NpcActiveSceneRadius", config.NpcActiveSceneRadius);
                config.NpcTrafficSlowRadius = ReadInt(values, "NpcTrafficSlowRadius", config.NpcTrafficSlowRadius);
                config.NpcFleeReactionRadius = ReadInt(values, "NpcFleeReactionRadius", config.NpcFleeReactionRadius);
                config.NpcInteractionPreparationSeconds = ReadInt(values, "NpcInteractionPreparationSeconds", config.NpcInteractionPreparationSeconds);
                config.NpcTrafficStopTimeoutSeconds = ReadInt(values, "NpcTrafficStopTimeoutSeconds", config.NpcTrafficStopTimeoutSeconds);
                config.NpcTrafficFleeChancePercent = ReadInt(values, "NpcTrafficFleeChancePercent", config.NpcTrafficFleeChancePercent);
                config.NpcCitizenFleeChancePercent = ReadInt(values, "NpcCitizenFleeChancePercent", config.NpcCitizenFleeChancePercent);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CONFIG_ERROR", ex);
            }

            config.NormalizePaths(scriptsDirectory);
            config.Clamp();
            return config;
        }
        private static string[] SplitCsv(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return new string[0];

            return value
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToArray();
        }
        private void NormalizePaths(string scriptsDirectory)
        {
            string actualScripts = AnyiLSPDPathProvider.ScriptsDirectory;
            string actualGameRoot = AnyiLSPDPathProvider.GameRoot;

            if (string.IsNullOrWhiteSpace(GangDataRoot) ||
                AnyiLSPDPathProvider.IsShadowCopyPath(GangDataRoot))
                GangDataRoot = AnyiLSPDPathProvider.GangDataDirectory;

            if (string.IsNullOrWhiteSpace(ChaosActivityRoot) ||
                AnyiLSPDPathProvider.IsShadowCopyPath(ChaosActivityRoot))
                ChaosActivityRoot = AnyiLSPDPathProvider.ChaosActivityDirectory;

            if (string.IsNullOrWhiteSpace(ChaosAudioRoot) ||
                AnyiLSPDPathProvider.IsShadowCopyPath(ChaosAudioRoot))
                ChaosAudioRoot = AnyiLSPDPathProvider.ChaosAudioDirectory;
        }
        public static void SaveSelectedStation(string path, string stationId)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(stationId))
                return;

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                List<string> lines = File.Exists(path)
                    ? File.ReadAllLines(path).ToList()
                    : new List<string>();

                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string trimmed = lines[i] == null ? string.Empty : lines[i].Trim();
                    if (trimmed.StartsWith("SelectedStationId=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = "SelectedStationId=" + stationId.Trim();
                        replaced = true;
                        break;
                    }
                }

                if (!replaced)
                    lines.Insert(0, "SelectedStationId=" + stationId.Trim());

                File.WriteAllLines(path, lines);
                LspdResponseLog.Write(
                    "POLICE_CONFIG",
                    "SelectedStationId persisted without regenerating user INI | Station=" + stationId.Trim());
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CONFIG_STATION_SAVE_ERROR", ex);
            }
        }

        public static void Save(string path, AnyiLSPDPoliceConfig config)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            // IMPORTANT:
            // AnyiLSPDPolice.ini is USER-OWNED.
            // Never regenerate or overwrite an existing INI during startup/reload.
            // C# field values are defaults only.
            if (File.Exists(path))
            {
                LspdResponseLog.Write(
                    "POLICE_CONFIG",
                    "Existing AnyiLSPDPolice.ini preserved; automatic rewrite blocked."
                );
                return;
            }

            List<string> lines = new List<string>();
            lines.Add("# Anyi LSPD Police Authority v4 configuration");
            lines.Add("# Police Authority only. Citizen and Gang XML remain external and read-only.");
            lines.Add("Enabled=" + config.Enabled.ToString());
            lines.Add("ActiveProfileId=" + config.ActiveProfileId);
            lines.Add("SelectedStationId=" + config.SelectedStationId);
            lines.Add("Department=" + config.Department);
            lines.Add("OfficerModel=" + config.OfficerModel);
            lines.Add("VehicleModel=" + config.VehicleModel);
            lines.Add("TransportVehicleModel=" + config.TransportVehicleModel);
            lines.Add("DefaultStation=" + config.DefaultStation);
            lines.Add("PrisonStation=" + config.PrisonStation);
            lines.Add("GangDataRoot=" + config.GangDataRoot);
            lines.Add("ChaosActivityRoot=" + config.ChaosActivityRoot);
            lines.Add("ChaosAudioRoot=" + config.ChaosAudioRoot);
            lines.Add("PoliceEventsFile=" + config.PoliceEventsFile);
            lines.Add("FavoriteOfficerModel=" + config.FavoriteOfficerModel);
            lines.Add("FavoritePoliceVehicleModel=" + config.FavoritePoliceVehicleModel);
            lines.Add("FavoriteWeaponHash=" + config.FavoriteWeaponHash);
            lines.Add("FavoriteWeaponAmmo=" + config.FavoriteWeaponAmmo);
            lines.Add("FavoriteWeaponTint=" + config.FavoriteWeaponTint);
            lines.Add("PoliceModelsFile=" + config.PoliceModelsFile);
            lines.Add("PoliceWeaponsFile=" + config.PoliceWeaponsFile);
            lines.Add("");
            lines.Add("UseNativeSiren=" + config.UseNativeSiren.ToString());
            lines.Add("EmergencyLights=" + config.EmergencyLights.ToString());
            lines.Add("RadioEnabled=" + config.RadioEnabled.ToString());
            lines.Add("EnableChaosAudio=" + config.EnableChaosAudio.ToString());
            lines.Add("EnableChaosGangActivities=" + config.EnableChaosGangActivities.ToString());
            lines.Add("EnableRandomEvents=" + config.EnableRandomEvents.ToString());
            lines.Add("EnableOrganicFallbackEvents=" + config.EnableOrganicFallbackEvents.ToString());
            lines.Add("EnableConvoy=" + config.EnableConvoy.ToString());
            lines.Add("EnableNpcReaction=" + config.EnableNpcReaction.ToString());
            lines.Add("EnablePoliceOfficerReaction=" + config.EnablePoliceOfficerReaction.ToString());
            lines.Add("EnablePoliceAwareCivilianReactions=" + config.EnablePoliceAwareCivilianReactions.ToString());
            lines.Add("EnableActiveSceneCivilianFlee=" + config.EnableActiveSceneCivilianFlee.ToString());
            lines.Add("EnableTrafficCollisionAvoidance=" + config.EnableTrafficCollisionAvoidance.ToString());
            lines.Add("NpcInteractionTimeoutSeconds=" + config.NpcInteractionTimeoutSeconds);
            lines.Add("NpcDocumentPresentationSeconds=" + config.NpcDocumentPresentationSeconds);
            lines.Add("NpcFleeChancePercent=" + config.NpcFleeChancePercent);
            lines.Add("NpcStationFollowUpChancePercent=" + config.NpcStationFollowUpChancePercent);
            lines.Add("TrafficStopTimeoutSeconds=" + config.TrafficStopTimeoutSeconds);
            lines.Add("TrafficStopFleeChancePercent=" + config.TrafficStopFleeChancePercent);
            lines.Add("TrafficStopSearchRadius=" + config.TrafficStopSearchRadius);
            lines.Add("CompletedEntityCleanupGraceSeconds=" + config.CompletedEntityCleanupGraceSeconds);
            lines.Add("CompletedEntityCleanupDistance=" + config.CompletedEntityCleanupDistance);
            lines.Add("CompletedEntityCleanupMaxSeconds=" + config.CompletedEntityCleanupMaxSeconds);
            lines.Add("ConvoyCleanupGraceSeconds=" + config.ConvoyCleanupGraceSeconds);
            lines.Add("ConvoyCleanupDistance=" + config.ConvoyCleanupDistance);
            lines.Add("ConvoyCleanupMaxSeconds=" + config.ConvoyCleanupMaxSeconds);
            lines.Add("EnablePoliceResponse=" + config.EnablePoliceResponse.ToString());
            lines.Add("EnableAutoWantedReset=" + config.EnableAutoWantedReset.ToString());
            lines.Add("EnableStationBlips=" + config.EnableStationBlips.ToString());
            lines.Add("EnableGangAttackDispatch=" + config.EnableGangAttackDispatch.ToString());
            lines.Add("EnableVanillaGangAttackDispatch=" + config.EnableVanillaGangAttackDispatch.ToString());
            lines.Add("AutoSpawnPlayerPatrol=" + config.AutoSpawnPlayerPatrol.ToString());
            lines.Add("EnablePoliceShortcuts=" + config.EnablePoliceShortcuts.ToString());
            lines.Add("ShortcutKeysRequireMenuClosed=" + config.ShortcutKeysRequireMenuClosed.ToString());
            lines.Add("AcceptDispatchKey=" + config.AcceptDispatchKey);
            lines.Add("RejectDispatchKey=" + config.RejectDispatchKey);
            lines.Add("SecureSuspectKey=" + config.SecureSuspectKey);
            lines.Add("RequestTransportKey=" + config.RequestTransportKey);
            lines.Add("CompleteTransportKey=" + config.CompleteTransportKey);
            lines.Add("NPCInteractionKey=" + config.NPCInteractionKey);
            lines.Add("InvestigateSceneKey=" + config.InvestigateSceneKey);
            lines.Add("CancelDispatchKey=" + config.CancelDispatchKey);
            lines.Add("PatrolKey=" + config.PatrolKey);
            lines.Add("EmergencySignalsKey=" + config.EmergencySignalsKey);
            lines.Add("CompleteDispatchOnSuspectDeath=" + config.CompleteDispatchOnSuspectDeath.ToString());
            lines.Add("PreferChaosActivities=" + config.PreferChaosActivities.ToString());
            lines.Add("ArrestSuccessAudioCategories=" + config.ArrestSuccessAudioCategories);
            lines.Add("DispatchSuccessAudioCategories=" + config.DispatchSuccessAudioCategories);
            lines.Add("TransportSuccessAudioCategories=" + config.TransportSuccessAudioCategories);
            lines.Add("");
            lines.Add("CoreTickMs=" + config.CoreTickMs);
            lines.Add("AuthorityRefreshMs=" + config.AuthorityRefreshMs);
            lines.Add("NearbyPedScanMs=" + config.NearbyPedScanMs);
            lines.Add("ReactionScanMs=" + config.ReactionScanMs);
            lines.Add("DispatchCheckMs=" + config.DispatchCheckMs);
            lines.Add("RandomEventCheckSeconds=" + config.RandomEventCheckSeconds);
            lines.Add("DispatchCooldownSeconds=" + config.DispatchCooldownSeconds);
            lines.Add("EventOfferRadius=" + config.EventOfferRadius);
            lines.Add("SceneArrivalRadius=" + config.SceneArrivalRadius);
            lines.Add("InteractionRadius=" + config.InteractionRadius);
            lines.Add("ArrestRadius=" + config.ArrestRadius);
            lines.Add("ResponseDriveSpeed=" + config.ResponseDriveSpeed);
            lines.Add("ResponseDriveRadius=" + config.ResponseDriveRadius);
            lines.Add("MaxPoliceUnits=" + config.MaxPoliceUnits);
            lines.Add("MaxPatrolUnits=" + config.MaxPatrolUnits);
            lines.Add("PoliceReactionCooldownSeconds=" + config.PoliceReactionCooldownSeconds);
            lines.Add("NpcReactionCooldownSeconds=" + config.NpcReactionCooldownSeconds);
            lines.Add("CustodyWatchdogSeconds=" + config.CustodyWatchdogSeconds);
            lines.Add("ConvoyPickupRadius=" + config.ConvoyPickupRadius);
            lines.Add("ConvoyArrivalRadius=" + config.ConvoyArrivalRadius);
            lines.Add("EventCooldownSeconds=" + config.EventCooldownSeconds);
            lines.Add("AudioCooldownSeconds=" + config.AudioCooldownSeconds);
            lines.Add("InitialFallbackMinSeconds=" + config.InitialFallbackMinSeconds);
            lines.Add("InitialFallbackMaxSeconds=" + config.InitialFallbackMaxSeconds);
            lines.Add("FallbackEventMinSeconds=" + config.FallbackEventMinSeconds);
            lines.Add("FallbackEventMaxSeconds=" + config.FallbackEventMaxSeconds);
            lines.Add("GangAttackOfferCooldownSeconds=" + config.GangAttackOfferCooldownSeconds);
            lines.Add("ReportHeartbeatSeconds=" + config.ReportHeartbeatSeconds);
            lines.Add("NpcActiveSceneRadius=" + config.NpcActiveSceneRadius);
            lines.Add("NpcTrafficSlowRadius=" + config.NpcTrafficSlowRadius);
            lines.Add("NpcFleeReactionRadius=" + config.NpcFleeReactionRadius);
            lines.Add("NpcInteractionPreparationSeconds=" + config.NpcInteractionPreparationSeconds);
            lines.Add("NpcTrafficStopTimeoutSeconds=" + config.NpcTrafficStopTimeoutSeconds);
            lines.Add("NpcTrafficFleeChancePercent=" + config.NpcTrafficFleeChancePercent);
            lines.Add("NpcCitizenFleeChancePercent=" + config.NpcCitizenFleeChancePercent);
            File.WriteAllLines(path, lines.ToArray());
        }

        private void Clamp()
        {
            if (FavoriteWeaponAmmo < 1) FavoriteWeaponAmmo = 1;
            if (FavoriteWeaponAmmo > 9999) FavoriteWeaponAmmo = 9999;
            if (FavoriteWeaponTint < 0) FavoriteWeaponTint = 0;
            if (FavoriteWeaponTint > 7) FavoriteWeaponTint = 7;

            CoreTickMs = Math.Max(100, CoreTickMs);
            AuthorityRefreshMs = Math.Max(500, AuthorityRefreshMs);
            NearbyPedScanMs = Math.Max(900, NearbyPedScanMs);
            ReactionScanMs = Math.Max(1000, ReactionScanMs);
            DispatchCheckMs = Math.Max(250, DispatchCheckMs);
            RandomEventCheckSeconds = Math.Max(5, RandomEventCheckSeconds);
            DispatchCooldownSeconds = Math.Max(30, DispatchCooldownSeconds);
            EventOfferRadius = Math.Max(150, Math.Min(1200, EventOfferRadius));
            SceneArrivalRadius = Math.Max(10, Math.Min(60, SceneArrivalRadius));
            InteractionRadius = Math.Max(2, Math.Min(12, InteractionRadius));
            ArrestRadius = Math.Max(2, Math.Min(12, ArrestRadius));
            ResponseDriveSpeed = Math.Max(15, Math.Min(60, ResponseDriveSpeed));
            ResponseDriveRadius = Math.Max(8, Math.Min(40, ResponseDriveRadius));
            MaxPoliceUnits = Math.Max(1, Math.Min(4, MaxPoliceUnits));
            MaxPatrolUnits = Math.Max(1, Math.Min(2, MaxPatrolUnits));
            PoliceReactionCooldownSeconds = Math.Max(3, PoliceReactionCooldownSeconds);
            NpcInteractionTimeoutSeconds = Math.Max(10, Math.Min(90, NpcInteractionTimeoutSeconds));
            NpcDocumentPresentationSeconds = Math.Max(1, Math.Min(6, NpcDocumentPresentationSeconds));
            NpcFleeChancePercent = Math.Max(0, Math.Min(100, NpcFleeChancePercent));
            NpcStationFollowUpChancePercent = Math.Max(0, Math.Min(100, NpcStationFollowUpChancePercent));
            TrafficStopTimeoutSeconds = Math.Max(10, Math.Min(90, TrafficStopTimeoutSeconds));
            TrafficStopFleeChancePercent = Math.Max(0, Math.Min(100, TrafficStopFleeChancePercent));
            TrafficStopSearchRadius = Math.Max(15, Math.Min(80, TrafficStopSearchRadius));
            CompletedEntityCleanupGraceSeconds = Math.Max(3, Math.Min(60, CompletedEntityCleanupGraceSeconds));
            CompletedEntityCleanupDistance = Math.Max(25, Math.Min(200, CompletedEntityCleanupDistance));
            CompletedEntityCleanupMaxSeconds = Math.Max(30, Math.Min(600, CompletedEntityCleanupMaxSeconds));
            ConvoyCleanupGraceSeconds = Math.Max(3, Math.Min(60, ConvoyCleanupGraceSeconds));
            ConvoyCleanupDistance = Math.Max(25, Math.Min(200, ConvoyCleanupDistance));
            ConvoyCleanupMaxSeconds = Math.Max(30, Math.Min(600, ConvoyCleanupMaxSeconds));
            NpcReactionCooldownSeconds = Math.Max(3, NpcReactionCooldownSeconds);
            CustodyWatchdogSeconds = Math.Max(1, CustodyWatchdogSeconds);
            ConvoyPickupRadius = Math.Max(10, Math.Min(50, ConvoyPickupRadius));
            ConvoyArrivalRadius = Math.Max(10, Math.Min(50, ConvoyArrivalRadius));
            EventCooldownSeconds = Math.Max(30, EventCooldownSeconds);
            AudioCooldownSeconds = Math.Max(3, AudioCooldownSeconds);
            InitialFallbackMinSeconds = Math.Max(10, InitialFallbackMinSeconds);
            InitialFallbackMaxSeconds = Math.Max(InitialFallbackMinSeconds, InitialFallbackMaxSeconds);
            FallbackEventMinSeconds = Math.Max(60, FallbackEventMinSeconds);
            FallbackEventMaxSeconds = Math.Max(FallbackEventMinSeconds, FallbackEventMaxSeconds);
            GangAttackOfferCooldownSeconds = Math.Max(10, GangAttackOfferCooldownSeconds);
            ReportHeartbeatSeconds = Math.Max(5, ReportHeartbeatSeconds);
            NpcActiveSceneRadius = Math.Max(12, Math.Min(60, NpcActiveSceneRadius));
            NpcTrafficSlowRadius = Math.Max(6, Math.Min(20, NpcTrafficSlowRadius));
            NpcFleeReactionRadius = Math.Max(8, Math.Min(35, NpcFleeReactionRadius));
            NpcInteractionPreparationSeconds = Math.Max(0, Math.Min(4, NpcInteractionPreparationSeconds));
            NpcTrafficStopTimeoutSeconds = Math.Max(10, Math.Min(90, NpcTrafficStopTimeoutSeconds));
            NpcTrafficFleeChancePercent = Math.Max(0, Math.Min(100, NpcTrafficFleeChancePercent));
            NpcCitizenFleeChancePercent = Math.Max(0, Math.Min(100, NpcCitizenFleeChancePercent));
        }

        private static string Read(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;
        }

        private static Keys ReadKey(Dictionary<string, string> values, string key, Keys fallback)
        {
            string raw = Read(values, key, fallback.ToString());
            if (string.IsNullOrWhiteSpace(raw))
                return Keys.None;

            Keys parsed;
            if (Enum.TryParse<Keys>(raw.Trim(), true, out parsed))
                return parsed;

            LspdResponseLog.Write("POLICE_CONFIG", "Invalid key binding | " + key + "=" + raw + " | Using=" + fallback);
            return fallback;
        }

        private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback)
        {
            bool value;
            return bool.TryParse(Read(values, key, fallback.ToString()), out value) ? value : fallback;
        }

        private static int ReadInt(Dictionary<string, string> values, string key, int fallback)
        {
            int value;
            return int.TryParse(Read(values, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }
    }
}

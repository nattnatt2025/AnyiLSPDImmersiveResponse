using GTA;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace AnyiLSPD
{
    public enum LspdCitizenState
    {
        Inactive,
        Calm,
        Cooperative,
        ArmedAndWary,
        UnderInvestigation,
        Threatened,
        Escalating
    }

    // The only Citizen-mode world owner. UI stays in LSPDMainUI and does not
    // scan peds or direct police by itself.
    public sealed class LSPDCitizenCore : Script
    {

        private static readonly object EmbeddedLogSync = new object();
        private static DateTime _nextDirectHeartbeat = DateTime.MinValue;

        private static string EmbeddedLogPath
        {
            get
            {
                try
                {
                    string location = Assembly.GetExecutingAssembly().Location;
                    string directory = Path.GetDirectoryName(location);

                    if (!string.IsNullOrWhiteSpace(directory))
                        return Path.Combine(directory, "AnyiLSPD_DEBUG_DIRECT.log");
                }
                catch
                {
                }

                return Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "scripts",
                    "AnyiLSPD_DEBUG_DIRECT.log");
            }
        }

        private static void DirectLog(string category, string message)
        {
            try
            {
                string path = EmbeddedLogPath;
                string directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                lock (EmbeddedLogSync)
                {
                    using (FileStream stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                            " | " +
                            (string.IsNullOrWhiteSpace(category) ? "LOG" : category) +
                            " | " +
                            (message ?? string.Empty));

                        writer.Flush();
                    }
                }
            }
            catch
            {
                // Never let diagnostics kill the GTA script.
            }
        }
        private static void DirectException(string category, Exception ex)
        {
            if (ex == null)
                return;

            DirectLog(
                category,
                ex.GetType().FullName +
                " | Message=" + ex.Message +
                " | Stack=" + ex.StackTrace);
        }



        public static LSPDCitizenCore Instance { get; private set; }

        private readonly LspdGangTurfContext _gangContext =
            new LspdGangTurfContext();
        private readonly LSPDCitizenReactFromGangAndViolentNPC _threatReact =
            new LSPDCitizenReactFromGangAndViolentNPC();
        private readonly LspdPoliceCitizenReactFromLspd _policeReact =
            new LspdPoliceCitizenReactFromLspd();

        private readonly LspdCitizenSnapshot _snapshot =
            new LspdCitizenSnapshot();

        private string _uiConfigPath;
        private string _citizenConfigPath;
        private LspdResponseUiConfig _uiConfig;
        private LspdCitizenConfig _config;
        private DateTime _nextRoleRefreshAt;
        private DateTime _nextConfigRefreshAt;
        private DateTime _nextStateScanAt;
        private DateTime _nextNearbyScanAt;
        private DateTime _nextPoliceReactionAt;
        private DateTime _nextGangDataRefreshAt;
        private DateTime _nextHeartbeatAt;
        private DateTime _aggressionUntil;
        private DateTime _assuranceUntil;
        private DateTime _threatUntil;
        private DateTime _collisionCooldownUntil;
        private int _previousPlayerHealth = -1;
        private Ped _rememberedThreat;
        private bool _rememberedThreatIsKnownGangMember;
        private LspdCitizenState _lastLoggedState = LspdCitizenState.Inactive;
        private int _stateChanges;

        public LspdCitizenState CurrentState
        {
            get { return _snapshot.State; }
        }

        public string CurrentTurfName
        {
            get { return _snapshot.CurrentTurfName ?? "No mapped turf"; }
        }

        public string StatusLine
        {
            get
            {
                return "Citizen: " + DisplayState(_snapshot.State) +
                       " | Stars: " + _snapshot.WantedLevel;
            }
        }

        public LSPDCitizenCore()
        {
            Instance = this;
            DirectLog("CITIZEN_DIRECT_BOOT", "Citizen core constructor entered.");
            LspdResponseLog.EnsureInitialized();

            _uiConfigPath = Path.Combine(
                LspdResponseLog.ScriptDirectory,
                LspdResponseUiConfig.FileName);
            _citizenConfigPath = Path.Combine(
                LspdResponseLog.ScriptDirectory,
                LspdCitizenConfig.FileName);

            _uiConfig = LspdResponseUiConfig.LoadOrCreate(
                _uiConfigPath,
                LogFromConfig);
            _config = LspdCitizenConfig.LoadOrCreate(
                LspdResponseLog.ScriptDirectory);
            Interval = _config.CoreTickMs;

            _gangContext.Load(_config.GangDataRoot);
            DirectLog(
                "CITIZEN_DIRECT_DATA",
                "Gang context loaded | Root=" + _config.GangDataRoot);
            _snapshot.State = LspdCitizenState.Inactive;

            Tick += OnTick;
            Aborted += OnAborted;

            LspdResponseLog.Write(
                "CITIZEN_BOOT",
                "Enabled=" + _config.Enabled +
                " | ActiveRole=" + _uiConfig.ActiveRole +
                " | CoreTickMs=" + Interval);
        }

        public string GreetPolice()
        {
            if (!IsCitizenRoleActive())
                return "Select Los Santos Citizen before using Citizen interactions.";

            string result = _policeReact.GreetNearestOfficer(_snapshot);
            LspdResponseLog.Write("CITIZEN_ACTION", "Greet Police | " + result);
            return result;
        }

        public string InteractWithPolice()
        {
            if (!IsCitizenRoleActive())
                return "Select Los Santos Citizen before using Citizen interactions.";

            string result = _policeReact.InteractWithNearestOfficer(_snapshot);
            LspdResponseLog.Write("CITIZEN_ACTION", "Interact with Police | " + result);
            return result;
        }

        public string MakeAssurance()
        {
            if (!IsCitizenRoleActive())
                return "Select Los Santos Citizen before using Citizen interactions.";

            _assuranceUntil = DateTime.UtcNow.AddSeconds(
                _config.AssuranceSeconds);

            LspdResponseLog.Write(
                "CITIZEN_ASSURANCE",
                "Assurance/Cooperative requested | Wanted=" +
                _snapshot.WantedLevel +
                " | Threat=" + DescribePed(_snapshot.ImmediateThreat) +
                " | Until=" + _assuranceUntil.ToString("HH:mm:ss"));

            string result = _policeReact.MakeAssurance(_snapshot);
            LspdResponseLog.Write(
                "CITIZEN_ACTION",
                "Make Assurance | Until=" +
                _assuranceUntil.ToString("HH:mm:ss") + " | " + result);
            return result;
        }

        public string CallDispatch()
        {
            if (!IsCitizenRoleActive())
                return "Select Los Santos Citizen before requesting Citizen assistance.";

            if (_snapshot.ImmediateThreat == null ||
                !_snapshot.ImmediateThreat.Exists() ||
                _snapshot.ImmediateThreat.IsDead)
            {
                string noThreat =
                    "No immediate attacker was detected. Citizen dispatch is reserved for a real nearby threat.";
                LspdResponseLog.Write("CITIZEN_ACTION", "Call Dispatch | " + noThreat);
                return noThreat;
            }

            bool assigned = _policeReact.RequestSupport(
                _snapshot,
                _config,
                "manual citizen assistance request");

            string result = assigned
                ? "Citizen assistance request sent. Police are responding to the nearby attacker."
                : "Citizen assistance request logged, but no safe police unit could be assigned yet.";

            LspdResponseLog.Write("CITIZEN_ACTION", "Call Dispatch | " + result);
            return result;
        }

        public void ResetCitizenRuntime()
        {
            _aggressionUntil = DateTime.MinValue;
            _assuranceUntil = DateTime.MinValue;
            _threatUntil = DateTime.MinValue;
            _rememberedThreat = null;
            _rememberedThreatIsKnownGangMember = false;
            _snapshot.ImmediateThreat = null;
            _snapshot.ImmediateThreatIsKnownGangMember = false;
            _policeReact.ResetCitizenState();
            LspdResponseLog.Write(
                "CITIZEN_RESET",
                "Citizen-owned temporary state cleared. Wanted level and external mods were not changed.");
        }

        public void RequestImmediateRefresh()
        {
            _nextRoleRefreshAt = DateTime.MinValue;
            _nextStateScanAt = DateTime.MinValue;
            _nextNearbyScanAt = DateTime.MinValue;
        }

        public void WriteDiagnosticReport(string reason)
        {
            List<string> lines = new List<string>();
            lines.Add("Reason: " + reason);
            lines.Add("Selected role: " + _uiConfig.ActiveRole);
            lines.Add("Citizen enabled: " + _config.Enabled);
            lines.Add("Citizen state: " + DisplayState(_snapshot.State));
            lines.Add("Wanted level: " + _snapshot.WantedLevel);
            lines.Add("Weapon drawn: " + _snapshot.HasWeaponDrawn);
            lines.Add("Recent aggression: " + _snapshot.IsRecentAggression);
            lines.Add("Immediate threat: " + DescribePed(_snapshot.ImmediateThreat));
            lines.Add("Threat is known Gang & Turf model: " + _snapshot.ImmediateThreatIsKnownGangMember);
            lines.Add("Current turf: " + (_snapshot.CurrentTurfName ?? "none"));
            lines.Add("Turf owner: " + (_snapshot.CurrentTurfOwner ?? "none"));
            lines.Add("Gang data player gang: " + (_gangContext.PlayerGangName ?? "none"));
            lines.Add("Gang member models loaded: " + _gangContext.KnownMemberModelCount);
            lines.Add("Turf zones loaded: " + _gangContext.TurfZoneCount);
            lines.Add("State changes this run: " + _stateChanges);
            lines.Add("Continuous chaos threshold: " + _config.ContinuousChaosWantedLevel);
            lines.Add("Assurance active: " + _snapshot.AssuranceActive);
            lines.Add("Assurance behavior: de-escalation only; Citizen Core owns no bust, weapon-removal, or station-teleport operation.");
            lines.Add("Scope: Citizen mode only. No military, aircraft, tanks, global wanted-level manipulation, or Gang & Turf XML writing exists in this build.");

            LspdResponseLog.WriteReport(
                "LSPD RESPONSE CITIZEN DIAGNOSTIC REPORT",
                lines);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                DateTime now = DateTime.UtcNow;

                RefreshConfigurationIfDue(now);
                RefreshRoleIfDue(now);

                if (!IsCitizenRoleActive())
                {
                    SetInactiveIfNeeded();
                    return;
                }

                if (now >= _nextGangDataRefreshAt)
                {
                    _gangContext.Load(_config.GangDataRoot);
                    _nextGangDataRefreshAt = now.AddSeconds(
                        _config.GangDataRefreshSeconds);
                }

                if (now >= _nextStateScanAt)
                {
                    UpdatePlayerState(now);
                    _nextStateScanAt = now.AddMilliseconds(
                        _config.StateScanMs);
                }

                if (now >= _nextNearbyScanAt)
                {
                    UpdateNearbyContext(now);
                    _nextNearbyScanAt = now.AddMilliseconds(
                        _config.NearbyPedScanMs);
                }

                UpdateCitizenState(now);

                if (_snapshot.WantedLevel >= _config.ContinuousChaosWantedLevel &&
                    (_snapshot.IsRecentAggression || _snapshot.IsShooting))
                {
                    LspdResponseLog.Write(
                        "CITIZEN_CHAOS_GATE",
                        "Continuous chaos gate active | Stars=" +
                        _snapshot.WantedLevel +
                        " | Threshold=" +
                        _config.ContinuousChaosWantedLevel +
                        " | Shooting=" +
                        _snapshot.IsShooting +
                        " | AggressionMemory=" +
                        _snapshot.IsRecentAggression);
                }

                if (now >= _nextPoliceReactionAt)
                {
                    _policeReact.Update(_snapshot, _config, now);
                    _nextPoliceReactionAt = now.AddMilliseconds(
                        _config.PoliceReactionMs);
                }

                LogStateTransitionIfNeeded();

                if (now >= _nextDirectHeartbeat)
                {
                    DirectLog(
                        "CITIZEN_HEARTBEAT",
                        StatusLine +
                        " | Threat=" + DescribePed(_snapshot.ImmediateThreat) +
                        " | Turf=" + (_snapshot.CurrentTurfName ?? "none") +
                        " | Owner=" + (_snapshot.CurrentTurfOwner ?? "none"));
                    _nextDirectHeartbeat = now.AddSeconds(2);
                }

                if (now >= _nextHeartbeatAt)
                {
                    LspdResponseLog.Write(
                        "CITIZEN_HEARTBEAT",
                        StatusLine +
                        " | Threat=" + DescribePed(_snapshot.ImmediateThreat) +
                        " | Turf=" + (_snapshot.CurrentTurfName ?? "none"));
                    _nextHeartbeatAt = now.AddSeconds(_config.HeartbeatSeconds);
                }
            }
            catch (Exception ex)
            {
                DirectException("CITIZEN_TICK_ERROR", ex);
                LspdResponseLog.WriteException("CITIZEN_TICK_ERROR", ex);
            }
        }

        private void OnAborted(object sender, EventArgs e)
        {
            ResetCitizenRuntime();
            WriteDiagnosticReport("Citizen core aborted or GTA is closing.");
            LspdResponseLog.Write("CITIZEN_STOP", "Citizen core stopped.");

            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        private void RefreshConfigurationIfDue(DateTime now)
        {
            if (now < _nextConfigRefreshAt)
                return;

            _config = LspdCitizenConfig.LoadOrCreate(
                LspdResponseLog.ScriptDirectory);
            Interval = _config.CoreTickMs;
            _nextConfigRefreshAt = now.AddSeconds(_config.ConfigReloadSeconds);
        }

        private void RefreshRoleIfDue(DateTime now)
        {
            if (now < _nextRoleRefreshAt)
                return;

            _uiConfig = LspdResponseUiConfig.LoadOrCreate(
                _uiConfigPath,
                LogFromConfig);
            _nextRoleRefreshAt = now.AddMilliseconds(_config.RoleRefreshMs);
        }

        private bool IsCitizenRoleActive()
        {
            return _config != null && _config.Enabled &&
                   _uiConfig != null &&
                   _uiConfig.ActiveRole == LspdResponseRole.LosSantosCitizen;
        }

        private void SetInactiveIfNeeded()
        {
            if (_snapshot.State == LspdCitizenState.Inactive)
                return;

            _snapshot.State = LspdCitizenState.Inactive;
            _snapshot.ImmediateThreat = null;
            _policeReact.ResetCitizenState();
            LspdResponseLog.Write(
                "CITIZEN_STATE",
                "Citizen layer inactive because the selected role is not Los Santos Citizen.");
        }

        private void UpdatePlayerState(DateTime now)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
            {
                _snapshot.State = LspdCitizenState.Inactive;
                return;
            }

            _snapshot.Player = player;
            _snapshot.WantedLevel = Game.Player.Wanted.WantedLevel;
            _snapshot.HealthDropped = _previousPlayerHealth > player.Health;
            _previousPlayerHealth = player.Health;

            Weapon weapon = player.Weapons.Current;
            _snapshot.HasWeaponDrawn = weapon != null &&
                weapon.Hash != WeaponHash.Unarmed &&
                weapon.Hash != WeaponHash.Parachute;
            _snapshot.IsShooting = player.IsShooting;
            _snapshot.IsAimingWeapon = player.IsAiming &&
                                      _snapshot.HasWeaponDrawn;

            bool vehicleImpact = false;
            Vehicle vehicle = player.CurrentVehicle;
            if (vehicle != null && vehicle.Exists() &&
                vehicle.HasCollided &&
                vehicle.Speed >= _config.RecklessImpactSpeed &&
                now >= _collisionCooldownUntil)
            {
                vehicleImpact = true;
                _collisionCooldownUntil = now.AddSeconds(8);
                LspdResponseLog.Write(
                    "CITIZEN_VEHICLE",
                    "High-speed collision observed | Speed=" +
                    vehicle.Speed.ToString("0.0"));
            }

            _snapshot.VehicleImpact = vehicleImpact;
            bool aggressiveNow = player.IsShooting ||
                                 player.IsInMeleeCombat ||
                                 vehicleImpact;

            if (aggressiveNow)
                _aggressionUntil = now.AddSeconds(
                    _config.AggressionMemorySeconds);

            _snapshot.IsRecentAggression = now < _aggressionUntil;
            _snapshot.AssuranceActive = now < _assuranceUntil;
        }

        private void UpdateNearbyContext(DateTime now)
        {
            if (_snapshot.Player == null || !_snapshot.Player.Exists())
                return;

            float scanRadius = Math.Max(
                _config.ThreatRadius,
                _config.PoliceAssistRadius);
            _snapshot.NearbyPeds = World.GetNearbyPeds(
                _snapshot.Player,
                scanRadius);

            bool knownGangMember;
            Ped threat = _threatReact.FindImmediateThreat(
                _snapshot,
                _config,
                _gangContext,
                out knownGangMember);

            if (threat != null && threat.Exists() && !threat.IsDead)
            {
                _rememberedThreat = threat;
                _rememberedThreatIsKnownGangMember = knownGangMember;
                _threatUntil = now.AddSeconds(_config.ThreatMemorySeconds);
            }

            if (_rememberedThreat != null &&
                _rememberedThreat.Exists() &&
                !_rememberedThreat.IsDead &&
                now < _threatUntil)
            {
                _snapshot.ImmediateThreat = _rememberedThreat;
                _snapshot.ImmediateThreatIsKnownGangMember =
                    _rememberedThreatIsKnownGangMember;
            }
            else
            {
                _rememberedThreat = null;
                _rememberedThreatIsKnownGangMember = false;
                _snapshot.ImmediateThreat = null;
                _snapshot.ImmediateThreatIsKnownGangMember = false;
            }

            LspdTurfZone zone = _gangContext.FindZone(
                _snapshot.Player.Position);
            _snapshot.CurrentTurfName = zone == null ? null : zone.Name;
            _snapshot.CurrentTurfOwner = zone == null
                ? null
                : zone.OwnerGangName;
        }

        private void UpdateCitizenState(DateTime now)
        {
            if (_snapshot.Player == null || !_snapshot.Player.Exists())
            {
                _snapshot.State = LspdCitizenState.Inactive;
                return;
            }

            if (_snapshot.ImmediateThreat != null)
            {
                _snapshot.State = LspdCitizenState.Threatened;
                return;
            }

            if (_snapshot.WantedLevel > _config.MildWantedMaximum ||
                _snapshot.IsRecentAggression)
            {
                _snapshot.State = LspdCitizenState.Escalating;
                return;
            }

            if (_snapshot.WantedLevel > 0)
            {
                _snapshot.State = LspdCitizenState.UnderInvestigation;
                return;
            }

            if (_snapshot.AssuranceActive)
            {
                _snapshot.State = LspdCitizenState.Cooperative;
                return;
            }

            if (_snapshot.HasWeaponDrawn)
            {
                _snapshot.State = LspdCitizenState.ArmedAndWary;
                return;
            }

            _snapshot.State = LspdCitizenState.Calm;
        }

        private void LogStateTransitionIfNeeded()
        {
            if (_lastLoggedState == _snapshot.State)
                return;

            _lastLoggedState = _snapshot.State;
            _stateChanges++;

            LspdResponseLog.Write(
                "CITIZEN_STATE",
                DisplayState(_snapshot.State) +
                " | Stars=" + _snapshot.WantedLevel +
                " | Armed=" + _snapshot.HasWeaponDrawn +
                " | Threat=" +
                LSPDCitizenReactFromGangAndViolentNPC.DescribeThreat(
                    _snapshot.ImmediateThreat,
                    _snapshot.ImmediateThreatIsKnownGangMember));

            if (_snapshot.State == LspdCitizenState.UnderInvestigation)
            {
                Notification.PostTicker(
                    "~b~LSPD Response~s~\nLow-level incident: officers are observing and investigating.",
                    false,
                    false);
            }
            else if (_snapshot.State == LspdCitizenState.Threatened)
            {
                Notification.PostTicker(
                    "~r~LSPD Response~s~\nNearby attacker detected. Citizen protection is responding.",
                    false,
                    false);
            }
            else if (_snapshot.State == LspdCitizenState.Escalating &&
                     _snapshot.WantedLevel >= 3)
            {
                Notification.PostTicker(
                    "~o~LSPD Response~s~\nHigh wanted level detected. Citizen mode leaves tactical and military response disabled for this stability pass.",
                    false,
                    false);
            }
        }

        private static string DisplayState(LspdCitizenState state)
        {
            switch (state)
            {
                case LspdCitizenState.Calm:
                    return "Calm Citizen";
                case LspdCitizenState.Cooperative:
                    return "Cooperative Citizen";
                case LspdCitizenState.ArmedAndWary:
                    return "Armed / Wary";
                case LspdCitizenState.UnderInvestigation:
                    return "Under Investigation";
                case LspdCitizenState.Threatened:
                    return "Threatened";
                case LspdCitizenState.Escalating:
                    return "Escalating";
                default:
                    return "Inactive";
            }
        }

        private static string DescribePed(Ped ped)
        {
            return ped != null && ped.Exists()
                ? "Ped=" + ped.Handle + " Model=" + ped.Model.Hash
                : "none";
        }

        private static void LogFromConfig(string text)
        {
            LspdResponseLog.Write("UI_CONFIG", text);
        }
    }

    public sealed class LspdCitizenSnapshot
    {
        public Ped Player;
        public Ped[] NearbyPeds = new Ped[0];
        public int WantedLevel;
        public bool HealthDropped;
        public bool HasWeaponDrawn;
        public bool IsAimingWeapon;
        public bool IsShooting;
        public bool VehicleImpact;
        public bool IsRecentAggression;
        public bool AssuranceActive;
        public Ped ImmediateThreat;
        public bool ImmediateThreatIsKnownGangMember;
        public string CurrentTurfName;
        public string CurrentTurfOwner;
        public LspdCitizenState State = LspdCitizenState.Inactive;
    }
}

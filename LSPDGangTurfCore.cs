using GTA;
using GTA.UI;
using GTA.Math;
using System;
using System.Collections.Generic;
using System.IO;

namespace AnyiLSPD
{
    public enum LspdGangState
    {
        Inactive,
        CalmLeader,
        TerritoryWatch,
        PoliceInvestigation,
        ActiveConflict,
        HighConflict
    }

    public sealed class LSPDGangTurfCore
    {
        public static LSPDGangTurfCore Instance { get; private set; }

        private readonly LspdGangMemberTerritoryCore _memberCore =
            new LspdGangMemberTerritoryCore();
        private readonly LspdPoliceReactToGangMemberAndTerritory _policeReact =
            new LspdPoliceReactToGangMemberAndTerritory();
        private readonly LspdPoliceReactToGangEnemyAndTerritory _enemyReact =
            new LspdPoliceReactToGangEnemyAndTerritory();

        private AnyiLSPDPoliceData.GangSnapshot _data;
        private LspdGangConfig _config;
        private LspdResponseUiConfig _uiConfig;
        private LspdGangProfileCore _profile;
        private Ped[] _nearby = new Ped[0];
        private Ped _enemyThreat;
        private AnyiLSPDPoliceData.GangProfile _enemyGang;
        private AnyiLSPDPoliceData.TurfZone _currentTurf;
        private GangPoliceState _policeState = new GangPoliceState();
        private LspdGangState _state = LspdGangState.Inactive;
        private DateTime _nextStateScan;
        private DateTime _nextNearbyScan;
        private DateTime _nextPoliceScan;
        private DateTime _nextConfigScan;
        private DateTime _nextDataRefresh;
        private DateTime _nextRoleRefresh;
        private DateTime _aggressionUntil;
        private Vector3 _lastGangConflictPosition;
        private DateTime _lastGangConflictAt;
        private int _lastKnownPlayerGangMemberCount = -1;
        private int _stateChanges;
        private LspdGangState _lastLoggedState = LspdGangState.Inactive;
        private readonly string _uiConfigPath;
        private bool _shutdown;
        private int _interval = 250;
        // Conflict support is owned exclusively by LspdGangMemberTerritoryCore.
        // Keeping a second support list here caused duplicate spawns and unsafe
        // combat tasks.
        private DateTime _nextHeartbeat;
        private int _heartbeatCounter;
        public LspdGangState CurrentState { get { return _state; } }
        public string CurrentTurfName { get { return _currentTurf == null ? "None" : _currentTurf.Name; } }
        public string CurrentTurfOwner { get { return _currentTurf == null ? "none" : _currentTurf.OwnerGang; } }
        public string PlayerGangName { get { return LspdGangIdentityContext.PlayerGangName == "none" && _profile != null ? _profile.PlayerGangName : LspdGangIdentityContext.PlayerGangName; } }
        public string PoliceStatusLine { get { return _policeState == null ? "Inactive" : _policeState.ResponseStage; } }
        public string StatusLine
        {
            get
            {
                return "Gang: " + PlayerGangName +
                       " | Turf: " + CurrentTurfName +
                       " | Stars: " + GetWantedLevel();
            }
        }

        public int PlayerGangMemberModelCount
        {
            get
            {
                return _profile == null || _profile.PlayerGang == null ||
                       _profile.PlayerGang.MemberHashes == null
                    ? 0
                    : _profile.PlayerGang.MemberHashes.Count;
            }
        }

        public int PlayerGangVehicleModelCount
        {
            get
            {
                return _profile == null || _profile.PlayerGang == null ||
                       _profile.PlayerGang.VehicleHashes == null
                    ? 0
                    : _profile.PlayerGang.VehicleHashes.Count;
            }
        }

        public List<int> GetPlayerGangMemberModelHashes()
        {
            List<int> result = new List<int>();
            if (_profile == null || _profile.PlayerGang == null ||
                _profile.PlayerGang.MemberHashes == null)
                return result;

            foreach (int hash in _profile.PlayerGang.MemberHashes)
            {
                if (hash != 0)
                    result.Add(hash);
            }
            return result;
        }

        public List<int> GetPlayerGangVehicleModelHashes()
        {
            List<int> result = new List<int>();
            if (_profile == null || _profile.PlayerGang == null ||
                _profile.PlayerGang.VehicleHashes == null)
                return result;

            foreach (int hash in _profile.PlayerGang.VehicleHashes)
            {
                if (hash != 0)
                    result.Add(hash);
            }
            return result;
        }

        public LSPDGangTurfCore()
        {
            Instance = this;
            LspdResponseLog.EnsureInitialized();
            _config = LspdGangConfig.LoadOrCreate(LspdResponseLog.ScriptDirectory);
            _uiConfigPath = Path.Combine(LspdResponseLog.ScriptDirectory, LspdResponseUiConfig.FileName);
            _uiConfig = LspdResponseUiConfig.LoadOrCreate(_uiConfigPath, LogUi);
            _interval = _config.CoreTickMs;

            DateTime bootNow = DateTime.UtcNow;
            LoadData(bootNow);
            LspdResponseLog.Write(
                "GANG_BOOT",
                "Paths | ScriptDirectory=" + LspdResponseLog.ScriptDirectory +
                " | Config=" + Path.Combine(LspdResponseLog.ScriptDirectory, LspdGangConfig.FileName) +
                " | UIConfig=" + _uiConfigPath +
                " | GangRoot=" + ResolveGangRoot() +
                " | GangDirExists=" + Directory.Exists(ResolveGangRoot()));
            LspdResponseLog.Write(
                "GANG_BOOT",
                "Gang core loaded | Enabled=" + _config.Enabled +
                " | PlayerGang=" + PlayerGangName +
                " | DataRoot=" + ResolveGangRoot());
        }

        public string GreetGangMember()
        {
            if (!IsGangRoleActive())
                return "Select Gang Turf Leader before using Gang interactions.";

            Ped player = Game.Player.Character;
            string result = _memberCore.GreetMember(
                player,
                _nearby,
                _profile,
                _config.GangInteractionRadius);
            LspdResponseLog.Write("GANG_ACTION", "Greet Gang Member | " + result);
            return result;
        }

        public string InteractWithGangMember()
        {
            if (!IsGangRoleActive())
                return "Select Gang Turf Leader before using Gang interactions.";

            Ped player = Game.Player.Character;
            string result = _memberCore.InteractMember(
                player,
                _nearby,
                _profile,
                _config.GangInteractionRadius);
            LspdResponseLog.Write("GANG_ACTION", "Interact with Gang Member | " + result);
            return result;
        }

        public string TerritoryStatus()
        {
            if (!IsGangRoleActive())
                return "Gang Turf Leader is not active.";

            string result = "Gang=" + PlayerGangName +
                            " | Turf=" + CurrentTurfName +
                            " | Owner=" + CurrentTurfOwner +
                            " | Police=" + PoliceStatusLine +
                            " | State=" + DisplayState(_state);
            LspdResponseLog.Write("GANG_ACTION", "Territory Status | " + result);
            return result;
        }

        public string ResetGangResponse()
        {
            ResetRuntime();
            string result = "Gang response state reset. Gang & Turf XML was not modified.";
            LspdResponseLog.Write("GANG_RESET", result);
            return result;
        }

        public void RequestImmediateRefresh()
        {
            _nextStateScan = DateTime.MinValue;
            _nextNearbyScan = DateTime.MinValue;
            _nextPoliceScan = DateTime.MinValue;
            _nextDataRefresh = DateTime.MinValue;
        }

        public void ResetRuntime()
        {
            _policeReact.Reset();
            _enemyReact.Reset();
            _memberCore.ClearOwnedTaskState();
            _enemyThreat = null;
            _enemyGang = null;
            _currentTurf = null;
            _nearby = new Ped[0];
            _state = LspdGangState.Inactive;
            _aggressionUntil = DateTime.MinValue;
            _stateChanges = 0;
            _lastGangConflictPosition = Vector3.Zero;
            _lastGangConflictAt = DateTime.MinValue;
            _lastKnownPlayerGangMemberCount = -1;
            LspdGangIdentityContext.Reset();
        }

        public void WriteDiagnosticReport(string reason)
        {
            List<string> lines = new List<string>();
            lines.Add("Reason: " + reason);
            lines.Add("Selected role: " + _uiConfig.ActiveRole);
            lines.Add("Gang enabled: " + _config.Enabled);
            lines.Add("Player gang: " + PlayerGangName);
            lines.Add("Current turf: " + CurrentTurfName);
            lines.Add("Turf owner: " + CurrentTurfOwner);
            lines.Add("Gang state: " + DisplayState(_state));
            lines.Add("Police response: " + PoliceStatusLine);
            lines.Add("Wanted level: " + GetWantedLevel());
            lines.Add("Recent aggression: " + IsRecentAggression());
            lines.Add("Enemy threat: " + DescribePed(_enemyThreat));
            lines.Add("Enemy gang: " + (_enemyGang == null ? "none" : _enemyGang.Name));
            lines.Add("Gangs loaded: " + (_data == null ? 0 : _data.Gangs.Count));
            lines.Add("Member pool entries: " + (_data == null ? 0 : _data.MemberPoolHashes.Count));
            lines.Add("Turf zones: " + (_data == null ? 0 : _data.TurfZones.Count));
            lines.Add("Player gang member model hashes: " + GetPlayerGangMemberHashCount());
            lines.Add("Last gang conflict position: " + _lastGangConflictPosition);
            lines.Add("Last gang conflict age: " + DescribeConflictAge());
            lines.Add("Config: PoliceWaryRadius=" + _config.PoliceWaryRadius +
                      " | TerritoryWaryRadius=" + _config.TerritoryWaryRadius +
                      " | GangProtectionRadius=" + _config.GangProtectionRadius +
                      " | DefaultTurfRadius=" + _config.DefaultTurfRadius +
                      " | PursuitBreakDistance=" + _config.PursuitBreakDistance);
            lines.Add("Military response implementation: OFF in Gang Turf 1.0 stability pass.");
            lines.Add("Gang & Turf XML write access: NONE (read-only).");

            LspdResponseLog.WriteReport(
                "LSPD RESPONSE GANG TURF DIAGNOSTIC REPORT",
                lines);
        }

        public void Update()
        {
            if (_shutdown)
                return;

            try
            {
                DateTime now = DateTime.UtcNow;
                RefreshConfiguration(now);
                RefreshRole(now);

                if (!IsGangRoleActive())
                {
                    if (_state != LspdGangState.Inactive)
                    {
                        ResetRuntime();
                        LspdResponseLog.Write("GANG_STATE", "Gang layer inactive; Citizen ownership remains untouched.");
                    }
                    return;
                }

                if (now >= _nextDataRefresh)
                {
                    LoadData(now);
                    _nextDataRefresh = now.AddSeconds(_config.DataRefreshSeconds);
                }

                if (now >= _nextStateScan)
                {
                    UpdatePlayerState(now);
                    _nextStateScan = now.AddMilliseconds(_config.StateScanMs);
                }

                if (now >= _nextNearbyScan)
                {
                    UpdateNearby(now);
                    _nextNearbyScan = now.AddMilliseconds(_config.NearbyScanMs);
                }

                if (now >= _nextPoliceScan)
                {
                    UpdatePoliceResponse(now);
                    _nextPoliceScan = now.AddMilliseconds(_config.PoliceReactionMs);
                }

                LogStateTransition();

                if (now >= _nextHeartbeat)
                {
                    WriteHeartbeat(now);
                    _nextHeartbeat = now.AddSeconds(1);
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("GANG_CORE_ERROR", ex);
            }
        }

        private void RefreshConfiguration(DateTime now)
        {
            if (now < _nextConfigScan)
                return;

            _config = LspdGangConfig.LoadOrCreate(LspdResponseLog.ScriptDirectory);
            _interval = _config.CoreTickMs;
            _nextConfigScan = now.AddSeconds(10);
        }

        private void RefreshRole(DateTime now)
        {
            if (now < _nextRoleRefresh)
                return;
            _uiConfig = LspdResponseUiConfig.LoadOrCreate(_uiConfigPath, LogUi);
            _nextRoleRefresh = now.AddSeconds(1);
        }

        private bool IsGangRoleActive()
        {
            return _config != null && _config.Enabled &&
                   _uiConfig != null &&
                   _uiConfig.ActiveRole == LspdResponseRole.GangTurfLeader;
        }

        private void LoadData(DateTime now)
        {
            string root = ResolveGangRoot();
            LspdResponseLog.Write(
                "GANG_DATA_PATH",
                "Loading root=" + root +
                " | GangData=" + File.Exists(Path.Combine(root, "GangData.xml")) +
                " | MemberPool=" + File.Exists(Path.Combine(root, "MemberPool.xml")) +
                " | TurfZoneData=" + File.Exists(Path.Combine(root, "TurfZoneData.xml")));
            _data = AnyiLSPDPoliceData.LoadGangSnapshot(root, LogData);
            LspdGangIdentityContext.Configure(
                _data,
                _config == null ? "Anyiii's Gang" : _config.PreferredPlayerGangName);
            _profile = new LspdGangProfileCore(_data);
            _memberCore.SetPreferredPlayerGangMemberHashes(GetPreferredPlayerGangMemberHashes());

            LspdResponseLog.Write(
                "GANG_DATA",
                "Refreshed | PlayerGang=" + PlayerGangName +
                " | Gangs=" + (_data == null ? 0 : _data.Gangs.Count) +
                " | MemberPool=" + (_data == null ? 0 : _data.MemberPoolHashes.Count) +
                " | TurfZones=" + (_data == null ? 0 : _data.TurfZones.Count) +
                " | PlayerGangModelHashes=" + GetPlayerGangMemberHashCount() +
                " | PlayerGangVehicleHashes=" + PlayerGangVehicleModelCount);

            if (GetPlayerGangMemberHashCount() == 0)
            {
                LspdResponseLog.Write(
                    "GANG_MEMBER_WARNING",
                    "Player-owned Gang has no memberVariations model hashes. Gang & Turf must supply the player gang models; adapter will not invent them.");
            }
        }

        private string ResolveGangRoot()
        {
            if (_config != null && !string.IsNullOrWhiteSpace(_config.GangRoot))
            {
                string configured = Environment.ExpandEnvironmentVariables(_config.GangRoot.Trim());
                if (Directory.Exists(configured))
                    return Path.GetFullPath(configured);
            }

            string scriptDirectory = LspdResponseLog.ScriptDirectory;
            string candidate = Path.GetFullPath(Path.Combine(scriptDirectory, "..", "gangModData"));
            if (Directory.Exists(candidate))
                return candidate;

            string gameDirectory = Path.GetFullPath(Path.Combine(scriptDirectory, ".."));
            return Path.Combine(gameDirectory, "gangModData");
        }

        private void UpdatePlayerState(DateTime now)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists() || player.IsDead)
                return;

            int wanted = GetWantedLevel();
            bool aggressiveNow = player.IsShooting || player.IsInMeleeCombat;
            Vehicle vehicle = player.CurrentVehicle;
            if (vehicle != null && vehicle.Exists() && vehicle.HasCollided && vehicle.Speed >= 20.0f)
                aggressiveNow = true;

            if (aggressiveNow)
                _aggressionUntil = now.AddSeconds(_config.AggressionMemorySeconds);
        }

        private bool IsRecentAggression()
        {
            return DateTime.UtcNow < _aggressionUntil;
        }

        private void UpdateNearby(DateTime now)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            int scanRadius = Math.Max(
                _config.PoliceWaryRadius,
                Math.Max(_config.GangProtectionRadius, _config.EnemyDetectionRadius));
            _nearby = World.GetNearbyPeds(player, scanRadius);

            _currentTurf = _data == null
                ? null
                : _data.GetNearestTurf(
                    player.Position.X,
                    player.Position.Y,
                    player.Position.Z,
                    _config.DefaultTurfRadius);

            if (_currentTurf == null && _data != null)
            {
                // Davis is the preferred Anyi test turf. This remains a real
                // XML lookup; it never fabricates ownership.
                _currentTurf = _data.GetPreferredTurf(
                    player.Position.X,
                    player.Position.Y,
                    player.Position.Z,
                    _config.PreferredTurfName,
                    _config.DefaultTurfRadius);
            }

            AnyiLSPDPoliceData.GangProfile enemyGang;
            _enemyThreat = _enemyReact.FindEnemyAttacker(
                player,
                _nearby,
                _profile,
                _config.EnemyDetectionRadius,
                out enemyGang);
            _enemyGang = enemyGang;

            if (_enemyThreat != null)
                _enemyReact.MarkEnemyObservation(_enemyThreat, now);

            bool gangConflictActive = _enemyThreat != null || HasGangWarInNearbyUnits();

            // If the player is not personally targeted but an actual Gang War
            // is occurring nearby, choose the external Gang member who is
            // fighting Anyiii's Gang. This keeps support useful without ever
            // treating Anyi or his own members as enemies.
            Ped supportThreat = _enemyThreat;
            if (supportThreat == null && gangConflictActive)
            {
                supportThreat = _memberCore.FindGangWarEnemyTarget(
                    player,
                    _nearby,
                    _profile,
                    _config.EnemyDetectionRadius);
            }

            if (gangConflictActive)
            {
                _lastGangConflictPosition = player.Position;
                _lastGangConflictAt = now;
            }

            int playerGangMembersNearby = CountPlayerGangMembersNearby();
            if (playerGangMembersNearby != _lastKnownPlayerGangMemberCount)
            {
                _lastKnownPlayerGangMemberCount = playerGangMembersNearby;
                LspdResponseLog.Write(
                    "GANG_MEMBER_SCAN",
                    "Nearby player-gang members=" + playerGangMembersNearby +
                    " | ModelHashes=" + GetPlayerGangMemberHashCount() +
                    " | Gang=" + PlayerGangName);
            }

            if (gangConflictActive && _enemyThreat == null)
            {
                LspdResponseLog.Write(
                    "GANG_CONFLICT",
                    "Gang conflict detected through nearby gang members; leader was not directly targeted.");
            }

            if (_config.EnableGangProtection && supportThreat != null)
            {
                int protectedMembers = _memberCore.DefendPlayer(
                    player,
                    supportThreat,
                    _nearby,
                    _profile,
                    _config.GangProtectionRadius,
                    _config.MaxProtectorsPerThreat,
                    now,
                    _config.TaskCooldownSeconds);

                if (protectedMembers > 0)
                {
                    LspdResponseLog.Write(
                        "GANG_PROTECTION",
                        "Protected Gang Leader with " + protectedMembers +
                        " member(s) | Threat=" + DescribePed(supportThreat));
                }
            }

            // This is the ONLY support lifecycle owner. When conflict ends,
            // MaintainConflictSupport(false, ...) dismisses the 3 spawned
            // protectors cleanly instead of killing them.
            _memberCore.MaintainConflictSupport(
                player,
                supportThreat,
                _config.EnableGangProtection && gangConflictActive,
                _profile,
                now,
                _config.TaskCooldownSeconds);

            UpdateCitizenWary(player, now);
        }

        private void UpdateCitizenWary(Ped player, DateTime now)
        {
            if (!_config.EnableCitizenWary || player == null || _nearby == null)
                return;

            bool intimidating = player.Weapons.Current != null &&
                                player.Weapons.Current.Hash != WeaponHash.Unarmed &&
                                player.Weapons.Current.Hash != WeaponHash.Parachute;
            if (!intimidating)
                return;

            int applied = 0;
            foreach (Ped ped in _nearby)
            {
                if (applied >= 2)
                    break;
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead || !ped.IsHuman)
                        continue;
                    if (ped.Handle == player.Handle ||
                        ped.Position.DistanceTo(player.Position) > _config.CitizenWaryRadius)
                        continue;
                    if (ped.IsInPoliceVehicle || IsPoliceModel(ped.Model.Hash))
                        continue;
                    if (_profile != null && _profile.IsPlayerGangMember(ped.Model.Hash))
                        continue;
                    if (_profile != null && _profile.IsEnemyGangMember(ped.Model.Hash))
                        continue;

                    ped.Task.LookAt(player, 2000);
                    applied++;
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("GANG_CITIZEN_WARY_ERROR", ex);
                }
            }
        }

        private void UpdatePoliceResponse(DateTime now)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return;

            int wanted = GetWantedLevel();
            bool gangConflictActive = _enemyThreat != null || HasGangWarInNearbyUnits();
            bool recentPersonalAggression = IsRecentAggression();

            LspdResponseLog.Write(
                "GANG_POLICE_DECISION",
                "Input | Wanted=" + wanted +
                " | GangConflict=" + gangConflictActive +
                " | PersonalAggression=" + recentPersonalAggression +
                " | Turf=" + CurrentTurfName +
                " | Owner=" + CurrentTurfOwner);

            _policeState = _policeReact.Update(
                player,
                _nearby,
                _data,
                _profile,
                _currentTurf,
                IsCurrentPlayerOwnedTurf(),
                recentPersonalAggression,
                gangConflictActive,
                wanted,
                _config,
                now);

            // A Gang Leader is not supposed to be dragged into vanilla arrest
            // behavior merely because a wanted star exists. The role's police
            // response is controlled by evidence/aggression, not by default
            // GTA arrest behavior.
            if (_config.PreventGangLeaderArrest && !recentPersonalAggression)
                PreventNearbyGangLeaderArrest(player, now);

            // IMPORTANT: police presence alone never spawns Gang support.
            // Support is owned by LspdGangMemberTerritoryCore and only exists
            // for a verified Gang/NPC conflict.
            if ((gangConflictActive || wanted > 0) && !recentPersonalAggression)
                TryEndGangPursuit(now, player);

            if (wanted >= 5 && _config.EnableMilitaryResponse)
            {
                // Military response intentionally remains disabled in the
                // current stability build. It is a separate escalation layer.
                LspdResponseLog.Write(
                    "GANG_ESCALATION",
                    "Military eligibility reached, but military spawning is disabled in the current stability build.");
            }
        }

        private int CountPlayerGangMembersNearby()
        {
            if (_nearby == null || _profile == null)
                return 0;

            int count = 0;
            Ped player = Game.Player.Character;
            foreach (Ped ped in _nearby)
            {
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead ||
                        player == null || ped.Handle == player.Handle)
                        continue;
                    if (LspdGangIdentityContext.IsPlayerGangMemberModel(ped.Model.Hash) ||
                        (_profile != null && _profile.IsPlayerGangMember(ped.Model.Hash)))
                        count++;
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("GANG_MEMBER_COUNT_ERROR", ex);
                }
            }
            return count;
        }

        private int GetPlayerGangMemberHashCount()
        {
            return LspdGangIdentityContext.MemberHashCount;
        }

        private IEnumerable<int> GetPreferredPlayerGangMemberHashes()
        {
            if (_data == null || _data.Gangs == null)
                yield break;

            string preferred = _config == null ? "Anyiii's Gang" : _config.PreferredPlayerGangName;
            AnyiLSPDPoliceData.GangProfile selected = null;

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                foreach (AnyiLSPDPoliceData.GangProfile gang in _data.Gangs)
                {
                    if (gang != null && gang.PlayerOwned &&
                        string.Equals(gang.Name, preferred, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = gang;
                        break;
                    }
                }
            }

            if (selected == null)
            {
                int bestCount = -1;
                foreach (AnyiLSPDPoliceData.GangProfile gang in _data.Gangs)
                {
                    if (gang == null || !gang.PlayerOwned) continue;
                    int count = gang.MemberHashes == null ? 0 : gang.MemberHashes.Count;
                    if (count > bestCount)
                    {
                        bestCount = count;
                        selected = gang;
                    }
                }
            }

            if (selected == null || selected.MemberHashes == null)
                yield break;

            foreach (int hash in selected.MemberHashes)
                yield return hash;
        }

        private bool IsCurrentPlayerOwnedTurf()
        {
            return _currentTurf != null &&
                   LspdGangIdentityContext.IsPlayerGangName(_currentTurf.OwnerGang);
        }

        private bool HasGangWarInNearbyUnits()
        {
            if (_nearby == null || _profile == null)
                return false;

            List<Ped> gangMembers = new List<Ped>();
            Ped player = Game.Player.Character;
            foreach (Ped ped in _nearby)
            {
                try
                {
                    if (ped != null && ped.Exists() && !ped.IsDead &&
                        player != null && ped.Handle != player.Handle &&
                        LspdGangIdentityContext.IsPlayerGangMemberModel(ped.Model.Hash))
                    {
                        gangMembers.Add(ped);
                    }
                }
                catch { }
            }

            if (gangMembers.Count == 0)
                return false;

            foreach (Ped enemy in _nearby)
            {
                try
                {
                    if (enemy == null || !enemy.Exists() || enemy.IsDead ||
                        enemy.Handle == (player == null ? 0 : player.Handle))
                        continue;

                    AnyiLSPDPoliceData.GangProfile enemyGang =
                        _profile.FindGangForModel(enemy.Model.Hash);
                    if (enemyGang == null ||
                        _profile.IsPlayerOwnedGangName(enemyGang.Name))
                        continue;

                    foreach (Ped member in gangMembers)
                    {
                        if (member.Position.DistanceTo(enemy.Position) > _config.GangProtectionRadius + 35)
                            continue;
                        if (enemy.IsInCombatAgainst(member) || member.IsInCombatAgainst(enemy))
                            return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private void TryEndGangPursuit(DateTime now, Ped player)
        {
            if (_lastGangConflictAt == DateTime.MinValue ||
                player == null || !player.Exists())
                return;

            if ((now - _lastGangConflictAt).TotalSeconds < _config.PursuitBreakDelaySeconds)
                return;

            float distance = player.Position.DistanceTo(_lastGangConflictPosition);
            if (distance < _config.PursuitBreakDistance)
                return;

            if (Game.Player.Wanted.WantedLevel <= 0)
                return;

            try
            {
                int current = Game.Player.Wanted.WantedLevel;
                int next = Math.Max(0, current - 1);
                Game.Player.Wanted.SetWantedLevel(next, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);

                LspdResponseLog.Write(
                    "GANG_PURSUIT_DEESCALATION",
                    "Leader escaped police focus | Distance=" +
                    distance.ToString("0.0") +
                    " | Stars=" + current + "->" + next +
                    " | SearchRadius=" + _config.GangPursuitSearchRadius);

                Notification.PostTicker(
                    "~b~LSPD Response~s~\nPolice lost track of the Gang Leader.\n~c~Wanted level reduced.",
                    false,
                    false);

                _lastGangConflictAt = now;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("GANG_PURSUIT_DEESCALATION_ERROR", ex);
            }
        }

        private void WriteHeartbeat(DateTime now)
        {
            _heartbeatCounter++;
            string support = "0";
            try
            {
                support = _memberCore.GetConflictSupportCount().ToString();
            }
            catch { }

            LspdResponseLog.WriteHeartbeat(
                "Heartbeat=" + _heartbeatCounter +
                " | Role=" + (_uiConfig == null ? "none" : _uiConfig.ActiveRole.ToString()) +
                " | State=" + _state +
                " | Wanted=" + GetWantedLevel() +
                " | Turf=" + CurrentTurfName +
                " | Owner=" + CurrentTurfOwner +
                " | NearbyPeds=" + (_nearby == null ? 0 : _nearby.Length) +
                " | GangMembers=" + CountPlayerGangMembersNearby() +
                " | SupportSpawned=" + support +
                " | Enemy=" + DescribePed(_enemyThreat) +
                " | Police=" + PoliceStatusLine +
                " | PersonalAggression=" + IsRecentAggression() +
                " | LastConflictAge=" + DescribeConflictAge() +
                " | ConfigRadius=" + _config.GangPursuitSearchRadius +
                " | Utc=" + now.ToString("O"));
        }

        public bool IsPlayerGangMemberModel(int modelHash)
        {
            return _profile != null && _profile.IsPlayerGangMember(modelHash);
        }

        private int CountNearbyPolice()
        {
            int count = 0;
            if (_nearby == null)
                return count;

            Ped player = Game.Player.Character;
            foreach (Ped ped in _nearby)
            {
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead ||
                        player == null || ped.Handle == player.Handle)
                        continue;
                    if (!IsPoliceModel(ped.Model.Hash) && !ped.IsInPoliceVehicle)
                        continue;
                    if (ped.Position.DistanceTo(player.Position) <= _config.PoliceWaryRadius)
                        count++;
                }
                catch { }
            }
            return count;
        }

        private void PreventNearbyGangLeaderArrest(Ped player, DateTime now)
        {
            if (_nearby == null || player == null || !player.Exists())
                return;

            int affected = 0;
            foreach (Ped officer in _nearby)
            {
                try
                {
                    if (officer == null || !officer.Exists() || officer.IsDead)
                        continue;
                    if (!IsPoliceModel(officer.Model.Hash) && !officer.IsInPoliceVehicle)
                        continue;
                    if (officer.Position.DistanceTo(player.Position) > 18f)
                        continue;

                    if (officer.IsInCombatAgainst(player))
                        continue;

                    officer.Task.ClearAll();
                    affected++;
                    if (affected >= _config.PoliceTasksPerScan)
                        break;
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("GANG_ARREST_BLOCK_ERROR", ex);
                }
            }

            if (affected > 0)
            {
                LspdResponseLog.Write(
                    "GANG_ARREST_BLOCK",
                    "Gang Leader arrest/de-escalation protection applied | OfficersCleared=" +
                    affected + " | Wanted=" + GetWantedLevel() + " | Utc=" + now.ToString("O"));
            }
        }

        private string DescribeConflictAge()
        {
            if (_lastGangConflictAt == DateTime.MinValue)
                return "none";
            return (DateTime.UtcNow - _lastGangConflictAt).TotalSeconds.ToString("0.0") + " s";
        }

        private void LogStateTransition()
        {
            LspdGangState next;
            int wanted = GetWantedLevel();
            bool gangConflictActive = _enemyThreat != null || HasGangWarInNearbyUnits();
            if (gangConflictActive)
                next = LspdGangState.ActiveConflict;
            else if (wanted >= 5 || IsRecentAggression())
                next = LspdGangState.HighConflict;
            else if (wanted > 0)
                next = LspdGangState.PoliceInvestigation;
            else if (IsCurrentPlayerOwnedTurf())
                next = LspdGangState.TerritoryWatch;
            else
                next = LspdGangState.CalmLeader;

            if (next == _lastLoggedState)
                return;

            _lastLoggedState = next;
            _state = next;
            _stateChanges++;

            LspdResponseLog.Write(
                "GANG_STATE",
                DisplayState(_state) +
                " | Stars=" + wanted +
                " | Turf=" + CurrentTurfName +
                " | Owner=" + CurrentTurfOwner +
                " | Enemy=" + DescribePed(_enemyThreat));

            if (_state == LspdGangState.PoliceInvestigation)
            {
                Notification.PostTicker(
                    "~o~LSPD Response~s~\nPolice are observing the Gang Leader.",
                    false,
                    false);
            }
            else if (_state == LspdGangState.ActiveConflict)
            {
                Notification.PostTicker(
                    "~r~Anyi Gang~s~\nGang conflict detected. Your members are responding.",
                    false,
                    false);
            }
            else if (_state == LspdGangState.TerritoryWatch)
            {
                Notification.PostTicker(
                    "~b~Anyi Gang~s~\nTerritory watch: " + CurrentTurfName,
                    false,
                    false);
            }
        }

        private int GetWantedLevel()
        {
            try
            {
                return Game.Player.Wanted.WantedLevel;
            }
            catch
            {
                return 0;
            }
        }

        private static string DisplayState(LspdGangState state)
        {
            switch (state)
            {
                case LspdGangState.CalmLeader: return "Calm Gang Leader";
                case LspdGangState.TerritoryWatch: return "Territory Watch";
                case LspdGangState.PoliceInvestigation: return "Police Investigation";
                case LspdGangState.ActiveConflict: return "Active Gang Conflict";
                case LspdGangState.HighConflict: return "High Conflict";
                default: return "Inactive";
            }
        }

        private static string DescribePed(Ped ped)
        {
            return ped != null && ped.Exists()
                ? "Ped=" + ped.Handle + " Model=" + ped.Model.Hash
                : "none";
        }

        private static bool IsPoliceModel(int modelHash)
        {
            int[] hashes =
            {
                unchecked((int)StringHash.AtStringHash("s_m_y_cop_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_f_y_cop_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_m_y_sheriff_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_f_y_sheriff_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_m_y_hwaycop_01", 0))
            };
            foreach (int hash in hashes)
            {
                if (modelHash == hash)
                    return true;
            }
            return false;
        }

        private static void LogUi(string text)
        {
            LspdResponseLog.Write("UI_CONFIG", text);
        }

        private static void LogData(string text)
        {
            LspdResponseLog.Write("GANG_DATA", text);
        }

        public void Shutdown()
        {
            if (_shutdown)
                return;

            _shutdown = true;
            try
            {
                ResetRuntime();
                LspdResponseLog.Write("GANG_STOP", "Gang Turf Leader layer stopped.");
            }
            finally
            {
                if (ReferenceEquals(Instance, this))
                    Instance = null;
            }
        }
    }
}

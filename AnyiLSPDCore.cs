using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.IO;

//stable build for 8/31/26
namespace AnyiLSPD
{
    public sealed class AnyiLSPDCore
    {
        public static AnyiLSPDCore Instance { get; private set; }

        private readonly string _scriptsDirectory;
        private AnyiLSPDPoliceConfig _config;
        private AnyiLSPDProfileCore _profiles;
        private AnyiLSPDPoliceStations _stations;
        private AnyiLSPDPoliceAuthority _authority;
        private AnyiLSPDChaosAudio _audio;
        private AnyiLSPDPoliceResponse _response;
        private AnyiLSPDDispatch _dispatch;
        private AnyiLSPDRandomEvent _randomEvents;
        private AnyiLSPDConvoy _convoy;
        private AnyiLSPDReactoPoliceAnyi _policeReaction;
        private AnyiLSPDPEDReactToPoliceAnyi _pedReaction;
        private AnyiLSPDAnyiiiGangEnemyResponse _gangEnemyResponse;
        private AnyiLSPDAnyiiiGangMemberResponse _gangMemberResponse;
        private AnyiLSPDVanillaGangAttackToLSPDAnyi _vanillaGangResponse;
        private AnyiLSPDPoliceData.GangSnapshot _gangData;
        private readonly AnyiLSPDPoliceIntegrationConfig _integrationConfig;
        private readonly AnyiLSPDPoliceGangIntegration _gangIntegration;
        private readonly AnyiLSPDChaosGangActivity _chaosGangActivity;
        private readonly AnyiLSPDPoliceHotkeys _hotkeys = new AnyiLSPDPoliceHotkeys();

        private DateTime _nextConfigReload = DateTime.MinValue;
        private DateTime _nextNearby = DateTime.MinValue;
        private DateTime _nextReaction = DateTime.MinValue;
        private DateTime _nextGangAttackCheck = DateTime.MinValue;
        private DateTime _nextHeartbeat = DateTime.MinValue;
        private Ped[] _nearby = new Ped[0];
        private bool _active;
        private Vehicle _playerPatrolVehicle;
        private int _emergencyVehicleHandle;
        private bool _emergencyLightsOn;
        private bool _emergencySirenOn;

        private Vector3 _recentSceneCenter = Vector3.Zero;
        private DateTime _recentSceneCooldownUntil = DateTime.MinValue;
        private const float RecentSceneRadius = 220f;
        private const int RecentSceneCooldownSeconds = 300;
        private DateTime _nextSubjectMaintenance = DateTime.MinValue;
        private const int SubjectMaintenanceSeconds = 3;

        public AnyiPoliceDutyState DutyState { get { return _authority == null ? AnyiPoliceDutyState.OffDuty : _authority.State.DutyState; } }
        public AnyiPoliceDispatchState DispatchState { get { return _dispatch == null ? AnyiPoliceDispatchState.None : _dispatch.State; } }
        public AnyiPoliceIncident CurrentDispatch { get { return _dispatch == null ? null : _dispatch.Current; } }
        public bool IsActive { get { return _active; } }
        public bool IsPrisonerHoldingAtStation { get { return _convoy != null && _convoy.HoldingAtStation; } }
        public AnyiLSPDProfileCore ProfileCore { get { return _profiles; } }
        public AnyiLSPDPoliceConfig Config { get { return _config; } }
        public AnyiLSPDPoliceStations StationCore { get { return _stations; } }
        public string DiagnosticLogPath
        {
            get { return AnyiLSPDPoliceDiagnostics.GetDiagnosticPath(_scriptsDirectory); }
        }

        public string StatusLine
        {
            get
            {
                if (!_active) return "Police Authority: Off Duty";
                string profile = _profiles == null || _profiles.Current == null ? "none" : _profiles.Current.Department;
                string station = _profiles == null || _profiles.Current == null ? "none" : _profiles.Current.StationId;
                string dispatch = _dispatch == null ? "None" : _dispatch.State.ToString();
                string convoy = _convoy == null ? "None" : _convoy.State.ToString();
                return "Police: " + profile + " | Station=" + station + " | Duty=" + DutyState + " | Dispatch=" + dispatch + " | Convoy=" + convoy;
            }
        }

        public AnyiLSPDCore(string scriptsDirectory)
        {
            Instance = this;
            _scriptsDirectory = scriptsDirectory;
            _config = AnyiLSPDPoliceConfig.LoadOrCreate(_scriptsDirectory);
            _profiles = new AnyiLSPDProfileCore(_scriptsDirectory, _config);
            _stations = new AnyiLSPDPoliceStations(_scriptsDirectory);
            _authority = new AnyiLSPDPoliceAuthority();
            _audio = new AnyiLSPDChaosAudio(_config);
            _response = new AnyiLSPDPoliceResponse(_profiles, _config, _stations);
            _dispatch = new AnyiLSPDDispatch(_config, _audio, _response);
            _randomEvents = new AnyiLSPDRandomEvent(_config);
            _convoy = new AnyiLSPDConvoy(_config, _profiles, _stations, _audio);
            _policeReaction = new AnyiLSPDReactoPoliceAnyi();
            _pedReaction = new AnyiLSPDPEDReactToPoliceAnyi();
            _gangEnemyResponse = new AnyiLSPDAnyiiiGangEnemyResponse();
            _gangMemberResponse = new AnyiLSPDAnyiiiGangMemberResponse();
            _vanillaGangResponse = new AnyiLSPDVanillaGangAttackToLSPDAnyi();
            _integrationConfig = AnyiLSPDPoliceIntegrationConfig.LoadOrCreate(_scriptsDirectory);
            _gangIntegration = new AnyiLSPDPoliceGangIntegration(_integrationConfig);
            _chaosGangActivity = new AnyiLSPDChaosGangActivity(_integrationConfig, _config);
            LspdResponseLog.Write("POLICE_BOOT", "Police Authority v5 coordinator created. External Citizen/Gang cores remain separate.");

        }

        public void UpdateRole(LspdResponseRole role)
        {
            bool shouldBeActive = role == LspdResponseRole.PoliceAuthority;
            if (shouldBeActive == _active)
                return;
            if (shouldBeActive)
                EnterAuthority();
            else
                ExitAuthority();
        }

        public void ProcessShortcutKeys(bool menuClosed)
        {
            _hotkeys.Process(this, _config, menuClosed);
        }

        private void EnterAuthority()
        {
            // Police Authority always starts from a clean Police-owned runtime state.
            _dispatch.Reset();
            _convoy.Reset();
            _response.ClearOwnedUnits();
            DestroyPlayerPatrolVehicle();

            _active = true;
            _config = AnyiLSPDPoliceConfig.LoadOrCreate(_scriptsDirectory);
            _profiles.Reload(_config);
            _authority.Enter(_config);
            if (_config.EnableStationBlips)
                _stations.EnsureBlips();
            else
                _stations.ClearBlips();
            _randomEvents.Reload();
            _gangData = AnyiLSPDPoliceData.LoadGangSnapshot(_config.GangDataRoot, LogGangData);
            _gangIntegration.Reset();
            _chaosGangActivity.Reload();
            _nextNearby = DateTime.MinValue;
            _nextReaction = DateTime.MinValue;
            _nextGangAttackCheck = DateTime.MinValue;
            _nextSubjectMaintenance = DateTime.MinValue;
            _recentSceneCenter = Vector3.Zero;
            _recentSceneCooldownUntil = DateTime.MinValue;
            _nextHeartbeat = DateTime.MinValue;
            _hotkeys.Reset();

            Notification.PostTicker(
                "~b~ANYI LSPD~s~\nPOLICE AUTHORITY ON DUTY\n~c~" +
                _profiles.Current.Department + " | " +
                _profiles.Current.OfficerModel + " | " +
                _profiles.Current.VehicleModel + " | Station=" + _profiles.Current.StationId,
                false,
                false);
            LspdResponseLog.Write("POLICE_MODE", "ENTER | Profile=" + _profiles.Current.Id + " | Station=" + _profiles.Current.StationId + " | GangDataLoaded=" + (_gangData != null && _gangData.Gangs.Count > 0));

            if (_config.AutoSpawnPlayerPatrol)
                Patrol();
        }

        private void ExitAuthority()
        {
            _dispatch.Cancel("Police Authority mode changed.");
            _convoy.Cancel("Police Authority mode changed.");
            _response.ClearOwnedUnits();
            DestroyPlayerPatrolVehicle();
            _stations.ClearBlips();
            _authority.Exit();
            _policeReaction.Reset();
            _pedReaction.Reset();
            _gangIntegration.Reset();
            _chaosGangActivity.Reset();
            _hotkeys.Reset();
            _active = false;
            _nearby = new Ped[0];
            _gangData = null;
            LspdResponseLog.Write("POLICE_MODE", "EXIT | Police-owned runtime state cleaned. Normal/Gang layers retain ownership of their own state.");
            Notification.PostTicker("~b~ANYI LSPD~s~\nPOLICE AUTHORITY OFF DUTY\n~c~Vanilla/Citizen/Gang ownership restored.", false, false);
        }

        public void Update()
        {
            if (!_active) return;
            DateTime now = DateTime.UtcNow;
            try
            {
                if (now >= _nextConfigReload)
                {
                    _config = AnyiLSPDPoliceConfig.LoadOrCreate(_scriptsDirectory);
                    _nextConfigReload = now.AddSeconds(10);
                }

                _authority.Update(_config, now);

                // This native means law peds are allowed to attack a non-wanted player.
                // We intentionally do NOT call it during Police Authority.
                // The opposite policy is maintained by SET_POLICE_IGNORE_PLAYER plus
                // dispatch suppression, keeping the effect local to this role.

                if (now >= _nextNearby)
                {
                    Ped player = Game.Player.Character;
                    if (player != null && player.Exists())
                    {
                        _nearby = World.GetNearbyPeds(player, Math.Max(65f, _config.EventOfferRadius));
                        if (_gangData == null)
                            _gangData = AnyiLSPDPoliceData.LoadGangSnapshot(_config.GangDataRoot, LogGangData);
                    }
                    _nextNearby = now.AddMilliseconds(_config.NearbyPedScanMs);
                }

                Ped anyi = Game.Player.Character;
                if (now >= _nextReaction)
                {
                    _policeReaction.Update(anyi, _nearby, _config);
                    bool policeSceneActiveForNpc = _dispatch.HasIncident || _gangIntegration.HasActiveIncident || _chaosGangActivity.HasActiveActivity;
                    _pedReaction.Update(anyi, _nearby, _config, _gangData, policeSceneActiveForNpc);
                    _nextReaction = now.AddMilliseconds(_config.ReactionScanMs);
                }

                // Gang incidents need a fast maintenance cadence while active so
                // resolution audio/state is not delayed by the offer cooldown.
                // A completely new gang incident is still throttled to the
                // configured offer interval.
                bool wasChaosActive = _chaosGangActivity.HasActiveActivity;
                Vector3 previousChaosCenter = wasChaosActive ? _chaosGangActivity.ActiveCenter : Vector3.Zero;
                bool wasDispatchActive = _dispatch.HasIncident;
                bool wasGangActive = _gangIntegration.HasActiveIncident;

                bool canRunGangLayer = !_dispatch.HasIncident;
                bool gangIncidentActive = _gangIntegration.HasActiveIncident;
                bool chaosActivityActive = _chaosGangActivity.HasActiveActivity;
                bool sceneAreaBlocked = IsRecentSceneAreaBlocked(anyi);

                if (canRunGangLayer &&
                    (gangIncidentActive || !chaosActivityActive) &&
                    !sceneAreaBlocked &&
                    now >= _nextGangAttackCheck)
                {
                    _gangIntegration.Update(anyi, _nearby, _gangData, _audio);
                    _nextGangAttackCheck = now.AddMilliseconds(
                        _gangIntegration.HasActiveIncident
                            ? 750
                            : Math.Max(5000, _config.GangAttackOfferCooldownSeconds * 1000));
                }

                if (!_dispatch.HasIncident &&
                    !_gangIntegration.HasActiveIncident &&
                    !sceneAreaBlocked)
                {
                    _chaosGangActivity.Update(anyi, _nearby, _gangData, _audio);
                }

                if (wasChaosActive && !_chaosGangActivity.HasActiveActivity)
                    MarkRecentSceneCompleted(previousChaosCenter, "ChaosGangActivity");

                sceneAreaBlocked = IsRecentSceneAreaBlocked(anyi);
                if (!_dispatch.HasIncident &&
                    !_gangIntegration.HasActiveIncident &&
                    !_chaosGangActivity.HasActiveActivity &&
                    !sceneAreaBlocked)
                {
                    AnyiPoliceIncident discovered = _randomEvents.TryDiscover(anyi, _nearby, _gangData);
                    if (discovered != null)
                        _dispatch.Offer(discovered);
                }

                Vector3? responseDestination =
                    _dispatch.Current == null || _dispatch.IsInTransportLifecycle
                        ? (Vector3?)null
                        : _dispatch.Current.Origin;
                _response.Update(responseDestination);

                // Dispatch subjects are created before the state machine runs.
                // This removes the race where Anyi reaches the yellow scene marker,
                // presses Investigate, and the dispatch still has no suspect entity.
                if (_dispatch.Current != null &&
                    (_dispatch.Current.Suspect == null || !_dispatch.Current.Suspect.Exists()) &&
                    now >= _nextSubjectMaintenance)
                {
                    _nextSubjectMaintenance = now.AddSeconds(SubjectMaintenanceSeconds);
                    EnsureSceneSubject(_dispatch.Current, anyi);
                }

                string dispatchStatus = _dispatch.Update(now, anyi);
                if (!string.IsNullOrWhiteSpace(dispatchStatus))
                    LspdResponseLog.Write("POLICE_DISPATCH_TICK", dispatchStatus);

                // A second guard keeps the subject alive if an external task or
                // streaming event removes the entity while the call is active.
                if (_dispatch.Current != null &&
                    (_dispatch.Current.Suspect == null || !_dispatch.Current.Suspect.Exists()) &&
                    now >= _nextSubjectMaintenance)
                {
                    _nextSubjectMaintenance = now.AddSeconds(SubjectMaintenanceSeconds);
                    EnsureSceneSubject(_dispatch.Current, anyi);
                }

                if (_convoy.Active)
                {
                    string convoyStatus = _convoy.Update(now);
                    if (!string.IsNullOrWhiteSpace(convoyStatus))
                        LspdResponseLog.Write("POLICE_CONVOY_TICK", convoyStatus);
                }

                if (_convoy.State == AnyiPoliceDispatchState.PickupEnRoute ||
                    _convoy.State == AnyiPoliceDispatchState.Escorting ||
                    _convoy.State == AnyiPoliceDispatchState.HoldingAtStation ||
                    _convoy.State == AnyiPoliceDispatchState.PrisonTransfer)
                {
                    _dispatch.SetConvoyState(_convoy.State);
                }
                else if (_convoy.State == AnyiPoliceDispatchState.Completed &&
                         _dispatch.HasIncident)
                {
                    // Prison booking is now a terminal convoy state. Keep the linked
                    // dispatch alive until Anyi explicitly confirms completion with
                    // the Transport Completed action / T hotkey. This prevents the
                    // old "booking completed but dispatch still active" ambiguity and
                    // makes the terminal handoff player-driven.
                    LspdResponseLog.Write(
                        "POLICE_CONVOY_TICK",
                        "TRANSPORT_BOOKING_READY | Dispatch retained for explicit terminal confirmation.");
                }
                else if (_convoy.State == AnyiPoliceDispatchState.Compromised &&
                         _dispatch.HasIncident &&
                         _dispatch.IsInTransportLifecycle)
                {
                    _dispatch.Fail("Prisoner transport was compromised; custody operation was safely cleaned.");
                    _convoy.ClearTerminalState();
                }

                if (wasGangActive && !_gangIntegration.HasActiveIncident)
                    MarkRecentSceneCompleted(anyi == null ? Vector3.Zero : anyi.Position, "GangIntegration");

                if (wasDispatchActive && !_dispatch.HasIncident)
                    MarkRecentSceneCompleted(anyi == null ? Vector3.Zero : anyi.Position, "Dispatch");

                if (_convoy.State == AnyiPoliceDispatchState.Completed && !_dispatch.HasIncident)
                    _convoy.ClearTerminalState();

                if (now >= _nextHeartbeat)
                {
                    _nextHeartbeat = now.AddSeconds(_config.ReportHeartbeatSeconds);
                    LspdResponseLog.WriteHeartbeat("PoliceAuthority=" + _active + " | Profile=" + (_profiles.Current == null ? "none" : _profiles.Current.Id) + " | Station=" + (_profiles.Current == null ? "none" : _profiles.Current.StationId) + " | Dispatch=" + DispatchState + " | Convoy=" + _convoy.State + " | GangIncident=" + _gangIntegration.HasActiveIncident + " | ChaosGangActivity=" + _chaosGangActivity.HasActiveActivity + " | ChaosActivities=" + _chaosGangActivity.ActivityCount + " | ResponseUnits=" + _response.ActiveUnitCount + " | PlayerPatrol=" + (_playerPatrolVehicle != null && _playerPatrolVehicle.Exists()));
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CORE_UPDATE_ERROR", ex);
            }
        }

        private bool IsRecentSceneAreaBlocked(Ped player)
        {
            if (player == null || !player.Exists() || _recentSceneCenter == Vector3.Zero)
                return false;

            if (DateTime.UtcNow >= _recentSceneCooldownUntil)
                return false;

            return player.Position.DistanceTo(_recentSceneCenter) <= RecentSceneRadius;
        }

        private void MarkRecentSceneCompleted(Vector3 center, string source)
        {
            if (center == Vector3.Zero)
                return;

            _recentSceneCenter = center;
            _recentSceneCooldownUntil = DateTime.UtcNow.AddSeconds(RecentSceneCooldownSeconds);
            _randomEvents.MarkSceneAreaCompleted(center);
            LspdResponseLog.Write(
                "POLICE_SCENE_AREA",
                "Area cooldown started | Source=" + source +
                " | Center=" + center +
                " | Seconds=" + RecentSceneCooldownSeconds +
                " | Radius=" + RecentSceneRadius);
        }

        private void TryGangAttackDispatch(Ped player, DateTime now)
        {
            if (player == null || !player.Exists()) return;
            Ped attacker;
            string gangName;
            if (_config.EnableGangAttackDispatch && _gangEnemyResponse.TryFindEnemyAttackingPlayer(player, _nearby, _gangData, out attacker, out gangName))
            {
                OfferGangAttack(attacker, gangName, "Enemy gang member attacked Police Authority Anyi.", 4f);
                return;
            }

            Ped member;
            Ped memberAttacker;
            string memberEnemyGang;
            if (_config.EnableGangAttackDispatch && _gangMemberResponse.TryFindMemberUnderAttack(player, _nearby, _gangData, out member, out memberAttacker, out memberEnemyGang))
            {
                OfferGangAttack(memberAttacker, memberEnemyGang, "Anyiii's Gang member is under attack while Officer Anyi is nearby.", 4f);
                return;
            }

            if (_config.EnableVanillaGangAttackDispatch && _vanillaGangResponse.TryFindVanillaGangAttack(player, _nearby, _gangData, out attacker))
            {
                OfferGangAttack(attacker, "Vanilla Gang", "Vanilla gang attacker engaged Police Authority Anyi.", 4f);
            }
        }

        private void OfferGangAttack(Ped attacker, string gangName, string title, float severity)
        {
            if (attacker == null || !attacker.Exists()) return;
            AnyiLSPDPoliceData.TurfZone turf = _gangData == null ? null : _gangData.GetNearestTurf(attacker.Position.X, attacker.Position.Y, attacker.Position.Z, 100f);
            AnyiPoliceIncident incident = new AnyiPoliceIncident
            {
                Type = AnyiPoliceIncidentType.GangAmbush,
                Title = title,
                Description = "Gang aggression was detected around the Police Authority patrol.",
                Origin = attacker.Position,
                Severity = severity,
                GangName = gangName ?? "none",
                TurfName = turf == null ? "none" : turf.Name,
                Suspect = attacker,
                OwnedByDispatch = false,
                GeneratedFromChaosActivity = false,
                AudioCategory = "CRIME_SHOTS_FIRED",
                State = AnyiPoliceDispatchState.Offered
            };
            _dispatch.Offer(incident);
        }

        private void EnsureSceneSubject(AnyiPoliceIncident incident, Ped player)
        {
            if (incident == null || incident.State == AnyiPoliceDispatchState.Offered || incident.State == AnyiPoliceDispatchState.Cancelled || incident.State == AnyiPoliceDispatchState.Completed)
                return;
            if (incident.Suspect != null && incident.Suspect.Exists()) return;

            if (incident.State != AnyiPoliceDispatchState.Accepted &&
                incident.State != AnyiPoliceDispatchState.EnRoute &&
                incident.State != AnyiPoliceDispatchState.OnScene &&
                incident.State != AnyiPoliceDispatchState.Investigating &&
                incident.State != AnyiPoliceDispatchState.SuspectFleeing &&
                incident.State != AnyiPoliceDispatchState.SuspectCompliant &&
                incident.State != AnyiPoliceDispatchState.SuspectResisting)
                return;

            string modelName = SelectDispatchSuspectModel(incident);
            if (incident.Type == AnyiPoliceIncidentType.RecklessDriver || incident.Type == AnyiPoliceIncidentType.VehiclePursuit || incident.Type == AnyiPoliceIncidentType.Hijacking)
            {
                SpawnDrivingSubject(incident, player);
                return;
            }

            Model model = CreateModel(modelName);
            if (!model.IsValid || !model.IsPed || !model.Request(1500) || !model.IsLoaded)
            {
                model = new Model("a_m_m_business_01");
                if (!model.Request(1500) || !model.IsLoaded)
                {
                    LspdResponseLog.Write("POLICE_DISPATCH_SUBJECT", "Subject model unavailable | " + modelName);
                    return;
                }
            }

            try
            {
                Vector3 spawn = incident.Origin + new Vector3(6.0f, 0.0f, 0.0f);
                Ped suspect = World.CreatePed(model, spawn);
                if (suspect == null || !suspect.Exists()) return;
                suspect.IsPersistent = true;
                suspect.BlockPermanentEvents = true;
                incident.Suspect = suspect;
                incident.OwnedByDispatch = true;
                ApplyDispatchSceneWeapon(suspect, incident);

                // The suspect is deliberately kept at the scene until the officer
                // actually arrives. Scene behavior is initialized by Dispatch when
                // Anyi reaches the scene, preventing the "statue at spawn" problem
                // without making a suspect disappear before the player arrives.
                suspect.Task.LookAt(player, 1800);

                LspdResponseLog.Write("POLICE_DISPATCH_SUBJECT", "Dispatch-owned suspect created | Ped=" + suspect.Handle + " | Model=" + suspect.Model.Hash + " | Type=" + incident.Type);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_DISPATCH_SUBJECT_ERROR", ex);
            }
            finally
            {
                model.MarkAsNoLongerNeeded();
            }
        }

        private void SpawnDrivingSubject(AnyiPoliceIncident incident, Ped player)
        {
            Model vehicleModel = new Model("buffalo");
            string pedModelName = SelectDispatchSuspectModel(incident);
            Model pedModel = CreateModel(pedModelName);
            if (!vehicleModel.Request(1500) || !vehicleModel.IsLoaded || !vehicleModel.IsVehicle)
                return;
            if (!pedModel.IsValid || !pedModel.IsPed || !pedModel.Request(1500) || !pedModel.IsLoaded)
            {
                vehicleModel.MarkAsNoLongerNeeded();
                pedModel = new Model("a_m_m_business_01");
                if (!pedModel.Request(1500) || !pedModel.IsLoaded)
                    return;
            }

            try
            {
                Vehicle vehicle = World.CreateVehicle(vehicleModel, incident.Origin + new Vector3(8f, 0f, 0f), player == null ? 0f : player.Heading);
                if (vehicle == null || !vehicle.Exists()) return;
                vehicle.IsPersistent = true;
                vehicle.PlaceOnGround();
                Ped driver = vehicle.CreatePedOnSeat(VehicleSeat.Driver, pedModel);
                if (driver == null || !driver.Exists())
                {
                    vehicle.Delete();
                    return;
                }
                driver.IsPersistent = true;
                driver.BlockPermanentEvents = true;
                incident.SuspectVehicle = vehicle;
                incident.Suspect = driver;
                incident.OwnedByDispatch = true;
                ApplyDispatchSceneWeapon(driver, incident);
                if (incident.State == AnyiPoliceDispatchState.EnRoute || incident.State == AnyiPoliceDispatchState.OnScene || incident.State == AnyiPoliceDispatchState.Investigating)
                    driver.Task.VehicleChase(player);
                LspdResponseLog.Write("POLICE_DISPATCH_SUBJECT", "Dispatch-owned vehicle subject created | Vehicle=" + vehicle.Handle + " | Driver=" + driver.Handle + " | Model=" + driver.Model.Hash + " | Type=" + incident.Type);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_DRIVING_SUBJECT_ERROR", ex);
            }
            finally
            {
                vehicleModel.MarkAsNoLongerNeeded();
                pedModel.MarkAsNoLongerNeeded();
            }
        }

        private int _dispatchSubjectSequence;

        private string SelectDispatchSuspectModel(AnyiPoliceIncident incident)
        {
            // Ordinary dispatches use a varied civilian/compatible gang pool.
            // GangData remains read-only and is preferred for gang-specific calls.
            if (incident != null &&
                (incident.Type == AnyiPoliceIncidentType.GangAmbush ||
                 incident.Type == AnyiPoliceIncidentType.SuspiciousGangActivity ||
                 incident.Type == AnyiPoliceIncidentType.ArmsDealing ||
                 incident.Type == AnyiPoliceIncidentType.WeaponSmuggling ||
                 incident.GeneratedFromChaosActivity))
            {
                string gangModel = FindGangSuspectModel();
                if (!string.IsNullOrWhiteSpace(gangModel))
                    return gangModel;
            }

            string[] candidates =
            {
                "a_m_m_business_01",
                "a_m_m_skidrow_01",
                "a_m_m_hillbilly_01",
                "a_m_m_salton_01",
                "a_m_y_hipster_01",
                "a_m_y_stbla_01",
                "a_m_y_downtown_01",
                "a_f_m_tourist_01",
                "a_f_y_hipster_01",
                "a_f_y_business_02"
            };

            string result = candidates[Math.Abs(_dispatchSubjectSequence++) % candidates.Length];
            return result;
        }

        private void ApplyDispatchSceneWeapon(Ped suspect, AnyiPoliceIncident incident)
        {
            if (suspect == null || !suspect.Exists() || suspect.IsDead || incident == null)
                return;

            try
            {
                if (incident.Severity < 4f &&
                    incident.Type != AnyiPoliceIncidentType.BankHeist &&
                    incident.Type != AnyiPoliceIncidentType.StoreRobbery)
                    return;

                int[] weapons =
                {
                    unchecked((int)0x1B06D571), // Pistol
                    unchecked((int)0x2BE6766B), // SMG
                    unchecked((int)0x83BF0278), // Carbine Rifle
                    unchecked((int)0xD205520E), // Heavy Pistol
                    unchecked((int)0x99B507EA)  // Knife
                };

                int weaponHash = weapons[Math.Abs((suspect.Handle * 31) + _dispatchSubjectSequence) % weapons.Length];
                Function.Call(
                    Hash.GIVE_WEAPON_TO_PED,
                    suspect,
                    weaponHash,
                    180,
                    false,
                    true);

                Function.Call(
                    Hash.SET_CURRENT_PED_WEAPON,
                    suspect,
                    weaponHash,
                    true);

                LspdResponseLog.Write(
                    "POLICE_DISPATCH_SUBJECT",
                    "Scene weapon assigned from AnyiLSPDPoliceSceneWeapons catalog | Ped=" +
                    suspect.Handle +
                    " | WeaponHash=0x" + weaponHash.ToString("X8") +
                    " | Incident=" + incident.Type);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_DISPATCH_SUBJECT_WEAPON_ERROR", ex);
            }
        }

        private string FindGangSuspectModel()
        {
            List<int> candidates = new List<int>();
            if (_gangData != null && _gangData.Gangs != null)
            {
                foreach (AnyiLSPDPoliceData.GangProfile gang in _gangData.Gangs)
                {
                    if (gang == null || gang.PlayerOwned || gang.MemberHashes == null) continue;
                    foreach (int hash in gang.MemberHashes)
                        if (hash != 0 && !candidates.Contains(hash)) candidates.Add(hash);
                }
            }

            if (candidates.Count > 0)
                return candidates[Math.Abs(_dispatchSubjectSequence++) % candidates.Count].ToString();

            string[] fallback =
            {
                "g_m_y_ballasout_01", "g_m_y_famca_01", "g_m_y_mexgoon_01",
                "g_m_y_vagos_01", "a_m_m_business_01", "a_m_y_hipster_01"
            };
            return fallback[Math.Abs(_dispatchSubjectSequence++) % fallback.Length];
        }

        public string Patrol()
        {
            if (!_active) return "Select Police Authority before starting patrol.";
            if (_profiles.Current == null) return "No Police Authority profile is selected.";

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return "Player character unavailable.";

            if (TryUseExistingPoliceVehicle(player, player.Position, player.Heading))
            {
                LspdResponseLog.Write("POLICE_PATROL", "Current-location patrol reused existing Police vehicle | Handle=" + _playerPatrolVehicle.Handle);
                return "Police patrol active at current location. Existing Police vehicle reused.";
            }

            Vector3 spawnPosition = FindSafePatrolSpawn(player.Position);
            return CreatePlayerPatrol(spawnPosition, player.Heading);
        }

        public string PatrolAtSelectedStation()
        {
            if (!_active) return "Select Police Authority before starting patrol.";
            if (_profiles.Current == null) return "No Police Authority profile is selected.";

            AnyiLSPDPoliceStations.Station station =
                _stations.Get(_profiles.Current.StationId) ??
                _stations.Get(_config.DefaultStation) ??
                _stations.Get("MissionRow");

            if (station == null)
                return "No selected Police Authority station is configured.";

            Vector3 safePosition = FindSafePatrolSpawn(station.Exterior);
            if (TryUseExistingPoliceVehicle(Game.Player.Character, safePosition, station.Heading))
            {
                LspdResponseLog.Write("POLICE_PATROL", "Selected-station patrol reused existing Police vehicle | Station=" + station.Id + " | Handle=" + _playerPatrolVehicle.Handle);
                return "Selected-station patrol ready. Existing Police vehicle reused at a safe station road position.";
            }

            return CreatePlayerPatrol(safePosition, station.Heading);
        }

        private bool TryUseExistingPoliceVehicle(Ped player, Vector3 targetPosition, float heading)
        {
            try
            {
                if (player == null || !player.Exists() || !player.IsInVehicle())
                    return false;

                Vehicle current = player.CurrentVehicle;
                if (current == null || !current.Exists() || current.IsDead)
                    return false;

                if (!IsPolicePatrolVehicle(current))
                    return false;

                _playerPatrolVehicle = current;
                _playerPatrolVehicle.IsPersistent = true;

                // Current-location patrol must not teleport Anyi or respawn the vehicle.
                if (current.Position.DistanceTo(targetPosition) > 4f)
                {
                    current.Position = targetPosition;
                    current.Heading = heading;
                    current.PlaceOnGround();
                    player.SetIntoVehicle(current, VehicleSeat.Driver);
                }

                _emergencyVehicleHandle = current.Handle;
                _emergencyLightsOn = false;
                _emergencySirenOn = false;
                ApplyEmergencyState(current);
                SaveConfig();
                return true;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_PATROL_REUSE_ERROR", ex);
                return false;
            }
        }

        private bool IsPolicePatrolVehicle(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists()) return false;
            string configured = _profiles.Current == null ? string.Empty : _profiles.Current.VehicleModel;
            string favorite = _config.FavoritePoliceVehicleModel;
            string[] names = { "police", "police2", "police3", "fbi", "riot", "sheriff", "polignus", configured, favorite };
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (vehicle.Model.Hash == unchecked((int)StringHash.AtStringHash(name, 0))) return true;
            }
            return false;
        }

        private Vector3 FindSafePatrolSpawn(Vector3 preferred)
        {
            try
            {
                Vector3 street = World.GetNextPositionOnStreet(preferred);
                if (street.DistanceTo(Vector3.Zero) > 1f)
                    return street;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_PATROL_SAFE_POSITION_ERROR", ex);
            }
            return preferred + new Vector3(3.0f, 3.0f, 0.0f);
        }

        private string CreatePlayerPatrol(Vector3 spawnPosition, float heading)
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return "Player character unavailable.";

            // Only destroy an existing Police-owned patrol vehicle when this method
            // actually has to create a replacement. Patrol() itself reuses a valid current vehicle.
            DestroyPlayerPatrolVehicle();

            Model model = CreateModel(_profiles.Current.VehicleModel);
            if (!model.IsValid || !model.IsVehicle || !model.Request(1500) || !model.IsLoaded)
                return "Police vehicle model is unavailable: " + _profiles.Current.VehicleModel;

            try
            {
                _playerPatrolVehicle = World.CreateVehicle(model, spawnPosition, heading);
                if (_playerPatrolVehicle == null || !_playerPatrolVehicle.Exists())
                    return "Police patrol vehicle could not be created.";

                _playerPatrolVehicle.IsPersistent = true;
                _playerPatrolVehicle.PlaceOnGround();
                player.SetIntoVehicle(_playerPatrolVehicle, VehicleSeat.Driver);

                _emergencyVehicleHandle = _playerPatrolVehicle.Handle;
                _emergencyLightsOn = _profiles.Current.EmergencyLights &&
                    !string.Equals(_profiles.Current.VehicleModel, "polignus", StringComparison.OrdinalIgnoreCase);
                _emergencySirenOn = false;
                ApplyEmergencyState(_playerPatrolVehicle);

                LspdResponseLog.Write(
                    "POLICE_PATROL",
                    "Player patrol created | Vehicle=" + _profiles.Current.VehicleModel +
                    " | Spawn=" + spawnPosition +
                    " | Heading=" + heading +
                    " | Handle=" + _playerPatrolVehicle.Handle);

                SaveConfig();
                return "Police patrol ready: " + _profiles.Current.VehicleModel + ".";
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_PATROL_SPAWN_ERROR", ex);
                DestroyPlayerPatrolVehicle();
                return "Police patrol vehicle could not be created safely.";
            }
            finally
            {
                model.MarkAsNoLongerNeeded();
            }
        }

        public bool ChangeOfficerModel(string input)
        {
            if (!_active || _profiles.Current == null || string.IsNullOrWhiteSpace(input)) return false;
            Model model = CreateModel(input.Trim());
            if (!model.IsValid || !model.IsPed || !model.Request(1500) || !model.IsLoaded)
                return false;
            try
            {
                if (!_profiles.ApplyOfficerModel(input.Trim())) return false;
                SaveConfig();
                Game.Player.ChangeModel(model);
                LspdResponseLog.Write("POLICE_PLAYER_MODEL", "Officer model changed | Model=" + input.Trim() + " | Hash=" + model.Hash);
                return true;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_PLAYER_MODEL_ERROR", ex);
                return false;
            }
            finally
            {
                model.MarkAsNoLongerNeeded();
            }
        }

        public bool ChangeVehicleModel(string input)
        {
            if (!_active || _profiles.Current == null || string.IsNullOrWhiteSpace(input)) return false;
            Model model = CreateModel(input.Trim());
            if (!model.IsValid || !model.IsVehicle || !model.Request(1500) || !model.IsLoaded)
                return false;
            model.MarkAsNoLongerNeeded();
            if (!_profiles.ApplyVehicleModel(input.Trim())) return false;
            SaveConfig();
            string result = Patrol();
            LspdResponseLog.Write("POLICE_PLAYER_VEHICLE", result);
            return result.IndexOf("ready", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool SelectPoliceProfile(string profileId)
        {
            if (!_active)
                return false;
            if (_dispatch.HasIncident || _convoy.Active)
                return false;
            if (!_profiles.Select(profileId))
                return false;

            DestroyPlayerPatrolVehicle();
            _response.ClearOwnedUnits();
            _randomEvents.Reload();
            _randomEvents.ForceScan();
            SaveConfig();
            return true;
        }

        public bool SelectPoliceStation(string stationId)
        {
            if (!_active || _stations.Get(stationId) == null)
                return false;
            if (_dispatch.HasIncident || _convoy.Active)
                return false;

            bool selected = _profiles.SelectStation(stationId);
            if (selected)
            {
                AnyiLSPDPoliceConfig.SaveSelectedStation(
                    Path.Combine(_scriptsDirectory, AnyiLSPDPoliceConfig.FileName),
                    _profiles.Current.StationId);
            }
            return selected;
        }

        public string AcceptDispatch() { return _dispatch.Accept(); }
        public string RejectDispatch()
        {
            if (_convoy.State == AnyiPoliceDispatchState.HoldingAtStation)
                return DeclinePrisonTransfer();
            return _dispatch.Reject();
        }
        public string CancelDispatch() { return _dispatch.Cancel("Cancelled by Anyi from Police UI."); }

        public string ForceDispatchScan()
        {
            if (!_active) return "Police Authority is not active.";
            _randomEvents.ForceScan();
            AnyiPoliceIncident discovered = _randomEvents.TryDiscover(
                Game.Player.Character,
                _nearby,
                _gangData);

            if (discovered == null)
                discovered = _randomEvents.CreateImmediatePatrolIncident(
                    Game.Player.Character,
                    _gangData);

            return discovered == null
                ? "A patrol callout could not be created safely."
                : (_dispatch.Offer(discovered)
                    ? "A new patrol dispatch was offered."
                    : "A dispatch could not be offered right now.");
        }

        public string ForceChaosDispatch()
        {
            if (!_active) return "Police Authority is not active.";
            if (_dispatch.HasIncident) return "Finish or cancel the current dispatch first.";

            AnyiPoliceIncident chaos = _randomEvents.CreateImmediateChaosIncident(
                Game.Player.Character,
                _gangData);

            if (chaos == null)
                return "No Chaos Gang Activity is currently within the configured offer radius.";

            return _dispatch.Offer(chaos)
                ? "Chaos Activity dispatch offered."
                : "Chaos Activity dispatch could not be offered.";
        }

        public string InvestigateScene()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return "Player character unavailable.";

            if (_convoy.Active)
                return "A prisoner custody operation is active. Finish custody/transport before investigating another scene.";

            // One local Police interaction owner. Once an ordinary Dispatch exists,
            // it owns Investigate until it is completed/cancelled. Chaos Activity
            // remains allowed to exist, but it cannot steal I from the active dispatch.
            if (_dispatch.HasIncident)
            {
                AnyiPoliceIncident incident = _dispatch.Current;
                if (incident != null && incident.Suspect == null)
                    EnsureSceneSubject(incident, player);

                return _dispatch.TrySceneInteraction(player);
            }

            if (_gangIntegration.HasActiveIncident)
                return "A live gang incident is active. Secure the threat before starting another investigation.";

            if (_chaosGangActivity.HasActiveActivity)
                return _chaosGangActivity.Investigate(player, _nearby, _gangData, _audio);

            return "No dispatch or Chaos Gang Activity is currently waiting for investigation.";
        }
        public string SecureSuspect() { return _dispatch.SecureSuspect(Game.Player.Character); }

        public string RequestTransport()
        {
            if (_convoy.State == AnyiPoliceDispatchState.HoldingAtStation)
            {
                string approval = _convoy.ContinueToPrison();
                if (_convoy.State == AnyiPoliceDispatchState.PrisonTransfer)
                    _dispatch.SetConvoyState(AnyiPoliceDispatchState.PrisonTransfer);
                return approval;
            }

            if (_dispatch.Current == null || _dispatch.Current.Suspect == null)
                return "No arrested suspect is ready for transport.";
            if (_dispatch.State != AnyiPoliceDispatchState.Arrested)
                return "Secure a compliant suspect first.";

            Ped custodyPrisoner = _dispatch.Current.Suspect;
            string result = _convoy.Start(custodyPrisoner, custodyPrisoner.Position);
            if (_convoy.Active)
            {
                // Atomic ownership handoff: Dispatch stops owning the physical ped
                // before the custody lifecycle continues. Convoy owns it exclusively.
                _dispatch.TransferCustodyOwnershipToConvoy();
                _dispatch.SetAwaitingTransport();
                _dispatch.SetConvoyState(AnyiPoliceDispatchState.PickupEnRoute);
                _dispatch.ReleaseAssignedResponseUnit();
            }
            return result;
        }

        public string DeclinePrisonTransfer()
        {
            if (_convoy.State != AnyiPoliceDispatchState.HoldingAtStation)
                return "No prisoner is waiting at the station for a transfer decision.";

            string result = _convoy.DeclineAtStation();
            if (_dispatch.HasIncident)
            {
                _dispatch.CompleteSuccessful(
                    "Prison transfer declined. Custody closed and the justice job was completed at the station.",
                    _config.DispatchSuccessAudioCategories);
            }
            return result;
        }

        public string ContinuePrisonTransfer()
        {
            string result = _convoy.ContinueToPrison();
            if (_convoy.State == AnyiPoliceDispatchState.PrisonTransfer)
                _dispatch.SetConvoyState(AnyiPoliceDispatchState.PrisonTransfer);
            return result;
        }

        public string AgreePrisonTransfer()
        {
            return ContinuePrisonTransfer();
        }

        public string DisagreePrisonTransfer()
        {
            return DeclinePrisonTransfer();
        }

        public string CompleteTransportNow()
        {
            if (!_dispatch.HasIncident)
                return "No active Police dispatch remains.";

            // If Convoy already completed booking, T only has one job: close the
            // linked Dispatch and advance the Police Authority state machine.
            if (_convoy.State == AnyiPoliceDispatchState.Completed)
            {
                string completion = _dispatch.CompleteTransportSuccess();
                if (!_dispatch.HasIncident)
                    _convoy.ClearTerminalState();
                return completion;
            }

            // Also allow T to finalize immediately after the player reaches the
            // configured prison, before the next normal convoy tick runs. Convoy
            // itself remains authoritative about distance/vehicle requirements.
            if (_convoy.State == AnyiPoliceDispatchState.PrisonTransfer)
            {
                string convoyStatus = _convoy.Update(DateTime.UtcNow);
                if (_convoy.State == AnyiPoliceDispatchState.Completed)
                {
                    string completion = _dispatch.CompleteTransportSuccess();
                    if (!_dispatch.HasIncident)
                        _convoy.ClearTerminalState();
                    return completion;
                }

                return string.IsNullOrWhiteSpace(convoyStatus)
                    ? "Transport is not yet at the prison. Continue the prison transfer, then press T when booking is complete."
                    : convoyStatus;
            }

            return "Transport completion is available after the prison booking is complete.";
        }

        public string CompleteConvoyAndDispatchIfArrived()
        {
            return CompleteTransportNow();
        }

        public string CancelPrisonerTransport()
        {
            if (!_convoy.Active && _convoy.State != AnyiPoliceDispatchState.HoldingAtStation && _convoy.State != AnyiPoliceDispatchState.PrisonTransfer && _convoy.State != AnyiPoliceDispatchState.PickupEnRoute && _convoy.State != AnyiPoliceDispatchState.Escorting)
                return "No active prisoner transport.";

            _convoy.Cancel("Cancelled by Anyi from Police UI.");
            if (_dispatch.HasIncident)
                _dispatch.SetConvoyState(AnyiPoliceDispatchState.Arrested);

            Notification.PostTicker("~y~ANYI LSPD~s~\nPRISONER TRANSPORT CANCELLED\n~c~Suspect remains in custody and can be transported again.", false, false);
            LspdResponseLog.Write("POLICE_CONVOY", "Cancelled by Anyi; dispatch returned to Arrested custody state.");
            return "Prisoner transport cancelled. Custody remains active.";
        }

        public bool HasActiveNpcInteraction { get { return _pedReaction != null && _pedReaction.HasActiveInteraction; } }

        public string NPCInteract()
        {
            if (_dispatch.HasIncident || _convoy.Active || _gangIntegration.HasActiveIncident || _chaosGangActivity.HasActiveActivity)
                return "Finish the current Police scene before starting a civilian interaction.";
            return _pedReaction.InteractNearest(Game.Player.Character, _nearby, _config.InteractionRadius, _config, _gangData);
        }

        public string AcceptNpcInteraction()
        {
            return _pedReaction.AcceptInteraction(Game.Player.Character, _config);
        }

        public string RejectNpcInteraction()
        {
            Ped pursuitPed;
            Vehicle pursuitVehicle;

            return _pedReaction.RejectInteraction(
                Game.Player.Character,
                _config,
                out pursuitPed,
                out pursuitVehicle);
        }

        public string ToggleEmergency()
        {
            Vehicle vehicle = Game.Player.Character == null ? null : Game.Player.Character.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists())
                return "Anyi is not inside a vehicle.";

            if (IsPolIgnusVehicle(vehicle))
            {
                _emergencyVehicleHandle = vehicle.Handle;
                _emergencyLightsOn = false;
                _emergencySirenOn = false;
                ApplyEmergencyState(vehicle);
                return "PolIgnus emergency signals locked off. Native siren and native emergency lights are disabled for this custom asset.";
            }

            if (_emergencyVehicleHandle != vehicle.Handle)
            {
                _emergencyVehicleHandle = vehicle.Handle;
                _emergencyLightsOn = false;
                _emergencySirenOn = false;
            }

            if (!_emergencyLightsOn)
            {
                _emergencyLightsOn = true;
                _emergencySirenOn = false;
            }
            else if (_profiles.Current != null && _profiles.Current.NativeSiren)
            {
                if (!_emergencySirenOn)
                    _emergencySirenOn = true;
                else
                {
                    _emergencyLightsOn = false;
                    _emergencySirenOn = false;
                }
            }
            else
            {
                _emergencyLightsOn = false;
                _emergencySirenOn = false;
            }

            ApplyEmergencyState(vehicle);
            return "Emergency state: " + (_emergencyLightsOn ? (_emergencySirenOn ? "Lights + Siren" : "Lights") : "Off") +
                   (_profiles.Current != null && !_profiles.Current.NativeSiren ? " | Native siren disabled for this profile." : "");
        }

        public string QuickGpsNearestStation()
        {
            Ped player = Game.Player.Character;
            AnyiLSPDPoliceStations.Station station = player == null ? null : _stations.FindNearest(player.Position);
            if (station == null) return "No police station is configured.";
            return _stations.SetWaypoint(station.Id) ? "GPS set to " + station.Name + "." : "Could not set the police station waypoint.";
        }

        public string QuickGpsSelectedStation()
        {
            string station = _profiles.Current == null ? null : _profiles.Current.StationId;
            if (string.IsNullOrWhiteSpace(station)) return "No police station is selected.";
            return _stations.SetWaypoint(station) ? "GPS set to selected station." : "Selected station is not configured.";
        }

        public string QuickGpsPrison()
        {
            return _stations.SetWaypoint(_config.PrisonStation) ? "GPS set to " + _config.PrisonStation + "." : "Prison destination is not configured.";
        }

        public string ResetBugs()
        {
            if (!_active)
                return "Police Authority is not active.";

            string profileId = _profiles.Current == null ? _config.ActiveProfileId : _profiles.Current.Id;
            string stationId = _profiles.Current == null ? _config.SelectedStationId : _profiles.Current.StationId;

            _dispatch.Reset();
            _convoy.Reset();
            _response.ClearOwnedUnits();
            DestroyPlayerPatrolVehicle();
            _policeReaction.Reset();
            _pedReaction.Reset();
            _gangIntegration.Reset();
            _chaosGangActivity.Reset();
            _stations.ClearBlips();
            _authority.Reset();

            _config.ActiveProfileId = profileId;
            _config.SelectedStationId = stationId;
            SaveConfig();

            _authority.Enter(_config);
            if (_config.EnableStationBlips)
                _stations.EnsureBlips();
            _randomEvents.Reload();
            _randomEvents.ForceScan();
            _gangData = AnyiLSPDPoliceData.LoadGangSnapshot(_config.GangDataRoot, LogGangData);
            _nextNearby = DateTime.MinValue;
            _nextReaction = DateTime.MinValue;
            _nextGangAttackCheck = DateTime.MinValue;

            LspdResponseLog.Write(
                "POLICE_RESET",
                "Manual Police Authority reset completed while remaining ON DUTY | Profile=" +
                profileId + " | Station=" + stationId);

            return "Police Authority runtime reset. Police duty remains active; dispatch, convoy, patrol and temporary response state were cleared.";
        }

        public void WriteDiagnosticReport(string reason)
        {
            List<string> lines = new List<string>();
            lines.Add("Reason: " + reason);
            lines.Add("Duty state: " + DutyState);
            lines.Add("Police active: " + _active);
            lines.Add("Profile: " + (_profiles.Current == null ? "none" : _profiles.Current.Id));
            lines.Add("Department: " + (_profiles.Current == null ? "none" : _profiles.Current.Department));
            lines.Add("Officer model: " + (_profiles.Current == null ? "none" : _profiles.Current.OfficerModel));
            lines.Add("Vehicle model: " + (_profiles.Current == null ? "none" : _profiles.Current.VehicleModel));
            lines.Add("Station: " + (_profiles.Current == null ? "none" : _profiles.Current.StationId));
            lines.Add("Dispatch state: " + DispatchState);
            lines.Add("Dispatch title: " + (_dispatch.Current == null ? "none" : _dispatch.Current.Title));
            lines.Add("Dispatch gang: " + (_dispatch.Current == null ? "none" : _dispatch.Current.GangName));
            lines.Add("Dispatch turf: " + (_dispatch.Current == null ? "none" : _dispatch.Current.TurfName));
            lines.Add("Dispatch cooldown seconds: " + _config.DispatchCooldownSeconds);
            lines.Add("Random event cooldown seconds: " + _config.EventCooldownSeconds);
            lines.Add("Random event scan seconds: " + _config.RandomEventCheckSeconds);
            lines.Add("Gang attack offer cooldown seconds: " + _config.GangAttackOfferCooldownSeconds);
            lines.Add("Audio cooldown seconds: " + _config.AudioCooldownSeconds);
            lines.Add("Chaos audio volume: " + (_audio == null ? "unavailable" : _audio.AudioStatusLine));
            lines.Add("Prefer Chaos activities: " + _config.PreferChaosActivities);
            lines.Add("Complete dispatch on suspect death: " + _config.CompleteDispatchOnSuspectDeath);
            lines.Add("Accept key: " + _config.AcceptDispatchKey);
            lines.Add("Reject key: " + _config.RejectDispatchKey);
            lines.Add("Secure key: " + _config.SecureSuspectKey);
            lines.Add("Transport key: " + _config.RequestTransportKey);
            lines.Add("NPC interaction key: " + _config.NPCInteractionKey);
            lines.Add("Investigation key: " + _config.InvestigateSceneKey);
            lines.Add("Patrol key: " + _config.PatrolKey);
            lines.Add("Convoy state: " + _convoy.State);
            lines.Add("Convoy active: " + _convoy.Active);
            lines.Add("Police response units: " + _response.ActiveUnitCount);
            lines.Add("Player patrol active: " + (_playerPatrolVehicle != null && _playerPatrolVehicle.Exists()));
            lines.Add("Gang data gangs: " + (_gangData == null ? 0 : _gangData.Gangs.Count));
            lines.Add("Gang data member hashes: " + (_gangData == null ? 0 : _gangData.MemberPoolHashes.Count));
            lines.Add("Gang data turf zones: " + (_gangData == null ? 0 : _gangData.TurfZones.Count));
            lines.Add("Chaos activity root: " + _config.ChaosActivityRoot);
            lines.Add("Chaos activity count: " + _randomEvents.ChaosActivityCount);
            lines.Add("Chaos event template count: " + _randomEvents.EventTemplateCount);
            lines.Add("Chaos audio root: " + _config.ChaosAudioRoot);
            lines.Add("NAudio present: " + System.IO.File.Exists(System.IO.Path.Combine(_scriptsDirectory, "NAudio.dll")));
            lines.Add("Evidence subsystem: removed from Police Authority.");
            lines.Add("Military/tank/helicopter spawning: disabled in Police Authority stability baseline.");
            AnyiLSPDPoliceDiagnostics.WriteReport(
                _scriptsDirectory,
                "ANYI LSPD POLICE AUTHORITY DIAGNOSTIC",
                lines);
            try
            {
                LspdResponseLog.WriteReport("ANYI LSPD POLICE AUTHORITY DIAGNOSTIC", lines);
            }
            catch
            {
            }
        }

        public string TestChaosDispatchAudio()
        {
            if (!_active)
                return "Police Authority is not active.";
            return _audio.TestDispatchAudio();
        }

        public string ChaosAudioStatus()
        {
            return _audio == null ? "Chaos Response audio service unavailable." : _audio.AudioStatusLine;
        }

        public string IncreaseChaosMasterVolume()
        {
            return _audio == null ? "Chaos Response audio service unavailable." : _audio.IncreaseMasterVolume(5);
        }

        public string DecreaseChaosMasterVolume()
        {
            return _audio == null ? "Chaos Response audio service unavailable." : _audio.DecreaseMasterVolume(5);
        }

        public string IncreaseChaosDispatchVolume()
        {
            return _audio == null ? "Chaos Response audio service unavailable." : _audio.IncreaseDispatchVolume(5);
        }

        public string DecreaseChaosDispatchVolume()
        {
            return _audio == null ? "Chaos Response audio service unavailable." : _audio.DecreaseDispatchVolume(5);
        }

        public string ToggleChaosAudioMute()
        {
            return _audio == null ? "Chaos Response audio service unavailable." : _audio.ToggleMute();
        }

        public string ResetChaosAudioSettings()
        {
            return _audio == null ? "Chaos Response audio service unavailable." : _audio.ResetVolumeSettings();
        }

        public void Shutdown()
        {
            if (!_active)
            {
                if (ReferenceEquals(Instance, this)) Instance = null;
                return;
            }
            ExitAuthority();
            _audio.Dispose();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }

        private void ApplyEmergencyState(Vehicle vehicle)
        {
            if (vehicle == null || !vehicle.Exists()) return;
            if (IsPolIgnusVehicle(vehicle))
            {
                // PolIgnus is permanently siren-free. Do not invoke the native siren path.
                try { Function.Call(Hash.SET_VEHICLE_LIGHTS, vehicle, 0); } catch { }
                _emergencyLightsOn = false;
                _emergencySirenOn = false;
                return;
            }

            bool lightsAllowed = _emergencyLightsOn;
            bool sirenAllowed = _profiles.Current != null && _profiles.Current.NativeSiren;
            Function.Call(Hash.SET_VEHICLE_LIGHTS, vehicle, lightsAllowed ? 2 : 0);
            Function.Call(Hash.SET_VEHICLE_SIREN, vehicle, _emergencySirenOn && sirenAllowed);
        }

        private static bool IsPolIgnusVehicle(Vehicle vehicle)
        {
            try
            {
                return vehicle != null && vehicle.Exists() &&
                       vehicle.Model.Hash == unchecked((int)StringHash.AtStringHash("polignus", 0));
            }
            catch { return false; }
        }

        private void DestroyPlayerPatrolVehicle()
        {
            try
            {
                if (_playerPatrolVehicle != null && _playerPatrolVehicle.Exists())
                {
                    if (Game.Player.Character != null && Game.Player.Character.Exists() && Game.Player.Character.IsInVehicle(_playerPatrolVehicle))
                        Game.Player.Character.Task.LeaveVehicle(_playerPatrolVehicle, true);
                    if (!IsPolIgnusVehicle(_playerPatrolVehicle))
                        Function.Call(Hash.SET_VEHICLE_SIREN, _playerPatrolVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, _playerPatrolVehicle, 0);
                    _playerPatrolVehicle.IsPersistent = false;
                    _playerPatrolVehicle.Delete();
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_PATROL_CLEANUP_ERROR", ex);
            }
            _playerPatrolVehicle = null;
            _emergencyVehicleHandle = 0;
            _emergencyLightsOn = false;
            _emergencySirenOn = false;
        }

        private static Model CreateModel(string value)
        {
            int hash;
            return int.TryParse(value, out hash) ? new Model(hash) : new Model(value);
        }

        private void SaveConfig()
        {
            try
            {
                AnyiLSPDPoliceConfig.Save(
                    Path.Combine(_scriptsDirectory, AnyiLSPDPoliceConfig.FileName),
                    _config);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CONFIG_SAVE_ERROR", ex);
            }
        }

        private void LogGangData(string message)
        {
            LspdResponseLog.Write("POLICE_GANG_DATA", message);
        }
    }
}

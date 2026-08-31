using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AnyiLSPD
{
    /// <summary>
    /// Police Authority-only discovery layer for the existing ChaosResponse.GangActivity XML.
    /// External ChaosResponse data remains read-only. This class owns only the local scene it creates.
    /// </summary>
    public sealed class AnyiLSPDChaosGangActivity
    {
        private sealed class ActivityDefinition
        {
            public string Name;
            public Vector3 Center;
            public int PedObjects;
            public int VehicleObjects;
            public int PropObjects;
            public bool VehicleFocused;
            public bool Ambush;
            public bool FootPursuit;
            public readonly List<Vector3> PedPositions = new List<Vector3>();
            public readonly List<Vector3> VehiclePositions = new List<Vector3>();
        }

        private sealed class SceneWeapon
        {
            public int Hash;
            public int Ammo;
            public string Name;
        }

        private sealed class DeferredCleanup
        {
            public Ped Ped;
            public Vehicle Vehicle;
            public DateTime Earliest;
            public DateTime Expires;
            public Vector3 Anchor;
        }

        private readonly AnyiLSPDPoliceIntegrationConfig _config;
        private readonly AnyiLSPDPoliceConfig _policeConfig;
        private readonly List<ActivityDefinition> _activities = new List<ActivityDefinition>();
        private readonly Dictionary<string, DateTime> _cooldowns = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Ped> _ownedPeds = new List<Ped>();
        private readonly List<Vehicle> _ownedVehicles = new List<Vehicle>();
        private readonly List<Blip> _sceneEntityBlips = new List<Blip>();
        private readonly List<DeferredCleanup> _deferredCleanup = new List<DeferredCleanup>();
        private readonly Dictionary<string, SceneWeapon> _sceneWeapons = new Dictionary<string, SceneWeapon>(StringComparer.OrdinalIgnoreCase);

        private DateTime _nextScan = DateTime.MinValue;
        private DateTime _activeStarted = DateTime.MinValue;
        private DateTime _noThreatSince = DateTime.MinValue;
        private ActivityDefinition _active;
        private bool _onScene;
        private bool _investigationRequested;
        private bool _sawThreat;
        private bool _waypointOwned;

        public bool HasActiveActivity { get { return _active != null; } }
        public int ActivityCount { get { return _activities.Count; } }
        public Vector3 ActiveCenter { get { return _active == null ? Vector3.Zero : _active.Center; } }

        public AnyiLSPDChaosGangActivity(AnyiLSPDPoliceIntegrationConfig config, AnyiLSPDPoliceConfig policeConfig)
        {
            _config = config;
            _policeConfig = policeConfig;
            Reload();
        }

        public void Reload()
        {
            CleanupOwnedScene();
            _activities.Clear();
            _sceneWeapons.Clear();
            _active = null;
            _onScene = false;
            _investigationRequested = false;
            _sawThreat = false;
            _waypointOwned = false;
            LoadSceneWeapons();
            LoadActivities();
            _nextScan = DateTime.MinValue;
            LspdResponseLog.Write("POLICE_CHAOS_ACTIVITY", "Reloaded | Activities=" + _activities.Count + " | SceneWeapons=" + _sceneWeapons.Count + " | Root=" + _policeConfig.ChaosActivityRoot);
        }

        public void Reset()
        {
            if (_waypointOwned)
            {
                try { World.RemoveWaypoint(); } catch { }
            }
            _waypointOwned = false;
            CleanupOwnedScene();
            CleanupAllDeferred();
            _active = null;
            _onScene = false;
            _investigationRequested = false;
            _sawThreat = false;
            _activeStarted = DateTime.MinValue;
            _noThreatSince = DateTime.MinValue;
            _cooldowns.Clear();
            _nextScan = DateTime.MinValue;
        }

        public string Investigate(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData, AnyiLSPDChaosAudio audio)
        {
            if (_active == null)
                return "No Chaos Gang Activity is currently active.";
            if (player == null || !player.Exists())
                return "Player character unavailable.";

            float distance = _active.Center.DistanceTo(player.Position);
            if (distance > _config.ChaosGangActivityInvestigationRadius)
                return "Move closer to the marked Chaos Gang Activity scene.";

            if (!_onScene)
                _onScene = true;
            if (_investigationRequested)
                return "Chaos Gang Activity investigation is already active.";

            _investigationRequested = true;
            _noThreatSince = DateTime.MinValue;
            _sawThreat = _ownedPeds.Any(p => p != null && p.Exists() && !p.IsDead) || _ownedVehicles.Any(v => v != null && v.Exists());
            IssueInvestigationTasks(player);

            Notification.PostTicker(
                "~b~ANYI LSPD~s~\nCHAOS GANG ACTIVITY\n~c~Investigation started at " + ToDisplayTitle(_active.Name) + ". Secure the scene.",
                false,
                false);

            LspdResponseLog.Write("POLICE_CHAOS_ACTIVITY", "INVESTIGATION_STARTED | Activity=" + _active.Name + " | Distance=" + distance.ToString("F1") + " | SpawnedPeds=" + _ownedPeds.Count + " | SpawnedVehicles=" + _ownedVehicles.Count);
            return "Chaos Gang Activity investigation started.";
        }

        public void Update(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData, AnyiLSPDChaosAudio audio)
        {
            ProcessDeferredCleanup(player);
            if (!_config.EnableChaosGangActivityDiscovery || player == null || !player.Exists())
                return;

            DateTime now = DateTime.UtcNow;
            try
            {
                if (_active != null)
                {
                    UpdateActive(player, nearby, gangData, audio, now);
                    return;
                }

                if (now < _nextScan)
                    return;

                _nextScan = now.AddSeconds(_config.ChaosGangActivityScanSeconds);
                ActivityDefinition nearest = FindNearestAvailable(player.Position, now);
                if (nearest == null)
                    return;

                Start(nearest, player, gangData, audio, now);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CHAOS_ACTIVITY_ERROR", ex);
            }
        }

        private void Start(ActivityDefinition activity, Ped player, AnyiLSPDPoliceData.GangSnapshot gangData, AnyiLSPDChaosAudio audio, DateTime now)
        {
            _active = activity;
            _activeStarted = now;
            _onScene = false;
            _investigationRequested = false;
            _sawThreat = false;
            _noThreatSince = DateTime.MinValue;

            SpawnOwnedScene(activity, gangData, player);

            string title = ToDisplayTitle(activity.Name);
            Notification.PostTicker("~b~ANYI LSPD~s~\nCHAOS GANG ACTIVITY\n~c~" + title + " reported nearby. Follow the waypoint and press Investigate.", false, false);

            if (_config.ChaosGangActivityUseWaypoint)
            {
                try
                {
                    World.WaypointPosition = activity.Center;
                    _waypointOwned = true;
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("POLICE_CHAOS_ACTIVITY_WAYPOINT_ERROR", ex);
                }
            }

            if (audio != null)
                audio.Play("REQUEST_BACKUP");

            LspdResponseLog.Write("POLICE_CHAOS_ACTIVITY", "DISCOVERED | Activity=" + activity.Name + " | Center=" + activity.Center + " | Peds=" + activity.PedObjects + " | Vehicles=" + activity.VehicleObjects + " | VehicleFocused=" + activity.VehicleFocused + " | Ambush=" + activity.Ambush + " | Source=ChaosResponse.GangActivity XML");
        }

        private void UpdateActive(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData, AnyiLSPDChaosAudio audio, DateTime now)
        {
            float distance = _active.Center.DistanceTo(player.Position);
            if (!_onScene && distance <= _config.ChaosGangActivityInvestigationRadius)
            {
                _onScene = true;
                Notification.PostTicker("~b~ANYI LSPD~s~\nCHAOS GANG ACTIVITY\n~c~Scene reached. Press Investigate to begin the police action.", false, false);
                LspdResponseLog.Write("POLICE_CHAOS_ACTIVITY", "ON_SCENE | Activity=" + _active.Name + " | Distance=" + distance.ToString("F1"));
            }

            if (!_investigationRequested)
            {
                if (now >= _activeStarted.AddSeconds(_config.ChaosGangActivityStaleCleanupSeconds))
                    FinishAbandoned(audio, now);
                return;
            }

            bool threatPresent = HasHostileActivity(nearby, player, gangData);
            if (threatPresent)
            {
                _sawThreat = true;
                _noThreatSince = DateTime.MinValue;
                return;
            }

            if (_noThreatSince == DateTime.MinValue)
                _noThreatSince = now;

            if (now >= _noThreatSince.AddSeconds(_config.ChaosGangActivityResolutionHoldSeconds))
            {
                if (_sawThreat || _config.ChaosGangActivityAllowClearSceneSuccess)
                    FinishResolved(audio, now);
            }
        }

        private bool HasHostileActivity(Ped[] nearby, Ped player, AnyiLSPDPoliceData.GangSnapshot data)
        {
            bool unresolvedOwnedActor = false;
            foreach (Ped owned in _ownedPeds.ToArray())
            {
                try
                {
                    if (owned == null || !owned.Exists() || owned.IsDead)
                        continue;
                    if (_active != null && owned.Position.DistanceTo(_active.Center) > 140f)
                        continue;
                    if (owned.IsShooting || owned.IsInCombatAgainst(player) || owned.IsFleeing)
                        return true;
                    unresolvedOwnedActor = true;
                }
                catch { }
            }
            if (unresolvedOwnedActor)
                return true;

            if (nearby == null) return false;
            foreach (Ped ped in nearby)
            {
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead || ped.Handle == player.Handle)
                        continue;
                    if (ped.Position.DistanceTo(player.Position) > _config.ChaosGangActivityInvestigationRadius + 25f)
                        continue;
                    if (!ped.IsShooting && !ped.IsInCombatAgainst(player) && !ped.IsFleeing)
                        continue;

                    AnyiLSPDPoliceData.GangProfile profile = data == null ? null : data.FindGangForModel(ped.Model.Hash);
                    if (profile != null && profile.PlayerOwned)
                        continue;
                    return true;
                }
                catch { }
            }
            return false;
        }

        private void IssueInvestigationTasks(Ped player)
        {
            bool pursuit = IsVehiclePursuit(_active);
            bool ambush = _active.Ambush;
            int index = 0;

            foreach (Ped ped in _ownedPeds.ToArray())
            {
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead)
                        continue;

                    Vehicle currentVehicle = ped.CurrentVehicle;
                    if (pursuit && currentVehicle != null && currentVehicle.Exists() && ped.IsInVehicle())
                    {
                        ped.Task.VehicleChase(player);
                        index++;
                        continue;
                    }

                    if (ambush || _active.Ambush || _active.VehicleFocused && _active.Name.IndexOf("shoot", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ped.Task.Combat(player);
                    }
                    else if ((_active.FootPursuit || index % 3 == 0) && index > 1)
                    {
                        ped.Task.ReactAndFlee(player);
                    }
                    else
                    {
                        ped.Task.Combat(player);
                    }
                    index++;
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("POLICE_CHAOS_ACTIVITY_TASK_ERROR", ex);
                }
            }
        }

        private void SpawnOwnedScene(ActivityDefinition activity, AnyiLSPDPoliceData.GangSnapshot gangData, Ped player)
        {
            CleanupOwnedScene();

            int pedCount = Math.Max(1, Math.Min(_config.ChaosGangActivityMaxScenePeds, activity.PedObjects));
            bool vehicleActivity = activity.VehicleFocused || activity.Ambush || activity.FootPursuit && activity.VehicleObjects > 0;
            int vehicleCount = Math.Max(0, Math.Min(_config.ChaosGangActivityMaxSceneVehicles, activity.VehicleObjects));
            if (vehicleActivity && vehicleCount == 0)
                vehicleCount = Math.Min(1, _config.ChaosGangActivityMaxSceneVehicles);

            int actorHash = ResolveActorHash(activity.Center, gangData);
            Model actorModel = new Model(actorHash);
            if (!actorModel.IsValid || !actorModel.IsPed || !actorModel.Request(1500) || !actorModel.IsLoaded)
            {
                actorModel = new Model(_config.ChaosGangActivityFallbackPedModel);
                if (!actorModel.Request(1500) || !actorModel.IsLoaded || !actorModel.IsPed)
                {
                    LspdResponseLog.Write("POLICE_CHAOS_ACTIVITY_SPAWN", "Actor model unavailable; activity remains location-based only.");
                    actorModel = null;
                }
            }

            try
            {
                if (actorModel != null)
                {
                    for (int i = 0; i < pedCount; i++)
                    {
                        Vector3 spawn;
                        if (activity.PedPositions.Count > 0)
                            spawn = activity.PedPositions[Math.Min(i, activity.PedPositions.Count - 1)];
                        else
                        {
                            double angle = (Math.PI * 2.0 * i) / Math.Max(1, pedCount);
                            spawn = activity.Center + new Vector3((float)Math.Cos(angle) * _config.ChaosGangActivitySceneSpawnRadius, (float)Math.Sin(angle) * _config.ChaosGangActivitySceneSpawnRadius, 0f);
                        }

                        Ped ped = World.CreatePed(actorModel, spawn);
                        if (ped == null || !ped.Exists()) continue;
                        ped.IsPersistent = true;
                        ped.BlockPermanentEvents = true;
                        ApplySceneWeapon(ped, ResolveWeaponCategory(activity, i));
                        IssuePreArrivalBehavior(ped, activity, i, player);
                        _ownedPeds.Add(ped);
                        AddSceneEntityBlip(ped, "Chaos Suspect");
                    }
                }

                for (int i = 0; i < vehicleCount; i++)
                {
                    Model vehicleModel = new Model(ResolveSceneVehicleModel(activity, i));
                    if (!vehicleModel.IsValid || !vehicleModel.IsVehicle || !vehicleModel.Request(1500) || !vehicleModel.IsLoaded)
                        continue;

                    Vector3 vehicleSpawn;
                    if (activity.VehiclePositions.Count > 0)
                        vehicleSpawn = activity.VehiclePositions[Math.Min(i, activity.VehiclePositions.Count - 1)];
                    else
                    {
                        double angle = (Math.PI * 2.0 * i) / Math.Max(1, vehicleCount);
                        vehicleSpawn = activity.Center + new Vector3((float)Math.Cos(angle) * (_config.ChaosGangActivitySceneSpawnRadius + 5f), (float)Math.Sin(angle) * (_config.ChaosGangActivitySceneSpawnRadius + 5f), 0f);
                    }
                    Vehicle vehicle = World.CreateVehicle(vehicleModel, vehicleSpawn, player == null ? 0f : player.Heading);
                    if (vehicle != null && vehicle.Exists())
                    {
                        vehicle.IsPersistent = true;
                        vehicle.PlaceOnGround();
                        _ownedVehicles.Add(vehicle);
                        AddSceneEntityBlip(vehicle, "Chaos Scene Vehicle");

                        if (actorModel != null)
                        {
                            Ped driver = vehicle.CreatePedOnSeat(VehicleSeat.Driver, actorModel);
                            if (driver != null && driver.Exists())
                            {
                                driver.IsPersistent = true;
                                driver.BlockPermanentEvents = true;
                                ApplySceneWeapon(driver, ResolveWeaponCategory(activity, i + pedCount));
                                if (activity.VehicleFocused || activity.Ambush)
                                    Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, driver, vehicle, 12f, 786603);
                                else
                                    IssuePreArrivalBehavior(driver, activity, i + pedCount, player);
                                _ownedPeds.Add(driver);
                            }
                        }
                    }
                    vehicleModel.MarkAsNoLongerNeeded();
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CHAOS_ACTIVITY_SPAWN_ERROR", ex);
            }
            finally
            {
                if (actorModel != null)
                    actorModel.MarkAsNoLongerNeeded();
            }
        }

        private void IssuePreArrivalBehavior(Ped ped, ActivityDefinition activity, int index, Ped player)
        {
            try
            {
                if (activity.Ambush && index == 0 && player != null && player.Exists())
                {
                    ped.Task.AimAt(player, 4000);
                    return;
                }

                // Keep the scene alive before Anyi arrives: actors wander/loiter instead of being statues.
                Function.Call(Hash.TASK_WANDER_IN_AREA, ped, activity.Center.X, activity.Center.Y, activity.Center.Z, 10f, 1f, 3f);
            }
            catch
            {
                try { Function.Call(Hash.TASK_WANDER_STANDARD, ped, 10f, 10); } catch { }
            }
        }

        private string ResolveSceneVehicleModel(ActivityDefinition activity, int index)
        {
            if (activity.Name.IndexOf("police", StringComparison.OrdinalIgnoreCase) >= 0)
                return _config.ChaosGangActivityFallbackVehicleModel;
            return index % 2 == 0 ? _config.ChaosGangActivityFallbackVehicleModel : "baller";
        }

        private string ResolveWeaponCategory(ActivityDefinition activity, int index)
        {
            if (activity.Ambush)
                return "Ambush";
            if (activity.Name.IndexOf("knife", StringComparison.OrdinalIgnoreCase) >= 0 || activity.Name.IndexOf("melee", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Melee";
            if (activity.VehicleFocused || activity.FootPursuit)
                return "Medium";
            if (activity.Name.IndexOf("shoot", StringComparison.OrdinalIgnoreCase) >= 0 || activity.Name.IndexOf("arms", StringComparison.OrdinalIgnoreCase) >= 0)
                return "High";
            return "Low";
        }

        private void ApplySceneWeapon(Ped ped, string category)
        {
            try
            {
                SceneWeapon weapon;
                if (!_sceneWeapons.TryGetValue(category, out weapon))
                    _sceneWeapons.TryGetValue("Low", out weapon);
                if (weapon == null || weapon.Hash == 0)
                    return;

                Function.Call(Hash.GIVE_WEAPON_TO_PED, ped, weapon.Hash, weapon.Ammo, false, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, ped, weapon.Hash, true);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CHAOS_ACTIVITY_WEAPON_ERROR", ex);
            }
        }

        private int ResolveActorHash(Vector3 center, AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            try
            {
                if (gangData != null)
                {
                    string owner = gangData.GetTerritoryOwner(center.X, center.Y, center.Z);
                    if (!string.IsNullOrWhiteSpace(owner) && !string.Equals(owner, gangData.PlayerGang == null ? string.Empty : gangData.PlayerGang.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        AnyiLSPDPoliceData.GangProfile profile = gangData.Gangs.FirstOrDefault(g => string.Equals(g.Name, owner, StringComparison.OrdinalIgnoreCase));
                        if (profile != null && profile.MemberHashes != null)
                        {
                            foreach (int hash in profile.MemberHashes)
                            {
                                Model candidate = new Model(hash);
                                if (candidate.IsValid && candidate.IsPed)
                                    return hash;
                            }
                        }
                    }
                }
            }
            catch { }

            return unchecked((int)StringHash.AtStringHash(_config.ChaosGangActivityFallbackPedModel, 0));
        }

        private void FinishResolved(AnyiLSPDChaosAudio audio, DateTime now)
        {
            Notification.PostTicker("~g~LSPD GANG ACTIVITY RESOLVED~s~\n~c~Mission justified. The reported activity has been secured.", false, false);
            if (audio != null)
                audio.Play("CASE_CLOSED");

            LspdResponseLog.Write("POLICE_CHAOS_ACTIVITY", "RESOLVED | Activity=" + _active.Name + " | Investigated=" + _investigationRequested + " | SawThreat=" + _sawThreat + " | SpawnedPeds=" + _ownedPeds.Count + " | SpawnedVehicles=" + _ownedVehicles.Count);
            FinishInternal(now, _active.Name);
        }

        private void FinishAbandoned(AnyiLSPDChaosAudio audio, DateTime now)
        {
            Notification.PostTicker("~y~LSPD CHAOS ACTIVITY~s~\n~c~The reported activity was not investigated and has been released from the patrol queue.", false, false);
            FinishInternal(now, _active.Name);
        }

        private void FinishInternal(DateTime now, string name)
        {
            if (_waypointOwned)
            {
                try { World.RemoveWaypoint(); } catch { }
            }
            _waypointOwned = false;
            QueueOwnedSceneCleanup(now);
            if (!string.IsNullOrWhiteSpace(name))
                _cooldowns[name] = now.AddSeconds(_config.ChaosGangActivityCooldownSeconds);
            _active = null;
            _onScene = false;
            _investigationRequested = false;
            _sawThreat = false;
            _activeStarted = DateTime.MinValue;
            _noThreatSince = DateTime.MinValue;
        }

        private void QueueOwnedSceneCleanup(DateTime now)
        {
            Vector3 anchor = _active == null ? Vector3.Zero : _active.Center;
            DateTime earliest = now.AddSeconds(_policeConfig.CompletedEntityCleanupGraceSeconds);
            DateTime expires = now.AddSeconds(_policeConfig.CompletedEntityCleanupMaxSeconds);

            foreach (Ped ped in _ownedPeds.ToArray())
            {
                if (ped == null || !ped.Exists()) continue;
                try { ped.IsPersistent = true; ped.BlockPermanentEvents = false; } catch { }
                _deferredCleanup.Add(new DeferredCleanup { Ped = ped, Earliest = earliest, Expires = expires, Anchor = anchor });
            }
            foreach (Vehicle vehicle in _ownedVehicles.ToArray())
            {
                if (vehicle == null || !vehicle.Exists()) continue;
                try { vehicle.IsPersistent = true; } catch { }
                _deferredCleanup.Add(new DeferredCleanup { Vehicle = vehicle, Earliest = earliest, Expires = expires, Anchor = anchor });
            }
            _ownedPeds.Clear();
            _ownedVehicles.Clear();
        }

        private void ProcessDeferredCleanup(Ped player)
        {
            if (_deferredCleanup.Count == 0)
                return;

            DateTime now = DateTime.UtcNow;
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
            {
                bool exists = (item.Ped != null && item.Ped.Exists()) || (item.Vehicle != null && item.Vehicle.Exists());
                if (!exists)
                {
                    _deferredCleanup.Remove(item);
                    continue;
                }

                float distance = float.MaxValue;
                if (player != null && player.Exists())
                {
                    if (item.Ped != null && item.Ped.Exists()) distance = item.Ped.Position.DistanceTo(player.Position);
                    else if (item.Vehicle != null && item.Vehicle.Exists()) distance = item.Vehicle.Position.DistanceTo(player.Position);
                }

                if (now >= item.Earliest && (distance >= _policeConfig.CompletedEntityCleanupDistance || now >= item.Expires))
                {
                    try { if (item.Ped != null && item.Ped.Exists()) item.Ped.Delete(); } catch { }
                    try { if (item.Vehicle != null && item.Vehicle.Exists()) item.Vehicle.Delete(); } catch { }
                    _deferredCleanup.Remove(item);
                }
            }
        }

        private void CleanupAllDeferred()
        {
            foreach (DeferredCleanup item in _deferredCleanup.ToArray())
            {
                try { if (item.Ped != null && item.Ped.Exists()) item.Ped.Delete(); } catch { }
                try { if (item.Vehicle != null && item.Vehicle.Exists()) item.Vehicle.Delete(); } catch { }
            }
            _deferredCleanup.Clear();
        }

        private void AddSceneEntityBlip(Entity entity, string name)
        {
            try
            {
                if (entity == null || !entity.Exists()) return;
                Blip blip = entity.AddBlip();
                if (blip != null && blip.Exists())
                {
                    blip.Name = name;
                    blip.IsShortRange = false;
                    _sceneEntityBlips.Add(blip);
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CHAOS_ACTIVITY_BLIP_ERROR", ex);
            }
        }

        private void CleanupSceneEntityBlips()
        {
            foreach (Blip blip in _sceneEntityBlips.ToArray())
            {
                try { if (blip != null && blip.Exists()) blip.Delete(); } catch { }
            }
            _sceneEntityBlips.Clear();
        }

        private void CleanupOwnedScene()
        {
            foreach (Ped ped in _ownedPeds.ToArray())
            {
                try { if (ped != null && ped.Exists()) ped.Delete(); } catch { }
            }
            foreach (Vehicle vehicle in _ownedVehicles.ToArray())
            {
                try { if (vehicle != null && vehicle.Exists()) vehicle.Delete(); } catch { }
            }
            _ownedPeds.Clear();
            _ownedVehicles.Clear();
            CleanupSceneEntityBlips();
        }

        private ActivityDefinition FindNearestAvailable(Vector3 playerPosition, DateTime now)
        {
            ActivityDefinition best = null;
            float bestDistance = float.MaxValue;
            foreach (ActivityDefinition activity in _activities)
            {
                DateTime ready;
                if (_cooldowns.TryGetValue(activity.Name, out ready) && now < ready)
                    continue;

                float distance = activity.Center.DistanceTo(playerPosition);
                if (distance <= _config.ChaosGangActivityDiscoverRadius && distance < bestDistance)
                {
                    best = activity;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private void LoadSceneWeapons()
        {
            string scripts = AnyiLSPDPathProvider.ScriptsDirectory;
            string path = Path.Combine(scripts, "AnyiLSPDPoliceSceneWeapons.xml");
            if (!File.Exists(path))
                return;

            try
            {
                XDocument doc = XDocument.Load(path);
                foreach (XElement node in doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "Weapon", StringComparison.OrdinalIgnoreCase)))
                {
                    string category = (string)node.Attribute("category") ?? "Low";
                    string hashText = (string)node.Attribute("hash") ?? "0";
                    int hash = ParseHash(hashText);
                    int ammo = ReadInt(node.Attribute("ammo"), 90);
                    if (hash == 0) continue;
                    _sceneWeapons[category] = new SceneWeapon { Hash = hash, Ammo = ammo, Name = (string)node.Attribute("name") ?? category };
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_CHAOS_ACTIVITY_WEAPON_XML_ERROR", ex);
            }
        }

        private void LoadActivities()
        {
            if (!Directory.Exists(_policeConfig.ChaosActivityRoot))
            {
                LspdResponseLog.Write("POLICE_CHAOS_ACTIVITY", "Root missing | " + _policeConfig.ChaosActivityRoot);
                return;
            }

            foreach (string file in Directory.GetFiles(_policeConfig.ChaosActivityRoot, "*.xml", SearchOption.AllDirectories))
            {
                try
                {
                    XDocument doc = XDocument.Load(file);
                    List<Vector3> positions = new List<Vector3>();
                    List<Vector3> currentPedPositions = new List<Vector3>();
                    List<Vector3> currentVehiclePositions = new List<Vector3>();
                    int pedCount = 0, vehicleCount = 0, propCount = 0;

                    foreach (XElement obj in doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "MapObject", StringComparison.OrdinalIgnoreCase)))
                    {
                        XElement position = obj.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "Position", StringComparison.OrdinalIgnoreCase));
                        Vector3? parsed = ParsePosition(position);
                        if (parsed.HasValue) positions.Add(parsed.Value);

                        string type = (string)obj.Element("Type") ?? string.Empty;
                        if (type.Equals("Ped", StringComparison.OrdinalIgnoreCase))
                        {
                            pedCount++;
                            if (parsed.HasValue) currentPedPositions.Add(parsed.Value);
                        }
                        else if (type.Equals("Vehicle", StringComparison.OrdinalIgnoreCase))
                        {
                            vehicleCount++;
                            if (parsed.HasValue) currentVehiclePositions.Add(parsed.Value);
                        }
                        else if (type.Equals("Prop", StringComparison.OrdinalIgnoreCase)) propCount++;
                    }

                    if (positions.Count == 0)
                        continue;

                    Vector3 center = currentPedPositions.Count > 0
                        ? Average(currentPedPositions)
                        : (currentVehiclePositions.Count > 0 ? Average(currentVehiclePositions) : Average(positions));
                    string name = Path.GetFileNameWithoutExtension(file);
                    string lower = name.ToLowerInvariant();
                    _activities.Add(new ActivityDefinition
                    {
                        Name = name,
                        Center = center,
                        PedObjects = pedCount,
                        VehicleObjects = vehicleCount,
                        PropObjects = propCount,
                        VehicleFocused = lower.Contains("vehicle") || lower.Contains("pursuit") || lower.Contains("reckless") || lower.Contains("hijack") || lower.Contains("car"),
                        Ambush = lower.Contains("ambush"),
                        FootPursuit = lower.Contains("foot") || lower.Contains("pedestrian") || lower.Contains("flee")
                    });
                    ActivityDefinition loadedActivity = _activities[_activities.Count - 1];
                    loadedActivity.PedPositions.AddRange(currentPedPositions);
                    loadedActivity.VehiclePositions.AddRange(currentVehiclePositions);
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("POLICE_CHAOS_ACTIVITY_LOAD_ERROR", ex);
                }
            }
        }

        private static int ParseHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Trim();
            try
            {
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    long parsed = Convert.ToInt64(value.Substring(2), 16);
                    return unchecked((int)parsed);
                }
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return 0; }
        }

        private static int ReadInt(XAttribute attribute, int fallback)
        {
            if (attribute == null) return fallback;
            int value;
            return int.TryParse(attribute.Value, out value) ? value : fallback;
        }

        private static Vector3 Average(List<Vector3> positions)
        {
            if (positions == null || positions.Count == 0) return Vector3.Zero;
            float x = 0f, y = 0f, z = 0f;
            foreach (Vector3 p in positions) { x += p.X; y += p.Y; z += p.Z; }
            float count = positions.Count;
            return new Vector3(x / count, y / count, z / count);
        }

        private static Vector3? ParsePosition(XElement position)
        {
            if (position == null) return null;
            return new Vector3(ReadCoordinate(position, "X"), ReadCoordinate(position, "Y"), ReadCoordinate(position, "Z"));
        }

        private static float ReadCoordinate(XElement element, string name)
        {
            XAttribute attribute = element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            if (attribute != null) return ReadFloat(attribute.Value);
            XElement child = element.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
            return child == null ? 0f : ReadFloat(child.Value);
        }

        private static float ReadFloat(string value)
        {
            float result;
            return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result) ? result : 0f;
        }

        private static string ToDisplayTitle(string fileName)
        {
            string name = (fileName ?? string.Empty).Trim();
            return name.Length == 0 ? "Gang activity" : name;
        }

        private static bool IsVehiclePursuit(ActivityDefinition activity)
        {
            return activity != null && (activity.VehicleFocused && (activity.Name.IndexOf("pursuit", StringComparison.OrdinalIgnoreCase) >= 0 || activity.Name.IndexOf("reckless", StringComparison.OrdinalIgnoreCase) >= 0 || activity.Name.IndexOf("hijack", StringComparison.OrdinalIgnoreCase) >= 0));
        }
    }

}

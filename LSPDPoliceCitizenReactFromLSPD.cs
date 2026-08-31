using GTA;
using GTA.Math;
using GTA.Native;
using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    // Citizen-mode controlled police authority.
    // Anyi's Citizen protection is deliberately isolated from vanilla police.
    // Dispatch/automatic threat response uses exactly three Clorinde officers.
    public sealed class LspdPoliceCitizenReactFromLspd
    {
        private static readonly int[] PoliceModelHashes =
        {
            unchecked((int)StringHash.AtStringHash("s_m_y_cop_01", 0)),
            unchecked((int)StringHash.AtStringHash("s_f_y_cop_01", 0)),
            unchecked((int)StringHash.AtStringHash("s_m_y_sheriff_01", 0)),
            unchecked((int)StringHash.AtStringHash("s_f_y_sheriff_01", 0)),
            unchecked((int)StringHash.AtStringHash("s_m_y_hwaycop_01", 0)),
            unchecked((int)StringHash.AtStringHash("s_m_y_swat_01", 0)),
            unchecked((int)StringHash.AtStringHash("s_m_m_fiboffice_01", 0)),
            unchecked((int)StringHash.AtStringHash("s_m_m_ciasec_01", 0))
        };

        private readonly Dictionary<int, DateTime> _lastTaskAt =
            new Dictionary<int, DateTime>();

        private readonly List<Ped> _supportOfficers =
            new List<Ped>();

        private DateTime _supportOfficersExpiresAt =
            DateTime.MinValue;

        private DateTime _supportCooldownUntil =
            DateTime.MinValue;

        private bool _policeIgnoreWasSetByCitizenLayer;
        private bool _dispatchSuppressionWasSetByCitizenLayer;
        private bool _assuranceDismissalApplied;
        private readonly HashSet<int> _assuranceBlockedOfficerHandles =
            new HashSet<int>();

        public void Update(
            LspdCitizenSnapshot snapshot,
            LspdCitizenConfig config,
            DateTime now)
        {
            if (snapshot == null || snapshot.Player == null ||
                config == null)
            {
                return;
            }

            CleanupSupportOfficers(snapshot, now);

            bool mildInvestigation =
                config.EnableMildWantedInvestigation &&
                snapshot.WantedLevel > 0 &&
                snapshot.WantedLevel <= config.MildWantedMaximum &&
                !snapshot.IsRecentAggression;

            bool continuousChaos =
                snapshot.WantedLevel >= config.ContinuousChaosWantedLevel &&
                (snapshot.IsRecentAggression || snapshot.IsShooting);

            bool hasThreat =
                snapshot.ImmediateThreat != null &&
                snapshot.ImmediateThreat.Exists() &&
                !snapshot.ImmediateThreat.IsDead;

            // Assurance is a deliberate de-escalation action, not an arrest/bust
            // state. While Anyi is assuring officers and no attacker is present,
            // this layer stops assigning new police tasks and temporarily asks
            // the ambient police AI to leave the Citizen interaction alone.
            bool assuranceDismissal =
                snapshot.AssuranceActive &&
                !hasThreat &&
                snapshot.WantedLevel <= config.MildWantedMaximum;

            if (assuranceDismissal)
            {
                SetPoliceIgnore(true);
                SetDispatchCopsForPlayer(false);

                int dismissed = 0;

                foreach (Ped officer in snapshot.NearbyPeds)
                {
                    if (officer == null ||
                        !officer.Exists() ||
                        officer.IsDead ||
                        officer.Handle == snapshot.Player.Handle ||
                        !IsPolicePed(officer))
                    {
                        continue;
                    }

                    float distance = officer.Position.DistanceTo(
                        snapshot.Player.Position);

                    if (distance > config.PoliceAssistRadius)
                        continue;

                    if (_lastTaskAt.ContainsKey(officer.Handle))
                    {
                        DateTime lastTask;
                        if (_lastTaskAt.TryGetValue(officer.Handle, out lastTask) &&
                            lastTask > now.AddSeconds(-3))
                        {
                            // Recently handled by this Citizen layer.
                            // Let the officer settle naturally.
                        }
                    }

                    try
                    {
                        officer.Task.ClearAll();

            
                        _assuranceBlockedOfficerHandles.Add(
                            officer.Handle);

                        _lastTaskAt[officer.Handle] = now;
                        dismissed++;

                        LspdResponseLog.Write(
                            "CITIZEN_ASSURANCE_DISMISS",
                            "Police officer disengaged and target lock applied | Officer=" +
                            officer.Handle +
                            " | Distance=" +
                            distance.ToString("0.0"));
                    }
                    catch (Exception ex)
                    {
                        LspdResponseLog.WriteException(
                            "CITIZEN_ASSURANCE_DISMISS_ERROR",
                            ex);
                    }
                }

                if (!_assuranceDismissalApplied)
                {
                    LspdResponseLog.Write(
                        "CITIZEN_ASSURANCE",
                        "Assurance active | Police disengagement sweep complete | Officers=" +
                        dismissed +
                        " | Wanted=" +
                        snapshot.WantedLevel);
                }

                _assuranceDismissalApplied = true;
                return;
            }

            // Assurance has ended. Restore normal police AI only if this
            // Citizen layer enabled the temporary ignore state.
            if (_policeIgnoreWasSetByCitizenLayer)
            {
                SetPoliceIgnore(false);
            }

            RestoreAssuranceOfficerTargeting();
            SetDispatchCopsForPlayer(true);

            // Continuous chaos is the actual Citizen escalation gate.
            // One controlled officer receives the combat task at a time through
            // the existing task cooldown, avoiding a police swarm.
            if (continuousChaos)
            {
                AssignEscalatedPoliceResponse(
                    FindClosestPolice(snapshot),
                    snapshot.Player,
                    now,
                    snapshot.WantedLevel);
                return;
            }

            if (mildInvestigation)
            {
                AssignLookAt(
                    FindClosestPolice(snapshot),
                    snapshot.Player,
                    now,
                    "INVESTIGATE");
                return;
            }

            // A drawn knife/pistol/rifle without continuous chaos remains a
            // wary observation state. It does not automatically become combat.
            if (snapshot.HasWeaponDrawn &&
                !snapshot.IsShooting &&
                !snapshot.IsRecentAggression)
            {
                AssignLookAt(
                    FindClosestPolice(snapshot),
                    snapshot.Player,
                    now,
                    "WARY_OBSERVE");
            }
        }

        public string GreetNearestOfficer(
            LspdCitizenSnapshot snapshot)
        {
            Ped officer = FindClosestPolice(snapshot);
            if (officer == null)
                return "No nearby officer is available to greet.";

            AssignLookAt(
                officer,
                snapshot.Player,
                DateTime.UtcNow,
                "CITIZEN_GREETING");

            return "Nearby officer acknowledged your greeting.";
        }

        public string InteractWithNearestOfficer(
            LspdCitizenSnapshot snapshot)
        {
            Ped officer = FindClosestPolice(snapshot);
            if (officer == null)
                return "No nearby officer is available for an interaction.";

            AssignLookAt(
                officer,
                snapshot.Player,
                DateTime.UtcNow,
                "CITIZEN_INTERACTION");

            return "Nearby officer is observing your interaction.";
        }

        public string MakeAssurance(
            LspdCitizenSnapshot snapshot)
        {
            Ped officer = FindClosestPolice(snapshot);
            if (officer == null)
                return "Your assurance was recorded; no nearby officer can acknowledge it yet.";

            AssignLookAt(
                officer,
                snapshot.Player,
                DateTime.UtcNow,
                "CITIZEN_ASSURANCE");

            return "Nearby officer acknowledged your assurance and remains non-aggressive.";
        }

        public bool RequestSupport(
            LspdCitizenSnapshot snapshot,
            LspdCitizenConfig config,
            string reason)
        {
            if (snapshot == null ||
                snapshot.Player == null ||
                !snapshot.Player.Exists() ||
                config == null)
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;

            Ped threat = snapshot.ImmediateThreat;
            if (threat != null &&
                threat.Exists() &&
                !threat.IsDead)
            {
                return AssistAgainstThreat(
                    snapshot,
                    config,
                    now,
                    reason);
            }

            if (now < _supportCooldownUntil &&
                CountLiveSupport() == 0)
            {
                return false;
            }

            if (!EnsureSupportOfficers(
                snapshot,
                config,
                now))
            {
                return false;
            }

            MaintainGuardFormation(snapshot, now);

            LspdResponseLog.Write(
                "CITIZEN_DISPATCH",
                "Three Clorinde Police Authority officers deployed | Reason=" +
                reason +
                " | Live=" + CountLiveSupport());

            return CountLiveSupport() >=
                   Math.Max(1, config.SupportUnitCount);
        }

        public void ResetCitizenState()
        {
            SetPoliceIgnore(false);
            SetDispatchCopsForPlayer(true);
            RestoreAssuranceOfficerTargeting();
            _lastTaskAt.Clear();
            _assuranceDismissalApplied = false;
            DeleteAllSupport("Citizen state reset.");

            _supportOfficersExpiresAt = DateTime.MinValue;
            _supportCooldownUntil = DateTime.MinValue;
        }

        private bool AssistAgainstThreat(
            LspdCitizenSnapshot snapshot,
            LspdCitizenConfig config,
            DateTime now,
            string reason)
        {
            Ped threat = snapshot.ImmediateThreat;
            if (threat == null ||
                !threat.Exists() ||
                threat.IsDead)
            {
                return RequestSupport(
                    snapshot,
                    config,
                    reason);
            }

            if (!EnsureSupportOfficers(
                snapshot,
                config,
                now))
            {
                return false;
            }

            int assigned = 0;

            foreach (Ped officer in _supportOfficers)
            {
                if (officer == null ||
                    !officer.Exists() ||
                    officer.IsDead)
                {
                    continue;
                }

                if (!CanAssignTask(
                    officer,
                    now,
                    6))
                {
                    continue;
                }

                try
                {
                    officer.BlockPermanentEvents = true;
                    officer.Task.CombatTimed(
                        threat,
                        30000,
                        TaskCombatFlags.None);

                    _lastTaskAt[officer.Handle] = now;
                    assigned++;

                    LspdResponseLog.Write(
                        "CITIZEN_POLICE_TASK",
                        "Clorinde officer engaging threat | Officer=" +
                        officer.Handle +
                        " | Threat=" + threat.Handle +
                        " | Reason=" + reason);
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "CITIZEN_POLICE_TASK_ERROR",
                        ex);
                }
            }

            return assigned > 0;
        }

        private bool EnsureSupportOfficers(
            LspdCitizenSnapshot snapshot,
            LspdCitizenConfig config,
            DateTime now)
        {
            CleanupDeadReferences();

            int desired = Math.Max(
                1,
                Math.Min(
                    3,
                    config.SupportUnitCount));

            if (_supportOfficers.Count >= desired)
                return true;

            if (now < _supportCooldownUntil &&
                _supportOfficers.Count == 0)
            {
                return false;
            }

            Model model = CreateConfiguredModel(
                config.SupportOfficerModel);

            if (!model.IsValid ||
                !model.IsInCdImage ||
                !model.IsPed ||
                !model.Request(1500))
            {
                LspdResponseLog.Write(
                    "CITIZEN_SUPPORT_MODEL_ERROR",
                    "Unable to load configured Clorinde model | Model=" +
                    config.SupportOfficerModel);

                _supportCooldownUntil =
                    now.AddSeconds(15);

                return false;
            }

            try
            {
                while (_supportOfficers.Count < desired)
                {
                    Vector3 spawnPosition =
                        GetSupportSpawnPosition(
                            snapshot.Player,
                            _supportOfficers.Count);

                    Ped officer = World.CreatePed(
                        model,
                        spawnPosition,
                        snapshot.Player.Heading);

                    if (officer == null ||
                        !officer.Exists())
                    {
                        LspdResponseLog.Write(
                            "CITIZEN_SUPPORT_SPAWN_ERROR",
                            "World.CreatePed returned no Clorinde officer | Index=" +
                            _supportOfficers.Count);
                        break;
                    }

                    ConfigureSupportOfficer(
                        officer,
                        config);

                    _supportOfficers.Add(officer);

                    LspdResponseLog.Write(
                        "CITIZEN_SUPPORT_SPAWNED",
                        "Clorinde Police Authority deployed" +
                        " | Index=" + _supportOfficers.Count +
                        " | Handle=" + officer.Handle +
                        " | Model=" + officer.Model.Hash +
                        " | Health=" + officer.Health +
                        " | Armor=" + officer.Armor +
                        " | Invincible=" + officer.IsInvincible);
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "CITIZEN_SUPPORT_SPAWN_EXCEPTION",
                    ex);
            }
            finally
            {
                model.MarkAsNoLongerNeeded();
            }

            _supportOfficersExpiresAt =
                now.AddSeconds(
                    Math.Max(
                        10,
                        config.SupportUnitLifetimeSeconds));

            _supportCooldownUntil =
                now.AddSeconds(
                    Math.Max(
                        10,
                        config.SupportUnitCooldownSeconds));

            return CountLiveSupport() >= desired;
        }

        private static Model CreateConfiguredModel(string value)
        {
            int numericHash;
            if (int.TryParse(value, out numericHash))
                return new Model(numericHash);

            return new Model(value);
        }

        private static void ConfigureSupportOfficer(
            Ped officer,
            LspdCitizenConfig config)
        {
            officer.IsPersistent = true;
            officer.BlockPermanentEvents = true;
            officer.MaxHealth = Math.Max(
                250,
                config.SupportOfficerHealth);
            officer.Health = officer.MaxHealth;
            officer.Armor = Math.Max(
                100,
                config.SupportOfficerArmor);
            officer.Accuracy = Math.Max(
                50,
                Math.Min(
                    100,
                    config.SupportOfficerAccuracy));
            officer.CombatAbility = CombatAbility.Professional;
            officer.CombatRange = CombatRange.Far;
            officer.CanRagdoll = false;
            officer.IsInvincible = true;

            try
            {
                WeaponHash weapon = WeaponHash.HeavyRifle;
                if (string.Equals(
                    config.SupportOfficerWeapon,
                    "MilitaryRifle",
                    StringComparison.OrdinalIgnoreCase))
                {
                    weapon = WeaponHash.MilitaryRifle;
                }

                officer.Weapons.Give(
                    weapon,
                    9999,
                    true,
                    true);
                officer.Weapons.Select(weapon);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "CITIZEN_SUPPORT_WEAPON_ERROR",
                    ex);
            }
        }

        private static Vector3 GetSupportSpawnPosition(
            Ped player,
            int index)
        {
            float[] angles =
            {
                150.0f,
                210.0f,
                270.0f
            };

            float angle =
                angles[Math.Max(
                    0,
                    Math.Min(
                        index,
                        angles.Length - 1))] *
                (float)Math.PI /
                180.0f;

            Vector3 offset = new Vector3(
                (float)Math.Cos(angle) * 7.0f,
                (float)Math.Sin(angle) * 7.0f,
                0.0f);

            Vector3 requested =
                player.Position + offset;

            try
            {
                return World.GetNextPositionOnStreet(
                    requested,
                    true);
            }
            catch
            {
                return requested;
            }
        }

        private void MaintainGuardFormation(
            LspdCitizenSnapshot snapshot,
            DateTime now)
        {
            if (snapshot == null ||
                snapshot.Player == null)
            {
                return;
            }

            foreach (Ped officer in _supportOfficers)
            {
                if (officer == null ||
                    !officer.Exists() ||
                    officer.IsDead)
                {
                    continue;
                }

                if (!CanAssignTask(
                    officer,
                    now,
                    8))
                {
                    continue;
                }

                try
                {
                    officer.Task.StandStill(2500);
                    _lastTaskAt[officer.Handle] = now;
                }
                catch
                {
                }
            }
        }

        private void CleanupSupportOfficers(
            LspdCitizenSnapshot snapshot,
            DateTime now)
        {
            CleanupDeadReferences();

            bool threatActive =
                snapshot != null &&
                snapshot.ImmediateThreat != null &&
                snapshot.ImmediateThreat.Exists() &&
                !snapshot.ImmediateThreat.IsDead;

            if (_supportOfficers.Count == 0)
                return;

            if (threatActive)
                return;

            if (now < _supportOfficersExpiresAt)
                return;

            DeleteAllSupport(
                "Citizen threat ended / dispatch lifetime expired.");
        }

        private void CleanupDeadReferences()
        {
            for (int i = _supportOfficers.Count - 1;
                 i >= 0;
                 i--)
            {
                Ped officer = _supportOfficers[i];
                if (officer == null ||
                    !officer.Exists() ||
                    officer.IsDead)
                {
                    _supportOfficers.RemoveAt(i);
                }
            }
        }

        private int CountLiveSupport()
        {
            CleanupDeadReferences();
            return _supportOfficers.Count;
        }

        private void DeleteAllSupport(
            string reason)
        {
            foreach (Ped officer in _supportOfficers)
            {
                try
                {
                    if (officer == null ||
                        !officer.Exists())
                    {
                        continue;
                    }

                    officer.Task.ClearAll();
                    officer.IsInvincible = false;
                    officer.CanRagdoll = true;
                    officer.BlockPermanentEvents = false;
                    officer.IsPersistent = false;
                    officer.Delete();
                }
                catch
                {
                }
            }

            if (_supportOfficers.Count > 0)
            {
                LspdResponseLog.Write(
                    "CITIZEN_SUPPORT_DESPAWN",
                    "Clorinde Police Authority dismissed | Reason=" +
                    reason);
            }

            _supportOfficers.Clear();
        }

        private void AssignEscalatedPoliceResponse(
            Ped officer,
            Ped player,
            DateTime now,
            int wantedLevel)
        {
            if (officer == null ||
                player == null ||
                !officer.Exists() ||
                officer.IsDead ||
                officer.IsInCombat ||
                !CanAssignTask(officer, now, 8))
            {
                return;
            }

            try
            {
                officer.BlockPermanentEvents = true;
                officer.Task.CombatTimed(
                    player,
                    12000,
                    TaskCombatFlags.None);

                _lastTaskAt[officer.Handle] = now;

                LspdResponseLog.Write(
                    "CITIZEN_CHAOS_ESCALATION",
                    "Continuous chaos escalation | Stars=" +
                    wantedLevel +
                    " | Officer=" +
                    officer.Handle +
                    " | Player=" +
                    player.Handle +
                    " | Response=controlled police engagement");
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "CITIZEN_CHAOS_ESCALATION_ERROR",
                    ex);
            }
        }

        private static Ped FindClosestPolice(
            LspdCitizenSnapshot snapshot)
        {
            if (snapshot == null ||
                snapshot.NearbyPeds == null ||
                snapshot.Player == null)
            {
                return null;
            }

            Ped closest = null;
            float closestDistance = float.MaxValue;

            foreach (Ped ped in snapshot.NearbyPeds)
            {
                if (ped == null ||
                    !ped.Exists() ||
                    ped.IsDead ||
                    ped.Handle == snapshot.Player.Handle ||
                    !IsPolicePed(ped))
                {
                    continue;
                }

                float distance =
                    ped.Position.DistanceTo(
                        snapshot.Player.Position);

                if (distance < closestDistance)
                {
                    closest = ped;
                    closestDistance = distance;
                }
            }

            return closest;
        }

        private bool AssignLookAt(
            Ped officer,
            Ped player,
            DateTime now,
            string taskName)
        {
            if (officer == null ||
                player == null ||
                !officer.Exists() ||
                officer.IsDead ||
                officer.IsInCombat ||
                !CanAssignTask(
                    officer,
                    now,
                    8))
            {
                return false;
            }

            try
            {
                officer.Task.LookAt(
                    player,
                    2500);

                _lastTaskAt[officer.Handle] = now;

                LspdResponseLog.Write(
                    "POLICE_TASK",
                    taskName +
                    " | Officer=" +
                    officer.Handle +
                    " | Player=" +
                    player.Handle);

                return true;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_TASK_ERROR",
                    ex);

                return false;
            }
        }

        private bool CanAssignTask(
            Ped officer,
            DateTime now,
            int cooldownSeconds)
        {
            DateTime lastTask;

            return !_lastTaskAt.TryGetValue(
                       officer.Handle,
                       out lastTask) ||
                   now >= lastTask.AddSeconds(
                       Math.Max(
                           2,
                           cooldownSeconds));
        }

        private void RestoreAssuranceOfficerTargeting()
        {
            if (_assuranceBlockedOfficerHandles.Count == 0)
                return;

            try
            {
                Ped player = Game.Player.Character;
                if (player != null && player.Exists())
                {
                    Ped[] nearby = World.GetNearbyPeds(player, 110.0f);

                    if (nearby != null)
                    {
                        foreach (Ped officer in nearby)
                        {
                            if (officer == null ||
                                !officer.Exists() ||
                                officer.IsDead ||
                                !_assuranceBlockedOfficerHandles.Contains(
                                    officer.Handle))
                            {
                                continue;
                            }

                      
                        }
                    }
                }

                LspdResponseLog.Write(
                    "CITIZEN_ASSURANCE_RESTORE",
                    "Restored police targeting for " +
                    _assuranceBlockedOfficerHandles.Count +
                    " officer handle(s) previously blocked.");
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "CITIZEN_ASSURANCE_RESTORE_ERROR",
                    ex);
            }
            finally
            {
                _assuranceBlockedOfficerHandles.Clear();
            }
        }

        private void SetDispatchCopsForPlayer(bool enabled)
        {
            bool shouldSuppress = !enabled;

            if (shouldSuppress ==
                _dispatchSuppressionWasSetByCitizenLayer)
            {
                return;
            }

            try
            {
                Function.Call(
                    Hash.SET_DISPATCH_COPS_FOR_PLAYER,
                    Game.Player,
                    enabled);

                _dispatchSuppressionWasSetByCitizenLayer =
                    shouldSuppress;

                LspdResponseLog.Write(
                    "CITIZEN_AUTHORITY",
                    "Citizen dispatch suppression=" +
                    !enabled +
                    " | Wanted level remains untouched.");
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "CITIZEN_DISPATCH_CONTROL_ERROR",
                    ex);
            }
        }

        private void SetPoliceIgnore(
            bool shouldIgnore)
        {
            if (shouldIgnore ==
                _policeIgnoreWasSetByCitizenLayer)
            {
                return;
            }

            try
            {
                Function.Call(
                    Hash.SET_POLICE_IGNORE_PLAYER,
                    Game.Player,
                    shouldIgnore);

                _policeIgnoreWasSetByCitizenLayer =
                    shouldIgnore;

                LspdResponseLog.Write(
                    "CITIZEN_AUTHORITY",
                    "Temporary police-ignore state=" +
                    shouldIgnore +
                    " | Wanted level remains untouched.");
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "CITIZEN_AUTHORITY_ERROR",
                    ex);
            }
        }

        private static bool IsPolicePed(
            Ped ped)
        {
            int modelHash = ped.Model.Hash;

            foreach (int policeHash in PoliceModelHashes)
            {
                if (modelHash == policeHash)
                    return true;
            }

            return ped.IsInPoliceVehicle;
        }
    }
}

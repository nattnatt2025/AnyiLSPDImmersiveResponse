using GTA;
using System;
using System.Collections.Generic;
using GTA.Native;

namespace AnyiLSPD
{
    /// <summary>
    /// Gang-aware police observation/de-escalation.
    ///
    /// Important behavior:
    /// - Gang conflict is not automatically treated as Anyi personally attacking police.
    /// - During neutral Gang conflict, direct police combat against Anyi is cleared.
    /// - Officers are NOT repeatedly assigned LookAt tasks; that was causing the
    ///   visible "police ant-whirl" around the Gang Leader.
    /// - SET_POLICE_IGNORE_PLAYER is temporary and does not change wanted stars.
    /// </summary>
    public sealed class LspdPoliceReactToGangMemberAndTerritory
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

        private readonly Dictionary<int, DateTime> _taskCooldowns =
            new Dictionary<int, DateTime>();

        private bool _ignoreWasSet;

        public GangPoliceState Update(
            Ped player,
            Ped[] nearby,
            AnyiLSPDPoliceData.GangSnapshot data,
            LspdGangProfileCore profile,
            AnyiLSPDPoliceData.TurfZone currentTurf,
            bool currentTurfIsPlayerOwned,
            bool recentPersonalAggression,
            bool gangConflictActive,
            int wantedLevel,
            LspdGangConfig config,
            DateTime now)
        {
            GangPoliceState state = new GangPoliceState();

            if (player == null ||
                !player.Exists() ||
                nearby == null ||
                config == null ||
                profile == null)
            {
                return state;
            }

            bool inOwnTerritory = currentTurfIsPlayerOwned;

            bool neutralLeader =
                config.KeepPoliceNeutralDuringGangConflict &&
                gangConflictActive &&
                !recentPersonalAggression;

            bool lowLevel =
                wantedLevel <= config.LowWantedMaximum &&
                !recentPersonalAggression;

            state.InOwnTerritory = inOwnTerritory;
            state.LowLevel = lowLevel;
            state.GangConflict = gangConflictActive;
            state.PersonalAggression = recentPersonalAggression;
            state.Wary =
                (lowLevel || neutralLeader) &&
                (inOwnTerritory ||
                 wantedLevel > 0 ||
                 gangConflictActive);

            state.PoliceNearby = 0;

            if (config.EnablePoliceWary &&
                (state.Wary || neutralLeader))
            {
                SetPoliceIgnore(true);
                state.PoliceNearby =
                    DeescalateNearbyPolice(
                        player,
                        nearby,
                        profile,
                        config.PoliceWaryRadius,
                        config.TerritoryWaryRadius,
                        inOwnTerritory,
                        neutralLeader,
                        now,
                        config);
            }
            else
            {
                SetPoliceIgnore(false);
            }

            if (recentPersonalAggression)
            {
                state.ResponseStage =
                    wantedLevel >= 5
                        ? "High Conflict"
                        : "Leader Personal Offense";
            }
            else if (gangConflictActive)
            {
                state.ResponseStage =
                    "Police Intervening in Gang War";
            }
            else if (wantedLevel > 0)
            {
                state.ResponseStage =
                    "Police Investigation";
            }
            else if (inOwnTerritory)
            {
                state.ResponseStage =
                    "Territory Watch";
            }
            else
            {
                state.ResponseStage =
                    "Wary / Observing";
            }

            return state;
        }

        public void Reset()
        {
            SetPoliceIgnore(false);
            _taskCooldowns.Clear();
        }

        private int DeescalateNearbyPolice(
     Ped player,
     Ped[] nearby,
     LspdGangProfileCore profile,
     int policeWaryRadius,
     int territoryWaryRadius,
     bool inOwnTerritory,
     bool neutralLeader,
     DateTime now,
     LspdGangConfig config)
        {
            int affected = 0;
            int max = Math.Max(1, config.PoliceTasksPerScan);

            foreach (Ped officer in nearby)
            {
                if (affected >= max)
                    break;

                try
                {
                    if (officer == null ||
                        !officer.Exists() ||
                        officer.IsDead)
                    {
                        continue;
                    }

                    if (!IsPolicePed(officer) ||
                        officer.Handle == player.Handle)
                    {
                        continue;
                    }

                    float distance =
                        officer.Position.DistanceTo(
                            player.Position);

                    if (distance > policeWaryRadius)
                        continue;

                    // During Gang conflict, police should not keep attacking
                    // Anyi unless Anyi personally committed aggression.
                    if (neutralLeader)
                    {
                        if (officer.IsInCombatAgainst(player))
                        {
                            if (CanTask(
                                officer.Handle,
                                now,
                                config.CalmDeescalationCooldownSeconds))
                            {
                                officer.Task.ClearAll();
                                _taskCooldowns[officer.Handle] = now;
                                affected++;

                                LspdResponseLog.Write(
                                    "GANG_POLICE_DEESCALATION",
                                    "Gang-war neutrality: cleared police combat " +
                                    "against leader | Officer=" +
                                    officer.Handle +
                                    " | Player=" +
                                    player.Handle);
                            }
                        }

                        Ped warTarget = FindGangWarTarget(
                            player,
                            nearby,
                            profile,
                            config.GangProtectionRadius);

                        if (warTarget != null &&
                            warTarget.Exists() &&
                            !warTarget.IsDead &&
                            warTarget.Handle != officer.Handle &&
                            !officer.IsInCombatAgainst(warTarget) &&
                            CanTask(
                                officer.Handle,
                                now,
                                Math.Max(8, config.TaskCooldownSeconds)))
                        {
                            try
                            {
                                officer.BlockPermanentEvents = true;
                                officer.Task.CombatTimed(
                                    warTarget,
                                    20000,
                                    TaskCombatFlags.None);
                                _taskCooldowns[officer.Handle] = now;
                                affected++;

                                LspdResponseLog.Write(
                                    "GANG_POLICE_INTERVENTION",
                                    "Police intervening in Gang War without targeting leader" +
                                    " | Officer=" + officer.Handle +
                                    " | Target=" + warTarget.Handle +
                                    " | TargetModel=" + warTarget.Model.Hash);
                            }
                            catch (Exception ex)
                            {
                                LspdResponseLog.WriteException(
                                    "GANG_POLICE_INTERVENTION_ERROR",
                                    ex);
                            }
                        }

                        continue;
                    }

                    int observationRadius =
                        inOwnTerritory
                            ? territoryWaryRadius
                            : policeWaryRadius;

                    if (distance <= observationRadius &&
                        CanTask(
                            officer.Handle,
                            now,
                            config.TaskCooldownSeconds))
                    {
                        // No LookAt task here. GTA's ambient AI is allowed
                        // to observe without being forced into a whirl/orbit.
                        _taskCooldowns[officer.Handle] = now;
                        affected++;

                        LspdResponseLog.Write(
                            "GANG_POLICE_WARY",
                            "Officer remains wary without forced LookAt | Officer=" +
                            officer.Handle +
                            " | Distance=" +
                            distance.ToString("0.0"));
                    }
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "GANG_POLICE_REACTION_ERROR",
                        ex);
                }
            }

            return affected;
        }

        private static Ped FindGangWarTarget(
            Ped player,
            Ped[] nearby,
            LspdGangProfileCore profile,
            int radius)
        {
            if (player == null ||
                nearby == null ||
                profile == null)
            {
                return null;
            }

            // Prefer enemy gang members actively fighting Anyiii's Gang.
            foreach (Ped enemy in nearby)
            {
                try
                {
                    if (enemy == null ||
                        !enemy.Exists() ||
                        enemy.IsDead ||
                        enemy.Handle == player.Handle)
                    {
                        continue;
                    }

                    AnyiLSPDPoliceData.GangProfile enemyGang =
                        profile.FindGangForModel(enemy.Model.Hash);

                    if (enemyGang == null ||
                        profile.IsPlayerOwnedGangName(enemyGang.Name))
                    {
                        continue;
                    }

                    if (!enemy.IsInCombat && !enemy.IsShooting)
                        continue;

                    foreach (Ped own in nearby)
                    {
                        if (own == null ||
                            !own.Exists() ||
                            own.IsDead ||
                            own.Handle == player.Handle ||
                            !profile.IsPlayerGangMember(own.Model.Hash))
                        {
                            continue;
                        }

                        if (own.Position.DistanceTo(enemy.Position) <=
                            radius + 60)
                        {
                            return enemy;
                        }
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private void SetPoliceIgnore(bool value)
        {
            if (_ignoreWasSet == value)
                return;

            try
            {
                Function.Call(
                    Hash.SET_POLICE_IGNORE_PLAYER,
                    Game.Player,
                    value);

                _ignoreWasSet = value;

                LspdResponseLog.Write(
                    "GANG_POLICE_AUTHORITY",
                    "Temporary police-ignore=" +
                    value +
                    " | Wanted level remains untouched.");
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "GANG_POLICE_AUTHORITY_ERROR",
                    ex);
            }
        }

        private bool CanTask(
            int handle,
            DateTime now,
            int cooldownSeconds)
        {
            DateTime last;

            return !_taskCooldowns.TryGetValue(
                       handle,
                       out last) ||
                   now >= last.AddSeconds(
                       Math.Max(2, cooldownSeconds));
        }

        private static bool IsPolicePed(Ped ped)
        {
            int hash = ped.Model.Hash;

            foreach (int policeHash in PoliceModelHashes)
            {
                if (hash == policeHash)
                    return true;
            }

            return ped.IsInPoliceVehicle;
        }
    }

    public sealed class GangPoliceState
    {
        public bool InOwnTerritory;
        public bool LowLevel;
        public bool Wary;
        public bool GangConflict;
        public bool PersonalAggression;
        public int PoliceNearby;
        public string ResponseStage = "Wary / Observing";
    }
}

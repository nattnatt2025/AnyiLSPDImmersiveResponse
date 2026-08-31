using GTA;
using GTA.Math;
using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    /// <summary>
    /// Owns only Anyiii's Gang member interaction/protection state.
    /// Gang & Turf XML remains read-only.
    ///
    /// Conflict support:
    /// - Maintains exactly 3 protected support peds during an active Gang conflict.
    /// - Uses a real player-owned Gang member model hash from GangData.xml.
    /// - Support peds receive 100 armor, 1000 health, invincibility and no ragdoll.
    /// - Support peds receive a Heavy Rifle with full ammunition (no explosive projectile).
    /// - Support peds are persistent only while the conflict is active.
    /// - When the conflict ends, they are dismissed by deletion, not by killing them.
    /// </summary>
    public sealed class LspdGangMemberTerritoryCore
    {
        private const int DesiredConflictSupport = 3;
        private const int SupportHealth = 1000;
        private const int SupportArmor = 100;
        private const int SupportWeaponAmmo = 9999;

        private readonly Dictionary<int, DateTime> _taskCooldowns =
            new Dictionary<int, DateTime>();

        private readonly HashSet<int> _preferredPlayerGangMemberHashes =
            new HashSet<int>();

        private readonly List<Ped> _conflictSupportMembers =
            new List<Ped>();

        private DateTime _nextSupportMaintenance = DateTime.MinValue;

        public void SetPreferredPlayerGangMemberHashes(IEnumerable<int> hashes)
        {
            _preferredPlayerGangMemberHashes.Clear();

            if (hashes == null)
                return;

            foreach (int hash in hashes)
            {
                if (hash != 0)
                    _preferredPlayerGangMemberHashes.Add(hash);
            }
        }

        public AnyiLSPDPoliceData.TurfZone CurrentZone(
            AnyiLSPDPoliceData.GangSnapshot data,
            Ped player)
        {
            if (data == null || player == null)
                return null;

            return data.GetNearestTurf(
                player.Position.X,
                player.Position.Y,
                player.Position.Z,
                100f);
        }

        public Ped FindClosestPlayerGangMember(
            Ped player,
            Ped[] nearby,
            LspdGangProfileCore profile,
            int radius)
        {
            if (player == null || nearby == null || profile == null)
                return null;

            Ped closest = null;
            float closestDistance = float.MaxValue;

            foreach (Ped ped in nearby)
            {
                try
                {
                    if (!IsValidCandidate(ped, player, radius) ||
                        !IsPlayerGangMember(ped, profile))
                    {
                        continue;
                    }

                    float distance = ped.Position.DistanceTo(player.Position);
                    if (distance < closestDistance)
                    {
                        closest = ped;
                        closestDistance = distance;
                    }
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "GANG_MEMBER_CHECK_ERROR", ex);
                }
            }

            return closest;
        }

        public List<Ped> FindPlayerGangMembers(
            Ped player,
            Ped[] nearby,
            LspdGangProfileCore profile,
            int radius,
            int maximum)
        {
            List<Ped> result = new List<Ped>();

            if (player == null || nearby == null || profile == null)
                return result;

            foreach (Ped ped in nearby)
            {
                if (result.Count >= maximum)
                    break;

                try
                {
                    if (IsValidCandidate(ped, player, radius) &&
                        IsPlayerGangMember(ped, profile))
                    {
                        result.Add(ped);
                    }
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "GANG_MEMBER_SCAN_ERROR", ex);
                }
            }

            return result;
        }

        public Ped FindEnemyGangThreat(
            Ped player,
            Ped[] nearby,
            LspdGangProfileCore profile,
            int radius,
            out AnyiLSPDPoliceData.GangProfile enemyGang)
        {
            enemyGang = null;

            if (player == null || nearby == null || profile == null)
                return null;

            foreach (Ped ped in nearby)
            {
                try
                {
                    if (!IsValidCandidate(ped, player, radius))
                        continue;

                    AnyiLSPDPoliceData.GangProfile gang =
                        profile.FindGangForModel(ped.Model.Hash);

                    if (gang == null ||
                        profile.IsPlayerOwnedGangName(gang.Name))
                    {
                        continue;
                    }

                    if (ped.IsInCombatAgainst(player) ||
                        (ped.IsShooting &&
                         ped.HasClearLineOfSightTo(player)))
                    {
                        enemyGang = gang;
                        return ped;
                    }
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "ENEMY_GANG_SCAN_ERROR", ex);
                }
            }

            return null;
        }

        /// <summary>
        /// Finds an external Gang & Turf enemy that is actively fighting
        /// Anyiii's Gang. This is separate from "attacking Anyi" so support
        /// can join a real Gang War without ever choosing the player or a
        /// player-owned Gang member as a combat target.
        /// </summary>
        public Ped FindGangWarEnemyTarget(
            Ped player,
            Ped[] nearby,
            LspdGangProfileCore profile,
            int radius)
        {
            if (player == null || !player.Exists() ||
                nearby == null || profile == null)
                return null;

            foreach (Ped enemy in nearby)
            {
                try
                {
                    if (!IsValidCandidate(enemy, player, radius))
                        continue;

                    if (IsPlayerGangMember(enemy, profile))
                        continue;

                    AnyiLSPDPoliceData.GangProfile enemyGang =
                        profile.FindGangForModel(enemy.Model.Hash);

                    if (enemyGang == null ||
                        profile.IsPlayerOwnedGangName(enemyGang.Name))
                        continue;

                    if (!enemy.IsInCombat && !enemy.IsShooting)
                        continue;

                    foreach (Ped own in nearby)
                    {
                        if (own == null || !own.Exists() || own.IsDead ||
                            own.Handle == player.Handle)
                            continue;

                        if (!IsPlayerGangMember(own, profile))
                            continue;

                        if (own.Position.DistanceTo(enemy.Position) > radius + 60)
                            continue;

                        if (enemy.IsInCombatAgainst(own) ||
                            own.IsInCombatAgainst(enemy))
                        {
                            return enemy;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "GANG_WAR_TARGET_SCAN_ERROR", ex);
                }
            }

            return null;
        }

        /// <summary>
        /// Ensures 3 hardened support members exist during a conflict,
        /// then assigns them to fight the detected enemy.
        /// Existing live player-gang members are used first; spawned support
        /// fills the missing slots.
        /// </summary>
        public int DefendPlayer(
            Ped player,
            Ped threat,
            Ped[] nearby,
            LspdGangProfileCore profile,
            int radius,
            int maximum,
            DateTime now,
            int taskCooldownSeconds)
        {
            if (player == null ||
                !player.Exists() ||
                threat == null ||
                !threat.Exists() ||
                threat.IsDead ||
                profile == null)
            {
                return 0;
            }

            int desired = Math.Max(
                DesiredConflictSupport,
                Math.Max(1, Math.Min(DesiredConflictSupport, maximum)));

            EnsureConflictSupport(
                player,
                profile,
                desired,
                now);

            List<Ped> members = new List<Ped>();

            if (nearby != null)
            {
                foreach (Ped member in nearby)
                {
                    if (members.Count >= desired)
                        break;

                    if (IsValidCandidate(member, player, radius) &&
                        IsPlayerGangMember(member, profile) &&
                        !ContainsHandle(members, member))
                    {
                        members.Add(member);
                    }
                }
            }

            foreach (Ped support in _conflictSupportMembers)
            {
                if (support != null &&
                    support.Exists() &&
                    !support.IsDead &&
                    !ContainsHandle(members, support))
                {
                    members.Add(support);
                }
            }

            int assigned = 0;

            foreach (Ped member in members)
            {
                if (assigned >= desired)
                    break;

                try
                {
                    // HARD SAFETY GATE:
                    // Never order a support member to attack Anyi or another
                    // Anyiii's Gang member. Gang & Turf owns global relationships,
                    // so this explicit target validation is safer than changing
                    // relationship groups globally.
                    if (!IsSafeExternalThreat(player, threat, profile))
                    {
                        LspdResponseLog.Write(
                            "GANG_PROTECTION_BLOCKED",
                            "Refused unsafe support target" +
                            " | Member=" + member.Handle +
                           "| Threat=" + (threat == null ? "none" : threat.Handle.ToString()));
                        continue;
                    }

                    HardenSupport(member);
                    ClearSelfCombatIfNecessary(member, player, profile);

                    if (!CanTask(
                        member.Handle,
                        now,
                        taskCooldownSeconds))
                    {
                        continue;
                    }

                    if (member.Handle == player.Handle ||
                        member.IsInCombatAgainst(player) ||
                        !IsPlayerGangMember(member, profile))
                    {
                        continue;
                    }

                    if (member.IsInCombatAgainst(threat))
                    {
                        _taskCooldowns[member.Handle] = now;
                        continue;
                    }

                    member.Task.CombatTimed(
                        threat,
                        30000,
                        TaskCombatFlags.None);

                    _taskCooldowns[member.Handle] = now;
                    assigned++;

                    LspdResponseLog.Write(
                        "GANG_PROTECTION",
                        "Hardened Gang member defending Anyi" +
                        " | Member=" + member.Handle +
                        " | Threat=" + threat.Handle +
                        " | Health=" + member.Health +
                        " | Armor=" + member.Armor +
                        " | Invincible=" + member.IsInvincible +
                        " | Weapon=HeavyRifle");
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "GANG_PROTECTION_ERROR", ex);
                }
            }

            return assigned;
        }

        /// <summary>
        /// Called every Gang update. Keeps the three support peds alive while
        /// the conflict remains active and dismisses them once it is over.
        /// </summary>
        public void MaintainConflictSupport(
            Ped player,
            Ped threat,
            bool conflictActive,
            LspdGangProfileCore profile,
            DateTime now,
            int taskCooldownSeconds)
        {
            if (player == null ||
                !player.Exists() ||
                profile == null)
            {
                ClearConflictSupport();
                return;
            }

            if (!conflictActive)
            {
                ClearConflictSupport();
                return;
            }

            if (now < _nextSupportMaintenance)
                return;

            _nextSupportMaintenance = now.AddMilliseconds(1500);

            EnsureConflictSupport(
                player,
                profile,
                DesiredConflictSupport,
                now);

            foreach (Ped support in _conflictSupportMembers)
            {
                if (support == null ||
                    !support.Exists() ||
                    support.IsDead)
                {
                    continue;
                }

                HardenSupport(support);
                ClearSelfCombatIfNecessary(support, player, profile);

                if (threat != null &&
                    threat.Exists() &&
                    !threat.IsDead &&
                    IsSafeExternalThreat(player, threat, profile) &&
                    !support.IsInCombatAgainst(threat) &&
                    CanTask(
                        support.Handle,
                        now,
                        taskCooldownSeconds))
                {
                    try
                    {
                        support.Task.CombatTimed(
                            threat,
                            30000,
                            TaskCombatFlags.None);

                        _taskCooldowns[support.Handle] = now;
                    }
                    catch (Exception ex)
                    {
                        LspdResponseLog.WriteException(
                            "GANG_SUPPORT_TASK_ERROR", ex);
                    }
                }
            }
        }

        public int GetConflictSupportCount()
        {
            CleanupDeadSupportReferences();

            int count = 0;

            foreach (Ped ped in _conflictSupportMembers)
            {
                if (ped != null &&
                    ped.Exists() &&
                    !ped.IsDead)
                {
                    count++;
                }
            }

            return count;
        }

        public void ClearOwnedTaskState()
        {
            _taskCooldowns.Clear();
            ClearConflictSupport();
        }

        public string GreetMember(
            Ped player,
            Ped[] nearby,
            LspdGangProfileCore profile,
            int radius)
        {
            Ped member = FindClosestPlayerGangMember(
                player,
                nearby,
                profile,
                radius);

            if (member == null)
                return "No known Anyiii's Gang member is nearby.";

            try
            {
                member.Task.LookAt(player, 2500);
                return "Your gang member acknowledged you.";
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "GANG_GREETING_ERROR", ex);

                return "Gang greeting could not be assigned safely.";
            }
        }

        public string InteractMember(
            Ped player,
            Ped[] nearby,
            LspdGangProfileCore profile,
            int radius)
        {
            Ped member = FindClosestPlayerGangMember(
                player,
                nearby,
                profile,
                radius);

            if (member == null)
                return "No known Anyiii's Gang member is close enough.";

            try
            {
                member.Task.LookAt(player, 3000);
                return "Gang member is watching and available for interaction.";
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "GANG_INTERACTION_ERROR", ex);

                return "Gang interaction could not be assigned safely.";
            }
        }

        private void EnsureConflictSupport(
            Ped player,
            LspdGangProfileCore profile,
            int desired,
            DateTime now)
        {
            CleanupDeadSupportReferences();

            if (_conflictSupportMembers.Count >= desired)
                return;

            int modelHash = GetSupportModelHash(profile);

            if (modelHash == 0)
            {
                LspdResponseLog.Write(
                    "GANG_SUPPORT_ERROR",
                    "No valid player-owned Gang member model hash is available. " +
                    "No invented model was used.");

                return;
            }

            while (_conflictSupportMembers.Count < desired)
            {
                try
                {
                    Model model = new Model(modelHash);

                    if (!model.IsValid || !model.IsPed)
                    {
                        LspdResponseLog.Write(
                            "GANG_SUPPORT_MODEL_INVALID",
                            "Configured player-gang model is invalid | Hash=" +
                            modelHash);

                        model.MarkAsNoLongerNeeded();
                        break;
                    }

                    model.Request(1000);

                    if (!model.IsLoaded)
                    {
                        LspdResponseLog.Write(
                            "GANG_SUPPORT_MODEL_LOAD_FAIL",
                            "Could not load player-gang model | Hash=" +
                            modelHash);

                        model.MarkAsNoLongerNeeded();
                        break;
                    }

                    Vector3 spawnPosition =
                        GetSupportSpawnPosition(
                            player,
                            _conflictSupportMembers.Count);

                    Ped support = World.CreatePed(
                        model,
                        spawnPosition);

                    model.MarkAsNoLongerNeeded();

                    if (support == null || !support.Exists())
                    {
                        LspdResponseLog.Write(
                            "GANG_SUPPORT_SPAWN_FAIL",
                            "World.CreatePed returned no usable ped | Hash=" +
                            modelHash);

                        break;
                    }

                    support.IsPersistent = true;
                    support.BlockPermanentEvents = true;
                    HardenSupport(support);
                    support.Task.ClearAll();

                    // Do NOT alter Gang & Turf's global relationship groups.
                    // Protection uses an explicit target safety gate instead.

                    _conflictSupportMembers.Add(support);

                    LspdResponseLog.Write(
                        "GANG_SUPPORT_SPAWNED",
                        "Support member spawned" +
                        " | Index=" + _conflictSupportMembers.Count +
                        " | Handle=" + support.Handle +
                        " | Model=" + support.Model.Hash +
                        " | Health=" + support.Health +
                        " | Armor=" + support.Armor +
                        " | Invincible=" + support.IsInvincible +
                        " | Ragdoll=" + support.CanRagdoll + " | Weapon=HeavyRifle");
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "GANG_SUPPORT_SPAWN_EXCEPTION", ex);

                    break;
                }
            }

            _nextSupportMaintenance = now.AddMilliseconds(1000);
        }

        private static int GetSupportModelHash(
            LspdGangProfileCore profile)
        {
            if (profile == null ||
                profile.PlayerGang == null ||
                profile.PlayerGang.MemberHashes == null)
            {
                return 0;
            }

            foreach (int hash in profile.PlayerGang.MemberHashes)
            {
                if (hash != 0)
                    return hash;
            }

            return 0;
        }

        private static Vector3 GetSupportSpawnPosition(
            Ped player,
            int index)
        {
            float[] angles =
            {
                135.0f,
                225.0f,
                315.0f
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
                (float)Math.Cos(angle) * 5.0f,
                (float)Math.Sin(angle) * 5.0f,
                0.0f);

            return player.Position + offset;
        }

        private static void HardenSupport(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return;

            try
            {
                ped.MaxHealth = SupportHealth;
                ped.Health = SupportHealth;
            }
            catch
            {
            }

            try
            {
                ped.Armor = SupportArmor;
            }
            catch
            {
            }

            try
            {
                ped.IsInvincible = true;
            }
            catch
            {
            }

            try
            {
                ped.CanRagdoll = false;
            }
            catch
            {
            }

            try
            {
                ped.Weapons.Give(
                    WeaponHash.HeavyRifle,
                    SupportWeaponAmmo,
                    true,
                    true);
                ped.Weapons.Select(WeaponHash.HeavyRifle);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "GANG_SUPPORT_WEAPON_ERROR", ex);
            }

            try
            {
                ped.Accuracy = 100;
            }
            catch
            {
            }

            try
            {
                ped.IsPersistent = true;
            }
            catch
            {
            }
        }

        private static bool IsSafeExternalThreat(
            Ped player,
            Ped threat,
            LspdGangProfileCore profile)
        {
            if (player == null || !player.Exists() ||
                threat == null || !threat.Exists() || threat.IsDead)
                return false;

            if (threat.Handle == player.Handle)
                return false;

            if (profile != null &&
                profile.IsPlayerGangMember(threat.Model.Hash))
                return false;

            if (LspdGangIdentityContext.IsPlayerGangMemberModel(threat.Model.Hash))
                return false;

            if (profile != null)
            {
                AnyiLSPDPoliceData.GangProfile gang =
                    profile.FindGangForModel(threat.Model.Hash);

                if (gang != null &&
                    profile.IsPlayerOwnedGangName(gang.Name))
                    return false;
            }

            return true;
        }

        private static void ClearSelfCombatIfNecessary(
            Ped support,
            Ped player,
            LspdGangProfileCore profile)
        {
            if (support == null || !support.Exists() ||
                player == null || !player.Exists())
                return;

            try
            {
                if (support.IsInCombatAgainst(player))
                {
                    support.Task.ClearAll();
                    LspdResponseLog.Write(
                        "GANG_SUPPORT_SELF_ATTACK_BLOCK",
                        "Cleared support combat against Anyi" +
                        " | Support=" + support.Handle +
                        " | Player=" + player.Handle);
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "GANG_SUPPORT_SELF_ATTACK_GUARD_ERROR", ex);
            }
        }

        private void ClearConflictSupport()
        {
            if (_conflictSupportMembers.Count == 0)
                return;

            foreach (Ped support in _conflictSupportMembers)
            {
                try
                {
                    if (support == null || !support.Exists())
                        continue;

                    // Do not kill the support member. Dismiss the entity
                    // cleanly when the conflict has ended.
                    support.Task.ClearAll();
                    support.IsInvincible = false;
                    support.CanRagdoll = true;
                    support.IsPersistent = false;
                    support.Delete();
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "GANG_SUPPORT_DESPAWN_ERROR", ex);
                }
            }

            LspdResponseLog.Write(
                "GANG_SUPPORT_DESPAWN",
                "Gang conflict ended. Hardened support members were dismissed naturally.");

            _conflictSupportMembers.Clear();
            _taskCooldowns.Clear();
            _nextSupportMaintenance = DateTime.MinValue;
        }

        private void CleanupDeadSupportReferences()
        {
            for (int i = _conflictSupportMembers.Count - 1;
                 i >= 0;
                 i--)
            {
                Ped ped = _conflictSupportMembers[i];

                if (ped == null ||
                    !ped.Exists() ||
                    ped.IsDead)
                {
                    _conflictSupportMembers.RemoveAt(i);
                }
            }
        }

        private bool IsPlayerGangMember(
            Ped ped,
            LspdGangProfileCore profile)
        {
            if (ped == null)
                return false;

            if (_preferredPlayerGangMemberHashes.Count > 0)
                return _preferredPlayerGangMemberHashes.Contains(
                    ped.Model.Hash);

            return profile != null &&
                   profile.IsPlayerGangMember(ped.Model.Hash);
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

        private static bool ContainsHandle(
            List<Ped> peds,
            Ped candidate)
        {
            foreach (Ped ped in peds)
            {
                if (ped != null &&
                    candidate != null &&
                    ped.Handle == candidate.Handle)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsValidCandidate(
            Ped ped,
            Ped player,
            int radius)
        {
            return ped != null &&
                   ped.Exists() &&
                   !ped.IsDead &&
                   ped.IsHuman &&
                   player != null &&
                   player.Exists() &&
                   ped.Handle != player.Handle &&
                   ped.Position.DistanceTo(
                       player.Position) <= radius;
        }
    }
}

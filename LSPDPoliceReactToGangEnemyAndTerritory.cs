using GTA;
using System;
using System.Collections.Generic;
using System.Drawing;
using GTA.Native;

namespace AnyiLSPD
{
    public sealed class LspdPoliceReactToGangEnemyAndTerritory
    {
        private readonly Dictionary<int, DateTime> _lastEnemyMark =
            new Dictionary<int, DateTime>();

        public Ped FindEnemyAttacker(
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
                    if (ped == null || !ped.Exists() || ped.IsDead || !ped.IsHuman)
                        continue;
                    if (ped.Handle == player.Handle ||
                        ped.Position.DistanceTo(player.Position) > radius)
                        continue;

                    if (LspdGangIdentityContext.IsPlayerGangMemberModel(ped.Model.Hash))
                        continue;

                    if (IsPolicePed(ped))
                        continue;

                    AnyiLSPDPoliceData.GangProfile gang =
                        profile.FindGangForModel(ped.Model.Hash);

                    if (gang != null &&
                        (LspdGangIdentityContext.IsPlayerGangName(gang.Name) ||
                         profile.IsPlayerOwnedGangName(gang.Name)))
                    {
                        continue;
                    }

                    if (ped.IsInCombatAgainst(player) ||
                        (ped.IsShooting && ped.HasClearLineOfSightTo(player)))
                    {
                        enemyGang = gang;

                        LspdResponseLog.Write(
                            "GANG_ENEMY",
                            (gang == null
                                ? "Non-Gang NPC attacker detected"
                                : "Enemy gang attacker detected") +
                            " | Ped=" + ped.Handle +
                            " | Model=" + ped.Model.Hash);

                        return ped;
                    }
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("GANG_ENEMY_CHECK_ERROR", ex);
                }
            }

            return null;
        }

        public bool MarkEnemyObservation(Ped enemy, DateTime now)
        {
            if (enemy == null || !enemy.Exists())
                return false;

            DateTime last;
            if (_lastEnemyMark.TryGetValue(enemy.Handle, out last) &&
                now < last.AddSeconds(8))
            {
                return false;
            }

            _lastEnemyMark[enemy.Handle] = now;
            LspdResponseLog.Write(
                "GANG_ENEMY",
                "Enemy gang threat observed | Ped=" + enemy.Handle +
                " | Model=" + enemy.Model.Hash);
            return true;
        }

        public void Reset()
        {
            _lastEnemyMark.Clear();
        }

        private static bool IsPolicePed(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return false;

            int hash = ped.Model.Hash;
            int[] hashes =
            {
                unchecked((int)StringHash.AtStringHash("s_m_y_cop_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_f_y_cop_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_m_y_sheriff_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_f_y_sheriff_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_m_y_hwaycop_01", 0)),
                unchecked((int)StringHash.AtStringHash("s_m_y_swat_01", 0))
            };

            foreach (int policeHash in hashes)
            {
                if (hash == policeHash)
                    return true;
            }

            return ped.IsInPoliceVehicle;
        }
    }
}

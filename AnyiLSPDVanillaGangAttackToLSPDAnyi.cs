using GTA;
using GTA.Native;
using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDVanillaGangAttackToLSPDAnyi
    {
        private readonly HashSet<int> _vanillaGangHashes = new HashSet<int>();

        public AnyiLSPDVanillaGangAttackToLSPDAnyi()
        {
            string[] names =
            {
                "g_m_y_ballaeast_01", "g_m_y_ballaorig_01", "g_m_y_ballasout_01",
                "g_m_y_famca_01", "g_m_y_famdnf_01", "g_m_y_famfor_01",
                "g_m_y_mexgang_01", "g_m_y_mexgoon_01", "g_m_y_mexgoon_02",
                "g_m_y_lost_01", "g_m_y_lost_02", "g_m_y_lost_03",
                "g_m_y_vagos_01", "g_m_y_salvaboss_01", "g_m_y_salvagoon_01"
            };
            foreach (string name in names)
                _vanillaGangHashes.Add(unchecked((int)StringHash.AtStringHash(name, 0)));
        }

        public bool TryFindVanillaGangAttack(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot data, out Ped attacker)
        {
            attacker = null;
            if (player == null || nearby == null)
                return false;

            foreach (Ped ped in nearby)
            {
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead || ped.Handle == player.Handle)
                        continue;
                    if (!_vanillaGangHashes.Contains(ped.Model.Hash))
                        continue;
                    if (data != null && data.IsKnownGangMember(ped.Model.Hash))
                        continue;
                    if (!ped.IsInCombatAgainst(player) && !ped.IsShooting)
                        continue;
                    attacker = ped;
                    return true;
                }
                catch { }
            }
            return false;
        }
    }
}

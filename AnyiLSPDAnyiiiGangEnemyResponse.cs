using GTA;
using System;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDAnyiiiGangEnemyResponse
    {
        public bool TryFindEnemyAttackingPlayer(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot data, out Ped attacker, out string gangName)
        {
            attacker = null;
            gangName = "none";
            if (player == null || nearby == null || data == null)
                return false;

            foreach (Ped ped in nearby)
            {
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead || ped.Handle == player.Handle)
                        continue;
                    if (!ped.IsInCombatAgainst(player) && !ped.IsShooting)
                        continue;

                    AnyiLSPDPoliceData.GangProfile gang = data.FindGangForModel(ped.Model.Hash);
                    if (gang == null || gang.PlayerOwned)
                        continue;

                    attacker = ped;
                    gangName = gang.Name;
                    return true;
                }
                catch { }
            }
            return false;
        }
    }
}

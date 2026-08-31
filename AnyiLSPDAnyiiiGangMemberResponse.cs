using GTA;
using System;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDAnyiiiGangMemberResponse
    {
        public bool TryFindMemberUnderAttack(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot data, out Ped protectedMember, out Ped attacker, out string gangName)
        {
            protectedMember = null;
            attacker = null;
            gangName = "none";
            if (player == null || nearby == null || data == null || data.PlayerGang == null)
                return false;

            foreach (Ped member in nearby)
            {
                try
                {
                    if (member == null || !member.Exists() || member.IsDead || member.Handle == player.Handle)
                        continue;
                    if (!data.PlayerGang.MemberHashes.Contains(member.Model.Hash))
                        continue;

                    foreach (Ped enemy in nearby)
                    {
                        if (enemy == null || !enemy.Exists() || enemy.IsDead || enemy.Handle == member.Handle)
                            continue;
                        if (!enemy.IsInCombatAgainst(member) && !member.IsInCombatAgainst(enemy))
                            continue;

                        AnyiLSPDPoliceData.GangProfile enemyGang = data.FindGangForModel(enemy.Model.Hash);
                        if (enemyGang == null || enemyGang.PlayerOwned)
                            continue;

                        protectedMember = member;
                        attacker = enemy;
                        gangName = enemyGang.Name;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }
    }
}

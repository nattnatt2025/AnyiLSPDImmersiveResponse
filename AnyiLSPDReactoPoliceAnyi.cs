using GTA;
using GTA.Native;
using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDReactoPoliceAnyi
    {
        private readonly Dictionary<int, DateTime> _processed = new Dictionary<int, DateTime>();

        public void Update(Ped player, Ped[] nearby, AnyiLSPDPoliceConfig config)
        {
            if (!config.EnablePoliceOfficerReaction || player == null || nearby == null)
                return;

            DateTime now = DateTime.UtcNow;
            int affected = 0;
            foreach (Ped officer in nearby)
            {
                if (affected >= 3) break;
                if (officer == null || !officer.Exists() || officer.IsDead || officer.Handle == player.Handle)
                    continue;
                if (!IsPolice(officer))
                    continue;
                if (officer.Position.DistanceTo(player.Position) > 30f)
                    continue;

                DateTime next;
                if (_processed.TryGetValue(officer.Handle, out next) && now < next)
                    continue;

                try
                {
                    if (officer.IsInCombatAgainst(player) || officer.IsFleeing)
                    {
                        officer.Task.ClearAll();
                    }
                    officer.Task.LookAt(player, 1800);
                    _processed[officer.Handle] = now.AddSeconds(config.PoliceReactionCooldownSeconds);
                    affected++;
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("POLICE_OFFICER_REACTION_ERROR", ex);
                }
            }

            if (_processed.Count > 256)
                Prune(now);
        }

        public void Reset()
        {
            _processed.Clear();
        }

        private void Prune(DateTime now)
        {
            List<int> expired = new List<int>();
            foreach (KeyValuePair<int, DateTime> pair in _processed)
                if (pair.Value <= now) expired.Add(pair.Key);
            foreach (int handle in expired) _processed.Remove(handle);
        }

        private static bool IsPolice(Ped ped)
        {
            return ped.IsInPoliceVehicle ||
                   ped.Model.Hash == unchecked((int)StringHash.AtStringHash("s_m_y_cop_01", 0)) ||
                   ped.Model.Hash == unchecked((int)StringHash.AtStringHash("s_f_y_cop_01", 0)) ||
                   ped.Model.Hash == unchecked((int)StringHash.AtStringHash("s_m_y_sheriff_01", 0)) ||
                   ped.Model.Hash == unchecked((int)StringHash.AtStringHash("s_f_y_sheriff_01", 0)) ||
                   ped.Model.Hash == unchecked((int)StringHash.AtStringHash("s_m_y_hwaycop_01", 0)) ||
                   ped.Model.Hash == unchecked((int)StringHash.AtStringHash("s_m_y_swat_01", 0)) ||
                   ped.Model.Hash == unchecked((int)StringHash.AtStringHash("s_m_m_fiboffice_01", 0));
        }
    }
}

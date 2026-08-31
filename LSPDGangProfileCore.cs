using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    public sealed class LspdGangProfileCore
    {
        private readonly AnyiLSPDPoliceData.GangSnapshot _snapshot;

        public LspdGangProfileCore(AnyiLSPDPoliceData.GangSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public AnyiLSPDPoliceData.GangProfile PlayerGang
        {
            get { return _snapshot == null ? null : _snapshot.PlayerGang; }
        }

        public string PlayerGangName
        {
            get { return PlayerGang == null ? "none" : PlayerGang.Name; }
        }

        public bool IsPlayerOwnedGangName(string name)
        {
            return PlayerGang != null &&
                   string.Equals(PlayerGang.Name, name, StringComparison.OrdinalIgnoreCase);
        }

        public AnyiLSPDPoliceData.GangProfile FindGangForModel(int modelHash)
        {
            return _snapshot == null ? null : _snapshot.FindGangForModel(modelHash);
        }

        public bool IsPlayerGangMember(int modelHash)
        {
            return PlayerGang != null && PlayerGang.MemberHashes.Contains(modelHash);
        }

        public bool IsEnemyGangMember(int modelHash)
        {
            AnyiLSPDPoliceData.GangProfile gang = FindGangForModel(modelHash);
            return gang != null && !IsPlayerOwnedGangName(gang.Name);
        }

        public IEnumerable<AnyiLSPDPoliceData.GangProfile> AllGangs
        {
            get { return _snapshot == null ? new List<AnyiLSPDPoliceData.GangProfile>() : _snapshot.Gangs; }
        }
    }
}

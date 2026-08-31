using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    // Runtime identity bridge for the currently selected/player-owned Gang & Turf gang.
    // It prefers the configured gang name (Anyiii's Gang) and otherwise chooses the
    // player-owned gang with the largest real memberVariations set. It never spawns or
    // modifies Gang & Turf data.
    public static class LspdGangIdentityContext
    {
        private static readonly HashSet<int> PlayerMemberHashes = new HashSet<int>();
        private static readonly HashSet<string> PlayerGangNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static string PlayerGangName { get; private set; } = "none";

        public static void Configure(AnyiLSPDPoliceData.GangSnapshot snapshot, string preferredGangName)
        {
            PlayerMemberHashes.Clear();
            PlayerGangNames.Clear();
            PlayerGangName = "none";

            if (snapshot == null || snapshot.Gangs == null)
                return;

            AnyiLSPDPoliceData.GangProfile selected = null;

            if (!string.IsNullOrWhiteSpace(preferredGangName))
            {
                foreach (AnyiLSPDPoliceData.GangProfile gang in snapshot.Gangs)
                {
                    if (gang == null) continue;
                    if (!string.Equals(gang.Name, preferredGangName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (gang.PlayerOwned)
                    {
                        selected = gang;
                        break;
                    }
                }
            }

            // If the preferred name is absent, choose the player-owned gang
            // with the most real model hashes. This avoids the old FirstOrDefault
            // problem when multiple gangs are marked isPlayerOwned=true.
            if (selected == null)
            {
                int bestCount = -1;
                foreach (AnyiLSPDPoliceData.GangProfile gang in snapshot.Gangs)
                {
                    if (gang == null || !gang.PlayerOwned) continue;
                    int count = gang.MemberHashes == null ? 0 : gang.MemberHashes.Count;
                    if (count > bestCount)
                    {
                        bestCount = count;
                        selected = gang;
                    }
                }
            }

            if (selected == null)
                return;

            PlayerGangName = string.IsNullOrWhiteSpace(selected.Name) ? "none" : selected.Name.Trim();
            PlayerGangNames.Add(PlayerGangName);
            if (selected.MemberHashes != null)
            {
                foreach (int hash in selected.MemberHashes)
                    PlayerMemberHashes.Add(hash);
            }

            LspdResponseLog.Write(
                "GANG_IDENTITY",
                "Resolved player gang=" + PlayerGangName +
                " | MemberModelHashes=" + PlayerMemberHashes.Count +
                " | Preferred=" + (preferredGangName ?? "none"));
        }

        public static bool IsPlayerGangMemberModel(int modelHash)
        {
            return PlayerMemberHashes.Contains(modelHash);
        }

        public static bool IsPlayerGangName(string gangName)
        {
            if (string.IsNullOrWhiteSpace(gangName))
                return false;
            return PlayerGangNames.Contains(gangName.Trim());
        }

        public static int MemberHashCount
        {
            get { return PlayerMemberHashes.Count; }
        }

        public static void Reset()
        {
            PlayerMemberHashes.Clear();
            PlayerGangNames.Clear();
            PlayerGangName = "none";
        }
    }
}

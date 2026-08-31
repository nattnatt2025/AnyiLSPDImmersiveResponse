using GTA;
using System;

namespace AnyiLSPD
{
    // Finds only immediate, local threats. It does not scan the city,
    // change gang data, or create gang peds.
    public sealed class LSPDCitizenReactFromGangAndViolentNPC
    {
        public Ped FindImmediateThreat(
            LspdCitizenSnapshot snapshot,
            LspdCitizenConfig config,
            LspdGangTurfContext gangContext,
            out bool knownGangMember)
        {
            knownGangMember = false;
            if (snapshot == null || snapshot.Player == null ||
                snapshot.NearbyPeds == null)
            {
                return null;
            }

            Ped fallbackShooter = null;

            foreach (Ped ped in snapshot.NearbyPeds)
            {
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead ||
                        ped.Handle == snapshot.Player.Handle || !ped.IsHuman)
                    {
                        continue;
                    }

                    if (ped.Position.DistanceTo(snapshot.Player.Position) >
                        config.ThreatRadius)
                    {
                        continue;
                    }

                    bool directlyAttacking = ped.IsInCombatAgainst(snapshot.Player);
                    bool damagedPlayer = snapshot.HealthDropped &&
                                         snapshot.Player.HasBeenDamagedBy(ped);

                    if (directlyAttacking || damagedPlayer)
                    {
                        knownGangMember = gangContext != null &&
                                            gangContext.IsKnownGangMemberModel(
                                                ped.Model.Hash);
                        return ped;
                    }

                    if (ped.IsShooting &&
                        ped.HasClearLineOfSightTo(snapshot.Player))
                    {
                        fallbackShooter = ped;
                    }
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("THREAT_CHECK_ERROR", ex);
                }
            }

            if (fallbackShooter != null)
            {
                knownGangMember = gangContext != null &&
                                    gangContext.IsKnownGangMemberModel(
                                        fallbackShooter.Model.Hash);
            }

            return fallbackShooter;
        }

        public static string DescribeThreat(Ped ped, bool knownGangMember)
        {
            if (ped == null)
                return "No immediate threat";

            return knownGangMember
                ? "Known Gang & Turf member threat"
                : "Violent nearby NPC threat";
        }
    }
}

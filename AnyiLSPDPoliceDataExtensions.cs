using System;

namespace AnyiLSPD
{
    public static class AnyiLSPDPoliceDataExtensions
    {
        public static AnyiLSPDPoliceData.TurfZone GetNearestTurf(
            this AnyiLSPDPoliceData.GangSnapshot snapshot,
            float x,
            float y,
            float z,
            float defaultRadius)
        {
            if (snapshot == null || snapshot.TurfZones == null)
                return null;

            AnyiLSPDPoliceData.TurfZone best = null;
            float bestDistance = float.MaxValue;

            foreach (AnyiLSPDPoliceData.TurfZone zone in snapshot.TurfZones)
            {
                if (zone == null)
                    continue;

                float radius = zone.HasCustomRadius && zone.Radius > 0f
                    ? zone.Radius
                    : defaultRadius;

                float dx = x - zone.X;
                float dy = y - zone.Y;
                float dz = z - zone.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 <= radius * radius && d2 < bestDistance)
                {
                    best = zone;
                    bestDistance = d2;
                }
            }

            return best;
        }

        // Davis/Anyi-focused helper. It is intentionally conservative: it
        // only returns the named turf when the player is actually within its
        // configured/expanded radius; it does not invent ownership.
        public static AnyiLSPDPoliceData.TurfZone GetPreferredTurf(
            this AnyiLSPDPoliceData.GangSnapshot snapshot,
            float x,
            float y,
            float z,
            string preferredName,
            float defaultRadius)
        {
            if (snapshot == null || snapshot.TurfZones == null ||
                string.IsNullOrWhiteSpace(preferredName))
                return null;

            AnyiLSPDPoliceData.TurfZone best = null;
            float bestDistance = float.MaxValue;

            foreach (AnyiLSPDPoliceData.TurfZone zone in snapshot.TurfZones)
            {
                if (zone == null ||
                    !string.Equals(zone.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                    continue;

                float radius = zone.HasCustomRadius && zone.Radius > 0f
                    ? zone.Radius
                    : defaultRadius;

                float dx = x - zone.X;
                float dy = y - zone.Y;
                float dz = z - zone.Z;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 <= radius * radius && d2 < bestDistance)
                {
                    best = zone;
                    bestDistance = d2;
                }
            }

            return best;
        }
    }
}

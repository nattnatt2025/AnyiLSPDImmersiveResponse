using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AnyiLSPD
{
    public static class AnyiLSPDPoliceData
    {
        public sealed class GangProfile
        {
            public string Name;
            public bool PlayerOwned;
            public int BlipColor;
            public HashSet<int> MemberHashes = new HashSet<int>();
            public HashSet<int> VehicleHashes = new HashSet<int>();
            public HashSet<string> Weapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public sealed class TurfZone
        {
            public string Name;
            public string OwnerGang;
            public float X, Y, Z, Radius;
            public bool HasCustomRadius;
        }

        public sealed class GangSnapshot
        {
            public bool GangFileFound;
            public bool MemberPoolFound;
            public bool TurfFileFound;
            public List<GangProfile> Gangs = new List<GangProfile>();
            public List<int> MemberPoolHashes = new List<int>();
            public List<TurfZone> TurfZones = new List<TurfZone>();

            public GangProfile FindGangForModel(int modelHash)
            {
                return Gangs.FirstOrDefault(g => g.MemberHashes.Contains(modelHash));
            }

            public bool IsKnownGangMember(int modelHash)
            {
                return Gangs.Any(g => g.MemberHashes.Contains(modelHash));
            }

            public bool IsMemberPoolModel(int modelHash)
            {
                return MemberPoolHashes.Contains(modelHash);
            }

            public string DescribePedGangContext(int modelHash)
            {
                GangProfile gang = FindGangForModel(modelHash);
                if (gang != null)
                    return gang.Name;
                return IsMemberPoolModel(modelHash) ? "MemberPool:Unassigned" : "none";
            }

            public GangProfile FindGangForVehicle(int vehicleHash)
            {
                return Gangs.FirstOrDefault(g => g.VehicleHashes.Contains(vehicleHash));
            }

            public string FindGangNameForVehicle(int vehicleHash)
            {
                GangProfile profile = FindGangForVehicle(vehicleHash);
                return profile == null ? "none" : profile.Name;
            }

            public GangProfile PlayerGang => Gangs.FirstOrDefault(g => g.PlayerOwned);

            public string GetTerritoryOwner(float x, float y, float z)
            {
                TurfZone best = null;
                float bestDistance = float.MaxValue;
                foreach (TurfZone zt in TurfZones)
                {
                    float radius = zt.HasCustomRadius ? zt.Radius : 100f;
                    float dx = x - zt.X, dy = y - zt.Y, dz = z - zt.Z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 <= radius * radius && d2 < bestDistance)
                    {
                        best = zt;
                        bestDistance = d2;
                    }
                }
                return best == null ? "none" : best.OwnerGang;
            }


            public TurfZone GetNearestTurf(float x, float y, float z, float maxRadius)
            {
                TurfZone best = null;
                float bestDistance = float.MaxValue;
                foreach (TurfZone zone in TurfZones)
                {
                    float radius = zone.HasCustomRadius ? zone.Radius : 100f;
                    if (radius <= 0f) radius = 100f;
                    radius = Math.Min(radius, maxRadius);
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

        public static GangSnapshot LoadGangSnapshot(string gameDataRoot, Action<string> log)
        {
            var snapshot = new GangSnapshot();
            string gangFile = Path.Combine(gameDataRoot, "GangData.xml");
            string memberPool = Path.Combine(gameDataRoot, "MemberPool.xml");
            string turf = Path.Combine(gameDataRoot, "TurfZoneData.xml");

            snapshot.GangFileFound = File.Exists(gangFile);
            snapshot.MemberPoolFound = File.Exists(memberPool);
            snapshot.TurfFileFound = File.Exists(turf);

            log?.Invoke("GANG_DATA | Root=" + gameDataRoot + " | GangFile=" + snapshot.GangFileFound + " | MemberPool=" + snapshot.MemberPoolFound + " | Turf=" + snapshot.TurfFileFound);

            if (snapshot.GangFileFound) LoadGangs(gangFile, snapshot, log);
            if (snapshot.MemberPoolFound) LoadMemberPool(memberPool, snapshot, log);
            if (snapshot.TurfFileFound) LoadTurf(turf, snapshot, log);

            log?.Invoke("GANG_DATA | Gangs=" + snapshot.Gangs.Count + " | MemberPoolEntries=" + snapshot.MemberPoolHashes.Count + " | TurfZones=" + snapshot.TurfZones.Count + " | PlayerGang=" + (snapshot.PlayerGang == null ? "NONE" : snapshot.PlayerGang.Name));
            return snapshot;
        }

        private static void LoadGangs(string path, GangSnapshot snapshot, Action<string> log)
        {
            try
            {
                XDocument doc = XDocument.Load(path, LoadOptions.None);
                XElement gangs = doc.Root == null ? null : doc.Root.Element("gangs");
                if (gangs == null) return;

                foreach (XElement g in gangs.Elements("Gang"))
                {
                    var profile = new GangProfile
                    {
                        Name = (string)g.Element("name") ?? "Unnamed",
                        PlayerOwned = ReadBool(g, "isPlayerOwned", false),
                        BlipColor = ReadInt(g, "blipColor", 0)
                    };
                    foreach (XElement m in g.Descendants("memberVariations").Elements("PotentialGangMember"))
                    {
                        int hash;
                        if (int.TryParse((string)m.Element("modelHash"), NumberStyles.Integer, CultureInfo.InvariantCulture, out hash) && hash != 0)
                            profile.MemberHashes.Add(hash);
                    }
                    foreach (XElement v in g.Descendants("carVariations").Elements("PotentialGangVehicle"))
                    {
                        int hash;
                        if (int.TryParse((string)v.Element("modelHash"), NumberStyles.Integer, CultureInfo.InvariantCulture, out hash) && hash != 0)
                            profile.VehicleHashes.Add(hash);
                    }
                    foreach (XElement w in g.Element("gangWeaponHashes") == null ? Enumerable.Empty<XElement>() : g.Element("gangWeaponHashes").Elements("WeaponHash"))
                        profile.Weapons.Add((string)w ?? string.Empty);
                    snapshot.Gangs.Add(profile);
                }
            }
            catch (Exception ex) { log?.Invoke("GANG_DATA_ERROR | GangData | " + ex.GetType().Name + " | " + ex.Message); }
        }

        private static void LoadMemberPool(string path, GangSnapshot snapshot, Action<string> log)
        {
            try
            {
                XDocument doc = XDocument.Load(path, LoadOptions.None);
                XElement list = doc.Root == null ? null : doc.Root.Element("memberList");
                if (list == null) return;
                foreach (XElement m in list.Elements("PotentialGangMember"))
                {
                    int hash;
                    if (int.TryParse((string)m.Element("modelHash"), NumberStyles.Integer, CultureInfo.InvariantCulture, out hash) && hash != 0)
                        snapshot.MemberPoolHashes.Add(hash);
                }
            }
            catch (Exception ex) { log?.Invoke("GANG_DATA_ERROR | MemberPool | " + ex.GetType().Name + " | " + ex.Message); }
        }

        private static void LoadTurf(string path, GangSnapshot snapshot, Action<string> log)
        {
            try
            {
                XDocument doc = XDocument.Load(path, LoadOptions.None);
                XElement list = doc.Root == null ? null : doc.Root.Element("zoneList");
                if (list == null) return;
                foreach (XElement z in list.Elements("TurfZone"))
                {
                    XElement p = z.Element("zoneBlipPosition");
                    if (p == null) continue;
                    snapshot.TurfZones.Add(new TurfZone
                    {
                        Name = (string)z.Element("zoneName") ?? "Unknown",
                        OwnerGang = (string)z.Element("ownerGangName") ?? "none",
                        X = ReadFloat(p, "X", 0f),
                        Y = ReadFloat(p, "Y", 0f),
                        Z = ReadFloat(p, "Z", 0f),
                        Radius = ReadFloat(z, "areaRadius", 0f),
                        HasCustomRadius = z.Element("areaRadius") != null
                    });
                }
            }
            catch (Exception ex) { log?.Invoke("GANG_DATA_ERROR | TurfZoneData | " + ex.GetType().Name + " | " + ex.Message); }
        }

        private static bool ReadBool(XElement e, string n, bool f) { bool v; return bool.TryParse((string)e.Element(n), out v) ? v : f; }
        private static int ReadInt(XElement e, string n, int f) { int v; return int.TryParse((string)e.Element(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : f; }
        private static float ReadFloat(XElement e, string n, float f) { float v; return float.TryParse((string)e.Element(n), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : f; }
    }
}

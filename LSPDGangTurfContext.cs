using GTA.Math;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace AnyiLSPD
{
    // Read-only snapshot of the existing Gang & Turf mod data.
    // This code never modifies GangData.xml, TurfZoneData.xml, or MemberPool.xml.
    public sealed class LspdGangTurfContext
    {
        private readonly HashSet<int> _knownMemberModels =
            new HashSet<int>();
        private readonly List<LspdTurfZone> _zones =
            new List<LspdTurfZone>();

        public string PlayerGangName { get; private set; }
        public int KnownMemberModelCount
        {
            get { return _knownMemberModels.Count; }
        }

        public int TurfZoneCount
        {
            get { return _zones.Count; }
        }

        public void Load(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                LspdResponseLog.Write(
                    "GANG_CONTEXT_WARNING",
                    "Gang data folder unavailable: " + root);
                return;
            }

            HashSet<int> memberModels = new HashSet<int>();
            List<LspdTurfZone> zones = new List<LspdTurfZone>();
            string playerGang = null;

            try
            {
                string gangDataPath = Path.Combine(root, "GangData.xml");
                if (File.Exists(gangDataPath))
                {
                    XDocument gangData = XDocument.Load(gangDataPath);
                    foreach (XElement gang in gangData.Descendants("Gang"))
                    {
                        bool playerOwned = ReadBool(gang.Element("isPlayerOwned"));
                        if (playerOwned)
                        {
                            XElement name = gang.Element("name");
                            if (name != null)
                                playerGang = name.Value.Trim();
                            break;
                        }
                    }
                }

                string memberPoolPath = Path.Combine(root, "MemberPool.xml");
                if (File.Exists(memberPoolPath))
                {
                    XDocument members = XDocument.Load(memberPoolPath);
                    foreach (XElement member in members.Descendants("PotentialGangMember"))
                    {
                        int hash;
                        XElement modelHash = member.Element("modelHash");
                        if (modelHash != null &&
                            int.TryParse(
                                modelHash.Value,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out hash))
                        {
                            memberModels.Add(hash);
                        }
                    }
                }

                string turfPath = Path.Combine(root, "TurfZoneData.xml");
                if (File.Exists(turfPath))
                {
                    XDocument turfData = XDocument.Load(turfPath);
                    foreach (XElement zone in turfData.Descendants("TurfZone"))
                    {
                        XElement position = zone.Element("zoneBlipPosition");
                        if (position == null)
                            continue;

                        float x;
                        float y;
                        float z;
                        if (!TryReadVector(position, out x, out y, out z))
                            continue;

                        XElement name = zone.Element("zoneName");
                        XElement owner = zone.Element("ownerGangName");
                        XElement radius = zone.Element("areaRadius");

                        zones.Add(new LspdTurfZone
                        {
                            Name = name == null ? "Unnamed Turf" : name.Value.Trim(),
                            OwnerGangName = owner == null ? "none" : owner.Value.Trim(),
                            Position = new Vector3(x, y, z),
                            Radius = radius == null
                                ? 0.0f
                                : ParseFloat(radius.Value, 0.0f)
                        });
                    }
                }

                _knownMemberModels.Clear();
                foreach (int model in memberModels)
                    _knownMemberModels.Add(model);

                _zones.Clear();
                _zones.AddRange(zones);
                PlayerGangName = playerGang;

                LspdResponseLog.Write(
                    "GANG_CONTEXT",
                    "PlayerGang=" + (PlayerGangName ?? "none") +
                    " | MemberModels=" + _knownMemberModels.Count +
                    " | TurfZones=" + _zones.Count);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("GANG_CONTEXT_ERROR", ex);
            }
        }

        public bool IsKnownGangMemberModel(int modelHash)
        {
            return _knownMemberModels.Contains(modelHash);
        }

        public LspdTurfZone FindZone(Vector3 position)
        {
            LspdTurfZone closest = null;
            float closestDistance = float.MaxValue;

            foreach (LspdTurfZone zone in _zones)
            {
                if (zone.Radius <= 0.0f)
                    continue;

                float distance = position.DistanceTo(zone.Position);
                if (distance <= zone.Radius && distance < closestDistance)
                {
                    closest = zone;
                    closestDistance = distance;
                }
            }

            return closest;
        }

        private static bool TryReadVector(
            XElement position,
            out float x,
            out float y,
            out float z)
        {
            x = 0.0f;
            y = 0.0f;
            z = 0.0f;

            XElement xElement = position.Element("X");
            XElement yElement = position.Element("Y");
            XElement zElement = position.Element("Z");
            if (xElement == null || yElement == null || zElement == null)
                return false;

            return float.TryParse(xElement.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                   float.TryParse(yElement.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
                   float.TryParse(zElement.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z);
        }

        private static bool ReadBool(XElement element)
        {
            bool value;
            return element != null && bool.TryParse(element.Value, out value) && value;
        }

        private static float ParseFloat(string text, float fallback)
        {
            float value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }
    }

    public sealed class LspdTurfZone
    {
        public string Name;
        public string OwnerGangName;
        public Vector3 Position;
        public float Radius;
    }
}

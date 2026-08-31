using GTA;
using GTA.Math;
using GTA.Native;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDPoliceStations
    {
        public sealed class Station
        {
            public string Id;
            public string Name;
            public Vector3 Exterior;
            public Vector3 SpawnPosition;
            public float Heading;
            public string InteriorMode;
            public bool VerifiedInterior;
        }

        private readonly Dictionary<string, Station> _stations = new Dictionary<string, Station>(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;
        private readonly List<Blip> _ownedBlips = new List<Blip>();

        public AnyiLSPDPoliceStations(string scriptsDirectory)
        {
            _path = Path.Combine(scriptsDirectory, "AnyiLSPDPoliceStations.xml");
            LoadOrCreate();
        }

        public Station Get(string id)
        {
            Station station;
            return _stations.TryGetValue(id ?? "", out station) ? station : null;
        }

        public Station FindNearest(Vector3 position)
        {
            Station nearest = null;
            float best = float.MaxValue;
            foreach (Station station in _stations.Values)
            {
                float d = station.Exterior.DistanceTo(position);
                if (d < best)
                {
                    best = d;
                    nearest = station;
                }
            }
            return nearest;
        }

        public IEnumerable<Station> All { get { return _stations.Values; } }

        public void EnsureBlips()
        {
            ClearBlips();
            foreach (Station station in _stations.Values)
            {
                try
                {
                    Blip blip = World.CreateBlip(station.Exterior);
                    blip.Sprite = BlipSprite.PoliceStation;
                    blip.Name = station.Name;
                    blip.IsShortRange = false;
                    _ownedBlips.Add(blip);
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("POLICE_STATION_BLIP_ERROR", ex);
                }
            }
            LspdResponseLog.Write("POLICE_STATIONS", "Station blips created=" + _ownedBlips.Count + " | Entries=" + _stations.Count);
        }

        public void ClearBlips()
        {
            foreach (Blip blip in _ownedBlips)
            {
                try
                {
                    if (blip != null && blip.Exists()) blip.Delete();
                }
                catch { }
            }
            _ownedBlips.Clear();
        }

        public bool SetWaypoint(string id)
        {
            Station station = Get(id);
            if (station == null) return false;
            Function.Call(Hash.SET_NEW_WAYPOINT, station.Exterior.X, station.Exterior.Y);
            return true;
        }

        private void LoadOrCreate()
        {
            if (!File.Exists(_path))
            {
                CreateDefault();
                return;
            }

            try
            {
                XDocument doc = XDocument.Load(_path);
                _stations.Clear();
                foreach (XElement node in doc.Root == null ? new XElement[0] : doc.Root.Elements("Station"))
                {
                    Vector3 exterior = ReadVector(node.Element("Exterior"));
                    Vector3 spawn = ReadVector(node.Element("Spawn"));
                    Station station = new Station
                    {
                        Id = (string)node.Attribute("id") ?? "Unknown",
                        Name = (string)node.Attribute("name") ?? "Police Station",
                        Exterior = exterior,
                        SpawnPosition = spawn,
                        Heading = ReadFloat((string)node.Attribute("heading"), 0f),
                        InteriorMode = (string)node.Attribute("interiorMode") ?? "ExteriorSafe",
                        VerifiedInterior = ReadBool((string)node.Attribute("verifiedInterior"), false)
                    };
                    _stations[station.Id] = station;
                }

                if (_stations.Count == 0)
                    CreateDefault();
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_STATION_LOAD_ERROR", ex);
                if (_stations.Count == 0)
                    CreateDefault();
            }
        }

        private void CreateDefault()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            XDocument doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("AnyiLSPDPoliceStations",
                    new XAttribute("version", "3.0"),
                    new XElement("Station",
                        new XAttribute("id", "MissionRow"),
                        new XAttribute("name", "Mission Row Police Station"),
                        new XAttribute("heading", "134.56"),
                        new XAttribute("interiorMode", "VerifiedLobby"),
                        new XAttribute("verifiedInterior", "true"),
                        new XElement("Exterior", new XAttribute("x", "434.7479"), new XAttribute("y", "-983.2151"), new XAttribute("z", "30.83926")),
                        new XElement("Spawn", new XAttribute("x", "432.7781"), new XAttribute("y", "-1020.37"), new XAttribute("z", "28.34021"))),
                    new XElement("Station",
                        new XAttribute("id", "Bolingbroke"),
                        new XAttribute("name", "Bolingbroke Penitentiary"),
                        new XAttribute("heading", "90"),
                        new XAttribute("interiorMode", "CustodyExterior"),
                        new XAttribute("verifiedInterior", "false"),
                        new XElement("Exterior", new XAttribute("x", "1845.0"), new XAttribute("y", "2597.5"), new XAttribute("z", "44.6")),
                        new XElement("Spawn", new XAttribute("x", "1826.0"), new XAttribute("y", "2604.0"), new XAttribute("z", "44.6")))));
            doc.Save(_path);
            LoadOrCreate();
        }

        private static Vector3 ReadVector(XElement node)
        {
            if (node == null) return Vector3.Zero;
            return new Vector3(
                ReadFloat((string)node.Attribute("x"), 0f),
                ReadFloat((string)node.Attribute("y"), 0f),
                ReadFloat((string)node.Attribute("z"), 0f));
        }

        private static float ReadFloat(string value, float fallback)
        {
            float result;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : fallback;
        }

        private static bool ReadBool(string value, bool fallback)
        {
            bool result;
            return bool.TryParse(value, out result) ? result : fallback;
        }
    }
}

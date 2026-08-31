using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using GTA;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDProfileCore
    {
        public sealed class PoliceProfile
        {
            public string Id;
            public string Department;
            public string OfficerModel;
            public string VehicleModel;
            public string ResponseOfficerModel;
            public string ResponseVehicleModel;
            public string TransportVehicleModel;
            public string StationId;
            public bool EmergencyLights;
            public bool NativeSiren;
            public string RadioStation;
            public string Description;
        }

        private readonly string _scriptDirectory;
        private readonly AnyiLSPDPoliceConfig _config;
        private readonly Dictionary<string, PoliceProfile> _profiles = new Dictionary<string, PoliceProfile>(StringComparer.OrdinalIgnoreCase);
        private PoliceProfile _current;

        public PoliceProfile Current { get { return _current; } }
        public IEnumerable<PoliceProfile> All { get { return _profiles.Values; } }

        public AnyiLSPDProfileCore(string scriptsDirectory, AnyiLSPDPoliceConfig config)
        {
            _scriptDirectory = scriptsDirectory;
            _config = config;
            Reload(config);
        }

        public void Reload(AnyiLSPDPoliceConfig config)
        {
            _profiles.Clear();
            LoadXml();
            if (_profiles.Count == 0)
                CreateFallbacks();

            string desired = config == null ? "LSPD" : config.ActiveProfileId;
            if (!Select(desired))
                Select("LSPD");

            if (_current != null && config != null)
            {
                if (!string.IsNullOrWhiteSpace(config.OfficerModel))
                    _current.OfficerModel = config.OfficerModel;
                if (!string.IsNullOrWhiteSpace(config.VehicleModel))
                    _current.VehicleModel = config.VehicleModel;
                if (!string.IsNullOrWhiteSpace(config.TransportVehicleModel))
                    _current.TransportVehicleModel = config.TransportVehicleModel;
                if (!string.IsNullOrWhiteSpace(config.SelectedStationId))
                    _current.StationId = config.SelectedStationId;
            }
        }

        public bool Select(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            PoliceProfile profile;
            if (!_profiles.TryGetValue(id.Trim(), out profile))
                return false;
            _current = profile;
            _config.ActiveProfileId = profile.Id;
            _config.Department = profile.Department;
            _config.OfficerModel = profile.OfficerModel;
            _config.VehicleModel = profile.VehicleModel;
            _config.TransportVehicleModel = profile.TransportVehicleModel;
            // Station is an independent player choice; selecting an agency/profile
            // must not silently reset it to that profile's default station.
            _config.UseNativeSiren = profile.NativeSiren;
            _config.EmergencyLights = profile.EmergencyLights;
            LspdResponseLog.Write("POLICE_PROFILE", "Selected | Id=" + profile.Id + " | Agency=" + profile.Department + " | Ped=" + profile.OfficerModel + " | Vehicle=" + profile.VehicleModel + " | Station=" + profile.StationId + " | NativeSiren=" + profile.NativeSiren);
            return true;
        }

        public bool SelectStation(string stationId)
        {
            if (string.IsNullOrWhiteSpace(stationId) || _current == null)
                return false;
            _current.StationId = stationId.Trim();
            _config.SelectedStationId = _current.StationId;
            LspdResponseLog.Write("POLICE_PROFILE", "Station selected | Profile=" + _current.Id + " | Station=" + _current.StationId);
            return true;
        }

        public bool ApplyOfficerModel(string input)
        {
            if (_current == null) return false;
            Model model;
            if (!TryCreateModel(input, out model) || !model.IsValid || !model.IsPed)
                return false;
            _current.OfficerModel = input.Trim();
            _config.OfficerModel = _current.OfficerModel;
            LspdResponseLog.Write("POLICE_PROFILE", "Officer model selected | " + _current.OfficerModel + " | Hash=" + model.Hash);
            return true;
        }

        public bool ApplyVehicleModel(string input)
        {
            if (_current == null) return false;
            Model model;
            if (!TryCreateModel(input, out model) || !model.IsValid || !model.IsVehicle)
                return false;
            _current.VehicleModel = input.Trim();
            _config.VehicleModel = _current.VehicleModel;
            bool customPolIgnus = string.Equals(_current.VehicleModel, "polignus", StringComparison.OrdinalIgnoreCase);
            if (customPolIgnus)
            {
                _current.NativeSiren = false;
                _current.EmergencyLights = false;
            }
            _config.UseNativeSiren = _current.NativeSiren;
            _config.EmergencyLights = _current.EmergencyLights;
            LspdResponseLog.Write("POLICE_PROFILE", "Police vehicle selected | " + _current.VehicleModel + " | Hash=" + model.Hash + " | NativeSiren=" + _current.NativeSiren);
            return true;
        }

        public bool SetEmergency(bool enabled)
        {
            if (_current == null) return false;
            _current.EmergencyLights = enabled;
            _config.EmergencyLights = enabled;
            return true;
        }

        private void LoadXml()
        {
            string path = Path.Combine(_scriptDirectory, "AnyiLSPDPoliceProfiles.xml");
            if (!File.Exists(path)) return;
            try
            {
                XDocument doc = XDocument.Load(path);
                foreach (XElement p in doc.Root == null ? new XElement[0] : doc.Root.Elements("Profile"))
                {
                    PoliceProfile profile = new PoliceProfile
                    {
                        Id = (string)p.Attribute("id") ?? "LSPD",
                        Department = (string)p.Attribute("department") ?? "LSPD",
                        OfficerModel = (string)p.Element("OfficerModel") ?? "s_m_y_cop_01",
                        VehicleModel = (string)p.Element("VehicleModel") ?? "police",
                        ResponseOfficerModel = (string)p.Element("ResponseOfficerModel") ?? "s_m_y_cop_01",
                        ResponseVehicleModel = (string)p.Element("ResponseVehicleModel") ?? "police",
                        TransportVehicleModel = (string)p.Element("TransportVehicleModel") ?? "stockade",
                        StationId = (string)p.Element("StationId") ?? "MissionRow",
                        EmergencyLights = ReadBool(p, "EmergencyLights", true),
                        NativeSiren = ReadBool(p, "NativeSiren", true),
                        RadioStation = (string)p.Element("RadioStation") ?? "RADIO_01_CLASS_ROCK",
                        Description = (string)p.Element("Description") ?? "Police Authority profile."
                    };
                    if (string.Equals(profile.VehicleModel, "polignus", StringComparison.OrdinalIgnoreCase))
                        profile.NativeSiren = false;
                    _profiles[profile.Id] = profile;
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_PROFILE_LOAD_ERROR", ex);
            }
        }

        private void CreateFallbacks()
        {
            Add("LSPD", "LSPD", "s_m_y_cop_01", "police", "s_m_y_cop_01", "police", "stockade", "MissionRow", true, true, "Los Santos Police Department.");
            Add("LSSD", "LSSD", "s_m_y_sheriff_01", "sheriff", "s_m_y_sheriff_01", "sheriff", "stockade", "SandyShores", true, true, "Los Santos County Sheriff.");
            Add("NOOSE", "NOOSE", "s_m_y_swat_01", "police3", "s_m_y_swat_01", "police3", "stockade", "MissionRow", true, true, "NOOSE tactical authority profile.");
            Add("FIB", "FIB", "s_m_m_fibsec_01", "fbi", "s_m_m_fibsec_01", "fbi", "stockade", "MissionRow", true, true, "Federal investigative profile.");
            Add("PolIgnus", "LSPD Custom", "s_m_y_cop_01", "polignus", "s_m_y_cop_01", "police", "stockade", "MissionRow", true, false, "Custom PolIgnus pursuit profile. Player uses PolIgnus; response units remain standard police.");
        }

        private void Add(string id, string department, string ped, string vehicle, string responsePed, string responseVehicle, string transport, string station, bool lights, bool siren, string description)
        {
            _profiles[id] = new PoliceProfile
            {
                Id = id,
                Department = department,
                OfficerModel = ped,
                VehicleModel = vehicle,
                ResponseOfficerModel = responsePed,
                ResponseVehicleModel = responseVehicle,
                TransportVehicleModel = transport,
                StationId = station,
                EmergencyLights = lights,
                NativeSiren = siren,
                RadioStation = "RADIO_01_CLASS_ROCK",
                Description = description
            };
        }

        private static bool TryCreateModel(string input, out Model model)
        {
            model = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            int hash;
            if (int.TryParse(input.Trim(), out hash))
                model = new Model(hash);
            else
                model = new Model(input.Trim());
            return model != null && model.IsValid;
        }

        private static bool ReadBool(XElement element, string name, bool fallback)
        {
            XAttribute attr = element.Attribute(name);
            if (attr == null) return fallback;
            bool value;
            return bool.TryParse(attr.Value, out value) ? value : fallback;
        }
    }
}

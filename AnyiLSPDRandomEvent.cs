using GTA;
using GTA.Math;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDRandomEvent
    {
        public sealed class EventTemplate
        {
            public AnyiPoliceIncidentType Type;
            public string Title;
            public string Audio;
            public float Severity;
            public string[] Keywords;
        }

        public sealed class ChaosActivityLocation
        {
            public string Name;
            public Vector3 Position;
            public AnyiPoliceIncidentType Type;
            public string Title;
            public string Audio;
            public float Severity;
        }

        private readonly AnyiLSPDPoliceConfig _config;
        private readonly Random _random = new Random();
        private readonly List<ChaosActivityLocation> _activities = new List<ChaosActivityLocation>();
        private readonly Dictionary<string, EventTemplate> _templates = new Dictionary<string, EventTemplate>(StringComparer.OrdinalIgnoreCase);
        private DateTime _nextScan = DateTime.MinValue;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private DateTime _nextFallback = DateTime.MinValue;
        private Vector3 _lastChaosAreaCenter = Vector3.Zero;
        private DateTime _lastChaosAreaCompletedAt = DateTime.MinValue;
        private static readonly TimeSpan ChaosAreaCooldown = TimeSpan.FromMinutes(5);
        private const float ChaosSameAreaRadius = 220f;

        public AnyiLSPDRandomEvent(AnyiLSPDPoliceConfig config)
        {
            _config = config;
            Reload();
        }

        public void Reload()
        {
            _activities.Clear();
            _templates.Clear();
            LoadTemplates();
            LoadChaosActivities();
            ScheduleInitialFallback(DateTime.UtcNow);
            LspdResponseLog.Write(
                "POLICE_ACTIVITY",
                "Reloaded | ChaosActivities=" + _activities.Count +
                " | EventTemplates=" + _templates.Count +
                " | ChaosRoot=" + _config.ChaosActivityRoot);
        }

        public int ChaosActivityCount { get { return _activities.Count; } }
        public int EventTemplateCount { get { return _templates.Count; } }

        public void ForceScan()
        {
            _nextScan = DateTime.MinValue;
            _cooldownUntil = DateTime.MinValue;
            _nextFallback = DateTime.MinValue;
        }

        public void MarkSceneAreaCompleted(Vector3 center)
        {
            if (center == Vector3.Zero)
                return;

            _lastChaosAreaCenter = center;
            _lastChaosAreaCompletedAt = DateTime.UtcNow;
        }

        private bool IsChaosAreaCooling(Vector3 position)
        {
            if (_lastChaosAreaCenter == Vector3.Zero ||
                DateTime.UtcNow - _lastChaosAreaCompletedAt >= ChaosAreaCooldown)
                return false;

            return position.DistanceTo(_lastChaosAreaCenter) <= ChaosSameAreaRadius;
        }

        private ChaosActivityLocation FindVariedChaos(Ped player)
        {
            List<ChaosActivityLocation> candidates = new List<ChaosActivityLocation>();
            foreach (ChaosActivityLocation activity in _activities)
            {
                if (activity == null) continue;
                float distance = activity.Position.DistanceTo(player.Position);
                if (distance > _config.EventOfferRadius) continue;
                if (IsChaosAreaCooling(activity.Position)) continue;
                candidates.Add(activity);
            }

            if (candidates.Count == 0)
                return null;

            candidates.Sort((a, b) => a.Position.DistanceTo(player.Position).CompareTo(b.Position.DistanceTo(player.Position)));
            int count = Math.Min(6, candidates.Count);
            return candidates[_random.Next(count)];
        }

        public AnyiPoliceIncident CreateImmediatePatrolIncident(Ped player, AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            if (player == null || !player.Exists() || !_config.EnableRandomEvents || !_config.EnableOrganicFallbackEvents)
                return null;

            AnyiPoliceIncident incident = BuildFallbackIncident(player, gangData);
            SetOfferCooldown(DateTime.UtcNow);
            ScheduleFallback(DateTime.UtcNow);
            return incident;
        }

        public AnyiPoliceIncident CreateImmediateChaosIncident(Ped player, AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            if (player == null || !player.Exists() || !_config.EnableChaosGangActivities)
                return null;

            ChaosActivityLocation nearest = FindVariedChaos(player);
            if (nearest == null)
                return null;

            SetOfferCooldown(DateTime.UtcNow);
            return BuildIncident(
                nearest.Type,
                nearest.Title,
                nearest.Position,
                nearest.Severity,
                nearest.Audio,
                nearest.Name,
                true,
                gangData);
        }

        public AnyiPoliceIncident TryDiscover(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            DateTime now = DateTime.UtcNow;
            if (!_config.EnableRandomEvents || player == null || !player.Exists())
                return null;
            if (now < _nextScan || now < _cooldownUntil)
                return null;

            _nextScan = now.AddSeconds(_config.RandomEventCheckSeconds);

            ChaosActivityLocation nearest = _config.PreferChaosActivities ? FindVariedChaos(player) : null;
            if (nearest != null)
            {
                SetOfferCooldown(now);
                return BuildIncident(
                    nearest.Type,
                    nearest.Title,
                    nearest.Position,
                    nearest.Severity,
                    nearest.Audio,
                    nearest.Name,
                    true,
                    gangData);
            }

            AnyiPoliceIncident observed = ObserveNearby(player, nearby, gangData);
            if (observed != null)
            {
                SetOfferCooldown(now);
                return observed;
            }

            if (!_config.PreferChaosActivities)
            {
                nearest = FindNearestChaos(player);
                if (nearest != null)
                {
                    SetOfferCooldown(now);
                    return BuildIncident(
                        nearest.Type,
                        nearest.Title,
                        nearest.Position,
                        nearest.Severity,
                        nearest.Audio,
                        nearest.Name,
                        true,
                        gangData);
                }
            }

            if (_config.EnableOrganicFallbackEvents && now >= _nextFallback && !IsChaosAreaCooling(player.Position))
            {
                AnyiPoliceIncident fallback = BuildFallbackIncident(player, gangData);
                SetOfferCooldown(now);
                ScheduleFallback(now);
                return fallback;
            }

            return null;
        }

        private AnyiPoliceIncident ObserveNearby(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            if (nearby == null) return null;
            Ped dangerous = null;
            Ped fleeing = null;
            Vehicle speedingVehicle = null;
            Ped speedingDriver = null;

            foreach (Ped ped in nearby)
            {
                try
                {
                    if (ped == null || !ped.Exists() || ped.IsDead || !ped.IsHuman || ped.Handle == player.Handle)
                        continue;
                    if (ped.Position.DistanceTo(player.Position) > _config.EventOfferRadius)
                        continue;

                    if (ped.IsShooting || ped.IsInCombatAgainst(player))
                    {
                        dangerous = ped;
                        break;
                    }

                    if (fleeing == null && ped.IsFleeing && !ped.IsInVehicle())
                        fleeing = ped;

                    Vehicle vehicle = ped.CurrentVehicle;
                    if (speedingVehicle == null && vehicle != null && vehicle.Exists() && ped.IsInVehicle())
                    {
                        if (vehicle.Speed >= 28f && vehicle.Position.DistanceTo(player.Position) <= Math.Min(160f, _config.EventOfferRadius))
                        {
                            speedingVehicle = vehicle;
                            speedingDriver = ped;
                        }
                    }
                }
                catch { }
            }

            if (dangerous != null)
            {
                string gang = "none";
                string turf = "none";
                if (gangData != null)
                {
                    gang = gangData.FindGangForModel(dangerous.Model.Hash) == null ? "none" : gangData.FindGangForModel(dangerous.Model.Hash).Name;
                    AnyiLSPDPoliceData.TurfZone zone = gangData.GetNearestTurf(player.Position.X, player.Position.Y, player.Position.Z, 100f);
                    if (zone != null) turf = zone.Name;
                }
                return new AnyiPoliceIncident
                {
                    Type = AnyiPoliceIncidentType.PoliceAssistance,
                    Title = "Officer / Public Assistance Required",
                    Description = "Violent activity observed during patrol.",
                    Origin = dangerous.Position,
                    Severity = gang == "none" ? 3 : 4,
                    GangName = gang,
                    TurfName = turf,
                    Suspect = dangerous,
                    AudioCategory = "ASSISTANCE_REQUIRED",
                    OwnedByDispatch = false
                };
            }

            if (fleeing != null)
            {
                return new AnyiPoliceIncident
                {
                    Type = AnyiPoliceIncidentType.PedestrianPursuit,
                    Title = "Suspect Fleeing On Foot",
                    Description = "A fleeing pedestrian was observed near the patrol route.",
                    Origin = fleeing.Position,
                    Severity = 2,
                    Suspect = fleeing,
                    AudioCategory = "REPORT_SUSPECT_IS_ON_FOOT",
                    OwnedByDispatch = false
                };
            }

            if (speedingDriver != null && speedingVehicle != null)
            {
                return new AnyiPoliceIncident
                {
                    Type = AnyiPoliceIncidentType.RecklessDriver,
                    Title = "Reckless Driver Observed",
                    Description = "A vehicle was observed travelling at an unsafe speed.",
                    Origin = speedingVehicle.Position,
                    Severity = 2,
                    Suspect = speedingDriver,
                    SuspectVehicle = speedingVehicle,
                    AudioCategory = "REQUEST_BACKUP",
                    OwnedByDispatch = false
                };
            }

            return null;
        }

        private ChaosActivityLocation FindNearestChaos(Ped player)
        {
            ChaosActivityLocation nearest = null;
            float best = float.MaxValue;
            foreach (ChaosActivityLocation activity in _activities)
            {
                float d = activity.Position.DistanceTo(player.Position);
                if (d <= _config.EventOfferRadius && d < best)
                {
                    nearest = activity;
                    best = d;
                }
            }
            return nearest;
        }

        private AnyiPoliceIncident BuildFallbackIncident(Ped player, AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            AnyiPoliceIncidentType[] types =
            {
                AnyiPoliceIncidentType.StoreRobbery,
                AnyiPoliceIncidentType.BankHeist,
                AnyiPoliceIncidentType.Kidnapping,
                AnyiPoliceIncidentType.RecklessDriver,
                AnyiPoliceIncidentType.PedestrianPursuit,
                AnyiPoliceIncidentType.VehiclePursuit
            };
            AnyiPoliceIncidentType type = types[_random.Next(types.Length)];
            Vector3 forward = player.Position + player.ForwardVector * _random.Next(70, 140);
            Vector3 origin = forward;
            EventTemplate template = FindTemplate(type);
            return BuildIncident(
                type,
                template == null ? DisplayTitle(type) : template.Title,
                origin,
                template == null ? DefaultSeverity(type) : template.Severity,
                template == null ? "REQUEST_BACKUP" : template.Audio,
                "Patrol fallback",
                false,
                gangData);
        }

        private AnyiPoliceIncident BuildIncident(AnyiPoliceIncidentType type, string title, Vector3 position, float severity, string audio, string activityName, bool fromChaos, AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            string gang = "none";
            string turf = "none";
            if (gangData != null)
            {
                string owner = gangData.GetTerritoryOwner(position.X, position.Y, position.Z);
                if (!string.IsNullOrWhiteSpace(owner)) gang = owner;
                AnyiLSPDPoliceData.TurfZone zone = gangData.GetNearestTurf(position.X, position.Y, position.Z, 100f);
                if (zone != null) turf = zone.Name;
            }

            return new AnyiPoliceIncident
            {
                Type = type,
                Title = title,
                Description = "Patrol dispatch reference created from " + (fromChaos ? "ChaosResponse activity data." : "police patrol observation."),
                Origin = position,
                Severity = severity,
                GangName = gang,
                TurfName = turf,
                GeneratedFromChaosActivity = fromChaos,
                ChaosActivityName = activityName ?? "",
                AudioCategory = string.IsNullOrWhiteSpace(audio) ? "ATTENTION_ALL_UNITS" : audio,
                State = AnyiPoliceDispatchState.Offered,
                OwnedByDispatch = false
            };
        }

        private void LoadTemplates()
        {
            string path = Path.Combine(LspdResponseLog.ScriptDirectory, _config.PoliceEventsFile);
            if (!File.Exists(path)) return;
            try
            {
                XDocument doc = XDocument.Load(path);
                foreach (XElement node in doc.Root == null ? new XElement[0] : doc.Root.Elements("Event"))
                {
                    AnyiPoliceIncidentType type;
                    if (!Enum.TryParse((string)node.Attribute("type"), true, out type))
                        continue;
                    float severity = ReadFloat((string)node.Attribute("severity"), 2f);
                    string keywords = (string)node.Attribute("keywords") ?? "";
                    _templates[type.ToString()] = new EventTemplate
                    {
                        Type = type,
                        Title = (string)node.Attribute("title") ?? DisplayTitle(type),
                        Audio = (string)node.Attribute("audio") ?? "ATTENTION_ALL_UNITS",
                        Severity = severity,
                        Keywords = keywords.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    };
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_EVENT_TEMPLATE_ERROR", ex);
            }
        }

        private void LoadChaosActivities()
        {
            if (!_config.EnableChaosGangActivities || !Directory.Exists(_config.ChaosActivityRoot))
                return;
            foreach (string file in Directory.GetFiles(_config.ChaosActivityRoot, "*.xml", SearchOption.AllDirectories))
            {
                try
                {
                    XDocument doc = XDocument.Load(file);
                    XElement p = doc.Descendants()
                        .FirstOrDefault(e => string.Equals(
                            e.Name.LocalName,
                            "Position",
                            StringComparison.OrdinalIgnoreCase));

                    if (p == null)
                    {
                        LspdResponseLog.Write(
                            "POLICE_ACTIVITY_SKIP",
                            "No Position element | File=" + file);
                        continue;
                    }

                    Vector3 position = new Vector3(
                        ReadCoordinate(p, "X"),
                        ReadCoordinate(p, "Y"),
                        ReadCoordinate(p, "Z"));
                    string name = Path.GetFileNameWithoutExtension(file);
                    EventTemplate template = FindTemplateByKeywords(name);
                    AnyiPoliceIncidentType type = template == null ? MapActivity(name) : template.Type;
                    _activities.Add(new ChaosActivityLocation
                    {
                        Name = name,
                        Position = position,
                        Type = type,
                        Title = template == null ? DisplayTitle(type) : template.Title,
                        Audio = template == null ? "ATTENTION_ALL_UNITS" : template.Audio,
                        Severity = template == null ? DefaultSeverity(type) : template.Severity
                    });
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("POLICE_ACTIVITY_LOAD_ERROR", ex);
                }
            }
        }

        private EventTemplate FindTemplate(AnyiPoliceIncidentType type)
        {
            EventTemplate template;
            return _templates.TryGetValue(type.ToString(), out template) ? template : null;
        }

        private EventTemplate FindTemplateByKeywords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (EventTemplate template in _templates.Values)
            {
                foreach (string keyword in template.Keywords)
                    if (text.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                        return template;
            }
            return null;
        }

        private static AnyiPoliceIncidentType MapActivity(string name)
        {
            string value = (name ?? "").ToLowerInvariant();
            if (value.Contains("kidnap")) return AnyiPoliceIncidentType.Kidnapping;
            if (value.Contains("heist") || value.Contains("bank")) return AnyiPoliceIncidentType.BankHeist;
            if (value.Contains("store") || value.Contains("shop")) return AnyiPoliceIncidentType.StoreRobbery;
            if (value.Contains("pursuit") || value.Contains("chase")) return AnyiPoliceIncidentType.VehiclePursuit;
            if (value.Contains("shoot") || value.Contains("ambush")) return AnyiPoliceIncidentType.GangAmbush;
            if (value.Contains("arms") || value.Contains("weapon")) return AnyiPoliceIncidentType.ArmsDealing;
            if (value.Contains("drug")) return AnyiPoliceIncidentType.DrugDealing;
            return AnyiPoliceIncidentType.SuspiciousGangActivity;
        }

        private static string DisplayTitle(AnyiPoliceIncidentType type)
        {
            switch (type)
            {
                case AnyiPoliceIncidentType.BankHeist: return "Bank Robbery Reported";
                case AnyiPoliceIncidentType.StoreRobbery: return "Store Robbery Reported";
                case AnyiPoliceIncidentType.Kidnapping: return "Possible Kidnapping";
                case AnyiPoliceIncidentType.VehiclePursuit: return "Vehicle Pursuit";
                case AnyiPoliceIncidentType.RecklessDriver: return "Reckless Driver";
                case AnyiPoliceIncidentType.PedestrianPursuit: return "Suspect Fleeing On Foot";
                default: return "Police Incident Reported";
            }
        }

        private static float DefaultSeverity(AnyiPoliceIncidentType type)
        {
            switch (type)
            {
                case AnyiPoliceIncidentType.BankHeist: return 5f;
                case AnyiPoliceIncidentType.MassShootout: return 5f;
                case AnyiPoliceIncidentType.GangAmbush: return 4f;
                case AnyiPoliceIncidentType.Kidnapping: return 4f;
                case AnyiPoliceIncidentType.VehiclePursuit: return 3f;
                default: return 2f;
            }
        }

        private void ScheduleInitialFallback(DateTime now)
        {
            int seconds = _random.Next(
                _config.InitialFallbackMinSeconds,
                _config.InitialFallbackMaxSeconds + 1);
            _nextFallback = now.AddSeconds(seconds);
        }

        private void SetOfferCooldown(DateTime now)
        {
            _cooldownUntil = now.AddSeconds(_config.EventCooldownSeconds);
        }

        private void ScheduleFallback(DateTime now)
        {
            int seconds = _random.Next(_config.FallbackEventMinSeconds, _config.FallbackEventMaxSeconds + 1);
            _nextFallback = now.AddSeconds(seconds);
        }

        private static float ReadCoordinate(XElement element, string name)
        {
            if (element == null)
                return 0f;

            foreach (XAttribute attribute in element.Attributes())
            {
                if (string.Equals(
                    attribute.Name.LocalName,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return ReadFloat(attribute.Value, 0f);
                }
            }

            XElement child = element.Elements()
                .FirstOrDefault(e => string.Equals(
                    e.Name.LocalName,
                    name,
                    StringComparison.OrdinalIgnoreCase));

            return child == null ? 0f : ReadFloat(child.Value, 0f);
        }

        private static float ReadFloat(string value, float fallback)
        {
            float result;
            return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result) ? result : fallback;
        }
    }
}

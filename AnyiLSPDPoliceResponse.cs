using GTA;
using GTA.Math;
using GTA.Native;
using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDPoliceResponse
    {
        public sealed class PoliceUnit
        {
            public Vehicle Vehicle;
            public Ped Driver;
            public string Role;
            public bool Owned;
            public Vector3 Destination;
            public DateTime LastTaskAt = DateTime.MinValue;
            public bool Responding;
        }

        private readonly List<PoliceUnit> _units = new List<PoliceUnit>();
        private readonly AnyiLSPDProfileCore _profiles;
        private readonly AnyiLSPDPoliceConfig _config;
        private readonly AnyiLSPDPoliceStations _stations;

        public AnyiLSPDPoliceResponse(
            AnyiLSPDProfileCore profiles,
            AnyiLSPDPoliceConfig config,
            AnyiLSPDPoliceStations stations)
        {
            _profiles = profiles;
            _config = config;
            _stations = stations;
        }

        public int ActiveUnitCount
        {
            get
            {
                CleanupDead();
                return _units.Count;
            }
        }

        public PoliceUnit EnsureResponseUnit(Vector3 destination)
        {
            CleanupDead();

            foreach (PoliceUnit existing in _units)
            {
                if (existing != null && existing.Vehicle != null && existing.Vehicle.Exists())
                {
                    existing.Destination = destination;
                    existing.Responding = true;
                    SendTo(existing, destination, true);
                    return existing;
                }
            }

            if (_units.Count >= _config.MaxPoliceUnits)
                return _units.Count > 0 ? _units[0] : null;

            AnyiLSPDProfileCore.PoliceProfile profile = _profiles.Current;
            if (profile == null)
                return null;

            AnyiLSPDPoliceStations.Station station = _stations.Get(profile.StationId)
                ?? _stations.Get("MissionRow");

            if (station == null)
                return null;

            string responseVehicleName = string.IsNullOrWhiteSpace(profile.ResponseVehicleModel) ? "police" : profile.ResponseVehicleModel;
            string responseOfficerName = string.IsNullOrWhiteSpace(profile.ResponseOfficerModel) ? "s_m_y_cop_01" : profile.ResponseOfficerModel;
            Model vehicleModel = CreateModel(responseVehicleName);
            Model pedModel = CreateModel(responseOfficerName);

            if (!ValidateAndRequest(vehicleModel, true) ||
                !ValidateAndRequest(pedModel, false))
            {
                ReleaseModel(vehicleModel);
                ReleaseModel(pedModel);
                return null;
            }

            try
            {
                Vehicle vehicle = World.CreateVehicle(
                    vehicleModel,
                    station.SpawnPosition,
                    station.Heading);

                if (vehicle == null || !vehicle.Exists())
                    return null;

                vehicle.IsPersistent = true;
                vehicle.PlaceOnGround();

                Ped driver = vehicle.CreatePedOnSeat(
                    VehicleSeat.Driver,
                    pedModel);

                if (driver == null || !driver.Exists())
                {
                    vehicle.Delete();
                    return null;
                }

                driver.IsPersistent = true;
                driver.BlockPermanentEvents = true;
                driver.Accuracy = 65;
                driver.MaxHealth = 250;
                driver.Health = 250;
                driver.Armor = 100;

                PoliceUnit unit = new PoliceUnit
                {
                    Vehicle = vehicle,
                    Driver = driver,
                    Role = "Response",
                    Owned = true,
                    Destination = destination,
                    Responding = true
                };

                _units.Add(unit);
                ConfigureEmergency(vehicle, profile);
                SendTo(unit, destination, true);

                LspdResponseLog.Write(
                    "POLICE_RESPONSE",
                    "Response unit created | Vehicle=" + vehicle.Model.Hash +
                    " | Driver=" + driver.Model.Hash +
                    " | Destination=" + destination);

                return unit;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_RESPONSE_SPAWN_ERROR",
                    ex);
                return null;
            }
            finally
            {
                ReleaseModel(vehicleModel);
                ReleaseModel(pedModel);
            }
        }

        public Vehicle SpawnPatrolForAnyiii()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return null;

            PoliceUnit unit = EnsureResponseUnit(player.Position);
            return unit == null ? null : unit.Vehicle;
        }

        public bool SendTo(
            PoliceUnit unit,
            Vector3 destination,
            bool forceTask = false)
        {
            if (unit == null ||
                unit.Vehicle == null ||
                !unit.Vehicle.Exists() ||
                unit.Driver == null ||
                !unit.Driver.Exists())
                return false;

            DateTime now = DateTime.UtcNow;
            if (!forceTask && now < unit.LastTaskAt.AddSeconds(6))
                return true;

            try
            {
                unit.Destination = destination;

                unit.Driver.Task.DriveTo(
                    unit.Vehicle,
                    destination,
                    _config.ResponseDriveRadius,
                    VehicleDrivingFlags.DrivingModeStopForVehicles,
                    _config.ResponseDriveSpeed);

                ConfigureEmergency(
                    unit.Vehicle,
                    _profiles.Current);

                unit.Responding = true;
                unit.LastTaskAt = now;

                LspdResponseLog.Write(
                    "POLICE_RESPONSE_TASK",
                    "Driver sent to scene | Driver=" +
                    unit.Driver.Handle +
                    " | Vehicle=" +
                    unit.Vehicle.Handle +
                    " | X=" + destination.X +
                    " | Y=" + destination.Y);

                return true;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_RESPONSE_TASK_ERROR",
                    ex);
                return false;
            }
        }

        public void Update(Vector3? destination)
        {
            CleanupDead();

            foreach (PoliceUnit unit in _units)
            {
                if (unit == null ||
                    !unit.Responding ||
                    unit.Vehicle == null ||
                    !unit.Vehicle.Exists())
                    continue;

                Vector3 target = destination.HasValue
                    ? destination.Value
                    : unit.Destination;

                unit.Destination = target;

                float distance = unit.Vehicle.Position.DistanceTo(target);

                if (distance <= _config.SceneArrivalRadius)
                {
                    unit.Responding = false;
                    try
                    {
                        unit.Driver.Task.ClearAll();
                    }
                    catch { }

                    LspdResponseLog.Write(
                        "POLICE_RESPONSE",
                        "Unit arrived near dispatch scene | Driver=" +
                        unit.Driver.Handle +
                        " | Distance=" + distance);

                    continue;
                }

                // Re-issue only when enough time has passed. This avoids
                // per-frame task spam while recovering from stalled AI.
                SendTo(unit, target, false);
            }
        }

        public void ReleaseUnit(PoliceUnit unit)
        {
            if (unit == null)
                return;

            int index = _units.IndexOf(unit);
            if (index < 0)
                return;

            try
            {
                if (unit.Driver != null && unit.Driver.Exists())
                {
                    unit.Driver.Task.ClearAll();
                    unit.Driver.IsPersistent = false;
                    unit.Driver.Delete();
                }

                if (unit.Vehicle != null && unit.Vehicle.Exists())
                {
                    unit.Vehicle.IsPersistent = false;
                    Function.Call(
                        Hash.SET_VEHICLE_SIREN,
                        unit.Vehicle,
                        false);
                    Function.Call(
                        Hash.SET_VEHICLE_LIGHTS,
                        unit.Vehicle,
                        0);
                    unit.Vehicle.Delete();
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_RESPONSE_RELEASE_ERROR",
                    ex);
            }

            _units.RemoveAt(index);
            LspdResponseLog.Write(
                "POLICE_RESPONSE",
                "Response unit released after dispatch lifecycle.");
        }

        public void ClearOwnedUnits()
        {
            foreach (PoliceUnit unit in _units)
            {
                ReleaseUnitInternal(unit);
            }

            _units.Clear();
        }

        private void ReleaseUnitInternal(PoliceUnit unit)
        {
            try
            {
                if (unit == null)
                    return;

                if (unit.Driver != null && unit.Driver.Exists())
                {
                    unit.Driver.Task.ClearAll();
                    unit.Driver.IsPersistent = false;
                    unit.Driver.Delete();
                }

                if (unit.Vehicle != null && unit.Vehicle.Exists())
                {
                    unit.Vehicle.IsPersistent = false;
                    Function.Call(Hash.SET_VEHICLE_SIREN, unit.Vehicle, false);
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, unit.Vehicle, 0);
                    unit.Vehicle.Delete();
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_RESPONSE_CLEANUP_ERROR",
                    ex);
            }
        }

        private void ConfigureEmergency(
            Vehicle vehicle,
            AnyiLSPDProfileCore.PoliceProfile profile)
        {
            if (vehicle == null ||
                !vehicle.Exists() ||
                profile == null)
                return;

            try
            {
                Function.Call(
                    Hash.SET_VEHICLE_LIGHTS,
                    vehicle,
                    profile.EmergencyLights ? 2 : 0);

                Function.Call(
                    Hash.SET_VEHICLE_SIREN,
                    vehicle,
                    profile.NativeSiren && vehicle.HasSiren);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_EMERGENCY_ERROR",
                    ex);
            }
        }

        private void CleanupDead()
        {
            for (int i = _units.Count - 1; i >= 0; i--)
            {
                PoliceUnit unit = _units[i];

                if (unit == null ||
                    unit.Vehicle == null ||
                    !unit.Vehicle.Exists() ||
                    unit.Driver == null ||
                    !unit.Driver.Exists() ||
                    unit.Driver.IsDead)
                {
                    ReleaseUnitInternal(unit);
                    _units.RemoveAt(i);
                }
            }
        }

        private static Model CreateModel(string value)
        {
            int hash;
            return int.TryParse(value, out hash)
                ? new Model(hash)
                : new Model(value);
        }

        private static bool ValidateAndRequest(
            Model model,
            bool vehicle)
        {
            if (model == null ||
                !model.IsValid ||
                (vehicle ? !model.IsVehicle : !model.IsPed))
                return false;

            return model.Request(1500) && model.IsLoaded;
        }

        private static void ReleaseModel(Model model)
        {
            if (model == null)
                return;

            try { model.MarkAsNoLongerNeeded(); }
            catch { }
        }
    }
}

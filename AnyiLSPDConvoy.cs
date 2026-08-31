using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDConvoy
    {
        private readonly AnyiLSPDPoliceConfig _config;
        private readonly AnyiLSPDProfileCore _profiles;
        private readonly AnyiLSPDPoliceStations _stations;
        private readonly AnyiLSPDChaosAudio _audio;

        private Vehicle _transport;
        private Ped _driver;
        private Ped _stationOfficer;
        private Ped _prisoner;
        private int _prisonerModelHash;
        private DateTime _custodyEstablishedAt = DateTime.MinValue;
        private const int CustodyEntityGraceSeconds = 6;

        private AnyiPoliceDispatchState _state = AnyiPoliceDispatchState.None;
        private Vector3 _stationDestination;
        private Vector3 _prisonDestination;

        private DateTime _nextWatchdog = DateTime.MinValue;
        private DateTime _nextPrompt = DateTime.MinValue;

        private sealed class DeferredEntityCleanup
        {
            public Ped Ped;
            public Vehicle Vehicle;
            public DateTime Earliest;
            public DateTime Expires;
        }

        private readonly System.Collections.Generic.List<DeferredEntityCleanup> _deferredCleanup = new System.Collections.Generic.List<DeferredEntityCleanup>();

        private bool _hadPreviousWaypoint;
        private Vector3 _previousWaypoint = Vector3.Zero;

        public AnyiPoliceDispatchState State { get { return _state; } }

        // HoldingAtStation deliberately remains Active even after the AI driver
        // is removed, because the prisoner and transport remain Police-owned.
        public bool Active
        {
            get
            {
                return (_state == AnyiPoliceDispatchState.HoldingAtStation ||
                        _state == AnyiPoliceDispatchState.PrisonTransfer) &&
                       _transport != null && _transport.Exists();
            }
        }

        public bool HoldingAtStation
        {
            get { return _state == AnyiPoliceDispatchState.HoldingAtStation && Active; }
        }

        public bool Completed
        {
            get { return _state == AnyiPoliceDispatchState.Completed; }
        }

        public AnyiLSPDConvoy(
            AnyiLSPDPoliceConfig config,
            AnyiLSPDProfileCore profiles,
            AnyiLSPDPoliceStations stations,
            AnyiLSPDChaosAudio audio)
        {
            _config = config;
            _profiles = profiles;
            _stations = stations;
            _audio = audio;
        }

        public string Start(Ped prisoner, Vector3 pickupTarget)
        {
            // The pickupTarget argument remains for compatibility with the current
            // Core/UI call signature. The repaired design intentionally does NOT
            // drive a van across the city for prisoner pickup.
            if (!_config.EnableConvoy)
                return "Prisoner convoy is disabled in AnyiLSPDPolice.ini.";

            if (Active)
                return "A prisoner custody operation is already active.";

            if (prisoner == null || !prisoner.Exists() || prisoner.IsDead)
                return "No living arrested suspect is available.";

            AnyiLSPDPoliceStations.Station station =
                FindNearestOperationalStation(prisoner.Position)
                ?? _stations.Get(_profiles.Current == null ? "MissionRow" : _profiles.Current.StationId)
                ?? _stations.Get("MissionRow");

            AnyiLSPDPoliceStations.Station prison =
                _stations.Get(_config.PrisonStation)
                ?? _stations.Get("Bolingbroke");

            if (station == null)
                return "No police station is configured.";

            if (prison == null)
                return "No prison destination is configured.";

            string transportName =
                _profiles.Current == null ||
                string.IsNullOrWhiteSpace(_profiles.Current.TransportVehicleModel) ||
                string.Equals(_profiles.Current.TransportVehicleModel, "stockade", StringComparison.OrdinalIgnoreCase)
                    ? "fbi2"
                    : _profiles.Current.TransportVehicleModel;

            string officerName =
                _profiles.Current == null ||
                string.IsNullOrWhiteSpace(_profiles.Current.ResponseOfficerModel)
                    ? "s_m_y_cop_01"
                    : _profiles.Current.ResponseOfficerModel;

            Model transportModel = new Model(transportName);
            Model officerModel = new Model(officerName);

            if (!transportModel.IsValid ||
                !transportModel.IsVehicle ||
                !transportModel.Request(1500) ||
                !transportModel.IsLoaded)
            {
                return "Transport vehicle model is unavailable: " + transportName;
            }

            if (!officerModel.IsValid ||
                !officerModel.IsPed ||
                !officerModel.Request(1500) ||
                !officerModel.IsLoaded)
            {
                transportModel.MarkAsNoLongerNeeded();
                return "Transport officer model is unavailable: " + officerName;
            }

            try
            {
                _stationDestination = station.Exterior;
                _prisonDestination = prison.Exterior;

                // Immediate, deterministic custody transfer:
                // arrest -> station. No fragile pickup-drive phase.
                _transport = World.CreateVehicle(
                    transportModel,
                    station.SpawnPosition,
                    station.Heading);

                if (_transport == null || !_transport.Exists())
                    return "Prisoner transport could not be spawned at the selected station.";

                _transport.IsPersistent = true;
                _transport.PlaceOnGround();

                // Emergency lighting can be left available without forcing the custom
                // PolIgnus native siren path. The profile remains authoritative.
                if (_config.EmergencyLights)
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, _transport, 0);

                _driver = _transport.CreatePedOnSeat(
                    VehicleSeat.Driver,
                    officerModel);

                if (_driver == null || !_driver.Exists())
                {
                    Cleanup(false);
                    return "Prisoner transport officer could not be created.";
                }

                _driver.IsPersistent = true;
                _driver.BlockPermanentEvents = true;
                _driver.MaxHealth = 250;
                _driver.Health = 250;
                _driver.Armor = 100;

                // One officer on foot at the station gives the custody scene a
                // believable police presence without creating a whole backup fleet.
                _stationOfficer = World.CreatePed(
                    officerModel,
                    station.Exterior + new Vector3(2.0f, 1.5f, 0.0f));

                if (_stationOfficer != null && _stationOfficer.Exists())
                {
                    _stationOfficer.IsPersistent = true;
                    _stationOfficer.BlockPermanentEvents = true;
                    _stationOfficer.MaxHealth = 250;
                    _stationOfficer.Health = 250;
                    _stationOfficer.Armor = 100;
                    _stationOfficer.Task.StandStill(5000);
                }

                _prisoner = prisoner;
                _prisonerModelHash = prisoner.Model.Hash;
                _custodyEstablishedAt = DateTime.UtcNow;
                _prisoner.IsPersistent = true;

                Function.Call(Hash.SET_ENABLE_HANDCUFFS, _prisoner, true);
                _prisoner.BlockPermanentEvents = true;
                _prisoner.CanSwitchWeapons = false;
                _prisoner.Task.ClearAll();

                // No walking animation, no pickup travel: the custody state is
                // intentionally instantiated already at the station.
                _prisoner.SetIntoVehicle(
                    _transport,
                    VehicleSeat.RightRear);

                // Establish custody only after the entity has survived the first
                // seat handoff. A transient streaming/task failure is tolerated by
                // CheckCustody for a short grace window instead of immediately
                // declaring the whole arrest compromised.
                _custodyEstablishedAt = DateTime.UtcNow;
                _state = AnyiPoliceDispatchState.HoldingAtStation;
                _nextWatchdog = DateTime.MinValue;
                _nextPrompt = DateTime.MinValue;

                CaptureAndSetWaypoint(_stationDestination);

                _audio.PlayFirstAvailable(
                    "CRIME_OFFICER_REQUESTS_TRANSPORT|UNIT_RESPONDING_DISPATCH",
                    false);

                Notification.PostTicker(
                    "~g~ARRESTED SUCCESSFULLY~s~\n" +
                    "Suspect is secured at " + station.Name + ".\n" +
                    "~c~Press Transport again to approve prison transfer.",
                    false,
                    false);

                LspdResponseLog.Write(
                    "POLICE_CONVOY",
                    "Station custody created instantly | Prisoner=" +
                    _prisoner.Handle +
                    " | Transport=" + _transport.Handle +
                    " | Station=" + station.Name +
                    " | Prison=" + prison.Name +
                    " | RequestedPickup=" + pickupTarget);

                return "Arrest completed. Suspect was secured at " +
                       station.Name +
                       ". Press Transport again to transfer to prison.";
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_CONVOY_START_ERROR",
                    ex);

                Cleanup(false);
                return "Station custody setup failed safely.";
            }
            finally
            {
                transportModel.MarkAsNoLongerNeeded();
                officerModel.MarkAsNoLongerNeeded();
            }
        }

        public string ContinueToPrison()
        {
            if (!HoldingAtStation)
                return "Prisoner is not waiting at the selected police station.";

            // The repaired flow is player-driven. Remove the AI driver so Anyi
            // can enter the transport and personally drive the prisoner to prison.
            try
            {
                if (_driver != null && _driver.Exists())
                {
                    _driver.Task.ClearAll();
                    _driver.IsPersistent = false;
                    _driver.Delete();
                }
            }
            catch { }

            _driver = null;
            _state = AnyiPoliceDispatchState.PrisonTransfer;
            CaptureAndSetWaypoint(_prisonDestination);

            _audio.PlayFirstAvailable(
                "CRIME_OFFICER_REQUESTS_TRANSPORT|UNIT_RESPONDING_DISPATCH",
                false);

            Notification.PostTicker(
                "~b~PRISON TRANSFER~s~\n" +
                "Transfer approved.\n" +
                "~c~Enter the Police transport and drive to " +
                _config.PrisonStation + ". GPS is set automatically.",
                false,
                false);

            LspdResponseLog.Write(
                "POLICE_CONVOY",
                "Prison transfer approved | Player-driven transport required | Destination=" +
                _config.PrisonStation);

            return "Prison transfer approved. Enter the transport and drive to prison.";
        }

        public string DeclineAtStation()
        {
            if (!HoldingAtStation)
                return "Prisoner is not waiting at the selected police station.";

            LspdResponseLog.Write(
                "POLICE_CONVOY",
                "Prison transfer declined at station. Prisoner justice task will close.");

            Cleanup(true);
            _state = AnyiPoliceDispatchState.Completed;

            return "Prison transfer declined. Justice task completed at the station and the Police-owned suspect was cleared.";
        }

        public string Update(DateTime now)
        {
            ProcessDeferredCleanup(now);
            if (!Active)
                return null;

            if (now >= _nextWatchdog)
            {
                _nextWatchdog =
                    now.AddSeconds(Math.Max(2, _config.CustodyWatchdogSeconds));

                string watchdog = CheckCustody();
                if (!string.IsNullOrWhiteSpace(watchdog))
                    return watchdog;
            }

            if (_state == AnyiPoliceDispatchState.HoldingAtStation)
            {
                // The station scene stays alive. Do not drive the vehicle automatically.
                if (now >= _nextPrompt)
                {
                    _nextPrompt = now.AddSeconds(12);

                    Notification.PostTicker(
                        "~b~PRISONER CUSTODY~s~\n" +
                        "Suspect is secured at the station.\n" +
                        "~c~Press Transport again to approve prison transfer.",
                        false,
                        false);

                    return "Prisoner holding at station; awaiting prison-transfer approval.";
                }

                return null;
            }

            if (_state == AnyiPoliceDispatchState.PrisonTransfer)
            {
                Ped player = Game.Player.Character;

                if (player == null || !player.Exists())
                    return "Player character unavailable during prison transfer.";

                // Player must actually be driving / occupying the configured transport.
                if (player.CurrentVehicle == null ||
                    !player.CurrentVehicle.Exists() ||
                    player.CurrentVehicle.Handle != _transport.Handle)
                {
                    if (now >= _nextPrompt)
                    {
                        _nextPrompt = now.AddSeconds(10);

                        Notification.PostTicker(
                            "~y~PRISON TRANSFER~s~\n" +
                            "Enter the Police transport to continue.\n" +
                            "~c~GPS is already marked.",
                            false,
                            false);

                        return "Prison transfer paused until Anyi enters the Police transport.";
                    }

                    return null;
                }

                if (_transport.Position.DistanceTo(_prisonDestination) <=
                    _config.ConvoyArrivalRadius)
                {
                    _state = AnyiPoliceDispatchState.Completed;

                    _audio.PlayFirstAvailable(
                        _config.TransportSuccessAudioCategories,
                        true);

                    Notification.PostTicker(
                        "~g~TRANSPORT SUCCESS~s~\n" +
                        "Prisoner delivered to " +
                        _config.PrisonStation +
                        ".\n" +
                        "~c~Prison booking complete.",
                        false,
                        false);

                    LspdResponseLog.Write(
                        "POLICE_CONVOY",
                        "Prison arrival reached | Prisoner=" +
                        _prisoner.Handle +
                        " | Booking complete.");

                    RestorePreviousWaypoint();
                    Cleanup(true);

                    // State remains Completed after cleanup. AnyiLSPDCore consumes
                    // this terminal state on the same update loop and closes the
                    // linked dispatch automatically; no "Finish Prison Booking"
                    // UI click is required.
                    LspdResponseLog.Write(
                        "POLICE_CONVOY",
                        "TRANSPORT_SUCCESS_TERMINAL | Convoy state=Completed | Auto dispatch completion requested.");

                    return "Prisoner delivered and booking completed.";
                }

                return null;
            }

            return null;
        }

        public void Cancel(string reason)
        {
            if (!Active &&
                _transport == null &&
                _prisoner == null &&
                _stationOfficer == null)
                return;

            LspdResponseLog.Write(
                "POLICE_CONVOY",
                "Cancelled | Reason=" + reason);

            Cleanup(false);
            _state = AnyiPoliceDispatchState.Cancelled;
        }

        private AnyiLSPDPoliceStations.Station FindNearestOperationalStation(Vector3 position)
        {
            AnyiLSPDPoliceStations.Station nearest = null;
            float best = float.MaxValue;

            foreach (AnyiLSPDPoliceStations.Station station in _stations.All)
            {
                if (station == null)
                    continue;
                if (string.Equals(station.Id, _config.PrisonStation, StringComparison.OrdinalIgnoreCase))
                    continue;

                float distance = station.Exterior.DistanceTo(position);
                if (distance < best)
                {
                    best = distance;
                    nearest = station;
                }
            }

            return nearest;
        }

        private string CheckCustody()
        {
            if (_transport == null ||
                !_transport.Exists())
            {
                _state = AnyiPoliceDispatchState.Compromised;
                Cleanup(false);
                return "Police transport entity was lost; custody operation was cleaned.";
            }

            if (_transport.IsDead || !_transport.IsDriveable)
            {
                _state = AnyiPoliceDispatchState.Compromised;

                LspdResponseLog.Write(
                    "POLICE_CONVOY",
                    "Transport compromised | prisoner=" +
                    (_prisoner == null ? -1 : _prisoner.Handle));

                Notification.PostTicker(
                    "~r~POLICE CONVOY~s~\n" +
                    "Transport compromised. Custody operation was stopped safely.",
                    false,
                    false);

                Cleanup(false);
                return "Transport compromised. Convoy was halted and cleaned.";
            }

            if (_prisoner == null ||
                !_prisoner.Exists())
            {
                DateTime graceUntil = _custodyEstablishedAt.AddSeconds(CustodyEntityGraceSeconds);
                if (DateTime.UtcNow <= graceUntil && _prisonerModelHash != 0)
                {
                    try
                    {
                        Model replacementModel = new Model(_prisonerModelHash);
                        if (replacementModel.IsValid && replacementModel.IsPed && replacementModel.Request(1000) && replacementModel.IsLoaded)
                        {
                            Vector3 spawn = _transport.Position + new Vector3(0f, -1.5f, 0.5f);
                            Ped replacement = World.CreatePed(replacementModel, spawn);
                            replacementModel.MarkAsNoLongerNeeded();
                            if (replacement != null && replacement.Exists())
                            {
                                replacement.IsPersistent = true;
                                replacement.BlockPermanentEvents = true;
                                replacement.CanSwitchWeapons = false;
                                Function.Call(Hash.SET_ENABLE_HANDCUFFS, replacement, true);
                                replacement.SetIntoVehicle(_transport, VehicleSeat.RightRear);
                                if (replacement.Exists())
                                {
                                    _prisoner = replacement;
                                    LspdResponseLog.Write(
                                        "POLICE_CONVOY",
                                        "Custody entity re-established during handoff grace | Ped=" + replacement.Handle +
                                        " | Model=" + _prisonerModelHash);
                                    return null;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LspdResponseLog.WriteException("POLICE_CONVOY_CUSTODY_REPAIR_ERROR", ex);
                    }
                }

                // A dead prisoner is a true custody failure. A missing entity is
                // only considered compromised after the grace/recovery window.
                _state = AnyiPoliceDispatchState.Compromised;

                LspdResponseLog.Write(
                    "POLICE_CONVOY",
                    "Prisoner entity was lost after custody handoff grace.");

                Cleanup(false);
                return "Prisoner custody entity was lost after handoff recovery. Convoy was cleaned.";
            }

            if (_prisoner.IsDead)
            {
                _state = AnyiPoliceDispatchState.Compromised;
                LspdResponseLog.Write("POLICE_CONVOY", "Prisoner died during custody.");
                Cleanup(false);
                return "Prisoner died during custody. Convoy was cleaned.";
            }

            Function.Call(
                Hash.SET_ENABLE_HANDCUFFS,
                _prisoner,
                true);

            _prisoner.BlockPermanentEvents = true;
            _prisoner.CanSwitchWeapons = false;

            if ((_state == AnyiPoliceDispatchState.HoldingAtStation ||
                 _state == AnyiPoliceDispatchState.PrisonTransfer) &&
                !_prisoner.IsInVehicle(_transport))
            {
                try
                {
                    _prisoner.SetIntoVehicle(
                        _transport,
                        VehicleSeat.RightRear);

                    LspdResponseLog.Write(
                        "POLICE_CONVOY",
                        "Custody watchdog reseated prisoner into transport.");
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "POLICE_CONVOY_RESEAT_ERROR",
                        ex);

                    return "Custody watchdog could not reseat the prisoner.";
                }
            }

            return null;
        }

        private void CaptureAndSetWaypoint(Vector3 position)
        {
            try
            {
                if (!_hadPreviousWaypoint &&
                    Game.IsWaypointActive)
                {
                    _previousWaypoint =
                        World.WaypointPosition;
                    _hadPreviousWaypoint = true;
                }

                World.WaypointPosition = position;

                LspdResponseLog.Write(
                    "POLICE_CONVOY_GPS",
                    "Waypoint set | X=" +
                    position.X +
                    " | Y=" +
                    position.Y +
                    " | Z=" +
                    position.Z);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_CONVOY_GPS_ERROR",
                    ex);
            }
        }

        private void RestorePreviousWaypoint()
        {
            try
            {
                if (_hadPreviousWaypoint)
                {
                    World.WaypointPosition =
                        _previousWaypoint;
                }
                else if (Game.IsWaypointActive)
                {
                    World.RemoveWaypoint();
                }
            }
            catch { }
            finally
            {
                _hadPreviousWaypoint = false;
                _previousWaypoint = Vector3.Zero;
            }
        }

        private void Cleanup(bool bookIn)
        {
            RestorePreviousWaypoint();

            if (bookIn)
            {
                QueueSuccessCleanup();
            }
            else
            {
                ImmediateCleanup();
            }

            _driver = null;
            _stationOfficer = null;
            _transport = null;
            _prisoner = null;
            _prisonerModelHash = 0;
            _custodyEstablishedAt = DateTime.MinValue;
            _nextDriveTask = DateTime.MinValue;
            _nextWatchdog = DateTime.MinValue;
            _nextPrompt = DateTime.MinValue;
        }

        private void QueueSuccessCleanup()
        {
            DateTime now = DateTime.UtcNow;
            DateTime earliest = now.AddSeconds(_config.ConvoyCleanupGraceSeconds);
            DateTime expires = now.AddSeconds(_config.ConvoyCleanupMaxSeconds);

            if (_prisoner != null && _prisoner.Exists())
            {
                try { _prisoner.IsPersistent = true; } catch { }
                _deferredCleanup.Add(new DeferredEntityCleanup { Ped = _prisoner, Earliest = earliest, Expires = expires });
            }
            if (_stationOfficer != null && _stationOfficer.Exists())
            {
                try { _stationOfficer.IsPersistent = true; } catch { }
                _deferredCleanup.Add(new DeferredEntityCleanup { Ped = _stationOfficer, Earliest = earliest, Expires = expires });
            }
            if (_transport != null && _transport.Exists())
            {
                try { _transport.IsPersistent = true; } catch { }
                _deferredCleanup.Add(new DeferredEntityCleanup { Vehicle = _transport, Earliest = earliest, Expires = expires });
            }

            LspdResponseLog.Write("POLICE_CONVOY", "Deferred cleanup queued | Entities=" + _deferredCleanup.Count + " | GraceSeconds=" + _config.ConvoyCleanupGraceSeconds + " | Distance=" + _config.ConvoyCleanupDistance);
        }

        private void ProcessDeferredCleanup(DateTime now)
        {
            if (_deferredCleanup.Count == 0) return;
            Ped player = Game.Player.Character;

            foreach (DeferredEntityCleanup item in _deferredCleanup.ToArray())
            {
                bool exists = (item.Ped != null && item.Ped.Exists()) || (item.Vehicle != null && item.Vehicle.Exists());
                if (!exists)
                {
                    _deferredCleanup.Remove(item);
                    continue;
                }

                float distance = float.MaxValue;
                if (player != null && player.Exists())
                {
                    if (item.Ped != null && item.Ped.Exists()) distance = item.Ped.Position.DistanceTo(player.Position);
                    else if (item.Vehicle != null && item.Vehicle.Exists()) distance = item.Vehicle.Position.DistanceTo(player.Position);
                }

                if (now >= item.Earliest && (distance >= _config.ConvoyCleanupDistance || now >= item.Expires))
                {
                    try
                    {
                        if (item.Ped != null && item.Ped.Exists()) item.Ped.Delete();
                    }
                    catch { }
                    try
                    {
                        if (item.Vehicle != null && item.Vehicle.Exists()) item.Vehicle.Delete();
                    }
                    catch { }
                    _deferredCleanup.Remove(item);
                }
            }
        }

        private void ImmediateCleanup()
        {
            try
            {
                if (_prisoner != null && _prisoner.Exists())
                {
                    Function.Call(Hash.SET_ENABLE_HANDCUFFS, _prisoner, false);
                    _prisoner.CanSwitchWeapons = true;
                    _prisoner.BlockPermanentEvents = false;
                    _prisoner.IsPersistent = false;
                    _prisoner.Delete();
                }
            }
            catch { }

            try
            {
                if (_driver != null && _driver.Exists())
                {
                    _driver.Task.ClearAll();
                    _driver.IsPersistent = false;
                    _driver.Delete();
                }
            }
            catch { }

            try
            {
                if (_stationOfficer != null && _stationOfficer.Exists())
                {
                    _stationOfficer.Task.ClearAll();
                    _stationOfficer.IsPersistent = false;
                    _stationOfficer.Delete();
                }
            }
            catch { }

            try
            {
                if (_transport != null && _transport.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_SIREN, _transport, false);
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, _transport, 0);
                    _transport.IsPersistent = false;
                    _transport.Delete();
                }
            }
            catch { }
        }

        public void ClearTerminalState()
        {
            if (Active)
                return;

            if (_state == AnyiPoliceDispatchState.Completed ||
                _state == AnyiPoliceDispatchState.Compromised ||
                _state == AnyiPoliceDispatchState.Cancelled)
            {
                AnyiPoliceDispatchState previous = _state;
                _state = AnyiPoliceDispatchState.None;
                LspdResponseLog.Write(
                    "POLICE_CONVOY",
                    "Terminal convoy state cleared | PreviousState=" + previous);
            }
        }

        public void Reset()
        {
            foreach (DeferredEntityCleanup item in _deferredCleanup.ToArray())
            {
                try { if (item.Ped != null && item.Ped.Exists()) item.Ped.Delete(); } catch { }
                try { if (item.Vehicle != null && item.Vehicle.Exists()) item.Vehicle.Delete(); } catch { }
            }
            _deferredCleanup.Clear();
            ImmediateCleanup();
            _state = AnyiPoliceDispatchState.None;
            _stationDestination = Vector3.Zero;
            _prisonDestination = Vector3.Zero;
        }

        // Kept as a private compatibility field reset target so older source
        // revisions that referenced a drive-task clock do not leak state.
        private DateTime _nextDriveTask = DateTime.MinValue;

     
        }
    }

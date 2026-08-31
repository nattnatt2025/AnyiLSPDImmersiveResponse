using GTA;
using GTA.Native;
using GTA.UI;
using GTA.Math;
using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDDispatch
    {
        private readonly AnyiLSPDPoliceConfig _config;
        private readonly AnyiLSPDChaosAudio _audio;
        private readonly AnyiLSPDPoliceResponse _response;
        private AnyiPoliceIncident _incident;
        private AnyiLSPDPoliceResponse.PoliceUnit _assignedUnit;
        private Blip _blip;
        private Blip _suspectBlip;
        private DateTime _lastStateCheck = DateTime.MinValue;
        private DateTime _lastSuspectGpsUpdate = DateTime.MinValue;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private DateTime _lastSurrenderRefresh = DateTime.MinValue;
        private bool _hadPreviousWaypoint;
        private Vector3 _previousWaypoint = Vector3.Zero;
        private bool _deadSuspectSceneAcknowledged;
        private bool _responseUnitArrivalAcknowledged;

        // Dispatch screen notifications were causing visible UI hitches in
        // gameplay. Runtime logging and dispatch audio remain intact.
        // Set to true only if the on-screen dispatch tickers are wanted again.
        private const bool EnableDispatchScreenNotifications = true;

        private sealed class DeferredEntityCleanup
        {
            public Ped Ped;
            public Vehicle Vehicle;
            public DateTime Earliest;
            public DateTime Expires;
        }

        private readonly List<DeferredEntityCleanup> _deferredCleanup = new List<DeferredEntityCleanup>();

        public AnyiPoliceIncident Current { get { return _incident; } }
        public AnyiPoliceDispatchState State { get { return _incident == null ? AnyiPoliceDispatchState.None : _incident.State; } }
        public bool HasIncident { get { return _incident != null; } }
        public Ped CurrentSuspect { get { return _incident == null ? null : _incident.Suspect; } }
        public bool HasCooldown { get { return DateTime.UtcNow < _cooldownUntil; } }

        // True only while the current dispatch is actually in prisoner-custody /
        // transport territory. This prevents a stale convoy failure state from
        // killing a brand-new Offered/EnRoute dispatch.
        public bool IsInTransportLifecycle
        {
            get
            {
                return _incident != null && IsTransportLifecycleState(_incident.State);
            }
        }

        public AnyiLSPDDispatch(AnyiLSPDPoliceConfig config, AnyiLSPDChaosAudio audio, AnyiLSPDPoliceResponse response)
        {
            _config = config;
            _audio = audio;
            _response = response;
        }

        public bool Offer(AnyiPoliceIncident incident)
        {
            if (incident == null || _incident != null || DateTime.UtcNow < _cooldownUntil)
                return false;

            incident.State = AnyiPoliceDispatchState.Offered;
            incident.StateChangedAt = DateTime.UtcNow;
            _incident = incident;
            _deadSuspectSceneAcknowledged = false;
            _responseUnitArrivalAcknowledged = false;
            CleanupSuspectBlip();
            CaptureAndSetDispatchWaypoint(incident.Origin);
            SetBlip(incident.Origin, incident.Title);
            PlayDispatchAudioForOffer(incident);
            LspdResponseLog.Write("POLICE_DISPATCH", "OFFERED | " + incident.Title + " | Type=" + incident.Type + " | Severity=" + incident.Severity + " | Gang=" + incident.GangName + " | Turf=" + incident.TurfName + " | Chaos=" + incident.GeneratedFromChaosActivity);
            if (EnableDispatchScreenNotifications)
            {
                Notification.PostTicker(
                    "~b~POLICE DISPATCH~s~\\n" +
                    incident.Title +
                    "\\n~c~Accept / Reject in Police Radio. No mission timer.",
                    false,
                    false);
            }
            return true;
        }

        public string Accept()
        {
            if (_incident == null) return "No dispatch is currently offered.";
            if (_incident.State != AnyiPoliceDispatchState.Offered) return "This dispatch is already active.";

            SetState(AnyiPoliceDispatchState.Accepted);
            if (_config.EnablePoliceResponse)
            {
                _assignedUnit = _response.EnsureResponseUnit(_incident.Origin);
                if (_assignedUnit == null)
                {
                    Fail("No police response unit could be created.");
                    return "Dispatch accepted, but no safe response unit was available.";
                }
            }

            SetState(AnyiPoliceDispatchState.EnRoute);
            CaptureAndSetDispatchWaypoint(_incident.Origin);
            _audio.Play("UNIT_RESPONDING_DISPATCH");
            if (EnableDispatchScreenNotifications)
            {
                Notification.PostTicker(
                    "~b~POLICE DISPATCH~s~\\nUnit Anyi responding to " +
                    _incident.Title +
                    ".\\n~c~Proceed to the marked scene.",
                    false,
                    false);
            }
            return "Dispatch accepted. Police response is en route.";
        }

        public string Reject()
        {
            if (_incident == null) return "No dispatch is currently offered.";
            if (_incident.State != AnyiPoliceDispatchState.Offered) return "Only an offered dispatch can be rejected.";
            Cancel("Rejected by Anyi.");
            return "Dispatch rejected.";
        }

        public string Cancel(string reason)
        {
            if (_incident == null) return "No active dispatch.";

            AnyiPoliceIncident incident = _incident;
            SetState(AnyiPoliceDispatchState.Cancelled);
            CleanupOwnedIncident(incident);
            CleanupBlip();
            CleanupSuspectBlip();
            RestorePreviousWaypoint();
            if (_assignedUnit != null) _response.ReleaseUnit(_assignedUnit);
            _assignedUnit = null;
            _cooldownUntil = DateTime.UtcNow.AddSeconds(_config.DispatchCooldownSeconds);
            _incident = null;
            LspdResponseLog.Write("POLICE_DISPATCH", "CANCELLED | " + incident.Title + " | Reason=" + reason);
            return "Dispatch cancelled and all Police-owned scene state was cleaned.";
        }

        public string TrySceneInteraction(Ped player)
        {
            if (_incident == null) return "No active dispatch.";
            if (player == null || !player.Exists()) return "Player character unavailable.";

            if (_incident.State != AnyiPoliceDispatchState.EnRoute &&
                _incident.State != AnyiPoliceDispatchState.OnScene &&
                _incident.State != AnyiPoliceDispatchState.Investigating &&
                _incident.State != AnyiPoliceDispatchState.SuspectFleeing &&
                _incident.State != AnyiPoliceDispatchState.SuspectResisting)
                return "Arrive at the dispatch scene first.";

            if (_incident.Suspect == null || !_incident.Suspect.Exists())
            {
                // Missing/streamed-out is NEVER treated as neutralized. The call
                // stays alive so a transient entity loss cannot produce an instant
                // dispatch success or ghost completion.
                return "The active dispatch suspect entity is unavailable. Dispatch remains active.";
            }

            // Pursuits are mobile scenes. Once the suspect is fleeing/resisting,
            // the suspect's current position—not the original dispatch origin—
            // becomes the relevant interaction area.
            bool mobilePursuit =
                _incident.State == AnyiPoliceDispatchState.SuspectFleeing ||
                _incident.State == AnyiPoliceDispatchState.SuspectResisting;

            if (_incident.Suspect.IsDead)
            {
                if (mobilePursuit)
                {
                    CompleteSuccessful(
                        "Suspect neutralized during pursuit. Dispatch completed.",
                        _config.DispatchSuccessAudioCategories);

                    return "Suspect neutralized during pursuit. Dispatch completed.";
                }

                float sceneDistance = player.Position.DistanceTo(_incident.Origin);
                if (sceneDistance > _config.SceneArrivalRadius)
                    return "You are not close enough to the dispatch scene.";

                SetState(AnyiPoliceDispatchState.Investigating);

                // Stationary / investigative incidents keep the deliberate
                // two-stage body acknowledgment behavior.
                if (!_deadSuspectSceneAcknowledged)
                {
                    _deadSuspectSceneAcknowledged = true;
                    return "Suspect neutralized. Scene secured. Press Investigate again to close the dispatch.";
                }

                CompleteSuccessful(
                    "Suspect neutralized and scene investigated. Dispatch completed.",
                    _config.DispatchSuccessAudioCategories);

                return "Suspect neutralized and scene investigated. Dispatch completed.";
            }

            // Mobile pursuit: do not force the player back to the original GPS
            // location just to press Investigate. The target marker/GPS is the
            // active scene.
            if (mobilePursuit || (_incident.State == AnyiPoliceDispatchState.EnRoute &&
                                  IsMobilePursuitType(_incident.Type)))
            {
                if (_incident.Suspect.IsInCombatAgainst(player) || _incident.Suspect.IsShooting)
                {
                    SetState(AnyiPoliceDispatchState.SuspectResisting);
                    return "Suspect is resisting. Continue the pursuit and secure the suspect when close.";
                }

                if (_incident.Suspect.IsFleeing)
                {
                    SetState(AnyiPoliceDispatchState.SuspectFleeing);
                    return "Suspect is fleeing. Follow the moving suspect marker and secure the suspect when close.";
                }

                return "Pursuit is active. Follow the suspect marker.";
            }

            float stationarySceneDistance = player.Position.DistanceTo(_incident.Origin);
            if (stationarySceneDistance > _config.SceneArrivalRadius)
                return "You are not close enough to the dispatch scene.";

            // Arrival is a world-state transition, not something the player has
            // to press I twice to trigger.
            if (_incident.State == AnyiPoliceDispatchState.EnRoute)
                SetState(AnyiPoliceDispatchState.OnScene);

            if (_incident.State == AnyiPoliceDispatchState.OnScene)
            {
                StartSceneBehavior(player);

                // StartSceneBehavior is authoritative for moving/hostile cases.
                // Do not immediately overwrite SuspectFleeing/SuspectResisting with
                // Investigating just because the officer pressed Investigate.
                if (_incident.State == AnyiPoliceDispatchState.OnScene)
                    SetState(AnyiPoliceDispatchState.Investigating);
            }

            if (_incident.Suspect.IsInCombatAgainst(player) || _incident.Suspect.IsShooting)
            {
                SetState(AnyiPoliceDispatchState.SuspectResisting);
                _audio.Play("REPORT_SUSPECT_IS_ON_FOOT");
                return "Suspect is resisting. Use Secure Suspect when you are close enough, or pursue.";
            }

            if (_incident.Suspect.IsFleeing)
            {
                SetState(AnyiPoliceDispatchState.SuspectFleeing);
                return "Suspect is fleeing. Follow the moving suspect marker and use Secure Suspect when you are close.";
            }

            return "Scene reached. Suspect is nearby. Use Secure Suspect to issue the police surrender command.";
        }

        public string SecureSuspect(Ped player)
        {
            if (_incident == null || _incident.Suspect == null || !_incident.Suspect.Exists())
                return "There is no living suspect owned by the active dispatch.";
            if (player == null || !player.Exists())
                return "Player character unavailable.";
            if (player.Position.DistanceTo(_incident.Suspect.Position) > _config.ArrestRadius)
                return "Move closer to the suspect before issuing the police command.";
            if (_incident.Suspect.IsDead)
                return "The suspect is deceased. Investigate the scene to close the case.";

            // First press = issue the surrender command only. We do NOT declare the
            // suspect compliant immediately; the state changes after the task has had
            // time to settle and the suspect is still non-hostile. Second press = arrest.
            if (_incident.State != AnyiPoliceDispatchState.SuspectCompliant)
            {
                if (_incident.State != AnyiPoliceDispatchState.OnScene &&
                    _incident.State != AnyiPoliceDispatchState.Investigating &&
                    _incident.State != AnyiPoliceDispatchState.SuspectFleeing &&
                    _incident.State != AnyiPoliceDispatchState.SuspectResisting)
                    return "Arrive at the scene first.";

                if (_incident.SurrenderRequested)
                    return "Surrender command already issued. Wait for the suspect to put their hands up.";

                try
                {
                    _incident.Suspect.Task.ClearAll();
                    _incident.Suspect.BlockPermanentEvents = true;
                    _incident.Suspect.CanSwitchWeapons = false;
                    _incident.Suspect.Task.HandsUp(10000);
                    _incident.Suspect.Task.LookAt(player, 2500);

                    _incident.SurrenderRequested = true;
                    _incident.SurrenderRequestedAt = DateTime.UtcNow;
                    _lastSurrenderRefresh = DateTime.UtcNow.AddSeconds(2);

                    LspdResponseLog.Write(
                        "POLICE_SUSPECT_SURRENDER",
                        "Surrender command issued | Ped=" + _incident.Suspect.Handle);

                    Notification.PostTicker(
                        "~b~POLICE COMMAND~s~\nSuspect ordered to show hands.\n~c~Wait for visible compliance, then press Secure again to arrest.",
                        false,
                        false);

                    return "Surrender command issued. Wait for the suspect to put their hands up, then press Secure again.";
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException("POLICE_SUSPECT_SURRENDER_ERROR", ex);
                    return "The surrender command could not be assigned safely.";
                }
            }

            if (player.Position.DistanceTo(_incident.Suspect.Position) > _config.ArrestRadius)
                return "Move closer to the compliant suspect before securing them.";

            if (_incident.Suspect.IsInCombatAgainst(player) || _incident.Suspect.IsShooting)
            {
                SetState(AnyiPoliceDispatchState.SuspectResisting);
                return "The suspect stopped complying and is resisting again.";
            }

            try
            {
                Function.Call(Hash.SET_ENABLE_HANDCUFFS, _incident.Suspect, true);
                _incident.Suspect.Task.HandsUp(3000);
                _incident.Suspect.BlockPermanentEvents = true;
                _incident.Suspect.CanSwitchWeapons = false;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_ARREST_ERROR", ex);
                return "The arrest task could not be assigned safely.";
            }

            _incident.ArrestSecured = true;
            SetState(AnyiPoliceDispatchState.Arrested);
            _audio.PlayFirstAvailable(_config.ArrestSuccessAudioCategories, true);
            LspdResponseLog.Write(
                "POLICE_DISPATCH",
                "Arrest phase complete; dispatch remains active until custody/transport finishes.");

            Notification.PostTicker(
                "~g~ARREST SUCCESS~s~\nSuspect arrested and secured.\n~c~Use Request Prisoner Transport when ready.",
                false,
                false);

            LspdResponseLog.Write(
                "POLICE_ARREST",
                "Suspect arrested | Ped=" + _incident.Suspect.Handle +
                " | Model=" + _incident.Suspect.Model.Hash);

            return "Suspect arrested and secured. Prisoner custody is ready for transport.";
        }

        public void ReleaseAssignedResponseUnit()
        {
            if (_assignedUnit == null)
                return;

            try
            {
                _response.ReleaseUnit(_assignedUnit);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_RESPONSE_RELEASE_ERROR", ex);
            }

            _assignedUnit = null;
            LspdResponseLog.Write(
                "POLICE_RESPONSE",
                "Assigned incident response unit released as custody lifecycle began.");
        }

        // Atomic physical-ownership handoff: after Convoy.Start succeeds, the
        // Dispatch state machine keeps the incident metadata, but no longer owns
        // the actual prisoner entity. Convoy becomes the sole cleanup/custody owner.
        public bool TransferCustodyOwnershipToConvoy()
        {
            if (_incident == null || _incident.Suspect == null || !_incident.Suspect.Exists())
                return false;

            if (!_incident.OwnedByDispatch)
                return true;

            _incident.OwnedByDispatch = false;
            LspdResponseLog.Write(
                "POLICE_CUSTODY_HANDOFF",
                "Dispatch -> Convoy physical custody ownership transferred | Ped=" +
                _incident.Suspect.Handle + " | Title=" + _incident.Title);
            return true;
        }

        public void SetAwaitingTransport()
        {
            if (_incident == null) return;
            SetState(AnyiPoliceDispatchState.AwaitingTransport);
        }

        public void SetConvoyState(AnyiPoliceDispatchState state)
        {
            if (_incident == null) return;
            if (state == AnyiPoliceDispatchState.AwaitingTransport && _incident.State == AnyiPoliceDispatchState.Arrested)
                SetState(state);
            else if (state != AnyiPoliceDispatchState.None)
                SetState(state);
        }

        public string MarkJusticeTaskCompletedAfterStationDecision(string reason)
        {
            if (_incident == null)
                return "No active Police dispatch remains.";

            CompleteSuccessful(
                string.IsNullOrWhiteSpace(reason) ? "Justice task completed." : reason,
                _config.DispatchSuccessAudioCategories);

            return "Justice task completed successfully.";
        }

        public string Update(DateTime now, Ped player)
        {
            ProcessDeferredCleanup(now, player);
            if (_incident == null) return null;
            if (now < _lastStateCheck.AddMilliseconds(_config.DispatchCheckMs))
                return null;
            _lastStateCheck = now;

            // Arrival is detected automatically. I is for interaction/investigation,
            // not for waking up a scene that the officer has already reached.
            if (_incident.State == AnyiPoliceDispatchState.EnRoute &&
                player != null && player.Exists() &&
                player.Position.DistanceTo(_incident.Origin) <= _config.SceneArrivalRadius)
            {
                SetState(AnyiPoliceDispatchState.OnScene);
            }

            if (_incident.State == AnyiPoliceDispatchState.OnScene &&
                _incident.Suspect != null && _incident.Suspect.Exists())
            {
                StartSceneBehavior(player);
                if (_incident.State == AnyiPoliceDispatchState.OnScene)
                    SetState(AnyiPoliceDispatchState.Investigating);
            }

            UpdateSuspectTracking(player);

            // Pursuit completion is based on the moving suspect outcome, not the
            // original dispatch origin. Once a fleeing/resisting suspect is dead,
            // the pursuit is already resolved wherever the officer caught them.
            if (_incident != null &&
                IsPursuitIncidentState(_incident.State) &&
                _incident.Suspect != null &&
                _incident.Suspect.Exists() &&
                _incident.Suspect.IsDead)
            {
                CompleteSuccessful(
                    "Suspect neutralized during pursuit. Dispatch completed.",
                    _config.DispatchSuccessAudioCategories);

                return "Suspect neutralized during pursuit. Dispatch completed.";
            }

            if (_assignedUnit != null && _incident.State == AnyiPoliceDispatchState.EnRoute && _assignedUnit.Vehicle != null && _assignedUnit.Vehicle.Exists())
            {
                if (!_responseUnitArrivalAcknowledged &&
                    _assignedUnit.Vehicle.Position.DistanceTo(_incident.Origin) <= _config.SceneArrivalRadius)
                {
                    // Response-unit arrival does not count as Anyi's scene arrival.
                    // The player must physically reach the dispatch scene. Play the
                    // acknowledgement once only; repeating audio on every state tick
                    // was a major source of micro-hitches during active dispatches.
                    _responseUnitArrivalAcknowledged = true;
                    _audio.Play("ASSISTANCE_REQUIRED");
                }
            }

            if (_incident.Suspect != null && _incident.Suspect.Exists() && !_incident.Suspect.IsDead)
            {
                if (_incident.State == AnyiPoliceDispatchState.OnScene || _incident.State == AnyiPoliceDispatchState.Investigating || _incident.State == AnyiPoliceDispatchState.SuspectCompliant)
                {
                    if (_incident.Suspect.IsInCombatAgainst(player) || _incident.Suspect.IsShooting)
                        SetState(AnyiPoliceDispatchState.SuspectResisting);
                    else if (_incident.Suspect.IsFleeing)
                        SetState(AnyiPoliceDispatchState.SuspectFleeing);
                }
            }
            else if (_incident.Suspect != null &&
                     _incident.Suspect.IsDead &&
                     _incident.State != AnyiPoliceDispatchState.Arrested)
            {
                // Do not auto-complete from Update(). A dead suspect must be handled
                // deliberately through InvestigateScene(), preventing the dispatch
                // from ending by itself a few seconds after it starts.
                if (_incident.State != AnyiPoliceDispatchState.Investigating &&
                    _incident.State != AnyiPoliceDispatchState.OnScene)
                    SetState(AnyiPoliceDispatchState.Investigating);

                return null;
            }

            if (_incident.SurrenderRequested &&
                _incident.State != AnyiPoliceDispatchState.SuspectCompliant &&
                _incident.Suspect != null &&
                _incident.Suspect.Exists() &&
                !_incident.Suspect.IsDead &&
                now >= _incident.SurrenderRequestedAt.AddSeconds(1.5))
            {
                if (_incident.Suspect.IsInCombatAgainst(player) || _incident.Suspect.IsShooting)
                {
                    _incident.SurrenderRequested = false;
                    SetState(AnyiPoliceDispatchState.SuspectResisting);
                    return "The suspect resisted the surrender command.";
                }

                if (_incident.Suspect.IsFleeing)
                {
                    _incident.SurrenderRequested = false;
                    SetState(AnyiPoliceDispatchState.SuspectFleeing);
                    return "The suspect ignored the surrender command and is fleeing.";
                }

                _incident.SurrenderRequested = false;
                SetState(AnyiPoliceDispatchState.SuspectCompliant);
                Notification.PostTicker("~g~SUSPECT COMPLIANT~s~\nHands are up.\n~c~Press Secure again to place the suspect under arrest.", false, false);
                LspdResponseLog.Write("POLICE_SUSPECT_SURRENDER", "Suspect reached compliant state | Ped=" + _incident.Suspect.Handle);
            }

            if (_incident.State == AnyiPoliceDispatchState.SuspectCompliant &&
                _incident.Suspect != null &&
                _incident.Suspect.Exists() &&
                !_incident.Suspect.IsDead &&
                now >= _lastSurrenderRefresh)
            {
                try
                {
                    _incident.Suspect.BlockPermanentEvents = true;
                    _incident.Suspect.CanSwitchWeapons = false;
                    _incident.Suspect.Task.HandsUp(4000);
                    _lastSurrenderRefresh = now.AddSeconds(2);
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "POLICE_SUSPECT_HANDS_UP_REFRESH_ERROR",
                        ex);
                }
            }

            return null;
        }

        public void AttachSuspect(Ped suspect, bool ownedByDispatch)
        {
            if (_incident == null || suspect == null || !suspect.Exists()) return;
            _incident.Suspect = suspect;
            _incident.OwnedByDispatch = ownedByDispatch;
            CleanupSuspectBlip();
            LspdResponseLog.Write("POLICE_DISPATCH", "Suspect attached | Ped=" + suspect.Handle + " | Model=" + suspect.Model.Hash + " | Owned=" + ownedByDispatch);
        }

        public string CompleteTransportSuccess()
        {
            if (_incident == null)
                return "No active dispatch remains.";

            if (!IsTransportLifecycleState(_incident.State))
            {
                LspdResponseLog.Write(
                    "POLICE_DISPATCH_GUARD",
                    "IGNORED transport success outside custody lifecycle | State=" +
                    _incident.State +
                    " | Title=" + _incident.Title);
                return "Transport success was ignored because the dispatch is not in a custody/transport state.";
            }

            CompleteSuccessful(
                "Prisoner transferred, delivered and booked successfully.",
                _config.TransportSuccessAudioCategories);

            return "Transport success. Dispatch completed automatically.";
        }

        public void Complete()
        {
            string successAudio =
                string.IsNullOrWhiteSpace(_config.DispatchSuccessAudioCategories)
                    ? "CASE_CLOSED"
                    : _config.DispatchSuccessAudioCategories;

            CompleteSuccessful("Dispatch completed.", successAudio);
        }

        public void CompleteSuccessful(string reason, string audioCategories)
        {
            if (_incident == null) return;

            // A previous prison/convoy completion must never be consumed by a new
            // Offered/EnRoute/OnScene dispatch. Only a dispatch that is currently
            // in the custody/transport lifecycle may consume a transport-success
            // completion message.
            if (IsTransportCompletionReason(reason) &&
                !IsTransportLifecycleState(_incident.State))
            {
                LspdResponseLog.Write(
                    "POLICE_DISPATCH_GUARD",
                    "IGNORED stale transport completion | State=" +
                    _incident.State +
                    " | Title=" +
                    _incident.Title +
                    " | Reason=" +
                    reason);

                return;
            }

            AnyiPoliceIncident incident = _incident;
            SetState(AnyiPoliceDispatchState.Completed);
            _audio.PlayFirstAvailable(audioCategories, true);
            QueueOwnedIncidentCleanup(incident);
            CleanupBlip();
            CleanupSuspectBlip();
            RestorePreviousWaypoint();
            _cooldownUntil = DateTime.UtcNow.AddSeconds(_config.DispatchCooldownSeconds);
            if (_assignedUnit != null)
                _response.ReleaseUnit(_assignedUnit);
            _assignedUnit = null;
            _deadSuspectSceneAcknowledged = false;
            _responseUnitArrivalAcknowledged = false;
            _incident = null;
            if (EnableDispatchScreenNotifications)
            {
                Notification.PostTicker(
                    "~g~POLICE DISPATCH SUCCESS~s~\\n" +
                    (reason ?? "Dispatch completed.") +
                    "\\n~c~Cooldown started.",
                    false,
                    false);
            }
            LspdResponseLog.Write("POLICE_DISPATCH", "COMPLETED | " + incident.Title + " | Reason=" + reason);
        }

        public void Fail(string reason)
        {
            if (_incident == null) return;

            // Regression protection for the 10:00 PM build:
            // the convoy subsystem can retain a terminal "Compromised" flag after
            // a previous prisoner transfer fails. A stale callback must never turn
            // a brand-new dispatch (Offered/Accepted/EnRoute/OnScene/etc.) into
            // FAILED with "Prisoner transport was compromised".
            //
            // Transport failures are valid only after the current incident has
            // actually entered the custody/transport lifecycle.
            if (IsTransportCompromiseReason(reason) && !IsTransportLifecycleState(_incident.State))
            {
                LspdResponseLog.Write(
                    "POLICE_DISPATCH_GUARD",
                    "IGNORED stale transport failure | State=" + _incident.State +
                    " | Title=" + _incident.Title +
                    " | Reason=" + reason);

                return;
            }

            AnyiPoliceIncident incident = _incident;
            SetState(AnyiPoliceDispatchState.Failed);
            CleanupOwnedIncident(incident);
            CleanupBlip();
            CleanupSuspectBlip();
            RestorePreviousWaypoint();
            _cooldownUntil = DateTime.UtcNow.AddSeconds(_config.DispatchCooldownSeconds);
            if (_assignedUnit != null)
                _response.ReleaseUnit(_assignedUnit);
            _assignedUnit = null;
            _incident = null;
            LspdResponseLog.Write("POLICE_DISPATCH", "FAILED | " + reason);
        }

        private static bool IsTransportCompletionReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            return reason.IndexOf("prisoner transferred", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("delivered and booked", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   reason.IndexOf("prison transfer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTransportLifecycleState(AnyiPoliceDispatchState state)
        {
            string name = state.ToString();

            // Use names instead of assuming a particular enum ordering or
            // requiring enum members that may differ between synchronized files.
            return name.Equals("Arrested", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("AwaitingTransport", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("PickupEnRoute", StringComparison.OrdinalIgnoreCase) ||
                   name.IndexOf("Transport", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.Equals("PrisonArrival", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Booking", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransportCompromiseReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return false;

            return reason.IndexOf("transport", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   reason.IndexOf("compromised", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void StartSceneBehavior(Ped player)
        {
            if (_incident == null || _incident.Suspect == null || !_incident.Suspect.Exists() || _incident.Suspect.IsDead)
                return;
            if (_incident.SuspectBehaviorInitialized)
                return;

            try
            {
                _incident.SuspectBehaviorInitialized = true;
                _incident.Suspect.BlockPermanentEvents = true;
                _incident.Suspect.CanSwitchWeapons = false;

                if (_incident.Type == AnyiPoliceIncidentType.GangAmbush ||
                    _incident.Type == AnyiPoliceIncidentType.MassShootout ||
                    _incident.Type == AnyiPoliceIncidentType.OfficerDistress ||
                    _incident.Severity >= 4f)
                {
                    _incident.Suspect.Task.Combat(player);
                    SetState(AnyiPoliceDispatchState.SuspectResisting);
                }
                else if (_incident.Type == AnyiPoliceIncidentType.PedestrianPursuit ||
                         _incident.Type == AnyiPoliceIncidentType.Kidnapping ||
                         _incident.Type == AnyiPoliceIncidentType.Hijacking ||
                         _incident.Type == AnyiPoliceIncidentType.VehiclePursuit ||
                         _incident.Type == AnyiPoliceIncidentType.RecklessDriver)
                {
                    _incident.Suspect.Task.ReactAndFlee(player);
                    SetState(AnyiPoliceDispatchState.SuspectFleeing);
                }
                else
                {
                    // Calm / investigative subjects must remain part of the GTA world.
                    // Do not freeze them as a permanent scripted statue.
                    _incident.Suspect.BlockPermanentEvents = false;
                    _incident.Suspect.CanSwitchWeapons = true;
                    _incident.Suspect.Task.LookAt(player, 2500);
                    SetState(AnyiPoliceDispatchState.Investigating);
                }

                LspdResponseLog.Write(
                    "POLICE_SCENE_BEHAVIOR",
                    "Scene behavior initialized | Type=" + _incident.Type +
                    " | Suspect=" + _incident.Suspect.Handle +
                    " | State=" + _incident.State);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_SCENE_BEHAVIOR_ERROR", ex);
            }
        }

        private static bool IsPursuitIncidentState(AnyiPoliceDispatchState state)
        {
            return state == AnyiPoliceDispatchState.SuspectFleeing ||
                   state == AnyiPoliceDispatchState.SuspectResisting;
        }

        private static bool IsMobilePursuitType(AnyiPoliceIncidentType type)
        {
            return type == AnyiPoliceIncidentType.PedestrianPursuit ||
                   type == AnyiPoliceIncidentType.VehiclePursuit ||
                   type == AnyiPoliceIncidentType.RecklessDriver ||
                   type == AnyiPoliceIncidentType.Hijacking ||
                   type == AnyiPoliceIncidentType.Kidnapping;
        }

        public bool IsTransportState
        {
            get
            {
                return _incident != null && IsTransportLifecycleState(_incident.State);
            }
        }

        private void SetState(AnyiPoliceDispatchState state)
        {
            if (_incident == null || _incident.State == state) return;
            _incident.State = state;
            _incident.StateChangedAt = DateTime.UtcNow;
            LspdResponseLog.Write("POLICE_DISPATCH_STATE", "State=" + state + " | Type=" + _incident.Type + " | Title=" + _incident.Title);
        }

        private void CaptureAndSetDispatchWaypoint(Vector3 position)
        {
            try
            {
                if (!_hadPreviousWaypoint && Game.IsWaypointActive)
                {
                    _previousWaypoint = World.WaypointPosition;
                    _hadPreviousWaypoint = true;
                }

                World.WaypointPosition = position;
                LspdResponseLog.Write("POLICE_DISPATCH_GPS", "Waypoint set | X=" + position.X + " | Y=" + position.Y);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_DISPATCH_GPS_ERROR", ex);
            }
        }

        private void RestorePreviousWaypoint()
        {
            try
            {
                if (_hadPreviousWaypoint)
                {
                    World.WaypointPosition = _previousWaypoint;
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

        private void SetBlip(Vector3 position, string title)
        {
            CleanupBlip();
            try
            {
                _blip = World.CreateBlip(position);
                _blip.Name = title;
                _blip.IsShortRange = false;
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_DISPATCH_BLIP_ERROR", ex);
            }
        }

        private void CleanupBlip()
        {
            try
            {
                if (_blip != null && _blip.Exists()) _blip.Delete();
            }
            catch { }
            _blip = null;
        }

        private void UpdateSuspectTracking(Ped player)
        {
            if (_incident == null)
            {
                CleanupSuspectBlip();
                return;
            }

            Ped suspect = _incident.Suspect;
            if (suspect == null || !suspect.Exists() || suspect.IsDead)
            {
                CleanupSuspectBlip();
                return;
            }

            bool trackingState =
                _incident.State == AnyiPoliceDispatchState.SuspectFleeing ||
                _incident.State == AnyiPoliceDispatchState.SuspectResisting;

            if (!trackingState)
            {
                CleanupSuspectBlip();
                return;
            }

            try
            {
                if (_suspectBlip == null || !_suspectBlip.Exists())
                {
                    _suspectBlip = suspect.AddBlip();
                    if (_suspectBlip != null && _suspectBlip.Exists())
                    {
                        _suspectBlip.Name = "Dispatch Suspect";
                        _suspectBlip.IsShortRange = false;
                    }
                }

                if (_suspectBlip != null && _suspectBlip.Exists())
                    _suspectBlip.Position = suspect.Position;

                // Do not fight the player's navigation every frame. During a pursuit,
                // update the waypoint gently so the GPS follows the actual suspect.
                if (_incident.State == AnyiPoliceDispatchState.SuspectFleeing &&
                    DateTime.UtcNow >= _lastSuspectGpsUpdate)
                {
                    _lastSuspectGpsUpdate = DateTime.UtcNow.AddSeconds(1.5);

                    if (player != null && player.Exists())
                        World.WaypointPosition = suspect.Position;
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_DISPATCH_SUSPECT_TRACKING_ERROR", ex);
            }
        }

        private void CleanupSuspectBlip()
        {
            try
            {
                if (_suspectBlip != null && _suspectBlip.Exists())
                    _suspectBlip.Delete();
            }
            catch { }

            _suspectBlip = null;
        }

        private void QueueOwnedIncidentCleanup(AnyiPoliceIncident incident)
        {
            if (incident == null || !incident.OwnedByDispatch)
                return;

            DateTime now = DateTime.UtcNow;
            DateTime earliest = now.AddSeconds(_config.CompletedEntityCleanupGraceSeconds);
            DateTime expires = now.AddSeconds(_config.CompletedEntityCleanupMaxSeconds);

            if (incident.Suspect != null && incident.Suspect.Exists())
            {
                try { incident.Suspect.IsPersistent = true; incident.Suspect.BlockPermanentEvents = false; } catch { }
                _deferredCleanup.Add(new DeferredEntityCleanup { Ped = incident.Suspect, Earliest = earliest, Expires = expires });
            }
            if (incident.Victim != null && incident.Victim.Exists())
            {
                try { incident.Victim.IsPersistent = true; } catch { }
                _deferredCleanup.Add(new DeferredEntityCleanup { Ped = incident.Victim, Earliest = earliest, Expires = expires });
            }
            if (incident.SuspectVehicle != null && incident.SuspectVehicle.Exists())
            {
                try { incident.SuspectVehicle.IsPersistent = true; } catch { }
                _deferredCleanup.Add(new DeferredEntityCleanup { Vehicle = incident.SuspectVehicle, Earliest = earliest, Expires = expires });
            }

            incident.Suspect = null;
            incident.Victim = null;
            incident.SuspectVehicle = null;
        }

        private void ProcessDeferredCleanup(DateTime now, Ped player)
        {
            if (_deferredCleanup.Count == 0) return;

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

                if (now >= item.Earliest && (distance >= _config.CompletedEntityCleanupDistance || now >= item.Expires))
                {
                    try { if (item.Ped != null && item.Ped.Exists()) item.Ped.Delete(); } catch { }
                    try { if (item.Vehicle != null && item.Vehicle.Exists()) item.Vehicle.Delete(); } catch { }
                    _deferredCleanup.Remove(item);
                }
            }
        }

        private void CleanupOwnedIncident(AnyiPoliceIncident incident)
        {
            if (incident == null || !incident.OwnedByDispatch) return;
            try
            {
                if (incident.Suspect != null && incident.Suspect.Exists())
                {
                    incident.Suspect.IsPersistent = false;
                    incident.Suspect.Delete();
                }
            }
            catch { }
            try
            {
                if (incident.Victim != null && incident.Victim.Exists())
                {
                    incident.Victim.IsPersistent = false;
                    incident.Victim.Delete();
                }
            }
            catch { }
            incident.Suspect = null;
            incident.Victim = null;
            incident.SuspectVehicle = null;
        }

        private void PlayDispatchAudioForOffer(AnyiPoliceIncident incident)
        {
            if (incident == null)
                return;

            if (!string.IsNullOrWhiteSpace(incident.AudioCategory))
            {
                if (_audio.Play(incident.AudioCategory))
                    return;
            }

            switch (incident.Type)
            {
                case AnyiPoliceIncidentType.MassShootout:
                case AnyiPoliceIncidentType.GangAmbush:
                case AnyiPoliceIncidentType.ArmsDealing:
                case AnyiPoliceIncidentType.WeaponSmuggling:
                    _audio.Play("CRIME_SHOTS_FIRED");
                    break;
                case AnyiPoliceIncidentType.Kidnapping:
                case AnyiPoliceIncidentType.Hijacking:
                case AnyiPoliceIncidentType.VehiclePursuit:
                case AnyiPoliceIncidentType.RecklessDriver:
                    _audio.Play("REQUEST_BACKUP");
                    break;
                case AnyiPoliceIncidentType.PedestrianPursuit:
                    _audio.Play("REPORT_SUSPECT_IS_ON_FOOT");
                    break;
                default:
                    _audio.Play("ATTENTION_ALL_UNITS");
                    break;
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
            if (_incident != null)
            {
                CleanupOwnedIncident(_incident);
                _incident = null;
            }
            CleanupBlip();
            CleanupSuspectBlip();
            RestorePreviousWaypoint();
            if (_assignedUnit != null) _response.ReleaseUnit(_assignedUnit);
            _assignedUnit = null;
            _cooldownUntil = DateTime.MinValue;
            _lastStateCheck = DateTime.MinValue;
            _lastSurrenderRefresh = DateTime.MinValue;
            _deadSuspectSceneAcknowledged = false;
            _responseUnitArrivalAcknowledged = false;
        }
    }
}

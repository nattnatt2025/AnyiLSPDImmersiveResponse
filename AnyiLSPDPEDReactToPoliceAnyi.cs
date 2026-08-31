using GTA;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;

namespace AnyiLSPD
{
    /// <summary>
    /// Anyi LSPD Police Authority NPC interaction/reaction overhaul.
    ///
    /// Design:
    /// - Police-authority civilian interaction is explicitly player-triggered by G.
    /// - G performs a fresh local scan instead of depending only on Core's cached
    ///   _nearby array.
    /// - One pedestrian/one traffic driver is selected at a time.
    /// - GangData/MemberPool gang models remain excluded and are never rewritten.
    /// - Active Police scenes create a small, local civilian safety reaction rather
    ///   than making the whole world ignore Officer Anyi.
    /// - Ordinary patrol traffic remains vanilla except for a short collision guard
    ///   on a single nearby vehicle that is about to hit the officer.
    /// - No Dispatch/Convoy/Chaos lifecycle code is modified by this class.
    /// </summary>
    public sealed class AnyiLSPDPEDReactToPoliceAnyi
    {
        private enum InteractionStage
        {
            None,
            CitizenPreparing,
            CitizenGreeting,
            CitizenDocuments,
            CitizenRefused,
            TrafficPreparing,
            TrafficDocuments,
            TrafficRefused
        }

        private static readonly Random Random = new Random();

        private readonly Dictionary<int, DateTime> _reactionCooldown =
            new Dictionary<int, DateTime>();

        private InteractionStage _stage = InteractionStage.None;
        private Ped _targetPed;
        private Vehicle _targetVehicle;

        private DateTime _stageReadyAt = DateTime.MinValue;
        private DateTime _expiresAt = DateTime.MinValue;
        private DateTime _lastReactionScan = DateTime.MinValue;

        private Ped _approachCandidate;
        private DateTime _candidateUntil = DateTime.MinValue;

        private Vehicle _guardedVehicle;
        private DateTime _guardedVehicleUntil = DateTime.MinValue;

        public bool HasActiveInteraction
        {
            get
            {
                return _stage != InteractionStage.None &&
                       _targetPed != null &&
                       _targetPed.Exists();
            }
        }

        public void Update(
            Ped player,
            Ped[] nearby,
            AnyiLSPDPoliceConfig config,
            AnyiLSPDPoliceData.GangSnapshot gangData,
            bool activeScene)
        {
            if (player == null || !player.Exists() || config == null || nearby == null)
                return;

            DateTime now = DateTime.UtcNow;

            MaintainInteraction(player, config, now);
            MaintainApproachCandidate(player, nearby, config, gangData, now);
            MaintainCollisionGuard(player, now);

            if (!config.EnableNpcReaction)
                return;

            if (now < _lastReactionScan.AddMilliseconds(
                    Math.Max(700, config.ReactionScanMs)))
                return;

            _lastReactionScan = now;

            int scenePedBudget = activeScene ? 4 : 0;
            bool safetyActionUsed = false;

            foreach (Ped ped in nearby)
            {
                if (ped == null || !ped.Exists() || ped.IsDead ||
                    !ped.IsHuman || ped.Handle == player.Handle)
                    continue;

                if (_targetPed != null && _targetPed.Exists() &&
                    ped.Handle == _targetPed.Handle)
                    continue;

                if (IsPolice(ped) || IsGangExcluded(ped, gangData) ||
                    IsPotentialGangThreat(ped, gangData))
                    continue;

                float distance = ped.Position.DistanceTo(player.Position);
                if (distance > Math.Max(18f, config.NpcActiveSceneRadius))
                    continue;

                DateTime previous;
                if (_reactionCooldown.TryGetValue(ped.Handle, out previous) &&
                    now < previous.AddSeconds(
                        Math.Max(2, config.NpcReactionCooldownSeconds)))
                    continue;

                try
                {
                    Vehicle vehicle = ped.CurrentVehicle;
                    bool isDriver = vehicle != null &&
                                    vehicle.Exists() &&
                                    IsDriverOf(ped, vehicle);

                    if (!activeScene &&
                        !safetyActionUsed &&
                        config.EnableTrafficCollisionAvoidance &&
                        isDriver &&
                        distance <= Math.Min(
                            14f,
                            Math.Max(8f, config.NpcTrafficSlowRadius)) &&
                        Math.Abs(vehicle.Speed) > 2.0f)
                    {
                        ApplyLocalTrafficSafety(player, ped, vehicle, now);
                        _reactionCooldown[ped.Handle] = now;
                        safetyActionUsed = true;
                        continue;
                    }

                    if (activeScene &&
                        config.EnableActiveSceneCivilianFlee &&
                        scenePedBudget > 0 &&
                        distance <= config.NpcFleeReactionRadius)
                    {
                        int fleeRoll = Random.Next(100);

                        if (player.IsAiming && fleeRoll < 70)
                        {
                            ped.Task.ReactAndFlee(player);
                        }
                        else if (fleeRoll < 35)
                        {
                            ped.Task.ReactAndFlee(player);
                        }
                        else
                        {
                            SafeLookAt(ped, player, 1200);
                        }

                        _reactionCooldown[ped.Handle] = now;
                        scenePedBudget--;
                        continue;
                    }

                    // Outside a live Police scene, do not globally rewrite civilian AI.
                    // A nearby ordinary pedestrian may notice the officer naturally.
                    if (!activeScene && distance <= 8f)
                    {
                        SafeLookAt(ped, player, 600);
                        _reactionCooldown[ped.Handle] = now;
                    }
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "POLICE_CIVILIAN_REACTION_ERROR", ex);
                }
            }

            if (_reactionCooldown.Count > 700)
                TrimReactionCooldown(now);
        }

        private void MaintainApproachCandidate(
            Ped player,
            Ped[] nearby,
            AnyiLSPDPoliceConfig config,
            AnyiLSPDPoliceData.GangSnapshot gangData,
            DateTime now)
        {
            if (HasActiveInteraction ||
                player == null ||
                !player.Exists() ||
                player.CurrentVehicle != null ||
                nearby == null)
                return;

            Ped best = null;
            float bestDistance = Math.Max(12f,
                Math.Min(18f, config.InteractionRadius + 7f));

            foreach (Ped ped in nearby)
            {
                if (ped == null || !ped.Exists() || ped.IsDead ||
                    !ped.IsHuman || ped.Handle == player.Handle)
                    continue;

                if (ped.IsInVehicle() || IsPolice(ped) || IsGangExcluded(ped, gangData))
                    continue;

                float distance = ped.Position.DistanceTo(player.Position);
                if (distance > bestDistance)
                    continue;

                best = ped;
                bestDistance = distance;
            }

            if (best == null)
            {
                if (_approachCandidate != null && _approachCandidate.Exists())
                {
                    try { _approachCandidate.BlockPermanentEvents = false; }
                    catch { }
                }

                _approachCandidate = null;
                _candidateUntil = DateTime.MinValue;
                return;
            }

            bool newCandidate =
                _approachCandidate == null ||
                !_approachCandidate.Exists() ||
                _approachCandidate.Handle != best.Handle;

            if (newCandidate)
            {
                _approachCandidate = best;
                _candidateUntil = now.AddSeconds(8);

                // Do not turn the civilian into a statue for the whole patrol.
                // We briefly acknowledge the approaching officer, then the
                // player-triggered G interaction owns the subject.
                try
                {
                    best.Task.LookAt(player, 1200);
                }
                catch { }

                LspdResponseLog.Write(
                    "POLICE_CIVILIAN_APPROACH",
                    "Approach candidate acquired | Ped=" +
                    best.Handle +
                    " | Distance=" +
                    bestDistance.ToString("0.0"));
            }
            else if (now >= _candidateUntil)
            {
                try { _approachCandidate.BlockPermanentEvents = false; }
                catch { }

                _approachCandidate = null;
                _candidateUntil = DateTime.MinValue;
            }
        }

        private void MaintainInteraction(
            Ped player,
            AnyiLSPDPoliceConfig config,
            DateTime now)
        {
            if (_stage == InteractionStage.None)
                return;

            if (_targetPed == null || !_targetPed.Exists() || _targetPed.IsDead)
            {
                ResetInteraction();
                return;
            }

            if (_expiresAt != DateTime.MinValue && now >= _expiresAt)
            {
                ReleaseCurrentSubject();
                ResetInteraction();

                Notification.PostTicker(
                    "~b~ANYI LSPD~s~\nPOLICE NPC INTERACTION\n~c~Contact timed out. The civilian was released.",
                    false,
                    false);
                return;
            }

            if (_stage == InteractionStage.CitizenPreparing &&
                now >= _stageReadyAt)
            {
                BeginCitizenGreeting(player, config, now);
            }
            else if (_stage == InteractionStage.CitizenGreeting &&
                     now >= _stageReadyAt)
            {
                BeginCitizenDocuments(player, config, now);
            }
            else if (_stage == InteractionStage.TrafficPreparing &&
                     now >= _stageReadyAt)
            {
                BeginTrafficDocuments(player, config, now);
            }
        }

        private void BeginCitizenGreeting(
            Ped player,
            AnyiLSPDPoliceConfig config,
            DateTime now)
        {
            _stage = InteractionStage.CitizenGreeting;
            _stageReadyAt = now.AddMilliseconds(850);

            try
            {
                _targetPed.Task.ClearAll();
                _targetPed.BlockPermanentEvents = true;
                SafeLookAt(_targetPed, player, 1600);
                PlayGreetingAnimation(_targetPed);
            }
            catch { }

            LspdResponseLog.Write(
                "POLICE_CIVILIAN_INTERACTION",
                "Citizen greeting phase | Ped=" + _targetPed.Handle);

            Notification.PostTicker(
                "~b~ANYI LSPD~s~\nCITIZEN CONTACT\n~c~Citizen acknowledged Officer Anyi and is preparing identification.",
                false,
                false);
        }

        private void BeginCitizenDocuments(
            Ped player,
            AnyiLSPDPoliceConfig config,
            DateTime now)
        {
            _stage = InteractionStage.CitizenDocuments;
            _stageReadyAt = now.AddSeconds(
                Math.Max(1, Math.Min(6, config.NpcDocumentPresentationSeconds)));

            try
            {
                _targetPed.Task.ClearAll();
                _targetPed.BlockPermanentEvents = true;
                SafeLookAt(_targetPed, player, 1800);
                PlayPaperAnimation(_targetPed);
            }
            catch { }

            LspdResponseLog.Write(
                "POLICE_CIVILIAN_INTERACTION",
                "Citizen entered document presentation | Ped=" +
                _targetPed.Handle);

            Notification.PostTicker(
                "~b~ANYI LSPD~s~\nCITIZEN CONTACT\n~c~Citizen has stopped and is preparing identification.",
                false,
                false);
        }

        private void BeginTrafficDocuments(
            Ped player,
            AnyiLSPDPoliceConfig config,
            DateTime now)
        {
            _stage = InteractionStage.TrafficDocuments;
            _stageReadyAt = now.AddSeconds(
                Math.Max(1, Math.Min(6, config.NpcDocumentPresentationSeconds)));

            try
            {
                _targetPed.BlockPermanentEvents = true;
                SafeLookAt(_targetPed, player, 1800);
                PlayPaperAnimation(_targetPed);
            }
            catch { }

            LspdResponseLog.Write(
                "POLICE_TRAFFIC_STOP",
                "Driver entered document presentation | Driver=" +
                _targetPed.Handle +
                " | Vehicle=" +
                (_targetVehicle == null ? 0 : _targetVehicle.Handle));

            Notification.PostTicker(
                "~b~ANYI LSPD~s~\nTRAFFIC STOP\n~c~Driver remains seated and is preparing identification. Y = Clear / N = Disagree.",
                false,
                false);
        }

        public string InteractNearest(
            Ped player,
            Ped[] nearby,
            int radius,
            AnyiLSPDPoliceConfig config,
            AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            if (player == null || !player.Exists())
                return "No Police interaction target is available.";

            if (config == null)
                return "Police interaction configuration is unavailable.";

            if (HasActiveInteraction)
            {
                switch (_stage)
                {
                    case InteractionStage.CitizenPreparing:
                        return "Citizen contact is being established.";
                    case InteractionStage.CitizenGreeting:
                        return "Citizen noticed Officer Anyi and is greeting before presenting identification.";
                    case InteractionStage.CitizenDocuments:
                        return "Citizen contact is active. Y = Clear / N = Disagree.";
                    case InteractionStage.CitizenRefused:
                        return "Citizen refused. Y = Release / N = Pursue.";
                    case InteractionStage.TrafficPreparing:
                        return "Driver contact is being established.";
                    case InteractionStage.TrafficDocuments:
                        return "Traffic stop is active. Driver remains seated. Y = Clear / N = Disagree.";
                    case InteractionStage.TrafficRefused:
                        return "Driver refused. Y = Release / N = Pursue.";
                }
            }

            bool playerInVehicle =
                player.CurrentVehicle != null &&
                player.CurrentVehicle.Exists();

            float requestedRadius = Math.Max(3f, radius);
            float effectiveRadius = playerInVehicle
                ? Math.Max(
                    20f,
                    Math.Min(55f, Math.Max(25f, config.TrafficStopSearchRadius + 15)))
                : Math.Max(
                    14f,
                    Math.Min(25f, requestedRadius + 10f));

            // Critical fix:
            // G performs its own fresh scan. The interaction path must never depend
            // solely on Core's periodic _nearby cache.
            Dictionary<int, Ped> candidates =
                CollectFreshPedCandidates(player, nearby, effectiveRadius);

            int scanned = 0;
            int gangExcluded = 0;
            int policeExcluded = 0;
            int distanceRejected = 0;
            int typeRejected = 0;
            int eligiblePedCandidates = 0;
            int driverCandidates = 0;

            Ped bestPed = null;
            Vehicle bestVehicle = null;
            float bestScore = float.MaxValue;

            foreach (Ped ped in candidates.Values)
            {
                scanned++;

                if (ped == null || !ped.Exists() || ped.IsDead ||
                    !ped.IsHuman || ped.Handle == player.Handle)
                {
                    typeRejected++;
                    continue;
                }

                if (IsPolice(ped))
                {
                    policeExcluded++;
                    continue;
                }

                if (IsGangExcluded(ped, gangData))
                {
                    gangExcluded++;
                    continue;
                }

                float distance = ped.Position.DistanceTo(player.Position);
                if (distance > effectiveRadius)
                {
                    distanceRejected++;
                    continue;
                }

                if (playerInVehicle)
                {
                    Vehicle vehicle = ped.CurrentVehicle;
                    if (vehicle == null || !vehicle.Exists() || !IsDriverOf(ped, vehicle))
                    {
                        typeRejected++;
                        continue;
                    }

                    driverCandidates++;

                    // Prefer a stopped/slowing driver close to the officer.
                    float speedPenalty =
                        Math.Min(6f, Math.Abs(vehicle.Speed) * 0.10f);
                    float score = distance + speedPenalty;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPed = ped;
                        bestVehicle = vehicle;
                    }
                }
                else
                {
                    if (ped.IsInVehicle())
                    {
                        typeRejected++;
                        continue;
                    }

                    eligiblePedCandidates++;

                    // Closest civilian wins. No requirement that the civilian
                    // be standing perfectly still; this is a contact interaction,
                    // not a static target detector.
                    if (distance < bestScore)
                    {
                        bestScore = distance;
                        bestPed = ped;
                        bestVehicle = null;
                    }
                }
            }

            // Fallback: if the cached/fresh ped merge found no traffic driver,
            // perform a second, slightly larger pass. This is still local and
            // does not create or freeze traffic.
            if (playerInVehicle && bestPed == null)
            {
                TryFindNearestDriverWithExpandedScan(
                    player,
                    gangData,
                    Math.Min(70f, effectiveRadius + 15f),
                    ref scanned,
                    ref gangExcluded,
                    ref policeExcluded,
                    ref distanceRejected,
                    ref typeRejected,
                    ref driverCandidates,
                    ref bestPed,
                    ref bestVehicle,
                    ref bestScore);
            }

            LspdResponseLog.Write(
                "POLICE_CIVILIAN_INTERACTION_SCAN",
                "G=" + config.NPCInteractionKey +
                " | PlayerInVehicle=" + playerInVehicle +
                " | EffectiveRadius=" + effectiveRadius.ToString("0.0") +
                " | Scanned=" + scanned +
                " | GangExcluded=" + gangExcluded +
                " | PoliceExcluded=" + policeExcluded +
                " | DistanceRejected=" + distanceRejected +
                " | TypeRejected=" + typeRejected +
                " | EligiblePedCandidates=" + eligiblePedCandidates +
                " | DriverCandidates=" + driverCandidates +
                " | Target=" +
                (bestPed == null ? "none" : bestPed.Handle.ToString()) +
                " | TargetDistance=" +
                (bestPed == null
                    ? "-"
                    : bestPed.Position.DistanceTo(player.Position).ToString("0.0")));

            if (bestPed == null)
            {
                string message = playerInVehicle
                    ? "No nearby civilian driver was found. Move closer to one stopped by your Police vehicle and press G."
                    : "No nearby ordinary civilian was found. Stand within roughly 14–25 m and press G.";

                Notification.PostTicker(
                    "~b~ANYI LSPD~s~\nPOLICE NPC INTERACTION\n~c~" + message,
                    false,
                    false);

                return message;
            }

            if (bestVehicle != null && bestVehicle.Exists())
            {
                BeginTrafficInteraction(bestPed, bestVehicle, player, config);
                return "Traffic stop initiated. The driver remains seated for Police contact.";
            }

            BeginCitizenInteraction(bestPed, player, config);
            return "Citizen contact initiated. The civilian has stopped to speak with Officer Anyi.";
        }

        private Dictionary<int, Ped> CollectFreshPedCandidates(
            Ped player,
            Ped[] cachedNearby,
            float radius)
        {
            Dictionary<int, Ped> result =
                new Dictionary<int, Ped>();

            try
            {
                if (cachedNearby != null)
                {
                    foreach (Ped ped in cachedNearby)
                    {
                        if (ped != null && ped.Exists())
                            result[ped.Handle] = ped;
                    }
                }

                Ped[] fresh = World.GetNearbyPeds(
                    player,
                    Math.Max(25f, radius + 10f));

                if (fresh != null)
                {
                    foreach (Ped ped in fresh)
                    {
                        if (ped != null && ped.Exists())
                            result[ped.Handle] = ped;
                    }
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_CIVILIAN_INTERACTION_SCAN_ERROR", ex);
            }

            return result;
        }

        private void TryFindNearestDriverWithExpandedScan(
            Ped player,
            AnyiLSPDPoliceData.GangSnapshot gangData,
            float radius,
            ref int scanned,
            ref int gangExcluded,
            ref int policeExcluded,
            ref int distanceRejected,
            ref int typeRejected,
            ref int driverCandidates,
            ref Ped bestPed,
            ref Vehicle bestVehicle,
            ref float bestScore)
        {
            try
            {
                Ped[] fresh = World.GetNearbyPeds(player, radius);
                if (fresh == null)
                    return;

                foreach (Ped ped in fresh)
                {
                    if (ped == null || !ped.Exists())
                        continue;

                    scanned++;

                    if (ped.IsDead || !ped.IsHuman ||
                        ped.Handle == player.Handle)
                    {
                        typeRejected++;
                        continue;
                    }

                    if (IsPolice(ped))
                    {
                        policeExcluded++;
                        continue;
                    }

                    if (IsGangExcluded(ped, gangData))
                    {
                        gangExcluded++;
                        continue;
                    }

                    Vehicle vehicle = ped.CurrentVehicle;
                    if (vehicle == null || !vehicle.Exists() ||
                        !IsDriverOf(ped, vehicle))
                    {
                        typeRejected++;
                        continue;
                    }

                    float distance = ped.Position.DistanceTo(player.Position);
                    if (distance > radius)
                    {
                        distanceRejected++;
                        continue;
                    }

                    driverCandidates++;

                    float score =
                        distance +
                        Math.Min(6f, Math.Abs(vehicle.Speed) * 0.10f);

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestPed = ped;
                        bestVehicle = vehicle;
                    }
                }
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_TRAFFIC_DRIVER_EXPANDED_SCAN_ERROR", ex);
            }
        }

        private void BeginCitizenInteraction(
            Ped ped,
            Ped player,
            AnyiLSPDPoliceConfig config)
        {
            _stage = InteractionStage.CitizenPreparing;
            _targetPed = ped;
            _targetVehicle = null;

            int prepSeconds = Math.Max(
                0,
                Math.Min(4, config.NpcInteractionPreparationSeconds));

            _stageReadyAt = DateTime.UtcNow.AddSeconds(prepSeconds);
            _expiresAt = DateTime.UtcNow.AddSeconds(
                Math.Max(20, config.NpcInteractionTimeoutSeconds));

            try
            {
                ped.BlockPermanentEvents = true;
                ped.Task.ClearAll();
                SafeLookAt(ped, player, 2000);
                ped.Task.StandStill(2600);

                if (prepSeconds == 0)
                    BeginCitizenDocuments(player, config, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_CIVILIAN_INTERACTION_START_ERROR", ex);
                ResetInteraction();
                return;
            }

            LspdResponseLog.Write(
                "POLICE_CIVILIAN_INTERACTION",
                "Citizen interaction STARTED | Ped=" +
                ped.Handle +
                " | PreparationSeconds=" +
                prepSeconds);

            Notification.PostTicker(
                "~b~ANYI LSPD~s~\nCITIZEN CONTACT\n~c~Citizen noticed Officer Anyi and stopped. Preparing identification...",
                false,
                false);
        }

        private void BeginTrafficInteraction(
            Ped driver,
            Vehicle vehicle,
            Ped player,
            AnyiLSPDPoliceConfig config)
        {
            _stage = InteractionStage.TrafficPreparing;
            _targetPed = driver;
            _targetVehicle = vehicle;

            _stageReadyAt = DateTime.UtcNow.AddSeconds(1.5);
            _expiresAt = DateTime.UtcNow.AddSeconds(
                Math.Max(20, config.NpcTrafficStopTimeoutSeconds));

            try
            {
                driver.BlockPermanentEvents = true;

                // Only this selected vehicle is held for the interaction.
                // No other civilian vehicle is modified.
                vehicle.Speed = 0f;
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, true);

                SafeLookAt(driver, player, 2200);

                LspdResponseLog.Write(
                    "POLICE_TRAFFIC_STOP",
                    "Traffic interaction STARTED | Driver=" +
                    driver.Handle +
                    " | Vehicle=" +
                    vehicle.Handle);

                Notification.PostTicker(
                    "~b~ANYI LSPD~s~\nTRAFFIC CONTACT\n~c~One driver selected. Driver remains seated; preparing identification...",
                    false,
                    false);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_TRAFFIC_STOP_ERROR", ex);
                ResetInteraction();
            }
        }

        public string AcceptInteraction(
            Ped player,
            AnyiLSPDPoliceConfig config)
        {
            if (!HasActiveInteraction)
                return "No Police civilian interaction is waiting for a response.";

            if (_stage == InteractionStage.CitizenPreparing ||
                _stage == InteractionStage.CitizenGreeting ||
                _stage == InteractionStage.TrafficPreparing)
                return "The subject is still preparing identification.";

            try
            {
                if (_stage == InteractionStage.CitizenDocuments ||
                    _stage == InteractionStage.CitizenRefused)
                {
                    int handle = _targetPed.Handle;

                    ReleaseCitizenNaturally();

                    LspdResponseLog.Write(
                        "POLICE_CIVILIAN_INTERACTION",
                        "Citizen CLEAR/RELEASE | Ped=" + handle);

                    Notification.PostTicker(
                        "~g~ANYI LSPD~s~\nCITIZEN COMPLIANT\n~c~Identification accepted. Citizen may continue naturally.",
                        false,
                        false);

                    ResetInteraction();
                    return "Citizen complied. Citizen was cleared and released naturally.";
                }

                if (_stage == InteractionStage.TrafficDocuments ||
                    _stage == InteractionStage.TrafficRefused)
                {
                    int handle = _targetPed.Handle;

                    ReleaseTrafficNaturally();

                    LspdResponseLog.Write(
                        "POLICE_TRAFFIC_STOP",
                        "Driver CLEAR/RELEASE | Driver=" + handle);

                    Notification.PostTicker(
                        "~g~ANYI LSPD~s~\nTRAFFIC STOP COMPLETE\n~c~Driver cleared and released naturally.",
                        false,
                        false);

                    ResetInteraction();
                    return "Driver complied. Traffic stop complete and driver released.";
                }

                return "The Police interaction is not ready for a decision.";
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_CIVILIAN_ACCEPT_ERROR", ex);
                ResetInteraction();
                return "The civilian response could not be completed safely.";
            }
        }

        public string RejectInteraction(
            Ped player,
            AnyiLSPDPoliceConfig config,
            out Ped pursuitPed,
            out Vehicle pursuitVehicle)
        {
            pursuitPed = null;
            pursuitVehicle = null;

            if (!HasActiveInteraction)
                return "No Police civilian interaction is waiting for a response.";

            if (_stage == InteractionStage.CitizenPreparing ||
                _stage == InteractionStage.CitizenGreeting ||
                _stage == InteractionStage.TrafficPreparing)
                return "The subject is still preparing identification.";

            try
            {
                bool traffic =
                    _targetVehicle != null &&
                    _targetVehicle.Exists();

                if (traffic)
                {
                    int fleeChance = Math.Max(
                        0,
                        Math.Min(100, config.TrafficStopFleeChancePercent));

                    bool fleeNow =
                        _stage == InteractionStage.TrafficRefused ||
                        Random.Next(100) < fleeChance;

                    if (fleeNow)
                    {
                        pursuitPed = _targetPed;
                        pursuitVehicle = _targetVehicle;

                        _targetPed.BlockPermanentEvents = true;
                        _targetPed.Task.VehicleChase(player);

                        LspdResponseLog.Write(
                            "POLICE_TRAFFIC_STOP",
                            "Driver FLED | Driver=" +
                            _targetPed.Handle +
                            " | Vehicle=" +
                            _targetVehicle.Handle);

                        Notification.PostTicker(
                            "~y~ANYI LSPD~s~\nDRIVER DISOBEYED\n~c~Driver fled. Pursuit initiated.",
                            false,
                            false);

                        ResetInteraction();
                        return "Driver refused Police contact and fled. Pursuit initiated.";
                    }

                    _stage = InteractionStage.TrafficRefused;

                    Notification.PostTicker(
                        "~y~ANYI LSPD~s~\nDRIVER DISOBEYED\n~c~Driver refused but remained stopped. Press N again to pursue or Y to release.",
                        false,
                        false);

                    LspdResponseLog.Write(
                        "POLICE_TRAFFIC_STOP",
                        "Driver REFUSED but remained stopped | Driver=" +
                        _targetPed.Handle);

                    return "Driver refused but remained stopped. Press N again to pursue.";
                }

                int citizenFleeChance = Math.Max(
                    0,
                    Math.Min(100, config.NpcCitizenFleeChancePercent));

                bool citizenFlees =
                    _stage == InteractionStage.CitizenRefused ||
                    Random.Next(100) < citizenFleeChance;

                if (citizenFlees)
                {
                    pursuitPed = _targetPed;

                    _targetPed.BlockPermanentEvents = true;
                    _targetPed.Task.ReactAndFlee(player);

                    LspdResponseLog.Write(
                        "POLICE_CIVILIAN_INTERACTION",
                        "Citizen FLED | Ped=" + _targetPed.Handle);

                    Notification.PostTicker(
                        "~y~ANYI LSPD~s~\nCITIZEN DISOBEYED\n~c~Citizen fled from Police contact. Pursuit initiated.",
                        false,
                        false);

                    ResetInteraction();
                    return "Citizen disobeyed and fled. Pursuit initiated.";
                }

                _stage = InteractionStage.CitizenRefused;

                _targetPed.BlockPermanentEvents = true;
                _targetPed.Task.HandsUp(7000);
                SafeLookAt(_targetPed, player, 1400);

                LspdResponseLog.Write(
                    "POLICE_CIVILIAN_INTERACTION",
                    "Citizen REFUSED but remained | Ped=" +
                    _targetPed.Handle);

                Notification.PostTicker(
                    "~y~ANYI LSPD~s~\nCITIZEN DISOBEYED\n~c~Citizen raised hands. Press N again to pursue or Y to release.",
                    false,
                    false);

                return "Citizen refused but remained at the scene with hands raised.";
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_CIVILIAN_REJECT_ERROR", ex);

                pursuitPed = null;
                pursuitVehicle = null;
                ResetInteraction();

                return "The civilian refusal response could not be assigned safely.";
            }
        }

        private void ReleaseCitizenNaturally()
        {
            if (_targetPed == null || !_targetPed.Exists())
                return;

            try
            {
                _targetPed.BlockPermanentEvents = false;
                _targetPed.Task.ClearAll();

                // The requested "normal citizen can leave naturally" behavior.
                Function.Call(
                    Hash.TASK_WANDER_STANDARD,
                    _targetPed,
                    10f,
                    10);
            }
            catch { }
        }

        private void ReleaseTrafficNaturally()
        {
            if (_targetVehicle != null && _targetVehicle.Exists())
            {
                try
                {
                    Function.Call(
                        Hash.SET_VEHICLE_HANDBRAKE,
                        _targetVehicle,
                        false);
                }
                catch { }
            }

            if (_targetPed != null && _targetPed.Exists())
            {
                try
                {
                    _targetPed.BlockPermanentEvents = false;
                    _targetPed.Task.ClearAll();

                    if (_targetVehicle != null && _targetVehicle.Exists())
                    {
                        Function.Call(
                            Hash.TASK_VEHICLE_DRIVE_WANDER,
                            _targetPed,
                            _targetVehicle,
                            12f,
                            786603);
                    }
                }
                catch { }
            }
        }

        private void ReleaseCurrentSubject()
        {
            if (_targetVehicle != null && _targetVehicle.Exists())
            {
                try
                {
                    Function.Call(
                        Hash.SET_VEHICLE_HANDBRAKE,
                        _targetVehicle,
                        false);
                }
                catch { }
            }

            if (_targetPed != null && _targetPed.Exists())
            {
                try
                {
                    _targetPed.BlockPermanentEvents = false;
                    _targetPed.Task.ClearAll();
                }
                catch { }
            }
        }

        private void ApplyLocalTrafficSafety(
            Ped player,
            Ped driver,
            Vehicle vehicle,
            DateTime now)
        {
            try
            {
                // Slow only one vehicle that is actually very close.
                vehicle.Speed = Math.Min(Math.Abs(vehicle.Speed), 2.0f);
                SafeLookAt(driver, player, 800);

                // Short exact-entity collision guard, then restore it.
                Function.Call(
                    (Hash)0xA53ED5520C07654A,
                    vehicle,
                    player,
                    true);

                _guardedVehicle = vehicle;
                _guardedVehicleUntil = now.AddSeconds(1.5);

                LspdResponseLog.Write(
                    "POLICE_CIVILIAN_TRAFFIC_SAFETY",
                    "Single-vehicle collision guard | Vehicle=" +
                    vehicle.Handle +
                    " | Driver=" +
                    driver.Handle);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException(
                    "POLICE_CIVILIAN_TRAFFIC_SAFETY_ERROR", ex);
            }
        }

        private void MaintainCollisionGuard(
            Ped player,
            DateTime now)
        {
            if (_guardedVehicle == null || !_guardedVehicle.Exists())
            {
                _guardedVehicle = null;
                _guardedVehicleUntil = DateTime.MinValue;
                return;
            }

            if (now < _guardedVehicleUntil)
                return;

            try
            {
                if (player != null && player.Exists())
                {
                    Function.Call(
                        (Hash)0xA53ED5520C07654A,
                        _guardedVehicle,
                        player,
                        false);
                }
            }
            catch { }

            _guardedVehicle = null;
            _guardedVehicleUntil = DateTime.MinValue;
        }

        private void ResetInteraction()
        {
            _stage = InteractionStage.None;
            _targetPed = null;
            _targetVehicle = null;
            _stageReadyAt = DateTime.MinValue;
            _expiresAt = DateTime.MinValue;
        }

        public void Reset()
        {
            try
            {
                if (_targetVehicle != null && _targetVehicle.Exists())
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, _targetVehicle, false);
            }
            catch { }

            try
            {
                if (_targetPed != null && _targetPed.Exists())
                    _targetPed.BlockPermanentEvents = false;
            }
            catch { }

            try
            {
                if (_approachCandidate != null && _approachCandidate.Exists())
                    _approachCandidate.BlockPermanentEvents = false;
            }
            catch { }

            try
            {
                if (_guardedVehicle != null && _guardedVehicle.Exists())
                {
                    Ped player = Game.Player.Character;
                    if (player != null && player.Exists())
                    {
                        Function.Call(
                            (Hash)0xA53ED5520C07654A,
                            _guardedVehicle,
                            player,
                            false);
                    }
                }
            }
            catch { }

            _reactionCooldown.Clear();
            _approachCandidate = null;
            _candidateUntil = DateTime.MinValue;
            _guardedVehicle = null;
            _guardedVehicleUntil = DateTime.MinValue;

            ResetInteraction();
        }

        private void TrimReactionCooldown(DateTime now)
        {
            try
            {
                List<int> expired = new List<int>();

                foreach (KeyValuePair<int, DateTime> pair in _reactionCooldown)
                {
                    if (now >= pair.Value.AddSeconds(30))
                        expired.Add(pair.Key);

                    if (expired.Count >= 250)
                        break;
                }

                foreach (int handle in expired)
                    _reactionCooldown.Remove(handle);
            }
            catch { }
        }

        private static bool IsGangExcluded(
            Ped ped,
            AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            if (ped == null || gangData == null)
                return false;

            try
            {
                // IMPORTANT: do not blanket-exclude MemberPool-only models.
                // MemberPool is a potential-member pool, not proof that the
                // currently spawned civilian is an active gang participant.
                // Exact GangData membership remains protected.
                return gangData.FindGangForModel(ped.Model.Hash) != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPotentialGangThreat(
            Ped ped,
            AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            if (ped == null || !ped.Exists() || gangData == null)
                return false;

            try
            {
                // A MemberPool-only model is a potential Gang & Turf model, but
                // that alone is not enough to classify a live civilian as gang
                // activity. Exact GangData matches remain excluded. A pool-only
                // ped is excluded from civilian contact only while visibly armed
                // or already in combat, preserving natural interaction with
                // ordinary-looking civilians in the global member pool.
                if (!gangData.IsMemberPoolModel(ped.Model.Hash))
                    return false;

                bool armed = Function.Call<bool>(
                    Hash.IS_PED_ARMED,
                    ped,
                    7);

                bool inCombat = Function.Call<bool>(
                    Hash.IS_PED_IN_COMBAT,
                    ped,
                    0);

                return armed || inCombat;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPolice(Ped ped)
        {
            if (ped == null || !ped.Exists())
                return false;

            try
            {
                return ped.IsInPoliceVehicle ||
                       ped.Model.Hash ==
                           unchecked((int)StringHash.AtStringHash("s_m_y_cop_01", 0)) ||
                       ped.Model.Hash ==
                           unchecked((int)StringHash.AtStringHash("s_f_y_cop_01", 0)) ||
                       ped.Model.Hash ==
                           unchecked((int)StringHash.AtStringHash("s_m_y_sheriff_01", 0)) ||
                       ped.Model.Hash ==
                           unchecked((int)StringHash.AtStringHash("s_f_y_sheriff_01", 0));
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDriverOf(Ped ped, Vehicle vehicle)
        {
            if (ped == null || vehicle == null ||
                !ped.Exists() || !vehicle.Exists())
                return false;

            try
            {
                // Native seat lookup is more reliable than depending only on the
                // managed Vehicle.Driver property in a heavily modded world.
                int driverHandle = Function.Call<int>(
                    Hash.GET_PED_IN_VEHICLE_SEAT,
                    vehicle,
                    -1);

                if (driverHandle == ped.Handle)
                    return true;

                if (vehicle.Driver != null &&
                    vehicle.Driver.Exists() &&
                    vehicle.Driver.Handle == ped.Handle)
                    return true;
            }
            catch { }

            return false;
        }

        private static void SafeLookAt(
            Ped ped,
            Ped player,
            int duration)
        {
            try
            {
                if (ped != null && ped.Exists() &&
                    player != null && player.Exists())
                {
                    ped.Task.LookAt(player, duration);
                }
            }
            catch { }
        }

        private static void PlayGreetingAnimation(Ped ped)
        {
            try
            {
                const string dict = "gestures@m@standing@casual";
                const string clip = "gesture_hello";

                Function.Call(Hash.REQUEST_ANIM_DICT, dict);

                if (!Function.Call<bool>(
                        Hash.HAS_ANIM_DICT_LOADED,
                        dict))
                    return;

                Function.Call(
                    Hash.TASK_PLAY_ANIM,
                    ped,
                    dict,
                    clip,
                    2.0f,
                    -2.0f,
                    1200,
                    0,
                    0f,
                    false,
                    false,
                    false);
            }
            catch { }
        }

        private static void PlayPaperAnimation(Ped ped)
        {
            try
            {
                const string dict = "mp_common";
                const string clip = "givetake1_a";

                Function.Call(Hash.REQUEST_ANIM_DICT, dict);

                if (!Function.Call<bool>(
                        Hash.HAS_ANIM_DICT_LOADED,
                        dict))
                    return;

                Function.Call(
                    Hash.TASK_PLAY_ANIM,
                    ped,
                    dict,
                    clip,
                    2.0f,
                    -2.0f,
                    2200,
                    0,
                    0f,
                    false,
                    false,
                    false);
            }
            catch { }
        }
    }
}

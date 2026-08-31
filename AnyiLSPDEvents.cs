using GTA;
using GTA.Math;
using System;

namespace AnyiLSPD
{
    public enum AnyiPoliceDutyState
    {
        OffDuty,
        Initializing,
        OnDuty,
        Resetting,
        Error
    }

    public enum AnyiPoliceDispatchState
    {
        None,
        Offered,
        Accepted,
        EnRoute,
        OnScene,
        Investigating,
        SuspectFleeing,
        SuspectCompliant,
        SuspectResisting,
        Arrested,
        AwaitingTransport,
        PickupEnRoute,
        Escorting,
        HoldingAtStation,
        PrisonTransfer,
        Completed,
        Cancelled,
        Failed,
        Compromised
    }

    public enum AnyiPoliceIncidentType
    {
        SuspiciousGangActivity,
        ArmsDealing,
        ContrabandSmuggling,
        DrugDealing,
        DrugManufacturing,
        CriminalHideout,
        GangAmbush,
        MassShootout,
        WeaponSmuggling,
        Hijacking,
        Kidnapping,
        BankHeist,
        StoreRobbery,
        RecklessDriver,
        PedestrianPursuit,
        VehiclePursuit,
        PoliceAssistance,
        OfficerDistress
    }

    public sealed class AnyiPoliceIncident
    {
        public Guid Id = Guid.NewGuid();
        public AnyiPoliceIncidentType Type;
        public string Title;
        public string Description;
        public Vector3 Origin;
        public float Severity;
        public string GangName = "none";
        public string TurfName = "none";
        public string AudioCategory = "";
        public Ped Suspect;
        public Ped Victim;
        public Vehicle SuspectVehicle;
        public bool OwnedByDispatch;
        public bool GeneratedFromChaosActivity;
        public string ChaosActivityName = "";
        public bool SuspectBehaviorInitialized;
        public bool SurrenderRequested;
        public DateTime SurrenderRequestedAt = DateTime.MinValue;
        public bool ArrestSecured;
        public AnyiPoliceDispatchState State = AnyiPoliceDispatchState.Offered;
        public DateTime CreatedAt = DateTime.UtcNow;
        public DateTime StateChangedAt = DateTime.UtcNow;
    }

    public sealed class AnyiPoliceAuthorityState
    {
        public bool IsPoliceOfficer;
        public bool PoliceHostilityDisabled;
        public bool VanillaWantedSuppressed;
        public bool VanillaDispatchSuppressed;
        public int CustomCrimeLevel;
        public AnyiPoliceDutyState DutyState = AnyiPoliceDutyState.OffDuty;
    }
}

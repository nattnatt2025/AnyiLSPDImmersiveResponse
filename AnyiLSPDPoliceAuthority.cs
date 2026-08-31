using GTA;
using GTA.Native;
using System;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDPoliceAuthority
    {
        private readonly AnyiPoliceAuthorityState _state = new AnyiPoliceAuthorityState();
        private DateTime _nextRefresh = DateTime.MinValue;
        private int _savedWantedAtEntry;
        private bool _entered;

        public AnyiPoliceAuthorityState State { get { return _state; } }

        public void Enter(AnyiLSPDPoliceConfig config)
        {
            if (_entered) return;
            _entered = true;
            _state.DutyState = AnyiPoliceDutyState.Initializing;
            _state.IsPoliceOfficer = true;
            _state.PoliceHostilityDisabled = true;
            _savedWantedAtEntry = SafeWantedLevel();
            ApplyAuthority(config, true);
            _state.DutyState = AnyiPoliceDutyState.OnDuty;
            _nextRefresh = DateTime.MinValue;
            LspdResponseLog.Write("POLICE_AUTHORITY", "ENTER | PreviousWanted=" + _savedWantedAtEntry);
        }

        public void Update(AnyiLSPDPoliceConfig config, DateTime now)
        {
            if (!_entered || !_state.IsPoliceOfficer)
                return;

            if (now >= _nextRefresh)
            {
                ApplyAuthority(config, false);

                _nextRefresh =
                    now.AddMilliseconds(config.AuthorityRefreshMs);
            }

            // Fast wanted-state correction only when GTA actually creates a wanted level.
            // This avoids making the whole authority maintenance loop run faster.
            if (config.EnableAutoWantedReset &&
                Game.Player != null &&
                Game.Player.Wanted.WantedLevel > 0)
            {
                try
                {
                    Function.Call(
                        Hash.SET_PLAYER_WANTED_LEVEL,
                        Game.Player,
                        0,
                        false);

                    Function.Call(
                        Hash.SET_PLAYER_WANTED_LEVEL_NOW,
                        Game.Player,
                        false);

                    _state.VanillaWantedSuppressed = true;
                }
                catch (Exception ex)
                {
                    LspdResponseLog.WriteException(
                        "POLICE_AUTHORITY_WANTED_RESET_ERROR",
                        ex);
                }
            }
        }
        public void Exit()
        {
            if (!_entered) return;
            _state.DutyState = AnyiPoliceDutyState.Resetting;
            try
            {
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, false);
                Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, Game.Player, true);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_AUTHORITY_EXIT_ERROR", ex);
            }

            _state.IsPoliceOfficer = false;
            _state.PoliceHostilityDisabled = false;
            _state.VanillaWantedSuppressed = false;
            _state.VanillaDispatchSuppressed = false;
            _state.CustomCrimeLevel = 0;
            _state.DutyState = AnyiPoliceDutyState.OffDuty;
            _entered = false;
            LspdResponseLog.Write("POLICE_AUTHORITY", "EXIT | Vanilla police targeting/dispatch restored.");
        }

        private void ApplyAuthority(AnyiLSPDPoliceConfig config, bool entering)
        {
            try
            {
                Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, true);
                Function.Call(Hash.SET_DISPATCH_COPS_FOR_PLAYER, Game.Player, false);
                _state.VanillaDispatchSuppressed = true;

                if (config.EnableAutoWantedReset && SafeWantedLevel() != 0)
                {
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, Game.Player, 0, false);
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, Game.Player, false);
                }

                _state.VanillaWantedSuppressed = config.EnableAutoWantedReset;
                _state.CustomCrimeLevel = 0;

                if (entering)
                {
                    LspdResponseLog.Write("POLICE_AUTHORITY", "Local authority engaged | Wanted cleared=" + config.EnableAutoWantedReset);
                }
            }
            catch (Exception ex)
            {
                _state.DutyState = AnyiPoliceDutyState.Error;
                LspdResponseLog.WriteException("POLICE_AUTHORITY_ERROR", ex);
            }
        }

        private static int SafeWantedLevel()
        {
            try { return Game.Player.Wanted.WantedLevel; }
            catch { return 0; }
        }

        public void Reset()
        {
            Exit();
        }
    }
}

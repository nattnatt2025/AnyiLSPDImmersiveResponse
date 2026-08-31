using System;
using System.Runtime.InteropServices;
using GTA;
using GTA.Native;
using System.Windows.Forms;

namespace AnyiLSPD
{
    public sealed class AnyiLSPDPoliceHotkeys
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private bool _accept;
        private bool _reject;
        private bool _secure;
        private bool _transport;
        private bool _completeTransport;
        private bool _npc;
        private bool _investigate;
        private bool _cancel;
        private bool _patrol;
        private bool _emergency;

        public void Reset()
        {
            _accept = _reject = _secure = _transport = _completeTransport = false;
            _npc = _investigate = _cancel = _patrol = _emergency = false;
        }

        public void Process(AnyiLSPDCore core, AnyiLSPDPoliceConfig config, bool menuClosed)
        {
            if (core == null || config == null || !core.IsActive || !config.EnablePoliceShortcuts)
                return;
            if (config.ShortcutKeysRequireMenuClosed && !menuClosed)
                return;

            SuppressConflictingGameplayInput(config);

            if (core.HasActiveNpcInteraction)
            {
                Trigger(config.AcceptDispatchKey, ref _accept, () => Report(core, "NPC Agree / Clear", core.AcceptNpcInteraction()));
                Trigger(config.RejectDispatchKey, ref _reject, () => Report(core, "NPC Disagree / Escalate", core.RejectNpcInteraction()));
            }
            else if (core.IsPrisonerHoldingAtStation)
            {
                // During the station custody decision, the existing Accept/Reject
                // bindings become Agree/Disagree for the prison transfer. This keeps
                // the familiar controls without inventing another key pair.
                Trigger(config.AcceptDispatchKey, ref _accept, () => Report(core, "Prison Transfer: AGREE", core.AgreePrisonTransfer()));
                Trigger(config.RejectDispatchKey, ref _reject, () => Report(core, "Prison Transfer: DISAGREE", core.DisagreePrisonTransfer()));
            }
            else
            {
                Trigger(config.AcceptDispatchKey, ref _accept, () => Report(core, "Accept Dispatch", core.AcceptDispatch()));
                Trigger(config.RejectDispatchKey, ref _reject, () => Report(core, "Reject Dispatch", core.RejectDispatch()));
            }
            Trigger(config.SecureSuspectKey, ref _secure, () => Report(core, "Secure Suspect", core.SecureSuspect()));
            Trigger(config.RequestTransportKey, ref _transport, () => Report(core, "Prisoner Transport", core.RequestTransport()));
            Trigger(config.CompleteTransportKey, ref _completeTransport, () => Report(core, "Transport Completed", core.CompleteTransportNow()));
            Trigger(config.NPCInteractionKey, ref _npc, () => Report(core, "Police NPC Interaction", core.NPCInteract()));
            Trigger(config.InvestigateSceneKey, ref _investigate, () => Report(core, "Investigate Scene", core.InvestigateScene()));
            Trigger(config.CancelDispatchKey, ref _cancel, () => Report(core, "Cancel Dispatch", core.CancelDispatch()));
            Trigger(config.PatrolKey, ref _patrol, () => Report(core, "Start Patrol", core.Patrol()));
            Trigger(config.EmergencySignalsKey, ref _emergency, () => Report(core, "Emergency Signals", core.ToggleEmergency()));
        }

        private static void SuppressConflictingGameplayInput(AnyiLSPDPoliceConfig config)
        {
            // GTA's default keyboard grenade input is G. Anyi uses G for the
            // police NPC interaction shortcut, so disable only that gameplay
            // control while the configured police shortcut is physically held.
            if (config != null && config.NPCInteractionKey == Keys.G)
            {
                bool down = (GetAsyncKeyState((int)Keys.G) & 0x8000) != 0;
                if (down)
                {
                    // Suppress the native grenade/throw control in both gameplay
                    // control groups while G is being consumed by Police Interaction.
                    Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 58, true);
                    Function.Call(Hash.DISABLE_CONTROL_ACTION, 2, 58, true);
                }
            }
        }

        private static void Trigger(Keys key, ref bool previous, Action action)
        {
            bool down = key != Keys.None && (GetAsyncKeyState((int)key) & 0x8000) != 0;
            bool rising = down && !previous;
            previous = down;
            if (rising && action != null)
                action();
        }

        private static void Report(AnyiLSPDCore core, string action, string result)
        {
            LspdResponseLog.Write("POLICE_SHORTCUT", action + " | " + (result ?? string.Empty));
            GTA.UI.Notification.PostTicker("~b~ANYI LSPD~s~\n" + action + "\n~c~" + (result ?? string.Empty), false, false);
        }
    }
}

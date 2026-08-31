using GTA;
using GTA.UI;
using GTA.Math;
using GTA.Native;
using LemonUI;
using LemonUI.Elements;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml.Linq;

namespace AnyiLSPD
{
    // UI owner only. Citizen world behavior belongs to LSPDCitizenCore so
    // the menu never scans peds or issues AI tasks every frame.
    public sealed class LSPDMainUI : Script
    {
        // Embedded logger: this is the authoritative runtime writer for the
        // compiled AnyiLSPD DLL. Other cores forward to it through LspdResponseLog.
        private static readonly object EmbeddedLogSync = new object();
        private static DateTime _nextEmbeddedHeartbeat = DateTime.MinValue;
        private static string _embeddedScriptsDirectory;
        private static string _embeddedRuntimeLogPath;
        private static string _embeddedHeartbeatLogPath;

        internal static string EmbeddedScriptsDirectory
        {
            get
            {
                EnsureEmbeddedLogger();
                return _embeddedScriptsDirectory;
            }
        }

        internal static string EmbeddedRuntimeLogPath
        {
            get
            {
                EnsureEmbeddedLogger();
                return _embeddedRuntimeLogPath;
            }
        }

        internal static string EmbeddedHeartbeatLogPath
        {
            get
            {
                EnsureEmbeddedLogger();
                return _embeddedHeartbeatLogPath;
            }
        }

        internal static void EnsureEmbeddedLogger()
        {
            if (!string.IsNullOrWhiteSpace(_embeddedRuntimeLogPath))
                return;

            lock (EmbeddedLogSync)
            {
                if (!string.IsNullOrWhiteSpace(_embeddedRuntimeLogPath))
                    return;

                string selected = AnyiLSPDPathProvider.ScriptsDirectory;

                try
                {
                    Directory.CreateDirectory(selected);
                    string probe = Path.Combine(selected, "AnyiLSPD_Runtime.log");
                    using (FileStream stream = new FileStream(
                        probe,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                            " | LOGGER_PROBE | Real GTA scripts directory verified.");
                        writer.Flush();
                    }
                }
                catch
                {
                    // The path provider already performed the strongest available
                    // process-root resolution. Logging remains fail-safe.
                }

                _embeddedScriptsDirectory = selected;
                _embeddedRuntimeLogPath =
                    Path.Combine(selected, "AnyiLSPD_Runtime.log");
                _embeddedHeartbeatLogPath =
                    Path.Combine(selected, "AnyiLSPD_Heartbeat.log");

                WriteEmbeddedLog(
                    "LOGGER_BOOT",
                    "Embedded logger ready | ScriptDirectory=" +
                    _embeddedScriptsDirectory +
                    " | Runtime=" + _embeddedRuntimeLogPath +
                    " | Heartbeat=" + _embeddedHeartbeatLogPath +
                    " | Assembly=" + SafeAssemblyLocation());
            }
        }

        internal static void WriteEmbeddedLog(string category, string message)
        {
            try
            {
                EnsureEmbeddedLogger();

                string line =
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " | " + (string.IsNullOrWhiteSpace(category) ? "LOG" : category) +
                    " | " + (message ?? string.Empty);

                lock (EmbeddedLogSync)
                {
                    using (FileStream stream = new FileStream(
                        _embeddedRuntimeLogPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(line);
                        writer.Flush();
                    }
                }
            }
            catch
            {
                // Diagnostics must never take down GTA.
            }
        }

        internal static void WriteEmbeddedHeartbeat(string message)
        {
            WriteEmbeddedLog("HEARTBEAT", message);

            try
            {
                EnsureEmbeddedLogger();

                string line =
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                    " | HEARTBEAT | " + (message ?? string.Empty);

                lock (EmbeddedLogSync)
                {
                    using (FileStream stream = new FileStream(
                        _embeddedHeartbeatLogPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(line);
                        writer.Flush();
                    }
                }
            }
            catch
            {
            }
        }

        internal static void WriteEmbeddedException(string category, Exception ex)
        {
            if (ex == null)
                return;

            WriteEmbeddedLog(
                category,
                ex.GetType().FullName +
                " | Message=" + ex.Message +
                " | Stack=" + ex.StackTrace);
        }

        private static string SafeAssemblyLocation()
        {
            try
            {
                return Assembly.GetExecutingAssembly().Location;
            }
            catch
            {
                return "unknown";
            }
        }
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static readonly Color HeaderBlue = Color.FromArgb(255, 47, 111, 190);

        private readonly ObjectPool _pool = new ObjectPool();
        private readonly List<NativeMenu> _menus = new List<NativeMenu>();
        private string _scriptDirectory;
        private string _configPath;
        private bool _menuKeyWasDown;
        private LspdResponseUiConfig _config;
        private LSPDGangTurfCore _gangCore;
        private AnyiLSPDCore _policeCore;

        private NativeMenu _root;
        private NativeMenu _roleProfile;
        private NativeMenu _agency;
        private NativeMenu _pedProfile;
        private NativeMenu _vehicles;
        private NativeMenu _weapons;
        private NativeMenu _roleplay;
        private NativeMenu _selectedRole;
        private NativeMenu _policeLocations;
        private NativeMenu _controls;
        private NativeMenu _diagnostics;
        private NativeMenu _chaosAudio;

        public LSPDMainUI()
        {
            Interval = 0;
            EnsureEmbeddedLogger();
            WriteEmbeddedLog("UI_BOOT", "LSPD MainUI v5.2 constructor entered.");

            _scriptDirectory = ResolveScriptDirectory();
            WriteEmbeddedLog("PATH_RESOLUTION", AnyiLSPDPathProvider.DescribeResolution());
            _configPath = Path.Combine(_scriptDirectory, LspdResponseUiConfig.FileName);
            _config = LspdResponseUiConfig.LoadOrCreate(_configPath, Log);

            _gangCore = new LSPDGangTurfCore();
            _policeCore = new AnyiLSPDCore(_scriptDirectory);

            // Fresh GTA session rule: never resume Police Authority automatically.
            // MainUI remains the role selector; Anyi explicitly enters Police Authority.
            if (_config.ActiveRole == LspdResponseRole.PoliceAuthority)
            {
                _config.ActiveRole = LspdResponseRole.LosSantosCitizen;
                _config.Save(_configPath, Log);
                Log("STARTUP_ROLE_RESET | Previous Police Authority selection cleared; starting in Citizen mode.");
            }

            _policeCore.UpdateRole(_config.ActiveRole);

            BuildMenus();
            RebuildRoleProfileAvailability();
            RebuildSelectedRoleMenu();
            RebuildPoliceLocationMenu();

            Tick += OnTick;
            Aborted += OnAborted;

            Log("BOOT | Anyi LSPD MainUI v5 | Role=" + DisplayRole(_config.ActiveRole) + " | Toggle=" + _config.MenuToggleKey);
        }

        private void BuildMenus()
        {
            _root = CreateMenu("LSPD Response 5.2", "ANYI LSPD");
            _roleProfile = CreateMenu("Profile", "AGENCY / PED / CAR");
            _agency = CreateMenu("Agency", "DEPARTMENT");
            _pedProfile = CreateMenu("Officer Ped", "MODEL");
            _vehicles = CreateMenu("Police Car", "VEHICLE / SIREN");
            _weapons = CreateMenu("Police Weapon", "LOADOUT / FAVORITE");
            _roleplay = CreateMenu("Roleplay", "SELECT ROLE");
            _selectedRole = CreateMenu("Police Response", "PATROL / CALL / ARREST");
            _policeLocations = CreateMenu("Stations", "POLICE / GPS");
            _controls = CreateMenu("Controls", "KEYS / STATUS");
            _diagnostics = CreateMenu("Diagnostics", "RESET / LOGS");
            _chaosAudio = CreateMenu("Chaos Audio", "VOLUME / TEST");

            AddMenuToPool(_root);
            AddMenuToPool(_roleProfile);
            AddMenuToPool(_agency);
            AddMenuToPool(_pedProfile);
            AddMenuToPool(_vehicles);
            AddMenuToPool(_weapons);
            AddMenuToPool(_roleplay);
            AddMenuToPool(_selectedRole);
            AddMenuToPool(_policeLocations);
            AddMenuToPool(_controls);
            AddMenuToPool(_diagnostics);
            AddMenuToPool(_chaosAudio);

            _root.AddSubMenu(_roleProfile, "Profile");
            _root.AddSubMenu(_selectedRole, "Police Response");
            _root.AddSubMenu(_policeLocations, "Stations");
            _root.AddSubMenu(_controls, "Controls");
            _root.AddSubMenu(_diagnostics, "Diagnostics");

            _roleProfile.AddSubMenu(_roleplay, "Roleplay");
            _roleProfile.AddSubMenu(_agency, "Agency");
            _roleProfile.AddSubMenu(_pedProfile, "Officer Ped");
            _roleProfile.AddSubMenu(_vehicles, "Police Vehicle");
            _roleProfile.AddSubMenu(_weapons, "Police Weapon");

            AddRoleItem("Gang Turf Leader", LspdResponseRole.GangTurfLeader, "Existing Gang & Turf layer; Police does not own this state.");
            AddRoleItem("Police Authority", LspdResponseRole.PoliceAuthority, "Anyiii becomes a local police authority with dispatch and patrol ownership.");
            AddRoleItem("Los Santos Citizen", LspdResponseRole.LosSantosCitizen, "Existing Citizen layer; Police does not own this state.");

            BuildControlsMenu();
            BuildDiagnosticsMenu();
        }

        private void RebuildRoleProfileAvailability()
        {
            bool policeActive = _config.ActiveRole == LspdResponseRole.PoliceAuthority;
            bool gangActive = _config.ActiveRole == LspdResponseRole.GangTurfLeader;

            _agency.Clear();
            _pedProfile.Clear();
            _vehicles.Clear();
            _weapons.Clear();

            if (policeActive)
            {
                BuildPoliceAgencyMenu();
                BuildPolicePedProfileMenu();
                BuildPoliceVehicleMenu();
                BuildPoliceWeaponMenu();
                return;
            }

            AddDisabledItem(_agency, "Police Authority only", "Select Police Authority before changing agency.");

            if (gangActive)
            {
                BuildGangPedMenu();
                BuildGangVehicleMenu();
            }
            else
            {
                AddDisabledItem(_pedProfile, "Police/Gang role required", "Select Police Authority or Gang Turf Leader to use model tools.");
                AddDisabledItem(_vehicles, "Police/Gang role required", "Select Police Authority or Gang Turf Leader to use vehicle tools.");
            }
        }

        private void BuildPoliceAgencyMenu()
        {
            AddDisabledItem(_agency, "SELECT POLICE AGENCY", "Agency controls the officer/vehicle defaults. Station can be selected independently.");
            if (_policeCore == null || _policeCore.ProfileCore == null)
            {
                AddDisabledItem(_agency, "Police profile core unavailable", "Check AnyiLSPD_Runtime.log.");
                return;
            }

            foreach (AnyiLSPDProfileCore.PoliceProfile profile in _policeCore.ProfileCore.All)
            {
                string id = profile.Id;
                NativeItem item = new NativeItem(
                    (profile.Id == _policeCore.ProfileCore.Current.Id ? "* " : "") + profile.Id,
                    profile.Department + " | " + profile.OfficerModel + " | " + profile.VehicleModel);
                item.Activated += delegate
                {
                    if (_policeCore.SelectPoliceProfile(id))
                    {
                        Notification.PostTicker(
                            "~b~ANYI LSPD~s~\nAgency selected: " + id +
                            "\n~c~Choose an Officer Ped / Police Vehicle, then start patrol.",
                            false,
                            false);
                    }
                    else
                    {
                        Notification.PostTicker(
                            "~r~ANYI LSPD~s~\nAgency change unavailable while a Police operation is active.",
                            false,
                            false);
                    }
                    RebuildRoleProfileAvailability();
                    RebuildSelectedRoleMenu();
                    RebuildPoliceLocationMenu();
                };
                _agency.Add(item);
            }
        }

        private void BuildPolicePedProfileMenu()
        {
            AddDisabledItem(_pedProfile, "AVAILABLE POLICE PEDS", "Click a preset. Presets never overwrite the saved favorite.");
            List<PoliceModelChoice> available = LoadPoliceModelChoices("OfficerModels");
            if (available.Count == 0)
            {
                AddDisabledItem(_pedProfile, "Police ped XML unavailable", "Check AnyiLSPDPoliceModels.xml.");
            }
            else
            {
                foreach (PoliceModelChoice choice in available)
                {
                    PoliceModelChoice selected = choice;
                    NativeItem item = new NativeItem(
                        selected.DisplayName,
                        selected.Model + " | Hash=" + selected.HashText);
                    item.Activated += delegate
                    {
                        ApplyPolicePedSelection(selected.Model, false);
                    };
                    _pedProfile.Add(item);
                }
            }

            AddDisabledItem(_pedProfile, "FAVORITE OFFICER PED", "Type a model once; it becomes persistent and is used whenever Police Authority starts.");
            NativeItem setFavorite = new NativeItem(
                "Set Favorite Officer Ped",
                "Example: venti, s_m_y_cop_01, or a numeric model hash.");
            setFavorite.Activated += delegate
            {
                SetFavoriteOfficerPed();
            };
            _pedProfile.Add(setFavorite);

            string favorite = _policeCore == null || _policeCore.Config == null
                ? "none"
                : _policeCore.Config.FavoriteOfficerModel;
            NativeItem useFavorite = new NativeItem(
                "Use Favorite: " + favorite,
                "Apply the saved favorite officer model and restore the saved Police Weapon loadout.");
            useFavorite.Activated += delegate
            {
                ApplyPolicePedSelection(favorite, true);
            };
            useFavorite.Enabled = !string.IsNullOrWhiteSpace(favorite);
            _pedProfile.Add(useFavorite);

            if (_policeCore != null && _policeCore.ProfileCore != null && _policeCore.ProfileCore.Current != null)
            {
                string current = _policeCore.ProfileCore.Current.OfficerModel;
                string hash = TryGetModelHashText(current, true);
                AddDisabledItem(_pedProfile, "Current: " + current + " | Hash=" + hash, "Current Police Authority player model.");
            }
        }

        private void BuildPoliceVehicleMenu()
        {
            AddDisabledItem(_vehicles, "AVAILABLE POLICE CARS", "Click a preset. Presets never overwrite the saved favorite.");
            List<PoliceModelChoice> available = LoadPoliceModelChoices("VehicleModels");
            if (available.Count == 0)
            {
                AddDisabledItem(_vehicles, "Police vehicle XML unavailable", "Check AnyiLSPDPoliceModels.xml.");
            }
            else
            {
                foreach (PoliceModelChoice choice in available)
                {
                    PoliceModelChoice selected = choice;
                    NativeItem item = new NativeItem(
                        selected.DisplayName,
                        selected.Model + " | Hash=" + selected.HashText);
                    item.Activated += delegate
                    {
                        ApplyPoliceVehicleSelection(selected.Model, false);
                    };
                    _vehicles.Add(item);
                }
            }

            AddDisabledItem(_vehicles, "FAVORITE POLICE CAR", "Type a model once; it becomes persistent and is used for Anyi's patrol vehicle.");
            NativeItem setFavorite = new NativeItem(
                "Set Favorite Police Car",
                "Example: polignus, police, or a numeric vehicle model hash.");
            setFavorite.Activated += delegate
            {
                SetFavoritePoliceVehicle();
            };
            _vehicles.Add(setFavorite);

            string favorite = _policeCore == null || _policeCore.Config == null
                ? "none"
                : _policeCore.Config.FavoritePoliceVehicleModel;
            NativeItem useFavorite = new NativeItem(
                "Use Favorite: " + favorite,
                "Spawn the saved favorite Police vehicle for Anyi.");
            useFavorite.Activated += delegate
            {
                ApplyPoliceVehicleSelection(favorite, true);
            };
            useFavorite.Enabled = !string.IsNullOrWhiteSpace(favorite);
            _vehicles.Add(useFavorite);

            NativeItem emergency = new NativeItem("Emergency Signals", "Cycle lights and siren.");
            emergency.Activated += delegate
            {
                Notification.PostTicker("~b~ANYI LSPD~s~\n" + _policeCore.ToggleEmergency(), false, false);
            };
            _vehicles.Add(emergency);

            if (_policeCore != null && _policeCore.ProfileCore != null && _policeCore.ProfileCore.Current != null)
            {
                string current = _policeCore.ProfileCore.Current.VehicleModel;
                AddDisabledItem(
                    _vehicles,
                    "Current: " + current + " | Hash=" + TryGetModelHashText(current, false),
                    "NativeSiren=" + _policeCore.ProfileCore.Current.NativeSiren + " | EmergencyLights=" + _policeCore.ProfileCore.Current.EmergencyLights);
            }
        }

        private void BuildPoliceWeaponMenu()
        {
            AddDisabledItem(_weapons, "POLICE WEAPON LOADOUT", "Loaded automatically when Police Authority starts and after a Police ped change.");

            List<PoliceWeaponChoice> weapons = LoadPoliceWeapons();
            if (weapons.Count == 0)
            {
                AddDisabledItem(_weapons, "Police weapon XML unavailable", "Check AnyiLSPDPoliceWeapons.xml.");
            }
            else
            {
                foreach (PoliceWeaponChoice choice in weapons)
                {
                    PoliceWeaponChoice selected = choice;
                    NativeItem item = new NativeItem(
                        selected.DisplayName,
                        "Hash=" + selected.HashText + " | Ammo=" + selected.Ammo + " | Tint=" + selected.Tint);
                    item.Activated += delegate
                    {
                        ApplyFavoriteWeapon();
                    };
                    _weapons.Add(item);
                }
            }

            string favoriteHash = _policeCore == null || _policeCore.Config == null
                ? "0x83BF0278"
                : _policeCore.Config.FavoriteWeaponHash;
            int favoriteAmmo = _policeCore == null || _policeCore.Config == null
                ? 240
                : _policeCore.Config.FavoriteWeaponAmmo;
            int favoriteTint = _policeCore == null || _policeCore.Config == null
                ? 2
                : _policeCore.Config.FavoriteWeaponTint;

            AddDisabledItem(_weapons, "FAVORITE WEAPON", "Saved in AnyiLSPDPolice.ini and restored automatically.");
            NativeItem setFavorite = new NativeItem(
                "Set Favorite Weapon Hash",
                "Example: 0x83BF0278 from your Menyoo loadout.");
            setFavorite.Activated += delegate
            {
                SetFavoriteWeaponHash();
            };
            _weapons.Add(setFavorite);

            NativeItem reloadFavorite = new NativeItem(
                "Reload Favorite Weapon",
                favoriteHash + " | Ammo=" + favoriteAmmo + " | Tint=" + favoriteTint);
            reloadFavorite.Activated += delegate
            {
                ApplyFavoriteWeapon();
            };
            _weapons.Add(reloadFavorite);
        }

        private void RebuildSelectedRoleMenu()
        {
            _selectedRole.Clear();
            AddDisabledItem(_selectedRole, "Active Role: " + DisplayRole(_config.ActiveRole), "Choose the active Anyi role.");

            if (_config.ActiveRole == LspdResponseRole.PoliceAuthority)
            {
                string status = _policeCore == null ? "Police Core unavailable" : _policeCore.StatusLine;
                AddDisabledItem(_selectedRole, "Duty: " + (_policeCore != null && _policeCore.IsActive ? "ON DUTY" : "OFF DUTY"), "Police Authority status.");
                AddDisabledItem(_selectedRole, "Agency: " + (_policeCore == null || _policeCore.ProfileCore.Current == null ? "none" : _policeCore.ProfileCore.Current.Id), "Selected police agency.");
                AddDisabledItem(_selectedRole, "Station: " + (_policeCore == null || _policeCore.ProfileCore.Current == null ? "none" : _policeCore.ProfileCore.Current.StationId), "Selected police station.");
                AddDisabledItem(_selectedRole, "Call: " + (_policeCore == null ? "none" : _policeCore.DispatchState.ToString()), "Dispatch/custody state.");

                AddPoliceAction("Start / Replace Patrol Here", "Spawn patrol at current position.");
                AddPoliceAction("Start Patrol At Selected Station", "Spawn patrol at selected station.");
                AddPoliceAction("Offer Patrol Dispatch", "Offer a patrol call now.");
                AddPoliceAction("Offer Chaos Activity", "Offer a nearby Chaos Activity call.");
                AddPoliceAction("Accept Dispatch", "Accept offered call.");
                AddPoliceAction("Reject Dispatch", "Reject offered call.");
                AddPoliceAction("Cancel Dispatch", "Cancel active call.");
                AddPoliceAction("Investigate Scene", "Investigate active scene.");
                AddPoliceAction("Secure Suspect", "Arrest a compliant suspect.");
                AddPoliceAction("Request Prisoner Transport", "Request prisoner transport.");
                AddPoliceAction("Agree Prison Transfer", "Approve station -> prison transfer.");
                AddPoliceAction("Disagree Prison Transfer", "Decline prison transfer and complete the justice task at the station.");
                AddPoliceAction("Complete Transport (T)", "Finalize prison booking, close the dispatch and move to the next activity.");
                AddPoliceAction("Cancel Prisoner Transport", "Cancel convoy but keep the prisoner in custody.");
                AddPoliceAction("Police NPC Interaction", "G: interact. Y: agree/clear. N: disagree/escalate.");
                AddPoliceAction("Emergency Signals", "Cycle emergency signals.");
                AddPoliceAction("Test Chaos Dispatch Audio", "Test a real ChaosResponse clip.");
                BuildChaosAudioMenu();
                _selectedRole.AddSubMenu(_chaosAudio, "Chaos Audio");
                AddPoliceAction("Reset Police Bugs", "Clear Police runtime state.");
                AddPoliceAction("Write Police Diagnostic", "Write Police diagnostic.");
                return;
            }

            if (_config.ActiveRole == LspdResponseRole.LosSantosCitizen)
            {
                LSPDCitizenCore core = LSPDCitizenCore.Instance;
                AddDisabledItem(_selectedRole, core == null ? "STATUS: Citizen core unavailable" : "STATUS: " + core.StatusLine, "Citizen mode owns civilian behavior.");
                AddCitizenActionItem("Greet Police", "Greet a nearby officer.");
                AddCitizenActionItem("Interact with Police", "Start a civilian interaction.");
                AddCitizenActionItem("Assure / Cooperate", "Ask nearby police to treat Anyi as cooperative when appropriate.");
                AddCitizenActionItem("Call Dispatch", "Request help from the existing Citizen layer.");
                return;
            }

            LSPDGangTurfCore gang = _gangCore;
            AddDisabledItem(_selectedRole, gang == null ? "STATUS: Gang core unavailable" : "STATUS: " + gang.StatusLine, "Gang mode owns Gang & Turf behavior.");
            AddGangActionItem("Greet Gang Member", "Greet an Anyiii's Gang member.");
            AddGangActionItem("Interact with Gang Member", "Interact with an Anyiii's Gang member.");
            AddGangActionItem("Territory Status", "Read current Gang & Turf context.");
            AddGangActionItem("Reset Gang Response", "Clear only Gang-owned temporary state.");
        }

        private void AddPoliceAction(string action, string description)
        {
            NativeItem item = new NativeItem(action, description);
            item.Activated += delegate { ExecutePoliceAction(action); };
            _selectedRole.Add(item);
        }

        private void ExecutePoliceAction(string action)
        {
            if (_policeCore == null) return;
            string result;
            switch (action)
            {
                case "Start / Replace Patrol Here": result = _policeCore.Patrol(); break;
                case "Start Patrol At Selected Station": result = _policeCore.PatrolAtSelectedStation(); break;
                case "Offer Patrol Dispatch": result = _policeCore.ForceDispatchScan(); break;
                case "Offer Chaos Activity": result = _policeCore.ForceChaosDispatch(); break;
                case "Accept Dispatch": result = _policeCore.AcceptDispatch(); break;
                case "Reject Dispatch": result = _policeCore.RejectDispatch(); break;
                case "Cancel Dispatch": result = _policeCore.CancelDispatch(); break;
                case "Investigate Scene": result = _policeCore.InvestigateScene(); break;
                case "Secure Suspect": result = _policeCore.SecureSuspect(); break;
                case "Request Prisoner Transport": result = _policeCore.RequestTransport(); break;
                case "Agree Prison Transfer": result = _policeCore.AgreePrisonTransfer(); break;
                case "Disagree Prison Transfer": result = _policeCore.DisagreePrisonTransfer(); break;
                case "Complete Transport (T)": result = _policeCore.CompleteTransportNow(); break;
                case "Cancel Prisoner Transport": result = _policeCore.CancelPrisonerTransport(); break;
                case "Police NPC Interaction": result = _policeCore.NPCInteract(); break;
                case "Emergency Signals": result = _policeCore.ToggleEmergency(); break;
                case "Test Chaos Dispatch Audio": result = _policeCore.TestChaosDispatchAudio(); break;
                case "Reset Police Bugs": result = _policeCore.ResetBugs(); break;
                default:
                    _policeCore.WriteDiagnosticReport("Manual Police diagnostic requested.");
                    result = "Police diagnostic report written.";
                    break;
            }

            Log("POLICE_ACTION | " + action + " | " + result);
            Notification.PostTicker("~b~ANYI LSPD~s~\n" + action + "\n~c~" + result, false, false);
            RebuildSelectedRoleMenu();
            RebuildRoleProfileAvailability();
            RebuildPoliceLocationMenu();
        }

        private void RebuildPoliceLocationMenu()
        {
            _policeLocations.Clear();
            if (_policeCore == null)
            {
                AddDisabledItem(_policeLocations, "Police Core unavailable", "Check AnyiLSPD_Runtime.log.");
                return;
            }

            AddDisabledItem(_policeLocations, "Selected Station: " + (_policeCore.ProfileCore.Current == null ? "none" : _policeCore.ProfileCore.Current.StationId), "Station used for patrol/custody.");

            NativeItem nearest = new NativeItem("GPS: Nearest Police Station", "GPS to nearest station.");
            nearest.Activated += delegate { NotifyPolice(_policeCore.QuickGpsNearestStation()); };
            _policeLocations.Add(nearest);

            NativeItem selected = new NativeItem("GPS: Selected Police Station", "GPS to selected station.");
            selected.Activated += delegate { NotifyPolice(_policeCore.QuickGpsSelectedStation()); };
            _policeLocations.Add(selected);

            NativeItem prison = new NativeItem("GPS: Prison / Custody", "GPS to prison.");
            prison.Activated += delegate { NotifyPolice(_policeCore.QuickGpsPrison()); };
            _policeLocations.Add(prison);

            foreach (AnyiLSPDPoliceStations.Station station in _policeCore.StationCore.All)
            {
                string id = station.Id;
                string title = (_policeCore.ProfileCore.Current != null && string.Equals(_policeCore.ProfileCore.Current.StationId, id, StringComparison.OrdinalIgnoreCase) ? "* " : "") + station.Name;
                NativeItem item = new NativeItem(title, station.Id + " | " + station.InteriorMode);
                item.Activated += delegate
                {
                    bool ok = _policeCore.SelectPoliceStation(id);
                    if (ok)
                    {
                        Notification.PostTicker("~b~ANYI LSPD~s~\nStation selected: " + id + "\n~c~Future patrol/transport spawns will use this station.", false, false);
                    }
                    RebuildSelectedRoleMenu();
                    RebuildPoliceLocationMenu();
                };
                _policeLocations.Add(item);
            }
        }

        private void NotifyPolice(string result)
        {
            Notification.PostTicker("~b~ANYI LSPD~s~\n" + result, false, false);
        }


        private void BuildControlsMenu()
        {
            _controls.Clear();
            AddDisabledItem(_controls, "MENU KEY: " + _config.MenuToggleKey, "UI toggle key.");
            AddDisabledItem(_controls, "UI: FRAME PROCESSING", "UI frame; world work throttled.");
            AddDisabledItem(
                _controls,
                "POLICE STATUS: " + (_policeCore == null ? "Unavailable" : (_policeCore.IsActive ? "ON DUTY" : "OFF DUTY")),
                "Police duty status.");

            if (_policeCore != null)
            {
                AnyiLSPDPoliceConfig pc = _policeCore.Config;
                AddDisabledItem(_controls, "ACCEPT/AGREE " + pc.AcceptDispatchKey + " / REJECT/DISAGREE " + pc.RejectDispatchKey, "Dispatch and NPC-interaction response keys.");
                AddDisabledItem(_controls, "SECURE " + pc.SecureSuspectKey + " / TRANSPORT " + pc.RequestTransportKey + " / COMPLETE " + pc.CompleteTransportKey, "Police custody/transport shortcut keys. At station: Accept=Agree, Reject=Disagree.");
                AddDisabledItem(_controls, "NPC " + pc.NPCInteractionKey + " / INVESTIGATE " + pc.InvestigateSceneKey, "Police shortcut keys.");
                AddDisabledItem(_controls, "PATROL " + pc.PatrolKey + " / EMERGENCY " + pc.EmergencySignalsKey, "Police shortcut keys.");
            }
        }

        private void BuildChaosAudioMenu()
        {
            _chaosAudio.Clear();

            if (_policeCore == null)
            {
                AddDisabledItem(_chaosAudio, "AUDIO UNAVAILABLE", "Police core unavailable.");
                return;
            }

            AddDisabledItem(
                _chaosAudio,
                "CHAOS AUDIO: " + _policeCore.ChaosAudioStatus(),
                "Saved automatically in AnyiLSPDPolice.ini.");

            NativeItem masterDown = new NativeItem("Master -5%", "Lower all Chaos Response audio.");
            masterDown.Activated += delegate
            {
                NotifyPolice(_policeCore.DecreaseChaosMasterVolume());
                BuildChaosAudioMenu();
            };
            _chaosAudio.Add(masterDown);

            NativeItem masterUp = new NativeItem("Master +5%", "Raise all Chaos Response audio.");
            masterUp.Activated += delegate
            {
                NotifyPolice(_policeCore.IncreaseChaosMasterVolume());
                BuildChaosAudioMenu();
            };
            _chaosAudio.Add(masterUp);

            NativeItem dispatchDown = new NativeItem("Dispatch -5%", "Lower Chaos dispatch announcements.");
            dispatchDown.Activated += delegate
            {
                NotifyPolice(_policeCore.DecreaseChaosDispatchVolume());
                BuildChaosAudioMenu();
            };
            _chaosAudio.Add(dispatchDown);

            NativeItem dispatchUp = new NativeItem("Dispatch +5%", "Raise Chaos dispatch announcements.");
            dispatchUp.Activated += delegate
            {
                NotifyPolice(_policeCore.IncreaseChaosDispatchVolume());
                BuildChaosAudioMenu();
            };
            _chaosAudio.Add(dispatchUp);

            NativeItem mute = new NativeItem("Mute / Unmute", "Toggle Chaos Response audio without changing saved levels.");
            mute.Activated += delegate
            {
                NotifyPolice(_policeCore.ToggleChaosAudioMute());
                BuildChaosAudioMenu();
            };
            _chaosAudio.Add(mute);

            NativeItem reset = new NativeItem("Reset Audio Levels", "Safe defaults: Master 35% / Dispatch 30%.");
            reset.Activated += delegate
            {
                NotifyPolice(_policeCore.ResetChaosAudioSettings());
                BuildChaosAudioMenu();
            };
            _chaosAudio.Add(reset);

            NativeItem test = new NativeItem("Test Dispatch Audio", "Play an installed Chaos Response dispatch clip.");
            test.Activated += delegate
            {
                NotifyPolice(_policeCore.TestChaosDispatchAudio());
                BuildChaosAudioMenu();
            };
            _chaosAudio.Add(test);
        }

        private void BuildDiagnosticsMenu()
        {
            _diagnostics.Clear();

            NativeItem reset = new NativeItem(
                "Reset Police Bugs",
                "Clear Police runtime state.");
            reset.Activated += delegate
            {
                string result = _policeCore == null
                    ? "Police core is unavailable."
                    : _policeCore.ResetBugs();
                Notification.PostTicker("~b~ANYI LSPD~s~\nRESET POLICE BUGS\n~c~" + result, false, false);
                Log("POLICE_RESET | " + result);
                RebuildSelectedRoleMenu();
                RebuildPoliceLocationMenu();
            };
            _diagnostics.Add(reset);

            NativeItem diagnostic = new NativeItem(
                "Write Diagnostic Log",
                "Write Police diagnostic log.");
            diagnostic.Activated += delegate
            {
                if (_policeCore != null)
                    _policeCore.WriteDiagnosticReport("Manual Police diagnostic requested from Reset & Diagnostics.");
                string path = _policeCore == null
                    ? Path.Combine(_scriptDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "AnyiLSPD_PoliceAuthority_Diagnostic.log")
                    : _policeCore.DiagnosticLogPath;
                Notification.PostTicker("~b~ANYI LSPD~s~\nDiagnostic written\n~c~" + path, false, false);
                Log("POLICE_DIAGNOSTIC | " + path);
            };
            _diagnostics.Add(diagnostic);

            NativeItem uiDiagnostic = new NativeItem(
                "Write UI Diagnostic",
                "Write UI diagnostic log.");
            uiDiagnostic.Activated += delegate
            {
                WriteReport("Manual UI diagnostic requested.");
                Notification.PostTicker("~b~ANYI LSPD~s~\nUI diagnostic written\n~c~" + EmbeddedRuntimeLogPath, false, false);
            };
            _diagnostics.Add(uiDiagnostic);

            NativeItem paths = new NativeItem(
                "Show Log Paths",
                "Show exact log paths.");
            paths.Activated += delegate
            {
                string policePath = _policeCore == null
                    ? Path.Combine(_scriptDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "AnyiLSPD_PoliceAuthority_Diagnostic.log")
                    : _policeCore.DiagnosticLogPath;
                Notification.PostTicker(
                    "~b~ANYI LSPD LOGS~s~\nRuntime: " + EmbeddedRuntimeLogPath +
                    "\n~c~Heartbeat: " + EmbeddedHeartbeatLogPath +
                    "\nPolice: " + policePath,
                    false,
                    false);
            };
            _diagnostics.Add(paths);
        }

        private sealed class PoliceModelChoice
        {
            public string DisplayName;
            public string Model;
            public string HashText;
        }

        private sealed class PoliceWeaponChoice
        {
            public string Id;
            public string DisplayName;
            public string HashText;
            public int Tint;
            public int Ammo;
            public List<string> ComponentHashes = new List<string>();
        }

        private List<PoliceModelChoice> LoadPoliceModelChoices(string sectionName)
        {
            List<PoliceModelChoice> result = new List<PoliceModelChoice>();
            try
            {
                string path = Path.Combine(_scriptDirectory, _policeCore.Config.PoliceModelsFile);
                if (!File.Exists(path))
                    return result;

                XDocument doc = XDocument.Load(path);
                XElement root = doc.Root;
                if (root == null)
                    return result;
                XElement section = root.Element(sectionName);
                if (section == null)
                    return result;

                foreach (XElement node in section.Elements("Model"))
                {
                    string model = (string)node.Attribute("model");
                    if (string.IsNullOrWhiteSpace(model))
                        continue;
                    string display = (string)node.Attribute("displayName");
                    if (string.IsNullOrWhiteSpace(display))
                        display = model;
                    string hash = (string)node.Attribute("hash");
                    if (string.IsNullOrWhiteSpace(hash))
                        hash = TryGetModelHashText(model, sectionName == "OfficerModels");
                    result.Add(new PoliceModelChoice
                    {
                        DisplayName = display.Trim(),
                        Model = model.Trim(),
                        HashText = hash.Trim()
                    });
                }
            }
            catch (Exception ex)
            {
                Log("POLICE_MODEL_XML_ERROR | " + ex.GetType().Name + " | " + ex.Message);
            }
            return result;
        }

        private List<PoliceWeaponChoice> LoadPoliceWeapons()
        {
            List<PoliceWeaponChoice> result = new List<PoliceWeaponChoice>();
            try
            {
                string path = Path.Combine(_scriptDirectory, _policeCore.Config.PoliceWeaponsFile);
                if (!File.Exists(path))
                    return result;

                XDocument doc = XDocument.Load(path);
                XElement root = doc.Root;
                if (root == null)
                    return result;

                foreach (XElement node in root.Elements("Weapon"))
                {
                    string hash = (string)node.Attribute("hash");
                    if (string.IsNullOrWhiteSpace(hash))
                        continue;
                    int tint = ReadIntAttribute(node, "tint", 0);
                    int ammo = ReadIntAttribute(node, "ammo", 240);
                    PoliceWeaponChoice item = new PoliceWeaponChoice
                    {
                        Id = ReadAttribute(node, "id", hash),
                        DisplayName = ReadAttribute(node, "displayName", hash),
                        HashText = hash.Trim(),
                        Tint = tint,
                        Ammo = Math.Max(1, ammo)
                    };
                    XElement components = node.Element("Components");
                    if (components != null)
                    {
                        foreach (XElement component in components.Elements("Component"))
                        {
                            string componentHash = (string)component.Attribute("hash");
                            if (!string.IsNullOrWhiteSpace(componentHash))
                                item.ComponentHashes.Add(componentHash.Trim());
                        }
                    }
                    result.Add(item);
                }
            }
            catch (Exception ex)
            {
                Log("POLICE_WEAPON_XML_ERROR | " + ex.GetType().Name + " | " + ex.Message);
            }
            return result;
        }

        private void SetFavoriteOfficerPed()
        {
            if (_policeCore == null || _policeCore.Config == null)
                return;
            string input = Game.GetUserInput();
            if (string.IsNullOrWhiteSpace(input))
                return;
            input = input.Trim();
            Model model = CreateModel(input);
            if (!model.IsValid || !model.IsPed || !model.Request(1500) || !model.IsLoaded)
            {
                Notification.PostTicker("~r~ANYI LSPD~s~\nInvalid/unavailable favorite ped: " + input, false, false);
                return;
            }
            model.MarkAsNoLongerNeeded();
            _policeCore.Config.FavoriteOfficerModel = input;
            SavePoliceConfigNow();
            ApplyPolicePedSelection(input, true);
        }

        private void SetFavoritePoliceVehicle()
        {
            if (_policeCore == null || _policeCore.Config == null)
                return;
            string input = Game.GetUserInput();
            if (string.IsNullOrWhiteSpace(input))
                return;
            input = input.Trim();
            Model model = CreateModel(input);
            if (!model.IsValid || !model.IsVehicle || !model.Request(1500) || !model.IsLoaded)
            {
                Notification.PostTicker("~r~ANYI LSPD~s~\nInvalid/unavailable favorite vehicle: " + input, false, false);
                return;
            }
            model.MarkAsNoLongerNeeded();
            _policeCore.Config.FavoritePoliceVehicleModel = input;
            SavePoliceConfigNow();
            ApplyPoliceVehicleSelection(input, true);
        }

        private void ApplyPolicePedSelection(string modelName, bool favorite)
        {
            if (_policeCore == null || string.IsNullOrWhiteSpace(modelName))
                return;
            bool ok = _policeCore.ChangeOfficerModel(modelName.Trim());
            if (ok)
            {
                ApplyFavoriteWeapon();
                Notification.PostTicker("~b~ANYI LSPD~s~\nOfficer Ped: " + modelName.Trim() +
                    (favorite ? "\n~c~Favorite applied and saved." : "\n~c~Available preset applied; favorite unchanged."),
                    false, false);
            }
            else
            {
                Notification.PostTicker("~r~ANYI LSPD~s~\nInvalid/unavailable ped: " + modelName.Trim(), false, false);
            }
            RebuildRoleProfileAvailability();
        }

        private void ApplyPoliceVehicleSelection(string modelName, bool favorite)
        {
            if (_policeCore == null || string.IsNullOrWhiteSpace(modelName))
                return;
            bool ok = _policeCore.ChangeVehicleModel(modelName.Trim());
            Notification.PostTicker(
                ok
                    ? "~b~ANYI LSPD~s~\nPolice Vehicle: " + modelName.Trim() +
                      (favorite ? "\n~c~Favorite applied and saved." : "\n~c~Available preset applied; favorite unchanged.")
                    : "~r~ANYI LSPD~s~\nInvalid/unavailable vehicle: " + modelName.Trim(),
                false, false);
            RebuildRoleProfileAvailability();
        }

        private void SetFavoriteWeaponHash()
        {
            if (_policeCore == null || _policeCore.Config == null)
                return;
            string input = Game.GetUserInput();
            if (string.IsNullOrWhiteSpace(input))
                return;
            input = input.Trim();
            uint parsed;
            if (!TryParseHash(input, out parsed))
            {
                Notification.PostTicker("~r~ANYI LSPD~s~\nInvalid weapon hash: " + input, false, false);
                return;
            }
            _policeCore.Config.FavoriteWeaponHash = FormatHash(parsed);
            SavePoliceConfigNow();
            ApplyFavoriteWeapon();
            RebuildRoleProfileAvailability();
        }

        private void ApplyFavoriteWeapon()
        {
            if (_policeCore == null || _policeCore.Config == null)
                return;
            string hashText = _policeCore.Config.FavoriteWeaponHash;
            uint hash;
            if (!TryParseHash(hashText, out hash))
            {
                Log("POLICE_WEAPON_FAVORITE_INVALID | Hash=" + hashText);
                return;
            }

            PoliceWeaponChoice match = null;
            foreach (PoliceWeaponChoice choice in LoadPoliceWeapons())
            {
                if (string.Equals(NormalizeHashText(choice.HashText), NormalizeHashText(hashText), StringComparison.OrdinalIgnoreCase))
                {
                    match = choice;
                    break;
                }
            }

            PoliceWeaponChoice applied = match ?? new PoliceWeaponChoice
            {
                DisplayName = "Favorite Weapon",
                HashText = FormatHash(hash),
                Tint = _policeCore.Config.FavoriteWeaponTint,
                Ammo = _policeCore.Config.FavoriteWeaponAmmo
            };

            if (match != null)
            {
                _policeCore.Config.FavoriteWeaponTint = match.Tint;
                _policeCore.Config.FavoriteWeaponAmmo = match.Ammo;
                SavePoliceConfigNow();
            }

            try
            {
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                    return;
                int weaponHash = unchecked((int)hash);
                Function.Call(GTA.Native.Hash.REMOVE_ALL_PED_WEAPONS, player, false);
                Function.Call(GTA.Native.Hash.GIVE_WEAPON_TO_PED, player, weaponHash, Math.Max(1, applied.Ammo), false, true);
                Function.Call(GTA.Native.Hash.SET_PED_WEAPON_TINT_INDEX, player, weaponHash, Math.Max(0, Math.Min(7, applied.Tint)));
                foreach (string componentText in applied.ComponentHashes)
                {
                    uint componentHash;
                    if (TryParseHash(componentText, out componentHash))
                        Function.Call(GTA.Native.Hash.GIVE_WEAPON_COMPONENT_TO_PED, player, weaponHash, unchecked((int)componentHash));
                }
                Log("POLICE_WEAPON_APPLIED | Weapon=" + applied.DisplayName + " | Hash=" + FormatHash(hash) + " | Ammo=" + applied.Ammo + " | Tint=" + applied.Tint);
                Notification.PostTicker("~b~ANYI LSPD~s~\nPolice weapon loaded: " + applied.DisplayName + "\n~c~" + FormatHash(hash) + " | Ammo=" + applied.Ammo, false, false);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_WEAPON_APPLY_ERROR", ex);
            }
        }

        private void SavePoliceConfigNow()
        {
            try
            {
                AnyiLSPDPoliceConfig.Save(
                    Path.Combine(_scriptDirectory, AnyiLSPDPoliceConfig.FileName),
                    _policeCore.Config);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_PROFILE_CONFIG_SAVE_ERROR", ex);
            }
        }

        private static int ReadIntAttribute(XElement element, string name, int fallback)
        {
            int value;
            return int.TryParse((string)element.Attribute(name), out value) ? value : fallback;
        }

        private static string ReadAttribute(XElement element, string name, string fallback)
        {
            string value = (string)element.Attribute(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static bool TryParseHash(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string cleaned = text.Trim();
            if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned.Substring(2);
            return uint.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static string FormatHash(uint value)
        {
            return "0x" + value.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string NormalizeHashText(string value)
        {
            uint parsed;
            return TryParseHash(value, out parsed) ? FormatHash(parsed) : (value ?? "").Trim().ToUpperInvariant();
        }

        private string TryGetModelHashText(string modelName, bool ped)
        {
            try
            {
                Model model = CreateModel(modelName);
                if (model.IsValid)
                    return FormatHash(unchecked((uint)model.Hash));
            }
            catch { }
            return "unknown";
        }

        private void BuildGangPedMenu()
        {
            AddDisabledItem(_pedProfile, "GANG TURF PED MODEL", "Existing Gang & Turf model source remains in the current Gang core.");
            NativeItem type = new NativeItem("Type Gang Ped Model", "Type a gang ped model or numeric hash.");
            type.Activated += delegate
            {
                string input = Game.GetUserInput();
                if (string.IsNullOrWhiteSpace(input)) return;
                Model model = CreateModel(input.Trim());
                if (!model.IsValid || !model.IsPed || !model.Request(1500) || !model.IsLoaded)
                {
                    Notification.PostTicker("~r~ANYI GANG~s~\nInvalid/unavailable ped: " + input.Trim(), false, false);
                    return;
                }
                Game.Player.ChangeModel(model);
                model.MarkAsNoLongerNeeded();
                Notification.PostTicker("~b~ANYI GANG~s~\nGang ped applied: " + input.Trim(), false, false);
            };
            _pedProfile.Add(type);

            if (_gangCore != null)
            {
                List<int> hashes = _gangCore.GetPlayerGangMemberModelHashes();
                foreach (int hash in hashes)
                {
                    string value = hash.ToString();
                    NativeItem item = new NativeItem("Gang Ped " + value, "Apply live player-owned Gang member model hash.");
                    item.Activated += delegate
                    {
                        Model model = new Model(hash);
                        if (model.Request(1500) && model.IsLoaded && model.IsPed)
                        {
                            Game.Player.ChangeModel(model);
                            model.MarkAsNoLongerNeeded();
                            Notification.PostTicker("~b~ANYI GANG~s~\nGang ped applied: " + hash, false, false);
                        }
                    };
                    _pedProfile.Add(item);
                }
            }
        }

        private void BuildGangVehicleMenu()
        {
            AddDisabledItem(_vehicles, "GANG TURF VEHICLES", "Existing Gang & Turf vehicle pool remains the source of truth.");
            NativeItem type = new NativeItem("Type Gang Vehicle Model", "Type a gang vehicle model or numeric hash.");
            type.Activated += delegate
            {
                string input = Game.GetUserInput();
                if (string.IsNullOrWhiteSpace(input)) return;
                SpawnVehicleModel(input.Trim());
            };
            _vehicles.Add(type);

            if (_gangCore != null)
            {
                foreach (int hash in _gangCore.GetPlayerGangVehicleModelHashes())
                {
                    int modelHash = hash;
                    NativeItem item = new NativeItem("Gang Vehicle " + hash, "Spawn live player-owned Gang vehicle model hash.");
                    item.Activated += delegate { SpawnVehicleModel(modelHash.ToString()); };
                    _vehicles.Add(item);
                }
            }
        }

        private void SpawnVehicleModel(string input)
        {
            try
            {
                Model model = CreateModel(input);
                if (!model.IsValid || !model.IsVehicle || !model.Request(1500) || !model.IsLoaded)
                {
                    Notification.PostTicker("~r~ANYI~s~\nInvalid/unavailable vehicle: " + input, false, false);
                    return;
                }
                Ped player = Game.Player.Character;
                if (player == null || !player.Exists()) return;
                Vehicle vehicle = World.CreateVehicle(model, player.Position + player.ForwardVector * 4f, player.Heading);
                if (vehicle != null && vehicle.Exists())
                {
                    vehicle.IsPersistent = true;
                    vehicle.PlaceOnGround();
                    player.SetIntoVehicle(vehicle, VehicleSeat.Driver);
                    Notification.PostTicker("~b~ANYI~s~\nVehicle spawned: " + input, false, false);
                }
                model.MarkAsNoLongerNeeded();
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("UI_VEHICLE_SPAWN_ERROR", ex);
            }
        }

        private void AddCitizenActionItem(string action, string description)
        {
            NativeItem item = new NativeItem(action, description);
            item.Activated += delegate
            {
                LSPDCitizenCore core = LSPDCitizenCore.Instance;
                string result;
                if (core == null) result = "Citizen core is unavailable.";
                else if (action == "Greet Police") result = core.GreetPolice();
                else if (action == "Interact with Police") result = core.InteractWithPolice();
                else if (action == "Assure / Cooperate") result = core.MakeAssurance();
                else result = core.CallDispatch();
                Notification.PostTicker("~b~LSPD Response~s~\n" + action + "\n~c~" + result, false, false);
                Log("CITIZEN_ACTION | " + action + " | " + result);
            };
            _selectedRole.Add(item);
        }

        private void AddGangActionItem(string action, string description)
        {
            NativeItem item = new NativeItem(action, description);
            item.Activated += delegate
            {
                string result;
                if (_gangCore == null) result = "Gang core is unavailable.";
                else if (action == "Greet Gang Member") result = _gangCore.GreetGangMember();
                else if (action == "Interact with Gang Member") result = _gangCore.InteractWithGangMember();
                else if (action == "Territory Status") result = _gangCore.TerritoryStatus();
                else result = _gangCore.ResetGangResponse();
                Notification.PostTicker("~b~LSPD Response~s~\n" + action + "\n~c~" + result, false, false);
                Log("GANG_ACTION | " + action + " | " + result);
            };
            _selectedRole.Add(item);
        }

        private void SelectRole(LspdResponseRole role)
        {
            _config.ActiveRole = role;
            _config.Save(_configPath, Log);

            if (_policeCore != null)
                _policeCore.UpdateRole(role);

            if (role == LspdResponseRole.PoliceAuthority)
                ApplyFavoriteWeapon();

            if (_gangCore != null)
                _gangCore.RequestImmediateRefresh();

            LSPDCitizenCore citizenCore = LSPDCitizenCore.Instance;
            if (citizenCore != null)
                citizenCore.RequestImmediateRefresh();

            RebuildRoleProfileAvailability();
            RebuildSelectedRoleMenu();
            RebuildPoliceLocationMenu();
            BuildControlsMenu();
            BuildDiagnosticsMenu();
            CloseAllMenus();
            _root.Visible = true;

            string layerMessage = DisplayRole(role) == "Police Authority"
                ? "Police authority, patrol, dispatch and custody are active."
                : DisplayRole(role) == "Gang Turf Leader"
                    ? "Gang & Turf layer active."
                    : "Citizen layer active.";

            Log("ROLE_SELECTED | " + DisplayRole(role));
            Notification.PostTicker("~b~LSPD Response~s~\nRole selected: " + DisplayRole(role) + "\n~c~" + layerMessage, false, false);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                if (now >= _nextEmbeddedHeartbeat)
                {
                    WriteEmbeddedHeartbeat("UI alive | Role=" + DisplayRole(_config.ActiveRole) + " | Police=" + (_policeCore == null ? "null" : _policeCore.StatusLine) + " | Gang=" + (_gangCore == null ? "null" : _gangCore.StatusLine));
                    _nextEmbeddedHeartbeat = now.AddSeconds(15);
                }

                if (Rising(_config.MenuToggleKey, ref _menuKeyWasDown))
                {
                    _root.Visible = !_root.Visible;
                    Log("MENU_TOGGLE | Visible=" + _root.Visible);
                }

                _pool.Process();
                if (_gangCore != null)
                    _gangCore.Update();
                if (_policeCore != null)
                {
                    _policeCore.Update();
                    _policeCore.ProcessShortcutKeys(!_root.Visible);
                }
            }
            catch (Exception ex)
            {
                Log("UI_ERROR | " + ex.GetType().Name + " | " + ex.Message);
            }
        }

        private void OnAborted(object sender, EventArgs e)
        {
            CloseAllMenus();
            try { if (_policeCore != null) _policeCore.Shutdown(); } catch { }
            try { if (_gangCore != null) _gangCore.Shutdown(); } catch { }
            try { WriteReport("Script aborted or GTA is closing."); } catch { }
            Log("STOP | Anyi LSPD MainUI v5.2 stopped.");
        }

        private void CloseAllMenus()
        {
            foreach (NativeMenu menu in _menus)
                menu.Visible = false;
        }

        private void AddMenuToPool(NativeMenu menu)
        {
            _menus.Add(menu);
            _pool.Add(menu);
        }

        private NativeMenu CreateMenu(string title, string subtitle)
        {
            NativeMenu menu = new NativeMenu(title, subtitle);
            ScaledRectangle banner = new ScaledRectangle(new PointF(0.0f, 0.0f), new SizeF(432.0f, 105.0f));
            banner.Color = HeaderBlue;
            menu.Banner = banner;
            return menu;
        }

        private void AddRoleItem(string label, LspdResponseRole role, string description)
        {
            NativeItem item = new NativeItem(label, description);
            item.Activated += delegate { SelectRole(role); };
            _roleplay.Add(item);
        }

        private static void AddDisabledItem(NativeMenu menu, string title, string description)
        {
            NativeItem item = new NativeItem(title, description);
            item.Enabled = false;
            menu.Add(item);
        }

        private static bool Rising(Keys key, ref bool previous)
        {
            bool down = (GetAsyncKeyState((int)key) & 0x8000) != 0;
            bool rising = down && !previous;
            previous = down;
            return rising;
        }


        private void Log(string message)
        {
            try
            {
                LspdResponseLog.Write("UI", message ?? string.Empty);
            }
            catch
            {
                WriteEmbeddedLog("UI", message ?? string.Empty);
            }
        }

        private void WriteReport(string reason)
        {
            List<string> lines = new List<string>();
            lines.Add("Reason: " + reason);
            lines.Add("Selected role: " + DisplayRole(_config.ActiveRole));
            lines.Add("Police: " + (_policeCore == null ? "null" : _policeCore.StatusLine));
            lines.Add("Gang: " + (_gangCore == null ? "null" : _gangCore.StatusLine));
            lines.Add("Citizen core: " + (LSPDCitizenCore.Instance == null ? "null" : "loaded"));
            lines.Add("Runtime log: " + EmbeddedRuntimeLogPath);
            lines.Add("Evidence UI: removed from Police Authority.");
            lines.Add("Police core owns only Police Authority runtime state.");
            LspdResponseLog.WriteReport("ANYI LSPD MAIN UI V4 REPORT", lines);
        }

        private static string DisplayRole(LspdResponseRole role)
        {
            switch (role)
            {
                case LspdResponseRole.PoliceAuthority: return "Police Authority";
                case LspdResponseRole.GangTurfLeader: return "Gang Turf Leader";
                default: return "Los Santos Citizen";
            }
        }

        private static Model CreateModel(string value)
        {
            int hash;
            return int.TryParse(value, out hash) ? new Model(hash) : new Model(value);
        }

        private static string ResolveScriptDirectory()
        {
            return AnyiLSPDPathProvider.ScriptsDirectory;
        }
    }
}

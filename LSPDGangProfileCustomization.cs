using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Windows.Forms;
using System.Reflection;
using GTA;
using GTA.UI;
using GTA.Math;
using LemonUI.Menus;


namespace AnyiLSPD
{
    /// <summary>
    /// Gang Turf Boss profile customization.
    ///
    /// Purpose:
    /// - Gives Gang Turf Role a real Ped Profile menu.
    /// - Gives Gang Turf Role a real Vehicles Customization menu.
    /// - Supports model names AND numeric hashes.
    /// - Does not depend on Gang & Turf returning a populated member/vehicle list.
    /// - Loads optional preset entries from LSPDResponse.Gang.Profile.xml.
    /// - Uses SHVDN v3-compatible APIs only.
    ///
    /// Integration is deliberately small: create this class from LSPDMainUI and add
    /// its two returned NativeItem objects to your existing Role Profile menu.
    /// </summary>
    public sealed class LSPDGangProfileCustomization
    {



        private static readonly object EmbeddedLogSync = new object();
        private static DateTime _nextDirectHeartbeat = DateTime.MinValue;

        private static string EmbeddedLogPath
        {
            get
            {
                try
                {
                    string location = Assembly.GetExecutingAssembly().Location;
                    string directory = Path.GetDirectoryName(location);

                    if (!string.IsNullOrWhiteSpace(directory))
                        return Path.Combine(directory, "AnyiLSPD_DEBUG_DIRECT.log");
                }
                catch
                {
                }

                return Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "scripts",
                    "AnyiLSPD_DEBUG_DIRECT.log");
            }
        }
    private static void DirectLog(string category, string message)
        {
            try
            {
                string path = EmbeddedLogPath;
                string directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
               
               
                lock (EmbeddedLogSync)
                {
                    using (FileStream stream = new FileStream(
                        path,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite))
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                            " | " +
                            (string.IsNullOrWhiteSpace(category) ? "LOG" : category) +
                            " | " +
                            (message ?? string.Empty));

                        writer.Flush();
                    }
                }
            }
            catch
            {
                // Never let diagnostics kill the GTA script.
            }
        }

        private static void DirectException(string category, Exception ex)
        {
            if (ex == null)
                return;

            DirectLog(
                category,
                ex.GetType().FullName +
                " | Message=" + ex.Message +
                " | Stack=" + ex.StackTrace);
        }

        private readonly string _scriptDirectory;
        private readonly string _profileXmlPath;
        private readonly Action<string> _log;

        private readonly List<GangProfileEntry> _pedPresets = new List<GangProfileEntry>();
        private readonly List<GangProfileEntry> _vehiclePresets = new List<GangProfileEntry>();

        private NativeMenu _pedMenu;
        private NativeMenu _vehicleMenu;

        public NativeMenu PedMenu => _pedMenu;
        public NativeMenu VehicleMenu => _vehicleMenu;

        public LSPDGangProfileCustomization(string scriptDirectory, Action<string> log = null)
        {
            _scriptDirectory = string.IsNullOrWhiteSpace(scriptDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : scriptDirectory;

            _profileXmlPath = Path.Combine(_scriptDirectory, "LSPDResponse.Gang.Profile.xml");
            _log = log ?? DefaultLog;

            LoadProfileXml();
        }

        public void BuildMenus(NativeMenu parentRoleProfileMenu)
        {
            if (parentRoleProfileMenu == null)
                throw new ArgumentNullException(nameof(parentRoleProfileMenu));

            BuildPedMenu(parentRoleProfileMenu);
            BuildVehicleMenu(parentRoleProfileMenu);
        }

        private void BuildPedMenu(NativeMenu parent)
        {
            _pedMenu = new NativeMenu("Ped Profile", "GANG TURF PED MODEL");

            NativeItem typedModel = new NativeItem("Enter Ped Model", "Type an addon, vanilla, DLC, or numeric ped hash.");
            typedModel.Activated += delegate
            {
                string input = Game.GetUserInput(
                GTA.WindowTitle.EnterMessage60,"",60);

                if (string.IsNullOrWhiteSpace(input))
                {
                    Notify("Ped input cancelled.");
                    return;
                }

                ApplyPedModel(input.Trim());
            };
            _pedMenu.Add(typedModel);

            NativeItem currentModel = new NativeItem("Use Current Gang Ped Presets", "Shows configured Gang Turf ped models from XML.");
            currentModel.Activated += delegate
            {
                LoadProfileXml();
                RebuildPedPresetItems();
                Notify("Gang Turf ped presets refreshed.");
            };
            _pedMenu.Add(currentModel);

            RebuildPedPresetItems();

            NativeItem backNote = new NativeItem("Gang Turf Ped Source", "Configured presets are read from LSPDResponse.Gang.Profile.xml.");
            backNote.Enabled = false;
            _pedMenu.Add(backNote);

            parent.Add(new NativeItem("Ped Profile", "Gang Turf player ped / model changer.")
            {
                // No object initializer event wiring here; see below.
            });

            NativeItem open = parent.Items[parent.Items.Count - 1] as NativeItem;
            if (open != null)
                open.Activated += delegate { _pedMenu.Visible = true; };
        }

        private void BuildVehicleMenu(NativeMenu parent)
        {
            _vehicleMenu = new NativeMenu("Gang Vehicles", "GANG TURF VEHICLE PROFILE");

            NativeItem typedModel = new NativeItem("Enter Vehicle Model", "Type an addon, vanilla, DLC, or numeric vehicle hash.");
            typedModel.Activated += delegate
            {
                string input = Game.GetUserInput(
               GTA.WindowTitle.EnterMessage60, "", 60);

                if (string.IsNullOrWhiteSpace(input))
                {
                    Notify("Vehicle input cancelled.");
                    return;
                }

                SpawnVehicle(input.Trim());
            };
            _vehicleMenu.Add(typedModel);

            NativeItem refresh = new NativeItem("Use Current Gang Vehicle Presets", "Refreshes the configured Gang Turf vehicle pool from XML.");
            refresh.Activated += delegate
            {
                LoadProfileXml();
                RebuildVehiclePresetItems();
                Notify("Gang Turf vehicle presets refreshed.");
            };
            _vehicleMenu.Add(refresh);

            RebuildVehiclePresetItems();

            NativeItem backNote = new NativeItem("Gang Turf Vehicle Source", "Configured presets are read from LSPDResponse.Gang.Profile.xml.");
            backNote.Enabled = false;
            _vehicleMenu.Add(backNote);

            parent.Add(new NativeItem("Vehicles Customization", "Gang Turf vehicle model selector / spawner."));
            NativeItem open = parent.Items[parent.Items.Count - 1] as NativeItem;
            if (open != null)
                open.Activated += delegate { _vehicleMenu.Visible = true; };
        }

        private void RebuildPedPresetItems()
        {
            // LemonUI's NativeMenu API in your current reference does not expose the
            // Size/RemoveAt members used by the broken build. We therefore rebuild the
            // menu by adding only preset entries that are not already represented.
            HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _pedMenu.Items)
                existing.Add(item.Title);

            foreach (GangProfileEntry preset in _pedPresets)
            {
                string title = "Gang Ped: " + preset.DisplayName;
                if (existing.Contains(title))
                    continue;

                string modelInput = preset.Model;
                NativeItem item = new NativeItem(title, "Apply " + modelInput + " to Anyi.");
                item.Activated += delegate { ApplyPedModel(modelInput); };
                _pedMenu.Add(item);
                existing.Add(title);
            }

            if (_pedPresets.Count == 0)
            {
                AddDisabledOnce(_pedMenu, "No preset peds configured", "Use Enter Ped Model or add <Ped> entries to the XML.");
            }
        }

        private void RebuildVehiclePresetItems()
        {
            HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _vehicleMenu.Items)
                existing.Add(item.Title);

            foreach (GangProfileEntry preset in _vehiclePresets)
            {
                string title = "Gang Vehicle: " + preset.DisplayName;
                if (existing.Contains(title))
                    continue;

                string modelInput = preset.Model;
                NativeItem item = new NativeItem(title, "Spawn " + modelInput + " for Anyi.");
                item.Activated += delegate { SpawnVehicle(modelInput); };
                _vehicleMenu.Add(item);
                existing.Add(title);
            }

            if (_vehiclePresets.Count == 0)
            {
                AddDisabledOnce(_vehicleMenu, "No preset vehicles configured", "Use Enter Vehicle Model or add <Vehicle> entries to the XML.");
            }
        }

        private void AddDisabledOnce(NativeMenu menu, string title, string description)
        {
            foreach (var item in menu.Items)
            {
                if (string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            NativeItem disabled = new NativeItem(title, description);
            disabled.Enabled = false;
            menu.Add(disabled);
        }

        public bool ApplyPedModel(string modelInput)
        {
            try
            {
                Model model;
                if (!TryCreateModel(modelInput, out model))
                {
                    Notify("Invalid ped model: " + modelInput);
                    _log("PED_PROFILE_INVALID | Input=" + modelInput);
                    return false;
                }

                if (!model.IsPed)
                {
                    Notify("That model is not a ped: " + modelInput);
                    _log("PED_PROFILE_NOT_PED | Input=" + modelInput + " | Hash=" + model.Hash);
                    return false;
                }

                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                {
                    Notify("Player character is unavailable.");
                    _log("PED_PROFILE_ERROR | Player character unavailable.");
                    return false;
                }

                Vector3 originalPosition = player.Position;
                float originalHeading = player.Heading;

                // SHVDN v3: Player.ChangeModel is the supported model-change path.
                // Do NOT assign player.Model; Entity.Model is read-only.
                if (!Game.Player.ChangeModel(model))
                {
                    Notify("Ped model could not be loaded: " + modelInput);
                    _log("PED_PROFILE_LOAD_FAIL | Input=" + modelInput + " | Hash=" + model.Hash);
                    model.MarkAsNoLongerNeeded();
                    return false;
                }

                Ped changedPlayer = Game.Player.Character;
                if (changedPlayer != null && changedPlayer.Exists())
                {
                    changedPlayer.Position = originalPosition;
                    changedPlayer.Heading = originalHeading;
                    changedPlayer.Style.SetDefaultClothes();
                }

                _log("PED_PROFILE_APPLIED | Input=" + modelInput + " | Hash=" + model.Hash);
                Notify("Gang Turf ped applied: " + modelInput);
                return true;
            }
            catch (Exception ex)
            {
                _log("PED_PROFILE_EXCEPTION | Input=" + modelInput + " | " + ex);
                Notify("Ped profile error. Check LSPDResponseLog.txt.");
                return false;
            }
        }

        public Vehicle SpawnVehicle(string modelInput)
        {
            try
            {
                Model model;
                if (!TryCreateModel(modelInput, out model))
                {
                    Notify("Invalid vehicle model: " + modelInput);
                    _log("VEHICLE_PROFILE_INVALID | Input=" + modelInput);
                    return null;
                }

                if (!model.IsVehicle)
                {
                    Notify("That model is not a vehicle: " + modelInput);
                    _log("VEHICLE_PROFILE_NOT_VEHICLE | Input=" + modelInput + " | Hash=" + model.Hash);
                    return null;
                }

                Ped player = Game.Player.Character;
                if (player == null || !player.Exists())
                {
                    Notify("Player character is unavailable.");
                    _log("VEHICLE_PROFILE_ERROR | Player character unavailable.");
                    model.MarkAsNoLongerNeeded();
                    return null;
                }

                Vector3 spawnPosition = player.Position + player.ForwardVector * 5.0f;
                float heading = player.Heading;

                Vehicle vehicle = World.CreateVehicle(model, spawnPosition, heading);
                if (vehicle == null || !vehicle.Exists())
                {
                    Notify("Vehicle could not be spawned: " + modelInput);
                    _log("VEHICLE_PROFILE_SPAWN_FAIL | Input=" + modelInput + " | Hash=" + model.Hash);
                    model.MarkAsNoLongerNeeded();
                    return null;
                }

                vehicle.IsPersistent = true;
                vehicle.PlaceOnGround();
                vehicle.Heading = heading;

                _log("VEHICLE_PROFILE_SPAWNED | Input=" + modelInput + " | Hash=" + model.Hash + " | Handle=" + vehicle.Handle);
                Notify("Gang Turf vehicle spawned: " + modelInput);

                model.MarkAsNoLongerNeeded();
                return vehicle;
            }
            catch (Exception ex)
            {
                _log("VEHICLE_PROFILE_EXCEPTION | Input=" + modelInput + " | " + ex);
                Notify("Vehicle profile error. Check LSPDResponseLog.txt.");
                return null;
            }
        }

        private static bool TryCreateModel(string input, out Model model)
        {
            model = null;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string trimmed = input.Trim();
            int numericHash;

            if (int.TryParse(trimmed, out numericHash))
            {
                model = new Model(numericHash);
                return model.IsValid;
            }

            model = new Model(trimmed);
            return model.IsValid;
        }

        private void LoadProfileXml()
        {
            _pedPresets.Clear();
            _vehiclePresets.Clear();

            try
            {
                if (!File.Exists(_profileXmlPath))
                {
                    _log("PROFILE_XML_MISSING | " + _profileXmlPath);
                    return;
                }

                XDocument doc = XDocument.Load(_profileXmlPath);
                XElement root = doc.Root;
                if (root == null)
                    return;

                XElement peds = root.Element("PedPresets");
                if (peds != null)
                {
                    foreach (XElement node in peds.Elements("Ped"))
                    {
                        string model = (string)node.Attribute("model");
                        if (string.IsNullOrWhiteSpace(model))
                            continue;

                        _pedPresets.Add(new GangProfileEntry(
                            (string)node.Attribute("name") ?? model,
                            model));
                    }
                }

                XElement vehicles = root.Element("VehiclePresets");
                if (vehicles != null)
                {
                    foreach (XElement node in vehicles.Elements("Vehicle"))
                    {
                        string model = (string)node.Attribute("model");
                        if (string.IsNullOrWhiteSpace(model))
                            continue;

                        _vehiclePresets.Add(new GangProfileEntry(
                            (string)node.Attribute("name") ?? model,
                            model));
                    }
                }

                _log("PROFILE_XML_LOADED | Peds=" + _pedPresets.Count + " | Vehicles=" + _vehiclePresets.Count + " | Path=" + _profileXmlPath);
            }
            catch (Exception ex)
            {
                _log("PROFILE_XML_ERROR | " + ex);
            }
        }

        private void Notify(string message)
        {
            Notification.PostTicker("~b~LSPD Response~s~~n~" + message, false, false);
        }

        private void DefaultLog(string message)
        {
            try
            {
                Directory.CreateDirectory(_scriptDirectory);
                string path = Path.Combine(_scriptDirectory, "LSPDResponseLog.txt");
                File.AppendAllText(path,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
            }
            catch
            {
                // Never let diagnostic logging crash the game script.
            }
        }

        private sealed class GangProfileEntry
        {
            public GangProfileEntry(string displayName, string model)
            {
                DisplayName = displayName;
                Model = model;
            }

            public string DisplayName { get; }
            public string Model { get; }
        }
    }
}

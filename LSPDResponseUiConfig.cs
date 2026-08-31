using System;
using System.IO;
using System.Windows.Forms;
using System.Xml.Linq;

namespace AnyiLSPD
{
    public enum LspdResponseRole
    {
        GangTurfLeader,
        PoliceAuthority,
        LosSantosCitizen
    }

    // This configuration belongs only to the first UI-skeleton milestone.
    // It deliberately contains no dispatch, AI, ped, vehicle, or Gang & Turf logic.
    public sealed class LspdResponseUiConfig
    {
        public const string FileName = "LSPDResponse.UI.xml";

        public string MenuTitle = "LSPD Response 1.0";
        public string MenuSubtitle = "IMMERSIVE LSPD AUTHORITY";
        public Keys MenuToggleKey = Keys.F6;
        public LspdResponseRole ActiveRole =
            LspdResponseRole.LosSantosCitizen;

        public static LspdResponseUiConfig LoadOrCreate(
            string path,
            Action<string> log)
        {
            LspdResponseUiConfig config = new LspdResponseUiConfig();

            try
            {
                if (!File.Exists(path))
                {
                    SaveDocument(path, CreateDocument(config));
                    log("CONFIG | Created " + FileName);
                    return config;
                }

                XDocument document = XDocument.Load(path);
                XElement root = document.Root;
                if (root == null || root.Name != "LspdResponseUi")
                {
                    log("CONFIG_WARNING | Invalid root in " + FileName +
                        "; using safe defaults.");
                    return config;
                }

                XElement menu = root.Element("Menu");
                if (menu != null)
                {
                    config.MenuTitle = ReadString(
                        menu,
                        "title",
                        config.MenuTitle);

                    config.MenuSubtitle = ReadString(
                        menu,
                        "subtitle",
                        config.MenuSubtitle);
                }

                XElement controls = root.Element("Controls");
                if (controls != null)
                {
                    string keyText = ReadString(
                        controls,
                        "menuToggle",
                        config.MenuToggleKey.ToString());

                    Keys parsedKey;
                    if (Enum.TryParse(keyText, true, out parsedKey))
                        config.MenuToggleKey = parsedKey;
                    else
                        log("CONFIG_WARNING | Unknown menuToggle '" +
                            keyText + "'; using F6.");
                }

                XElement role = root.Element("Role");
                if (role != null)
                {
                    string roleText = ReadString(
                        role,
                        "active",
                        config.ActiveRole.ToString());

                    LspdResponseRole parsedRole;
                    if (Enum.TryParse(roleText, true, out parsedRole))
                        config.ActiveRole = parsedRole;
                    else
                        log("CONFIG_WARNING | Unknown active role '" +
                            roleText + "'; using Los Santos Citizen.");
                }
            }
            catch (Exception ex)
            {
                log("CONFIG_ERROR | " + ex.GetType().Name + " | " +
                    ex.Message + " | Safe UI defaults were kept.");
            }

            return config;
        }

        public void Save(string path, Action<string> log)
        {
            try
            {
                SaveDocument(path, CreateDocument(this));
                log("CONFIG | Saved selected role: " + ActiveRole);
            }
            catch (Exception ex)
            {
                log("CONFIG_ERROR | Could not save selected role | " +
                    ex.GetType().Name + " | " + ex.Message);
            }
        }

        private static XDocument CreateDocument(
            LspdResponseUiConfig config)
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement("LspdResponseUi",
                    new XAttribute("version", "1.0"),
                    new XElement("Menu",
                        new XAttribute("title", config.MenuTitle),
                        new XAttribute("subtitle", config.MenuSubtitle)),
                    new XElement("Controls",
                        new XAttribute(
                            "menuToggle",
                            config.MenuToggleKey)),
                    new XElement("Role",
                        new XAttribute("active", config.ActiveRole)),
                    new XElement("CitizenProfile",
                        new XComment(
                            "UI preferences only. No police or NPC behavior is enabled in this first build."),
                        new XElement("GreetPolice",
                            new XAttribute("visible", "true")),
                        new XElement("InteractWithPolice",
                            new XAttribute("visible", "true")),
                        new XElement("MakeAssurance",
                            new XAttribute("visible", "true")),
                        new XElement("CallDispatch",
                            new XAttribute("visible", "true")))));
        }

        private static void SaveDocument(string path, XDocument document)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) &&
                !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            document.Save(path);
        }

        private static string ReadString(
            XElement element,
            string attributeName,
            string fallback)
        {
            XAttribute attribute = element.Attribute(attributeName);
            if (attribute == null ||
                string.IsNullOrWhiteSpace(attribute.Value))
            {
                return fallback;
            }

            return attribute.Value.Trim();
        }
    }
}

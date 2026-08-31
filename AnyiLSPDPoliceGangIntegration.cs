using GTA;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AnyiLSPD
{
    /// <summary>
    /// Police-mode-only bridge into Gang & Turf data.
    /// It does not modify GangData.xml, MemberPool.xml, TurfZoneData.xml, or the Gang & Turf mod.
    /// It also does not create a Dispatch/Convoy job; it owns only the local gang-attack incident state.
    /// </summary>
    public sealed class AnyiLSPDPoliceGangIntegration
    {
        private sealed class ActiveGangIncident
        {
            public string GangName = "Unknown Gang";
            public string TerritoryName = "none";
            public bool VanillaGang;
            public bool ProtectingPlayerGangMember;
            public readonly HashSet<int> ThreatHandles = new HashSet<int>();
            public DateTime StartedUtc = DateTime.UtcNow;
            public DateTime LastThreatSeenUtc = DateTime.UtcNow;
            public DateTime NoThreatSinceUtc = DateTime.MinValue;
        }

        private readonly HashSet<int> _vanillaGangHashes = new HashSet<int>();
        private readonly Dictionary<int, DateTime> _courtesyUntil = new Dictionary<int, DateTime>();
        private ActiveGangIncident _activeIncident;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private readonly AnyiLSPDPoliceIntegrationConfig _config;

        // The seven personal Anyiii/Genshin member hashes already present in Anyiii's GangData.xml.
        private static readonly Dictionary<int, string> KnownAnyiiiCompanions = new Dictionary<int, string>
        {
            { 343272203, "Shenhe" },
            { 1957851257, "Raiden" },
            { -314526266, "Navia" },
            { 834197053, "Clorinde" },
            { -1205420430, "Nefer" },
            { 1823612999, "Arlecchino" },
            { -514572009, "Venti" }
        };

        public bool HasActiveIncident { get { return _activeIncident != null; } }

        public AnyiLSPDPoliceGangIntegration(AnyiLSPDPoliceIntegrationConfig config)
        {
            _config = config;
            string[] names =
            {
                "g_m_y_ballaeast_01", "g_m_y_ballaorig_01", "g_m_y_ballasout_01",
                "g_m_y_famca_01", "g_m_y_famdnf_01", "g_m_y_famfor_01",
                "g_m_y_mexgang_01", "g_m_y_mexgoon_01", "g_m_y_mexgoon_02",
                "g_m_y_lost_01", "g_m_y_lost_02", "g_m_y_lost_03",
                "g_m_y_vagos_01", "g_m_y_salvaboss_01", "g_m_y_salvagoon_01"
            };
            foreach (string name in names)
                _vanillaGangHashes.Add(unchecked((int)StringHash.AtStringHash(name, 0)));
        }

        public void Reset()
        {
            _activeIncident = null;
            _cooldownUntil = DateTime.MinValue;
            _courtesyUntil.Clear();
        }

        public void Update(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData, AnyiLSPDChaosAudio audio)
        {
            if (!_config.EnableGangAuthorityIntegration || player == null || !player.Exists() || nearby == null)
                return;

            DateTime now = DateTime.UtcNow;
            try
            {
                ApplyAnyiiiGangCourtesy(player, nearby, gangData, now);

                if (_activeIncident != null)
                {
                    UpdateActiveIncident(player, nearby, gangData, audio, now);
                    return;
                }

                if (now < _cooldownUntil)
                    return;

                Ped attacker;
                string gangName;
                bool vanilla;
                bool protectingMember;
                string territory;

                if (!TryFindHostileIncident(player, nearby, gangData, out attacker, out gangName, out vanilla, out protectingMember, out territory))
                    return;

                StartIncident(player, attacker, gangName, territory, vanilla, protectingMember, audio, now);
            }
            catch (Exception ex)
            {
                LspdResponseLog.WriteException("POLICE_GANG_INTEGRATION_ERROR", ex);
            }
        }

        private void ApplyAnyiiiGangCourtesy(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData, DateTime now)
        {
            if (gangData == null || gangData.PlayerGang == null)
                return;

            foreach (Ped member in nearby)
            {
                try
                {
                    if (member == null || !member.Exists() || member.IsDead || member.Handle == player.Handle)
                        continue;
                    if (member.Position.DistanceTo(player.Position) > _config.GangCourtesyRadius)
                        continue;
                    if (!gangData.PlayerGang.MemberHashes.Contains(member.Model.Hash) && !KnownAnyiiiCompanions.ContainsKey(member.Model.Hash))
                        continue;

                    string owner = gangData.GetTerritoryOwner(member.Position.X, member.Position.Y, member.Position.Z);
                    if (!string.Equals(owner, gangData.PlayerGang.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (member.IsInCombatAgainst(player) || member.IsShooting)
                        continue;

                    DateTime until;
                    if (_courtesyUntil.TryGetValue(member.Handle, out until) && now < until)
                        continue;

                    // Gentle greeting only. We intentionally do not change relationship groups or Gang & Turf state.
                    member.Task.LookAt(player, 1800);
                    _courtesyUntil[member.Handle] = now.AddSeconds(_config.GangCourtesyCooldownSeconds);

                    string displayName = KnownAnyiiiCompanions.ContainsKey(member.Model.Hash)
                        ? KnownAnyiiiCompanions[member.Model.Hash]
                        : "Anyiii's Gang member";

                    Notification.PostTicker(
                        "~b~ANYI LSPD~s~\nANYIII'S GANG MEMBER\n~c~" + displayName + " recognizes Officer Anyi and remains friendly in Anyiii's territory.",
                        false,
                        false);
                    LspdResponseLog.Write(
                        "POLICE_GANG_COURTESY",
                        "Friendly Anyiii gang member near Police Authority | Member=" + member.Handle + " | Model=" + member.Model.Hash + " | Name=" + displayName + " | Territory=" + owner);
                }
                catch { }
            }
        }

        private bool TryFindHostileIncident(
            Ped player,
            Ped[] nearby,
            AnyiLSPDPoliceData.GangSnapshot gangData,
            out Ped attacker,
            out string gangName,
            out bool vanillaGang,
            out bool protectingMember,
            out string territory)
        {
            attacker = null;
            gangName = "Unknown Gang";
            vanillaGang = false;
            protectingMember = false;
            territory = "none";

            AnyiLSPDPoliceData.GangProfile playerGang = gangData == null ? null : gangData.PlayerGang;

            foreach (Ped ped in nearby)
            {
                if (!IsPotentialAttacker(ped, player))
                    continue;

                AnyiLSPDPoliceData.GangProfile profile = gangData == null ? null : gangData.FindGangForModel(ped.Model.Hash);
                if (profile != null)
                {
                    if (profile.PlayerOwned)
                        continue;

                    attacker = ped;
                    gangName = profile.Name;
                    vanillaGang = false;
                    protectingMember = false;
                    territory = ResolveTerritory(gangData, ped.Position);
                    return true;
                }

                if (_vanillaGangHashes.Contains(ped.Model.Hash))
                {
                    attacker = ped;
                    gangName = "Vanilla Gang";
                    vanillaGang = true;
                    territory = ResolveTerritory(gangData, ped.Position);
                    return true;
                }
            }

            if (playerGang != null)
            {
                foreach (Ped member in nearby)
                {
                    try
                    {
                        if (member == null || !member.Exists() || member.IsDead)
                            continue;
                        if (member.Position.DistanceTo(player.Position) > _config.GangDetectionRadius)
                            continue;
                        if (!playerGang.MemberHashes.Contains(member.Model.Hash) && !KnownAnyiiiCompanions.ContainsKey(member.Model.Hash))
                            continue;

                        foreach (Ped enemy in nearby)
                        {
                            if (!IsPotentialAttacker(enemy, member))
                                continue;

                            AnyiLSPDPoliceData.GangProfile enemyProfile = gangData.FindGangForModel(enemy.Model.Hash);
                            if (enemyProfile != null && enemyProfile.PlayerOwned)
                                continue;
                            if (enemyProfile == null && !_vanillaGangHashes.Contains(enemy.Model.Hash))
                                continue;

                            attacker = enemy;
                            gangName = enemyProfile == null ? "Vanilla Gang" : enemyProfile.Name;
                            vanillaGang = enemyProfile == null;
                            protectingMember = true;
                            territory = ResolveTerritory(gangData, member.Position);
                            return true;
                        }
                    }
                    catch { }
                }
            }

            return false;
        }

        private void StartIncident(Ped player, Ped attacker, string gangName, string territory, bool vanillaGang, bool protectingMember, AnyiLSPDChaosAudio audio, DateTime now)
        {
            _activeIncident = new ActiveGangIncident
            {
                GangName = gangName,
                TerritoryName = territory,
                VanillaGang = vanillaGang,
                ProtectingPlayerGangMember = protectingMember,
                StartedUtc = now,
                LastThreatSeenUtc = now
            };
            _activeIncident.ThreatHandles.Add(attacker.Handle);

            string territoryText = territory == "none" ? "" : " in " + territory;
            string title;
            string body;
            if (protectingMember)
            {
                title = "GANG CONFLICT DETECTED";
                body = gangName + " is attacking Anyiii's Gang" + territoryText + ". Officer Anyi is handling the situation.";
            }
            else if (vanillaGang)
            {
                title = "VANILLA GANG ATTACK";
                body = "A vanilla gang is attacking LSPD Authority Anyi" + territoryText + ". Handle the threat.";
            }
            else
            {
                title = "GANG ATTACK ON LSPD";
                body = gangName + " is attacking LSPD Authority Anyi" + territoryText + ". Officer Anyi is handling the situation.";
            }

            Notification.PostTicker("~b~ANYI LSPD~s~\n" + title + "\n~c~" + body, false, false);
            if (audio != null)
                audio.Play(protectingMember ? "ASSISTANCE_REQUIRED" : "CRIME_SHOTS_FIRED");

            LspdResponseLog.Write(
                "POLICE_GANG_INCIDENT",
                "START | Gang=" + gangName + " | Territory=" + territory + " | Vanilla=" + vanillaGang + " | ProtectingPlayerGangMember=" + protectingMember + " | Attacker=" + attacker.Handle + " | DispatchOwned=false");
        }

        private void UpdateActiveIncident(Ped player, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData, AnyiLSPDChaosAudio audio, DateTime now)
        {
            bool threatPresent = false;

            foreach (Ped ped in nearby)
            {
                try
                {
                    if (!IsPotentialAttacker(ped, player) && !IsPotentialThreatToPlayerGang(ped, nearby, gangData))
                        continue;

                    AnyiLSPDPoliceData.GangProfile profile = gangData == null ? null : gangData.FindGangForModel(ped.Model.Hash);
                    if (profile != null && profile.PlayerOwned)
                        continue;
                    if (profile == null && !_vanillaGangHashes.Contains(ped.Model.Hash))
                        continue;

                    _activeIncident.ThreatHandles.Add(ped.Handle);
                    threatPresent = true;
                }
                catch { }
            }

            if (threatPresent)
            {
                _activeIncident.LastThreatSeenUtc = now;
                _activeIncident.NoThreatSinceUtc = DateTime.MinValue;
                return;
            }

            if (_activeIncident.NoThreatSinceUtc == DateTime.MinValue)
                _activeIncident.NoThreatSinceUtc = now;

            bool allKnownThreatsGone = true;
            foreach (int handle in _activeIncident.ThreatHandles.ToArray())
            {
                Ped ped = FindByHandle(nearby, handle);
                if (ped != null && ped.Exists() && !ped.IsDead && (ped.IsShooting || ped.IsInCombatAgainst(player)))
                {
                    allKnownThreatsGone = false;
                    break;
                }
            }

            if (allKnownThreatsGone && now >= _activeIncident.NoThreatSinceUtc.AddSeconds(_config.GangResolutionHoldSeconds))
            {
                string message = _activeIncident.VanillaGang
                    ? "Mission justified, the vanilla gang threat was neutralized."
                    : "Mission justified, the gang threat was neutralized.";

                Notification.PostTicker("~g~LSPD GANG RESPONSE~s~\n" + message, false, false);
                if (audio != null)
                    audio.Play("CASE_CLOSED");

                LspdResponseLog.Write(
                    "POLICE_GANG_INCIDENT",
                    "RESOLVED | Gang=" + _activeIncident.GangName + " | Territory=" + _activeIncident.TerritoryName + " | Threats=" + _activeIncident.ThreatHandles.Count);

                _activeIncident = null;
                _cooldownUntil = now.AddSeconds(_config.GangIncidentCooldownSeconds);
            }
        }

        private static bool IsPotentialAttacker(Ped ped, Ped target)
        {
            try
            {
                if (ped == null || target == null || !ped.Exists() || !target.Exists() || ped.IsDead || ped.Handle == target.Handle)
                    return false;
                if (ped.Position.DistanceTo(target.Position) > 90f)
                    return false;
                return ped.IsInCombatAgainst(target) || ped.IsShooting;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPotentialThreatToPlayerGang(Ped ped, Ped[] nearby, AnyiLSPDPoliceData.GangSnapshot gangData)
        {
            if (ped == null || gangData == null || gangData.PlayerGang == null)
                return false;

            foreach (Ped member in nearby)
            {
                try
                {
                    if (member == null || !member.Exists() || member.IsDead)
                        continue;
                    if (!gangData.PlayerGang.MemberHashes.Contains(member.Model.Hash) && !KnownAnyiiiCompanions.ContainsKey(member.Model.Hash))
                        continue;
                    if (ped.Handle == member.Handle)
                        continue;
                    if (ped.Position.DistanceTo(member.Position) > 80f)
                        continue;
                    if (!ped.IsInCombatAgainst(member) && !member.IsInCombatAgainst(ped))
                        continue;
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static string ResolveTerritory(AnyiLSPDPoliceData.GangSnapshot data, GTA.Math.Vector3 position)
        {
            if (data == null) return "none";
            AnyiLSPDPoliceData.TurfZone zone = data.GetNearestTurf(position.X, position.Y, position.Z, 120f);
            return zone == null ? "none" : zone.Name;
        }

        private static Ped FindByHandle(Ped[] nearby, int handle)
        {
            foreach (Ped ped in nearby)
                if (ped != null && ped.Exists() && ped.Handle == handle)
                    return ped;
            return null;
        }
    }
}

/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Addons.DisplayName
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "DisplayNameModule")]
    public class DisplayNameModule : ISharedRegionModule
    {
        private const string DisplayNameKey = "display_name";
        private const string DisplayNameUpdatedKey = "display_name_updated";

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly Regex m_displayNamePattern =
            new Regex("^[A-Za-z0-9][A-Za-z0-9 ._'-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly List<Scene> m_scenes = new();
        private readonly Dictionary<UUID, DateTime> m_lastChangeCache = new();

        private bool m_enabled;
        private bool m_requireOldNameMatch = true;
        private int m_minLength = 3;
        private int m_maxLength = 31;
        private int m_cooldownSeconds = 7 * 24 * 60 * 60;
        private string m_capSetDisplayName = "localhost";

        public string Name => "DisplayNameModule";

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig cfg = source?.Configs["DisplayName"];
            if (cfg is null)
                return;

            m_enabled = cfg.GetBoolean("Enabled", false);
            if (!m_enabled)
                return;

            m_capSetDisplayName = cfg.GetString("Cap_SetDisplayName", "localhost");
            m_requireOldNameMatch = cfg.GetBoolean("RequireOldNameMatch", true);
            m_minLength = Math.Clamp(cfg.GetInt("MinLength", 3), 1, 64);
            m_maxLength = Math.Clamp(cfg.GetInt("MaxLength", 31), m_minLength, 64);
            m_cooldownSeconds = Math.Max(0, cfg.GetInt("ChangeCooldownSeconds", 7 * 24 * 60 * 60));

            m_log.Info("[DISPLAY NAME]: Module enabled.");
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            lock (m_scenes)
                m_scenes.Add(scene);

            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            scene.EventManager.OnRegisterCaps -= RegisterCaps;

            lock (m_scenes)
                m_scenes.Remove(scene);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            lock (m_scenes)
                m_scenes.Clear();

            lock (m_lastChangeCache)
                m_lastChangeCache.Clear();
        }

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            if (!m_enabled || string.IsNullOrEmpty(m_capSetDisplayName))
                return;

            Scene scene = FindScene(caps.RegionName);
            if (scene is null)
                return;

            if (m_capSetDisplayName == "localhost")
            {
                caps.RegisterSimpleHandler(
                    "SetDisplayName",
                    new SimpleStreamHandler("/" + UUID.Random(), (req, resp) => ProcessSetDisplayName(scene, agentID, req, resp)));
            }
            else
            {
                caps.RegisterHandler("SetDisplayName", m_capSetDisplayName);
            }
        }

        private Scene FindScene(string regionName)
        {
            lock (m_scenes)
            {
                foreach (Scene scene in m_scenes)
                {
                    if (scene.RegionInfo.RegionName == regionName)
                        return scene;
                }
            }

            return null;
        }

        private void ProcessSetDisplayName(Scene scene, UUID agentID, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            if (httpRequest.HttpMethod != "POST")
            {
                httpResponse.StatusCode = (int)System.Net.HttpStatusCode.NotFound;
                return;
            }

            scene.TryGetScenePresence(agentID, out ScenePresence sp);
            if (sp is null || sp.IsDeleted || sp.IsChildAgent)
            {
                WriteReply(scene, agentID, httpResponse, 410, "Agent not available", null, null, null);
                return;
            }

            OSDMap req;
            try
            {
                req = (OSDMap)OSDParser.DeserializeLLSDXml(httpRequest.InputStream);
            }
            catch
            {
                httpResponse.StatusCode = (int)System.Net.HttpStatusCode.BadRequest;
                return;
            }

            if (!TryReadDisplayNameRequest(req, out string oldDisplayName, out string newDisplayName))
            {
                WriteReply(scene, agentID, httpResponse, 400, "Malformed SetDisplayName payload", null, null, null);
                return;
            }

            IUserAccountService userAccountService = scene.RequestModuleInterface<IUserAccountService>();
            if (userAccountService is null)
            {
                WriteReply(scene, agentID, httpResponse, 503, "User account service unavailable", null, null, null);
                return;
            }

            UserAccount account = userAccountService.GetUserAccount(scene.RegionInfo.ScopeID, agentID)
                ?? userAccountService.GetUserAccount(UUID.Zero, agentID);

            if (account is null)
            {
                WriteReply(scene, agentID, httpResponse, 404, "User account not found", null, null, null);
                return;
            }

            string legacyFirst = account.FirstName ?? string.Empty;
            string legacyLast = account.LastName ?? string.Empty;
            string legacyFullName = (legacyFirst + " " + legacyLast).Trim();
            string currentDisplayName = GetStoredDisplayName(account, legacyFullName);

            if (m_requireOldNameMatch && !string.Equals(oldDisplayName, currentDisplayName, StringComparison.Ordinal))
            {
                WriteReply(scene, agentID, httpResponse, 409, "Old display name does not match current display name", currentDisplayName,
                    legacyFirst, legacyLast);
                return;
            }

            if (!ValidateDisplayName(newDisplayName, out string validationReason))
            {
                WriteReply(scene, agentID, httpResponse, 400, validationReason, currentDisplayName, legacyFirst, legacyLast);
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!CanChangeDisplayName(account, now, out DateTime nextAllowed))
            {
                WriteReply(scene, agentID, httpResponse, 429,
                    $"Display name cooldown active. Try again after {nextAllowed:u}", currentDisplayName, legacyFirst, legacyLast);
                return;
            }

            account.ServiceURLs ??= new Dictionary<string, object>();
            account.ServiceURLs[DisplayNameKey] = newDisplayName;
            account.ServiceURLs[DisplayNameUpdatedKey] = Util.UnixTimeSinceEpoch().ToString();

            if (!userAccountService.StoreUserAccount(account))
            {
                WriteReply(scene, agentID, httpResponse, 500, "Failed to persist display name", currentDisplayName, legacyFirst, legacyLast);
                return;
            }

            lock (m_lastChangeCache)
                m_lastChangeCache[agentID] = now;

            WriteReply(scene, agentID, httpResponse, 200, "OK", newDisplayName, legacyFirst, legacyLast);
            EmitDisplayNameUpdate(scene, agentID, currentDisplayName, newDisplayName, legacyFirst, legacyLast, now);
        }

        private void EmitDisplayNameUpdate(Scene scene, UUID agentID, string oldDisplayName, string newDisplayName,
            string legacyFirstName, string legacyLastName, DateTime updatedAt)
        {
            IEventQueue eq = scene.RequestModuleInterface<IEventQueue>();
            if (eq is null)
                return;

            DateTime nextUpdate = updatedAt.AddSeconds(Math.Max(1, m_cooldownSeconds));

            osUTF8 sb = eq.StartEvent("DisplayNameUpdate", 512);
            LLSDxmlEncode2.AddMap("agent", sb);
            LLSDxmlEncode2.AddElem("id", agentID, sb);
            LLSDxmlEncode2.AddElem("username", ((legacyFirstName ?? string.Empty) + " " + (legacyLastName ?? string.Empty)).Trim(), sb);
            LLSDxmlEncode2.AddElem("display_name", newDisplayName ?? string.Empty, sb);
            LLSDxmlEncode2.AddElem("legacy_first_name", legacyFirstName ?? string.Empty, sb);
            LLSDxmlEncode2.AddElem("legacy_last_name", legacyLastName ?? string.Empty, sb);
            LLSDxmlEncode2.AddElem("is_display_name_default", false, sb);
            LLSDxmlEncode2.AddElem("display_name_next_update", nextUpdate, sb);
            LLSDxmlEncode2.AddElem("last_updated", updatedAt, sb);
            LLSDxmlEncode2.AddElem("old_display_name", oldDisplayName ?? string.Empty, sb);
            LLSDxmlEncode2.AddEndMap(sb);

            byte[] updateEvent = eq.EndEventToBytes(sb);

            scene.ForEachRootScenePresence(sp =>
            {
                if (sp is null || sp.IsDeleted)
                    return;

                eq.Enqueue(updateEvent, sp.UUID);
            });
        }

        private bool CanChangeDisplayName(UserAccount account, DateTime now, out DateTime nextAllowed)
        {
            nextAllowed = now;

            if (m_cooldownSeconds <= 0)
                return true;

            if (TryGetLastUpdateTime(account, out DateTime storedLastUpdate))
            {
                nextAllowed = storedLastUpdate.AddSeconds(m_cooldownSeconds);
                if (now < nextAllowed)
                    return false;
            }

            return true;
        }

        private bool TryGetLastUpdateTime(UserAccount account, out DateTime when)
        {
            when = DateTime.MinValue;

            lock (m_lastChangeCache)
            {
                if (m_lastChangeCache.TryGetValue(account.PrincipalID, out DateTime cached))
                {
                    when = cached;
                    return true;
                }
            }

            if (account.ServiceURLs is not null
                && account.ServiceURLs.TryGetValue(DisplayNameUpdatedKey, out object lastChangedRaw)
                && Int64.TryParse(lastChangedRaw?.ToString(), out long unix))
            {
                when = Util.ToDateTime((int)unix);
                lock (m_lastChangeCache)
                    m_lastChangeCache[account.PrincipalID] = when;
                return true;
            }

            return false;
        }

        private static string GetStoredDisplayName(UserAccount account, string fallback)
        {
            if (account.ServiceURLs is not null
                && account.ServiceURLs.TryGetValue(DisplayNameKey, out object displayNameRaw))
            {
                string displayName = displayNameRaw?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(displayName))
                    return displayName;
            }

            return fallback;
        }

        private bool ValidateDisplayName(string input, out string reason)
        {
            reason = "";
            string candidate = input?.Trim() ?? string.Empty;

            if (candidate.Length < m_minLength || candidate.Length > m_maxLength)
            {
                reason = $"Display name length must be between {m_minLength} and {m_maxLength} characters.";
                return false;
            }

            if (!m_displayNamePattern.IsMatch(candidate))
            {
                reason = "Display name contains unsupported characters.";
                return false;
            }

            return true;
        }

        private static bool TryReadDisplayNameRequest(OSDMap req, out string oldDisplayName, out string newDisplayName)
        {
            oldDisplayName = string.Empty;
            newDisplayName = string.Empty;

            if (!req.TryGetValue("display_name", out OSD displayNameOsd) || displayNameOsd is not OSDArray displayNameArray)
                return false;

            if (displayNameArray.Count < 2)
                return false;

            oldDisplayName = displayNameArray[0].AsString();
            newDisplayName = displayNameArray[1].AsString();
            return true;
        }

        private void WriteReply(Scene scene, UUID agentID, IOSHttpResponse httpResponse, int status, string reason,
            string displayName, string legacyFirstName, string legacyLastName)
        {
            DateTime now = DateTime.UtcNow;
            DateTime nextUpdate = now.AddSeconds(Math.Max(1, m_cooldownSeconds));

            OSDMap content = new()
            {
                ["id"] = agentID,
                ["username"] = OSD.FromString(((legacyFirstName ?? string.Empty) + " " + (legacyLastName ?? string.Empty)).Trim()),
                ["display_name"] = OSD.FromString(displayName ?? string.Empty),
                ["legacy_first_name"] = OSD.FromString(legacyFirstName ?? string.Empty),
                ["legacy_last_name"] = OSD.FromString(legacyLastName ?? string.Empty),
                ["is_display_name_default"] = false,
                ["display_name_next_update"] = OSD.FromDate(nextUpdate),
                ["last_updated"] = OSD.FromDate(now)
            };

            OSDMap body = new()
            {
                ["content"] = content,
                ["reason"] = OSD.FromString(reason ?? string.Empty),
                ["status"] = OSD.FromInteger(status)
            };

            httpResponse.RawBuffer = Util.UTF8NBGetbytes(OSDParser.SerializeLLSDXmlString(body));
            httpResponse.ContentType = "application/llsd+xml";
            httpResponse.StatusCode = (int)System.Net.HttpStatusCode.OK;

            IEventQueue eq = scene.RequestModuleInterface<IEventQueue>();
            if (eq is null)
                return;

            osUTF8 sb = eq.StartEvent("SetDisplayNameReply", 512);
            LLSDxmlEncode2.AddMap("content", sb);
            LLSDxmlEncode2.AddElem("id", agentID, sb);
            LLSDxmlEncode2.AddElem("username", content["username"].AsString(), sb);
            LLSDxmlEncode2.AddElem("display_name", content["display_name"].AsString(), sb);
            LLSDxmlEncode2.AddElem("legacy_first_name", content["legacy_first_name"].AsString(), sb);
            LLSDxmlEncode2.AddElem("legacy_last_name", content["legacy_last_name"].AsString(), sb);
            LLSDxmlEncode2.AddElem("is_display_name_default", status != 200, sb);
            LLSDxmlEncode2.AddElem("display_name_next_update", nextUpdate, sb);
            LLSDxmlEncode2.AddElem("last_updated", now, sb);
            LLSDxmlEncode2.AddEndMap(sb);
            LLSDxmlEncode2.AddElem("reason", reason ?? string.Empty, sb);
            LLSDxmlEncode2.AddElem("status", status, sb);
            eq.Enqueue(eq.EndEventToBytes(sb), agentID);
        }
    }
}

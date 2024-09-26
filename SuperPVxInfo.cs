using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins
{
  [Info("Super PVx Info", "HunterZ", "1.5.0")]
  [Description("Displays PvE/PvP/etc. status on player's HUD")]
  public class SuperPVxInfo : RustPlugin
  {
    #region Plugin Data

    // list of plugins whose PVP delay statuses are tracked
    public enum PvpDelayType
    {
      AbandonedBases,
      DynamicPvp,
      PlayerBasePvpZones,
      RaidableBases,
      TruePve
    }

    // primary status types tracked by this plugin
    public enum PVxType { PVE, PVP, PVPDelay, SafeZone }

    // (plugin) events that are managed via listening to start/stop hooks
    private enum PvpEventType
    {
      Air,           // KpucTaJl Air Event
      ArcticBase,    // KpucTaJl Arctic Base Event
      FerryTerminal, // KpucTaJl Ferry Terminal Event
      GasStation,    // KpucTaJl Gas Station Event
      Harbor,        // KpucTaJl Harbor Event
      Junkyard,      // KpucTaJl Junkyard Event
      PowerPlant,    // KpucTaJl Power Plant Event
      SatDish,       // KpucTaJl Satellite Dish Event
      Supermarket,   // KpucTaJl Supermarket Event
      Water          // KpucTaJl Water Event
    }

#pragma warning disable CS0649
    [PluginReference] private readonly Plugin
      AbandonedBases, DynamicPVP, DangerousTreasures, PlayerBasePvpZones,
      PopupNotifications, RaidableBases, SimpleStatus, TruePVE, ZoneManager;
#pragma warning restore CS0649

    private ConfigData _configData;

    // active TruePVE PVP delay timers by plugin name by player ID
    private Dictionary<ulong, Dictionary<string, Timer>> _excludedPlayers =
      new();

    // NOTE: this is not to be used directly for sending messages, but rather
    //  for populating the default language dictionary, and for enumerating
    //  which messages exist
    private readonly Dictionary<string, string> _notifyMessages = new()
    {
      ["Unexpected Exit From Abandoned Or Raidable Base"] =
        "{0}Left Abandoned/Raidable Base Zone",
      ["Unexpected Exit From Dangerous Treasures Event"] =
        "{0}Left Dangerous Treasures Zone",
      ["Safe Zone Entry"] =
        "{0}Entering Safe Zone",
      ["Safe Zone Exit"] =
        "{0}Leaving Safe Zone",
      ["PVP Height Entry"] =
        "{0}WARNING: Entering Sky/Portal PVP Zone",
      ["PVP Height Exit"] =
        "{0}Leaving Sky/Portal PVP Zone",
      ["PVP Depth Entry"] =
        "{0}WARNING: Entering Train Tunnels PVP Zone",
      ["PVP Depth Exit"] =
        "{0}Leaving Train Tunnels PVP Zone"
    };

    private Timer _saveDataTimer;

    private StoredData _storedData;

    private const string _uiName = "SuperPVxInfoUI";

    #endregion Plugin Data

    #region Utility Methods

    private bool IsValidPlayer(BasePlayer player, bool checkConnected) =>
      null != player &&
      !player.IsNpc &&
      player.userID.IsSteamId() &&
      (!checkConnected || player.IsConnected);

    private PlayerWatcher GetPlayerWatcher(BasePlayer player) =>
      IsValidPlayer(player, true) ? player.GetComponent<PlayerWatcher>() : null;

    private void SendCannedMessage(BasePlayer player, string key)
    {
      if (null == _configData ||
          !_configData.NotifySettings.Enabled.TryGetValue(
            key, out bool enabled) ||
          !enabled)
      {
        return;
      }
      var message = lang.GetMessage(key, this, player.UserIDString);
      if (null == message) return;
      if (_configData.NotifySettings.ChatEnabled)
      {
        SendReply(player, string.Format(
          message, _configData.NotifySettings.ChatPrefix));
      }
      if (_configData.NotifySettings.PopupNotificationsEnabled &&
          null != PopupNotifications)
      {
        PopupNotifications.Call(
          "CreatePopupNotification",
          string.Format(
            message, _configData.NotifySettings.PopupNotificationsPrefix),
          player);
      }
    }

    private void ExcludePlayerRemove(ulong userid, string pluginName)
    {
      // get timers-by-plugin for player
      if (!_excludedPlayers.TryGetValue(userid, out var excludeTimers) ||
          null == excludeTimers)
      {
        return;
      }

      // remove timer entry if present, and destroy it if needed
      if (excludeTimers.Remove(pluginName, out var removedTimer) &&
          null != removedTimer && !removedTimer.Destroyed)
      {
        removedTimer.Destroy();
      }

      // abort if timers-by-plugin is still not empty for this player
      if (excludeTimers.Count > 0) return;

      // timers-by-plugin is empty - remove PVP delay status
      var player = BasePlayer.FindByID(userid);
      if (null != player)
      {
        SetPvpDelay(player, PvpDelayType.TruePve, false);
      }
    }

    #endregion Utility Methods

    #region Oxide Methods

    protected override void LoadDefaultMessages()
    {
      lang.RegisterMessages(_notifyMessages, this);
    }

    private void Init()
    {
      LoadData();
      PlayerWatcher.AllowForceUpdate =
        null == _configData || _configData.forceUpdates;
      PlayerWatcher.Instance = this;
      PlayerWatcher.PvpAboveHeight =
        null == _configData ? 1000.0f : _configData.pvpAboveHeight;
      PlayerWatcher.PvpBelowHeight =
        null == _configData ? -50.0f : _configData.pvpBelowHeight;
      PlayerWatcher.UpdateIntervalSeconds =
        null == _configData ? 1.0f : _configData.updateIntervalSeconds;
      if (null != _configData &&
          !string.IsNullOrEmpty(_configData.toggleCommand))
      {
        AddCovalenceCommand(_configData.toggleCommand, nameof(ToggleUI));
      }
    }

    private void OnServerInitialized()
    {
      if(null != TruePVE && null != PlayerBasePvpZones &&
         TruePVE.Version >= new VersionNumber(2, 2, 3) &&
         PlayerBasePvpZones.Version >= new VersionNumber(1, 1, 0))
      {
        Puts("OnServerInitialized(): TruePVE 2.2.3+ & PlayerBasePvpZones 1.1.0+ detected! TruePVE PVP delays will be used");
        Unsubscribe(nameof(OnPlayerBasePvpDelayStart));
        Unsubscribe(nameof(OnPlayerBasePvpDelayStop));
      }

      if (null != _storedData)
      {
        // purge any mappings that Zone Manager doesn't recognize
        var deadZoneIds = Pool.Get<List<string>>();
        foreach (var (zoneId, _) in _storedData.Mappings)
        {
          if (!ZM_CheckZoneID(zoneId)) deadZoneIds.Add(zoneId);
        }
        foreach (var deadZoneId in deadZoneIds)
        {
          PrintWarning($"Purging unknown/obsolete zoneId={deadZoneId} from database");
          _storedData.Mappings.Remove(deadZoneId);
        }
        if (deadZoneIds.Count > 0) SaveData();
        Pool.FreeUnmanaged(ref deadZoneIds);
      }

      // setup SimpleStatus integration if appropriate
      SS_CreateStatuses();

      foreach (var player in BasePlayer.activePlayerList)
      {
        OnPlayerConnected(player);
      }

      if (true ==_configData?.NotifySettings.PopupNotificationsEnabled &&
          null == PopupNotifications)
      {
        PrintWarning("Notify via PopupNotifications enabled, but required plugin is missing");
      }
    }

    private void Unload()
    {
      // clear out any active TruePVE PVP delay timers
      foreach (var (_, excludeTimers) in _excludedPlayers)
      {
        foreach (var (_, excludeTimer) in excludeTimers)
        {
          if (null != excludeTimer && !excludeTimer.Destroyed)
          {
            excludeTimer.Destroy();
          }
        }
        excludeTimers.Clear();
      }
      _excludedPlayers.Clear();
      // destroy GUIs for all active players
      foreach (var player in BasePlayer.activePlayerList)
      {
        OnPlayerDisconnected(player, _uiName);
      }
      PlayerWatcher.Instance = null;
      // if save timer active, destroy and force write
      if (null != _saveDataTimer) WriteData();
    }

    private void OnPlayerConnected(BasePlayer player)
    {
      if (!IsValidPlayer(player, true)) return;

      // abort if an active watcher is already attached
      // isActiveAndEnabled is needed because sometimes reloading the plugin
      //  causes it to catch a watcher that is still in the process of being
      //  destroyed
      var watcher = player.GetComponent<PlayerWatcher>();
      if (watcher != null && watcher.isActiveAndEnabled) return;

      watcher = player.gameObject.AddComponent<PlayerWatcher>();
      watcher.Init(
        IsPlayerInBase(player), IsPlayerInPVPDelay(player.userID.Get()),
        GetPlayerZoneType(player), player);
      watcher.StartWatching();
    }

    private void OnPlayerDisconnected(BasePlayer player, string reason)
    {
      if (IsValidPlayer(player, false))
      {
        player.gameObject.GetComponent<PlayerWatcher>()?.OnDestroy();
      }
      DestroyUI(player);
    }

    private void OnPlayerRespawned(BasePlayer player)
    {
      NextTick(() =>
      {
        if (!IsValidPlayer(player, true)) return;
        var watcher = GetPlayerWatcher(player);
        if (null == watcher) return;

        // check everything because the player could be anywhere now
        if (watcher.InBaseType != null) watcher.CheckBase = true;
        watcher.CheckPVxEvent = true;
        watcher.CheckZone = true;
        watcher.CheckPvpDelay = true;
        watcher.Force();
      });
    }

    #endregion Oxide Methods

    #region TruePVE Hook Handlers

    // called when a plugin maps a Zone Manager zone to a TruePVE ruleset
    private void AddOrUpdateMapping(string zoneId, string ruleset)
    {
      if (null == _storedData ||
          string.IsNullOrEmpty(zoneId) || string.IsNullOrEmpty(ruleset))
      {
        return;
      }

      NextTick(() =>
      {
        _storedData.Mappings[zoneId] = ruleset;
        SaveData();
      });
    }

    // called when a plugin deletes a mapping
    private void RemoveMapping(string zoneId)
    {
      if (null == _storedData || string.IsNullOrEmpty(zoneId)) return;

      NextTick(() =>
      {
        _storedData.Mappings.Remove(zoneId);
        SaveData();
      });
    }

    // called when a plugin requests a timed rule exclusion (PVP exit delay)
    private void ExcludePlayer(
      ulong userid, float maxDelayLength, Plugin plugin)
    {
      if (null == plugin || !userid.IsSteamId()) return;
      var pluginName = plugin.Name;

      NextTick(() =>
      {
        // if delay is non-positive, just try to remove any existing record
        if (maxDelayLength <= 0.0f)
        {
          ExcludePlayerRemove(userid, pluginName);
          return;
        }

        // handle the case of updating an existing record
        var hasTimers =
          _excludedPlayers.TryGetValue(userid, out var excludeTimers);
        if (hasTimers &&
            excludeTimers.TryGetValue(pluginName, out var excludeTimer))
        {
          if (null != excludeTimer && !excludeTimer.Destroyed)
          {
            excludeTimer.Reset(maxDelayLength);
            return;
          }
          // pathological: remove defunct entry and we'll create a new one below
          excludeTimers.Remove(pluginName);
        }

        // handle the case that no timers have ever been recorded for player
        // (just create an empty timers-by-plugin sub-dictionary)
        if (null == excludeTimers)
        {
          excludeTimers = new();
          _excludedPlayers.Add(userid, excludeTimers);
        }

        // add a timer to the dictionary that simply removes itself on fire
        // existence of a dictionary entry then represents an active PVP delay
        excludeTimers.Add(pluginName, timer.Once(
          maxDelayLength, () => { ExcludePlayerRemove(userid, pluginName); }));

        var player = BasePlayer.FindByID(userid);
        if (null != player)
        {
          SetPvpDelay(player, PvpDelayType.TruePve, true);
        }
      });
    }

    #endregion TruePVE Hook Handlers

    #region ZoneManager Integration

    #region ZoneManager Utilities

    bool ZM_CheckZoneID(string zoneId)
    {
      return ZoneManager?.Call("CheckZoneID", zoneId) is string s && null != s;
    }

    private string[] ZM_GetPlayerZoneIDs(BasePlayer player) =>
      ZoneManager?.Call("GetPlayerZoneIDs", player) is string[] s ?
        s : new string[0];

    // if player is in a zone, return its type if possible, else return null
    public PVxType? GetPlayerZoneType(BasePlayer player)
    {
      if (null == _configData || !IsValidPlayer(player, true)) return null;

      // get current zone (if any)
      (var zoneId, var zoneName) = GetSmallestZoneIdAndName(player);

      // go by zone name first
      if (!string.IsNullOrEmpty(zoneName))
      {
        if (_configData.PveZoneManagerNames.Any(
          x => zoneName.Contains(x, CompareOptions.IgnoreCase))
        )
        {
          return PVxType.PVE;
        }
        if (_configData.PvpZoneManagerNames.Any(
          x => zoneName.Contains(x, CompareOptions.IgnoreCase))
        )
        {
          return PVxType.PVP;
        }
      }

      if (!string.IsNullOrEmpty(zoneId))
      {
        // return PVP if this is a TruePVE/NextGenPVE exclusion zone
        // (needed for e.g. ZoneManagerAutoZones which doesn't put "PVP" in
        //  its zone names)
#pragma warning disable CS8604 // Possible null reference argument.
        if (IsExcludeZone(zoneId))
#pragma warning restore CS8604 // Possible null reference argument.
        {
          return PVxType.PVP;
        }

        // check Zone Manager flags
        if (ZM_GetZoneFlag(zoneId, "pvpgod"))
        {
          if (ZM_GetZoneFlag(zoneId, "pvegod"))
          {
            // no-PvP *and* no-PvE => treat as safe zone
            return PVxType.SafeZone;
          }

          // no-PvP only => treat as PvE zone
          return PVxType.PVE;
        }
      }

      // give up
      return null;
    }

    private (string, string) GetSmallestZoneIdAndName(BasePlayer player)
    {
      if (ZoneManager == null) return (null, null);
      float smallestRadius = float.MaxValue;
      string smallestId = null;
      string smallestName = null;
      var zoneIDs = ZM_GetPlayerZoneIDs(player);
      foreach (var zoneId in zoneIDs)
      {
        if (string.IsNullOrEmpty(zoneId)) continue;
        var zoneName = ZM_GetZoneName(zoneId);

        // get whichever of 2D zone size or radius is greater than zero
        var zoneMagnitude2D = ZM_GetZoneSize(zoneId).Magnitude2D();
        float zoneRadius = zoneMagnitude2D < float.Epsilon ?
            ZM_GetZoneRadius(zoneId) : zoneMagnitude2D;
        if (zoneRadius < float.Epsilon) continue;
        // if zone is the smallest we've seen, record it as such
        if (zoneRadius < smallestRadius)
        {
          smallestRadius = zoneRadius;
          smallestId = zoneId;
          smallestName = zoneName;
        }
      }

      return (smallestId, smallestName);
    }

    private bool ZM_GetZoneFlag(string zoneId, string zoneFlag) =>
      ZoneManager?.Call("HasFlag", zoneId, zoneFlag) is bool flagState &&
      flagState;

    private string ZM_GetZoneName(string zoneId) =>
      ZoneManager?.Call("GetZoneName", zoneId) is string zoneName ?
        zoneName : "";

    private float ZM_GetZoneRadius(string zoneId) =>
      ZoneManager?.Call("GetZoneRadius", zoneId) is float zoneRadius ?
        zoneRadius : 0.0f;

    private Vector3 ZM_GetZoneSize(string zoneId) =>
      ZoneManager?.Call("GetZoneSize", zoneId) is Vector3 zoneSize ?
        zoneSize : Vector3.zero;

    private bool IsExcludeZone(string zoneId) =>
      null != _storedData &&
      null != _configData &&
      _storedData.Mappings.TryGetValue(zoneId, out string ruleset) &&
      _configData.PveExclusionNames.Any(
        x => ruleset.Contains(x, CompareOptions.IgnoreCase));

    // common logic for setting watcher's zone check request flag
    private void CheckZone(BasePlayer player)
    {
      NextTick(() =>
      {
        if (!IsValidPlayer(player, true)) return;
        var watcher = GetPlayerWatcher(player);
        if (null == watcher) return;
        watcher.CheckZone = true;
        watcher.Force();
      });
    }

    #endregion ZoneManager Utilities

    #region ZoneManager Hook Handlers

    private void OnEnterZone(string zoneId, BasePlayer player) =>
      CheckZone(player);

    private void OnExitZone(string zoneId, BasePlayer player) =>
      // check if player is exiting from a smaller zone into a larger one
      CheckZone(player);

    #endregion ZoneManager Hook Handlers

    #endregion ZoneManager Integration

    #region PVP Plugin Integrations

    #region PVP Plugin Utilities

    // create or update an event record with the given data
    private void CreateOrUpdatePvpEvent(
      PvpEventType type, Vector3 location, float radius)
    {
      if (null == _storedData) return;
      if (_storedData.PvpEvents.TryGetValue(type, out var eventData))
      {
        eventData.Location = location;
        eventData.Radius = radius;
      }
      else
      {
        _storedData.PvpEvents.Add(type, new(location, radius));
      }
      SaveData();
    }

    // delete an event record with the given data, if any
    private void DeletePvpEvent(PvpEventType type)
    {
      if (null == _storedData) return;
      if (_storedData.PvpEvents.Remove(type)) SaveData();
    }

    // check whether player is in any Raidable Base
    // TODO: add Abandoned Bases support?
    // this is expensive, and should only be called if state is totally unknown
    // (e.g. on connect)
    private PVxType? IsPlayerInBase(BasePlayer player)
    {
      // get list of all active Raidable Bases
      if (RaidableBases != null && RaidableBases.Call("GetAllEvents") is
          List<(Vector3 pos, int mode, bool allowPVP, string a, float b,
          float c, float loadTime, ulong ownerId, BasePlayer owner,
          List<BasePlayer> raiders, List<BasePlayer> intruders,
          List<BaseEntity> entities, string baseName, DateTime spawnDateTime,
          DateTime despawnDateTime, float radius, int lootRemaining)> rbEvents
          // && rbEvents.Exists(x => x.intruders.Contains(player))
      )
      {
        // look for a base that the player is in
        foreach (
          var (_, _, allowPVP, _, _, _, _, _, _, _, intruders, _, _, _, _, _, _)
          in rbEvents)
        {
          if (intruders.Contains(player))
          {
            // base found; return its type
            return allowPVP ? PVxType.PVP : PVxType.PVE;
          }
        }
      }

      // player not in any bases
      return null;
    }

    // check whether player in an a PVP event that only provides event
    // start/stop hooks with location, requiring active polling of player
    // position
    private bool IsPlayerInPvpEvent(BasePlayer player)
    {
      if (null == _storedData) return false;
      foreach (var (_, eventData) in _storedData.PvpEvents)
      {
        if (Vector3.Distance(eventData.Location, player.transform.position) <=
            eventData.Radius)
        {
          return true;
        }
      }
      return false;
    }

    // check whether player is in an event that can be PvE or PvP
    // note that this is only useful for unexpected exits (e.g. respawning)
    //  because we don't get a PvP-versus-PvE indication from this
    private bool IsPlayerInPVxEvent(BasePlayer player) =>
      null != DangerousTreasures &&
      Convert.ToBoolean(DangerousTreasures.Call(
        "EventTerritory", player.transform.position));

    // check if player has any PVP delays active
    // this should only be called when hook-reported states don't exist yet, or
    //  can't be relied upon for some reason
    private HashSet<PvpDelayType> IsPlayerInPVPDelay(ulong playerID)
    {
      var pvpDelays = new HashSet<PvpDelayType>();

      if (AbandonedBases != null && Convert.ToBoolean(
          AbandonedBases.Call("HasPVPDelay", playerID)))
      {
        pvpDelays.Add(PvpDelayType.AbandonedBases);
      }

      if (DynamicPVP != null && Convert.ToBoolean(
          DynamicPVP.Call("IsPlayerInPVPDelay", playerID)))
      {
        pvpDelays.Add(PvpDelayType.DynamicPvp);
      }

      if (PlayerBasePvpZones != null && !string.IsNullOrEmpty(Convert.ToString(
          PlayerBasePvpZones.Call("OnPlayerBasePvpDelayQuery", playerID))))
      {
        pvpDelays.Add(PvpDelayType.PlayerBasePvpZones);
      }

      if (RaidableBases != null && Convert.ToBoolean(
          RaidableBases.Call("HasPVPDelay", playerID)))
      {
        pvpDelays.Add(PvpDelayType.RaidableBases);
      }

      if (_excludedPlayers.TryGetValue(playerID, out var excludeTimers) &&
          excludeTimers.Count > 0)
      {
        pvpDelays.Add(PvpDelayType.TruePve);
      }

      return pvpDelays;
    }

    // common logic for Abandoned/Raidable Base entry hooks
    private void EnteredBase(
      BasePlayer player, PVxType baseType,
      Vector3 baseLocation, float baseRadius)
    {
      if (!IsValidPlayer(player, true)) return;
      var watcher = GetPlayerWatcher(player);
      if (null == watcher) return;
      watcher.BaseLocation = baseLocation;
      watcher.BaseRadius = baseRadius;
      watcher.InBaseType = baseType;
      watcher.Force();
    }

    // common logic for Abandoned/Raidable Base exit hooks
    private void ExitedBase(BasePlayer player, bool checkPlayerValid = true)
    {
      if (checkPlayerValid && !IsValidPlayer(player, true)) return;
      var watcher = GetPlayerWatcher(player);
      if (null == watcher) return;
      watcher.CheckZone = true;
      watcher.InBaseType = null;
      watcher.Force();
    }

    // common logic for PVP Bubble hooks
    private void SetPvpBubble(BasePlayer player, bool state)
    {
      if (!IsValidPlayer(player, true)) return;
      var watcher = GetPlayerWatcher(player);
      if (null == watcher) return;
      if (!state)
      {
        watcher.CheckZone = true;
      }
      watcher.InPvpBubble = state;
      watcher.Force();
    }

    // common logic for PVP Delay hooks
    private void SetPvpDelay(BasePlayer player, PvpDelayType type, bool state)
    {
      if (!IsValidPlayer(player, true)) return;
      var watcher = GetPlayerWatcher(player);
      if (null == watcher) return;
      if (state)
      {
        watcher.AddPvpDelay(type);
      }
      else if (watcher.ClearPvpDelay(type) <= 0)
      {
        watcher.CheckBase = true;
        watcher.CheckZone = true;
      }
      watcher.Force();
    }

    // common logic for PVx event hooks
    private void SetPvxEvent(BasePlayer player, PVxType eventType, bool state)
    {
      if (!IsValidPlayer(player, true)) return;
      var watcher = GetPlayerWatcher(player);
      if (null == watcher) return;
      if (state)
      {
        watcher.InPVxEventType = eventType;
      }
      else
      {
        watcher.CheckZone = true;
        watcher.InPVxEventType = null;
      }
      watcher.Force();
    }

    #endregion PVP Plugin Utilities

    #region RaidableBases Hook Handlers

    private void OnPlayerEnteredRaidableBase(
      BasePlayer player, Vector3 location, bool allowPVP, int mode, string id,
      float _, float __, float loadTime, ulong ownerId, string baseName,
      DateTime spawnTime, DateTime despawnTime, float radius, int lootRemaining)
    {
      NextTick(() =>
      {
        EnteredBase(
          player, allowPVP ? PVxType.PVP : PVxType.PVE, location, radius);
      });
    }

    private void OnPlayerExitedRaidableBase(
      BasePlayer player, Vector3 location, bool allowPVP, int mode, string id,
      float _, float __, float loadTime, ulong ownerId, string baseName,
      DateTime spawnTime, DateTime despawnTime, float radius)
    {
      NextTick(() =>
      {
        ExitedBase(player);
      });
    }

    private void OnRaidableBaseEnded(
      Vector3 location, int mode, bool allowPvP, string id, float _,
      float __, float loadTime, ulong ownerId, BasePlayer owner,
      List<BasePlayer> raiders, List<BasePlayer> intruders,
      List<BaseEntity> entities, string baseName, DateTime spawnDateTime,
      DateTime despawnDateTime, float protectionRadius, int lootAmountRemaining)
    {
      NextTick(() =>
      {
        // set zone check flag for any players in base radius
        foreach (var player in intruders)
        {
          if (!IsValidPlayer(player, true)) continue;
          // skip player if not within radius of raidable base
          if (Vector3.Distance(location, player.transform.position) >
                protectionRadius)
          {
            continue;
          }
          ExitedBase(player, false);
        }
      });
    }

    private void OnPlayerPvpDelayStart(
      BasePlayer player, int mode, Vector3 location, bool allowPvP, string id,
      float _, float __, float loadTime, ulong ownerId, string baseName,
      DateTime spawnDateTime, DateTime despawnDateTime, int lootAmountRemaining)
    {
      NextTick(() =>
      {
        SetPvpDelay(player, PvpDelayType.RaidableBases, true);
      });
    }

    private void OnPlayerPvpDelayReset(
      BasePlayer player, int mode, Vector3 location, bool allowPvP, string id,
      float _, float __, float loadTime, ulong ownerId, string baseName,
      DateTime spawnDateTime, DateTime despawnDateTime, int lootAmountRemaining)
    {
      NextTick(() =>
      {
        SetPvpDelay(player, PvpDelayType.RaidableBases, true);
      });
    }

    private void OnPlayerPvpDelayExpired(
      BasePlayer player, int mode, Vector3 location, bool allowPvP, string id,
      float _, float __, float loadTime, ulong ownerId, string baseName,
      DateTime spawnDateTime, DateTime despawnDateTime, int lootAmountRemaining)
    {
      NextTick(() =>
      {
        SetPvpDelay(player, PvpDelayType.RaidableBases, false);
      });
    }

    #endregion RaidableBases Hook Handlers

    #region AbandonedBases Hook Handlers

    private void OnPlayerEnteredAbandonedBase(
      BasePlayer player, Vector3 eventPos, float radius, bool allowPVP,
      List<BasePlayer> intruders, List<ulong> intruderIds,
      List<BaseEntity> entities)
    {
      NextTick(() =>
      {
        EnteredBase(
          player, allowPVP ? PVxType.PVP : PVxType.PVE, eventPos, radius);
      });
    }

    private void OnPlayerExitAbandonedBase(
      BasePlayer player, Vector3 location, bool allowPVP)
    {
      NextTick(() =>
      {
        ExitedBase(player);
      });
    }

    private void OnAbandonedBaseEnded(
      Vector3 eventPos, float radius, bool allowPVP,
      List<BasePlayer> participants, List<ulong> participantIds,
      List<BaseEntity> entities)
    {
      NextTick(() =>
      {
        foreach (var player in participants)
        {
          if (!IsValidPlayer(player, true)) continue;
          if (Vector3.Distance(eventPos, player.transform.position) > radius)
          {
            continue;
          }
          ExitedBase(player, false);
        }
      });
    }

    private void OnPlayerPvpDelayStart(
      BasePlayer player, ulong userid, Vector3 eventPos,
      List<BasePlayer> intruders, List<BaseEntity> entities)
    {
      NextTick(() =>
      {
        SetPvpDelay(player, PvpDelayType.AbandonedBases, true);
      });
    }

    private void OnPlayerPvpDelayExpiredII(
      BasePlayer player, ulong userid, Vector3 eventPos,
      List<BasePlayer> intruders, List<BaseEntity> entities)
    {
      NextTick(() =>
      {
        SetPvpDelay(player, PvpDelayType.AbandonedBases, false);
      });
    }

    #endregion AbandonedBases Hook Handlers

    #region DangerousTreasures Hook Handlers

    private void OnPlayerEnteredDangerousEvent(
      BasePlayer player, Vector3 eventPos, bool allowPVP)
    {
      SetPvxEvent(player, allowPVP ? PVxType.PVP : PVxType.PVE, true);
    }

    private void OnPlayerExitedDangerousEvent(
      BasePlayer player, Vector3 eventPos, bool allowPVP)
    {
      SetPvxEvent(player, allowPVP ? PVxType.PVP : PVxType.PVE, false);
    }

    #endregion DangerousTreasures Hook Handlers

    #region CargoTrainTunnel Hook Handlers

    private void OnPlayerEnterPVPBubble(
      TrainEngine trainEngine, BasePlayer player)
    {
      NextTick(() =>
      {
        SetPvpBubble(player, true);
      });
    }

    private void OnPlayerExitPVPBubble(
      TrainEngine trainEngine, BasePlayer player)
    {
      NextTick(() =>
      {
        SetPvpBubble(player, false);
      });
    }

    private void OnTrainEventEnded(TrainEngine trainEngine)
    {
      NextTick(() =>
      {
        foreach (var player in BasePlayer.activePlayerList)
        {
          SetPvpBubble(player, false);
        }
      });
    }

    #endregion CargoTrainTunnel Hook Handlers

    #region PlayerBasePvpZones Hook Handlers

    private void OnPlayerBasePvpDelayStart(ulong playerId, string zoneId)
    {
      NextTick(() =>
      {
        var player = BasePlayer.FindByID(playerId);
        SetPvpDelay(player, PvpDelayType.PlayerBasePvpZones, true);
      });
    }

    private void OnPlayerBasePvpDelayStop(ulong playerId, string zoneId)
    {
      NextTick(() =>
      {
        var player = BasePlayer.FindByID(playerId);
        SetPvpDelay(player, PvpDelayType.PlayerBasePvpZones, false);
      });
    }

    #endregion PlayerBasePvpZones Hook Handlers

    #region DynamicPVP Hook Handlers

    private void OnPlayerAddedToPVPDelay(
      ulong playerId, string zoneId, float pvpDelayTime)
    {
      NextTick(() =>
      {
        var player = BasePlayer.FindByID(playerId);
        SetPvpDelay(player, PvpDelayType.DynamicPvp, true);
      });
    }

    private void OnPlayerRemovedFromPVPDelay(ulong playerId, string zoneId)
    {
      NextTick(() =>
      {
        var player = BasePlayer.FindByID(playerId);
        SetPvpDelay(player, PvpDelayType.DynamicPvp, false);
      });
    }

    #endregion DynamicPVP Hook Handlers

    #region KpucTaJl Hook Handlers

    private void OnAirEventStart(
      HashSet<BaseEntity> entities, Vector3 pos, float radius) =>
      NextTick(() => CreateOrUpdatePvpEvent(PvpEventType.Air, pos, radius));

    void OnAirEventEnd() => NextTick(() => DeletePvpEvent(PvpEventType.Air));

    private void OnArcticBaseEventStart(Vector3 pos, float radius) =>
      NextTick(() =>
        CreateOrUpdatePvpEvent(PvpEventType.ArcticBase, pos, radius));

    private void OnArcticBaseEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.ArcticBase));

    private void OnFerryTerminalEventStart(Vector3 pos, float radius) =>
      NextTick(() =>
        CreateOrUpdatePvpEvent(PvpEventType.FerryTerminal, pos, radius));

    private void OnFerryTerminalEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.FerryTerminal));

    private void OnGasStationEventStart(Vector3 pos, float radius) =>
      NextTick(() =>
        CreateOrUpdatePvpEvent(PvpEventType.GasStation, pos, radius));

    private void OnGasStationEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.GasStation));

    private void OnHarborEventStart(Vector3 pos, float radius) =>
      NextTick(() => CreateOrUpdatePvpEvent(PvpEventType.Harbor, pos, radius));

    private void OnHarborEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.Harbor));

    private void OnJunkyardEventStart(Vector3 pos, float radius) =>
      NextTick(() =>
        CreateOrUpdatePvpEvent(PvpEventType.Junkyard, pos, radius));

    private void OnJunkyardEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.Junkyard));

    private void OnPowerPlantEventStart(Vector3 pos, float radius) =>
      NextTick(() =>
        CreateOrUpdatePvpEvent(PvpEventType.PowerPlant, pos, radius));

    private void OnPowerPlantEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.PowerPlant));

    private void OnSatDishEventStart(Vector3 pos, float radius) =>
      NextTick(() => CreateOrUpdatePvpEvent(PvpEventType.SatDish, pos, radius));

    private void OnSatDishEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.SatDish));

    private void OnSupermarketEventStart(Vector3 pos, float radius) =>
      NextTick(() =>
        CreateOrUpdatePvpEvent(PvpEventType.Supermarket, pos, radius));

    private void OnSupermarketEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.Supermarket));

    private void OnWaterEventStart(
      HashSet<BaseEntity> entities, Vector3 pos, float radius) =>
      NextTick(() => CreateOrUpdatePvpEvent(PvpEventType.Water, pos, radius));

    void OnWaterEventEnd() =>
      NextTick(() => DeletePvpEvent(PvpEventType.Water));

    #endregion KpucTaJl Hook Handlers

    #endregion PVP Plugin Integrations

    #region Command Handlers

    private void ToggleUI(IPlayer iPlayer, string command, string[] args)
    {
      if (iPlayer.Object is not BasePlayer player) return;
      if (!IsValidPlayer(player, true)) return;
      if (null == GetPlayerWatcher(player))
      {
        OnPlayerConnected(player);
      }
      else
      {
        OnPlayerDisconnected(player, _uiName);
      }
    }

    #endregion Command Handlers

    #region UI Handling

    private void CreateUI(
      BasePlayer player, PVxType type, PVxType? oldType = null)
    {
      if (null == _configData || oldType == type) return;

      // CUI

      // destroy old UI (if there was one)
      // update: this should no longer be needed because we're setting the
      //  destroyUi parameter on the CuiPanel
      // if (null != oldType) CuiHelper.DestroyUi(player, _uiName);

      // create UI for new type if configured and enabled
      if (_configData.UISettings.TryGetValue(type, out var cuiSettings) &&
          cuiSettings.Enabled)
      {
        var cuiJson = cuiSettings.Json;
        if (!string.IsNullOrEmpty(cuiJson))
        {
          CuiHelper.AddUi(player, cuiJson);
        }
      }

      // Simple Status
      if (null == SimpleStatus) return;

      // clear old status (if there was one)
      if (null != oldType)
      {
        SimpleStatus.CallHook(
          "SetStatus", player.UserIDString, oldType.ToString(), 0);
      }

      // enable status for new type if configured and enabled
      if (_configData.SimpleStatusSettings.TryGetValue(type, out var ssSettings)
          && ssSettings.Enabled)
      {
        SimpleStatus.CallHook(
          "SetStatus", player.UserIDString, type.ToString());
      }
    }

    // forcefully destroy any active UIs for the given player
    private void DestroyUI(BasePlayer player)
    {
      CuiHelper.DestroyUi(player, _uiName);
      SS_HideAllStatuses(player);
    }

    #region SimpleStatus Integration

    // hide SimpleStatus statues for all enabled PVxType values
    private void SS_HideAllStatuses(BasePlayer player)
    {
      if (null == SimpleStatus || null == _configData) return;
      foreach (var (type, ssData) in _configData.SimpleStatusSettings)
      {
        if (null == ssData || !ssData.Enabled) continue;
        SimpleStatus.CallHook(
          "SetStatus", player.UserIDString, type.ToString(), 0);
      }
    }

    // register SimpleStatus statuses for each enabled PVxType value
    // NOTE: apparently there is no corresponding destroy API
    private void SS_CreateStatuses()
    {
      if (null == SimpleStatus || null == _configData) return;
      foreach (var (type, ssData) in _configData.SimpleStatusSettings)
      {
        if (null == ssData || !ssData.Enabled) continue;
        SimpleStatus.CallHook(
          "CreateStatus", this, type.ToString(), ssData.ToDict());
      }
    }

    #endregion SimpleStatus Integration

    #endregion UI Handling

    #region Config File Handling

    private sealed class NotificationSettings
    {
      [JsonProperty(PropertyName = "Chat notify enabled")]
      public bool ChatEnabled { get; set; } = false;

      [JsonProperty(PropertyName = "Chat notify prefix (empty string to disable)")]
      public string ChatPrefix { get; set; } = "[SuperPVxInfo]: ";

      [JsonProperty(PropertyName = "PopupNotifications notify enabled")]
      public bool PopupNotificationsEnabled { get; set; } = true;

      [JsonProperty(PropertyName = "PopupNotifications notify prefix (empty string to disable)")]
      public string PopupNotificationsPrefix { get; set; } = "";

      [JsonProperty(PropertyName = "Individual Notification Toggles")]
      public Dictionary<string, bool> Enabled { get; set; } = new();
    }

    private sealed class UiSettings
    {
      [JsonProperty(PropertyName = "Enabled")]
      public bool Enabled { get; set; } = true;

      [JsonProperty(PropertyName = "Min Anchor")]
      public string MinAnchor { get; set; } = "0.5 0";

      [JsonProperty(PropertyName = "Max Anchor")]
      public string MaxAnchor { get; set; } = "0.5 0";

      [JsonProperty(PropertyName = "Min Offset")]
      public string MinOffset { get; set; } = "190 30";

      [JsonProperty(PropertyName = "Max Offset")]
      public string MaxOffset { get; set; } = "250 60";

      [JsonProperty(PropertyName = "Layer")]
      public string Layer { get; set; } = "Hud";

      [JsonProperty(PropertyName = "Text")]
      public string Text { get; set; } = "PVP";

      [JsonProperty(PropertyName = "Text Size")]
      public int TextSize { get; set; } = 12;

      [JsonProperty(PropertyName = "Text Color")]
      public string TextColor { get; set; } = "1 1 1 1";

      [JsonProperty(PropertyName = "Background Color")]
      public string BackgroundColor { get; set; } = "0.8 0.5 0.1 0.8";

      [JsonProperty(PropertyName = "Fade In")]
      public float FadeIn { get; set; } = 0.25f;

      [JsonProperty(PropertyName = "Fade Out")]
      public float FadeOut { get; set; } = 0.25f;

      private string _json;

      public UiSettings()
      {
        _json = "";
      }

      [JsonIgnore]
      public string Json
      {
        get
        {
          // generate JSON for a PVxType on first use, and cache it in _json
          // ...unless this PVxType is disabled, in which case return the
          //  default empty string
          if (string.IsNullOrEmpty(_json))
          {
            _json = new CuiElementContainer
            {
              {
                new CuiPanel
                {
                  Image = { Color = BackgroundColor, FadeIn = FadeIn },
                  RectTransform = {
                    AnchorMin = MinAnchor, AnchorMax = MaxAnchor,
                    OffsetMin = MinOffset, OffsetMax = MaxOffset
                  },
                  CursorEnabled = false,
                  FadeOut = FadeOut,
                },
                Layer, _uiName, _uiName
              },
              {
                new CuiLabel
                {
                  Text = {
                    Text = Text,
                    FontSize = TextSize,
                    Align = TextAnchor.MiddleCenter,
                    Color = TextColor,
                    FadeIn = FadeIn,
                  },
                  RectTransform = {
                    AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.95"
                  },
                  FadeOut = FadeOut,
                },
                _uiName, CuiHelper.GetGuid()
              }
            }.ToJson();
          }
          return _json;
        }
      }
    }

    // Class for managing Simple Status settings for an individual PVxType enum
    //  value
    // Supports user-friendly JSON configuration values and uses them to
    //  produce a dictionary to be passed to Simple Status hooks.
    private sealed class SimpleStatusSettings
    {
      [JsonProperty(PropertyName = "Enabled")]
      public bool Enabled { get; set; } = false;

      [JsonProperty(PropertyName = "Background Color")]
      public string Color { get; set; } = "0.5 0.5 0.5 1.0";

      [JsonProperty(PropertyName = "Title Text")]
      public string TitleText { get; set; } = "PVx STATUS";

      [JsonProperty(PropertyName = "Title Color")]
      public string TitleColor { get; set; } = "1.0 1.0 1.0 1.0";

      [JsonProperty(PropertyName = "Status Text")]
      public string StatusText { get; set; } = "UNKNOWN";

      [JsonProperty(PropertyName = "Status Color")]
      public string StatusColor { get; set; } = "1.0 1.0 1.0 1.0";

      [JsonProperty(PropertyName = "Icon Path")]
      public string IconPath { get; set; } = "assets/icons/resource.png";

      [JsonProperty(PropertyName = "Icon Color")]
      public string IconColor { get; set; } = "1.0 1.0 1.0 1.0";

      // dictionary containing SimpleStatus values
      [JsonIgnore]
      private Dictionary<string, object> _dict = null;
      // accessor for SimpleStatus values dictionary
      // Populates and returns the dictionary on first call, and returns the
      //  cached dictionary on subsequent calls
      public Dictionary<string, object> ToDict()
      {
        if (null == _dict)
        {
          _dict = new()
          {
            ["color"] = Color,
            ["title"] = TitleText,
            ["titleColor"] = TitleColor,
            ["text"] = StatusText,
            ["textColor"] = StatusColor,
            ["icon"] = IconPath,
            ["iconColor"] = IconColor
          };
        }
        return _dict;
      }
    }

    private sealed class ConfigData
    {
      [JsonConverter(typeof(StringEnumConverter))]
      [JsonProperty(PropertyName = "Server Default PVx (PVP or PVE)")]
      public PVxType defaultType = PVxType.PVE;

      [JsonProperty(PropertyName = "Assume PVP Below Height")]
      public float pvpBelowHeight = -50.0f;

      [JsonProperty(PropertyName = "Assume PVP Above Height")]
      public float pvpAboveHeight = 1000.0f;

      [JsonProperty(PropertyName = "Toggle UI Command (empty string to disable)")]
      public string toggleCommand = "pvxui";

      [JsonProperty(PropertyName = "Seconds Between Updates")]
      public float updateIntervalSeconds = 1.0f;

      [JsonProperty(PropertyName = "Force Updates On State Change")]
      public bool forceUpdates = true;

      [JsonProperty(PropertyName = "Minimum Seconds Data File Saves")]
      public float saveIntervalSeconds = 5.0f;

      [JsonProperty(PropertyName = "PVE Exclusion Mapping Names (case insensitive substrings / none to disable)")]
      public HashSet<string> PveExclusionNames { get; set; } = new()
      {
        "exclude"
      };

      [JsonProperty(PropertyName = "PVE Zone Names (case insensitive substrings / none to disable)")]
      public HashSet<string> PveZoneManagerNames { get; set; } = new()
      {
        "PVE"
      };

      [JsonProperty(PropertyName = "PVP Zone Names (case insensitive substrings / none to disable)")]
      public HashSet<string> PvpZoneManagerNames { get; set; } = new()
      {
        "PVP"
      };

      [JsonProperty(PropertyName = "Notification Settings")]
      public NotificationSettings NotifySettings { get; set; } = new();

      [JsonProperty(PropertyName = "Default UI Settings")]
      public Dictionary<PVxType, UiSettings> UISettings { get; set; } = new()
      {
        [PVxType.PVE] = new UiSettings
        {
          Enabled = true,
          Text = "PVE",
          TextSize = 14,
          TextColor = "1.0 1.0 1.0 1.0",
          BackgroundColor = "0.0 1.0 0.0 0.8"
        },
        [PVxType.PVP] = new UiSettings
        {
          Enabled = true,
          Text = "PVP",
          TextSize = 14,
          TextColor = "1.0 1.0 1.0 1.0",
          BackgroundColor = "1.0 0.0 0.0 0.8"
        },
        [PVxType.PVPDelay] = new UiSettings
        {
          Enabled = true,
          Text = "WAIT",
          TextSize = 14,
          TextColor = "1.0 1.0 1.0 1.0",
          BackgroundColor = "1.0 0.5 0.0 0.8"
        },
        [PVxType.SafeZone] = new UiSettings
        {
          Enabled = true,
          Text = "SAFE",
          TextSize = 14,
          TextColor = "1.0 1.0 1.0 1.0",
          BackgroundColor = "0.0 0.0 1.0 0.8"
        }
      };

      [JsonProperty(PropertyName = "Simple Status UI Settings")]
      public Dictionary<PVxType, SimpleStatusSettings>
        SimpleStatusSettings { get; set; } = new()
      {
        [PVxType.PVE] = new SimpleStatusSettings
        {
          Enabled = false,
          Color = "0.0 0.7 0.0 0.8",
          TitleText = "PVE",
          TitleColor = "1.0 1.0 1.0 1.0",
          StatusText = "SuperPVxInfo",
          StatusColor = "0.0 1.0 0.0 0.2",
          IconPath = "assets/icons/resource.png",
          IconColor = "0.5 1.0 0.5 1.0"
        },
        [PVxType.PVP] = new SimpleStatusSettings
        {
          Enabled = false,
          Color = "0.7 0.0 0.0 0.8",
          TitleText = "PVP",
          TitleColor = "1.0 1.0 1.0 1.0",
          StatusText = "SuperPVxInfo",
          StatusColor = "1.0 0.0 0.0 0.2",
          IconPath = "assets/icons/warning_2.png",
          IconColor = "1.0 0.5 0.5 1.0"
        },
        [PVxType.PVPDelay] = new SimpleStatusSettings
        {
          Enabled = false,
          Color = "0.7 0.7 0.0 0.8",
          TitleText = "WAIT",
          TitleColor = "1.0 1.0 1.0 1.0",
          StatusText = "SuperPVxInfo",
          StatusColor = "1.0 1.0 0.0 0.2",
          IconPath = "assets/icons/stopwatch.png",
          IconColor = "1.0 1.0 0.5 1.0"
        },
        [PVxType.SafeZone] = new SimpleStatusSettings
        {
          Enabled = false,
          Color = "0.0 0.0 0.7 0.8",
          TitleText = "SAFE",
          TitleColor = "1.0 1.0 1.0 1.0",
          StatusText = "SuperPVxInfo",
          StatusColor = "0.0 0.0 1.0 0.2",
          IconPath = "assets/icons/peace.png",
          IconColor = "0.5 0.5 1.0 1.0"
        }
      };
    }

    protected override void LoadConfig()
    {
      base.LoadConfig();
      try
      {
        _configData = Config.ReadObject<ConfigData>();
        if (_configData == null)
        {
          LoadDefaultConfig();
        }
        else
        {
          // only PVE and PVP are allowed as the default server PVx types
          if (PVxType.PVE != _configData.defaultType &&
              PVxType.PVP != _configData.defaultType)
          {
            PrintWarning($"Forcing nonsensical configured default type {_configData.defaultType} to PVE");
            _configData.defaultType = PVxType.PVE;
          }
          // add default toggle states for any missing notifications
          foreach (var msgKey in _notifyMessages.Select(x => x.Key).Where(
            y => !_configData.NotifySettings.Enabled.ContainsKey(y)))
          {
            PrintWarning($"Adding new player notification toggle in disabled state: \"{msgKey}\"");
            _configData.NotifySettings.Enabled.Add(msgKey, false);
          }
          // remove toggle states for any unrecognized notifications in config
          var deadMsgKeys = Pool.Get<List<string>>();
          foreach (var (key, _) in _configData.NotifySettings.Enabled)
          {
            if (!_notifyMessages.ContainsKey(key)) deadMsgKeys.Add(key);
          }
          foreach (var deadMsgKey in deadMsgKeys)
          {
            PrintWarning($"Removing unknown/obsolete player notification toggle: \"{deadMsgKey}\"");
            _configData.NotifySettings.Enabled.Remove(deadMsgKey);
          }
          Pool.FreeUnmanaged(ref deadMsgKeys);
        }
      }
      catch (Exception ex)
      {
        PrintError($"Exception while loading configuration file:\n{ex}");
        LoadDefaultConfig();
      }
      SaveConfig();
    }

    protected override void LoadDefaultConfig()
    {
      PrintWarning("Creating a new configuration file");
      _configData = new ConfigData();
      // need to set default HashSet values here instead of in class, or else
      //  they will get re-added on every load
      _configData.PveExclusionNames.Add("exclude");
      _configData.PveZoneManagerNames.Add("PVE");
      _configData.PvpZoneManagerNames.Add("PVP");
      // also need to set default notification toggle states here, as the config
      //  class doesn't have access to the message dictionary
      foreach (var msgKvp in _notifyMessages)
      {
        _configData.NotifySettings.Enabled.Add(msgKvp.Key, true);
      }
    }

    protected override void SaveConfig() => Config.WriteObject(_configData);

    #endregion Config File Handling

    #region Data File Handling

    private struct PvpEventData
    {
      public Vector3 Location { get; set; }
      public float Radius { get; set; }

      public PvpEventData(Vector3 pos, float radius)
      {
        Location = pos;
        Radius = radius;
      }
    }

    private sealed class StoredData
    {
      public Dictionary<string, string> Mappings { get; set; } = new();

      public Dictionary<PvpEventType, PvpEventData> PvpEvents { get; set; } =
        new();
    }

    private void LoadData()
    {
      try
      {
        _storedData =
          Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
      }
      catch
      {
        _storedData = null;
      }
      if (_storedData == null) ClearData();
    }

    private void ClearData()
    {
      _storedData = new StoredData();
      SaveData();
    }

    // this is a frontend to WriteData() that enforces a minimum delay between
    //  data file writes
    private void SaveData()
    {
      // abort if save already pending
      if (null != _saveDataTimer) return;
      // start a save timer
      _saveDataTimer = timer.Once(
        null == _configData ? 5.0f : _configData.saveIntervalSeconds,
        () => WriteData());
    }

    private void WriteData()
    {
      if (null != _saveDataTimer)
      {
        if (!_saveDataTimer.Destroyed) _saveDataTimer.Destroy();
        _saveDataTimer = null;
      }
      Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);
    }

    #endregion Data File Handling

    #region Player Watcher

    // player watcher class
    public class PlayerWatcher : FacepunchBehaviour
    {
      // public static members

      // true if force updates should be allowed
      public static bool AllowForceUpdate { get; set; }
      // reference back to plugin
      public static SuperPVxInfo Instance { get; set; }
      // consider at/above this height to be PvP
      public static float PvpAboveHeight { get; set; }
      // consider at/below this height to be PvP
      public static float PvpBelowHeight { get; set; }
      // config-based update interval
      public static float UpdateIntervalSeconds { get; set; }

      // public non-static members

      // coordinates of current abandoned/raidable base (if applicable)
      public Vector3 BaseLocation { get; set; }
      // radius of current abandoned/raidable base (if applicable)
      public float BaseRadius { get; set; }
      // true if abandoned/raidable base exit check requested
      private bool _checkBase;
      public bool CheckBase {
        get { return _checkBase; }
        set { _forceUpdate |= value != _checkBase; _checkBase = value; }
      }
      // true if PVP delay check requested
      private bool _checkPvpDelay;
      public bool CheckPvpDelay {
        get { return _checkPvpDelay; }
        set { _forceUpdate |= value != _checkPvpDelay; _checkPvpDelay = value; }
      }
      // true if dangerous treasures exit check requested
      private bool _checkPVxEvent;
      public bool CheckPVxEvent {
        get { return _checkPVxEvent; }
        set { _forceUpdate |= value != _checkPVxEvent; _checkPVxEvent = value; }
      }
      // true if zone check requested
      private bool _checkZone;
      public bool CheckZone {
        get { return _checkZone; }
        set { _forceUpdate |= value != _checkZone; _checkZone = value; }
      }
      // non-null if in abandoned/raidable base or bubble
      private PVxType? _inBaseType;
      public PVxType? InBaseType {
        get { return _inBaseType; }
        set { _forceUpdate |= value != _inBaseType; _inBaseType = value; }
      }
      // in cargo train event PvP bubble
      private bool _inPvpBubble;
      public bool InPvpBubble {
        get { return _inPvpBubble; }
        set { _forceUpdate |= value != _inPvpBubble; _inPvpBubble = value; }
      }
      // in dangerous treasures PVx event
      private PVxType? _inPVxEventType;
      public PVxType? InPVxEventType {
        get { return _inPVxEventType; }
        set { _forceUpdate |= value != _inPVxEventType; _inPVxEventType = value; }
      }

      // private members

      // true if check delay should be preempted
      private bool _forceUpdate;
      // true if in height was within PvP thresholds on last check
      private bool? _heightAbovePvp;
      // true if in height was within PvP thresholds on last check
      private bool? _heightBelowPvp;
      // true if in PVP start/stop event on last check
      private bool _inPvpEvent;
      // true if in safe zone on last check
      private bool? _inSafeZone;
      // true if in tutorial island on last check
      private bool? _inTutorial;
      // non-null if in Zone Manager zone
      private PVxType? _inZoneType;
      // reference back to player
      private BasePlayer _player;
      // set of active PVP exit delays
      private HashSet<PvpDelayType> _pvpDelays = new();
      // PvX state on last check
      private PVxType? _pvxState;

      // public methods

      // record an active PVP delay
      public void AddPvpDelay(PvpDelayType type) =>
        _forceUpdate |= _pvpDelays.Add(type);

      // clear an active PVP delay
      // returns number of remaining active PVP delays
      public int ClearPvpDelay(PvpDelayType type)
      {
        _forceUpdate |= _pvpDelays.Remove(type);
        return _pvpDelays.Count;
      }

      // invoke watcher processing ASAP if warranted
      public void Force()
      {
        if (!_forceUpdate) return;
        if (AllowForceUpdate) Invoke("Watch", 0.0f);
        _forceUpdate = false;
      }

      // reset watcher state
      public void Init(
        PVxType? inBaseType = null, HashSet<PvpDelayType> pvpDelays = null,
        PVxType? inZoneType = null, BasePlayer player = null)
      {
        // (re)set public variables
        _checkBase = false;
        _checkPvpDelay = false;
        _checkPVxEvent = false;
        _checkZone = false;
        _inBaseType = inBaseType;
        _inPvpBubble = false;
        _inPVxEventType = null;

        // (re)set private variables
        _forceUpdate = false;
        _heightAbovePvp = null;
        _heightBelowPvp = null;
        _inPvpEvent = false;
        _inSafeZone = null;
        _inTutorial = null;
        _inZoneType = inZoneType;
        _player = player;
        if (null == pvpDelays)
        {
          _pvpDelays.Clear();
        }
        else
        {
          _pvpDelays = pvpDelays;
        }
        _pvxState = null;
      }

      // tear down watcher
      public void OnDestroy()
      {
        CancelInvoke();
        Init();
        Destroy(this);
      }

      // kick off the watcher's periodic processing
      // NOTE: this is used instead of Update() because the latter gets called
      //  much too frequently for our needs, wasting a lot of processing power
      //  on time counting overhead
      public void StartWatching() =>
        InvokeRepeating("Watch", 0.0f, UpdateIntervalSeconds);

      // update states, derive resulting PVx state, and - if the latter changed
      //  - update the GUI
      public void Watch()
      {
        // abort if plugin reference or player invalid
        if (null == _player || null == Instance ||
            !Instance.IsValidPlayer(_player, true))
        {
          return;
        }

        UpdateFlags();

        // determine new state
        var currentPvxState = GetPVxState();

        // check for change from old/no state
        if (currentPvxState != _pvxState)
        {
          // (re)create GUI for new state
          Instance.CreateUI(_player, currentPvxState, _pvxState);

          // record new state
          _pvxState = currentPvxState;
        }
      }

      // private methods

      // check for (and optionally notify player regarding) changes in a
      //  condition that requires polling by the watcher
      private void CheckPeriodic(
        ref bool? storedState, bool currentState,
        string enterMessage, string exitMessage)
      {
        if (currentState == storedState || null == Instance || null == _player)
        {
          // no change or missing stuff
          return;
        }
        if (currentState)
        {
          // false->true
          SendCannedMessage(enterMessage);
        }
        else
        {
          // true->false
          _checkZone = true;
          SendCannedMessage(exitMessage);
        }
        storedState = currentState;
      }

      // derive new PVx status from current set of states
      private PVxType GetPVxState()
      {
        // current order of precedence (subject to change):
        // - in Facepunch/ZoneManager safe zone => PvE
        // - in PvP base/bubble/event/zone => PvP
        // - above/below PvP height => PvP
        // - pvp exit delay active => PvP Delay
        // - in PvE base/event/tutorial/zone => PvE
        // - configured default
        if (true == _inSafeZone)             return PVxType.SafeZone;
        if (PVxType.SafeZone == _inZoneType) return PVxType.SafeZone;
        if (PVxType.PVP == _inBaseType)      return PVxType.PVP;
        if (_inPvpBubble)                    return PVxType.PVP;
        if (_inPvpEvent)                     return PVxType.PVP;
        if (PVxType.PVP == _inPVxEventType)  return PVxType.PVP;
        if (PVxType.PVP == _inZoneType)      return PVxType.PVP;
        if (true == _heightAbovePvp)         return PVxType.PVP;
        if (true == _heightBelowPvp)         return PVxType.PVP;
        if (_pvpDelays.Count > 0)            return PVxType.PVPDelay;
        if (PVxType.PVE == _inBaseType)      return PVxType.PVE;
        if (PVxType.PVE == _inPVxEventType)  return PVxType.PVE;
        if (true == _inTutorial)             return PVxType.PVE;
        if (PVxType.PVE == _inZoneType)      return PVxType.PVE;
        // defer to default state (or PVE if somehow not defined)
        return Instance?._configData == null ?
          PVxType.PVE : Instance._configData.defaultType;
      }

      // send message with the given key to watcher's player if appropriate
      private void SendCannedMessage(string message)
      {
        // note: _pvxState check is to suppress blasting change messages on
        //  initial check
        if (null == _player ||
            null == Instance ||
            null == _pvxState ||
            string.IsNullOrEmpty(message))
        {
          return;
        }

        Instance.SendCannedMessage(_player, message);
      }

      // perform any requested and/or periodic flag update checks that could
      //  affect PVx state
      private void UpdateFlags()
      {
        // abort if plugin reference or player invalid
        if (null == _player || null == Instance ||
            !Instance.IsValidPlayer(_player, true))
        {
          return;
        }

        // perform on-request checks

        // check for exit from base on request
        // TODO: this should no longer be needed for RaidableBases, but need to
        //  test with AbandonedBases someday before removing
        if (_checkBase)
        {
          if (null != _inBaseType &&
              Vector3.Distance(BaseLocation, _player.transform.position) >
                BaseRadius)
          {
            _inBaseType = null;
            SendCannedMessage("Unexpected Exit From Abandoned Or Raidable Base");
            // check PVP delay status as well, since that may now also be wrong
            if (_pvpDelays.Count > 0) _checkPvpDelay = true;
          }
          _checkBase = false;
        }

        // check for exit from PVx event on request
        if (_checkPVxEvent)
        {
          if (null != _inPVxEventType && !Instance.IsPlayerInPVxEvent(_player))
          {
            _inPVxEventType = null;
            SendCannedMessage("Unexpected Exit From Dangerous Treasures Event");
            // check PVP delay status as well, since that may now also be wrong
            if (_pvpDelays.Count > 0) _checkPvpDelay = true;
          }
          _checkPVxEvent = false;
        }

        // get zone type on request (if any)
        if (_checkZone)
        {
          _inZoneType = Instance.GetPlayerZoneType(_player);
          _checkZone = false;
        }

        // PVP delay check
        if (_checkPvpDelay)
        {
          _pvpDelays = Instance.IsPlayerInPVPDelay(_player.userID.Get());
          _checkPvpDelay = false;
        }

        // perform periodic checks

        // PVP event check
        _inPvpEvent = Instance.IsPlayerInPvpEvent(_player);

        // safe zone check
        CheckPeriodic(
          ref _inSafeZone, _player.InSafeZone(),
          "Safe Zone Entry", "Safe Zone Exit");

        // tutorial island check
        // don't bother with enter/exit messages for this
        CheckPeriodic(
          ref _inTutorial, _player.IsInTutorial,
          "", "");

        // height check
        CheckPeriodic(
          ref _heightAbovePvp, _player.transform.position.y > PvpAboveHeight,
          "PVP Height Entry", "PVP Height Exit"
        );

        // depth check
        CheckPeriodic(
          ref _heightBelowPvp, _player.transform.position.y < PvpBelowHeight,
          "PVP Depth Entry", "PVP Depth Exit"
        );
      }
    }

    #endregion Player Watcher
  }
}

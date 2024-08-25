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
  [Info("Super PVx Info", "HunterZ", "1.1.1")]
  [Description("Displays PvE/PvP/etc. status on player's HUD")]
  public class SuperPVxInfo : RustPlugin
  {
    #region Plugin Data

    public enum PVxType { PVE, PVP, PVPDelay, SafeZone }

    [PluginReference] private readonly Plugin?
      AbandonedBases, DynamicPVP, PlayerBasePvpZones, PopupNotifications,
      RaidableBases, SimpleStatus, ZoneManager;

    private ConfigData? _configData;

    // NOTE: this is not to be used directly for sending messages, but rather
    //  for populating the default language dictionary, and for enumerating
    //  which messages exist
    private readonly Dictionary<string, string> _notifyMessages = new()
    {
      ["Unexpected Exit From Abandoned Or Raidable Base"] =
        "{0}Left Abandoned/Raidable Base Zone",
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

    private StoredData? _storedData;

    private const string _uiName = "SuperPVxInfoUI";

    #endregion Plugin Data

    #region Utility Methods

    private bool IsValidPlayer(BasePlayer player, bool checkConnected)
    {
      if (player.IsNpc || !player.userID.IsSteamId()) return false;
      if (checkConnected && !player.IsConnected) return false;
      return true;
    }

    private PlayerWatcher? GetPlayerWatcher(BasePlayer player)
    {
      if (!IsValidPlayer(player, true)) return null;
      return player.GetComponent<PlayerWatcher>();
    }

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
      if (null != _storedData)
      {
        // purge any mappings that Zone Manager doesn't recognize
        var deadZoneIds = Pool.GetList<string>();
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
        Pool.FreeList(ref deadZoneIds);
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
      foreach (var player in BasePlayer.activePlayerList)
      {
        OnPlayerDisconnected(player, _uiName);
      }
      PlayerWatcher.Instance = null;
    }

    private void OnPlayerConnected(BasePlayer player)
    {
      if (!IsValidPlayer(player, true)) return;

      // abort if a watcher is already attached
      var watcher = player.GetComponent<PlayerWatcher>();
      if (watcher != null) return;

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
        watcher.CheckZone = true;
        watcher.CheckPvpDelay = true;
        watcher.Force();
      });
    }

    #endregion Oxide Methods

    #region TruePVE Hook Handlers

    private void AddOrUpdateMapping(string zoneId, string ruleset)
    {
      if (null == _storedData) return;
      if (string.IsNullOrEmpty(zoneId) || string.IsNullOrEmpty(ruleset))
      {
        return;
      }

      _storedData.Mappings[zoneId] = ruleset;
      SaveData();
    }

    private void RemoveMapping(string zoneId)
    {
      if (null == _storedData) return;
      if (string.IsNullOrEmpty(zoneId)) return;

      _storedData.Mappings.Remove(zoneId);
      SaveData();
    }

    #endregion TruePVE Hook Handlers

    #region ZoneManager Integration

    #region ZoneManager Utilities

    bool ZM_CheckZoneID(string zoneId)
    {
      return ZoneManager?.Call("CheckZoneID", zoneId) is string s && null != s;
    }

    private string[] ZM_GetPlayerZoneIDs(BasePlayer player)
    {
      if (ZoneManager?.Call("GetPlayerZoneIDs", player) is string[] s)
      {
        return s;
      }
      return new string[0];
    }

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

    private (string?, string?) GetSmallestZoneIdAndName(BasePlayer player)
    {
      if (ZoneManager == null) return (null, null);
      float smallestRadius = float.MaxValue;
      string? smallestId = null;
      string? smallestName = null;
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

    private bool ZM_GetZoneFlag(string zoneId, string zoneFlag)
    {
      if (ZoneManager?.Call("HasFlag", zoneId, zoneFlag) is bool flagState)
      {
        return flagState;
      }
      return false;
    }

    private string ZM_GetZoneName(string zoneId)
    {
      if (ZoneManager?.Call("GetZoneName", zoneId) is string zoneName)
      {
        return zoneName;
      }
      return "";
    }

    private float ZM_GetZoneRadius(string zoneId)
    {
      if (ZoneManager?.Call("GetZoneRadius", zoneId) is float zoneRadius)
      {
        return zoneRadius;
      }
      return 0.0f;
    }

    private Vector3 ZM_GetZoneSize(string zoneId)
    {
      if (ZoneManager?.Call("GetZoneSize", zoneId) is Vector3 zoneSize)
      {
        return zoneSize;
      }
      return Vector3.zero;
    }

    private bool IsExcludeZone(string zoneId)
    {
      if (null == _configData || null == _storedData) return false;
      if (!_storedData.Mappings.TryGetValue(zoneId, out string ruleset))
      {
        return false;
      }
      return _configData.PveExclusionNames.Any(
        x => ruleset.Contains(x, CompareOptions.IgnoreCase));
    }

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

    private void OnEnterZone(string zoneId, BasePlayer player)
    {
      CheckZone(player);
    }

    private void OnExitZone(string zoneId, BasePlayer player)
    {
      // check if player is exiting from a smaller zone into a larger one
      CheckZone(player);
    }

    #endregion ZoneManager Hook Handlers

    #endregion ZoneManager Integration

    #region PVP Plugins Integration

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
        foreach (var (_, _, allowPVP, _, _, _, _, _, _, _, intruders, _, _, _, _, _, _) in rbEvents)
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

    // check if player has any PVP delays active
    // this should only be called when hook-reported states don't exist yet, or
    //  can't be relied upon for some reason
    private bool IsPlayerInPVPDelay(ulong playerID)
    {
      if (PlayerBasePvpZones != null && !string.IsNullOrEmpty(Convert.ToString(
          PlayerBasePvpZones.Call("OnPlayerBasePvpDelayQuery", playerID))))
      {
        return true;
      }

      if (DynamicPVP != null && Convert.ToBoolean(
          DynamicPVP.Call("IsPlayerInPVPDelay", playerID)))
      {
        return true;
      }

      if (RaidableBases != null && Convert.ToBoolean(
          RaidableBases.Call("HasPVPDelay", playerID)))
      {
        return true;
      }

      if (AbandonedBases != null && Convert.ToBoolean(
          AbandonedBases.Call("HasPVPDelay", playerID)))
      {
        return true;
      }

      return false;
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
    private void SetPvpDelay(BasePlayer player, bool state)
    {
      if (!IsValidPlayer(player, true)) return;
      var watcher = GetPlayerWatcher(player);
      if (null == watcher) return;
      if (!state)
      {
        watcher.CheckBase = true;
        watcher.CheckZone = true;
      }
      watcher.InPvpDelay = state;
      watcher.Force();
    }

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
        SetPvpDelay(player, true);
      });
    }

    private void OnPlayerPvpDelayReset(
      BasePlayer player, int mode, Vector3 location, bool allowPvP, string id,
      float _, float __, float loadTime, ulong ownerId, string baseName,
      DateTime spawnDateTime, DateTime despawnDateTime, int lootAmountRemaining)
    {
      NextTick(() =>
      {
        SetPvpDelay(player, true);
      });
    }

    private void OnPlayerPvpDelayExpired(
      BasePlayer player, int mode, Vector3 location, bool allowPvP, string id,
      float _, float __, float loadTime, ulong ownerId, string baseName,
      DateTime spawnDateTime, DateTime despawnDateTime, int lootAmountRemaining)
    {
      NextTick(() =>
      {
        SetPvpDelay(player, false);
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

    private void OnPlayerExitAbandonedBase(BasePlayer player, Vector3 location, bool allowPVP)
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
        SetPvpDelay(player, true);
      });
    }

    private void OnPlayerPvpDelayExpiredII(
      BasePlayer player, ulong userid, Vector3 eventPos,
      List<BasePlayer> intruders, List<BaseEntity> entities)
    {
      NextTick(() =>
      {
        SetPvpDelay(player, false);
      });
    }

    #endregion AbandonedBases Hook Handlers

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
        SetPvpDelay(player, true);
      });
    }

    private void OnPlayerBasePvpDelayStop(ulong playerId, string zoneId)
    {
      NextTick(() =>
      {
        var player = BasePlayer.FindByID(playerId);
        SetPvpDelay(player, false);
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
        SetPvpDelay(player, true);
      });
    }

    private void OnPlayerRemovedFromPVPDelay(ulong playerId, string zoneId)
    {
      NextTick(() =>
      {
        var player = BasePlayer.FindByID(playerId);
        SetPvpDelay(player, false);
      });
    }

    #endregion DynamicPVP Hook Handlers

    #endregion PVP Plugins Integration

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
      if (null != oldType) CuiHelper.DestroyUi(player, _uiName);

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
                Layer, _uiName
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
      private Dictionary<string, object>? _dict = null;
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
      public NotificationSettings NotifySettings {get; set; } = new();

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
          var deadMsgKeys = Pool.GetList<string>();
          foreach (var (key, _) in _configData.NotifySettings.Enabled)
          {
            if (!_notifyMessages.ContainsKey(key)) deadMsgKeys.Add(key);
          }
          foreach (var deadMsgKey in deadMsgKeys)
          {
            PrintWarning($"Removing unknown/obsolete player notification toggle: \"{deadMsgKey}\"");
            _configData.NotifySettings.Enabled.Remove(deadMsgKey);
          }
          Pool.FreeList(ref deadMsgKeys);
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

    protected override void SaveConfig()
    {
      Config.WriteObject(_configData);
    }

    #endregion Config File Handling

    #region Data File Handling

    private sealed class StoredData
    {
      public Dictionary<string, string> Mappings { get; set; } = new();
    }

    private void LoadData()
    {
      try
      {
        _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
      }
      catch
      {
        _storedData = null;
      }
      if (_storedData == null)
      {
        ClearData();
      }
    }

    private void ClearData()
    {
      _storedData = new StoredData();
      SaveData();
    }

    private void SaveData()
    {
      Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);
    }

    #endregion Data File Handling

    #region Player Watcher

    // player watcher class
    public class PlayerWatcher : FacepunchBehaviour
    {
      // true if force updates should be allowed
      public static bool AllowForceUpdate { get; set; }
      // reference back to plugin
      public static SuperPVxInfo? Instance { get; set; }
      // consider at/above this height to be PvP
      public static float PvpAboveHeight { get; set; }
      // consider at/below this height to be PvP
      public static float PvpBelowHeight { get; set; }
      // config-based update interval
      public static float UpdateIntervalSeconds { get; set; }
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
      // true if zone check requested
      private bool _checkZone;
      public bool CheckZone {
        get { return _checkZone; }
        set { _forceUpdate |= value != _checkZone; _checkZone = value; }
      }
      // coordinates of current abandoned/raidable base (if applicable)
      public Vector3 BaseLocation { get; set; }
      // radius of current abandoned/raidable base (if applicable)
      public float BaseRadius { get; set; }
      // true if check delay should be preempted
      private bool _forceUpdate;
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
      // true if PvP removal delay in effect
      private bool _inPvpDelay;
      public bool InPvpDelay {
        get { return _inPvpDelay; }
        set { _forceUpdate |= value != _inPvpDelay; _inPvpDelay = value; }
      }
      // non-null if in Zone Manager zone
      private PVxType? _inZoneType;
      // PvX state on last check
      private PVxType? _lastPvxState;
      // reference back to player
      private BasePlayer? _player;
      // true if in height was within PvP thresholds on last check
      private bool? _wasAbovePvpHeight;
      // true if in height was within PvP thresholds on last check
      private bool? _wasBelowPvpHeight;
      // true if in safe zone on last check
      private bool? _wasInSafeZone;

      // check for (and optionally notify player regarding) changes in a
      //  condition that requires polling by the watcher
      private bool? CheckPeriodic(
        bool current, bool? previous,
        string enterMessage, string exitMessage)
      {
        if (current == previous || null == Instance || null == _player)
        {
          return previous;
        }
        if (current)
        {
          Instance.SendCannedMessage(_player, enterMessage);
        }
        else if (true == previous)
        {
          _checkZone = true;
          Instance.SendCannedMessage(_player, exitMessage);
        }
        return current;
      }

      // derive new PVx status from current set of states
      private PVxType GetPVxState(
        bool isInSafeZone, bool isAbovePvpHeight, bool isBelowPvpHeight)
      {
        // current order of precedence (subject to change):
        // - in Facepunch safe zone => PvE
        // - in ZoneManager safe zone => PvE
        // - in PvP base/bubble/zone => PvP
        // - above PvP height => PvP
        // - below PvP depth => PvP
        // - pvp exit delay active => PvP
        // - in PvE base/zone => PvE
        // - configured default
        if (isInSafeZone)                    return PVxType.SafeZone;
        if (PVxType.SafeZone == _inZoneType) return PVxType.SafeZone;
        if (PVxType.PVP == _inBaseType)      return PVxType.PVP;
        if (_inPvpBubble)                    return PVxType.PVP;
        if (PVxType.PVP == _inZoneType)      return PVxType.PVP;
        if (isAbovePvpHeight)                return PVxType.PVP;
        if (isBelowPvpHeight)                return PVxType.PVP;
        if (_inPvpDelay)                     return PVxType.PVPDelay;
        if (PVxType.PVE == _inBaseType)      return PVxType.PVE;
        if (PVxType.PVE == _inZoneType)      return PVxType.PVE;
        // defer to default state (or PVE if somehow not defined)
        return Instance?._configData == null ?
          PVxType.PVE : Instance._configData.defaultType;
      }

      // kick off the watcher's periodic processing
      // NOTE: this is used instead of Update() because the latter gets called
      //  much too frequently for our needs, wasting a lot of processing power
      //  on time counting overhead
      public void StartWatching()
      {
        InvokeRepeating("Watch", 0.0f, UpdateIntervalSeconds);
      }

      // invoke watcher processing ASAP if warranted
      public void Force()
      {
        if (!_forceUpdate) return;
        if (AllowForceUpdate) Invoke("Watch", 0.0f);
        _forceUpdate = false;
      }

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

        // gather some up-front data

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
            Instance.SendCannedMessage(
              _player, "Unexpected Exit From Abandoned Or Raidable Base");
            // check PVP delay status as well, since that may now also be wrong
            if (_inPvpDelay) _checkPvpDelay = true;
          }
          _checkBase = false;
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
          _inPvpDelay = Instance.IsPlayerInPVPDelay(_player.userID.Get());
          _checkPvpDelay = false;
        }

        // safe zone check
        bool isInSafeZone = _player.InSafeZone();
        _wasInSafeZone = CheckPeriodic(
          isInSafeZone, _wasInSafeZone, "Safe Zone Entry", "Safe Zone Exit");

        // height check
        bool isAbovePvpHeight =
          _player.transform.position.y > PvpAboveHeight;
        _wasAbovePvpHeight = CheckPeriodic(
          isAbovePvpHeight, _wasAbovePvpHeight,
          "PVP Height Entry", "PVP Height Exit"
        );

        // depth check
        bool isBelowPvpHeight =
          _player.transform.position.y < PvpBelowHeight;
        _wasBelowPvpHeight = CheckPeriodic(
          isBelowPvpHeight, _wasBelowPvpHeight,
          "PVP Depth Entry", "PVP Depth Exit"
        );

        // determine new state
        var newPvxState =
          GetPVxState(isInSafeZone, isAbovePvpHeight, isBelowPvpHeight);

        // abort if no state change
        if (newPvxState == _lastPvxState) return;

        // (re)create GUI for new state
        Instance.CreateUI(_player, newPvxState, _lastPvxState);

        // record new state
        _lastPvxState = newPvxState;
      }

      // tear down watcher
      public void OnDestroy()
      {
        CancelInvoke();
        Init();
        Destroy(this);
      }

      // reset watcher state
      public void Init(
        PVxType? inBaseType = null, bool inPvpDelay = false,
        PVxType? inZoneType = null, BasePlayer? player = null)
      {
        _checkBase = false;
        _checkPvpDelay = false;
        _checkZone = false;
        _forceUpdate = false;
        _inBaseType = inBaseType;
        _inPvpBubble = false;
        _inPvpDelay = inPvpDelay;
        _inZoneType = inZoneType;
        _lastPvxState = null;
        _player = player;
        _wasAbovePvpHeight = null;
        _wasBelowPvpHeight = null;
        _wasInSafeZone = null;
      }
    }

    #endregion Player Watcher
  }
}

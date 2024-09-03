// Requires: ZoneManager

using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
  [Info("Player Base PvP Zones", "HunterZ", "1.0.5")]
  [Description("Maintains Zone Manager / TruePVE exclusion zones around player bases")]
  public class PlayerBasePvpZones : RustPlugin
  {
    #region Fields

    // user-defined plugin config data
    private ConfigData _configData = new();

    // active building zones by TC ID
    private Dictionary<NetworkableId, BuildingData> _buildingData = new();

    // active building zone check delay timers by TC ID
    private Dictionary<NetworkableId, Timer> _buildingCheckTimers = new();

    // building zone creation delay timers by TC ID
    private Dictionary<NetworkableId, Timer> _buildingCreateTimers = new();

    // building zone deletion delay timers by TC ID
    private Dictionary<NetworkableId, Timer> _buildingDeleteTimers = new();

    // set of containing base zone net IDs by player ID
    // NOTE: to avoid heap churn, the HashSet's are pooled, and are only freed
    //  in Unload(). This means that empty sets may exist when a player is not
    //  in any base zones!
    private readonly Dictionary<ulong, HashSet<NetworkableId>> _playerZones =
      new();

    // active pvp delay timer + zoneID by player ID
    private readonly Dictionary<ulong, (Timer, string)> _pvpDelayTimers = new();

    // active legacy shelter zones by shelter ID
    private Dictionary<NetworkableId, ShelterData> _shelterData = new();

    // legacy shelter zone creation delay timers by shelter ID
    private Dictionary<NetworkableId, Timer> _shelterCreateTimers = new();

    // legacy shelter zone deletion delay timers by shelter ID
    private Dictionary<NetworkableId, Timer> _shelterDeleteTimers = new();

    // active tugboat zones by tugboat ID
    private Dictionary<NetworkableId, TugboatData> _tugboatData = new();

    // tugboat zone deletion delay timers by tugboat ID
    private Dictionary<NetworkableId, Timer> _tugboatDeleteTimers = new();

    // ZoneManager zone ID prefix
    private const string _zoneIdPrefix = "PlayerBasePVP:";

    #endregion Fields

    #region Core Methods

    // generate a current 3D bounding box around a base
    private Bounds CalculateBuildingBounds(BuildingPrivlidge toolCupboard)
    {
      // start with a box around the TC, whose space diagonal is the minimum
      //  total building radius
      var buildingBounds = new Bounds(
        toolCupboard.CenterPoint(),
        Vector3.one * _configData.building.minimumBuildingRadius / (float)Math.Sqrt(3));

      // get building, aborting if not found
      var building = toolCupboard.GetBuilding();
      if (null == building) return buildingBounds;

      // precalculate extents for a cube whose space diagonal is the radius
      var minimumBlockExtents =
        Vector3.one * _configData.building.minimumBlockRadius / (float)Math.Sqrt(3);
      // precalculate square of minimum block magnitude for comparison purposes
      var minimumBlockSqrMagnitude =
        _configData.building.minimumBlockRadius *
        _configData.building.minimumBlockRadius;

      // grow buildingBounds volume to contain all building blocks
      foreach (var entity in building.buildingBlocks)
      {
        // get the block's bounds and center those on its world coordinates
        var entityBounds = entity.bounds;
        entityBounds.center = entity.CenterPoint();
        // try to be efficient by checking the largest dimension followed by
        //  the squared magnitude
        // this will probably always return true, unless minimumBlockRadius is
        //  set to a very small value
        if (entityBounds.extents.Max() < _configData.building.minimumBlockRadius
            && entityBounds.extents.sqrMagnitude < minimumBlockSqrMagnitude)
        {
          // block is smaller than the minimum, so substitute in the minimum
          entityBounds.extents = minimumBlockExtents;
        }
        // grow volume to contain this block
        buildingBounds.Encapsulate(entityBounds);
      }

      return buildingBounds;
    }

    // derive a radius from building bounding box
    private static float CalculateBuildingRadius(Bounds buildingBounds) =>
      buildingBounds.extents.magnitude;

    // remove+destroy timer stored in the given dictionary under the given key
    private bool CancelDictionaryTimer<T>(
      ref Dictionary<T, Timer> dictionary, T key)
    {
      if (dictionary.Remove(key, out var cTimer))
      {
        cTimer.Destroy();
        return true;
      }
      return false;
    }

    // BuildingBlock wrapper for GetToolCupboard()
    private BuildingPrivlidge? GetToolCupboard(BuildingBlock buildingBlock) =>
      GetToolCupboard(buildingBlock.GetBuilding());

    // try to find and return a physically-attached TC for the given building,
    //  or null if no suitable result found
    // only supports player-owned TCs (NOT to be confused with player-authed!)
    // this is our differentiator of whether a building should have a zone
    private BuildingPrivlidge? GetToolCupboard(
      BuildingManager.Building building)
    {
      // check the easy stuff first
      if (null == building ||
          !building.HasBuildingBlocks() ||
          !building.HasBuildingPrivileges())
      {
        return null;
      }

      // since the building is in *some* TC range, see if any of those are
      //  physically attached (credit: Kulltero for this more efficient method)
      foreach (var toolCupboard in building.buildingPrivileges)
      {
        if (building.decayEntities.Contains(toolCupboard))
        {
          // only one TC can be connected, so return it, unless it's not
          //  player-owned
          return IsPlayerOwned(toolCupboard) ? toolCupboard : null;
        }
      }

      // no attached player-owned TC found
      return null;
    }

    // get unique ID for any BaseNetworkable object
    // credit: Karuza for suggesting this as the best unique ID approach
    private static NetworkableId GetNetworkableID(
      BaseNetworkable baseNetworkable) =>
      baseNetworkable.net.ID;

    private bool IsKnownPlayerBaseId(NetworkableId networkableId) =>
      _buildingData.ContainsKey(networkableId) ||
      _buildingCreateTimers.ContainsKey(networkableId) ||
      _shelterData.ContainsKey(networkableId) ||
      _shelterCreateTimers.ContainsKey(networkableId) ||
      _tugboatData.ContainsKey(networkableId);

    private static ulong? GetOwnerID(EntityPrivilege legacyShelter)
    {
      foreach (var playerNameID in legacyShelter.authorizedPlayers)
      {
        if (playerNameID.userid.IsSteamId()) return playerNameID.userid;
      }
      return null;
    }

    private static VehiclePrivilege? GetVehiclePrivilege(BaseVehicle vehicle)
    {
      foreach (BaseEntity child in vehicle.children)
      {
        if (child is not VehiclePrivilege) continue;
        return (VehiclePrivilege)child;
      }
      return null;
    }

    // OwnerID is zero for shelters for some reason, so check auth list
    private static bool IsPlayerOwned(EntityPrivilege? legacyShelter)
    {
      if (null == legacyShelter) return false;
      foreach (var playerNameID in legacyShelter.authorizedPlayers)
      {
        if (playerNameID.userid.IsSteamId()) return true;
      }
      return false;
    }

    private static bool IsPlayerOwned(DecayEntity? decayEntity) =>
      null != decayEntity && decayEntity.OwnerID.IsSteamId();

    private static bool IsValid(BaseNetworkable? baseNetworkable) =>
      null != baseNetworkable &&
      !baseNetworkable.IsDestroyed &&
      null != baseNetworkable.net &&
      null != baseNetworkable.transform;

    private void NotifyOwnerAbort(ulong ownerID)
    {
      var player = BasePlayer.FindByID(ownerID);
      if (null == player || !player.userID.IsSteamId()) return;
      var message = lang.GetMessage(
        "NotifyOwnerAbort", this, player.UserIDString);
      SendReply(player, string.Format(message, _configData.prefixNotify));
    }

    private void NotifyOwnerCreate(ulong ownerID)
    {
      var player = BasePlayer.FindByID(ownerID);
      if (null == player || !player.userID.IsSteamId()) return;
      var message = lang.GetMessage(
        "NotifyOwnerCreate", this, player.UserIDString);
      SendReply(player, string.Format(message, _configData.prefixNotify, _configData.createDelaySeconds));
    }

    private void NotifyZoneDelete(NetworkableId baseID)
    {
      var playersInZone = ZM_GetPlayersInZone(GetZoneID(baseID));
      if (null == playersInZone) return;
      foreach (var player in playersInZone)
      {
        if (null == player || !player.userID.IsSteamId()) continue;
        var message = lang.GetMessage(
          "NotifyZoneDelete", this, player.UserIDString);
        SendReply(player, string.Format(message, _configData.prefixNotify, _configData.deleteDelaySeconds));
      }
    }

    // detect and handle any updates to physical footprint of given TC ID's
    //  building
    private void CheckBuildingData(NetworkableId toolCupboardID)
    {
      // clean up any check timer that may have invoked this
      CancelDictionaryTimer(ref _buildingCheckTimers, toolCupboardID);

      // abort if this building is pending creation or deletion
      if (_buildingCreateTimers.ContainsKey(toolCupboardID) ||
          _buildingDeleteTimers.ContainsKey(toolCupboardID))
      {
        return;
      }

      // get building record, aborting if unknown or no TC reference
      // NOTE: lacking a TC reference is pathological, as it should only occur
      //  when a building is pending deletion, which we've already checked
      if (!_buildingData.TryGetValue(toolCupboardID, out var buildingData) ||
          null == buildingData.ToolCupboard ||
          !IsValid(buildingData.ToolCupboard))
      {
        return;
      }

      // get cached TC
      BuildingPrivlidge toolCupboard = buildingData.ToolCupboard;

      // get building record, aborting if not found
      var building = toolCupboard.GetBuilding();
      if (null == building) return;

      // get updated building footprint data
      var buildingBounds = CalculateBuildingBounds(toolCupboard);
      var radius = CalculateBuildingRadius(buildingBounds);

      // abort if footprint basically unchanged
      if (buildingData.Location == buildingBounds.center &&
          Math.Abs(buildingData.Radius - radius) <= float.Epsilon)
      {
        return;
      }

      // update existing building data record
      buildingData.Update(buildingBounds.center, radius);
      // update zone
      ZM_CreateOrUpdateZone(buildingData, toolCupboardID);

      // schedule a follow-up in case more changes are occuring
      ScheduleCheckBuildingData(toolCupboardID);
    }

    // create new building record + zone for given TC
    private void CreateBuildingData(BuildingPrivlidge toolCupboard)
    {
      // abort if TC object is destroyed
      if (!IsValid(toolCupboard)) return;

      var toolCupboardID = GetNetworkableID(toolCupboard);

      // clean up any creation timer that may have invoked this
      CancelDictionaryTimer(ref _buildingCreateTimers, toolCupboardID);

      // if a building already exists, then this call is probably redundant
      if (_buildingData.ContainsKey(toolCupboardID)) return;

      // get initial building footprint data
      var buildingBounds = CalculateBuildingBounds(toolCupboard);
      var radius = CalculateBuildingRadius(buildingBounds);

      // create + record new building data record
      var buildingData =
        new BuildingData(toolCupboard, buildingBounds.center, radius);
      _buildingData.Add(toolCupboardID, buildingData);

      // create zone
      if (ZM_CreateOrUpdateZone(buildingData, toolCupboardID))
      {
        // ...and mapping
        TP_AddOrUpdateMapping(toolCupboardID);
      }

      // schedule a follow-up in case more changes are occuring
      ScheduleCheckBuildingData(toolCupboardID);
    }

    // delete building record + zone for given TC ID
    private void DeleteBuildingData(NetworkableId toolCupboardID)
    {
      // clean up any deletion timer that may have invoked this
      CancelDictionaryTimer(ref _buildingDeleteTimers, toolCupboardID);

      // remove building record to local variable, aborting if not found
      if (!_buildingData.Remove(toolCupboardID, out var buildingData)) return;

      // destroy building record
      buildingData.Destroy();

      // destroy building zone
      ZM_EraseZone(toolCupboardID);

      // destroy mapping
      TP_RemoveMapping(toolCupboardID);
    }

    // schedule a delayed building update check
    // this should be used where practical to reduce recalculation of base
    //  footprint when lots of building blocks are being spawned/destroyed
    //  around the same time
    // also provides various sanity checks
    private void ScheduleCheckBuildingData(NetworkableId toolCupboardID)
    {
      // abort if base is unknown, or if any timers are already running
      if (!_buildingData.ContainsKey(toolCupboardID) ||
          _buildingCheckTimers.ContainsKey(toolCupboardID) ||
          _buildingCreateTimers.ContainsKey(toolCupboardID) ||
          _buildingDeleteTimers.ContainsKey(toolCupboardID))
      {
        return;
      }

      // schedule call
      _buildingCheckTimers.Add(toolCupboardID, timer.Once(
        _configData.building.checkDelaySeconds,
        () => CheckBuildingData(toolCupboardID)));
    }

    // schedule a delayed building creation
    // this should be used during normal operations, in order to apply delays
    //  and/or notifications per plugin configuration
    private void ScheduleCreateBuildingData(BuildingPrivlidge toolCupboard)
    {
      var toolCupboardID = GetNetworkableID(toolCupboard);

      // abort if building is already known, or if any timers are already
      //  running
      if (_buildingData.ContainsKey(toolCupboardID) ||
          _buildingCheckTimers.ContainsKey(toolCupboardID) ||
          _buildingCreateTimers.ContainsKey(toolCupboardID) ||
          _buildingDeleteTimers.ContainsKey(toolCupboardID))
      {
        return;
      }

      // schedule call
      _buildingCreateTimers.Add(toolCupboardID, timer.Once(
        _configData.createDelaySeconds,
        () => CreateBuildingData(toolCupboard)));

      // notify player
      if (_configData.createNotify)
      {
        NotifyOwnerCreate(toolCupboard.OwnerID);
      }
    }

    // schedule a delayed building deletion
    // this should be used during normal operations, in order to apply delays
    //  and/or notifications per plugin configuration
    private void ScheduleDeleteBuildingData(
      NetworkableId toolCupboardID, ulong ownerID)
    {
      bool notifyZone = _configData.deleteNotify;

      // notify owner if this was pending creation
      if (CancelDictionaryTimer(ref _buildingCreateTimers, toolCupboardID) &&
          _configData.createNotify)
      {
        NotifyOwnerAbort(ownerID);
        // suppress zone notify because there isn't one yet
        notifyZone = false;
      }

      // also cancel any check timer
      CancelDictionaryTimer(ref _buildingCheckTimers, toolCupboardID);

      // abort if building is unknown, or if deletion timer is already running
      if (!_buildingData.ContainsKey(toolCupboardID) ||
          _buildingDeleteTimers.ContainsKey(toolCupboardID))
      {
        return;
      }

      // schedule call
      _buildingDeleteTimers.Add(toolCupboardID, timer.Once(
        _configData.deleteDelaySeconds,
        () => DeleteBuildingData(toolCupboardID)));

      // notify players
      if (notifyZone) NotifyZoneDelete(toolCupboardID);
    }

    // create a new shelter record + zone for given legacy shelter
    private void CreateShelterData(EntityPrivilege legacyShelter)
    {
      // abort if shelter object is destroyed
      if (!IsValid(legacyShelter)) return;

      var legacyShelterID = GetNetworkableID(legacyShelter);

      // clean up any creation timer that may have invoked this
      CancelDictionaryTimer(ref _shelterCreateTimers, legacyShelterID);

      // if a shelter already exists, then this call is probably redundant
      if (_shelterData.ContainsKey(legacyShelterID)) return;

      // create + record new shelter data record
      var shelterData =
        new ShelterData(legacyShelter, _configData.shelter.radius);
      _shelterData.Add(legacyShelterID, shelterData);

      // create zone
      if (ZM_CreateOrUpdateZone(shelterData, legacyShelterID))
      {
        // ...and mapping
        TP_AddOrUpdateMapping(legacyShelterID);
      }
    }

    // delete shelter record + zone for given LS ID
    private void DeleteShelterData(NetworkableId legacyShelterID)
    {
      // clean up any deletion timer that may have invoked this
      CancelDictionaryTimer(ref _shelterDeleteTimers, legacyShelterID);

      // remove shelter record to local variable, aborting if not found
      if (!_shelterData.Remove(legacyShelterID, out var shelterData)) return;

      // destroy shelter record
      shelterData.Destroy();

      // destroy shelter zone
      ZM_EraseZone(legacyShelterID);

      // destroy mapping
      TP_RemoveMapping(legacyShelterID);
    }

    // schedule a delayed shelter creation
    // this should be used during normal operations, in order to apply delays
    //  and/or notifications per plugin configuration
    private void ScheduleCreateShelterData(EntityPrivilege legacyShelter)
    {
      var legacyShelterID = GetNetworkableID(legacyShelter);

      // abort if shelter is already known, or if any timers are already running
      if (_shelterData.ContainsKey(legacyShelterID) ||
          _shelterCreateTimers.ContainsKey(legacyShelterID) ||
          _shelterDeleteTimers.ContainsKey(legacyShelterID))
      {
        return;
      }

      // schedule call
      _shelterCreateTimers.Add(legacyShelterID, timer.Once(
        _configData.createDelaySeconds,
        () => CreateShelterData(legacyShelter)));

      // notify players
      if (_configData.createNotify)
      {
        var ownerID = GetOwnerID(legacyShelter);
        if (ownerID != null) NotifyOwnerCreate((ulong)ownerID);
      }
    }

    // schedule a delayed shelter deletion
    // this should be used during normal operations, in order to apply delays
    //  and/or notifications per plugin configuration
    private void ScheduleDeleteShelterData(
      NetworkableId legacyShelterID, ulong? ownerID)
    {
      bool notifyZone = _configData.deleteNotify;

      // notify owner if this was pending creation
      if (CancelDictionaryTimer(ref _shelterCreateTimers, legacyShelterID) &&
          _configData.createNotify)
      {
        if (null != ownerID) NotifyOwnerAbort((ulong)ownerID);
        // suppress zone notify because there isn't one yet
        notifyZone = false;
      }

      // abort if shelter is unknown, or if deletion timer is already running
      if (!_shelterData.ContainsKey(legacyShelterID) ||
          _shelterDeleteTimers.ContainsKey(legacyShelterID))
      {
        return;
      }

      // schedule call
      _shelterDeleteTimers.Add(legacyShelterID, timer.Once(
        _configData.deleteDelaySeconds,
        () => DeleteShelterData(legacyShelterID)));

      // notify players in zone
      if (notifyZone) NotifyZoneDelete(legacyShelterID);
    }

    // create a new tugboat record + zone for given tugboat
    private void CreateTugboatData(VehiclePrivilege tugboat)
    {
      // abort if tugboat object is destroyed
      if (!IsValid(tugboat)) return;

      var tugboatID = GetNetworkableID(tugboat);

      // if a tugboat already exists, then this call is probably redundant
      if (_tugboatData.ContainsKey(tugboatID)) return;

      // create + record new tugboat data record
      var tugboatData = new TugboatData(
        tugboat, _configData.tugboat.radius,
        _configData.tugboat.forceNetworking, _configData.tugboat.forceBuoyancy);
      _tugboatData.Add(tugboatID, tugboatData);

      // create zone
      if (ZM_CreateOrUpdateZone(tugboatData, tugboatID))
      {
        // ...and mapping
        TP_AddOrUpdateMapping(tugboatID);
      }
    }

    // delete tugboat record + zone for given tugboat ID
    private void DeleteTugboatData(NetworkableId tugboatID)
    {
      // clean up any deletion timer that may have invoked this
      CancelDictionaryTimer(ref _tugboatDeleteTimers, tugboatID);

      // remove tugboat record to local variable, aborting if not found
      if (!_tugboatData.Remove(tugboatID, out var tugboatData)) return;

      // destroy tugboat record
      tugboatData.Destroy();

      // destroy tugboat zone
      ZM_EraseZone(tugboatID);

      // destroy mapping
      TP_RemoveMapping(tugboatID);
    }

    // schedule a delayed shelter deletion
    // this should be used during normal operations, in order to apply delays
    //  and/or notifications per plugin configuration
    private void ScheduleDeleteTugboatData(NetworkableId tugboatID)
    {
      // abort if tugboat is unknown, or if deletion timer is already running
      if (!_tugboatData.ContainsKey(tugboatID) ||
          _tugboatDeleteTimers.ContainsKey(tugboatID))
      {
        return;
      }

      // schedule call
      _tugboatDeleteTimers.Add(tugboatID, timer.Once(
        _configData.deleteDelaySeconds, () => DeleteTugboatData(tugboatID)));

      // notify players in zone
      if (_configData.deleteNotify) NotifyZoneDelete(tugboatID);
    }

    // activate/restart PvP delay for given player and send notification
    // does nothing if no PvP delay configured
    // notifies if delay not already active
    // restarts delay if already active
    // if zoneID changes with an already-active delay, sends stop notification
    //  for old zoneID, plus start notification for new one
    private void PlayerBasePvpDelayStart(ulong playerID, string zoneID)
    {
      // abort if no delay configured
      if (_configData.pvpDelaySeconds <= 0.0f) return;

      if (_pvpDelayTimers.TryGetValue(playerID, out var delayData))
      {
        // delay already active - reset timer
        delayData.Item1.Reset(_configData.pvpDelaySeconds);

        // handle zone change
        if (zoneID != delayData.Item2)
        {
          // fire notification hook for old zone
          Interface.CallHook(
            "OnPlayerBasePvpDelayStop", playerID, delayData.Item2);

          // record new zone
          delayData.Item2 = zoneID;

          // fire notification hook for new zone
          Interface.CallHook("OnPlayerBasePvpDelayStart", playerID, zoneID);
        }

        return;
      }

      // no delay active - start+record one
      _pvpDelayTimers.Add(playerID, (timer.Once(
        _configData.pvpDelaySeconds, () => PlayerBasePvpDelayStop(
          playerID)), zoneID));

      // fire notification hook
      Interface.CallHook("OnPlayerBasePvpDelayStart", playerID, zoneID);
    }

    // stop/end PvP delay for given player and send notification
    private void PlayerBasePvpDelayStop(ulong playerID)
    {
      // abort if no delay record exists
      if (!_pvpDelayTimers.Remove(playerID, out var delayData)) return;

      // clean up timer
      delayData.Item1.Destroy();

      // fire notification hook
      Interface.CallHook("OnPlayerBasePvpDelayStop", playerID, delayData.Item2);
    }

    // purge any dangling zones + mappings from previous runs
    // this shouldn't be needed, but DynamicPVP does it, so I figured it
    //  wouldn't hurt
    private void PurgeOldZones()
    {
      var zoneIds = ZM_GetZoneIDs();
      if (null == zoneIds) return;

      uint zoneCount = 0;
      uint mappingCount = 0;

      foreach (var zoneId in zoneIds)
      {
        if (!zoneId.Contains(_zoneIdPrefix)) continue;
        // this is one of ours - delete it
        if (ZM_EraseZone(zoneId)) ++zoneCount;
        // ...and any corresponding mapping
        if (TP_RemoveMapping(zoneId)) ++mappingCount;
      }

      Puts($"OnServerInitialized():  Deleted {zoneCount} dangling zone(s)...");
      Puts($"OnServerInitialized():  Deleted {mappingCount} dangling mapping(s)...");
    }

    #endregion Core Methods

    #region Oxide/RustPlugin API/Hooks

    protected override void LoadDefaultMessages()
    {
      lang.RegisterMessages(new()
      {
        ["NotifyOwnerAbort"] =
          "{0}Player Base PvP Zone creation canceled",
        ["NotifyOwnerCreate"] =
          "{0}Player Base PvP Zone will be created here in {1} second(s)",
        ["NotifyZoneDelete"] =
          "{0}Player Base PvP Zone will be deleted here in {1} second(s)",
        ["MessageZoneEnter"] =
          "WARNING: Entering Player Base PVP Zone",
        ["MessageZoneExit"] =
          "Leaving Player Base PVP Zone",
      }, this);
    }

    private void Init()
    {
      if (null == _configData) return;
      BaseData.SphereDarkness = _configData.sphereDarkness;
    }

    private void OnServerInitialized()
    {
      Puts("OnServerInitialized(): Starting up...");

      // purge any dangling zones
      PurgeOldZones();

      // create zones immediately for all existing player-owned bases
      foreach (var (_, building) in BuildingManager.server.buildingDictionary)
      {
        var toolCupboard = GetToolCupboard(building);
#pragma warning disable CS8604 // Possible null reference argument.
        if (IsValid(toolCupboard) && IsPlayerOwned(toolCupboard))
        {
          CreateBuildingData(toolCupboard);
        }
#pragma warning restore CS8604 // Possible null reference argument.
      }
      Puts($"OnServerInitialized():  Created {_buildingData.Count} building zones...");

      // create zones immediately for all existing player-owned legacy shelters
      foreach (var (p, shelterList) in LegacyShelter.SheltersPerPlayer)
      {
        foreach (var shelter in shelterList)
        {
          if (IsValid(shelter) &&
              shelter.entityPrivilege.TryGet(true, out var legacyShelter) &&
              IsPlayerOwned(legacyShelter))
          {
            CreateShelterData(legacyShelter);
          }
        }
      }
      Puts($"OnServerInitialized():  Created {_shelterData.Count} shelter zones...");

      // create zones immediately for all existing tugboats
      foreach (var serverEntity in BaseNetworkable.serverEntities)
      {
        if (serverEntity is VehiclePrivilege tugboat && IsValid(tugboat))
        {
          CreateTugboatData(tugboat);
        }
      }
      Puts($"OnServerInitialized():  Created {_tugboatData.Count} tugboat zones...");

      Puts("OnServerInitialized(): ...Startup complete.");
    }

    private void DestroyBaseDataDictionary<T>(
      ref Dictionary<NetworkableId, T> dict, Action<NetworkableId> deleter)
    {
      var networkableIds = Pool.GetList<NetworkableId>();
      foreach (var (networkableId, _) in dict)
      {
        networkableIds.Add(networkableId);
      }
      foreach(var networkableId in networkableIds)
      {
        deleter(networkableId);
      }
      dict.Clear();
      Pool.FreeList(ref networkableIds);
    }

    private void DestroyTimerDictionary<T>(
      ref Dictionary<T, Timer> dict, string desc)
    {
      if (dict.Count <= 0) return;
      Puts($"Unload():  Destroying {dict.Count} {desc} timer(s)...");
      foreach (var (_, dTimer) in dict) dTimer?.Destroy();
    }

    private void Unload()
    {
      Puts("Unload(): Cleaning up...");
      if (_buildingData.Count > 0)
      {
        Puts($"Unload():  Destroying {_buildingData.Count} building zone records...");
        DestroyBaseDataDictionary(ref _buildingData, DeleteBuildingData);
      }
      DestroyTimerDictionary(ref _buildingCheckTimers, "building check");
      DestroyTimerDictionary(ref _buildingCreateTimers, "building creation");
      DestroyTimerDictionary(ref _buildingDeleteTimers, "building deletion");
      if (_playerZones.Count > 0)
      {
        Puts($"Unload():  Destroying {_playerZones.Count} player-in-zones records...");
        foreach (var playerZoneData in _playerZones)
        {
          var zones = playerZoneData.Value;
          if (null == zones) continue;
          Pool.Free(ref zones);
        }
        _playerZones.Clear();
      }
      if (_pvpDelayTimers.Count > 0)
      {
        Puts($"Unload():  Destroying {_pvpDelayTimers.Count} player PvP delay timers...");
        foreach (var (_, timerData) in _pvpDelayTimers) timerData.Item1.Destroy();
        _buildingDeleteTimers.Clear();
      }
      if (_shelterData.Count > 0)
      {
        Puts($"Unload():  Destroying {_shelterData.Count} shelter zone records...");
        DestroyBaseDataDictionary(ref _shelterData, DeleteShelterData);
      }
      DestroyTimerDictionary(ref _shelterCreateTimers, "shelter creation");
      DestroyTimerDictionary(ref _shelterDeleteTimers, "shelter deletion");
      if (_tugboatData.Count > 0)
      {
        Puts($"Unload():  Destroying {_tugboatData.Count} tugboat zone records...");
        DestroyBaseDataDictionary(ref _tugboatData, DeleteTugboatData);
      }
      DestroyTimerDictionary(ref _tugboatDeleteTimers, "tugboat deletion");

      Puts("Unload(): ...Cleanup complete.");
    }

    private void ClampToZero(ref float value, string name)
    {
      if (value >= 0) return;
      PrintWarning($"Illegal {name}={value}; clamping to zero");
      value = 0.0f;
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
          ClampToZero(ref _configData.createDelaySeconds, "createDelaySeconds");
          ClampToZero(ref _configData.deleteDelaySeconds, "deleteDelaySeconds");
          ClampToZero(ref _configData.pvpDelaySeconds, "pvpDelaySeconds");
          if (_configData.sphereDarkness > 10)
          {
            PrintWarning($"Illegal sphereDarkness={_configData.sphereDarkness} value; clamping to 10");
            _configData.sphereDarkness = 10;
          }
          if (string.IsNullOrEmpty(_configData.rulesetName))
          {
            PrintWarning($"Illegal truePveRuleset={_configData.rulesetName} value; resetting to \"exclude\"");
            _configData.rulesetName = "exclude";
          }
          ClampToZero(ref _configData.building.checkDelaySeconds, "building.checkDelaySeconds");
          ClampToZero(ref _configData.building.minimumBuildingRadius, "building.minimumBuildingRadius");
          ClampToZero(ref _configData.building.minimumBlockRadius, "building.minimumBlockRadius");
          ClampToZero(ref _configData.shelter.radius, "shelter.radius");
          ClampToZero(ref _configData.tugboat.radius, "tugboat.radius");
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
    }

    protected override void SaveConfig() => Config.WriteObject(_configData);

    // called when a tugboat reaches zero health and sinks
    // NOTE: this only fires for Tugboat and not VehiclePrivilege
    private void OnEntityDeath(Tugboat tugboat)
    {
      // cache tugboat ID
      var tugboatPrivilege = GetVehiclePrivilege(tugboat);
      if (null == tugboatPrivilege) return;
      var tugboatID = GetNetworkableID(tugboatPrivilege);

      // if we have a record on this tugboat, clear its entity reference ASAP to
      //  minimize side effects
      // NOTE: we can abort here if we have no record, because tugboats have no
      //  creation delay
      if (!_tugboatData.TryGetValue(tugboatID, out var tugboatData))
      {
        return;
      }
      // release entity reference immediately to minimize side effects
      tugboatData.ClearEntity();
      // also untether ZoneManager zone if present, so that it doesn't disappear
      UntetherZone(GetZoneID(tugboatID), tugboat);

      NextTick(() =>
      {
        // schedule deletion of the dropped base
        ScheduleDeleteTugboatData(tugboatID);
      });
    }

    // called when a building block is destroyed
    private void OnEntityKill(BuildingBlock buildingBlock)
    {
      // abort if this isn't a player-owned building block
      if (!IsPlayerOwned(buildingBlock)) return;

      // attempt to find an attached TC
      var toolCupboard = GetToolCupboard(buildingBlock);

      // abort if no TC found, since there's nothing we can do without one
      if (null == toolCupboard) return;

      // cache TC ID
      var toolCupboardID = GetNetworkableID(toolCupboard);

      NextTick(() =>
      {
        // schedule a check of the updated player base
        ScheduleCheckBuildingData(toolCupboardID);
      });
    }

    // called when a TC is destroyed
    private void OnEntityKill(BuildingPrivlidge toolCupboard)
    {
      // abort if this isn't a player-owned building TC
      if (!IsPlayerOwned(toolCupboard)) return;

      // cache TC ID, because it's going away
      var toolCupboardID = GetNetworkableID(toolCupboard);
      // owner player ID too
      var ownerID = toolCupboard.OwnerID;

      // if we have a record on this base, clear its TC reference ASAP to
      //  minimize side effects
      // NOTE: don't abort here if we have no record, as we need to also handle
      //  the case of a base that is pending creation
      if (_buildingData.TryGetValue(toolCupboardID, out var buildingData))
      {
        // release TC reference immediately to minimize side effects
        buildingData.ClearEntity();
      }

      NextTick(() =>
      {
        // schedule deletion of the dropped base
        ScheduleDeleteBuildingData(toolCupboardID, ownerID);
      });
    }

    // called when a legacy shelter is destroyed
    private void OnEntityKill(EntityPrivilege legacyShelter)
    {
      // abort if this isn't a player-owned shelter
      if (!IsPlayerOwned(legacyShelter)) return;

      // cache shelter ID, because it's going away
      var legacyShelterID = GetNetworkableID(legacyShelter);
      // owner player ID too
      var ownerID = GetOwnerID(legacyShelter);

      // if we have a record on this shelter, clear its entity reference ASAP to
      //  minimize side effects
      // NOTE: don't abort here if we have no record, as we need to also handle
      //  the case of a base that is pending creation
      if (_shelterData.TryGetValue(legacyShelterID, out var shelterData))
      {
        // release entity reference immediately to minimize side effects
        shelterData.ClearEntity();
      }

      NextTick(() =>
      {
        // schedule deletion of the dropped base
        ScheduleDeleteShelterData(legacyShelterID, ownerID);
      });
    }

    // called when a tugboat despawns
    // NOTE: using Tugboat instead of VehiclePrivilege since we've tethered a
    //  bunch of stuff to that
    private void OnEntityKill(Tugboat tugboat) => OnEntityDeath(tugboat);

    // called when a building block is spawned
    // this is preferred over OnEntityBuilt both because it's more
    //  straightforward, and because it catches things like CopyPaste spawning
    //  with player owner set
    private void OnEntitySpawned(BuildingBlock buildingBlock) =>
      OnEntityKill(buildingBlock);

    // called when a TC is spawned
    // this is preferred over OnEntityBuilt both because it's more
    //  straightforward, and because it catches things like CopyPaste spawning
    //  with player owner set
    private void OnEntitySpawned(BuildingPrivlidge toolCupboard)
    {
      // abort if this isn't a player-owned TC
      if (!IsPlayerOwned(toolCupboard)) return;

      NextTick(() =>
      {
        // schedule creation of new player base record + zone
        ScheduleCreateBuildingData(toolCupboard);
      });
    }

    // called when a legacy shelter is spawned
    // this is preferred over OnEntityBuilt both because it's more
    //  straightforward, and because it catches things like CopyPaste spawning
    //  with player owner set
    private void OnEntitySpawned(EntityPrivilege legacyShelter)
    {
      NextTick(() =>
      {
        // abort if this isn't a player-owned shelter
        // this needs to be checked inside of NextTick() because Raidable
        //  Shelters doesn't clear auth until after spawning
        if (!IsPlayerOwned(legacyShelter)) return;
        // schedule creation of new player base record + zone
        ScheduleCreateShelterData(legacyShelter);
      });
    }

    // called when a tugboat is spawned
    // this is preferred over OnEntityBuilt both because it's more
    //  straightforward, and because it catches things like CopyPaste spawning
    //  with player owner set
    private void OnEntitySpawned(VehiclePrivilege tugboat)
    {
      NextTick(() =>
      {
        // immediately create new player base record + zone
        // (tugboats have no creation delay because players can't normally
        //  (de)spawn them at will)
        CreateTugboatData(tugboat);
      });
    }

    // custom hook defined by this plugin to return originating zoneID if player
    //  has an active PvP delay, or an empty string if not
    private string OnPlayerBasePvpDelayQuery(ulong playerID) =>
      _pvpDelayTimers.TryGetValue(playerID, out var delayData) ?
        delayData.Item2 : string.Empty;

    #endregion Oxide/RustPlugin API/Hooks

    #region ZoneManager Integration

    #region ZoneManager Helpers

    // return NetworkableId from a given ZoneManager zoneID string, or null if
    //  not found
    private NetworkableId? GetNetworkableID(string zoneID) =>
      zoneID.StartsWith(_zoneIdPrefix) && ulong.TryParse(
        zoneID.Substring(_zoneIdPrefix.Length), out var value) ?
          new NetworkableId(value) : null;

    // synthesize ZoneManager zoneID string from networkableID
    private string GetZoneID(NetworkableId networkableID) =>
      _zoneIdPrefix + networkableID.ToString();

    // get options array for ZoneManager zone creation
    private string[] GetZoneOptions(string zoneName, float radius) =>
      _configData.zoneMessages ?
        new[]
        {
          "name", zoneName,
          "radius", radius.ToString(),
          "enter_message", lang.GetMessage("MessageZoneEnter", this),
          "leave_message", lang.GetMessage("MessageZoneExit", this)
        } :
        new[] { "radius", radius.ToString() };

    // tether a ZoneManager zone to a transform
    // credit: CatMeat & Arainrr for examples via DynamicPVP
    void TetherZone(string zoneID, Transform parentTransform)
    {
      var zoneTransform = ZM_GetZoneByID(zoneID)?.transform;
      if (null == zoneTransform) return;
      zoneTransform.SetParent(parentTransform);
      zoneTransform.rotation = parentTransform.rotation;
      zoneTransform.position = parentTransform.position;
    }

    // untether a ZoneManager zone from its parent transform (if any)
    void UntetherZone(string zoneID, BaseEntity parentEntity)
    {
      var zone = ZM_GetZoneByID(zoneID);
      var zoneTransform = zone?.transform;
      var parentTransform = parentEntity.transform ?? zoneTransform?.parent;
      var position = parentTransform?.position;
      // abort if we didn't find the zone, or it's not parented
      if (null == zone || null == zoneTransform ||
          null == zoneTransform.parent || null == position)
      {
        return;
      }
      zoneTransform.parent = null;
    }

    // create or update a ZoneManager zone
    private static bool ZM_CreateOrUpdateZone(
      string zoneID, string[] zoneOptions, Vector3 location) =>
        Convert.ToBoolean(Interface.CallHook(
          "CreateOrUpdateZone", zoneID, zoneOptions, location));

    // create or update a ZoneManager zone for given building record
    // optionally takes TC ID for performance reasons
    private bool ZM_CreateOrUpdateZone(
      BuildingData buildingData, NetworkableId? toolCupboardID = null)
    {
      if (null == toolCupboardID && null != buildingData.ToolCupboard)
      {
        toolCupboardID = GetNetworkableID(buildingData.ToolCupboard);
      }
      if (null == toolCupboardID) return false;
      return ZM_CreateOrUpdateZone(
        GetZoneID((NetworkableId)toolCupboardID),
        GetZoneOptions(_zoneIdPrefix + "building", buildingData.Radius),
        buildingData.Location);
    }

    // create or update a ZoneManager zone for given shelter record
    // optionally takes shelter ID for performance reasons
    private bool ZM_CreateOrUpdateZone(
      ShelterData shelterData, NetworkableId? legacyShelterID = null)
    {
      if (null == legacyShelterID && null != shelterData.LegacyShelter)
      {
        legacyShelterID = GetNetworkableID(shelterData.LegacyShelter);
      }
      if (null == legacyShelterID) return false;
      return ZM_CreateOrUpdateZone(
        GetZoneID((NetworkableId)legacyShelterID),
        GetZoneOptions(_zoneIdPrefix + "shelter", shelterData.Radius),
        shelterData.Location);
    }

    // create or update a ZoneManager zone for given tugboat record
    // optionally takes tugboat ID for performance reasons
    private bool ZM_CreateOrUpdateZone(
      TugboatData tugboatData, NetworkableId? tugboatID = null)
    {
      if (null == tugboatID && null != tugboatData.Tugboat)
      {
        tugboatID = GetNetworkableID(tugboatData.Tugboat);
      }
      if (null == tugboatID) return false;
      var zoneID = GetZoneID((NetworkableId)tugboatID);
      if (!ZM_CreateOrUpdateZone(
        zoneID,
        GetZoneOptions(_zoneIdPrefix + "tugboat", tugboatData.Radius),
        tugboatData.Location))
      {
        return false;
      }

      // tether ZoneManager zone to tugboat
      var tugboatTransform = tugboatData.Tugboat?.GetParentEntity()?.transform;
      if (null != tugboatTransform) TetherZone(zoneID, tugboatTransform);

      return true;
    }

    // delete a ZoneManager zone by Zone ID
    private static bool ZM_EraseZone(string zoneID) =>
      Convert.ToBoolean(Interface.CallHook("EraseZone", zoneID));

    // delete a ZoneManager zone by networkable ID
    private void ZM_EraseZone(NetworkableId networkableId) =>
      ZM_EraseZone(GetZoneID(networkableId));

    // get list of players in the given ZoneManager zone, if any
    private static List<BasePlayer>? ZM_GetPlayersInZone(string zoneID) =>
      (List<BasePlayer>?)Interface.CallHook("GetPlayersInZone", zoneID);

    // get ZoneManager's actual data object for a given zoneID
    // credit: CatMeat & Arainrr for examples via DynamicPVP
    private static ZoneManager.Zone? ZM_GetZoneByID(string zoneID) =>
      (ZoneManager.Zone?)Interface.CallHook("GetZoneByID", zoneID);

    private string[]? ZM_GetZoneIDs() =>
      (string[]?)Interface.CallHook("GetZoneIDs");

    #endregion ZoneManager Helpers

    #region ZoneManager Hooks

    // called when a player enters any ZoneManager zone
    private void OnEnterZone(string zoneID, BasePlayer player)
    {
      var playerID = player.userID.Get();

      // abort if not a real player
      if (!playerID.IsSteamId()) return;

      // abort if not one of ours
      var networkableId = GetNetworkableID(zoneID);
      if (null == networkableId ||
          !IsKnownPlayerBaseId((NetworkableId)networkableId))
      {
        return;
      }

      // record player in zone set
      if (!_playerZones.TryGetValue(playerID, out var zones))
      {
        // no zone record - create one
        zones = Pool.Get<HashSet<NetworkableId>>();
        _playerZones.Add(playerID, zones);
      }
      zones.Add((NetworkableId)networkableId);

      // cancel any active pvp delay
      PlayerBasePvpDelayStop(playerID);
    }

    // called when a player exits any ZoneManager zone
    private void OnExitZone(string zoneID, BasePlayer player)
    {
      var playerID = player.userID.Get();

      // abort if not a real player
      if (!playerID.IsSteamId()) return;

      // abort if not one of ours
      var networkableId = GetNetworkableID(zoneID);
      if (null == networkableId ||
          !IsKnownPlayerBaseId((NetworkableId)networkableId))
      {
        return;
      }

      // remove player from zone set
      var stillInZone = false;
      if (_playerZones.TryGetValue(playerID, out var zones) && null != zones)
      {
        zones.Remove((NetworkableId)networkableId);
        // player may still be in an overlapping base zone
        if (zones.Count > 0) stillInZone = true;
      }

      // abort if player is asleep or dead
      if (player.IsDead() || player.IsSleeping()) return;

      // (re)start pvp delay, but only if not still in a base zone
      if (!stillInZone) PlayerBasePvpDelayStart(playerID, zoneID);
    }

    #endregion ZoneManager Hooks

    #endregion ZoneManager Integration

    #region TruePVE Integration

    // helper method to determine whether a player ID has PvP status
    // returns true iff player is in a base zone or has PvP delay
    // assumes playerID is valid
    bool IsPvpPlayer(ulong playerID) =>
      _pvpDelayTimers.ContainsKey(playerID) ||
      (_playerZones.TryGetValue(playerID, out var zones) && zones?.Count > 0);

    // called when TruePVE wants to know if a player can take damage
    // returns true if both attacker and target are players with PvP status,
    //  else returns null
    private object CanEntityTakeDamage(BasePlayer entity, HitInfo hitInfo)
    {
      // return unknown if either attacker or target is null / not a player
#pragma warning disable CS8603 // Possible null reference return.
      if (null == entity || null == hitInfo.InitiatorPlayer) return null;
#pragma warning restore CS8603 // Possible null reference return.

      var attackerID = hitInfo.InitiatorPlayer.userID.Get();
      var targetID = entity.userID.Get();

      // return unknown if either player is not a real player
#pragma warning disable CS8603 // Possible null reference return.
      if (!attackerID.IsSteamId() || !targetID.IsSteamId()) return null;
#pragma warning restore CS8603 // Possible null reference return.

      // return true if both players are in some kind of PvP state, else null
#pragma warning disable CS8603 // Possible null reference return.
      return IsPvpPlayer(attackerID) && IsPvpPlayer(targetID) ? true : null;
#pragma warning restore CS8603 // Possible null reference return.
    }

    // map a ZoneManager zone ID to a TruePVE ruleset (i.e. marks it as PVP)
    private static bool TP_AddOrUpdateMapping(
      string zoneID, string ruleset) =>
      Convert.ToBoolean(Interface.CallHook(
        "AddOrUpdateMapping", zoneID, ruleset));

    // create mapping for networkable ID to configured ruleset
    private void TP_AddOrUpdateMapping(NetworkableId networkableID) =>
      TP_AddOrUpdateMapping(GetZoneID(networkableID), _configData.rulesetName);

    // delete TruePVE mapping for given ZoneManager zone ID
    private static bool TP_RemoveMapping(string zoneID) =>
      Convert.ToBoolean(Interface.CallHook("RemoveMapping", zoneID));

    private void TP_RemoveMapping(NetworkableId networkableID) =>
      TP_RemoveMapping(GetZoneID(networkableID));

    #endregion TruePVE Integration

    #region Internal Classes

    // internal classes

    // base class for tracking player base data
    private abstract class BaseData
    {
      public static uint SphereDarkness { get; set; } = 0;

      // center point of base
      public Vector3 Location { get; protected set; }

      // radius of base
      public float Radius { get; private set; }

      // spheres/domes associated with base
      protected List<SphereEntity> _sphereList;

      // constructor - requires a base center point, and then Update() can be
      //  used to move it and/or set a radius
      protected BaseData(Vector3 location, float radius = 1.0f)
      {
        Location = location;
        Radius = radius;
        // sphere darkness is accomplished by creating multiple sphers (seems
        //  silly but appears to perform okay)
        _sphereList = Pool.GetList<SphereEntity>();
        CreateSpheres();
      }

      public abstract void ClearEntity(bool destroying = false);

      protected void CreateSpheres()
      {
        if (null == _sphereList) return;
        for (var i = 0; i < SphereDarkness; ++i)
        {
          var sphere = GameManager.server.CreateEntity(
            "assets/prefabs/visualization/sphere.prefab") as SphereEntity;
          if (null == sphere) continue;
          sphere.enableSaving = false;
          sphere.Spawn();
          sphere.LerpRadiusTo(Radius * 2.0f, Radius / 2.0f);
          sphere.ServerPosition = Location;
          _sphereList.Add(sphere);
        }
      }

      protected void DestroySpheres()
      {
        if (null == _sphereList) return;
        foreach (var sphere in _sphereList)
        {
          if (!IsValid(sphere)) continue;
          sphere.KillMessage();
        }
        _sphereList.Clear();
      }

      // this must be called whenever an instance of this class gets tossed out
      virtual public void Destroy()
      {
        ClearEntity(true);
        DestroySpheres();
        Pool.FreeList(ref _sphereList);
      }

      // set/update base location and/or radius
      // either option can be ommitted; this is for efficiency sice it may need
      //  to iterate over a list of spheres
      virtual public void Update(Vector3? location = null, float? radius = null)
      {
        if (null == location && null == radius) return;

        if (null != location) Location = (Vector3)location;
        if (null != radius) Radius = (float)radius;

        foreach (var sphere in _sphereList)
        {
          if (!IsValid(sphere)) continue;
          if (null != location) sphere.ServerPosition = Location;
          if (null != radius) sphere.LerpRadiusTo(Radius * 2.0f, Radius / 2.0f);
        }
      }
    }

    // extension of BaseData to track tool cupboard based player buildings
    private sealed class BuildingData : BaseData
    {
      // reference to TC entity
      // if null, the base is pending deletion
      public BuildingPrivlidge? ToolCupboard { get; private set; }

      // constructor
      public BuildingData(
        BuildingPrivlidge toolCupboard, Vector3 location, float radius = 1.0f)
        : base(location, radius)
      {
        ToolCupboard = toolCupboard;
      }

      // clear base entity reference
      // should be called when the entity is killed
      public override void ClearEntity(bool destroying = false) =>
        ToolCupboard = null;
    }

    // extension of BaseData to track legacy shelters
    private sealed class ShelterData : BaseData
    {
      // reference to legacy shelter entity
      // if null, the base is pending deletion
      public EntityPrivilege? LegacyShelter { get; private set; }

      // constructor
      public ShelterData(
        EntityPrivilege legacyShelter, float radius = 1.0f)
        : base(legacyShelter.CenterPoint(), radius)
      {
        LegacyShelter = legacyShelter;
      }

      // clear base entity reference
      // should be called when the entity is killed
      public override void ClearEntity(bool destroying = false) =>
        LegacyShelter = null;
    }

    // extension of BaseData to track tugboats
    private sealed class TugboatData : BaseData
    {
      // reference to tugboat entity
      // if null, the base is pending deletion
      public VehiclePrivilege? Tugboat { get; private set; }

      // constructor - requires a base center point, and then Update() can be
      //  used to move it and/or set a radius
      public TugboatData(
        VehiclePrivilege tugboat, float radius = 1.0f,
        bool? forceNetworking = null, bool forceBuoyancy = false)
        : base(tugboat.GetParentEntity().CenterPoint(), radius)
      {
        Tugboat = tugboat;
        var tugboatParent = tugboat.GetParentEntity();
        if (null == tugboatParent) return;
        if (null != forceNetworking && SphereDarkness > 0)
        {
          // this keeps the spheres from disappearing by also keeping the
          //  tugboat from de-rendering (credit: bmgjet & Karuza)
          tugboatParent.EnableGlobalBroadcast((bool)forceNetworking);
          // ...and this keeps the tugboats from sinking and popping back up
          // (credit: bmgjet for idea + code)
          // NOTE: Ryan says this may not be needed anymore?
          if (forceBuoyancy)
          {
            var buoyancy = tugboatParent.GetComponent<Buoyancy>();
            if (buoyancy != null)
            {
              buoyancy.CancelInvoke("CheckSleepState");
              buoyancy.Invoke(new Action(buoyancy.Wake), 0f); //Force Awake
            }
          }
        }
        // parent spheres to the tugboat
        foreach (var sphere in _sphereList)
        {
          sphere.ServerPosition = Vector3.zero;
          sphere.SetParent(tugboatParent);
          // match networking with parent (avoids need to force global tugboats)
          // (credit: WhiteThunder)
          sphere.EnableGlobalBroadcast(tugboatParent.globalBroadcast);
        }
      }

      // clear base entity reference
      // must be called when the entity is killed
      public override void ClearEntity(bool destroying = false)
      {
        if (!destroying)
        {
          // the spheres will still get clobbered when the tugboat goes away, so
          //  just trash them now and make new ones
          var location = Tugboat?.GetParentEntity()?.CenterPoint();
          if (null != location) Location = (Vector3)location;
        }
        UntetherSpheres();

        Tugboat = null;
      }

      private void UntetherSpheres()
      {
        if (null == _sphereList) return;

        // un-tether any spheres from the tugboat
        foreach (var sphere in _sphereList)
        {
          sphere.SetParent(null, true, true);
          sphere.ServerPosition = Location;
          sphere.LerpRadiusTo(Radius * 2.0f, Radius / 2.0f);
        }
      }
    }

    private sealed class ConfigData
    {
      [JsonProperty(PropertyName = "Zone creation delay in seconds (excludes tugboat)")]
      public float createDelaySeconds = 60.0f;

      [JsonProperty(PropertyName = "Zone creation delay notifications (owner only, excludes tugboat)")]
      public bool createNotify = true;

      [JsonProperty(PropertyName = "Zone deletion delay in seconds")]
      public float deleteDelaySeconds = 300.0f;

      [JsonProperty(PropertyName = "Zone deletion delay notifications (all players in zone)")]
      public bool deleteNotify = true;

      [JsonProperty(PropertyName = "Zone creation/deletion notification prefix")]
      public string prefixNotify = "[PBPZ] ";

      [JsonProperty(PropertyName = "Zone exit PvP delay in seconds (0 for none)")]
      public float pvpDelaySeconds = 5.0f;

      [JsonProperty(PropertyName = "Zone sphere darkness (0 to disable, maximum 10)")]
      public uint sphereDarkness = 0;

      [JsonProperty(PropertyName = "Zone entry/exit ZoneManager messages")]
      public bool zoneMessages = true;

      [JsonProperty(PropertyName = "Zone TruePVE mappings ruleset name")]
      public string rulesetName = "exclude";

      [JsonProperty(PropertyName = "Building settings")]
      public BuildingConfigData building = new();

      [JsonProperty(PropertyName = "Shelter settings")]
      public ShelterConfigData shelter = new();

      [JsonProperty(PropertyName = "Tugboat settings")]
      public TugboatConfigData tugboat = new();
    }

    private sealed class BuildingConfigData
    {
      [JsonProperty(PropertyName = "Building update check delay in seconds")]
      public float checkDelaySeconds = 5.0f;

      [JsonProperty(PropertyName = "Building zone overall minimum radius")]
      public float minimumBuildingRadius = 16.0f;

      [JsonProperty(PropertyName = "Building zone per-block minimum radius")]
      public float minimumBlockRadius = 16.0f;
    }

    private sealed class ShelterConfigData
    {
      [JsonProperty(PropertyName = "Shelter zone radius")]
      public float radius = 8.0f;
    }

    private sealed class TugboatConfigData
    {
      [JsonProperty(PropertyName = "Tugboat force global rendering on/off when spheres enabled (null=skip)")]
      public bool? forceNetworking = null;

      [JsonProperty(PropertyName = "Tugboat force enable buoyancy when forcing global rendering")]
      public bool forceBuoyancy = false;

      [JsonProperty(PropertyName = "Tugboat zone radius")]
      public float radius = 32.0f;
    }
  }

  #endregion Internal Classes
}

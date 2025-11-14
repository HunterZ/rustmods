// Requires: ZoneManager

using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Player Base PvP Zones", "HunterZ", "1.4.0")]
[Description("Maintains Zone Manager / TruePVE exclusion zones around player bases")]
public class PlayerBasePvpZones : RustPlugin
{
  #region Fields

  [PluginReference] Plugin TruePVE;

  // permission for zone toggle command
  private const string PermissionToggle = "playerbasepvpzones.toggle";

  // tracks which players have zones enabled (stored by OwnerID)
  private HashSet<ulong> _playersWithZonesEnabled = new HashSet<ulong>();

  // tracks pending zone enable/disable requests by player ID
  private Dictionary<ulong, Timer> _toggleTimers = new Dictionary<ulong, Timer>();

  // data file name for persistence
  private const string DataFileName = "PlayerBasePvpZones_EnabledPlayers";

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
  // NOTE: to avoid heap churn, the HashSet's are pooled, and are only freed in
  //  Unload(). This means that empty sets may exist when a player is not in any
  //  base zones!
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

  // true if TruePVE 2.2.3+ ExcludePlayer() should be used for PVP delays, false
  //  if CanEntityTakeDamage() hook handler should be used
  private bool _useExcludePlayer;

  private Coroutine _createDataCoroutine;
  private readonly YieldInstruction _fastYield = null;
  private readonly YieldInstruction _throttleYield =
    CoroutineEx.waitForSeconds(0.1f);
  private float _targetFps = -1.0f;

  // ZoneManager zone ID prefix
  private const string ZoneIdPrefix = "PlayerBasePVP:";

  #endregion Fields

  #region Core Methods

  // Collect zone IDs for a player before deletion
  private List<string> CollectPlayerZoneIds(ulong playerID)
  {
    var zoneIds = Pool.Get<List<string>>();

    // Collect building zone IDs
    foreach (var (tcID, buildingData) in _buildingData)
    {
      if (buildingData.ToolCupboard &&
          buildingData.ToolCupboard.OwnerID == playerID)
      {
        zoneIds.Add(GetZoneID(tcID));
      }
    }

    // Collect shelter zone IDs
    foreach (var (shelterID, shelterData) in _shelterData)
    {
      if (shelterData.LegacyShelter &&
          GetOwnerID(shelterData.LegacyShelter) == playerID)
      {
        zoneIds.Add(GetZoneID(shelterID));
      }
    }

    // Collect tugboat zone IDs
    foreach (var (tugboatID, tugboatData) in _tugboatData)
    {
      if (tugboatData.Tugboat &&
          tugboatData.Tugboat.authorizedPlayers.Exists(
            x => x.userid == playerID))
      {
        zoneIds.Add(GetZoneID(tugboatID));
      }
    }

    return zoneIds;
  }

  // generate a current 3D bounding box around a base
  private Bounds CalculateBuildingBounds(BuildingPrivlidge toolCupboard)
  {
    // start with a box around the TC, whose space diagonal is the minimum
    //  total building radius
    var buildingBounds = new Bounds(
      toolCupboard.CenterPoint(),
      Vector3.one *
      _configData.Building.MinimumBuildingRadius / Mathf.Sqrt(3f));

    // get building, aborting if not found
    var building = toolCupboard.GetBuilding();
    if (null == building) return buildingBounds;

    // precalculate extents for a cube whose space diagonal is the radius
    var minimumBlockExtents =
      Vector3.one * _configData.Building.MinimumBlockRadius / Mathf.Sqrt(3f);
    // precalculate square of minimum block magnitude for comparison purposes
    var minimumBlockSqrMagnitude =
      _configData.Building.MinimumBlockRadius *
      _configData.Building.MinimumBlockRadius;

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
      if (entityBounds.extents.Max() < _configData.Building.MinimumBlockRadius
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
  private static bool CancelDictionaryTimer<T>(
    ref Dictionary<T, Timer> dictionary, T key)
  {
    if (!dictionary.Remove(key, out var cTimer)) return false;
    cTimer.Destroy();
    return true;
  }

  // BuildingBlock wrapper for GetToolCupboard()
  private static BuildingPrivlidge GetToolCupboard(
    BuildingBlock buildingBlock) =>
    GetToolCupboard(buildingBlock.GetBuilding());

  // try to find and return a physically-attached TC for the given building, or
  //  null if no suitable result found
  // only supports player-owned TCs (NOT to be confused with player-authed!)
  // this is our differentiator of whether a building should have a zone
  private static BuildingPrivlidge GetToolCupboard(
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
      if (!building.decayEntities.Contains(toolCupboard)) continue;
      // only one TC can be connected, so return it - or null if it's not
      //  player-owned
      return IsPlayerOwned(toolCupboard) ? toolCupboard : null;
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
    if (!legacyShelter) return null;
    // OwnerID is zero for shelters for some reason, so find the first
    //  authorized player (if any)
    // only one player can normally auth to a shelter, but we also want to
    //  account for the possibility of plugin-spawned shelters that are authed
    //  to non-player(s)
    foreach (var playerNameID in legacyShelter.authorizedPlayers)
    {
      if (playerNameID.IsSteamId()) return playerNameID;
    }
    return null;
  }

  private static VehiclePrivilege GetVehiclePrivilege(BaseVehicle vehicle)
  {
    foreach (var child in vehicle.children)
    {
      if (child is VehiclePrivilege privilege) return privilege;
    }
    return null;
  }

  // OwnerID is zero for shelters for some reason, so check auth list
  private static bool IsPlayerOwned(EntityPrivilege legacyShelter) =>
    null != GetOwnerID(legacyShelter);

  private static bool IsPlayerOwned(DecayEntity decayEntity) =>
    decayEntity && decayEntity.OwnerID.IsSteamId();

  private static bool IsValid(BaseNetworkable baseNetworkable) =>
    baseNetworkable &&
    !baseNetworkable.IsDestroyed &&
    null != baseNetworkable.net &&
    baseNetworkable.transform;

  private bool IsPlayerZonesEnabled(ulong playerID)
  {
    return _playersWithZonesEnabled.Contains(playerID);
  }

  private void NotifyOwnerAbort(ulong ownerID)
  {
    var player = BasePlayer.FindByID(ownerID);
    if (!player || !player.userID.IsSteamId()) return;
    var message = lang.GetMessage(
      "NotifyOwnerAbort", this, player.UserIDString);
    SendReply(player, string.Format(message, _configData.PrefixNotify));
  }

  private void NotifyOwnerCreate(ulong ownerID)
  {
    var player = BasePlayer.FindByID(ownerID);
    if (!player || !player.userID.IsSteamId()) return;
    var message = lang.GetMessage(
      "NotifyOwnerCreate", this, player.UserIDString);
    SendReply(player, string.Format(
      message, _configData.PrefixNotify, _configData.CreateDelaySeconds));
  }

  private void NotifyZoneDelete(NetworkableId baseID)
  {
    var playersInZone = Pool.Get<List<BasePlayer>>();
    ZM_GetPlayersInZone(GetZoneID(baseID), playersInZone);
    foreach (var player in playersInZone)
    {
      if (!player || !player.userID.IsSteamId()) continue;
      var message =
        lang.GetMessage("NotifyZoneDelete", this, player.UserIDString);
      SendReply(player, string.Format(
        message, _configData.PrefixNotify, _configData.DeleteDelaySeconds));
    }
    Pool.FreeUnmanaged(ref playersInZone);
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
        null == buildingData ||
        !IsValid(buildingData.ToolCupboard))
    {
      return;
    }

    // get cached TC
    var toolCupboard = buildingData.ToolCupboard;

    // get building record, aborting if not found
    var building = toolCupboard.GetBuilding();
    if (null == building) return;

    // get updated building footprint data
    var buildingBounds = CalculateBuildingBounds(toolCupboard);
    var radius = CalculateBuildingRadius(buildingBounds);

    // abort if footprint basically unchanged
    if (buildingData.Location == buildingBounds.center &&
        Mathf.Approximately(buildingData.Radius, radius))
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
    var buildingData = Pool.Get<BuildingData>();
    if (null == buildingData)
    {
      PrintError("CreateBuildingData(): Failed to allocate BuildingData from pool");
      return;
    }
    buildingData.Init(toolCupboard, buildingBounds.center, radius);
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
  private void DeleteBuildingData(
    NetworkableId toolCupboardID, List<string> bulkDeleteList = null)
  {
    // clean up any deletion timer that may have invoked this
    CancelDictionaryTimer(ref _buildingDeleteTimers, toolCupboardID);

    // remove building record to local variable, aborting if not found
    if (!_buildingData.Remove(toolCupboardID, out var buildingData)) return;

    // drop building record
    Pool.Free(ref buildingData);

    // destroy building zone
    if (null == bulkDeleteList)
    {
      // ...immediately
      TP_RemoveMapping(toolCupboardID);
      ZM_EraseZone(toolCupboardID);
    }
    else
    {
      // ...by adding to given bulk delete list
      bulkDeleteList.Add(GetZoneID(toolCupboardID));
    }
  }

  // schedule a delayed building update check
  // this should be used where practical to reduce recalculation of base
  //  footprint when lots of building blocks are being spawned/destroyed around
  //  the same time
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
      _configData.Building.CheckDelaySeconds,
      () => CheckBuildingData(toolCupboardID)));
  }

  // schedule a delayed building creation
  // this should be used during normal operations, in order to apply delays
  //  and/or notifications per plugin configuration
  private void ScheduleCreateBuildingData(BuildingPrivlidge toolCupboard)
  {
    // Check if this player has zones enabled
    if (!IsPlayerZonesEnabled(toolCupboard.OwnerID)) return;

    var toolCupboardID = GetNetworkableID(toolCupboard);

    // abort if building is already known, or if any timers are already running
    if (_buildingData.ContainsKey(toolCupboardID) ||
        _buildingCheckTimers.ContainsKey(toolCupboardID) ||
        _buildingCreateTimers.ContainsKey(toolCupboardID) ||
        _buildingDeleteTimers.ContainsKey(toolCupboardID))
    {
      return;
    }

    // schedule call
    _buildingCreateTimers.Add(toolCupboardID, timer.Once(
      _configData.CreateDelaySeconds, () => CreateBuildingData(toolCupboard)));

    // notify player
    if (_configData.CreateNotify) NotifyOwnerCreate(toolCupboard.OwnerID);
  }

  // schedule a delayed building deletion
  // this should be used during normal operations, in order to apply delays
  //  and/or notifications per plugin configuration
  private void ScheduleDeleteBuildingData(
    NetworkableId toolCupboardID, ulong ownerID)
  {
    var notifyZone = _configData.DeleteNotify;

    // notify owner if this was pending creation
    if (CancelDictionaryTimer(ref _buildingCreateTimers, toolCupboardID) &&
        _configData.CreateNotify)
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
      _configData.DeleteDelaySeconds,
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
    var shelterData = Pool.Get<ShelterData>();
    if (null == shelterData)
    {
      PrintError("CreateShelterData(): Failed to allocate ShelterData from pool");
      return;
    }
    shelterData.Init(legacyShelter, _configData.Shelter.Radius);
    _shelterData.Add(legacyShelterID, shelterData);

    // create zone
    if (ZM_CreateOrUpdateZone(shelterData, legacyShelterID))
    {
      // ...and mapping
      TP_AddOrUpdateMapping(legacyShelterID);
    }
  }

  // delete shelter record + zone for given LS ID
  private void DeleteShelterData(
    NetworkableId legacyShelterID, List<string> bulkDeleteList = null)
  {
    // clean up any deletion timer that may have invoked this
    CancelDictionaryTimer(ref _shelterDeleteTimers, legacyShelterID);

    // remove shelter record to local variable, aborting if not found
    if (!_shelterData.Remove(legacyShelterID, out var shelterData)) return;

    // drop shelter record
    Pool.Free(ref shelterData);

    // destroy shelter zone
    if (null == bulkDeleteList)
    {
      // ...immediately
      TP_RemoveMapping(legacyShelterID);
      ZM_EraseZone(legacyShelterID);
    }
    else
    {
      // ...by adding to given bulk delete list
      bulkDeleteList.Add(GetZoneID(legacyShelterID));
    }
  }

  // schedule a delayed shelter creation
  // this should be used during normal operations, in order to apply delays
  //  and/or notifications per plugin configuration
  private void ScheduleCreateShelterData(EntityPrivilege legacyShelter)
  {
    var ownerID = GetOwnerID(legacyShelter);
    if (null == ownerID) return;

    // Check if this player has zones enabled
    if (!IsPlayerZonesEnabled((ulong)ownerID)) return;

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
      _configData.CreateDelaySeconds,
      () => CreateShelterData(legacyShelter)));

    // notify players
    if (_configData.CreateNotify) NotifyOwnerCreate((ulong)ownerID);
  }

  // schedule a delayed shelter deletion
  // this should be used during normal operations, in order to apply delays
  //  and/or notifications per plugin configuration
  private void ScheduleDeleteShelterData(
    NetworkableId legacyShelterID, ulong? ownerID)
  {
    var notifyZone = _configData.DeleteNotify;

    // notify owner if this was pending creation
    if (CancelDictionaryTimer(ref _shelterCreateTimers, legacyShelterID) &&
        _configData.CreateNotify)
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
      _configData.DeleteDelaySeconds,
      () => DeleteShelterData(legacyShelterID)));

    // notify players in zone
    if (notifyZone) NotifyZoneDelete(legacyShelterID);
  }

  // create a new tugboat record + zone for given tugboat
  private void CreateTugboatData(VehiclePrivilege tugboat)
  {
    // Check if any authorized player has zones enabled
    bool hasEnabledPlayer = false;
    foreach (var auth in tugboat.authorizedPlayers)
    {
      if (IsPlayerZonesEnabled(auth.userid))
      {
        hasEnabledPlayer = true;
        break;
      }
    }
    if (!hasEnabledPlayer) return;

    // abort if tugboat object is destroyed
    if (!IsValid(tugboat)) return;

    var tugboatID = GetNetworkableID(tugboat);

    // if a tugboat already exists, then this call is probably redundant
    if (_tugboatData.ContainsKey(tugboatID)) return;

    // create + record new tugboat data record
    var tugboatData = Pool.Get<TugboatData>();
    if (null == tugboatData)
    {
      PrintError("CreateTugboatData(): Failed to allocate TugboatData from pool");
      return;
    }
    tugboatData.Init(
      tugboat, _configData.Tugboat.Radius,
      _configData.Tugboat.ForceNetworking, _configData.Tugboat.ForceBuoyancy);
    _tugboatData.Add(tugboatID, tugboatData);

    // create zone
    if (ZM_CreateOrUpdateZone(tugboatData, tugboatID))
    {
      // ...and mapping
      TP_AddOrUpdateMapping(tugboatID);
    }
  }

  // delete tugboat record + zone for given tugboat ID
  private void DeleteTugboatData(
    NetworkableId tugboatID, List<string> bulkDeleteList = null)
  {
    // clean up any deletion timer that may have invoked this
    CancelDictionaryTimer(ref _tugboatDeleteTimers, tugboatID);

    // remove tugboat record to local variable, aborting if not found
    if (!_tugboatData.Remove(tugboatID, out var tugboatData)) return;

    // drop tugboat record
    Pool.Free(ref tugboatData);

    // destroy tugboat zone
    if (null == bulkDeleteList)
    {
      // ...immediately
      TP_RemoveMapping(tugboatID);
      ZM_EraseZone(tugboatID);
    }
    else
    {
      // ...by adding to given bulk delete list
      bulkDeleteList.Add(GetZoneID(tugboatID));
    }
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
      _configData.DeleteDelaySeconds, () => DeleteTugboatData(tugboatID)));

    // notify players in zone
    if (_configData.DeleteNotify) NotifyZoneDelete(tugboatID);
  }

  // activate/restart PvP delay for given player and send notification
  // does nothing if no PvP delay configured
  // notifies if delay not already active
  // restarts delay if already active
  // sends stop notification for old zoneID, plus start notification for new
  //  one, if zoneID changes with an already-active delay
  private void PlayerBasePvpDelayStart(ulong playerID, string zoneID)
  {
    // abort if no delay configured
    if (_configData.PvpDelaySeconds <= 0.0f) return;

    if (_pvpDelayTimers.TryGetValue(playerID, out var delayData))
    {
      // delay already active - reset timer
      delayData.Item1.Reset(_configData.PvpDelaySeconds);

      // handle zone change
      if (zoneID != delayData.Item2)
      {
        // fire notification hook for old zone
        Interface.CallHook("OnPlayerBasePvpDelayStop",
          playerID, delayData.Item2);

        // record new zone
        delayData.Item2 = zoneID;
      }
    }
    else
    {
      // no delay active - start+record one
      _pvpDelayTimers.Add(playerID, (timer.Once(
        _configData.PvpDelaySeconds, () => PlayerBasePvpDelayStop(
          playerID)), zoneID));
    }

    // fire notification hook
    Interface.CallHook("OnPlayerBasePvpDelayStart", playerID, zoneID);

    // also notify TruePVE if we're doing that
    if (_useExcludePlayer)
    {
      Interface.CallHook("ExcludePlayer",
        playerID, _configData.PvpDelaySeconds, this);
    }
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

    // also notify TruePVE if we're doing that
    if (_useExcludePlayer)
    {
      Interface.CallHook("ExcludePlayer", playerID, 0.0f, this);
    }
  }

  // utility method to return an appropriate yield instruction based on whether
  //  this is a long pause for debug logging to catch up, whether current server
  //  framerate is too low, etc.
  private YieldInstruction DynamicYield()
  {
    // perform one-time caching of target FPS
    if (_targetFps <= 0) _targetFps = Mathf.Min(ConVar.FPS.limit, 30);

    return
      Performance.report.frameRate >= _targetFps ? _fastYield : _throttleYield;
  }

  // coroutine method to asynchronously create zones for all existing bases
  // coroutine method to asynchronously create zones for enabled players only
  private IEnumerator CreateData()
  {
    var startTime = DateTime.UtcNow;

    Puts("CreateData(): Starting zone creation...");

    // create zones for all existing player-owned bases
    // create zones for enabled players' bases
    foreach (var building in BuildingManager.server.buildingDictionary.Values)
    {
      var toolCupboard = GetToolCupboard(building);
      if (!IsValid(toolCupboard) || !IsPlayerOwned(toolCupboard)) continue;
      if (!IsPlayerZonesEnabled(toolCupboard.OwnerID)) continue;

      CreateBuildingData(toolCupboard);
      yield return DynamicYield();
    }
    Puts($"CreateData():  Created {_buildingData.Count} building zones...");

    // create zones for all existing player-owned legacy shelters
    // create zones for enabled players' shelters
    foreach (var (playerID, shelterList) in LegacyShelter.SheltersPerPlayer)
    {
      if (!IsPlayerZonesEnabled(playerID)) continue;

      foreach (var shelter in shelterList)
      {
        if (!IsValid(shelter) ||
            !shelter.entityPrivilege.TryGet(true, out var legacyShelter) ||
            !IsPlayerOwned(legacyShelter))
        {
          continue;
        }
        CreateShelterData(legacyShelter);
        yield return DynamicYield();
      }
    }
    Puts($"CreateData():  Created {_shelterData.Count} shelter zones...");

    // create zones for all existing tugboats
    // create zones for tugboats (check authorization)
    foreach (var serverEntity in BaseNetworkable.serverEntities)
    {
      if (serverEntity is not VehiclePrivilege tugboat || !IsValid(tugboat))
      {
        continue;
      }

      // Check if any authorized player has zones enabled
      bool hasEnabledPlayer = false;
      foreach (var auth in tugboat.authorizedPlayers)
      {
        if (IsPlayerZonesEnabled(auth.userid))
        {
          hasEnabledPlayer = true;
          break;
        }
      }
      if (!hasEnabledPlayer) continue;

      CreateTugboatData(tugboat);
      yield return DynamicYield();
    }
    Puts($"CreateData():  Created {_tugboatData.Count} tugboat zones...");

    Puts($"CreateData(): ...Startup completed in {(DateTime.UtcNow - startTime).TotalSeconds} seconds.");

    _createDataCoroutine = null;
  }

  // Method to create zones for a specific player
  private int CreatePlayerZones(ulong playerID)
  {
    Puts($"CreatePlayerZones(): Creating zones for player {playerID}...");
    var count = 0;

    // Create building zones
    foreach (var building in BuildingManager.server.buildingDictionary.Values)
    {
      var toolCupboard = GetToolCupboard(building);
      if (!IsValid(toolCupboard) || !IsPlayerOwned(toolCupboard)) continue;
      if (toolCupboard.OwnerID != playerID) continue;

      CreateBuildingData(toolCupboard);
      count++;
    }

    // Create shelter zones
    if (LegacyShelter.SheltersPerPlayer.TryGetValue(playerID, out var shelterList))
    {
      foreach (var shelter in shelterList)
      {
        if (!IsValid(shelter) ||
            !shelter.entityPrivilege.TryGet(true, out var legacyShelter) ||
            !IsPlayerOwned(legacyShelter))
        {
          continue;
        }
        CreateShelterData(legacyShelter);
        count++;
      }
    }

    // Create tugboat zones - tugboats don't have direct owner tracking
    // so we need to check each one
    foreach (var serverEntity in BaseNetworkable.serverEntities)
    {
      if (serverEntity is not VehiclePrivilege tugboat || !IsValid(tugboat))
      {
        continue;
      }

      // Check if this player is authorized on the tugboat
      if (!tugboat.authorizedPlayers.Exists(x => x.userid == playerID))
      {
        continue;
      }

      CreateTugboatData(tugboat);
      count++;
    }

    Puts($"CreatePlayerZones(): Created {count} zone(s) for player {playerID}");
    return count;
  }

  // Method to remove zones for a specific player
  private void RemovePlayerZones(ulong playerID)
  {
    Puts($"RemovePlayerZones(): Removing zones for player {playerID}...");
    var bulkDeleteList = Pool.Get<List<string>>();
    var count = 0;

    // Remove building zones
    var buildingIDsToRemove = Pool.Get<List<NetworkableId>>();
    foreach (var (tcID, buildingData) in _buildingData)
    {
      if (buildingData.ToolCupboard && buildingData.ToolCupboard.OwnerID == playerID)
      {
        buildingIDsToRemove.Add(tcID);
      }
    }
    foreach (var tcID in buildingIDsToRemove)
    {
      DeleteBuildingData(tcID, bulkDeleteList);
      count++;
    }
    Pool.FreeUnmanaged(ref buildingIDsToRemove);

    // Cancel any pending building timers
    CancelPlayerTimers(playerID, ref _buildingCheckTimers);
    CancelPlayerTimers(playerID, ref _buildingCreateTimers);
    CancelPlayerTimers(playerID, ref _buildingDeleteTimers);

    // Remove shelter zones
    var shelterIDsToRemove = Pool.Get<List<NetworkableId>>();
    foreach (var (shelterID, shelterData) in _shelterData)
    {
      if (shelterData.LegacyShelter)
      {
        var ownerID = GetOwnerID(shelterData.LegacyShelter);
        if (ownerID == playerID)
        {
          shelterIDsToRemove.Add(shelterID);
        }
      }
    }
    foreach (var shelterID in shelterIDsToRemove)
    {
      DeleteShelterData(shelterID, bulkDeleteList);
      count++;
    }
    Pool.FreeUnmanaged(ref shelterIDsToRemove);

    // Cancel any pending shelter timers
    CancelPlayerTimers(playerID, ref _shelterCreateTimers);
    CancelPlayerTimers(playerID, ref _shelterDeleteTimers);

    // Remove tugboat zones - check authorization
    var tugboatIDsToRemove = Pool.Get<List<NetworkableId>>();
    foreach (var (tugboatID, tugboatData) in _tugboatData)
    {
      if (tugboatData.Tugboat &&
          tugboatData.Tugboat.authorizedPlayers.Exists(x => x.userid == playerID))
      {
        tugboatIDsToRemove.Add(tugboatID);
      }
    }
    foreach (var tugboatID in tugboatIDsToRemove)
    {
      DeleteTugboatData(tugboatID, bulkDeleteList);
      count++;
    }
    Pool.FreeUnmanaged(ref tugboatIDsToRemove);

    // Cancel any pending tugboat timers
    CancelPlayerTimers(playerID, ref _tugboatDeleteTimers);

    // Bulk delete zones
    if (bulkDeleteList.Count > 0)
    {
      ZM_EraseZones(bulkDeleteList);
    }
    Pool.FreeUnmanaged(ref bulkDeleteList);

    Puts($"RemovePlayerZones(): Removed {count} zone(s) for player {playerID}");
  }

  // Helper method to cancel timers for a specific player
  private void CancelPlayerTimers<T>(ulong playerID, ref Dictionary<T, Timer> timerDict)
  {
    var toRemove = Pool.Get<List<T>>();

    foreach (var (key, timer) in timerDict)
    {
      // Check if this is a building/shelter/tugboat owned by the player
      if (key is NetworkableId netID)
      {
        bool shouldRemove = false;

        // Check building data
        if (_buildingData.TryGetValue(netID, out var buildingData))
        {
          if (buildingData.ToolCupboard && buildingData.ToolCupboard.OwnerID == playerID)
          {
            shouldRemove = true;
          }
        }

        // Check shelter data
        if (_shelterData.TryGetValue(netID, out var shelterData))
        {
          if (shelterData.LegacyShelter)
          {
            var ownerID = GetOwnerID(shelterData.LegacyShelter);
            if (ownerID == playerID)
            {
              shouldRemove = true;
            }
          }
        }

        // Check tugboat data
        if (_tugboatData.TryGetValue(netID, out var tugboatData))
        {
          if (tugboatData.Tugboat &&
              tugboatData.Tugboat.authorizedPlayers.Exists(x => x.userid == playerID))
          {
            shouldRemove = true;
          }
        }

        if (shouldRemove)
        {
          toRemove.Add(key);
        }
      }
    }

    foreach (var key in toRemove)
    {
      CancelDictionaryTimer(ref timerDict, key);
    }

    Pool.FreeUnmanaged(ref toRemove);
  }

  #endregion Core Methods

  #region Oxide/RustPlugin API/Hooks

  protected override void LoadDefaultMessages()
  {
    lang.RegisterMessages(new Dictionary<string, string>
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
      ["NoPermission"] =
        "You don't have permission to use this command.",
      ["ZonesEnabled"] =
        "[PBPZ] Your base PvP zones are now ENABLED.",
      ["ZonesDisabled"] =
        "[PBPZ] Your base PvP zones are now DISABLED.",
      ["ToggleAlreadyPending"] =
        "[PBPZ] You already have a zone toggle in progress. Please wait.",
      ["ToggleEnableStarted"] =
        "[PBPZ] Your base PvP zones will be ENABLED in {0} second(s).",
      ["ToggleDisableStarted"] =
        "[PBPZ] Your base PvP zones will be DISABLED in {0} second(s).",
      ["ToggleEnableBroadcast"] =
        "[PBPZ] {0} is enabling their base PvP zones in {1} second(s).",
      ["ToggleDisableBroadcast"] =
        "[PBPZ] {0} is disabling their base PvP zones in {1} second(s).",
      ["ToggleEnableComplete"] =
        "[PBPZ] {0}'s base PvP zones are now ACTIVE.",
      ["ToggleDisableComplete"] =
        "[PBPZ] {0}'s base PvP zones are now INACTIVE."
    }, this);
  }

  private void LoadData()
  {
    try
    {
      _playersWithZonesEnabled = Interface.Oxide.DataFileSystem.ReadObject<HashSet<ulong>>(DataFileName);
      if (_playersWithZonesEnabled == null)
      {
        _playersWithZonesEnabled = new HashSet<ulong>();
      }
      Puts($"Loaded {_playersWithZonesEnabled.Count} player(s) with zones enabled");
    }
    catch
    {
      _playersWithZonesEnabled = new HashSet<ulong>();
      Puts("Created new enabled players data file");
    }
  }

  private void SaveData()
  {
    Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _playersWithZonesEnabled);
  }

  private void Init()
  {
    // unsubscribe from OnEntitySpawned() hook calls under OnServerInitialized()
    // for some reason Carbon allows these to fire super early on startup for
    //  tugboats, which can cause a lot of problems
    // PBPZ is not dependent on OnEntitySpawned() for initial creation of
    //  tugboat zones, so this won't hurt anything
    Unsubscribe(nameof(OnEntitySpawned));
    if (null == _configData) return;
    BaseData.SphereDarkness = _configData.SphereDarkness;

    // Register permission
    permission.RegisterPermission(PermissionToggle, this);

    // Load enabled players data
    LoadData();
  }

  private void OnServerInitialized()
  {
    _useExcludePlayer =
      null != TruePVE && TruePVE.Version >= new VersionNumber(2, 2, 3);
    if (_useExcludePlayer)
    {
      Puts("OnServerInitialized(): TruePVE 2.2.3+ detected! TruePVE PVP delays will be used");
      Unsubscribe(nameof(CanEntityTakeDamage));
    }

    // resubscribe OnEntitySpawned() hook, as it's now safe to handle this
    Subscribe(nameof(OnEntitySpawned));

    // Create zones only for players who have them enabled
    NextTick(() =>
    {
      _createDataCoroutine = ServerMgr.Instance.StartCoroutine(CreateData());
    });
  }

  [ChatCommand("pbpz")]
  private void CommandTogglePvpZones(BasePlayer player, string command, string[] args)
  {
    // Check permission
    if (!permission.UserHasPermission(player.UserIDString, PermissionToggle))
    {
      SendReply(player, lang.GetMessage("NoPermission", this, player.UserIDString));
      return;
    }

    var playerID = player.userID.Get();

    // Check if player already has a pending toggle
    if (_toggleTimers.ContainsKey(playerID))
    {
      SendReply(player, lang.GetMessage("ToggleAlreadyPending", this, player.UserIDString));
      return;
    }

    // Determine if enabling or disabling
    bool isEnabling = !_playersWithZonesEnabled.Contains(playerID);
    float delay = isEnabling ? _configData.ToggleEnableDelaySeconds : _configData.ToggleDisableDelaySeconds;

    // Schedule the toggle
    _toggleTimers.Add(playerID, timer.Once(delay, () => ExecuteToggle(playerID, isEnabling)));

    // Notify the player
    if (isEnabling)
    {
      var message = lang.GetMessage("ToggleEnableStarted", this, player.UserIDString);
      SendReply(player, string.Format(message, delay));

      // Broadcast to server if configured
      if (_configData.ToggleEnableBroadcast)
      {
        var broadcastMsg = lang.GetMessage("ToggleEnableBroadcast", this);
        Server.Broadcast(string.Format(broadcastMsg, player.displayName, delay));
      }
    }
    else
    {
      var message = lang.GetMessage("ToggleDisableStarted", this, player.UserIDString);
      SendReply(player, string.Format(message, delay));

      // Broadcast to server if configured
      if (_configData.ToggleDisableBroadcast)
      {
        var broadcastMsg = lang.GetMessage("ToggleDisableBroadcast", this);
        Server.Broadcast(string.Format(broadcastMsg, player.displayName, delay));
      }
    }
  }

  private void ExecuteToggle(ulong playerID, bool isEnabling)
  {
    // Remove timer
    _toggleTimers.Remove(playerID);

    // Get player for notifications
    var player = BasePlayer.FindByID(playerID);
    var playerName = player?.displayName ?? "Unknown Player";

    if (isEnabling)
    {
      // Enable zones for this player
      _playersWithZonesEnabled.Add(playerID);
      SaveData();

      if (player != null)
      {
        SendReply(player, lang.GetMessage("ZonesEnabled", this, player.UserIDString));
      }

      // Broadcast completion if configured
      if (_configData.ToggleEnableCompleteBroadcast)
      {
        var broadcastMsg = lang.GetMessage("ToggleEnableComplete", this);
        Server.Broadcast(string.Format(broadcastMsg, playerName));
      }

      // Create zones for this player's bases
      var zoneCount = CreatePlayerZones(playerID);

      // Call hook to notify other plugins (like ZoneMarkerSync)
      Interface.CallHook("OnPlayerBasePvpZonesEnabled", playerID, zoneCount);
    }
    else
    {
      // Disable zones for this player
      var removedZoneIds = CollectPlayerZoneIds(playerID);

      _playersWithZonesEnabled.Remove(playerID);
      SaveData();

      if (player != null)
      {
        SendReply(player, lang.GetMessage("ZonesDisabled", this, player.UserIDString));
      }

      // Broadcast completion if configured
      if (_configData.ToggleDisableCompleteBroadcast)
      {
        var broadcastMsg = lang.GetMessage("ToggleDisableComplete", this);
        Server.Broadcast(string.Format(broadcastMsg, playerName));
      }

      // Remove all zones owned by this player
      RemovePlayerZones(playerID);
      // Call hook to notify other plugins (like ZoneMarkerSync)
      Interface.CallHook("OnPlayerBasePvpZonesDisabled", playerID, removedZoneIds);

      // Free the list
      Pool.FreeUnmanaged(ref removedZoneIds);
    }
  }

  private static void DestroyBaseDataDictionary<T>(
    ref Dictionary<NetworkableId, T> dict,
    Action<NetworkableId, List<string>> deleter,
    List<string> bulkDeleteList)
  {
    var networkableIds = Pool.Get<List<NetworkableId>>();
    foreach (var networkableId in dict.Keys)
    {
      networkableIds.Add(networkableId);
    }
    foreach(var networkableId in networkableIds)
    {
      deleter(networkableId, bulkDeleteList);
    }
    dict.Clear();
    Pool.FreeUnmanaged(ref networkableIds);
  }

  private void DestroyTimerDictionary<T>(
    ref Dictionary<T, Timer> dict, string desc)
  {
    if (dict.Count <= 0) return;
    Puts($"Unload():  Destroying {dict.Count} {desc} timer(s)...");
    foreach (var dTimer in dict.Values) dTimer?.Destroy();
  }

  private void Unload()
  {
    // Save enabled players data
    SaveData();

    if (null != _createDataCoroutine)
    {
      ServerMgr.Instance.StopCoroutine(_createDataCoroutine);
      _createDataCoroutine = null;
    }

    // Clean up toggle timers
    if (_toggleTimers.Count > 0)
    {
      Puts($"Unload():  Destroying {_toggleTimers.Count} toggle timer(s)...");
      foreach (var (_, toggleTimer) in _toggleTimers)
      {
        toggleTimer?.Destroy();
      }
      _toggleTimers.Clear();
    }

    Puts("Unload(): Cleaning up...");
    // cleanup base zones
    var bulkDeleteList = Pool.Get<List<string>>();
    if (_buildingData.Count > 0)
    {
      Puts($"Unload():  Destroying {_buildingData.Count} building zone records...");
      DestroyBaseDataDictionary(
        ref _buildingData, DeleteBuildingData, bulkDeleteList);
    }
    DestroyTimerDictionary(ref _buildingCheckTimers, "building check");
    DestroyTimerDictionary(ref _buildingCreateTimers, "building creation");
    DestroyTimerDictionary(ref _buildingDeleteTimers, "building deletion");
    if (_shelterData.Count > 0)
    {
      Puts($"Unload():  Destroying {_shelterData.Count} shelter zone records...");
      DestroyBaseDataDictionary(
        ref _shelterData, DeleteShelterData, bulkDeleteList);
    }
    DestroyTimerDictionary(ref _shelterCreateTimers, "shelter creation");
    DestroyTimerDictionary(ref _shelterDeleteTimers, "shelter deletion");
    if (_tugboatData.Count > 0)
    {
      Puts($"Unload():  Destroying {_tugboatData.Count} tugboat zone records...");
      DestroyBaseDataDictionary(
        ref _tugboatData, DeleteTugboatData, bulkDeleteList);
    }
    DestroyTimerDictionary(ref _tugboatDeleteTimers, "tugboat deletion");
    if (bulkDeleteList.Count > 0)
    {
      TP_RemoveMappings(bulkDeleteList);
      ZM_EraseZones(bulkDeleteList);
    }
    Pool.FreeUnmanaged(ref bulkDeleteList);
    // cleanup player records
    if (_playerZones.Count > 0)
    {
      Puts($"Unload():  Destroying {_playerZones.Count} player-in-zones records...");
      foreach (var playerZoneData in _playerZones)
      {
        var zones = playerZoneData.Value;
        if (null == zones) continue;
        Pool.FreeUnmanaged(ref zones);
      }
      _playerZones.Clear();
    }
    if (_pvpDelayTimers.Count > 0)
    {
      Puts($"Unload():  Destroying {_pvpDelayTimers.Count} player PvP delay timers...");
      foreach (var timerData in _pvpDelayTimers.Values)
      {
        timerData.Item1.Destroy();
      }
      _pvpDelayTimers.Clear();
    }
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
      if (null == _configData)
      {
        LoadDefaultConfig();
      }
      else
      {
        ClampToZero(ref _configData.CreateDelaySeconds, "createDelaySeconds");
        ClampToZero(ref _configData.DeleteDelaySeconds, "deleteDelaySeconds");
        ClampToZero(ref _configData.PvpDelaySeconds, "pvpDelaySeconds");
        ClampToZero(ref _configData.ToggleEnableDelaySeconds, "toggleEnableDelaySeconds");
        ClampToZero(ref _configData.ToggleDisableDelaySeconds, "toggleDisableDelaySeconds");
        if (_configData.SphereDarkness > 10)
        {
          PrintWarning($"Illegal sphereDarkness={_configData.SphereDarkness} value; clamping to 10");
          _configData.SphereDarkness = 10;
        }
        if (string.IsNullOrEmpty(_configData.RulesetName))
        {
          PrintWarning($"Illegal truePveRuleset={_configData.RulesetName} value; resetting to \"exclude\"");
          _configData.RulesetName = "exclude";
        }
        ClampToZero(ref _configData.Building.CheckDelaySeconds, "building.checkDelaySeconds");
        ClampToZero(ref _configData.Building.MinimumBuildingRadius, "building.minimumBuildingRadius");
        ClampToZero(ref _configData.Building.MinimumBlockRadius, "building.minimumBlockRadius");
        ClampToZero(ref _configData.Shelter.Radius, "shelter.radius");
        ClampToZero(ref _configData.Tugboat.Radius, "tugboat.radius");
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
    if (!tugboatPrivilege) return;
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

    // schedule deletion of the dropped base
    NextTick(() => ScheduleDeleteTugboatData(tugboatID));
  }

  // called when a building block is destroyed
  private void OnEntityKill(BuildingBlock buildingBlock)
  {
    // abort if this isn't a player-owned building block
    if (!IsPlayerOwned(buildingBlock)) return;

    // attempt to find an attached TC
    var toolCupboard = GetToolCupboard(buildingBlock);

    // abort if no TC found, since there's nothing we can do without one
    if (!toolCupboard) return;

    // cache TC ID
    var toolCupboardID = GetNetworkableID(toolCupboard);

    // schedule a check of the updated player base
    NextTick(() => ScheduleCheckBuildingData(toolCupboardID));
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

    // if we have a record on this base, clear its TC reference ASAP to minimize
    //  side effects
    // NOTE: don't abort here if we have no record, as we need to also handle
    //  the case of a base that is pending creation
    if (_buildingData.TryGetValue(toolCupboardID, out var buildingData))
    {
      // release TC reference immediately to minimize side effects
      buildingData.ClearEntity();
    }

    // schedule deletion of the dropped base
    NextTick(() => ScheduleDeleteBuildingData(toolCupboardID, ownerID));
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

    // schedule deletion of the dropped base
    NextTick(() => ScheduleDeleteShelterData(legacyShelterID, ownerID));
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

    // schedule creation of new player base record + zone
    NextTick(() => ScheduleCreateBuildingData(toolCupboard));
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
      // this needs to be checked inside of NextTick() because Raidable Shelters
      //  doesn't clear auth until after spawning
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
    // immediately create new player base record + zone
    // (tugboats have no creation delay because players can't normally (de)spawn
    //  them at will)
    NextTick(() => CreateTugboatData(tugboat));
  }

  // custom hook defined by this plugin to return originating zoneID if player
  //  has an active PvP delay, or an empty string if not
  private string OnPlayerBasePvpDelayQuery(ulong playerID) =>
    _pvpDelayTimers.TryGetValue(playerID, out var delayData) ?
      delayData.Item2 : string.Empty;

  private bool IsUsingExcludePlayer() => _useExcludePlayer;

  #endregion Oxide/RustPlugin API/Hooks

  #region ZoneManager Integration

  #region ZoneManager Helpers

  // return NetworkableId from a given ZoneManager zoneID string, or null if not
  //  found
  private static NetworkableId? GetNetworkableID(string zoneID) =>
    zoneID.StartsWith(ZoneIdPrefix) && ulong.TryParse(
      zoneID[ZoneIdPrefix.Length..], out var value) ?
      new NetworkableId(value) : null;

  // synthesize ZoneManager zoneID string from networkableID
  private static string GetZoneID(NetworkableId networkableID) =>
    ZoneIdPrefix + networkableID;

  // get options array for ZoneManager zone creation
  private string[] GetZoneOptions(string zoneName, float radius) =>
    _configData.ZoneMessages ?
      new[]
      {
        "name", zoneName,
        "radius", radius.ToString(CultureInfo.InvariantCulture),
        "enter_message", lang.GetMessage("MessageZoneEnter", this),
        "leave_message", lang.GetMessage("MessageZoneExit", this)
      } :
      new[] { "radius", radius.ToString(CultureInfo.InvariantCulture) };

  // tether a ZoneManager zone to a transform
  // credit: CatMeat & Arainrr for examples via DynamicPVP
  private static void TetherZone(string zoneID, Transform parentTransform)
  {
    var zoneTransform = ZM_GetZoneByID(zoneID)?.transform;
    if (!zoneTransform) return;
    zoneTransform.SetParent(parentTransform);
    zoneTransform.rotation = parentTransform.rotation;
    zoneTransform.position = parentTransform.position;
  }

  // untether a ZoneManager zone from its parent transform (if any)
  private static void UntetherZone(string zoneID, BaseEntity parentEntity) =>
    ZM_GetZoneByID(zoneID)?.transform.SetParent(null, true);

  // create or update a ZoneManager zone
  private bool ZM_CreateOrUpdateZone(
    string zoneID, string[] zoneOptions, Vector3 location) =>
    Convert.ToBoolean(Interface.CallHook("CreateOrUpdateTemporaryZone",
      this, zoneID, zoneOptions, location));

  // create or update a ZoneManager zone for given building record
  // optionally takes TC ID for performance reasons
  private bool ZM_CreateOrUpdateZone(
    BuildingData buildingData, NetworkableId? toolCupboardID = null)
  {
    if (null == toolCupboardID && buildingData.ToolCupboard)
    {
      toolCupboardID = GetNetworkableID(buildingData.ToolCupboard);
    }
    if (null == toolCupboardID) return false;
    return ZM_CreateOrUpdateZone(
      GetZoneID((NetworkableId)toolCupboardID),
      GetZoneOptions(ZoneIdPrefix + "building", buildingData.Radius),
      buildingData.Location);
  }

  // create or update a ZoneManager zone for given shelter record
  // optionally takes shelter ID for performance reasons
  private bool ZM_CreateOrUpdateZone(
    ShelterData shelterData, NetworkableId? legacyShelterID = null)
  {
    if (null == legacyShelterID && shelterData.LegacyShelter)
    {
      legacyShelterID = GetNetworkableID(shelterData.LegacyShelter);
    }
    if (null == legacyShelterID) return false;
    return ZM_CreateOrUpdateZone(
      GetZoneID((NetworkableId)legacyShelterID),
      GetZoneOptions(ZoneIdPrefix + "shelter", shelterData.Radius),
      shelterData.Location);
  }

  // create or update a ZoneManager zone for given tugboat record
  // optionally takes tugboat ID for performance reasons
  private bool ZM_CreateOrUpdateZone(
    TugboatData tugboatData, NetworkableId? tugboatID = null)
  {
    if (null == tugboatID && tugboatData.Tugboat)
    {
      tugboatID = GetNetworkableID(tugboatData.Tugboat);
    }
    if (null == tugboatID) return false;
    var zoneID = GetZoneID((NetworkableId)tugboatID);
    if (!ZM_CreateOrUpdateZone(
          zoneID,
          GetZoneOptions(ZoneIdPrefix + "tugboat", tugboatData.Radius),
          tugboatData.Location))
    {
      return false;
    }

    // tether ZoneManager zone to tugboat
    var tugboatTransform = tugboatData.Tugboat?.GetParentEntity()?.transform;
    if (tugboatTransform) TetherZone(zoneID, tugboatTransform);

    return true;
  }

  // delete a ZoneManager zone by Zone ID
  private bool ZM_EraseZone(string zoneID) =>
    Convert.ToBoolean(Interface.CallHook("EraseTemporaryZone", this, zoneID));

  // delete a batch of ZoneManager zones by Zone ID
  private void ZM_EraseZones(
    List<string> zoneIDs, List<bool> results = null) =>
    Interface.CallHook("EraseTemporaryZones", this, zoneIDs, results);

  // delete a ZoneManager zone by networkable ID
  private void ZM_EraseZone(NetworkableId networkableId) =>
    ZM_EraseZone(GetZoneID(networkableId));

  // get list of players in the given ZoneManager zone, if any
  private static void ZM_GetPlayersInZone(
    string zoneID, List<BasePlayer> list) =>
    Interface.CallHook("GetPlayersInZoneNoAlloc", zoneID, list);

  // get ZoneManager's actual data object for a given zoneID
  // credit: CatMeat & Arainrr for examples via DynamicPVP
  private static ZoneManager.Zone ZM_GetZoneByID(string zoneID) =>
    Interface.CallHook("GetZoneByID", zoneID) as ZoneManager.Zone;

  // get the list of all active ZoneManager zone IDs
  private static void ZM_GetZoneIDs(List<string> list) =>
    Interface.CallHook("GetZoneIDSNoAlloc", list);

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
  private bool IsPvpPlayer(ulong playerID) =>
    _pvpDelayTimers.ContainsKey(playerID) ||
    (_playerZones.TryGetValue(playerID, out var zones) && zones?.Count > 0);

  // called when TruePVE wants to know if a player can take damage
  // returns true if both attacker and target are players with PvP status,
  //  else returns null
  private object CanEntityTakeDamage(BasePlayer entity, HitInfo hitInfo)
  {
    // pathological: abort if we're using TruePVE's PVP delay API
    if (_useExcludePlayer)
    {
      Unsubscribe(nameof(CanEntityTakeDamage));
      return null;
    }

    // return unknown if either attacker or target is null / not a player
    if (!entity || !hitInfo.InitiatorPlayer) return null;

    var attackerID = hitInfo.InitiatorPlayer.userID.Get();
    var targetID = entity.userID.Get();

    // return unknown if either player is not a real player
    if (!attackerID.IsSteamId() || !targetID.IsSteamId()) return null;

    // return true if both players are in some kind of PvP state, else null
    return IsPvpPlayer(attackerID) && IsPvpPlayer(targetID) ? true : null;
  }

  // map a ZoneManager zone ID to a TruePVE ruleset (i.e. marks it as PVP)
  private static bool TP_AddOrUpdateMapping(string zoneID, string ruleset) =>
    Convert.ToBoolean(
      Interface.CallHook("AddOrUpdateMapping", zoneID, ruleset));

  // create mapping for networkable ID to configured ruleset
  private void TP_AddOrUpdateMapping(NetworkableId networkableID) =>
    TP_AddOrUpdateMapping(GetZoneID(networkableID), _configData.RulesetName);

  // delete TruePVE mapping for given ZoneManager zone ID
  private static bool TP_RemoveMapping(string zoneID) =>
    Convert.ToBoolean(Interface.CallHook("RemoveMapping", zoneID));

  private static void TP_RemoveMapping(NetworkableId networkableID) =>
    TP_RemoveMapping(GetZoneID(networkableID));

  // bulk delete TruePVE mappings for the given list of ZoneManager zone IDs
  private static void TP_RemoveMappings(List<string> zoneIDs) =>
    Interface.CallHook("RemoveMappings", zoneIDs);

  #endregion TruePVE Integration

  #region Internal Classes

  // internal classes

  // base class for tracking player base data
  private abstract class BaseData : Pool.IPooled
  {
    public static uint SphereDarkness { get; set; }

    // center point of base
    public Vector3 Location { get; protected set; } = Vector3.zero;

    // radius of base
    public float Radius { get; private set; }

    // spheres/domes associated with base
    protected List<SphereEntity> SphereList;

    protected void Init(Vector3 location, float radius = 1.0f)
    {
      Location = location;
      Radius = radius;
      // sphere darkness is accomplished by creating multiple spheres (seems
      //  silly but appears to perform okay)
      CreateSpheres();
    }

    public abstract void ClearEntity(bool destroying = false);

    private void CreateSpheres()
    {
      if (null == SphereList) return;
      for (var i = 0; i < SphereDarkness; ++i)
      {
        var sphere = GameManager.server.CreateEntity(
          "assets/prefabs/visualization/sphere.prefab") as SphereEntity;
        if (!sphere) continue;
        sphere.enableSaving = false;
        sphere.Spawn();
        sphere.LerpRadiusTo(Radius * 2.0f, Radius / 2.0f);
        sphere.ServerPosition = Location;
        SphereList.Add(sphere);
      }
    }

    private void DestroySpheres()
    {
      if (null == SphereList) return;
      foreach (var sphere in SphereList)
      {
        if (!IsValid(sphere)) continue;
        sphere.KillMessage();
      }
      SphereList.Clear();
    }

    // called automatically when Pool.Free() is called
    // needs to clean up instances for reuse
    public void EnterPool()
    {
      ClearEntity(true);
      DestroySpheres();
      if (null != SphereList)
      {
        Pool.FreeUnmanaged(ref SphereList);
        SphereList = null;
      }
      Location = Vector3.zero;
      Radius = 0.0f;
    }

    // called automatically when Pool.Get() is called
    // needs to initialize instances for use
    public void LeavePool()
    {
      if (null == SphereList && SphereDarkness > 0)
      {
        SphereList = Pool.Get<List<SphereEntity>>();
      }
    }

    // set/update base location and/or radius
    // either option can be omitted; this is for efficiency since it may need to
    //  iterate over a list of spheres
    public void Update(Vector3? location = null, float? radius = null)
    {
      if (null == location && null == radius) return;

      if (null != location) Location = (Vector3)location;
      if (null != radius) Radius = (float)radius;

      if (null == SphereList) return;
      foreach (var sphere in SphereList)
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
    public BuildingPrivlidge ToolCupboard { get; private set; }

    public void Init(
      BuildingPrivlidge toolCupboard, Vector3 location, float radius = 1.0f)
    {
      Init(location, radius);
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
    public EntityPrivilege LegacyShelter { get; private set; }

    public void Init(
      EntityPrivilege legacyShelter, float radius = 1.0f)
    {
      Init(legacyShelter.CenterPoint(), radius);
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
    public VehiclePrivilege Tugboat { get; private set; }

    public void Init(
      VehiclePrivilege tugboat, float radius = 1.0f,
      bool? forceNetworking = null, bool forceBuoyancy = false)
    {
      Init(tugboat.GetParentEntity().CenterPoint(), radius);
      Tugboat = tugboat;
      var tugboatParent = tugboat.GetParentEntity();
      if (!tugboatParent) return;
      if (null != forceNetworking && SphereDarkness > 0)
      {
        // this keeps the spheres from disappearing by also keeping the
        //  tugboat from de-rendering (credit: bmgjet & Karuza)
        tugboatParent.EnableGlobalBroadcast((bool)forceNetworking);
        // ...and this keeps the tugboats from sinking and popping back up
        // (credit: bmgjet for idea + code)
        // NOTE: Ryan says this may not be needed anymore?
        if (forceBuoyancy &&
            tugboatParent.TryGetComponent<Buoyancy>(out var buoyancy) &&
            buoyancy)
        {
          buoyancy.CancelInvoke(buoyancy.CheckSleepState);
          buoyancy.Invoke(buoyancy.Wake, 0f); //Force Awake
        }
      }
      // parent spheres to the tugboat
      if (null == SphereList) return;
      foreach (var sphere in SphereList)
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

      Tugboat = null;

      if (null == SphereList) return;
      // un-tether any spheres from the tugboat
      foreach (var sphere in SphereList)
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
    public float CreateDelaySeconds = 60.0f;

    [JsonProperty(PropertyName = "Zone creation delay notifications (owner only, excludes tugboat)")]
    public bool CreateNotify = true;

    [JsonProperty(PropertyName = "Zone deletion delay in seconds")]
    public float DeleteDelaySeconds = 300.0f;

    [JsonProperty(PropertyName = "Zone deletion delay notifications (all players in zone)")]
    public bool DeleteNotify = true;

    [JsonProperty(PropertyName = "Zone creation/deletion notification prefix")]
    public string PrefixNotify = "[PBPZ] ";

    [JsonProperty(PropertyName = "Zone exit PvP delay in seconds (0 for none)")]
    public float PvpDelaySeconds = 5.0f;

    [JsonProperty(PropertyName = "Zone sphere darkness (0 to disable, maximum 10)")]
    public uint SphereDarkness;

    [JsonProperty(PropertyName = "Zone entry/exit ZoneManager messages")]
    public bool ZoneMessages = true;

    [JsonProperty(PropertyName = "Zone TruePVE mappings ruleset name")]
    public string RulesetName = "exclude";

    [JsonProperty(PropertyName = "Toggle enable delay in seconds")]
    public float ToggleEnableDelaySeconds = 300.0f;

    [JsonProperty(PropertyName = "Toggle disable delay in seconds")]
    public float ToggleDisableDelaySeconds = 60.0f;

    [JsonProperty(PropertyName = "Broadcast when player starts enabling zones")]
    public bool ToggleEnableBroadcast = true;

    [JsonProperty(PropertyName = "Broadcast when player starts disabling zones")]
    public bool ToggleDisableBroadcast = true;

    [JsonProperty(PropertyName = "Broadcast when player zones are enabled (complete)")]
    public bool ToggleEnableCompleteBroadcast = true;

    [JsonProperty(PropertyName = "Broadcast when player zones are disabled (complete)")]
    public bool ToggleDisableCompleteBroadcast = true;

    [JsonProperty(PropertyName = "Building settings")]
    public BuildingConfigData Building = new();

    [JsonProperty(PropertyName = "Shelter settings")]
    public ShelterConfigData Shelter = new();

    [JsonProperty(PropertyName = "Tugboat settings")]
    public TugboatConfigData Tugboat = new();
  }

  private sealed class BuildingConfigData
  {
    [JsonProperty(PropertyName = "Building update check delay in seconds")]
    public float CheckDelaySeconds = 5.0f;

    [JsonProperty(PropertyName = "Building zone overall minimum radius")]
    public float MinimumBuildingRadius = 16.0f;

    [JsonProperty(PropertyName = "Building zone per-block minimum radius")]
    public float MinimumBlockRadius = 16.0f;
  }

  private sealed class ShelterConfigData
  {
    [JsonProperty(PropertyName = "Shelter zone radius")]
    public float Radius = 8.0f;
  }

  private sealed class TugboatConfigData
  {
    [JsonProperty(PropertyName = "Tugboat force global rendering on/off when spheres enabled (null=skip)")]
    public bool? ForceNetworking;

    [JsonProperty(PropertyName = "Tugboat force enable buoyancy when forcing global rendering")]
    public bool ForceBuoyancy;

    [JsonProperty(PropertyName = "Tugboat zone radius")]
    public float Radius = 32.0f;
  }

  #endregion Internal Classes
}

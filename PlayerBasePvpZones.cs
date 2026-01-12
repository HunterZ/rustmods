// Requires: ZoneManager

using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Player Base PvP Zones", "HunterZ", "1.4.0")]
[Description("Maintains Zone Manager / TruePVE exclusion zones around player bases")]
public class PlayerBasePvpZones : RustPlugin
{
  #region Fields

  [PluginReference] Plugin TruePVE;

  // user-defined plugin config data
  private ConfigData _configData = new();

  // persistent data
  private PersistData _persistData = new();

  // _persistData disk write delay timer
  private Timer _saveDataTimer;

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

  // zone toggle data
  //  layer mask for tool cupboard raycast searches
  private readonly int _buildingLayerMask = LayerMask.GetMask("Deployed");
  //  layer mask for legacy shelter raycast searches
  private readonly int _shelterLayerMask =
    LayerMask.GetMask("Deployed", "Construction");
  //  maximum distance from tool cupboard at which toggling allowed; this is
  //   currently meant to roughly match regular interaction distance
  private const float BuildingToggleRadius = 3.0f;
  //  shelters use player being in building privilege as a requirement
  //  tugboats use player being mounted at the wheel as a requirement, in lieu
  //   of raycast/distance checks

  private const string CommandRoot = "pbpz";
  private const string CommandHelp = "help";
  private const string CommandToggle = "toggle";
  private const string TogglePermission = "toggleZones";

  #endregion Fields

  #region Core Methods

  private string GetPermission(string suffix) => $"{Name}.{TogglePermission}";

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
    ref Dictionary<T, Timer> dictionary, T key) =>
      dictionary.Remove(key, out var cTimer) && DestroyTimer(cTimer);

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
    BaseNetworkable baseNetworkable) => baseNetworkable.net.ID;

  // construct NetworkableId from the given unsigned long integer value
  private static NetworkableId GetNetworkableID(ulong value) => new(value);

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

  // get building privilege for a given legacy shelter
  private static EntityPrivilege GetShelterPrivilege(BaseEntity entity) =>
    entity is LegacyShelter shelter && IsValid(shelter) ?
      shelter.GetEntityBuildingPrivilege() : null;

  // get building privilege for a given vehicle (tugboat)
  private static VehiclePrivilege GetVehiclePrivilege(BaseVehicle vehicle)
  {
    if (!IsValid(vehicle)) return null;
    foreach (var child in vehicle.children)
    {
      if (child is VehiclePrivilege privilege) return privilege;
    }
    return null;
  }

  // OwnerID is zero for shelters for some reason, so check auth list instead
  private static bool IsPlayerOwned(EntityPrivilege legacyShelter) =>
    IsValid(legacyShelter) && null != GetOwnerID(legacyShelter);

  private static bool IsPlayerOwned(DecayEntity decayEntity) =>
    IsValid(decayEntity) && decayEntity.OwnerID.IsSteamId();

  private static bool IsValid(BaseNetworkable baseNetworkable) =>
    baseNetworkable &&
    !baseNetworkable.IsDestroyed &&
    null != baseNetworkable.net &&
    baseNetworkable.transform;

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

    // abort if zone toggled off
    if (!GetToggleStates(_persistData.BuildingToggleData, toolCupboardID).Item1)
    {
      return;
    }

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

    // abort if zone toggled off
    if (!GetToggleStates(_persistData.ShelterToggleData, legacyShelterID).Item1)
    {
      return;
    }

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
    if (!_configData.CreateNotify) return;
    var ownerID = GetOwnerID(legacyShelter);
    if (null != ownerID) NotifyOwnerCreate((ulong)ownerID);
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
    // abort if tugboat object is destroyed
    if (!IsValid(tugboat)) return;

    var tugboatID = GetNetworkableID(tugboat);

    // if a tugboat already exists, then this call is probably redundant
    if (_tugboatData.ContainsKey(tugboatID)) return;

    // abort if zone toggled off
    if (!GetToggleStates(_persistData.TugboatToggleData, tugboatID).Item1)
    {
      return;
    }

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
    DestroyTimer(delayData.Item1);

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
  private IEnumerator CreateData()
  {
    var startTime = DateTime.UtcNow;

    Puts("CreateData(): Starting zone creation...");

    // create zones for all existing player-owned bases
    foreach (var building in BuildingManager.server.buildingDictionary.Values)
    {
      var toolCupboard = GetToolCupboard(building);
      if (!IsValid(toolCupboard) || !IsPlayerOwned(toolCupboard)) continue;
      CreateBuildingData(toolCupboard);
      yield return DynamicYield();
    }
    Puts($"CreateData():  Created {_buildingData.Count} building zones...");

    // create zones for all existing player-owned legacy shelters
    foreach (var shelterList in LegacyShelter.SheltersPerPlayer.Values)
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
        yield return DynamicYield();
      }
    }
    Puts($"CreateData():  Created {_shelterData.Count} shelter zones...");

    // create zones for all existing tugboats
    foreach (var serverEntity in BaseNetworkable.serverEntities)
    {
      if (serverEntity is not VehiclePrivilege tugboat || !IsValid(tugboat))
      {
        continue;
      }
      CreateTugboatData(tugboat);
      yield return DynamicYield();
    }
    Puts($"CreateData():  Created {_tugboatData.Count} tugboat zones...");

    Puts($"CreateData(): ...Startup completed in {(DateTime.UtcNow - startTime).TotalSeconds} seconds.");

    _createDataCoroutine = null;
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
      ["NotifyLockoutDamage"] =
        "{0}Player Base PvP Zone cannot be toggled for another {1} second(s) due to recent damage",
      ["NotifyLockoutToggle"] =
        "{0}Player Base PvP Zone cannot be toggled for another {1} second(s) due to recent toggle",
      ["NotifyCancelCreate"] =
        "{0}Player Base PvP Zone creation canceled due to toggle command",
      ["NotifyCancelDelete"] =
        "{0}Player Base PvP Zone deletion canceled due to toggle command",
      ["NotifyToggleDelete"] =
        "{0}Player Base PvP Zone deletion triggered due to toggle command",
      ["NotifyToggleCreate"] =
        "{0}Player Base PvP Zone creation triggered due to toggle command",
      ["NotifyToggleFail"] =
        "{0}Player Base PvP Zone not found",
      ["NotifyNoParams"] =
        "Valid parameter(s) required; type the following chat command for help: /{0} {1}",
      ["NotifyNoPerms"] =
        "You do not have permission to run any PBPZ commands",
      ["NotifyCommandToggle"] =
        "/{0} {1} - toggle Player Base PvP Zone for authorized nearby shelter, looked-at TC, or mounted tugboat"
    }, this);
  }

  private void Init()
  {
    LoadData();

    // unsubscribe from OnEntitySpawned() hook calls under OnServerInitialized()
    // PBPZ is not dependent on OnEntitySpawned() during server startup, because
    //  it scans all entities instead
    Unsubscribe(nameof(OnEntitySpawned));
    // also initially unsubscribe from damage hooks, because they're expensive
    Unsubscribe(nameof(OnEntityTakeDamage));

    AddCovalenceCommand(CommandRoot, nameof(HandleCommand));

    permission.RegisterPermission(GetPermission(TogglePermission), this);

    if (null == _configData) return;
    BaseData.SphereDarkness = _configData.SphereDarkness;
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

    // subscribe OnEntityTakeDamage() if toggle lockout on damage is requested
    //  via config file
    if (_configData.Toggle.DamageLockoutSeconds > 0.0f)
    {
      Subscribe(nameof(OnEntityTakeDamage));
    }

    // clean up toggle data dictionaries, and save data file if anything changed
    // NOTE: this mainly handles the case that a wipe happened between sessions
    var saveData = false;
    if (CleanupToggleDict(_persistData.BuildingToggleData) is
        var defunctBuildings and > 0)
    {
      Puts($"Removed {defunctBuildings} defunct building zone toggle data entries from data file");
      saveData = true;
    }
    if (CleanupToggleDict(_persistData.ShelterToggleData) is
        var defunctShelters and > 0)
    {
      Puts($"Removed {defunctShelters} defunct shelter zone toggle data entries from data file");
      saveData = true;
    }
    if (CleanupToggleDict(_persistData.TugboatToggleData) is
        var defunctTugboats and > 0)
    {
      Puts($"Removed {defunctTugboats} defunct tugboat zone toggle data entries from data file");
      saveData = true;
    }
    if (saveData)
    {
      SaveData();
    }

    NextTick(() =>
    {
      _createDataCoroutine = ServerMgr.Instance.StartCoroutine(CreateData());
    });
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

  private static bool DestroyTimer(Timer t) => TimerValid(t) && t.Destroy();

  private static bool TimerValid(Timer t) => false == t?.Destroyed;

  private void DestroyTimerDictionary<T>(
    ref Dictionary<T, Timer> dict, string desc)
  {
    if (dict.Count <= 0) return;
    Puts($"Unload():  Destroying {dict.Count} {desc} timer(s)...");
    foreach (var dTimer in dict.Values)
    {
      DestroyTimer(dTimer);
    }
  }

  private void Unload()
  {
    if (null != _createDataCoroutine)
    {
      ServerMgr.Instance.StopCoroutine(_createDataCoroutine);
      _createDataCoroutine = null;
    }

    // if save timer active, force immediate write
    if (TimerValid(_saveDataTimer))
    {
      Puts("Unload(): Forcing data file write");
      WriteData();
    }
    _saveDataTimer = null;

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
        DestroyTimer(timerData.Item1);
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

  private void LoadData()
  {
    try
    {
      _persistData =
        Interface.Oxide.DataFileSystem.ReadObject<PersistData>(Name);
    }
    catch (Exception ex)
    {
      Puts($"LoadData(): Error reading data file: {ex}");
      _persistData = null;
    }
    if (_persistData == null) ClearData();
  }

  private void ClearData()
  {
    _persistData = new PersistData();
    Puts("ClearData(): Scheduling write of new data file");
    SaveData();
  }

  // this is a frontend to WriteData() that enforces a minimum delay between
  //  data file writes
  private void SaveData()
  {
    // start a save timer unless already running
    if (TimerValid(_saveDataTimer)) return;
    // TODO: make save delay configurable?
    _saveDataTimer = timer.Once(60.0f, WriteData);
  }

  // do the actual data file write
  private void WriteData()
  {
    DestroyTimer(_saveDataTimer);
    Interface.Oxide.DataFileSystem.WriteObject(Name, _persistData);
    Puts("WriteData(): Wrote data file");
  }

  // common logic for tugboat zone end-of-life scenarios
  // untether controls whether sphere(s) and/or zone should be disconnected from
  //  the tugboat, which is desirable when it's sunk/killed but not when the
  //  zone is being toggled off by a player
  private void HandleTugboatEol(Tugboat tugboat, bool disconnect = true)
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
    // disconnect sphere(s) and/or zone if requested
    if (disconnect)
    {
      // release entity reference immediately to minimize side effects
      tugboatData.ClearEntity();
      // also untether ZoneManager zone if present, so that it doesn't disappear
      UntetherZone(GetZoneID(tugboatID), tugboat);
    }

    // schedule deletion of the dropped base
    NextTick(() => ScheduleDeleteTugboatData(tugboatID));
  }

  // called when a tugboat reaches zero health and sinks
  // NOTE: this only fires for Tugboat and not VehiclePrivilege
  private void OnEntityDeath(Tugboat tugboat) => HandleTugboatEol(tugboat);

  // called when a building block is destroyed
  private void OnEntityKill(BuildingBlock buildingBlock)
  {
    // abort if this isn't a player-owned building block
    if (!IsPlayerOwned(buildingBlock)) return;

    // attempt to find an attached TC
    var toolCupboard = GetToolCupboard(buildingBlock);

    // abort if no TC found, or if TC is not player-owned
    if (!IsPlayerOwned(toolCupboard)) return;

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
  private void OnEntityKill(Tugboat tugboat) => HandleTugboatEol(tugboat);

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

  // called when a building block is damaged
  private object OnEntityTakeDamage(
    BuildingBlock buildingBlock, HitInfo hitInfo)
  {
    NextTick(() =>
    {
      // abort if this isn't a player-owned building block
      if (!IsPlayerOwned(buildingBlock)) return;

      // attempt to find an attached TC
      var toolCupboard = GetToolCupboard(buildingBlock);

      // abort if no TC found, or if TC is not player-owned
      if (!IsPlayerOwned(toolCupboard)) return;

      // cache TC ID
      var toolCupboardID = GetNetworkableID(toolCupboard);

      // abort if no PBPZ PVP zone for this TC
      if (!_buildingData.ContainsKey(toolCupboardID)) return;

      // record damage lockout
      // NOTE: state=true is specified because we've verified that a zone exists
      SetToggleStates(
        _persistData.BuildingToggleData, toolCupboardID, true,
        LockoutReason.Damage);
    });

    return null;
  }

  // called when a TC is damaged
  private object OnEntityTakeDamage(
    BuildingPrivlidge toolCupboard, HitInfo hitInfo)
  {
    NextTick(() =>
    {
      // abort if this isn't a player-owned building TC
      if (!IsPlayerOwned(toolCupboard)) return;

      // cache TC ID
      var toolCupboardID = GetNetworkableID(toolCupboard);

      // abort if no PBPZ PVP zone for this TC
      if (!_buildingData.ContainsKey(toolCupboardID)) return;

      // record damage lockout
      // NOTE: state=true is specified because we've verified that a zone exists
      SetToggleStates(
        _persistData.BuildingToggleData, toolCupboardID, true,
        LockoutReason.Damage);
    });

    return null;
  }

  // called when a legacy shelter is damaged
  private object OnEntityTakeDamage(
    LegacyShelter legacyShelter, HitInfo hitInfo)
  {
    NextTick(() =>
    {
      // abort if this isn't a player-owned building shelter
      if (!IsPlayerOwned(legacyShelter)) return;

      // cache shelter ID
      var legacyShelterID =
        GetNetworkableID(legacyShelter.GetEntityPrivilege());

      // abort if no PBPZ PVP zone for this shelter
      if (!_shelterData.ContainsKey(legacyShelterID)) return;

      // record damage lockout
      // NOTE: state=true is specified because we've verified that a zone exists
      SetToggleStates(
        _persistData.ShelterToggleData, legacyShelterID, true,
        LockoutReason.Damage);
    });

    return null;
  }

  // called when a tugboat is damaged
  private object OnEntityTakeDamage(Tugboat tugboat, HitInfo hitInfo)
  {
    NextTick(() =>
    {
      // cache tugboat ID
      var tugboatPrivilege = GetVehiclePrivilege(tugboat);
      if (!tugboatPrivilege) return;
      var tugboatID = GetNetworkableID(tugboatPrivilege);

      // abort if no PBPZ PVP zone for this tugboat
      if (!_tugboatData.ContainsKey(tugboatID)) return;

      // record damage lockout
      // NOTE: state=true is specified because we've verified that a zone exists
      SetToggleStates(
        _persistData.TugboatToggleData, tugboatID, true, LockoutReason.Damage);
    });

    return null;
  }

  // custom hook defined by this plugin to return originating zoneID if player
  //  has an active PvP delay, or an empty string if not
  private string OnPlayerBasePvpDelayQuery(ulong playerID) =>
    _pvpDelayTimers.TryGetValue(playerID, out var delayData) ?
      delayData.Item2 : string.Empty;

  private bool IsUsingExcludePlayer() => _useExcludePlayer;

  // get legacy shelter whose build privilege the player is both inside of and
  //  authorized to
  private EntityPrivilege GetPlayerShelter(BasePlayer player) =>
    // this first step is really here to force a
    //  player.cachedEntityBuildingPrivilege cache update when applicable
    !player.IsBuildingBlockedByEntity(true) &&
    GetShelterPrivilege(player.cachedEntityBuildingPrivilege) is {} privilege &&
    privilege.IsAuthed(player)
        ? privilege
        : null;

  // get TC that player is within interaction range of, looking at, and
  //  authorized to, or null if none
  private BuildingPrivlidge GetPlayerBuilding(BasePlayer player) =>
    Physics.Raycast(
      player.eyes.HeadRay(), out var hit, BuildingToggleRadius,
      _buildingLayerMask) &&
    hit.GetEntity() is BuildingPrivlidge toolCupboard &&
    IsValid(toolCupboard) &&
    toolCupboard.IsAuthed(player)
      ? toolCupboard
      : null;

  // get non-dying tugboat that player is mounted and authorized to, or null if
  //  none
  private VehiclePrivilege GetPlayerTugboat(BasePlayer player) =>
    player.GetMountedVehicle() is Tugboat { IsDying: false } tugboat &&
    GetVehiclePrivilege(tugboat) is {} privilege &&
    privilege.IsAuthed(player)
      ? privilege
      : null;

  private string GetLockoutMessageName(LockoutReason reason) => reason switch
  {
    LockoutReason.None => null,
    LockoutReason.Damage => "NotifyLockoutDamage",
    LockoutReason.Toggle => "NotifyLockoutToggle",
    _ => null
  };

  // apply zone toggle logic for given player and data set
  private void ToggleZone<T>(
    BasePlayer                            player,
    ref Dictionary<NetworkableId, Timer>  createDict,
    ref Dictionary<NetworkableId, Timer>  deleteDict,
    Dictionary<NetworkableId, T>          dataDict,
    Dictionary<ulong, ToggleData>         toggleDict,
    BaseNetworkable                       entity)
  {
    if (!player || !player.userID.IsSteamId()) return;
    var netID = GetNetworkableID(entity);
    var toggleStates = GetToggleStates(toggleDict, netID);

    // abort if lockout in progress
    var lockoutMessage = GetLockoutMessageName(toggleStates.Item2);
    if (null != lockoutMessage)
    {
      SendReply(player, string.Format(lang.GetMessage(lockoutMessage,
          this, player.UserIDString),
        _configData.PrefixNotify, toggleStates.Item3));
      return;
    }

    // handle zone currently scheduled for creation due to spawn or previous
    //  toggle
    // NOTE: supports null because tugboats don't currently have creation delays
    if (true == createDict?.ContainsKey(netID))
    {
      SendReply(player, string.Format(lang.GetMessage("NotifyCancelCreate",
        this, player.UserIDString), _configData.PrefixNotify));

      // record disabled state and apply toggle lockout (if applicable)
      SetToggleStates(toggleDict, netID, false, LockoutReason.Toggle);

      // cancel zone creation to keep it as PVE
      CancelDictionaryTimer(ref createDict, netID);

      return;
    }

    // handle zone currently scheduled for deletion due to deploy or previous
    //  toggle
    //
    // NOTE: it is assumed that previous code has verified that the delete timer
    //  is not running due to entity kill/death
    if (deleteDict.ContainsKey(netID))
    {
      SendReply(player, string.Format(lang.GetMessage("NotifyCancelDelete",
        this, player.UserIDString), _configData.PrefixNotify));

      // record toggle state and apply lockout (if applicable)
      SetToggleStates(toggleDict, netID, true, LockoutReason.Toggle);

      // cancel zone delete to keep it as PVP
      CancelDictionaryTimer(ref deleteDict, netID);

      return;
    }

    // handle zone currently active
    if (dataDict.ContainsKey(netID))
    {
      SendReply(player, string.Format(lang.GetMessage("NotifyToggleDelete",
        this, player.UserIDString), _configData.PrefixNotify));

      // record toggle state and apply lockout (if applicable)
      SetToggleStates(toggleDict, netID, false, LockoutReason.Toggle);

      // schedule zone delete to convert it to PVE
      switch (entity)
      {
        case VehiclePrivilege tugboat:
        {
          // call tugboat end-of-life logic, but leave zone/sphere(s) connected
          HandleTugboatEol(tugboat.GetParentEntity() as Tugboat, false);
          break;
        }
        case BuildingPrivlidge building: OnEntityKill(building); break;
        case EntityPrivilege   shelter:  OnEntityKill(shelter);  break;
      }

      return;
    }

    // else zone was previously toggled off
    SendReply(player, string.Format(lang.GetMessage("NotifyToggleCreate",
      this, player.UserIDString), _configData.PrefixNotify));

    // record toggle state and apply lockout (if applicable)
    SetToggleStates(toggleDict, netID, true, LockoutReason.Toggle);

    // schedule zone create to convert it to PVP
    switch (entity)
    {
      case VehiclePrivilege  tugboat:  OnEntitySpawned(tugboat);  break;
      case BuildingPrivlidge building: OnEntitySpawned(building); break;
      case EntityPrivilege   shelter:  OnEntitySpawned(shelter);  break;
    }
  }

  private void HandleCommandHelp(IPlayer iPlayer, BasePlayer player)
  {
    var noPerms = true;

    if (iPlayer.IsAdmin ||
        iPlayer.HasPermission(GetPermission(TogglePermission)))
    {
      SendReply(player, string.Format(lang.GetMessage("NotifyCommandToggle",
        this, player.UserIDString), CommandRoot, CommandToggle));
      noPerms = false;
    }

    if (noPerms)
    {
      SendReply(player, lang.GetMessage("NotifyNoPerms",
        this, player.UserIDString));
    }
  }

  private void HandleCommandToggle(BasePlayer player)
  {
    // handle player in tugboat
    var tugboat = GetPlayerTugboat(player);
    if (tugboat)
    {
      Dictionary<NetworkableId, Timer> temp = null;
      ToggleZone(
        player, ref temp, ref _tugboatDeleteTimers,
        _tugboatData, _persistData.TugboatToggleData, tugboat);

      return;
    }

    // handle player looking at TC (do this before shelter because it's possible
    //  to put a TC inside a shelter if you're a goofball like hJune)
    var building = GetPlayerBuilding(player);
    if (building)
    {
      ToggleZone(
        player, ref _buildingCreateTimers, ref _buildingDeleteTimers,
        _buildingData, _persistData.BuildingToggleData, building);

      return;
    }

    // handle player looking at legacy shelter
    var shelter = GetPlayerShelter(player);
    if (shelter)
    {
      ToggleZone(
        player, ref _shelterCreateTimers, ref _shelterDeleteTimers,
        _shelterData, _persistData.ShelterToggleData, shelter);

      return;
    }

    SendReply(player, string.Format(lang.GetMessage("NotifyToggleFail",
      this, player.UserIDString), _configData.PrefixNotify));
  }

  // chat command handler
  private void HandleCommand(IPlayer iPlayer, string command, string[] args)
  {
    if (iPlayer.Object is not BasePlayer player ||
        !player || !player.userID.IsSteamId())
    {
      return;
    }

    if (args.Length < 1)
    {
      SendReply(player, string.Format(lang.GetMessage("NotifyNoParams",
        this, player.UserIDString), CommandRoot, CommandHelp));
      return;
    }

    switch (args[0])
    {
      case CommandHelp:
        HandleCommandHelp(iPlayer, player);
        return;

      case CommandToggle:
        if (!iPlayer.IsAdmin &&
            !iPlayer.HasPermission(GetPermission(TogglePermission)))
        {
          break;
        }
        HandleCommandToggle(player);
        return;
    }

    SendReply(player, string.Format(lang.GetMessage("NotifyNoParams",
      this, player.UserIDString), CommandRoot, CommandHelp));
  }

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
        if (!IsValid(sphere)) continue;
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
        if (!IsValid(sphere)) continue;
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

    [JsonProperty(PropertyName = "Building settings")]
    public BuildingConfigData Building = new();

    [JsonProperty(PropertyName = "Shelter settings")]
    public ShelterConfigData Shelter = new();

    [JsonProperty(PropertyName = "Tugboat settings")]
    public TugboatConfigData Tugboat = new();

    [JsonProperty(PropertyName = "Zone toggle settings")]
    public ToggleConfigData Toggle = new();
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

  private sealed class ToggleConfigData
  {
    [JsonProperty(PropertyName = "Zone toggle lockout seconds after damage")]
    public float DamageLockoutSeconds = 60.0f;
    [JsonProperty(PropertyName = "Zone toggle lockout seconds after toggle")]
    public float ToggleLockoutSeconds = 60.0f;
  }

  // data file structure
  private sealed class PersistData
  {
    public Dictionary<ulong, ToggleData>
      BuildingToggleData { get; set; } = new();
    public Dictionary<ulong, ToggleData>
      ShelterToggleData  { get; set; } = new();
    public Dictionary<ulong, ToggleData>
      TugboatToggleData  { get; set; } = new();
  }

  // state data pertaining to zone toggling
  private class ToggleData
  {
    public ToggleData()
    {
    }

    public ToggleData(LockoutReason lockReason, DateTime unlockTime, bool zoneEnabled)
    {
      LockReason = lockReason;
      UnlockTime = unlockTime;
      ZoneEnabled = zoneEnabled;
    }

    // if not None, toggling is locked out for this reason
    public LockoutReason LockReason { get; set; } = LockoutReason.None;
    // if not MinValue, toggling is locked out until this time
    public DateTime UnlockTime { get; set; } = DateTime.MinValue;
    // whether zone is enabled
    public bool ZoneEnabled { get; set; } = true;
  }

  private readonly ToggleData _defaultToggleData  = new(
    LockoutReason.None,
    DateTime.MinValue,
    true
  );

  // reasons that toggling of a zone may be locked out
  private enum LockoutReason { None, Damage, Toggle }

  // result flags for CleanupToggleData()
  [Flags]
  private enum CleanupToggleResult
  {
    None   = 0,
    Changed = 1,
    Defunct = 2
  }

  private static CleanupToggleResult CleanupToggleData(
    ToggleData toggleData, DateTime curTime)
  {
    var retVal = CleanupToggleResult.None;

    if (LockoutReason.None != toggleData.LockReason &&
        curTime >= toggleData.UnlockTime)
    {
      // lockout expired; reset state to None + MinValue
      toggleData.LockReason = LockoutReason.None;
      toggleData.UnlockTime = DateTime.MinValue;
      retVal |= CleanupToggleResult.Changed;
    }

    if (LockoutReason.None == toggleData.LockReason && toggleData.ZoneEnabled)
    {
      // no reason to track enabled zone with no lockout in effect, as this is
      //  the default state
      retVal |= CleanupToggleResult.Defunct;
    }

    return retVal;
  }

  // prune any dead entities from given toggle dictionary
  // NOTE: doesn't bother expiring lockouts or pruning redundant entries, as
  //  this will get done when checking remaining entries during zone creation
  private int CleanupToggleDict(
    Dictionary<ulong, ToggleData> toggleDict)
  {
    // not worth using pooling here, as it will be 3 lists per plugin lifetime
    HashSet<ulong> deadKeys = new();

    foreach (var id in toggleDict.Keys)
    {
      if (!BaseNetworkable.serverEntities.Contains(GetNetworkableID(id)))
      {
        // this entry is for an entity that no longer exists on the server
        deadKeys.Add(id);
      }
    }

    // trash any dead keys
    foreach (var key in deadKeys)
    {
      toggleDict.Remove(key);
    }

    return deadKeys.Count;
  }

  private readonly (bool, LockoutReason, double) _defaultToggleStates =
    (true, LockoutReason.None, 0.0f);

  // get zone enable state and lockout reason
  //
  // handles various cases:
  // - no toggleDict entry
  // - redundant toggleDict entry
  // - lockout expired
  //
  // schedules data file save if something changed
  private (bool, LockoutReason, double) GetToggleStates(
    Dictionary<ulong, ToggleData> toggleDict, NetworkableId netId)
  {
    // if no toggle entry, zone should be enabled
    if (!toggleDict.TryGetValue(netId.Value, out var toggleData))
    {
      return _defaultToggleStates;
    }

    var changed = false;
    var curTime = DateTime.UtcNow;
    var cleanupState = CleanupToggleData(toggleData, curTime);
    if (cleanupState.HasFlag(CleanupToggleResult.Defunct))
    {
      toggleDict.Remove(netId.Value);
      changed = true;
    }
    else if (cleanupState.HasFlag(CleanupToggleResult.Changed))
    {
      changed = true;
    }
    if (changed)
    {
      SaveData();
    }

    var lockoutRemainingSeconds = LockoutReason.None == toggleData.LockReason ?
      0.0f : (toggleData.UnlockTime - curTime).TotalSeconds;
    if (lockoutRemainingSeconds < 0.0f) lockoutRemainingSeconds = 0.0f;

    return
      (toggleData.ZoneEnabled, toggleData.LockReason, lockoutRemainingSeconds);
  }

  // records zone toggle state and/or applies lockout of the appropriate
  //  duration for the given reason
  //
  // lockout only applied if reason is other than None, has a configured
  //  positive lockout duration, and (if a lockout is already in effect) new
  //  lockout would expire at or after time of any existing one
  //
  // data file save will be scheduled if anything changed
  private void SetToggleStates(
    Dictionary<ulong, ToggleData> toggleDict, NetworkableId netId,
    bool state, LockoutReason reason)
  {
    // get configured lockout duration for requested lockout reason
    var lockoutTime = reason switch
    {
      LockoutReason.None    => 0.0f,
      LockoutReason.Damage  => _configData.Toggle.DamageLockoutSeconds,
      LockoutReason.Toggle => _configData.Toggle.ToggleLockoutSeconds,
      _                     => 0.0f
    };
    if (LockoutReason.None != reason && lockoutTime <= 0.0f)
    {
      reason = LockoutReason.None;
    }
    // get effective proposed lockout expiration time
    var expireTime = LockoutReason.None == reason ?
      DateTime.MinValue : DateTime.UtcNow.AddSeconds(lockoutTime);

    // if no existing record, add one, schedule save, and return
    if (!toggleDict.TryGetValue(netId.Value, out var toggleData))
    {
      // ...unless the record would be redundant
      if (!state && reason == LockoutReason.None) return;

      toggleDict.Add(netId.Value, new ToggleData(
        lockReason: reason, unlockTime: expireTime, zoneEnabled: state));
      SaveData();
      return;
    }
    // else found an existing record

    var changed = false;
    if (toggleData.ZoneEnabled != state)
    {
      toggleData.ZoneEnabled = state;
      changed = true;
    }

    // only apply lockout if reason is other than None, and either no current
    //  lockout in effect, or new lockout would expire after existing one
    if (reason != LockoutReason.None &&
        (toggleData.LockReason == LockoutReason.None ||
         expireTime >= toggleData.UnlockTime))
    {
      toggleData.LockReason = reason;
      toggleData.UnlockTime = expireTime;
      changed = true;
    }

    if (!changed) return;

    SaveData();
  }
}

#endregion Internal Classes

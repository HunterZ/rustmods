//Requires: ZoneManager

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Prefabs.Misc;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Dynamic PVP", "HunterZ/CatMeat/Arainrr", "4.11.0", ResourceId = 2728)]
[Description("Creates temporary PvP zones on certain actions/events")]
public class DynamicPVP : RustPlugin
{
  #region Fields

  [PluginReference] Plugin Backpacks, BotReSpawn, TruePVE, ZoneManager;

  private const string PermissionAdmin = "dynamicpvp.admin";
  private const string PrefabLargeOilRig =
    "assets/bundled/prefabs/autospawn/monument/offshore/oilrig_1.prefab";
  private const string PrefabOilRig =
    "assets/bundled/prefabs/autospawn/monument/offshore/oilrig_2.prefab";
  private const string PrefabSphereDome =
    "assets/prefabs/visualization/sphere.prefab";
  private const string PrefabSphereRedRing =
    "assets/bundled/prefabs/modding/events/twitch/br_sphere_red.prefab";
  private const string PrefabSphereGreenRing =
    "assets/bundled/prefabs/modding/events/twitch/br_sphere_green.prefab";
  private const string PrefabSphereBlueRing =
    "assets/bundled/prefabs/modding/events/twitch/br_sphere.prefab";
  private const string PrefabSpherePurpleRing =
    "assets/bundled/prefabs/modding/events/twitch/br_sphere_purple.prefab";
  private const string ZoneName = "DynamicPVP";

  private readonly Dictionary<string, Timer> _eventTimers = new();
  private readonly Dictionary<ulong, LeftZone> _pvpDelays = new();

  // Map of event names + base events by ZoneID
  private readonly Dictionary<string, BaseEvent> _activeDynamicZones =
    new();
  // ZoneID/NetID of Deep Sea zones that we may have tried to create
  private readonly HashSet<string> _potentialDeepSeaZones = new();

  // plugin integration zone tracking - used for managing hook subscriptions
  //  and for faster lookups

  private enum PluginZoneCategory
  {
    BackpacksForce,
    BackpacksPrevent,
    LootDefender,
    RestoreUponDeath
  }

  private readonly Dictionary<PluginZoneCategory, HashSet<string>>
    _activePluginZones = new();

  private Vector3 _oilRigPosition = Vector3.zero;
  private Vector3 _largeOilRigPosition = Vector3.zero;
  private bool _useExcludePlayer;
  private bool _brokenTunnels;
  private bool _dataChanged;
  private Coroutine _coroutine;
  private readonly YieldInstruction _fastYield = null;
  private readonly YieldInstruction _throttleYield =
    CoroutineEx.waitForSeconds(0.1f);
  private readonly YieldInstruction _pauseYield =
    CoroutineEx.waitForSeconds(0.5f);
  private float _targetFps = -1.0f;

  private sealed class LeftZone : Pool.IPooled
  {
    public string zoneId;
    public BaseEvent baseEvent;
    public Timer zoneTimer;

    private void Reset()
    {
      zoneId = null;
      baseEvent = null;
      zoneTimer?.Destroy();
      zoneTimer = null;
    }

    public void EnterPool() => Reset();

    public void LeavePool() => Reset();
  }

  private enum CoroutineTypes
  {
    CreateStartupEvents,
    CreateDeepSeaEvents,
    DeleteDeepSeaEvents
  }

  [Flags]
  [JsonConverter(typeof(StringEnumConverter))]
  private enum PvpDelayTypes
  {
    None = 0,
    ZonePlayersCanDamageDelayedPlayers = 1,
    DelayedPlayersCanDamageZonePlayers = 1 << 1,
    DelayedPlayersCanDamageDelayedPlayers = 1 << 2
  }

  // general and deep sea event types
  // these are managed as a single enum to prevent name collisions
  public enum GeneralEventType
  {
    Bradley,
    Helicopter,
    TimedSupply,
    SupplySignal,
    CargoShip,
    HackableCrate,
    ExcavatorIgnition,
    GhostShip,
    DeepSeaIsland,
    IslandCannon
  }

  [Flags]
  private enum HookCheckReasons
  {
    None         = 0,
    DelayAdded   = 1 << 0,
    DelayRemoved = 1 << 1,
    ZoneAdded    = 1 << 2,
    ZoneRemoved  = 1 << 3
  }

  private enum HookCategory
  {
    Command,
    PluginBackpacksForce,
    PluginBackpacksPrevent,
    PluginLootDefender,
    PluginRestoreUponDeath,
    PvpDelay,
    Zone
  }

  // hook names by hook category
  private readonly Dictionary<HookCategory, List<string>> _hooksByCategory =
    new()
    {
      { HookCategory.Command,                new List<string> {
        nameof(OnPlayerCommand),
        nameof(OnServerCommand) } },
      { HookCategory.PluginBackpacksForce,   new List<string> {
        nameof(OnPlayerDeath) } },
      { HookCategory.PluginBackpacksPrevent, new List<string> {
        nameof(CanDropBackpack) } },
      { HookCategory.PluginLootDefender,     new List<string> {
        nameof(OnLootLockedEntity) } },
      { HookCategory.PluginRestoreUponDeath, new List<string> {
        nameof(OnRestoreUponDeath) } },
      { HookCategory.PvpDelay,               new List<string> {
        nameof(CanEntityTakeDamage) } },
      { HookCategory.Zone,                   new List<string> {
        nameof(OnEnterZone),
        nameof(OnExitZone) } }
    };

  // current hook subscription state by hook category
  private readonly Dictionary<HookCategory, bool> _subscriptionsByCategory =
    new();

  private readonly Collider[] _colliderBuffer = new Collider[8];

  private enum MonumentEventType
  {
    Default,
    Custom,
    TunnelEntrance,
    TunnelLink,
    TunnelSection,
    UnderwaterLabs
  }

  private readonly Dictionary<string, OriginalMonumentGeometry>
    _originalMonumentGeometries = new();

  #endregion Fields

  #region Oxide Hooks

  private void Init()
  {
    foreach (
      PluginZoneCategory pzCat in Enum.GetValues(typeof(PluginZoneCategory)))
    {
      _activePluginZones[pzCat] = Pool.Get<HashSet<string>>();
    }

    _brokenTunnels = false;

    LoadData();
    permission.RegisterPermission(PermissionAdmin, this);
    AddCovalenceCommand(_configData.Chat.Command, nameof(CmdDynamicPVP));

    Unsubscribe(nameof(CanDropBackpack));
    Unsubscribe(nameof(CanEntityTakeDamage));
    Unsubscribe(nameof(OnCargoPlaneSignaled));
    Unsubscribe(nameof(OnCargoShipEgress));
    Unsubscribe(nameof(OnCargoShipHarborApproach));
    Unsubscribe(nameof(OnCargoShipHarborArrived));
    Unsubscribe(nameof(OnCargoShipHarborLeave));
    Unsubscribe(nameof(OnCrateHack));
    Unsubscribe(nameof(OnCrateHackEnd));
    Unsubscribe(nameof(OnDeepSeaClosed));
    Unsubscribe(nameof(OnDeepSeaOpened));
    Unsubscribe(nameof(OnDieselEngineToggled));
    Unsubscribe(nameof(OnEnterZone));
    Unsubscribe(nameof(OnEntityDeath));
    Unsubscribe(nameof(OnEntityKill));
    Unsubscribe(nameof(OnEntitySpawned));
    Unsubscribe(nameof(OnExitZone));
    Unsubscribe(nameof(OnLootEntity));
    Unsubscribe(nameof(OnLootLockedEntity));
    Unsubscribe(nameof(OnPlayerCommand));
    Unsubscribe(nameof(OnPlayerDeath));
    Unsubscribe(nameof(OnRestoreUponDeath));
    Unsubscribe(nameof(OnServerCommand));
    Unsubscribe(nameof(OnSupplyDropLanded));
    foreach (var category in _hooksByCategory.Keys)
    {
      _subscriptionsByCategory[category] = false;
    }

    if (_configData.Global.LogToFile)
    {
      _debugStringBuilder = Pool.Get<StringBuilder>();
    }

    // setup new TruePVE "ExcludePlayer" support
    _useExcludePlayer = _configData.Global.UseExcludePlayer;
    // if ExcludePlayer is disabled in config but is supported...
    if (!_useExcludePlayer &&
        null != TruePVE &&
        TruePVE.Version >= new VersionNumber(2, 2, 3))
    {
      // ...and all PVP delays are enabled, auto-enable internally and warn
      if ((PvpDelayTypes.ZonePlayersCanDamageDelayedPlayers |
           PvpDelayTypes.DelayedPlayersCanDamageZonePlayers |
           PvpDelayTypes.DelayedPlayersCanDamageDelayedPlayers) ==
          _configData.Global.PvpDelayFlags)
      {
        _useExcludePlayer = true;
        Puts("All PVP delay flags active and TruePVE 2.2.3+ detected, so TruePVE PVP delays will be used for performance and cross-plugin support; please consider enabling TruePVE PVP Delay API in the config file to skip this check");
      }
      // else just nag, since settings are not compatible
      else
      {
        Puts("Some/all PVP delay flags NOT active, but TruePVE 2.2.3+ detected; please consider switching to TruePVE PVP Delay API in the config file for performance and cross-plugin support");
      }
    } // else ExcludePlayer is already enabled, or TruePVE 2.2.3+ not running
  }

  private void OnServerInitialized()
  {
    if (null == ZoneManager ||
        ZoneManager.Version < new VersionNumber(3, 1, 10))
    {
      PrintError("Zone Manager missing or outdated; please update for proper function of this plugin!");
    }

    // resubscribe to any hooks that are conditional due to config options
    // code is grouped alphabetically by first hook name, to make it easier t
    //  see what gets subscribed and why
    if (_configData.GeneralEvents.SupplySignal.Enabled ||
        _configData.GeneralEvents.TimedSupply.Enabled)
    {
      // this is subscribed if either drop event is enabled, because we need to
      //  differentiate either way
      Subscribe(nameof(OnCargoPlaneSignaled));
      Subscribe(nameof(OnSupplyDropDropped));
      // this is now subscribed regardless of start on spawn-vs-landing, as we
      //  need to tether the zone to the drop on landing in both cases
      Subscribe(nameof(OnSupplyDropLanded));
    }
    if (_configData.GeneralEvents.CargoShip.Enabled)
    {
      Subscribe(nameof(OnCargoShipEgress));
      Subscribe(nameof(OnCargoShipHarborApproach));
      Subscribe(nameof(OnCargoShipHarborArrived));
      Subscribe(nameof(OnCargoShipHarborLeave));
    }
    if (_configData.GeneralEvents.HackableCrate.Enabled &&
        !_configData.GeneralEvents.HackableCrate.StartWhenSpawned)
    {
      Subscribe(nameof(OnCrateHack));
    }
    if (_configData.GeneralEvents.HackableCrate.Enabled &&
        _configData.GeneralEvents.HackableCrate.TimerStartWhenUnlocked)
    {
      Subscribe(nameof(OnCrateHackEnd));
    }
    if (AnySeepSeaEventEnabled())
    {
      Subscribe(nameof(OnDeepSeaClosed));
      Subscribe(nameof(OnDeepSeaOpened));
    }
    if (_configData.GeneralEvents.ExcavatorIgnition.Enabled)
    {
      Subscribe(nameof(OnDieselEngineToggled));
    }
    if (_configData.GeneralEvents.PatrolHelicopter.Enabled ||
        _configData.GeneralEvents.BradleyApc.Enabled)
    {
      Subscribe(nameof(OnEntityDeath));
    }
    if ((_configData.GeneralEvents.TimedSupply.Enabled &&
         _configData.GeneralEvents.TimedSupply.StopWhenKilled) ||
        (_configData.GeneralEvents.SupplySignal.Enabled &&
         _configData.GeneralEvents.SupplySignal.StopWhenKilled) ||
        (_configData.GeneralEvents.HackableCrate.Enabled &&
         _configData.GeneralEvents.HackableCrate.StopWhenKilled) ||
        _configData.GeneralEvents.CargoShip.Enabled)
    {
      Subscribe(nameof(OnEntityKill));
    }
    if ((_configData.GeneralEvents.TimedSupply.Enabled &&
         _configData.GeneralEvents.TimedSupply.StartWhenSpawned) ||
        (_configData.GeneralEvents.SupplySignal.Enabled &&
         _configData.GeneralEvents.SupplySignal.StartWhenSpawned) ||
        (_configData.GeneralEvents.HackableCrate.Enabled &&
         _configData.GeneralEvents.HackableCrate.StartWhenSpawned) ||
        _configData.GeneralEvents.CargoShip.Enabled)
    {
      Subscribe(nameof(OnEntitySpawned));
    }
    if ((_configData.GeneralEvents.TimedSupply.Enabled &&
         _configData.GeneralEvents.TimedSupply.TimerStartWhenLooted) ||
        (_configData.GeneralEvents.SupplySignal.Enabled &&
         _configData.GeneralEvents.SupplySignal.TimerStartWhenLooted) ||
        (_configData.GeneralEvents.HackableCrate.Enabled &&
         _configData.GeneralEvents.HackableCrate.TimerStartWhenLooted))
    {
      Subscribe(nameof(OnLootEntity));
    }

    NextTick(() => TryStartCoroutine(CoroutineTypes.CreateStartupEvents));
  }

  private void Unload()
  {
    if (_coroutine != null)
    {
      ServerMgr.Instance.StopCoroutine(_coroutine);
    }

    if (_activeDynamicZones.Count > 0)
    {
      PrintDebug($"Unload(): Deleting {_activeDynamicZones.Count} active zone(s)");
      // copy zone keys to a temporary list, because each deletion will modify
      //  _activeDynamicZones
      var zoneKeys = Pool.Get<List<string>>();
      zoneKeys.AddRange(_activeDynamicZones.Keys);
      foreach (var key in zoneKeys)
      {
        DeleteDynamicZone(key);
      }
      Pool.FreeUnmanaged(ref zoneKeys);
      _activeDynamicZones.Clear();
      _potentialDeepSeaZones.Clear();
    }

    // copy LeftZone records to a temporary list to that we can reverse iterate
    var leftZones = Pool.Get<List<LeftZone>>();
    leftZones.AddRange(_pvpDelays.Values);
    for (var i = leftZones.Count - 1; i >= 0; --i)
    {
      // this is cheating because it leaves leftZones[i] in a dangling state,
      //  but it's okay because we're going to free/clear everything anyway
      var value = leftZones[i];
      Pool.Free(ref value);
    }
    Pool.FreeUnmanaged(ref leftZones);
    _pvpDelays.Clear();
    // also remove LeftZone class from pool framework, in case it changes on
    //  plugin reload
    Pool.Directory.TryRemove(typeof(LeftZone), out _);

    // copy sphere lists to a temporary list so that we can reverse iterate
    var spheres = Pool.Get<List<List<SphereEntity>>>();
    spheres.AddRange(_zoneSpheres.Values);
    for (var i = _zoneSpheres.Count - 1; i >= 0; --i)
    {
      // this is cheating because it leaves spheres[i] in a dangling state, but
      //  it's okay because we're going to free/clear everything anyway
      var sphereEntities = spheres[i];
      foreach (var sphereEntity in sphereEntities)
      {
        if (!sphereEntity || sphereEntity.IsDestroyed) continue;
        sphereEntity.KillMessage();
      }
      Pool.FreeUnmanaged(ref sphereEntities);
    }
    Pool.FreeUnmanaged(ref spheres);
    _zoneSpheres.Clear();

    SaveData();
    SaveDebug();

    if (null == _debugStringBuilder) return;
    Pool.FreeUnmanaged(ref _debugStringBuilder);
    _debugStringBuilder = null;

    _originalMonumentGeometries.Clear();

    foreach (var apZone in _activePluginZones.Values)
    {
      // this is cheating because it leaves apZone in a dangling state, but it's
      //  okay because we're going to clear the list just after this
      var apZoneI = apZone;
      Pool.FreeUnmanaged(ref apZoneI);
    }
    _activePluginZones.Clear();
    DomeEvent._domeEventsToCheck = null;

    _activeSignaledPlanesAndDrops.Clear();
  }

  private void OnServerSave() =>
    timer.Once(UnityEngine.Random.Range(0f, 60f), () =>
    {
      SaveDebug();
      if (!_dataChanged) return;
      SaveData();
      _dataChanged = false;
    });

  private void OnPlayerRespawned(BasePlayer player)
  {
    if (!player || !player.userID.IsSteamId())
    {
      PrintDebug("OnPlayerRespawned(): Ignoring respawn of null/NPC player");
      return;
    }

    TryRemovePVPDelay(player);
  }

  #endregion Oxide Hooks

  #region Methods

  // try to start event creation coroutine
  // startup: true => CreateStartupEvents, false => CreateDeepSeaEvents
  // if coroutine already running, waits for it to stop and then starts it
  private void TryStartCoroutine(
    CoroutineTypes type, DeepSeaManager deepSeaManager = null)
  {
    if (null != _coroutine)
    {
      Puts("Waiting for current event creation coroutine to finish...");
      timer.Once(1.0f, () => TryStartCoroutine(type, deepSeaManager));
      return;
    }

    if (!deepSeaManager)
    {
      deepSeaManager = DeepSeaManager.ServerInstance;
    }

    switch (type)
    {
      case CoroutineTypes.CreateStartupEvents:
        _coroutine = ServerMgr.Instance.StartCoroutine(CreateStartupEvents());
        return;

      case CoroutineTypes.CreateDeepSeaEvents:
        if (deepSeaManager)
        {
          _coroutine = ServerMgr.Instance.StartCoroutine(CreateDeepSeaEvents(
              deepSeaManager, true, false));
        }
        else
        {
          Puts("ERROR: Can't start Deep Sea Events creation coroutine because DeepSeaManager is null");
        }
        return;

      case CoroutineTypes.DeleteDeepSeaEvents:
        _coroutine = ServerMgr.Instance.StartCoroutine(DeleteDeepSeaEvents());
        return;

      default:
        throw new ArgumentOutOfRangeException(nameof(type), type, null);
    }
  }

  private void TryRemoveEventTimer(string zoneId)
  {
    if (_eventTimers.Remove(zoneId, out var value))
    {
      value?.Destroy();
    }
  }

  private LeftZone GetOrAddPVPDelay(
    BasePlayer player, string zoneId, BaseEvent baseEvent)
  {
    PrintDebug($"GetOrAddPVPDelay(): Adding {player.displayName} to PVP delay");
    var added = false;
    if (_pvpDelays.TryGetValue(player.userID, out var leftZone))
    {
      leftZone.zoneTimer?.Destroy();
    }
    else
    {
      added = true;
      leftZone = Pool.Get<LeftZone>();
      _pvpDelays.Add(player.userID, leftZone);
    }

    leftZone.zoneId = zoneId;
    leftZone.baseEvent = baseEvent;
    if (added)
    {
      CheckHooks(HookCheckReasons.DelayAdded, baseEvent);
    }

    return leftZone;
  }

  private bool TryRemovePVPDelay(BasePlayer player)
  {
    PrintDebug($"TryRemovePVPDelay(): Removing {player.displayName} from PVP delay");
    var playerId = player.userID.Get();
    if (!_pvpDelays.Remove(playerId, out var leftZone)) return false;
    Interface.CallHook("OnPlayerRemovedFromPVPDelay",
      playerId, leftZone.zoneId, player);
    CheckHooks(HookCheckReasons.DelayRemoved, null); // baseEvent not needed
    Pool.Free(ref leftZone);
    return true;
  }

  private bool CheckEntityOwner(BaseEntity baseEntity)
  {
    if (!_configData.Global.CheckEntityOwner ||
        !baseEntity.OwnerID.IsSteamId() ||
        // HeliSignals and BradleyDrops exception
        baseEntity.skinID != 0)
    {
      return true;
    }

    PrintDebug($"CheckEntityOwner(): Skipping event creation because baseEntity={baseEntity} is owned by player={baseEntity.OwnerID}");
    return false;
  }

  private bool CanCreateDynamicPVP(string eventName, BaseEntity entity)
  {
    if (Interface.CallHook("OnCreateDynamicPVP", eventName, entity) == null)
    {
      return true;
    }

    PrintDebug($"CanCreateDynamicPVP(): Skipping event creation for eventName={eventName} due to OnCreateDynamicPVP hook result");
    return false;
  }

  private bool HasCommands()
  {
    // track which events we've checked, to avoid redundant calls to
    //  GetBaseEvent(); note that use of pool API means we need to free this
    //  on every return
    var checkedEvents = Pool.Get<HashSet<BaseEvent>>();
    // check for command-containing zones referenced by PVP delays, which
    //  either work when PVP delayed, or are an active zone
    // HZ: I guess this is really trying to catch the corner case of players
    //  in PVP delay because a zone expired?
    foreach (var leftZone in _pvpDelays.Values)
    {
      if (leftZone.baseEvent == null ||
          leftZone.baseEvent.CommandList.Count <= 0)
      {
        continue;
      }

      if (leftZone.baseEvent.CommandWorksForPVPDelay ||
          _activeDynamicZones.ContainsValue(leftZone.baseEvent))
      {
        Pool.FreeUnmanaged(ref checkedEvents);
        return true;
      }

      checkedEvents.Add(leftZone.baseEvent);
    }

    foreach (var baseEvent in _activeDynamicZones.Values)
    {
      // optimization: skip if we've already checked this in the other loop
      if (checkedEvents.Contains(baseEvent))
      {
        continue;
      }

      if (null == baseEvent || baseEvent.CommandList.Count <= 0) continue;
      Pool.FreeUnmanaged(ref checkedEvents);
      return true;
    }

    Pool.FreeUnmanaged(ref checkedEvents);
    return false;
  }

  /// toggle dynamic hook subscription(s) based on need
  private void UpdateDynamicHook(
    bool needSubscription, HookCategory hookCategory)
  {
    // abort if subscription tracking undefined, or subscription need already
    //  met, or hooks not defined
    if (!_subscriptionsByCategory.TryGetValue(
          hookCategory, out var haveSubscription) ||
        needSubscription == haveSubscription ||
        !_hooksByCategory.TryGetValue(hookCategory, out var hooks))
    {
      return;
    }

    // (un)subscribe per subscription need
    foreach (var hook in hooks)
    {
      if (needSubscription)
      {
        Subscribe(hook);
      }
      else
      {
        Unsubscribe(hook);
      }
    }

    // record that we've achieved desired subscription state
    _subscriptionsByCategory[hookCategory] = needSubscription;
  }

  private void CheckCommandHooks(bool added)
  {
    // optimization: avoid calling HasCommands() if added + already subscribed
    if (added &&
        _subscriptionsByCategory.TryGetValue(
          HookCategory.Command, out var subscribed) &&
        subscribed)
    {
      return;
    }

    UpdateDynamicHook(HasCommands(), HookCategory.Command);
  }

  /// update plugin integration tracking/subscriptions as appropriate
  private void CheckPluginHooks(BaseEvent baseEvent)
  {
    // this currently only supports checks when baseEvent is provided
    if (null == baseEvent) return;
    foreach (
      PluginZoneCategory pzCat in Enum.GetValues(typeof(PluginZoneCategory)))
    {
      if (HasPluginZoneCategory(baseEvent, pzCat))
      {
        UpdateDynamicHook(
          _activePluginZones[pzCat].Count > 0, ToHookCategory(pzCat));
      }
    }
  }

  private void CheckPvpDelayHooks() =>
    UpdateDynamicHook(
      !_useExcludePlayer && _pvpDelays.Count > 0, HookCategory.PvpDelay);

  private void CheckZoneHooks() =>
    UpdateDynamicHook(_activeDynamicZones.Count > 0, HookCategory.Zone);

  /// check whether hook subscription changes are warranted
  //
  // baseEvent is used as an optimization to only check plugin integration
  //  hook subscriptions when relevant zones are added/removed
  private void CheckHooks(HookCheckReasons reasons, BaseEvent baseEvent)
  {
    // update command hooks based on PVP delay or zone changes
    if (reasons.HasFlag(HookCheckReasons.DelayAdded) ||
        reasons.HasFlag(HookCheckReasons.ZoneAdded))
    {
      CheckCommandHooks(true);
    }
    else if (reasons.HasFlag(HookCheckReasons.DelayRemoved) ||
             reasons.HasFlag(HookCheckReasons.ZoneRemoved))
    {
      CheckCommandHooks(false);
    }

    // update PVP delay hooks based on PVP delay changes
    if (reasons.HasFlag(HookCheckReasons.DelayAdded) ||
        reasons.HasFlag(HookCheckReasons.DelayRemoved))
    {
      CheckPvpDelayHooks();
    }

    // update plugin and zone hooks based on zone changes
    if (reasons.HasFlag(HookCheckReasons.ZoneAdded) ||
        reasons.HasFlag(HookCheckReasons.ZoneRemoved))
    {
      CheckPluginHooks(baseEvent);
      CheckZoneHooks();
    }
  }

  #endregion Methods

  #region Events

  #region Startup

  // utility method to return an appropriate yield instruction based on
  //  whether this is a long pause for debug logging to catch up, whether
  //  current server framerate is too low, etc.
  private YieldInstruction DynamicYield(bool pause = false)
  {
    // perform one-time caching of target FPS
    if (_targetFps <= 0) _targetFps = Mathf.Min(ConVar.FPS.limit, 30);

    return
      pause && _configData.Global.DebugEnabled ? _pauseYield :
      Performance.report.frameRate >= _targetFps ? _fastYield :
      _throttleYield;
  }

  // coroutine to orchestrate creation of all relevant events on startup
  private IEnumerator CreateStartupEvents()
  {
    var startTime = DateTime.UtcNow;

    Puts("Creating General Events");
    yield return CreateGeneralEvents();

    var deepSeaManager = DeepSeaManager.ServerInstance;
    switch (AnySeepSeaEventEnabled())
    {
      case true when IsDeepSeaOpen(deepSeaManager):
        // this will get logged at a lower level
        yield return CreateDeepSeaEvents(
          deepSeaManager, delay: false, init: true);
        break;
      case true when deepSeaManager:
        Puts("Skipping Deep Sea Events (Deep Sea closed)");
        break;
      case true:
        Puts("Skipping Deep Sea Events (Deep Sea disabled)");
        break;
      default:
        Puts("Skipping Deep Sea Events (no events enabled)");
        break;
    }

    // this will get logged at a lower level
    yield return CreateMonumentEvents();

    Puts("Creating Auto Events");
    yield return CreateAutoEvents();

    Puts($"Startup event creation completed in {(DateTime.UtcNow - startTime).TotalSeconds} seconds");
    _coroutine = null;
  }

  #endregion Startup

  #region General Events

  // coroutine to determine whether any General Events should be created based
  //  on currently existing entities of interest
  // this is expected to only be called on startup
  private IEnumerator CreateGeneralEvents()
  {
    // determine up-front whether there are any general events to create,
    //  because iterating over all net entities is not cheap
    var checkGeneralEvents = false;
    // TODO: Bradley, Patrol Helicopter, Supply Drop, Timed Supply
    checkGeneralEvents |= _configData.GeneralEvents.CargoShip.Enabled;
    // NOTE: StopWhenKilled is checked because we don't want to start events
    //  whose end is determined by a timer, as we don't know elapsed times
    checkGeneralEvents |=
      _configData.GeneralEvents.HackableCrate.Enabled &&
      _configData.GeneralEvents.HackableCrate.StopWhenKilled;
    checkGeneralEvents |= _configData.GeneralEvents.ExcavatorIgnition.Enabled;
    if (checkGeneralEvents)
    {
      foreach (var serverEntity in BaseNetworkable.serverEntities)
      {
        switch (serverEntity)
        {
          // Cargo Ship Event
          case CargoShip cargoShip:
            StartupCargoShip(cargoShip);
            yield return DynamicYield();
            break;
          // Excavator Ignition Event
          case DieselEngine dieselEngine:
            StartupDieselEngine(dieselEngine);
            yield return DynamicYield();
            break;
          // Hackable Crate Event
          case HackableLockedCrate hackableLockedCrate:
            StartupHackableLockedCrate(hackableLockedCrate);
            yield return DynamicYield();
            break;
        }
      }
    }

    yield return DynamicYield(true);
  }

  #region ExcavatorIgnition Event

  // invoke appropriate hook handler for current DieselEngine state
  // this is only used on startup, to (re)create events for already-existing
  //  DieselEngine entities
  private void StartupDieselEngine(DieselEngine dieselEngine)
  {
    if (!dieselEngine)
    {
      PrintDebug("StartupDieselEngine(): DieselEngine is null");
      return;
    }

    if (!_configData.GeneralEvents.ExcavatorIgnition.Enabled)
    {
      PrintDebug("StartupDieselEngine(): Excavator Ignition Event is disabled");
      return;
    }

    if (!dieselEngine.IsOn())
    {
      PrintDebug("StartupDieselEngine(): DieselEngine is off");
      return;
    }

    PrintDebug("StartupDieselEngine(): Found activated Giant Excavator");
    OnDieselEngineToggled(dieselEngine);
  }

  private void OnDieselEngineToggled(DieselEngine dieselEngine)
  {
    if (!dieselEngine || null == dieselEngine.net)
    {
      PrintDebug("OnDieselEngineToggled(): ERROR: Engine or Net is null", DebugLevel.Error);
      return;
    }

    var zoneId = dieselEngine.net.ID.ToString();
    if (dieselEngine.IsOn())
    {
      PrintDebug(
        $"OnDieselEngineToggled(): Requesting 'just-in-case' delete of zoneId={zoneId} due to excavator enable");
      DeleteDynamicZone(zoneId);
      HandleGeneralEvent(
        _configData.GeneralEvents.ExcavatorIgnition, dieselEngine, true);
    }
    else
    {
      PrintDebug($"OnDieselEngineToggled(): Scheduling delete of zoneId={zoneId} due to excavator disable");
      HandleDeleteDynamicZone(zoneId);
    }
  }

  #endregion ExcavatorIgnition Event

  #region HackableLockedCrate Event

  // invoke appropriate hook handler for current HackableLockedCrate state
  // this is only used on startup, to (re)create events for already-existing
  //  HackableLockedCrate entities
  private void StartupHackableLockedCrate(
    HackableLockedCrate hackableLockedCrate)
  {
    if (!hackableLockedCrate)
    {
      PrintDebug("StartupHackableLockedCrate(): HackableLockedCrate is null");
      return;
    }

    var baseEvent = _configData.GeneralEvents.HackableCrate;
    if (!baseEvent.Enabled)
    {
      PrintDebug("StartupHackableLockedCrate(): Hackable Crate Event is disabled");
      return;
    }

    if (!baseEvent.StopWhenKilled)
    {
      PrintDebug("StartupHackableLockedCrate(): Hackable Crate Event doesn't stop when killed");
      return;
    }

    if (0 != hackableLockedCrate.FirstLooterId &&
        baseEvent.TimerStartWhenLooted)
    {
      // looted and stop after time since loot enabled
      // we don't know elapsed time, so err on the side of assuming the event
      //  has already ended
      PrintDebug(
        "StartupHackableLockedCrate(): Found looted hackable locked crate, and TimerStartWhenLooted set; ignoring because elapsed time unknown");
    }
    else if (
      hackableLockedCrate.HasFlag(HackableLockedCrate.Flag_FullyHacked) &&
      baseEvent.TimerStartWhenUnlocked)
    {
      // unlocked and stop after time since unlock enabled
      // we don't know elapsed time, so err on the side of assuming the event
      //  has already ended
      PrintDebug(
        "StartupHackableLockedCrate(): Found unlocked hackable locked crate and TimerStartWhenUnlocked set; ignoring because elapsed time unknown");
    }
    else if (hackableLockedCrate.HasFlag(HackableLockedCrate.Flag_Hacking) &&
             !baseEvent.StartWhenSpawned)
    {
      // hacking and start on hacking enabled
      PrintDebug("StartupHackableLockedCrate(): Found hacking hackable locked crate and StartWhenSpawned NOT set; triggering OnCrateHack()");
      OnCrateHack(hackableLockedCrate);
    }
    else if (baseEvent.StartWhenSpawned)
    {
      // any other state and start on spawn + stop when killed enabled
      PrintDebug("StartupHackableLockedCrate(): Found hackable locked crate, and StartWhenSpawned set; triggering OnEntitySpawned()");
      OnEntitySpawned(hackableLockedCrate);
    }
    else
    {
      PrintDebug(
        "StartupHackableLockedCrate(): Found hackable locked crate, but ignoring because of either start on hack, or stop on timer with elapsed time unknown");
    }
  }

  private void OnEntitySpawned(HackableLockedCrate hackableLockedCrate)
  {
    if (!hackableLockedCrate || null == hackableLockedCrate.net)
    {
      PrintDebug("StartupHackableLockedCrate(): ERROR: Crate or Net is null", DebugLevel.Error);
      return;
    }

    var baseEvent = _configData.GeneralEvents.HackableCrate;
    if (!baseEvent.Enabled || !baseEvent.StartWhenSpawned)
    {
      PrintDebug("OnEntitySpawned(HackableLockedCrate): Ignoring due to event or spawn start disabled");
      return;
    }

    PrintDebug("StartupHackableLockedCrate(): Trying to create hackable crate spawn event");
    NextTick(() => LockedCrateEvent(hackableLockedCrate));
  }

  private void OnCrateHack(HackableLockedCrate hackableLockedCrate)
  {
    if (!hackableLockedCrate || null == hackableLockedCrate.net)
    {
      PrintDebug("OnCrateHack(): ERROR: Crate or Net is null", DebugLevel.Error);
      return;
    }

    PrintDebug("OnCrateHack(): Trying to create hackable crate hack event");
    NextTick(() => LockedCrateEvent(hackableLockedCrate));
  }

  private void OnCrateHackEnd(HackableLockedCrate hackableLockedCrate)
  {
    if (!hackableLockedCrate || null == hackableLockedCrate.net)
    {
      PrintDebug("OnCrateHackEnd(): ERROR: Crate or Net is null", DebugLevel.Error);
      return;
    }

    var zoneId = hackableLockedCrate.net.ID.ToString();
    var duration = _configData.GeneralEvents.HackableCrate.Duration;
    PrintDebug(
      $"OnCrateHackEnd(): Scheduling delete of zoneId={zoneId} in {duration} seconds");
    HandleDeleteDynamicZone(zoneId, duration);
  }

  private void OnLootEntity(
    BasePlayer player, HackableLockedCrate hackableLockedCrate)
  {
    if (!hackableLockedCrate || null == hackableLockedCrate.net)
    {
      PrintDebug("OnLootEntity(HackableLockedCrate): ERROR: Crate or Net is null", DebugLevel.Error);
      return;
    }

    var baseEvent = _configData.GeneralEvents.HackableCrate;
    if (!baseEvent.Enabled || !baseEvent.TimerStartWhenLooted)
    {
      PrintDebug("OnLootEntity(HackableLockedCrate): Ignoring due to event or loot delay disabled");
      return;
    }

    var zoneId = hackableLockedCrate.net.ID.ToString();
    var duration = baseEvent.Duration;
    PrintDebug(
      $"OnLootEntity(HackableLockedCrate): Scheduling delete of zoneId={zoneId} in {duration} seconds");
    HandleDeleteDynamicZone(zoneId, duration);
  }

  private void OnEntityKill(HackableLockedCrate hackableLockedCrate)
  {
    if (!hackableLockedCrate || null == hackableLockedCrate.net)
    {
      PrintDebug("OnEntityKill(HackableLockedCrate): ERROR: Crate or Net is null", DebugLevel.Error);
      return;
    }

    var baseEvent = _configData.GeneralEvents.HackableCrate;
    if (!baseEvent.Enabled || !baseEvent.StopWhenKilled)
    {
      PrintDebug("OnEntityKill(HackableLockedCrate): Ignoring due to event or kill stop disabled");
      return;
    }

    var zoneId = hackableLockedCrate.net.ID.ToString();
    if (!_activeDynamicZones.ContainsKey(zoneId))
    {
      // no active zone for this hackable locked crate
      return;
    }

    // untether everything that we may have parented to the
    //  HackableLockedCrate so that they don't get killed along with it
    ZM_GetZoneByID(zoneId)?.transform.SetParent(null, true);
    ParentDome(zoneId, Vector3.zero);

    //When the timer starts, don't stop the event immediately
    if (_eventTimers.ContainsKey(zoneId))
    {
      PrintDebug(
        $"OnEntityKill(HackableLockedCrate): Ignoring due to event timer already active for zoneId={zoneId}");
      return;
    }

    PrintDebug($"OnEntityKill(HackableLockedCrate): Scheduling delete of zoneId={zoneId}");
    HandleDeleteDynamicZone(zoneId);
  }

  private void LockedCrateEvent(HackableLockedCrate hackableLockedCrate)
  {
    if (!CheckEntityOwner(hackableLockedCrate))
    {
      return;
    }

    var baseEvent = _configData.GeneralEvents.HackableCrate;
    if (baseEvent.ExcludeOilRig && IsOnTheOilRig(hackableLockedCrate))
    {
      PrintDebug("LockedCrateEvent(): The hackable locked crate is on an oil rig. Skipping event creation.");
      return;
    }

    if (baseEvent.ExcludeCargoShip && IsOnTheCargoShip(hackableLockedCrate))
    {
      PrintDebug("LockedCrateEvent(): The hackable locked crate is on a cargo ship. Skipping event creation.");
      return;
    }

    if (baseEvent.ExcludeGhostShip && IsOnGhostShip(hackableLockedCrate))
    {
      PrintDebug("LockedCrateEvent(): The hackable locked crate is on a ghost ship. Skipping event creation.");
      return;
    }

    // call this here, because otherwise it's difficult to ensure that we call
    //  it exactly once
    if (!CanCreateDynamicPVP(baseEvent.GetName(), hackableLockedCrate))
    {
      return;
    }

    // NOTE: we are already NextTick() protected here
    HandleParentedEntityEvent(
      baseEvent, hackableLockedCrate, parentOnCreate: true);
  }

  private static bool IsOnTheCargoShip(
    HackableLockedCrate hackableLockedCrate) =>
    true == hackableLockedCrate?.transform.parent?.HasComponent<CargoShip>();

  private static bool IsOnGhostShip(HackableLockedCrate hackableLockedCrate) =>
    true == hackableLockedCrate?.transform.parent?.HasComponent<GhostShip>();

  private bool IsOnTheOilRig(HackableLockedCrate hackableLockedCrate)
  {
    // this may now get called before monument event creation if hackable
    //  crates exist on startup, so populate oilrig positions here if needed
    if (Vector3.zero == _oilRigPosition ||
        Vector3.zero == _largeOilRigPosition)
    {
      foreach (LandmarkInfo landmarkInfo in TerrainMeta.Path.Landmarks)
      {
        switch (landmarkInfo.name)
        {
          case PrefabLargeOilRig:
            _largeOilRigPosition = landmarkInfo.transform.position;
            break;
          case PrefabOilRig:
            _oilRigPosition = landmarkInfo.transform.position;
            break;
        }

        if (Vector3.zero != _oilRigPosition &&
            Vector3.zero != _largeOilRigPosition)
        {
          break;
        }
      }
    }

    if (_oilRigPosition != Vector3.zero && Vector3Ex.Distance2D(
          hackableLockedCrate.transform.position, _oilRigPosition) < 50f)
    {
      return true;
    }

    if (_largeOilRigPosition != Vector3.zero && Vector3Ex.Distance2D(
          hackableLockedCrate.transform.position, _largeOilRigPosition) < 50f)
    {
      return true;
    }

    return false;
  }

  #endregion HackableLockedCrate Event

  #region PatrolHelicopter And BradleyAPC Event

  private void OnEntityDeath(PatrolHelicopter patrolHelicopter, HitInfo info)
  {
    if (!patrolHelicopter || null == patrolHelicopter.net)
    {
      return;
    }

    PatrolHelicopterEvent(patrolHelicopter);
  }

  private void OnEntityDeath(BradleyAPC bradleyApc, HitInfo info)
  {
    if (!bradleyApc || null == bradleyApc.net)
    {
      return;
    }

    BradleyApcEvent(bradleyApc);
  }

  private void PatrolHelicopterEvent(PatrolHelicopter patrolHelicopter)
  {
    var baseEvent = _configData.GeneralEvents.PatrolHelicopter;
    if (!baseEvent.Enabled)
    {
      return;
    }

    PrintDebug("PatrolHelicopterEvent(): Trying to create Patrol Helicopter killed event.");
    if (!CheckEntityOwner(patrolHelicopter))
    {
      return;
    }

    HandleGeneralEvent(baseEvent, patrolHelicopter, useEntityId: false);
  }

  private void BradleyApcEvent(BradleyAPC bradleyApc)
  {
    var baseEvent = _configData.GeneralEvents.BradleyApc;
    if (!baseEvent.Enabled)
    {
      return;
    }

    PrintDebug("BradleyApcEvent(): Trying to create Bradley APC killed event.");
    if (!CheckEntityOwner(bradleyApc))
    {
      return;
    }

    HandleGeneralEvent(baseEvent, bradleyApc, useEntityId: false);
  }

  #endregion PatrolHelicopter And BradleyAPC Event

  #region SupplyDrop And SupplySignal Event

  // stores NetIDs of active CargoPlane and SupplyDrop instances spawned by a
  //  SupplySignal
  private readonly HashSet<NetworkableId> _activeSignaledPlanesAndDrops = new();

  private void OnCargoPlaneSignaled(
    CargoPlane plane, SupplySignal signal) => NextTick(() =>
  {
    if (!plane || !signal)
    {
      return;
    }

    // record that plane was signaled
    var planeID = plane.net.ID;
    if (!_activeSignaledPlanesAndDrops.Add(planeID))
    {
      return;
    }

    PrintDebug($"OnCargoPlaneSignaled(): Supply Signal {signal.net.ID} spawned Cargo Plane {planeID} with drop position {plane.dropPosition}");
  });

  private void OnSupplyDropDropped(
    BaseEntity entity, CargoPlane plane) => NextTick(() =>
  {
    var supplyDrop = entity as SupplyDrop;
    if (!supplyDrop || null == supplyDrop.net || !plane || null == plane.net)
    {
      return;
    }

    // if plane was signaled, record drop as signaled too
    var planeID = plane.net.ID;
    var supplyDropID = supplyDrop.net.ID;
    if (_activeSignaledPlanesAndDrops.Contains(planeID))
    {
      _activeSignaledPlanesAndDrops.Add(supplyDropID);
      PrintDebug($"OnSupplyDropDropped(): Signaled Cargo Plane {planeID} spawned Supply Drop {supplyDropID} at position {supplyDrop.transform.position}");
    }
    else
    {
      // must be a timed drop rather than a signaled one
      PrintDebug($"OnSupplyDropDropped(): Timed Cargo Plane {planeID} spawned Supply Drop {supplyDropID} at position {supplyDrop.transform.position}");
    }

    HandleSupplyDropEvent(supplyDrop, false);
  });

  private void OnSupplyDropLanded(SupplyDrop supplyDrop) => NextTick(() =>
  {
    if (!supplyDrop || null == supplyDrop.net)
    {
      return;
    }

    var zoneId = supplyDrop.net.ID.ToString();
    if (_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
    {
      // event was already created on spawn; parent the event to the entity, so
      //  that they move together
      // NOTES:
      // - don't delete on failure, because leaving the existing zone on the
      //    ground is better than deleting it
      // - no need to delay parenting, as the zone presumably has already
      //    existed for a bit
      ParentEventToEntity(
        zoneId, baseEvent, supplyDrop, deleteOnFailure: false, delay: false);
      return;
    }

    HandleSupplyDropEvent(supplyDrop, true);
  });

  private void OnLootEntity(BasePlayer _, SupplyDrop supplyDrop)
  {
    if (!supplyDrop || null == supplyDrop.net)
    {
      return;
    }

    var zoneId = supplyDrop.net.ID.ToString();
    if (!_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
    {
      // no active zone for this supply drop
      return;
    }

    if (baseEvent is not SupplyDropEvent supplyDropEvent)
    {
      // pathological
      PrintDebug($"OnLootEntity(): Unknown SupplyDrop eventName={baseEvent.GetName()} for zoneId={zoneId}", DebugLevel.Warning);
      return;
    }

    if (!supplyDropEvent.Enabled || !supplyDropEvent.TimerStartWhenLooted)
    {
      return;
    }

    HandleDeleteDynamicZone(zoneId, supplyDropEvent.Duration);
  }

  private void OnEntityKill(SupplyDrop supplyDrop)
  {
    if (!supplyDrop || null == supplyDrop.net)
    {
      PrintDebug("OnEntityKill(SupplyDrop): ERROR: Drop or Net is null", DebugLevel.Error);
      return;
    }

    var supplyDropID = supplyDrop.net.ID;
    _activeSignaledPlanesAndDrops.Remove(supplyDropID);

    var zoneId = supplyDropID.ToString();
    if (!_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
    {
      // no active zone for this supply drop
      return;
    }

    if (baseEvent is not SupplyDropEvent supplyDropEvent)
    {
      // pathological
      PrintDebug($"OnEntityKill(SupplyDrop): Unknown SupplyDrop eventName={baseEvent.GetName()} for zoneId={zoneId}", DebugLevel.Warning);
      return;
    }

    // untether everything that we may have parented to the SupplyDrop so that
    //  they don't get killed along with it
    ZM_GetZoneByID(zoneId)?.transform.SetParent(null, true);
    ParentDome(zoneId, Vector3.zero);

    if (supplyDropEvent is not { Enabled: true } ||
        !supplyDropEvent.StopWhenKilled)
    {
      return;
    }

    // When the timer starts, don't stop the event immediately
    if (_eventTimers.ContainsKey(zoneId))
    {
      PrintDebug(
        $"OnEntityKill(SupplyDrop): Ignoring due to event timer already active for zoneId={zoneId}");
      return;
    }

    PrintDebug($"OnEntityKill(SupplyDrop): Scheduling delete of zoneId={zoneId}");
    HandleDeleteDynamicZone(zoneId);
  }

  private void OnEntityKill(CargoPlane plane)
  {
    if (!plane || null == plane.net)
    {
      return;
    }

    _activeSignaledPlanesAndDrops.Remove(plane.net.ID);
  }

  private static string GetSupplyDropStateName(bool isLanded) =>
    isLanded ? "Landed" : "Spawned";

  private void HandleSupplyDropEvent(SupplyDrop supplyDrop, bool isLanded)
  {
    if (!supplyDrop || null == supplyDrop.net)
    {
      return;
    }

    PrintDebug(
      $"HandleSupplyDropEvent(): Trying to create supply drop {GetSupplyDropStateName(isLanded)} event at {supplyDrop.transform.position}.");
    if (!CheckEntityOwner(supplyDrop))
    {
      return;
    }

    var supplyDropID = supplyDrop.net.ID;
    var isSignaled = _activeSignaledPlanesAndDrops.Contains(supplyDropID);
    if (isSignaled)
    {
      PrintDebug($"HandleSupplyDropEvent(): Supply Drop {supplyDropID} is from supply signal");
      HandleSupplyDropEvent(
        _configData.GeneralEvents.SupplySignal, isLanded, supplyDrop);
    }
    else
    {
      PrintDebug($"HandleSupplyDropEvent(): Supply Drop {supplyDropID} is from timed event");
      HandleSupplyDropEvent(
        _configData.GeneralEvents.TimedSupply, isLanded, supplyDrop);
    }
  }

  private void HandleSupplyDropEvent(
    SupplyDropEvent supplyDropEvent, bool isLanded, SupplyDrop supplyDrop)
  {
    if (!supplyDropEvent.Enabled)
    {
      PrintDebug($"HandleSupplyDropEvent(): Skipping event creation because supply drop event type is disabled: {supplyDropEvent}");
      return;
    }

    // abort if:
    // - landed but configured to start on spawn, or
    // - not landed but configured to start on landing
    if (isLanded == supplyDropEvent.StartWhenSpawned)
    {
      PrintDebug($"HandleSupplyDropEvent(): Skipping event creation because supply drop event state is disabled: {GetSupplyDropStateName(isLanded)}");
      return;
    }

    if (!CanCreateDynamicPVP(supplyDropEvent.GetName(), supplyDrop))
    {
      return;
    }

    HandleParentedEntityEvent(
      supplyDropEvent, supplyDrop, parentOnCreate: isLanded);
  }

  #endregion SupplyDrop And SupplySignal Event

  #region CargoShip Event

  private bool IsLeavingHarbor(CargoShip cargoShip)
  {
    // abort if ship invalid, not doing approach sequence, or currently docked
    if (
      !cargoShip || !cargoShip.isDoingHarborApproach || cargoShip.IsShipDocked)
    {
      return false;
    }

    // abort with error if path object or node list is missing
    if (null == cargoShip.harborApproachPath?.nodes)
    {
      PrintError($"IsLeavingHarbor(): ERROR: Cargo ship at {cargoShip.transform.position} is on harbor docking sequence, but the path data is null");
      return false;
    }

    // scan the remaining approach path nodes to see if the docking node is
    //  still coming up
    for (var i = cargoShip.currentHarborApproachNode;
         i < cargoShip.harborApproachPath.nodes.Count;
         ++i)
    {
      if (cargoShip.harborApproachPath.nodes[i].maxVelocityOnApproach == 0.0f)
      {
        // this is the docking node, so it must still be approaching
        return false;
      }
    }
    // scanned all remaining nodes without finding the docking one, so it must
    //  have already docked and is leaving
    return true;
  }

  // invoke appropriate hook handler for current CargoShip state
  // this is only used on startup, to (re)create events for already-existing
  //  CargoShip entities
  private void StartupCargoShip(CargoShip cargoShip)
  {
    if (!cargoShip)
    {
      PrintDebug("StartupCargoShip(): CargoShip is null");
      return;
    }

    if (!_configData.GeneralEvents.CargoShip.Enabled)
    {
      PrintDebug("StartupCargoShip(): Cargo Ship Event is disabled");
      return;
    }

    if (cargoShip.HasFlag(CargoShip.Egressing))
    {
      // leaving the map
      PrintDebug("StartupCargoShip(): Found cargo ship in egress from the map");
      OnCargoShipEgress(cargoShip);
    }
    else if (cargoShip.isDoingHarborApproach)
    {
      // approaching, docked at, or leaving a Harbor
      if (cargoShip.IsShipDocked)
      {
        // docked at a Harbor
        PrintDebug("StartupCargoShip(): Found cargo ship docked at a Harbor");
        OnCargoShipHarborArrived(cargoShip);
      }
      else if (IsLeavingHarbor(cargoShip))
      {
        // leaving a Harbor
        PrintDebug("StartupCargoShip(): Found cargo ship leaving a Harbor");
        OnCargoShipHarborLeave(cargoShip);
      }
      else
      {
        // approaching a Harbor
        PrintDebug("StartupCargoShip(): Found cargo ship approaching a Harbor");
        OnCargoShipHarborApproach(cargoShip);
      }
    }
    else if (cargoShip.HasFlag(CargoShip.HasDocked))
    {
      // not doing anything of interest, but has previously docked, so treat as
      //  leaving harbor since that was the last state change of interest
      PrintDebug("StartupCargoShip(): Found cargo ship that has previously docked at a Harbor");
      OnCargoShipHarborLeave(cargoShip);
    }
    else
    {
      // not doing anything of interest, so treat as a normal spawn
      PrintDebug("StartupCargoShip(): Found cargo ship that has not approached a Harbor");
      OnEntitySpawned(cargoShip);
    }
  }

  // create/update or attempt to delete CargoShip event zone, based on
  //  specified desired state
  private void HandleCargoState(CargoShip cargoShip, bool state)
  {
    if (!cargoShip || null == cargoShip.net)
    {
      return;
    }

    var baseEvent = _configData.GeneralEvents.CargoShip;
    if (!baseEvent.Enabled)
    {
      return;
    }

    // create/update or attempt to delete zone, based on desired state
    var zoneId = cargoShip.net.ID.ToString();
    var zoneExists = _activeDynamicZones.ContainsKey(zoneId);
    if (zoneExists == state)
    {
      PrintDebug($"HandleCargoState(): CargoShip event {zoneId} is already in desired state={state}");
      return;
    }

    if (state)
    {
      PrintDebug($"HandleCargoState(): Trying to create CargoShip post-spawn event {zoneId}");
      if (!CheckEntityOwner(cargoShip))
      {
        return;
      }

      // call this here, because otherwise it's difficult to ensure that we
      //  call it exactly once
      if (!CanCreateDynamicPVP(baseEvent.GetName(), cargoShip))
      {
        return;
      }

      NextTick(() =>
        HandleParentedEntityEvent(baseEvent, cargoShip, parentOnCreate: true));
    }
    else
    {
      PrintDebug($"HandleCargoState(): Trying to delete CargoShip post-spawn event {zoneId}");
      HandleDeleteDynamicZone(zoneId);
    }
  }

  private void OnEntitySpawned(CargoShip cargoShip)
  {
    if (!cargoShip || null == cargoShip.net)
    {
      // bad entity
      return;
    }

    var baseEvent = _configData.GeneralEvents.CargoShip;
    if (!baseEvent.Enabled || !baseEvent.SpawnState)
    {
      // not configured to create event on spawn
      return;
    }

    PrintDebug("OnEntitySpawned(CargoShip): Trying to create CargoShip spawn event");
    if (!CheckEntityOwner(cargoShip))
    {
      return;
    }

    // call this here, because otherwise it's difficult to ensure that we call
    //  it exactly once
    if (!CanCreateDynamicPVP(baseEvent.GetName(), cargoShip))
    {
      return;
    }

    NextTick(() =>
      HandleParentedEntityEvent(baseEvent, cargoShip, parentOnCreate: true));
  }

  private void OnEntityKill(CargoShip cargoShip)
  {
    if (!cargoShip || null == cargoShip.net)
    {
      return;
    }

    if (!_configData.GeneralEvents.CargoShip.Enabled)
    {
      return;
    }

    HandleDeleteDynamicZone(cargoShip.net.ID.ToString());
  }

  private void OnCargoShipEgress(CargoShip cargoShip) =>
    HandleCargoState(
      cargoShip, _configData.GeneralEvents.CargoShip.EgressState);

  private void OnCargoShipHarborApproach(CargoShip cargoShip) =>
    HandleCargoState(
      cargoShip, _configData.GeneralEvents.CargoShip.ApproachState);

  private void OnCargoShipHarborArrived(CargoShip cargoShip) =>
    HandleCargoState(
      cargoShip, _configData.GeneralEvents.CargoShip.DockState);

  private void OnCargoShipHarborLeave(CargoShip cargoShip) =>
    HandleCargoState(
      cargoShip, _configData.GeneralEvents.CargoShip.DepartState);

  #endregion CargoShip Event

  #endregion General Events

  #region Deep Sea Events

  private bool AnySeepSeaEventEnabled() =>
    true == _configData?.DeepSeaEvents.GhostShip.Enabled ||
    true == _configData?.DeepSeaEvents.DeepSeaIsland.Enabled ||
    true == _configData?.DeepSeaEvents.IslandCannon.Enabled;

  private static bool IsDeepSeaEvent(GeneralEventType eventType) => eventType is
    GeneralEventType.GhostShip or
    GeneralEventType.DeepSeaIsland or
    GeneralEventType.IslandCannon;

  private static bool IsDeepSeaOpen(DeepSeaManager deepSeaManager) =>
    true == deepSeaManager?.IsOpen();

  private void OnDeepSeaOpened(DeepSeaManager deepSeaManager) => NextTick(() =>
  {
    TryStartCoroutine(CoroutineTypes.CreateDeepSeaEvents, deepSeaManager);
  });

  // use "closed" instead of "close" hook for now, as trying to tear down Deep
  //  Sea events while the server is also trying to tear down the Deep Sea
  //  itself may cause more server FPS drop
  private void OnDeepSeaClosed(DeepSeaManager deepSeaManager) => NextTick(() =>
  {
    TryStartCoroutine(CoroutineTypes.DeleteDeepSeaEvents, deepSeaManager);
  });

  // coroutine method to create all Deep Sea Events on plugin load or on Deep
  //  Sea open
  private IEnumerator CreateDeepSeaEvents(
    DeepSeaManager deepSeaManager, bool delay, bool init)
  {
    var startTime = DateTime.UtcNow;

    Puts("Creating Deep Sea Events");

    var total = 0;
    var sinceSleep = 0;
    var deepSeaEntities = Pool.Get<HashSet<BaseEntity>>();
    deepSeaManager.GetAllDeepSeaEntities(deepSeaEntities);
    yield return DynamicYield();
    foreach (var entity in deepSeaEntities)
    {
      switch (entity)
      {
        case Cannon cannon:
          CreateCannonEvent(cannon, delay);
          yield return DynamicYield();
          break;
        case GhostShip ghostShip:
          CreateGhostShipEvent(ghostShip, delay);
          yield return DynamicYield();
          break;
        case DeepSeaIsland deepSeaIsland:
          CreateDeepSeaIslandEvent(deepSeaIsland, delay);
          yield return DynamicYield();
          break;
      }

      // also log+sleep every 250 entities
      if (++sinceSleep < 250) continue;
      total += sinceSleep;
      sinceSleep = 0;
      Puts($"...{total}/{deepSeaEntities.Count} Deep Sea entities scanned...");
      yield return DynamicYield();
    }
    total += sinceSleep;
    Puts($"...Finished scanning {total}/{deepSeaEntities.Count} Deep Sea entities");
    Pool.FreeUnmanaged(ref deepSeaEntities);

    // don't log timer or null out coroutine on startup, because it runs as part
    //  of a larger startup coroutine
    if (init) yield break;

    Puts($"Deep Sea Events creation completed in {(DateTime.UtcNow - startTime).TotalSeconds} seconds");
    _coroutine = null;
  }

  // delete all Deep Sea Events in a coroutine (e.g. on Deep Sea close)
  // NOTE: global event deletion logic will take care of them in Unload()
  private IEnumerator DeleteDeepSeaEvents()
  {
    var startTime = DateTime.UtcNow;
    Puts("Deleting Deep Sea Events");

    foreach (var zoneID in _potentialDeepSeaZones)
    {
      HandleDeleteDynamicZone(zoneID);
      yield return DynamicYield();
    }
    _potentialDeepSeaZones.Clear();

    Puts($"Deep Sea Events deletion completed in {(DateTime.UtcNow - startTime).TotalSeconds} seconds");
    _coroutine = null;
  }

  #region GhostShip Event

  private void CreateGhostShipEvent(GhostShip ghostShip, bool delay)
  {
    if (!ghostShip || null == ghostShip.net)
    {
      PrintDebug("CreateGhostShipEvent(): ERROR: GhostShip or Net is null", DebugLevel.Error);
      return;
    }

    var baseEvent = _configData.DeepSeaEvents.GhostShip;
    if (!baseEvent.Enabled)
    {
      PrintDebug("CreateGhostShipEvent(): Ignoring due to event disabled");
      return;
    }

    PrintDebug("CreateGhostShipEvent(): Trying to create Ghost Ship event");
    if (!CheckEntityOwner(ghostShip))
    {
      return;
    }

    // center event at sea level
    HandleGeneralEvent(
      baseEvent, ghostShip, useEntityId: true, delay, EventHeightStrategy.Zero);
  }

  #endregion GhostShip Event

  #region DeepSeaIsland Event

  private void CreateDeepSeaIslandEvent(DeepSeaIsland deepSeaIsland, bool delay)
  {
    if (!deepSeaIsland || null == deepSeaIsland.net)
    {
      PrintDebug("CreateDeepSeaIslandEvent(): ERROR: DeepSeaIsland or Net is null", DebugLevel.Error);
      return;
    }

    var baseEvent = _configData.DeepSeaEvents.DeepSeaIsland;
    if (!baseEvent.Enabled)
    {
      PrintDebug("CreateDeepSeaIslandEvent(): Ignoring due to event disabled");
      return;
    }

    PrintDebug("CreateDeepSeaIslandEvent(): Trying to create Deep Sea Island event");
    if (!CheckEntityOwner(deepSeaIsland))
    {
      return;
    }

    // TODO: move event-specific logic below to polymorphic event class
    //  hierarchy and have HandleGeneralEvent() call it, so that we don't have
    //  to maintain this fork
    var eventName = baseEvent.GetName();
    var zoneId = deepSeaIsland.net?.ID.ToString();
    if (string.IsNullOrEmpty(zoneId))
    {
      PrintDebug($"CreateDeepSeaIslandEvent(): Aborting creation of eventName={eventName}, because entity is null", DebugLevel.Warning);
      return;
    }
    if (_activeDynamicZones.ContainsKey(zoneId))
    {
      PrintDebug($"CreateDeepSeaIslandEvent(): Aborting creation of redundant eventName={eventName} for DeepSeaIsland={deepSeaIsland} with baseEntity.net.ID={zoneId}", DebugLevel.Warning);
      return;
    }
    if (!CanCreateDynamicPVP(eventName, deepSeaIsland))
    {
      return;
    }

    var (transform, position, radius) = GetIslandGeometry(deepSeaIsland);

    // also calculate a zone volume if auto-geo is enabled (otherwise
    //  user-specified volume is used)
    var dynamicZone = baseEvent.GetDynamicZone();
    if (dynamicZone is SphereCubeAutoGeoDynamicZone { DoAutoGeo: true } geo)
    {
      geo.Radius = radius;
      geo.Size = Vector3.zero; // disable cuboid zone
    }

    if (!_potentialDeepSeaZones.Add(zoneId))
    {
      return;
    }

    CreateDynamicZone(
      baseEvent, position, zoneId, dynamicZone.ZoneSettings(transform), delay);
  }

  #endregion DeepSeaIsland Event

  #region IslandCannon Event

  private void CreateCannonEvent(Cannon cannon, bool delay)
  {
    if (!cannon || null == cannon.net)
    {
      PrintDebug("CreateCannonEvent(): ERROR: Cannon or Net is null", DebugLevel.Error);
      return;
    }

    var baseEvent = _configData.DeepSeaEvents.IslandCannon;
    if (!baseEvent.Enabled)
    {
      PrintDebug("CreateCannonEvent(): Ignoring due to event disabled");
      return;
    }

    // only create zones for static land-based cannons
    var cannonPrefab = cannon.ShortPrefabName;
    if ("cannon.land.static" != cannonPrefab)
    {
      // also suppress debug log for deployed cannons (e.g. on player boats)
      if ("cannon.deployed" != cannonPrefab)
      {
        PrintDebug($"CreateCannonEvent(): Ignoring unsupported Cannon prefab '{cannonPrefab}'");
      }
      return;
    }

    PrintDebug("CreateCannonEvent(): Trying to create Island Cannon event");
    if (!CheckEntityOwner(cannon))
    {
      return;
    }

    HandleGeneralEvent(baseEvent, cannon, true, delay);
  }

  #endregion IslandCannon Event

  #endregion Deep Sea Events

  #region Monument Events

  #region Automatic Geometry

  private static bool IsCustomPreventBuildingPrefab(string name) =>
    name is "assets/bundled/prefabs/modding/volumes_and_triggers/prevent_building_cube.prefab"
      or "assets/bundled/prefabs/modding/volumes_and_triggers/prevent_building_sphere.prefab";

  // return Prevent Building collider that engulfs the given location, or null
  //  if none found
  // this only works for custom monuments, as vanilla ones hide their volumes
  //  within their monument transform/component trees
  // credit: WhiteThunder (Monument Finder)
  private Collider GetPreventBuildingCollider(Vector3 position)
  {
    var position2D = position.XZ2D();
    var count = Physics.OverlapSphereNonAlloc(
      position, 1, _colliderBuffer, Rust.Layers.Mask.Prevent_Building,
      QueryTriggerInteraction.Ignore);

    // scan discovered colliders (if any) and choose in decreasing priority
    //  order: closest, largest, first
    var mDist = float.MaxValue;
    var mSize = 0.0f;
    var mIndex = -1;
    for (var i = 0; i < count; ++i)
    {
      var collider = _colliderBuffer[i];
      // skip non-box/sphere colliders
      if (collider is not BoxCollider && collider is not SphereCollider)
      {
        continue;
      }
      // skip non-modding PB prefabs
      if (!IsCustomPreventBuildingPrefab(collider.name)) continue;
      // check 2D distance from reference position
      var center2D = GetCenter(collider).XZ2D();
      var dist = Vector2.Distance(position2D, center2D);
      var size = GetSize(collider);
      // choose closer colliders
      if (dist < mDist)
      {
        mDist = dist;
        mSize = size;
        mIndex = i;
        continue;
      }
      // skip more distance colliders
      if (dist > mDist) continue;
      // skip smaller or equal-but-later colliders
      if (size <= mSize) continue;
      // choose larger colliders
      mDist = dist;
      mSize = size;
      mIndex = i;
    }

    return mIndex >= 0 ? _colliderBuffer[mIndex] : null;
  }

  // get center of box or sphere collider, or collider's transform position if
  //  something else
  private static Vector3 GetCenter(Collider collider)
  {
    return collider switch
    {
      BoxCollider b => collider.transform.TransformPoint(b.center),
      SphereCollider s => collider.transform.TransformPoint(s.center),
      _ => collider.transform.position
    };
  }

  // get size of box or sphere collider, or zero if something else
  private static float GetSize(Collider collider)
  {
    return collider switch
    {
      BoxCollider b => b.size.magnitude,
      SphereCollider s => s.radius,
      _ => 0f
    };
  }

  // return most suitable Prevent Building collider that is attached somewhere
  //  under the given transform's child tree, or null if none found
  // this only works for vanilla monuments, as custom ones are global
  private static Collider GetPreventBuildingCollider(Transform transform)
  {
    if (!transform) return null;

    var list = Pool.Get<List<Collider>>();
    // prefer monument marker oriented bounds if available
    if (transform.TryGetComponent<MonumentInfo>(out var monumentInfo))
    {
      Vis.Colliders(
        monumentInfo.obbBounds, list, Rust.Layers.Mask.Prevent_Building);
    }
    else
    {
      // fall back to transform world bounds
      var tSize = transform.GetBounds().size.Max();
      if (tSize <= 0.0f)
      {
        // use a default hard-coded size (this seems to not trigger)
        tSize = 5.0f;
      }
      Vis.Colliders(
        transform.position, tSize, list, Rust.Layers.Mask.Prevent_Building);
    }
    // scan the list for optimal collider
    Collider mCollider = null;
    var mSize = 0.0f;
    var mDist = float.MaxValue;
    foreach (var collider in list)
    {
      // ignore if not under the main transform's child tree
      if (!collider.transform.IsChildOf(transform)) continue;
      var cSize = GetSize(collider);
      // ignore if unknown shape or zero size
      if (cSize <= 0) continue;
      // record if no previous candidate, or larger than previous candidate
      if (!mCollider || cSize > mSize)
      {
        mCollider = collider;
        mSize = cSize;
        mDist = Vector3.Distance(transform.position, GetCenter(collider));
        continue;
      }
      // ignore if smaller than largest found so far
      if (cSize < mSize) continue;
      // same size - choose the one closer to main transform
      var cCenter = GetCenter(collider);
      var cDist = Vector3.Distance(transform.position, cCenter);
      if (cDist >= mDist) continue;
      mCollider = collider;
      mSize = cSize;
      mDist = cDist;
    }
    Pool.FreeUnmanaged(ref list);

    return mCollider;
  }

  // return the largest Prevent Building collider found for the given monument
  //  transform and type, or null if none found
  private Collider GetPreventBuildingCollider(
    Transform transform, bool customMonument)
  {
    if (!transform) return null;

    return customMonument ?
      GetPreventBuildingCollider(transform.position) :
      GetPreventBuildingCollider(transform);
  }

  // apply Collider data to the given DynamicPVP zone parameters
  // used by GetPreventBuildingParams() to apply values before returning
  private static (Vector3 size, float rotation, Vector3 offset)
    ApplyColliderData(
      Collider collider, Vector3 size, float rotation, Vector3 offset)
  {
    if (!collider) return (Vector3.zero, 0.0f, Vector3.zero);
    switch (collider)
    {
      case BoxCollider b:
      {
        size.Scale(b.size);
        offset += b.center;
        break;
      }
      case SphereCollider s:
      {
        size *= s.radius;
        offset += s.center;
        break;
      }
      default:
      {
        return (Vector3.zero, 0.0f, Vector3.zero);
      }
    }
    return (size, rotation, offset);
  }

  // get cumulative size, rotation, offset of prevent building volume relative
  //  to given monument transform
  // if global=false, monument rotation is subtracted from volume rotation
  //  prior to normalizing (NOTE: custom monuments have global PB colliders,
  //  while vanilla ones attach them to their transform hierarchies)
  // returns all zeroes if something went wrong
  private static (Vector3 size, float rotation, Vector3 offset)
  GetPreventBuildingParams(Collider collider, Transform tMonument, bool global)
  {
    if (!collider || !tMonument)
    {
      return (Vector3.zero, 0.0f, Vector3.zero);
    }

    // collect the parent transform hierarchy into a list
    // for global colliders this will only collect the parent, but for all
    //  others it will collect everything below the root of the hierarchy
    var tList = Pool.Get<List<Transform>>();
    for (var transform = collider.transform;
         transform && transform != tMonument;
         transform = transform.parent)
    {
      tList.Add(transform);
      if (global) break;
    }
    // now walk the list backwards, accumulating offset+rotation+size values
    var offset = Vector3.zero;
    var rotation = Quaternion.identity;
    var size = Vector3.one;
    for (var i = tList.Count - 1; i >= 0; --i)
    {
      var transform = tList[i];
      offset += rotation * transform.localPosition;
      rotation *= transform.localRotation;
      size.Scale(transform.localScale);
    }
    Pool.FreeUnmanaged(ref tList);

    if (global)
    {
      // we actually calculated the world position/rotation for a global PB
      //  volume, so calculate the offset from the custom monument transform
      offset -= tMonument.position;
    }

    // apply collider's non-transform size and offset values and return that
    return ApplyColliderData(collider, size, rotation.eulerAngles.y, offset);

/* TODO: Is it worth cleaning up excessive rotations?
      // normalize rotation to range [0, 90)
      // confine to [0, 180) by flipping +/- 180 degrees as needed
      while (rotation >= 180.0f) rotation -= 180.0f;
      while (rotation < 0) rotation += 180.0f;
      if (rotation >= 90.0f)
      {
        // confine to [0, 90) by swapping x and y
        rotation -= 90.0f;
        (size.x, size.z) = (size.z, size.x);
      }
*/
  }

  // get DynamicPVP event zone geometry (radius/size, rotation, offset) for
  //  given monument info, or all zeroes if not found
  // only one of radius or size can be nonzero, depending on whether a sphere
  //  or box volume was found, respectively
  private (float radius, Vector3 size, float rotation, Vector3 offset)
  GetPreventBuildingParams(Transform monumentTransform, bool customMonument)
  {
    var pbCollider = GetPreventBuildingCollider(
      monumentTransform, customMonument);
    if (!pbCollider) return (0.0f, Vector3.zero, 0.0f, Vector3.zero);
    var (size, rotation, offset) =
      GetPreventBuildingParams(pbCollider, monumentTransform, customMonument);
    if (Vector3.zero == size)
    {
      return (0.0f, Vector3.zero, 0.0f, Vector3.zero);
    }
    return pbCollider switch
    {
      BoxCollider => (0.0f, size, rotation, offset),
      SphereCollider => (size.Max(), Vector3.zero, rotation, offset),
      _ => (0.0f, Vector3.zero, 0.0f, Vector3.zero)
    };
  }

  // get DynamicPVP monument event zone geometry parameters for an Underwater
  //  Labs related transform
  // returns a volume encompassing all Underwater Labs PB volumes, with offset
  //  from the given transform
  private (float radius, Vector3 size, float rotation, Vector3 offset)
  GetLabLinkParams(Transform transform)
  {
    // get the Underwater Labs landmark at this location
    DungeonBaseInfo dungeonBaseInfo = TerrainMeta.Path.FindClosest(
      TerrainMeta.Path.DungeonBaseEntrances, transform.position);
    // scan all Underwater Labs modules to find the ones near this landmark
    Bounds bounds = new();
    var first = true;
    foreach (DungeonBaseLink linkI in TerrainMeta.Path.DungeonBaseLinks)
    {
      // get the closest Underwater Labs landmark to this link
      // this is used instead of dungeonLink.Dungeon in case this is a wacky
      //  custom map with scrambled data
      DungeonBaseInfo linkDungeon = TerrainMeta.Path.FindClosest(
        TerrainMeta.Path.DungeonBaseEntrances, linkI.transform.position);
      // ignore links that are closest to a different landmark (handles the
      //  case of multiple Underwater Labs monuments on a custom map)
      if (dungeonBaseInfo != linkDungeon) continue;

      // apparently the PB colliders are attached to the links' children; it
      //  seems like checking only the first child is sufficient
      // fall back to the link if it has no children, even though it probably
      //  won't work
      var child0 = linkI.transform.childCount > 0 ?
        linkI.transform.GetChild(0) : linkI.transform;

      var collider = GetPreventBuildingCollider(child0, false);
      if (!collider) continue;

      // Collider.bounds returns a world-space bounding box around the
      //  collider, which is exactly what we want
      var cBounds = collider.bounds;

      if (first)
      {
        // center global bounds on first collider bounds
        bounds.center = cBounds.center;
        first = false;
      }
      bounds.Encapsulate(cBounds);
    }

    return (0.0f, bounds.size, 0.0f, bounds.center - transform.position);
  }

  // return whether the given GameObject is a Train Tunnel pedestrian surface
  //  entrance to station linkage section of the desired type (stairwell=true
  //  => main stairwell, stairwell=false => dwellings corridor)
  private bool IsApplicableTunnelLink(
    DungeonGridInfo dungeon, GameObject link, bool stairwell)
  {
    // ignore entries whose prefab names indicate they're not intermediate
    //  links; this usually means monument/bunker entrances and stations
    // on vanilla maps we could simply skip the first and last entries, but
    //  I've found custom maps that violate this pattern for some reason
    if (!link.name.StartsWith(
          "assets/bundled/prefabs/autospawn/tunnel-link/link-"))
    {
      return false;
    }
    // ignore entries that are too far from the landmark; this seems to happen
    //  on custom maps where the creator has scrambled things up
    var linkDist =
      Vector3.Distance(dungeon.transform.position, link.transform.position);
    if (linkDist >= 300.0f)
    {
      if (!_brokenTunnels)
      {
        PrintWarning("Corrupt Train Tunnel stairwell/dwelling linkage(s) detected. Related monument events may not work as expected. This is a map/RustEdit issue, not a DynamicPVP issue.");
        _brokenTunnels = true;
      }
      return false;
    }
    // check DungeonGridLink for upward connection type, which determines
    //  whether it's a stairwell versus a corridor
    if (!link.TryGetComponent<DungeonGridLink>(out var dungeonGridLink))
    {
      return false;
    }
    var isStairwell = DungeonGridLinkType.Elevator == dungeonGridLink.UpType;
    return isStairwell == stairwell;
  }

  // get DynamicPVP monument event zone geometry parameters for a transform
  //  belonging to a Train Tunnels entrance linkage system
  // stairwell=true => transform is the entrance link; return volume
  //  encompassing all stairwell sections
  // stairwell=false => transform is central dwellings link; return volume
  //  encompassing all dwelling sections
  private (float radius, Vector3 size, float rotation, Vector3 offset)
  GetTunnelLinkParams(Transform linkTransform, bool stairwell)
  {
    DungeonGridInfo dungeonGridInfo = TerrainMeta.Path.FindClosest(
      TerrainMeta.Path.DungeonGridEntrances, linkTransform.position);
    if (!dungeonGridInfo) return (0.0f, Vector3.zero, 0.0f, Vector3.zero);

    Bounds bounds = new();
    var first = true;
    foreach (GameObject link in dungeonGridInfo.Links)
    {
      if (!IsApplicableTunnelLink(dungeonGridInfo, link, stairwell))
      {
        continue;
      }
      var collider = GetPreventBuildingCollider(link.transform, false);
      if (!collider)
      {
        continue;
      }
      var cBounds = collider.bounds;
      if (first)
      {
        bounds = cBounds;
        first = false;
        continue;
      }
      bounds.Encapsulate(cBounds);
    }

    return (0.0f, bounds.size, 0.0f, bounds.center - linkTransform.position);
  }

  // try to automatically derive a reasonable set of DynamicPVP monument event
  //  zone geometry parameters (radius/size, rotation, offset) for the given
  //  transform
  private (float radius, Vector3 size, float rotation, Vector3 offset)
  GetMonumentGeometry(Transform monumentTransform, MonumentEventType type) =>
    type switch
    {
      MonumentEventType.Default =>
        GetPreventBuildingParams(monumentTransform, false),
      MonumentEventType.Custom =>
        GetPreventBuildingParams(monumentTransform, true),
      MonumentEventType.TunnelEntrance =>
        GetTunnelLinkParams(monumentTransform, true),
      MonumentEventType.TunnelLink =>
        GetTunnelLinkParams(monumentTransform, false),
      MonumentEventType.TunnelSection =>
        GetPreventBuildingParams(monumentTransform, false),
      MonumentEventType.UnderwaterLabs =>
        GetLabLinkParams(monumentTransform),
      _ =>
        throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

  // update monument event geometry parameters with given data if appropriate
  private static void UpdateGeometry(
    MonumentEvent monumentEvent, bool first, float newRadius, Vector3 newSize,
    float newRotation, Vector3 newOffset)
  {
    var updateRadius = newRadius > monumentEvent.DynamicZone.Radius;
    var updateSizeX = newSize.x > monumentEvent.DynamicZone.Size.x;
    var updateSizeY = newSize.y > monumentEvent.DynamicZone.Size.y;
    var updateSizeZ = newSize.z > monumentEvent.DynamicZone.Size.z;
    if (!first && !updateRadius && !updateSizeX && !updateSizeY &&
        !updateSizeZ)
    {
      return;
    }

    if (updateRadius) monumentEvent.DynamicZone.Radius = newRadius;
    var tempSize = monumentEvent.DynamicZone.Size;
    if (updateSizeX) tempSize.x = newSize.x;
    if (updateSizeY) tempSize.y = newSize.y;
    if (updateSizeZ) tempSize.z = newSize.z;
    monumentEvent.DynamicZone.Size = tempSize;
    monumentEvent.DynamicZone.Rotation = newRotation;
    monumentEvent.TransformPosition = newOffset;
  }

  // handle monument geometry checking for the given monument event
  private void CheckMonumentGeometry(
    string monumentName, Transform transform,
    MonumentEventType monumentEventType, bool first,
    MonumentEvent monumentEvent)
  {
    // if this is the first time checking this event type, record config
    //  values in dictionary and then reset them, so that we can start
    //  accumulating calculated values
    if (first)
    {
      _originalMonumentGeometries.TryAdd(monumentName,
        new OriginalMonumentGeometry
        {
          Radius = monumentEvent.DynamicZone.Radius,
          Rotation = monumentEvent.DynamicZone.Rotation,
          Size = monumentEvent.DynamicZone.Size,
          FixedRotation = monumentEvent.DynamicZone.FixedRotation,
          TransformPosition = monumentEvent.TransformPosition
        });

      monumentEvent.DynamicZone.Radius = 0.0f;
      monumentEvent.DynamicZone.Rotation = 0.0f;
      monumentEvent.DynamicZone.Size = Vector3.zero;
      monumentEvent.DynamicZone.FixedRotation = monumentEventType switch
      {
        MonumentEventType.Default => false,
        MonumentEventType.Custom => true,
        MonumentEventType.TunnelEntrance => true,
        MonumentEventType.TunnelLink => true,
        MonumentEventType.TunnelSection => false,
        MonumentEventType.UnderwaterLabs => true,
        _ => false
      };
      monumentEvent.TransformPosition = Vector3.zero;
    }

    // calculate geometry for current monument instance
    var (radius, size, rotation, offset) =
      GetMonumentGeometry(transform, monumentEventType);

    // record any updated parameters
    UpdateGeometry(monumentEvent, first, radius, size, rotation, offset);
  }

  // compare saved-off original monument geometry config data to auto-geo
  //  outputs, and report whether anything actually changed
  private bool ShouldSaveGeometry()
  {
    // NOTE: don't break early, because we need to set all AutoGeoOnNextLoad
    //  flags to false
    var changed = false;
    foreach (var (name, oGeo) in _originalMonumentGeometries)
    {
      if (!_configData.MonumentEvents.TryGetValue(name, out var monumentEvent))
      {
        // pathological: cached original geometry for a monument event that
        //  doesn't exist
        continue;
      }
      if (changed || oGeo.HasSameData(monumentEvent)) continue;
      // geometry data changed from original config values; request save
      PrintDebug($"ShouldSaveGeometry(): Geometry changed for monument event {name}; subsequent changes may not be reported");
      changed = true;
    }
    _originalMonumentGeometries.Clear();
    return changed;
  }

  private struct OriginalMonumentGeometry
  {
    public float Radius = 0f;
    public float Rotation = 0f;
    public Vector3 Size = Vector3.zero;
    public bool FixedRotation = false;
    public Vector3 TransformPosition = Vector3.zero;

    public OriginalMonumentGeometry()
    {
    }

    public bool HasSameData(MonumentEvent monumentEvent) =>
      Mathf.Approximately(Radius, monumentEvent.DynamicZone.Radius) &&
      Mathf.Approximately(Rotation, monumentEvent.DynamicZone.Rotation) &&
      Size == monumentEvent.DynamicZone.Size &&
      FixedRotation == monumentEvent.DynamicZone.FixedRotation &&
      TransformPosition == monumentEvent.TransformPosition;
  }

  #endregion Automatic Geometry

  // add and/or start (create) the given monument event name
  // records via collection modification whether it was added and/or created
  // NOTE: monument events currently default to disabled, but the code is
  //  structured to support auto-starting new events in case there is ever a
  //  desire to support this
  private IEnumerator CreateMonumentEvent(
    string monumentName, Transform transform, MonumentEventType type,
    HashSet<string> addedEvents, List<string> createdEvents)
  {
    if (!_configData.MonumentEvents.TryGetValue(
          monumentName, out var monumentEvent))
    {
      monumentEvent = new MonumentEvent { EventName = monumentName };
      _configData.MonumentEvents.Add(monumentName, monumentEvent);
      addedEvents.Add(monumentName);
    }

    if (monumentEvent.Enabled)
    {
      if (monumentEvent.DynamicZone.DoAutoGeo)
      {
        PrintDebug($"CreateMonumentEvent(): Calculating geometry for monument event {monumentName} with type {type} at location {transform.position}");
        CheckMonumentGeometry(
          monumentName, transform, type,
          first: !createdEvents.Contains(monumentName), monumentEvent);
      }
      if (HandleMonumentEvent(monumentEvent, transform))
      {
        createdEvents.Add(monumentName);
      }
    }

    yield return DynamicYield();
  }

  private static (string monumentName, bool custom) GetLandmarkName(
    LandmarkInfo landmarkInfo)
  {
    // TODO: cache this if it ever gets called more than once per custom
    //  monument (I tested, and it does not as of 4.9.0)

    // vanilla monument
    if (!landmarkInfo.name.Contains("monument_marker.prefab"))
    {
      return (landmarkInfo.displayPhrase?.english?.Trim(), false);
    }

    // custom monument

    // this sucks (results in scanning 5000+ prefabs during startup), but it
    //  seems to be how Facepunch decided to make us do it as of late 2025
    //  (stolen from their MonumentMarker class)
    var obj = landmarkInfo.transform.root.gameObject;
    foreach (var (prefabName, objectSet) in World.SpawnedPrefabs)
    {
      if (objectSet.Contains(obj)) return (prefabName, true);
    }

    return (landmarkInfo.transform.root.name, true);
  }

  // sub-coroutine to create (vanilla and custom map) map marker based
  //  monument events
  private IEnumerator CreateLandmarkMonumentEvents(
    HashSet<string> addedEvents, List<string> createdEvents)
  {
    foreach (LandmarkInfo landmarkInfo in TerrainMeta.Path.Landmarks)
    {
      // skip train tunnel stairwells/dwellings and underwater labs sections,
      //  as they are handled in other ways
      if (landmarkInfo is DungeonGridInfo or DungeonBaseLandmarkInfo)
      {
        continue;
      }

      var (monumentName, custom) = GetLandmarkName(landmarkInfo);
      if (string.IsNullOrEmpty(monumentName) &&
          landmarkInfo.shouldDisplayOnMap)
      {
        PrintDebug($"CreateLandmarkMonumentEvents(): Skipping visible landmark because it has no map title: {landmarkInfo}");
        continue;
      }

      if (landmarkInfo is MonumentInfo && "Train Tunnel" == monumentName)
      {
        // Train Tunnel bunker entrances are MonumentInfo objects that aren't
        //  map-visible, because they connect to DungeonGridInfo landmarks
        //  with the same name; promote this to a first-class monument by
        //  giving it a map visibility exception, but tweak the name to
        //  disambiguate from the other flavor
        monumentName = "Train Tunnel Bunker";
      }
      else if (!landmarkInfo.shouldDisplayOnMap)
      {
        // all other landmarks must have map markers/labels for now
        PrintDebug($"CreateLandmarkMonumentEvents(): Skipping unlabeled landmark {monumentName} of type {landmarkInfo}");
        continue;
      }

      switch (landmarkInfo.name)
      {
        case "assets/bundled/prefabs/autospawn/monument/harbor/harbor_1.prefab":
          monumentName += " A";
          break;
        case "assets/bundled/prefabs/autospawn/monument/harbor/harbor_2.prefab":
          monumentName += " B";
          break;
        case PrefabLargeOilRig:
          _largeOilRigPosition = landmarkInfo.transform.position;
          break;
        case PrefabOilRig:
          _oilRigPosition = landmarkInfo.transform.position;
          break;
      }

      var type =
        custom                           ? MonumentEventType.Custom :
        "Underwater Lab" == monumentName ? MonumentEventType.UnderwaterLabs :
                                           MonumentEventType.Default;
      yield return CreateMonumentEvent(
        monumentName, landmarkInfo.transform, type, addedEvents, createdEvents);
    }

    yield return DynamicYield(true);
  }

  // derive a user-friendly event name from a Train Tunnel section prefab name
  // returns empty string on failure, which is debug logged
  private string ToTunnelSectionEventName(string cellName)
  {
    if (!cellName.StartsWith("assets/bundled/prefabs/autospawn/tunnel"))
    {
      PrintDebug($"ToTunnelSectionEventName(): Skipping unsupported DungeonGridCell type due to non-tunnel: {cellName}");
      return "";
    }
    // NOTE: this must be returned to pool on any return from this method
    var stringBuilder = Pool.Get<StringBuilder>();
    stringBuilder.Clear().Append("Train Tunnel");
    // get the "tunnelXYZ" part of the name
    var slashSplit = cellName.Split("/");
    if (slashSplit.Length < 6)
    {
      PrintDebug($"ToTunnelSectionEventName(): Skipping unsupported DungeonGridCell type due to path components: {cellName}");
      Pool.FreeUnmanaged(ref stringBuilder);
      return "";
    }
    var upwards = "tunnel-upwards" == slashSplit[4];
    var dashSplit = slashSplit[5].Split("-");
    if (dashSplit.Length < 2)
    {
      PrintDebug($"ToTunnelSectionEventName(): Skipping unsupported DungeonGridCell type due to name components: {cellName}");
      Pool.FreeUnmanaged(ref stringBuilder);
      return "";
    }
    // extract feature part of name
    var feature = slashSplit[5];
    if (upwards)
    {
      stringBuilder.Append(" Upwards");
    }
    else if (feature.StartsWith("curve"))
    {
      stringBuilder.Append(" Curve");
    }
    else if (feature.StartsWith("intersection"))
    {
      stringBuilder.Append(" Intersection");
    }
    else if (feature.StartsWith("station"))
    {
      stringBuilder.Append(" Station");
    }
    else if (feature.StartsWith("straight"))
    {
      stringBuilder.Append(" Straight");
    }
    else if (feature.StartsWith("transition"))
    {
      stringBuilder.Append(" Transition");
    }
    else
    {
      PrintDebug($"ToTunnelSectionEventName(): Skipping unsupported DungeonGridCell type due to feature component '{feature}': {cellName}");
      Pool.FreeUnmanaged(ref stringBuilder);
      return "";
    }
    // extract direction part of name
    var direction =
      dashSplit[upwards ? dashSplit.Length - 1 : 1].Split(".")[0];
    switch (direction)
    {
      case "e":  stringBuilder.Append(" East");        break;
      case "n":  stringBuilder.Append(" North");       break;
      case "ne": stringBuilder.Append(" North-East");  break;
      case "nw": stringBuilder.Append(" North-West");  break;
      case "s":  stringBuilder.Append(" South");       break;
      case "se": stringBuilder.Append(" South-East");  break;
      case "sn": stringBuilder.Append(" North-South"); break;
      case "sw": stringBuilder.Append(" South-West");  break;
      case "w":  stringBuilder.Append(" West");        break;
      case "we": stringBuilder.Append(" East-West");   break;
      default:
      {
        PrintDebug($"ToTunnelSectionEventName(): Skipping unsupported DungeonGridCell type due to unknown direction '{direction}': {cellName}");
        Pool.FreeUnmanaged(ref stringBuilder);
        return "";
      }
    }
    var eventName = stringBuilder.ToString();
    Pool.FreeUnmanaged(ref stringBuilder);
    return eventName;
  }

  // sub-coroutine to create Train Tunnel section based monument events
  private IEnumerator CreateTunnelSectionMonumentEvents(
    HashSet<string> addedEvents, List<string> createdEvents)
  {
    foreach (DungeonGridCell cell in TerrainMeta.Path.DungeonGridCells)
    {
      var eventName = ToTunnelSectionEventName(cell.name);
      if (string.IsNullOrEmpty(eventName)) continue;
      yield return CreateMonumentEvent(
        eventName, cell.transform, MonumentEventType.TunnelSection,
        addedEvents, createdEvents);
    }

    yield return DynamicYield(true);
  }

  // get index of center-most section in Train Tunnel (stairwell=true) or
  //  Train Tunnel Dwelling (stairwell=false) portion of entrance links
  // returns negative number on failure
  private int GetUpperTunnelCenterIndex(
    DungeonGridInfo dungeonGridInfo, bool stairwell = false)
  {
    if (dungeonGridInfo.Links.Count <= 0)
    {
      PrintDebug($"GetUpperTunnelCenterIndex(): Skipping DungeonGridInfo with empty Links list: {dungeonGridInfo.name}");
      return -1;
    }
    // scan the list once, to create a bounding box around the transform
    //  positions of all applicable links
    Bounds linkBounds = new(Vector3.zero, Vector3.zero);
    var first = true;
    foreach (GameObject link in dungeonGridInfo.Links)
    {
      if (!IsApplicableTunnelLink(dungeonGridInfo, link, stairwell)) continue;
      if (first)
      {
        // this is the first valid entry; record its position
        linkBounds.center = link.transform.position;
        first = false;
        continue;
      }
      linkBounds.Encapsulate(link.transform.position);
    }
    // scan the list again, so find the closest link to bounding box center
    if (Vector3.zero == linkBounds.size)
    {
      PrintDebug($"GetUpperTunnelCenterIndex(): Skipping DungeonGridInfo with unknown bounds: {dungeonGridInfo.name}@{dungeonGridInfo.transform.position}");
      return -2;
    }
    // find the link closest to the center of the bounding box
    var transformIndex = -3;
    var minDistance = -1.0f;
    for (var i = 0; i < dungeonGridInfo.Links.Count; ++i)
    {
      GameObject linkI = dungeonGridInfo.Links[i];
      if (!IsApplicableTunnelLink(dungeonGridInfo, linkI, stairwell))
      {
        continue;
      }
      var distance =
        Vector3.Distance(linkBounds.center, linkI.transform.position);
      if (minDistance > 0 && distance > minDistance) continue;
      transformIndex = i;
      minDistance = distance;
    }
    return transformIndex;
  }

  // sub-coroutine to create Train Tunnel stairwell and dweller area monument
  //  events
  private IEnumerator CreateUpperTunnelMonumentEvents(
    HashSet<string> addedEvents, List<string> createdEvents)
  {
    foreach (
      DungeonGridInfo entrance in TerrainMeta.Path.DungeonGridEntrances)
    {
      if (entrance.Links.Count <= 0) continue;

      // create Train Tunnel stairwell monument event
      var landmarkName = GetLandmarkName(entrance).monumentName;
      if (string.IsNullOrEmpty(landmarkName))
      {
        PrintWarning($"CreateUpperTunnelMonumentEvents(): Skipping Train Tunnels entrance {entrance}@{entrance.transform.position} because it has no landmark name");
        continue;
      }
      yield return CreateMonumentEvent(
        landmarkName, entrance.transform, MonumentEventType.TunnelEntrance,
        addedEvents, createdEvents);

      // create Train Tunnel Dwelling monument event
      var transformIndex =
        GetUpperTunnelCenterIndex(entrance);
      if (transformIndex < 0) continue;
      yield return CreateMonumentEvent(
        landmarkName + " Dwelling", entrance.Links[transformIndex].transform,
        MonumentEventType.TunnelLink,
        addedEvents, createdEvents);
    }

    yield return DynamicYield(true);
  }

  // coroutine to orchestrate creation of all monument event types
  private IEnumerator CreateMonumentEvents()
  {
    var save = false;
    var addedEvents = Pool.Get<HashSet<string>>();
    var createdEvents = Pool.Get<List<string>>();

    Puts("Creating Landmark Monument Events");
    yield return CreateLandmarkMonumentEvents(addedEvents, createdEvents);
    Puts("Creating Train Tunnel Section Monument Events");
    yield return CreateTunnelSectionMonumentEvents(
      addedEvents, createdEvents);
    Puts("Creating Train Tunnel Entrance Monument Events");
    yield return CreateUpperTunnelMonumentEvents(addedEvents, createdEvents);

    if (addedEvents.Count > 0)
    {
      PrintDebug($"CreateMonumentEvents(): {addedEvents.Count} new monument event(s) added to config: {string.Join(", ", addedEvents)}");
      save = true;
    }
    if (ShouldSaveGeometry())
    {
      PrintDebug("CreateMonumentEvents(): Recording geometry change(s) to config file");
      save = true;
    }
    if (save) SaveConfig();

    if (createdEvents.Count > 0)
    {
      PrintDebug($"CreateMonumentEvents(): {createdEvents.Count} monument event(s) successfully created: {string.Join(", ", createdEvents)}");
    }

    Pool.FreeUnmanaged(ref addedEvents);
    Pool.FreeUnmanaged(ref createdEvents);

    yield return DynamicYield(true);
  }

  #endregion Monument Events

  #region Auto Event

  // coroutine to create user-defined auto events
  private IEnumerator CreateAutoEvents()
  {
    var createdEvents = Pool.Get<List<string>>();

    // create auto events from data file
    foreach (var (eventName, baseEvent) in _storedData.autoEvents)
    {
      if (!baseEvent.AutoStart ||
          !CreateDynamicZone(baseEvent, baseEvent.Position, eventName))
      {
        continue;
      }
      createdEvents.Add(eventName);
      yield return DynamicYield();
    }
    if (createdEvents.Count > 0)
    {
      PrintDebug($"CreateAutoEvents(): {createdEvents.Count} auto event(s) successfully created: {string.Join(", ", createdEvents)}");
    }

    Pool.FreeUnmanaged(ref createdEvents);

    yield return DynamicYield(true);
  }

  #endregion Auto Event

  #endregion Events

  #region Chat/Console Command Handler

  private object OnPlayerCommand(
    BasePlayer player, string command, string[] args) =>
    CheckCommand(player, command, true);

  private object OnServerCommand(ConsoleSystem.Arg arg) =>
    CheckCommand(arg?.Player(), arg?.cmd?.FullName, false);

  private object CheckCommand(BasePlayer player, string command, bool isChat)
  {
    if (!player || string.IsNullOrEmpty(command))
    {
      return null;
    }
    command = command.ToLower().TrimStart('/');
    if (string.IsNullOrEmpty(command))
    {
      return null;
    }

    if (_pvpDelays.TryGetValue(player.userID, out var leftZone))
    {
      if (true == leftZone.baseEvent?.CommandWorksForPVPDelay &&
          IsBlockedCommand(leftZone.baseEvent, command, isChat))
      {
        return false;
      }
    }

    var playerZones = Pool.Get<List<string>>();
    ZM_GetPlayerZoneIDs(player, playerZones);
    foreach (var zoneId in playerZones)
    {
      if (string.IsNullOrEmpty(zoneId) ||
          !_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
      {
        continue;
      }
      PrintDebug($"CheckCommand(): Checking command: {command} , zoneId: {zoneId}");
      if (null != baseEvent && IsBlockedCommand(baseEvent, command, isChat))
      {
        Pool.FreeUnmanaged(ref playerZones);
        return false;
      }
    }

    Pool.FreeUnmanaged(ref playerZones);
    return null;
  }

  private bool IsBlockedCommand(
    BaseEvent baseEvent, string command, bool isChat)
  {
    if (null == baseEvent || baseEvent.CommandList.Count <= 0) return false;
    var commandExist = baseEvent.CommandList.Exists(entry =>
      entry.StartsWith('/') && isChat ?
        entry[1..].Equals(command) : command.Contains(entry));
    if (baseEvent.UseBlacklistCommands != commandExist) return false;
    PrintDebug($"IsBlockedCommand(): Blocked command: '{command}' due to {(baseEvent.UseBlacklistCommands ? "blacklist" : "whitelist")}");
    return true;
  }

  #endregion Chat/Console Command Handler

  #region DynamicZone Handler

  // create a zone that is parented to an entity, so that they move together
  //
  // NOTE: caller is responsible for calling CheckEntityOwner() and
  //  CanCreateDynamicPVP() first, because this method can't easily implement
  //  calling them exactly once
  private void HandleParentedEntityEvent(
    BaseEvent baseEvent, BaseEntity parentEntity, bool parentOnCreate,
    bool delay = true)
  {
    if (baseEvent == null)
    {
      return;
    }
    if (!parentEntity || null == parentEntity.net)
    {
      return;
    }
    if (delay && baseEvent.EventStartDelay > 0f)
    {
      timer.Once(baseEvent.EventStartDelay, () => HandleParentedEntityEvent(
        baseEvent, parentEntity, parentOnCreate, false));
      return;
    }
    PrintDebug($"HandleParentedEntityEvent(): Trying to create parented entity eventName={baseEvent.GetName()} on parentEntity={parentEntity}.");
    var zonePosition = parentEntity.transform.position;
    if (!parentOnCreate)
    {
      var groundY = TerrainMeta.HeightMap.GetHeight(zonePosition);
      if (Mathf.Abs(zonePosition.y - groundY) < 10.0f)
      {
        // entity is already near the ground; force enable immediate parenting
        // this catches the case that e.g. a Supply Drop landed during the
        //  event start delay
        parentOnCreate = true;
      }
      else
      {
        // entity is not near the ground yet; start the zone on the ground
        zonePosition.y = groundY;
      }
    }
    var zoneId = parentEntity.net.ID.ToString();
    if (!CreateDynamicZone(baseEvent, zonePosition, zoneId, delay: false))
    {
      return;
    }
    if (parentOnCreate)
    {
      // attach the event (zone, plus domes if applicable) to the parent
      //  entity, so that they move together
      ParentEventToEntity(
        zoneId, baseEvent, parentEntity, deleteOnFailure: true);
    }
    // else something will attach it later
  }

  private static Vector3 GetMonumentPosition(
    MonumentEvent monumentEvent, Transform transform) =>
    monumentEvent.TransformPosition == Vector3.zero ?
      transform.position :
      monumentEvent.DynamicZone.FixedRotation ?
        transform.position + monumentEvent.TransformPosition :
        transform.TransformPoint(monumentEvent.TransformPosition);

  private bool HandleMonumentEvent(
    MonumentEvent monumentEvent, Transform transform) => CreateDynamicZone(
      monumentEvent,
      GetMonumentPosition(monumentEvent, transform),
      monumentEvent.ZoneId,
      monumentEvent.GetDynamicZone().ZoneSettings(transform));

  private static (Transform, Vector3, float)
  GetIslandGeometry(DeepSeaIsland deepSeaIsland)
  {
    if (deepSeaIsland.monumentNavMesh)
    {
      // use navmesh bounds if available, as they're slightly better-centered
      var bounds = deepSeaIsland.monumentNavMesh.GetBounds();
      var navMeshPosition = bounds.center;
      navMeshPosition.y = 0;
      // the navmesh bounds cling pretty tightly to the island, so instead we
      //  calculate a sphere that fully encompasses them, rounded up to the next
      //  multiple of rVal
      const float rVal = 100.0f;
      var radius = Mathf.Ceil(bounds.extents.magnitude / rVal) * rVal;
      return (deepSeaIsland.monumentNavMesh.transform, navMeshPosition, radius);
    }

    var islandPosition = deepSeaIsland.transform.position;
    islandPosition.y = 0;
    return (deepSeaIsland.transform, islandPosition, 200.0f);
  }

  private enum EventHeightStrategy
  {
    Entity,  // place event at entity's height
    Terrain, // place event on ground
    Zero     // place event at sea level
  };

  private void HandleGeneralEvent(
    IGeneralEvent generalEvent,
    BaseEntity baseEntity,
    bool useEntityId,
    bool delay = true,
    EventHeightStrategy heightStrategy = EventHeightStrategy.Terrain)
  {
    if (generalEvent is not BaseEvent baseEvent)
    {
      return;
    }
    var eventName = baseEvent.GetName();
    if (!CanCreateDynamicPVP(eventName, baseEntity))
    {
      return;
    }

    string zoneId = null;
    if (useEntityId)
    {
      zoneId = baseEntity?.net?.ID.ToString();
      if (string.IsNullOrEmpty(zoneId))
      {
        PrintDebug($"HandleGeneralEvent(): Aborting creation of eventName={eventName}, because entity is null", DebugLevel.Warning);
        return;
      }
      if (_activeDynamicZones.ContainsKey(zoneId))
      {
        PrintDebug($"HandleGeneralEvent(): Aborting creation of redundant eventName={eventName} for baseEntity={baseEntity} with baseEntity.net.ID={baseEntity.net.ID}", DebugLevel.Warning);
        return;
      }
    }

    // TODO: move some of this to the event class hierarchy to streamline
    //  things?
    var position = baseEntity.transform.position;
    position.y = heightStrategy switch
    {
      EventHeightStrategy.Entity  => position.y,
      EventHeightStrategy.Terrain => TerrainMeta.HeightMap.GetHeight(position),
      EventHeightStrategy.Zero    => 0,
      _ => throw new ArgumentOutOfRangeException(
        nameof(heightStrategy), heightStrategy, null)
    };

    // NOTE: zoneId will be null if useEntityId is false; this is intentional
    CreateDynamicZone(
      baseEvent, position, zoneId,
      baseEvent.GetDynamicZone().ZoneSettings(baseEntity.transform), delay);

    if (IsDeepSeaEvent(generalEvent.GeneralEventType))
    {
      _potentialDeepSeaZones.Add(zoneId);
    }
  }

  private bool CreateDynamicZone(
    BaseEvent baseEvent, Vector3 position, string zoneId = "",
    string[] zoneSettings = null, bool delay = true)
  {
    var eventName = baseEvent.GetName();
    if (position == Vector3.zero)
    {
      PrintDebug($"CreateDynamicZone(): ERROR: Invalid location, zone creation failed for eventName={eventName}", DebugLevel.Error);
      return false;
    }

    switch (delay)
    {
      // protect against immediate creation of a zone if one already exists with
      //  given zone ID (don't want to block delays because they may be
      //  orchestrated?)
      case false when _activeDynamicZones.ContainsKey(zoneId):
        PrintDebug($"CreateDynamicZone(): WARNING: Cannot create zone for eventName={eventName} because requested zone ID {zoneId} already exists", DebugLevel.Warning);
        // return true to indicate that the zone already exists
        return true;
      case true when baseEvent.EventStartDelay > 0f:
        timer.Once(baseEvent.EventStartDelay, () => CreateDynamicZone(
          baseEvent, position, zoneId, zoneSettings, false));
        PrintDebug($"CreateDynamicZone(): Delaying zone creation for eventName={eventName} by {baseEvent.EventStartDelay} seconds");
        return false;
    }

    var duration = -1f;
    if (baseEvent is BaseTimedEvent timedEvent &&
        (baseEvent is not ITimedDisable timedDisable ||
         !timedDisable.IsTimedDisabled()))
    {
      duration = timedEvent.Duration;
    }

    if (string.IsNullOrEmpty(zoneId))
    {
      // TODO: prefix with plugin name or event type?
      // TODO: switch to using entity netID in more cases?
      zoneId = DateTime.Now.ToString("HHmmssffff");
    }

    var dynamicZone = baseEvent.GetDynamicZone();
    zoneSettings ??= dynamicZone.ZoneSettings();

    PrintDebug($"CreateDynamicZone(): Trying to create zoneId={zoneId} for eventName={eventName} at position={position}{(dynamicZone is ISphereZone zone ? $", radius={zone.Radius}m" : null)}{(dynamicZone is ICubeZone cubeZone ? $", size={cubeZone.Size}" : null)}{(dynamicZone is IParentZone parentZone ? $", center={parentZone.Center}" : null)}{(dynamicZone is IRotateZone rotateZone ? $", rotation={rotateZone.Rotation}, fixedRotation={rotateZone.FixedRotation}" : null)}, duration={duration}s.");
    var zoneRadius = dynamicZone is ISphereZone sz ? sz.Radius : 0;
    var zoneSize = dynamicZone is ICubeZone cz ? cz.Size.magnitude : 0;
    if (zoneRadius <= 0 && zoneSize <= 0)
    {
      PrintError($"CreateDynamicZone(): ERROR: Cannot create zone for eventName={eventName} because both radius and size are less than or equal to zero");
      return false;
    }
    if (!ZM_CreateOrUpdateZone(zoneId, zoneSettings, position))
    {
      PrintDebug($"CreateDynamicZone(): ERROR: Zone NOT created for eventName={eventName}.", DebugLevel.Error);
      return false;
    }

    if (_activeDynamicZones.TryAdd(zoneId, baseEvent))
    {
      UpdateActivePluginZones(added: true, zoneId, baseEvent);
      CheckHooks(HookCheckReasons.ZoneAdded, baseEvent);
    }

    var stringBuilder = Pool.Get<StringBuilder>();
    stringBuilder.Clear();
    if (baseEvent is DomeEvent domeEvent &&
        dynamicZone is ISphereZone sphereZone &&
        domeEvent.DomeData.DomeCreateAllowed(sphereZone))
    {
      if (CreateDome(zoneId, position, sphereZone.Radius, domeEvent.DomeData))
      {
        stringBuilder.Append("Dome,");
      }
      else
      {
        PrintDebug($"CreateDynamicZone(): ERROR: Dome NOT created for zoneId={zoneId}.", DebugLevel.Error);
      }
    }

    if (baseEvent is BotDomeEvent botEvent && BotReSpawnAllowed(botEvent))
    {
      if (SpawnBots(position, botEvent.BotProfileName, zoneId))
      {
        stringBuilder.Append("Bots,");
      }
      else
      {
        PrintDebug($"CreateDynamicZone(): ERROR: Bot(s) NOT spawned for zoneId={zoneId}.", DebugLevel.Error);
      }
    }

    if (TP_AddOrUpdateMapping(zoneId, baseEvent.Mapping))
    {
      stringBuilder.Append("Mapping,");
    }
    else
    {
      PrintDebug($"CreateDynamicZone(): ERROR: Mapping NOT created for zoneId={zoneId}.", DebugLevel.Error);
    }

    PrintDebug($"CreateDynamicZone(): Created zoneId={zoneId} for eventName={eventName} with properties: {stringBuilder.ToString().TrimEnd(',')}.");
    HandleDeleteDynamicZone(zoneId, duration);

    stringBuilder.Clear();
    Pool.FreeUnmanaged(ref stringBuilder);
    Interface.CallHook("OnCreatedDynamicPVP",
      zoneId, eventName, position, duration);
    return true;
  }

  // schedule deletion of an event zone after the given duration
  // ignored if duration is <= 0, with the assumption that deletion will be
  //  triggered by something else
  private void HandleDeleteDynamicZone(string zoneId, float duration)
  {
    if (string.IsNullOrEmpty(zoneId) ||
        !_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
    {
      // this isn't an error, because sometimes deletion is requested "just in
      //  case", and/or because multiple delete stimuli occurred
      PrintDebug($"HandleDeleteDynamicZone({duration}): Skipping scheduled deletion for unknown zoneId={zoneId}");
      return;
    }

    var eventName = baseEvent.GetName();
    if (duration <= 0f)
    {
      PrintDebug($"HandleDeleteDynamicZone(): Skipping scheduled deletion of zoneId={zoneId} for eventName={eventName} because duration={duration} is non-positive");
      return;
    }

    TryRemoveEventTimer(zoneId);
    PrintDebug($"HandleDeleteDynamicZone(): Scheduling deletion of zoneId={zoneId} for eventName={eventName} in {duration} seconds");
    _eventTimers.Add(zoneId, timer.Once(
      duration, () => HandleDeleteDynamicZone(zoneId)));
  }

  // schedule deletion of an event zone after its configured duration
  private void HandleDeleteDynamicZone(string zoneId)
  {
    if (string.IsNullOrEmpty(zoneId) ||
        !_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
    {
      // this isn't an error, because sometimes deletion is requested "just in
      //  case", and/or because multiple delete stimuli occurred
      PrintDebug($"HandleDeleteDynamicZone(): Skipping delete for unknown zoneId={zoneId}.");
      return;
    }
    var eventName = baseEvent.GetName();
    PrintDebug($"HandleDeleteDynamicZone(): Calling OnDeleteDynamicPVP({zoneId}, {eventName})");
    if (Interface.CallHook("OnDeleteDynamicPVP", zoneId, eventName) != null)
    {
      PrintDebug("HandleDeleteDynamicZone(): Aborting delete due to OnDeleteDynamicPVP hook response");
      return;
    }
    if (baseEvent.EventStopDelay <= 0f)
    {
      // immediate delete
      DeleteDynamicZone(zoneId);
      return;
    }
    // delayed delete
    TryRemoveEventTimer(zoneId);
    if (baseEvent.GetDynamicZone() is IParentZone)
    {
      // untether zone from parent entity in case it is dying or will die
      ZM_GetZoneByID(zoneId)?.transform.SetParent(null, true);
      // also untether any domes
      ParentDome(zoneId, Vector3.zero);
    }
    _eventTimers.Add(zoneId, timer.Once(
      baseEvent.EventStopDelay, () => DeleteDynamicZone(zoneId)));
  }

  private bool DeleteDynamicZone(string zoneId)
  {
    if (string.IsNullOrEmpty(zoneId) ||
        !_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
    {
      // this isn't an error, because sometimes deletion is requested "just in
      //  case", and/or because multiple delete stimuli occurred
      PrintDebug($"DeleteDynamicZone(): Skipping delete for unknown zoneId={zoneId}.");
      return false;
    }

    TryRemoveEventTimer(zoneId);

    var eventName = baseEvent.GetName();
    // avoid allocating this StringBuilder when not debug logging
    var sbProperties =
      _configData.Global.DebugEnabled ? Pool.Get<StringBuilder>() : null;
    if (baseEvent is DomeEvent domeEvent &&
        domeEvent.DomeData.DomeCreateAllowed(
          baseEvent.GetDynamicZone() as ISphereZone))
    {
      if (RemoveDome(zoneId))
      {
        sbProperties?.Append("Dome,");
      }
      else
      {
        PrintDebug($"DeleteDynamicZone(): ERROR: Dome NOT removed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.Error);
      }
    }

    if (BotReSpawnAllowed(baseEvent as BotDomeEvent))
    {
      if (KillBots(zoneId))
      {
        sbProperties?.Append("Bots,");
      }
      else
      {
        PrintDebug($"DeleteDynamicZone(): ERROR: Bot(s) NOT killed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.Error);
      }
    }

    if (TP_RemoveMapping(zoneId))
    {
      sbProperties?.Append("Mapping,");
    }
    else
    {
      PrintDebug($"DeleteDynamicZone(): ERROR: Mapping NOT removed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.Error);
    }

    // need to get list of players in zone *before* deleting it
    // this is unfortunate because it's wasted processing in the case that zone
    //  deletion fails, but that's an off-nominal case anyway
    var players = Pool.Get<List<BasePlayer>>();
    ZM_GetPlayersInZone(zoneId, players);
    var zoneRemoved = ZM_EraseZone(zoneId, eventName);
    if (zoneRemoved)
    {
      // avoid allocating this StringBuilder when not debug logging or when no
      //  players were in the zone
      var sbPlayers = _configData.Global.DebugEnabled && players.Count > 0 ?
        Pool.Get<StringBuilder>() : null;
      var first = true;
      // Release zone players immediately
      foreach (var player in players)
      {
        OnExitZone(zoneId, player);
        if (first) first = false; else sbPlayers?.Append(", ");
        sbPlayers?.Append(player.displayName);
      }
      if (null != sbPlayers)
      {
        PrintDebug($"DeleteDynamicZone(): Released zone players: {sbPlayers}");
        Pool.FreeUnmanaged(ref sbPlayers);
      }

      if (_activeDynamicZones.Remove(zoneId))
      {
        UpdateActivePluginZones(added: false, zoneId, baseEvent);
        CheckHooks(HookCheckReasons.ZoneRemoved, baseEvent);
      }
      PrintDebug($"DeleteDynamicZone(): Deleted zoneId={zoneId} with eventName={eventName} and properties: {sbProperties?.ToString().TrimEnd(',')}.");
      PrintDebug($"DeleteDynamicZone(): Calling OnDeletedDynamicPVP({zoneId}, {eventName})");
      Interface.CallHook("OnDeletedDynamicPVP", zoneId, eventName);
    }
    else
    {
      PrintDebug($"DeleteDynamicZone(): ERROR: Zone NOT removed for zoneId={zoneId} with eventName={eventName} and properties: {sbProperties?.ToString().TrimEnd(',')}.", DebugLevel.Error);
    }
    Pool.FreeUnmanaged(ref players);
    if (null != sbProperties) Pool.FreeUnmanaged(ref sbProperties);
    return zoneRemoved;
  }

  #endregion DynamicZone Handler

  #region Domes

  private readonly Dictionary<string, List<SphereEntity>> _zoneSpheres = new();

  private void CreateDome(
    List<SphereEntity> list, string prefabName, Vector3 position, float radius)
  {
    if (null == list || string.IsNullOrEmpty(prefabName) || radius <= 0)
    {
      return;
    }

    if (GameManager.server.CreateEntity(prefabName, position)
          is not SphereEntity sphereEntity || !sphereEntity)
    {
      PrintDebug($"CreateDome(): ERROR: Failed to create SphereEntity; prefabName={prefabName}, position={position}, radius={radius}", DebugLevel.Error);
      return;
    }

    sphereEntity.enableSaving = false;
    sphereEntity.Spawn();
    sphereEntity.LerpRadiusTo(radius * 2f, radius);
    list.Add(sphereEntity);
  }

  private bool CreateDome(
    string zoneId, Vector3 position, float radius, DomeSettings domeData)
  {
    // Method for spherical dome creation
    if (radius <= 0) return false;

    var sphereEntities = Pool.Get<List<SphereEntity>>();

    // add domes
    for (var i = 0; i < domeData.Darkness; ++i)
    {
      CreateDome(sphereEntities, PrefabSphereDome, position, radius);
    }

    // add rings
    foreach (var ring in new[]
             {
               (domeData.RedRing,    PrefabSphereRedRing),
               (domeData.GreenRing,  PrefabSphereGreenRing),
               (domeData.BlueRing,   PrefabSphereBlueRing),
               (domeData.PurpleRing, PrefabSpherePurpleRing)
             })
    {
      if (!ring.Item1) continue;
      CreateDome(sphereEntities, ring.Item2, position, radius);
    }

    _zoneSpheres.Add(zoneId, sphereEntities);
    return true;
  }

  private void ParentDome(
    string zoneId, Vector3 position, BaseEntity parentEntity = null)
  {
    if (string.IsNullOrEmpty(zoneId) ||
        !_zoneSpheres.TryGetValue(zoneId, out var sphereEntities))
    {
      return;
    }
    foreach (var sphereEntity in sphereEntities)
    {
      if (parentEntity is not null && parentEntity)
      {
        // tethering dome to parent entity
        sphereEntity.SetParent(parentEntity);
        sphereEntity.transform.position = position;
        sphereEntity.EnableGlobalBroadcast(parentEntity.globalBroadcast);
      }
      else
      {
        // un-tethering dome from parent entity
        sphereEntity.SetParent(null, true);
      }
    }
  }

  private bool RemoveDome(string zoneId)
  {
    if (!_zoneSpheres.TryGetValue(zoneId, out var sphereEntities))
    {
      return false;
    }
    foreach (var sphereEntity in sphereEntities)
    {
      sphereEntity.LerpRadiusTo(0, sphereEntity.currentRadius);
    }
    timer.Once(5f, () =>
    {
      foreach (var sphereEntity in sphereEntities)
      {
        if (sphereEntity && !sphereEntity.IsDestroyed)
        {
          sphereEntity.KillMessage();
        }
      }
      _zoneSpheres.Remove(zoneId);
      Pool.FreeUnmanaged(ref sphereEntities);
    });
    return true;
  }

  #endregion ZoneDome Integration

  #region TruePVE Integration

  private object CanEntityTakeDamage(BasePlayer victim, HitInfo info)
  {
    if (null == info || !victim || !victim.userID.IsSteamId())
    {
      return null;
    }
    var attacker = info.InitiatorPlayer ??
     (info.Initiator && info.Initiator.OwnerID.IsSteamId() ?
       BasePlayer.FindByID(info.Initiator.OwnerID) : null);
    if (attacker is null || !attacker || !attacker.userID.IsSteamId())
    {
      //The attacker cannot be fully captured
      return null;
    }

    var pvpConfig = _configData.Global.PvpDelayFlags;
    if (_pvpDelays.TryGetValue(victim.userID, out var victimLeftZone))
    {
      if (pvpConfig.HasFlag(PvpDelayTypes.ZonePlayersCanDamageDelayedPlayers) &&
          !string.IsNullOrEmpty(victimLeftZone.zoneId) &&
          ZM_IsPlayerInZone(victimLeftZone, attacker))
      {
        //ZonePlayer attack DelayedPlayer
        return true;
      }
      if (pvpConfig.HasFlag(
            PvpDelayTypes.DelayedPlayersCanDamageDelayedPlayers) &&
          _pvpDelays.TryGetValue(attacker.userID, out var attackerLeftZone) &&
          victimLeftZone.zoneId == attackerLeftZone.zoneId)
      {
        //DelayedPlayer attack DelayedPlayer
        return true;
      }
      return null;
    }
    if (_pvpDelays.TryGetValue(attacker.userID, out var attackerLeftZone2) &&
        pvpConfig.HasFlag(PvpDelayTypes.DelayedPlayersCanDamageZonePlayers) &&
        !string.IsNullOrEmpty(attackerLeftZone2.zoneId) &&
        ZM_IsPlayerInZone(attackerLeftZone2, victim))
    {
      //DelayedPlayer attack ZonePlayer
      return true;
    }
    return null;
  }

  private static bool TP_AddOrUpdateMapping(string zoneId, string mapping) =>
    Convert.ToBoolean(
      Interface.CallHook("AddOrUpdateMapping", zoneId, mapping));

  private static bool TP_RemoveMapping(string zoneId) =>
    Convert.ToBoolean(Interface.CallHook("RemoveMapping", zoneId));

  #endregion TruePVE Integration

  #region BotReSpawn/MonBots Integration

  private bool BotReSpawnAllowed(BotDomeEvent botEvent) =>
    BotReSpawn != null &&
    botEvent is { BotsEnabled: true } &&
    !string.IsNullOrEmpty(botEvent.BotProfileName);

  private bool SpawnBots(Vector3 location, string profileName, string groupId)
  {
    if (BotReSpawn == null)
    {
      return false;
    }
    var result = BS_AddGroupSpawn(location, profileName, groupId);
    if (result == null || result.Length < 2)
    {
      PrintDebug("SpawnBots(): AddGroupSpawn returned invalid response.");
      return false;
    }
    switch (result[0])
    {
      case "true":
        return true;
      case "false":
        return false;
      case "error":
        PrintDebug($"SpawnBots(): ERROR: AddGroupSpawn failed: {result[1]}", DebugLevel.Error);
        return false;
    }
    PrintDebug($"SpawnBots(): AddGroupSpawn returned unknown response: {result[0]}.");
    return false;
  }

  private bool KillBots(string groupId)
  {
    if (BotReSpawn == null)
    {
      return true;
    }
    var result = BS_RemoveGroupSpawn(groupId);
    if (result == null || result.Length < 2)
    {
      PrintDebug("KillBots(): RemoveGroupSpawn returned invalid response.");
      return false;
    }
    if (result[0] == "error")
    {
      PrintDebug($"KillBots(): ERROR: RemoveGroupSpawn failed: {result[1]}", DebugLevel.Error);
      return false;
    }
    return true;
  }

  private string[] BS_AddGroupSpawn(
    Vector3 location, string profileName, string groupId, int quantity = 0) =>
    BotReSpawn?.Call(
      "AddGroupSpawn", location, profileName, groupId, quantity) as string[];

  private string[] BS_RemoveGroupSpawn(string groupId) =>
    BotReSpawn?.Call("RemoveGroupSpawn", groupId) as string[];

  #endregion BotReSpawn/MonBots Integration

  #region ZoneManager Integration

  private void OnEnterZone(string zoneId, BasePlayer player)
  {
    if (!player || !player.userID.IsSteamId())
    {
      return;
    }
    if (!_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
    {
      return;
    }
    Interface.CallHook("OnPlayerEnterPVP", player, zoneId);
    PrintDebug($"OnEnterZone(): {player.displayName} has entered PVP zoneId={zoneId} with eventName={baseEvent.GetName()}.");

    if (TryRemovePVPDelay(player)) return;

    if (baseEvent?.HolsterTime is not > 0) return;
    // if player is not re-entering zone while in PVP delay, check for
    //  weapon holster
    player.equippingBlocked = true;
    player.UpdateActiveItem(default);
    player.Invoke(
      () => { player.equippingBlocked = false; }, baseEvent.HolsterTime);
    Print(player, Lang("Holster", player.UserIDString));
  }

  private void OnExitZone(string zoneId, BasePlayer player)
  {
    if (!player || !player.userID.IsSteamId())
    {
      return;
    }
    if (!_activeDynamicZones.TryGetValue(zoneId, out var baseEvent))
    {
      return;
    }
    PrintDebug($"OnExitZone(): {player.displayName} has left PVP zoneId={zoneId} with eventName={baseEvent.GetName()}");

    if (baseEvent is not { PvpDelayEnabled: true } ||
        baseEvent.PvpDelayTime <= 0)
    {
      Interface.CallHook("OnPlayerExitPVP", player, zoneId);
      return;
    }
    Interface.CallHook("OnPlayerExitPVP",
      player, zoneId, baseEvent.PvpDelayTime);

    var leftZone = GetOrAddPVPDelay(player, zoneId, baseEvent);
    leftZone.zoneTimer =
      timer.Once(baseEvent.PvpDelayTime, () => { TryRemovePVPDelay(player); });
    var playerID = player.userID.Get();
    Interface.CallHook("OnPlayerAddedToPVPDelay",
      playerID, zoneId, baseEvent.PvpDelayTime, player);
    // also notify TruePVE if we're using its API to implement the delay
    if (_useExcludePlayer)
    {
      Interface.CallHook("ExcludePlayer",
        playerID, baseEvent.PvpDelayTime, this);
    }
  }

  private bool ZM_CreateOrUpdateZone(
    string zoneId, string[] zoneArgs, Vector3 location) => Convert.ToBoolean(
    ZoneManager.Call("CreateOrUpdateTemporaryZone",
      this, zoneId, zoneArgs, location));

  private bool ZM_EraseZone(string zoneId, string eventName = "")
  {
    try
    {
      return Convert.ToBoolean(
        ZoneManager.Call("EraseTemporaryZone", this, zoneId));
    }
    catch (Exception exception)
    {
      PrintDebug($"ZM_EraseZone(): ERROR: EraseTemporaryZone(zoneId={zoneId}) for eventName={eventName} failed: {exception}");
      return true;
    }
  }

  private void ZM_GetZoneIDs(List<string> list) =>
    ZoneManager.Call("GetZoneIDsNoAlloc", list);

  private string ZM_GetZoneName(string zoneId) =>
    Convert.ToString(ZoneManager.Call("GetZoneName", zoneId));

  private ZoneManager.Zone ZM_GetZoneByID(string zoneId) =>
    ZoneManager.Call("GetZoneByID", zoneId) as ZoneManager.Zone;

  private void ZM_GetPlayerZoneIDs(BasePlayer player, List<string> list) =>
    ZoneManager.Call("GetPlayerZoneIDsNoAlloc", player, list);

  private bool ZM_IsPlayerInZone(LeftZone leftZone, BasePlayer player) =>
    Convert.ToBoolean(
      ZoneManager.Call("IsPlayerInZone", leftZone.zoneId, player));

  private void ZM_GetPlayersInZone(string zoneId, List<BasePlayer> list) =>
    ZoneManager.Call("GetPlayersInZoneNoAlloc", zoneId, list);

  // parent event's zone and (if applicable) domes to a given entity, so that
  //  they move together
  private void ParentEventToEntity(
    string zoneId, BaseEvent baseEvent, BaseEntity parentEntity,
    bool deleteOnFailure, bool delay = true)
  {
    if (delay)
    {
      timer.Once(0.25f, () => ParentEventToEntity(
        zoneId, baseEvent, parentEntity, deleteOnFailure, false));
      return;
    }
    var zone = ZM_GetZoneByID(zoneId);
    if (!parentEntity || !zone)
    {
      PrintDebug($"ParentEventToEntity(): ERROR: The zoneId={zoneId} has null zone={zone} and/or parentEntity={parentEntity}.", DebugLevel.Error);
      if (deleteOnFailure) DeleteDynamicZone(zoneId);
      return;
    }
    // only support parenting if event implements IParentZone
    if (baseEvent.GetDynamicZone() is not IParentZone parentZone)
    {
      PrintDebug($"ParentEventToEntity(): ERROR: Not parenting zoneId={zoneId} to parentEntity={parentEntity} because event's DynamicZone does not implement IParentZone.", DebugLevel.Error);
      if (deleteOnFailure) DeleteDynamicZone(zoneId);
      return;
    }
    var zoneTransform = zone.transform;
    var position = parentEntity.transform.TransformPoint(parentZone.Center);
    zoneTransform.SetParent(parentEntity.transform);
    zoneTransform.rotation = parentEntity.transform.rotation;
    zoneTransform.position = position;
    PrintDebug($"ParentEventToEntity(): Parented zoneId={zoneId} to parentEntity={parentEntity}.");
    // also parent any domes
    ParentDome(zoneId, position, parentEntity);
  }

  #endregion ZoneManager Integration

  #region Backpacks/LootDefender/RestoreUponDeath Integration

  /// return whether zones for the given base event are relevant to the given
  ///  plugin integration category
  private static bool HasPluginZoneCategory(
    BaseEvent baseEvent, PluginZoneCategory category) =>
    category switch
    {
      PluginZoneCategory.BackpacksForce =>
        baseEvent.DropPluginBackpacks == true,
      PluginZoneCategory.BackpacksPrevent =>
        baseEvent.DropPluginBackpacks == false,
      PluginZoneCategory.LootDefender =>
        baseEvent.BypassLootDefenderLocks,
      PluginZoneCategory.RestoreUponDeath =>
        baseEvent.BlockRestoreUponDeath,
      _ => false
    };

  /// get the hook category enum value corresponding to the given plugin zone
  ///  category value
  private static HookCategory ToHookCategory(
    PluginZoneCategory pluginZoneCategory) =>
    pluginZoneCategory switch
    {
      PluginZoneCategory.BackpacksForce =>
        HookCategory.PluginBackpacksForce,
      PluginZoneCategory.BackpacksPrevent =>
        HookCategory.PluginBackpacksPrevent,
      PluginZoneCategory.LootDefender =>
        HookCategory.PluginLootDefender,
      PluginZoneCategory.RestoreUponDeath =>
        HookCategory.PluginRestoreUponDeath,
      _ => throw new ArgumentOutOfRangeException(nameof(pluginZoneCategory))
    };

  /// add/remove _activePluginZones HashSet entries for any plugin integration
  ///  categories associated with the base event for a given zone that's being
  ///  added/removed
  private void UpdateActivePluginZones(
    bool added, string zoneId, BaseEvent baseEvent)
  {
    // check each possible plugin integration category
    foreach (var (category, zoneSet) in _activePluginZones)
    {
      // skip base events without this plugin integration active
      if (!HasPluginZoneCategory(baseEvent, category))
      {
        continue;
      }

      if (added)
      {
        // add new zone to plugin integration category active zones
        zoneSet.Add(zoneId);
      }
      else
      {
        // remove defunct zone from plugin integration category active zones
        zoneSet.Remove(zoneId);
      }
    }
  }

  /// return true if player is determined to be in a zone of the given plugin
  ///  integration category, else false
  private bool PlayerInActivePluginZone(
    BasePlayer player, PluginZoneCategory pzCat)
  {
    // abort if not a valid, real player
    if (!player || !player.userID.IsSteamId())
    {
      return false;
    }

    // abort if we don't have a zone set for requested category (pathological)
    if (!_activePluginZones.TryGetValue(pzCat, out var zoneSet))
    {
      return false;
    }

    // get zones player is in, on the assumption that backpack is dropping at
    //  their position, as this is much more efficient to check than a raw
    //  position
    var zoneIDs = Pool.Get<List<string>>();
    ZM_GetPlayerZoneIDs(player, zoneIDs);

    // check to see if any of the player's zones are in the set of active
    //  zones for the relevant plugin integration
    foreach (var zoneId in zoneIDs)
    {
      if (string.IsNullOrEmpty(zoneId) || !zoneSet.Contains(zoneId)) continue;
      // report match
      Pool.FreeUnmanaged(ref zoneIDs);
      return true;
    }

    // no match found
    Pool.FreeUnmanaged(ref zoneIDs);
    return false;
  }

  /// hook handler: return false to prevent plugin Backpacks drop if owner ID
  ///  can be resolved to a BasePlayer who is in a drop-prevented zone, else
  ///  return true to allow backpack drop
  private bool CanDropBackpack(ulong backpackOwnerID, Vector3 position) =>
    !BasePlayer.TryFindByID(backpackOwnerID, out var player) ||
    !PlayerInActivePluginZone(player, PluginZoneCategory.BackpacksPrevent);

  /// request plugin Backpacks drop if player dies in a drop-forced zone
  private void OnPlayerDeath(BasePlayer player, HitInfo info)
  {
    if (PlayerInActivePluginZone(player, PluginZoneCategory.BackpacksForce))
    {
      // request backpack drop
      Backpacks?.Call("API_DropBackpack", player);
    }
  }

  /// hook handler: return true to bypass Loot Defender locks if player is in
  ///  a bypass-enabled zone, else return null to take no action
  private object OnLootLockedEntity(BasePlayer player, BaseEntity entity) =>
    PlayerInActivePluginZone(player, PluginZoneCategory.LootDefender) ?
      true : null;

  /// hook handler: return true to block Restore Upon Death if player is in a
  ///  restore-blocked zone, else return null to take no action
  private object OnRestoreUponDeath(BasePlayer player) =>
    PlayerInActivePluginZone(player, PluginZoneCategory.RestoreUponDeath) ?
      true : null;

  #endregion Backpacks/LootDefender/RestoreUponDeath Integration

  #region Debug

  private StringBuilder _debugStringBuilder;

  private enum DebugLevel { Error, Warning, Info };

  private void PrintDebug(string message, DebugLevel level = DebugLevel.Info)
  {
    if (_configData.Global.DebugEnabled)
    {
      switch (level)
      {
        case DebugLevel.Error:   PrintError(message);   break;
        case DebugLevel.Warning: PrintWarning(message); break;
        case DebugLevel.Info:    Puts(message);         break;
      }
    }

    if (_configData.Global.LogToFile)
    {
      _debugStringBuilder.AppendLine($"[{DateTime.Now.ToString(CultureInfo.InstalledUICulture)}] | {message}");
    }
  }

  private void SaveDebug()
  {
    if (!_configData.Global.LogToFile)
    {
      return;
    }
    var debugText = _debugStringBuilder.ToString().Trim();
    _debugStringBuilder.Clear();
    if (!string.IsNullOrEmpty(debugText))
    {
      LogToFile("debug", debugText, this);
    }
  }

  #endregion Debug

  #region API

  private string[] AllDynamicPVPZones()
  {
    // this is tortured, but we do it to avoid Linq
    var retVal = new string[_activeDynamicZones.Count];
    var i = 0;
    foreach (var key in _activeDynamicZones.Keys) retVal[i++] = key;
    return retVal;
  }

  private void AllDynamicPVPZonesNoAlloc(List<string> list) =>
    list.AddRange(_activeDynamicZones.Keys);

  private bool IsDynamicPVPZone(string zoneId) =>
    _activeDynamicZones.ContainsKey(zoneId);

  private bool EventDataExists(string eventName) =>
    _storedData.EventDataExists(eventName);

  private bool IsPlayerInPVPDelay(ulong playerId) =>
    _pvpDelays.ContainsKey(playerId);

  private string GetPlayerPVPDelayedZoneID(ulong playerId) =>
    _pvpDelays.TryGetValue(playerId, out var leftZone) ?
      leftZone.zoneId : null;

  private string GetEventName(string zoneId) =>
    _activeDynamicZones.TryGetValue(zoneId, out var baseEvent) ?
      baseEvent.GetName() : null;

  private bool CreateOrUpdateEventData(
    string eventName, string eventData, bool isTimed = false)
  {
    if (string.IsNullOrEmpty(eventName) || string.IsNullOrEmpty(eventData))
    {
      return false;
    }
    if (EventDataExists(eventName))
    {
      RemoveEventData(eventName);
    }
    if (isTimed)
    {
      CustomTimedEvent timedEvent;
      try
      {
        timedEvent = JsonConvert.DeserializeObject<CustomTimedEvent>(eventData);
      }
      catch (Exception exception)
      {
        PrintDebug($"CreateOrUpdateEventData(): Exception while processing eventData<CustomTimedEvent> for eventName={eventName}: {exception}");
        return false;
      }

      timedEvent.EventName = eventName;
      _storedData.timedEvents.Add(eventName, timedEvent);
    }
    else
    {
      AutoEvent autoEvent;
      try
      {
        autoEvent = JsonConvert.DeserializeObject<AutoEvent>(eventData);
      }
      catch (Exception exception)
      {
        PrintDebug($"CreateOrUpdateEventData(): Exception while processing eventData<AutoEvent> for eventName={eventName}: {exception}");
        return false;
      }

      autoEvent.EventName = eventName;
      _storedData.autoEvents.Add(eventName, autoEvent);
      if (autoEvent.AutoStart)
      {
        CreateDynamicZone(autoEvent, autoEvent.Position, autoEvent.ZoneId);
      }
    }
    _dataChanged = true;
    return true;
  }

  private bool CreateEventData(
    string eventName, Vector3 position, bool isTimed)
  {
    if (EventDataExists(eventName))
    {
      return false;
    }
    if (isTimed)
    {
      _storedData.timedEvents.Add(
        eventName, new CustomTimedEvent { EventName = eventName });
    }
    else
    {
      _storedData.autoEvents.Add(
        eventName, new AutoEvent { Position = position, EventName = eventName});
    }
    _dataChanged = true;
    return true;
  }

  private bool RemoveEventData(string eventName, bool forceClose = true)
  {
    if (!_storedData.RemoveEventData(eventName))
    {
      return false;
    }
    if (forceClose)
    {
      ForceCloseZones(eventName);
    }
    _dataChanged = true;
    return true;
  }

  private bool StartEvent(string eventName, Vector3 position) =>
    _storedData.GetEvent(eventName) switch
    {
      AutoEvent autoEvent =>
        CreateDynamicZone(
          autoEvent,
          position == default ? autoEvent.Position : position,
          autoEvent.ZoneId),
      CustomTimedEvent customTimedEvent =>
        CreateDynamicZone(customTimedEvent, position),
      _ => false
    };

  private bool StopEvent(string eventName) =>
    EventDataExists(eventName) && ForceCloseZones(eventName);

  private bool ForceCloseZones(string eventName)
  {
    var closed = false;
    // create a temporary list of _activeDynamicZones entries, because deleting
    //  any will cause modifications to the latter
    var entries = Pool.Get<List<KeyValuePair<string, BaseEvent>>>();
    entries.AddRange(_activeDynamicZones);
    foreach (var (zoneId, baseEvent) in entries)
    {
      if (baseEvent.GetName() == eventName && DeleteDynamicZone(zoneId))
      {
        closed = true;
      }
    }
    Pool.FreeUnmanaged(ref entries);
    return closed;
  }

  private bool IsUsingExcludePlayer() => _useExcludePlayer;

  #endregion API

  #region Commands

  private static void DrawCube(
    BasePlayer player, float duration, Color color,
    Vector3 pos, Vector3 size, float rotation)
  {
    // this is complicated because ddraw doesn't have a rectangular prism
    //  rendering option, so we need to figure out where all the rotated
    //  vertices are and then draw all the edges
    var halfSize = size / 2;
    Vector3[] vertices =
    {
      // corners
      new(pos.x + halfSize.x, pos.y + halfSize.y, pos.z + halfSize.z),
      new(pos.x + halfSize.x, pos.y + halfSize.y, pos.z - halfSize.z),
      new(pos.x + halfSize.x, pos.y - halfSize.y, pos.z + halfSize.z),
      new(pos.x + halfSize.x, pos.y - halfSize.y, pos.z - halfSize.z),
      new(pos.x - halfSize.x, pos.y + halfSize.y, pos.z + halfSize.z),
      new(pos.x - halfSize.x, pos.y + halfSize.y, pos.z - halfSize.z),
      new(pos.x - halfSize.x, pos.y - halfSize.y, pos.z + halfSize.z),
      new(pos.x - halfSize.x, pos.y - halfSize.y, pos.z - halfSize.z),
      // axes
      new(pos.x, pos.y, pos.z),
      new(pos.x + halfSize.x, pos.y, pos.z),
      new(pos.x, pos.y + halfSize.y, pos.z),
      new(pos.x, pos.y, pos.z + halfSize.z)
    };

    // rotate all the points
    var rotQ = Quaternion.Euler(0, rotation, 0);
    for (var i = 0; i < vertices.Length; ++i)
    {
      vertices[i] = rotQ * (vertices[i] - pos) + pos;
    }

    // corners
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[0], vertices[1]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[0], vertices[2]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[0], vertices[4]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[1], vertices[3]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[1], vertices[5]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[2], vertices[3]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[2], vertices[6]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[3], vertices[7]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[4], vertices[5]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[4], vertices[6]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[5], vertices[7]);
    player.SendConsoleCommand(
      "ddraw.line",  duration, color, vertices[6], vertices[7]);
    // axes
    player.SendConsoleCommand(
      "ddraw.arrow", duration, Color.red,   vertices[8], vertices[9],  5);
    player.SendConsoleCommand(
      "ddraw.arrow", duration, Color.green, vertices[8], vertices[10], 5);
    player.SendConsoleCommand(
      "ddraw.arrow", duration, Color.blue,  vertices[8], vertices[11], 5);
    player.SendConsoleCommand(
      "ddraw.text",  duration, Color.red,   vertices[9],  "+x");
    player.SendConsoleCommand(
      "ddraw.text",  duration, Color.green, vertices[10], "+y");
    player.SendConsoleCommand(
      "ddraw.text",  duration, Color.blue,  vertices[11], "+z");
  }

  private static void DrawSphere(
    BasePlayer player, float duration, Color color,
    Vector3 pos, float radius, float rotation)
  {
    player.SendConsoleCommand(
      "ddraw.sphere", duration, color, pos, radius);

    // axes
    Vector3[] vertices =
    {
      new(pos.x,          pos.y,          pos.z),
      new(pos.x + radius, pos.y,          pos.z),
      new(pos.x,          pos.y + radius, pos.z),
      new(pos.x,          pos.y,          pos.z + radius)
    };

    // rotate all the points
    var rotQ = Quaternion.Euler(0, rotation, 0);
    for (var i = 0; i < vertices.Length; ++i)
    {
      vertices[i] = (rotQ * (vertices[i] - pos)) + pos;
    }

    player.SendConsoleCommand(
      "ddraw.arrow", duration, Color.red,   vertices[0], vertices[1], 5);
    player.SendConsoleCommand(
      "ddraw.arrow", duration, Color.green, vertices[0], vertices[2], 5);
    player.SendConsoleCommand(
      "ddraw.arrow", duration, Color.blue,  vertices[0], vertices[3], 5);
    player.SendConsoleCommand(
      "ddraw.text",  duration, Color.red,    vertices[1], "+x");
    player.SendConsoleCommand(
      "ddraw.text",  duration, Color.green,  vertices[2], "+y");
    player.SendConsoleCommand(
      "ddraw.text",  duration, Color.blue,   vertices[3], "+z");
  }

  private void CommandHelp(IPlayer iPlayer)
  {
    var stringBuilder = Pool.Get<StringBuilder>();
    var result = stringBuilder
        .Clear()
        .AppendLine()
        .AppendLine(Lang("Syntax",  iPlayer.Id, _configData.Chat.Command))
        .AppendLine(Lang("Syntax1", iPlayer.Id, _configData.Chat.Command))
        .AppendLine(Lang("Syntax2", iPlayer.Id, _configData.Chat.Command))
        .AppendLine(Lang("Syntax3", iPlayer.Id, _configData.Chat.Command))
        .AppendLine(Lang("Syntax4", iPlayer.Id, _configData.Chat.Command))
        .AppendLine(Lang("Syntax5", iPlayer.Id, _configData.Chat.Command))
        .AppendLine(Lang("Syntax6", iPlayer.Id, _configData.Chat.Command))
        .AppendLine(Lang("Syntax7", iPlayer.Id, _configData.Chat.Command))
        .AppendLine(Lang("Syntax8", iPlayer.Id, _configData.Chat.Command))
        .ToString()
      ;
    stringBuilder.Clear();
    Pool.FreeUnmanaged(ref stringBuilder);
    Print(iPlayer, result);
  }

  private void CommandList(IPlayer iPlayer)
  {
    var customEventCount = _storedData.CustomEventsCount;
    if (customEventCount <= 0)
    {
      Print(iPlayer, Lang("NoCustomEvent", iPlayer.Id));
      return;
    }
    var i = 0;
    var stringBuilder = Pool.Get<StringBuilder>();
    stringBuilder.Clear();
    stringBuilder.AppendLine(Lang("CustomEvents",
      iPlayer.Id, customEventCount));
    foreach (var entry in _storedData.autoEvents)
    {
      i++;
      stringBuilder.AppendLine(Lang("AutoEvent",
        iPlayer.Id, i,
        entry.Key, entry.Value.AutoStart, entry.Value.Position));
    }
    foreach (var entry in _storedData.timedEvents)
    {
      i++;
      stringBuilder.AppendLine(Lang("TimedEvent",
        iPlayer.Id, i, entry.Key, entry.Value.Duration));
    }
    Print(iPlayer, stringBuilder.ToString());
    stringBuilder.Clear();
    Pool.FreeUnmanaged(ref stringBuilder);
  }

  private void CommandShow(BasePlayer player)
  {
    if (!player)
    {
      PrintDebug("CommandShow(): Got null player; aborting", DebugLevel.Error);
      return;
    }

    Print(player, Lang("ShowingZones", player.UserIDString, _configData.Chat.ShowDistance, _configData.Chat.ShowDuration));

    var playerPosition2D = player.transform.position.XZ2D();
    foreach (var (zoneId, baseEvent) in _activeDynamicZones)
    {
      if (null == baseEvent) continue;
      var zoneData = ZM_GetZoneByID(zoneId);
      if (!zoneData) continue;
      var position = zoneData.transform.position;
      if (Vector2.Distance(playerPosition2D, position.XZ2D()) >
          _configData.Chat.ShowDistance)
      {
        continue;
      }
      var baseZone = baseEvent.GetDynamicZone();
      var zoneColor = baseZone switch
      {
        SphereCubeDynamicZone       => Color.yellow,
        SphereCubeParentDynamicZone => Color.blue,
        _                           => Color.red
      };

      switch (zoneData.collider)
      {
        case BoxCollider b:
          DrawCube(
            player, _configData.Chat.ShowDuration, zoneColor,
            zoneData.transform.position, b.size,
            zoneData.transform.eulerAngles.y);
          break;

        case SphereCollider s:
          DrawSphere(
            player, _configData.Chat.ShowDuration, zoneColor,
            zoneData.transform.position, s.radius,
            zoneData.transform.eulerAngles.y);
          break;
      }

      player.SendConsoleCommand(
        "ddraw.text", _configData.Chat.ShowDuration, zoneColor,
        zoneData.transform.position,
        $"{zoneId}\n{baseEvent.GetName()}");
    }
  }

  private void CommandEdit(
    IPlayer iPlayer, string eventName, Vector3 position, string arg)
  {
    if (_storedData.autoEvents.TryGetValue(eventName, out var autoEvent))
    {
      switch (arg.ToLower())
      {
        case "0":
        case "false":
        {
          autoEvent.AutoStart = false;
          Print(iPlayer, Lang("AutoEventAutoStart",
            iPlayer.Id, eventName, false));
          _dataChanged = true;
          return;
        }

        case "1":
        case "true":
        {
          autoEvent.AutoStart = true;
          Print(iPlayer, Lang("AutoEventAutoStart",
            iPlayer.Id, eventName, true));
          _dataChanged = true;
          return;
        }

        case "move":
        {
          autoEvent.Position = position;
          Print(iPlayer, Lang("AutoEventMove", iPlayer.Id, eventName));
          _dataChanged = true;
          return;
        }
      }
    }
    else if (_storedData.timedEvents.TryGetValue(eventName, out var timedEvent)
             && float.TryParse(arg, out var duration))
    {
      timedEvent.Duration = duration;
      Print(iPlayer, Lang("TimedEventDuration",
        iPlayer.Id, eventName, duration));
      _dataChanged = true;
      return;
    }
    Print(iPlayer, Lang("SyntaxError", iPlayer.Id, _configData.Chat.Command));
  }

  private void CmdDynamicPVP(IPlayer iPlayer, string command, string[] args)
  {
    if (!iPlayer.IsAdmin && !iPlayer.HasPermission(PermissionAdmin))
    {
      Print(iPlayer, Lang("NotAllowed", iPlayer.Id));
      return;
    }
    if (args == null || args.Length < 1)
    {
      Print(iPlayer, Lang("SyntaxError", iPlayer.Id, _configData.Chat.Command));
      return;
    }
    var commandName = args[0].ToLower();
    // check command and dispatch to appropriate handler
    switch (commandName)
    {
      case "?":
      case "h":
      case "help":
      {
        CommandHelp(iPlayer);
        return;
      }

      case "list":
      {
        CommandList(iPlayer);
        return;
      }

      case "show":
      {
        CommandShow(iPlayer.Object as BasePlayer);
        return;
      }
    }
    // handle commands that take additional parameters
    var eventName = args[1];
    var position =
      (iPlayer.Object as BasePlayer)?.transform.position ?? Vector3.zero;
    switch (commandName)
    {
      case "add":
      {
        var isTimed = args.Length >= 3;
        Print(iPlayer, CreateEventData(eventName, position, isTimed) ?
          Lang("EventDataAdded", iPlayer.Id, eventName) :
          Lang("EventNameExist", iPlayer.Id, eventName));
        return;
      }

      case "remove":
      {
        Print(iPlayer, RemoveEventData(eventName) ?
          Lang("EventDataRemoved", iPlayer.Id, eventName) :
          Lang("EventNameNotExist", iPlayer.Id, eventName));
        return;
      }

      case "start":
      {
        Print(iPlayer, StartEvent(eventName, position) ?
          Lang("EventStarted", iPlayer.Id, eventName) :
          Lang("EventNameNotExist", iPlayer.Id, eventName));
        return;
      }

      case "stop":
      {
        Print(iPlayer, StopEvent(eventName) ?
          Lang("EventStopped", iPlayer.Id, eventName) :
          Lang("EventNameNotExist", iPlayer.Id, eventName));
        return;
      }

      case "edit":
      {
        if (args.Length >= 3)
        {
          CommandEdit(iPlayer, eventName, position, args[2]);
          return;
        }
        break;
      }
    }
    Print(iPlayer, Lang("SyntaxError", iPlayer.Id, _configData.Chat.Command));
  }

  #endregion Commands

  #region ConfigurationFile

  private ConfigData _configData;

  private sealed class ConfigData
  {
    [JsonProperty(PropertyName = "Global Settings")]
    public GlobalSettings Global { get; set; } = new();

    [JsonProperty(PropertyName = "Chat Settings")]
    public ChatSettings Chat { get; set; } = new();

    [JsonProperty(PropertyName = "General Event Settings")]
    public GeneralEventSettings GeneralEvents { get; set; } = new();

    [JsonProperty(PropertyName = "Deep Sea Event Settings")]
    public DeepSeaEventSettings DeepSeaEvents { get; set; } = new();

    [JsonProperty(PropertyName = "Monument Event Settings")]
    public SortedDictionary<string, MonumentEvent> MonumentEvents { get; set; }
      = new();

    [JsonProperty(PropertyName = "Version")]
    public VersionNumber Version { get; set; }

    // copy each dictionary event's key into its name field to support
    //  BaseEvent.GetName() API, which is needed for hook calls and logging
    public void PopulateEventNames()
    {
      foreach (var (monumentEventName, monumentEvent) in MonumentEvents)
      {
        monumentEvent.EventName = monumentEventName;
      }
    }
  }

  private sealed class GlobalSettings
  {
    [JsonProperty(PropertyName = "Enable Debug Mode")]
    public bool DebugEnabled { get; set; }

    [JsonProperty(PropertyName = "Log Debug To File")]
    public bool LogToFile { get; set; }

    [JsonProperty(PropertyName = "Compare Radius (Used to determine if it is a SupplySignal)")]
    public float CompareRadius { get; set; } = 2f;

    [JsonProperty(PropertyName = "If the entity has an owner, don't create a PVP zone")]
    public bool CheckEntityOwner { get; set; } = true;

    [JsonProperty(PropertyName = "Use TruePVE PVP Delay API (more efficient and cross-plugin, but supersedes PVP Delay Flags)")]
    public bool UseExcludePlayer { get; set; }

    [JsonProperty(PropertyName = "PVP Delay Flags")]
    public PvpDelayTypes PvpDelayFlags { get; set; } =
      PvpDelayTypes.ZonePlayersCanDamageDelayedPlayers |
      PvpDelayTypes.DelayedPlayersCanDamageDelayedPlayers |
      PvpDelayTypes.DelayedPlayersCanDamageZonePlayers;
  }

  private sealed class ChatSettings
  {
    [JsonProperty(PropertyName = "Command")]
    public string Command { get; set; } = "dynpvp";

    [JsonProperty(PropertyName = "Chat Prefix")]
    public string Prefix { get; set; } = "[DynamicPVP]: ";

    [JsonProperty(PropertyName = "Chat Prefix Color")]
    public string PrefixColor { get; set; } = "#00FFFF";

    [JsonProperty(PropertyName = "Chat SteamID Icon")]
    public ulong SteamIdIcon { get; set; }

    [JsonProperty(PropertyName = "Zone Show Distance")]
    public float ShowDistance { get; set; } = 1000.0f;

    [JsonProperty(PropertyName = "Zone Show Duration (in seconds)")]
    public float ShowDuration { get; set; } = 15.0f;
  }

  private sealed class GeneralEventSettings
  {
    [JsonProperty(PropertyName = "Bradley Event")]
    public BradleyEvent BradleyApc { get; set; } = new();

    [JsonProperty(PropertyName = "Patrol Helicopter Event")]
    public HelicopterEvent PatrolHelicopter { get; set; } = new();

    [JsonProperty(PropertyName = "Supply Signal Event")]
    public SupplySignalEvent SupplySignal { get; set; } = new();

    [JsonProperty(PropertyName = "Timed Supply Event")]
    public TimedSupplyEvent TimedSupply { get; set; } = new();

    [JsonProperty(PropertyName = "Hackable Crate Event")]
    public HackableCrateEvent HackableCrate { get; set; } = new();

    [JsonProperty(PropertyName = "Excavator Ignition Event")]
    public IgnitionEvent ExcavatorIgnition { get; set; } = new();

    [JsonProperty(PropertyName = "Cargo Ship Event")]
    public CargoShipEvent CargoShip { get; set; } = new();
  }

  private sealed class DeepSeaEventSettings
  {
    [JsonProperty(PropertyName = "Deep Sea Ghost Ship Event")]
    public GhostShipEvent GhostShip { get; set; } = new();

    [JsonProperty(PropertyName = "Deep Sea Island Event")]
    public DeepSeaIslandEvent DeepSeaIsland { get; set; } = new();

    [JsonProperty(PropertyName = "Deep Sea Island Cannon Event")]
    public IslandCannonEvent IslandCannon { get; set; } = new();
  }

  #region Event

  // base class for ALL DynamicPVP events
  // NOTE: reserve order 1-19
  public abstract class BaseEvent
  {
    [JsonProperty(PropertyName = "Enable Event", Order = 1)]
    public bool Enabled { get; set; }

    [JsonProperty(PropertyName = "Delay In Starting Event", Order = 2)]
    public float EventStartDelay { get; set; }

    [JsonProperty(PropertyName = "Delay In Stopping Event", Order = 3)]
    public float EventStopDelay { get; set; }

    [JsonProperty(PropertyName = "Holster Time On Enter (In seconds, or 0 to disable)", Order = 4)]
    public float HolsterTime { get; set; }

    [JsonProperty(PropertyName = "Enable PVP Delay", Order = 5)]
    public bool PvpDelayEnabled { get; set; }

    [JsonProperty(PropertyName = "PVP Delay Time", Order = 6)]
    public float PvpDelayTime { get; set; } = 10f;

    [JsonProperty(PropertyName = "TruePVE Mapping", Order = 7)]
    public string Mapping { get; set; } = "exclude";

    [JsonProperty(PropertyName = "Use Blacklist Commands (If false, a whitelist is used)", Order = 8)]
    public bool UseBlacklistCommands { get; set; } = true;

    [JsonProperty(PropertyName = "Command works for PVP delayed players", Order = 9)]
    public bool CommandWorksForPVPDelay { get; set; }

    [JsonProperty(PropertyName = "Command List (If there is a '/' at the front, it is a chat command)", Order = 10)]
    public List<string> CommandList { get; set; } = new();

    [JsonProperty(PropertyName = "Drop plugin Backpacks on death (null disables override)", Order = 11)]
    public bool? DropPluginBackpacks { get; set; }

    [JsonProperty(PropertyName = "Bypass Loot Defender locks", Order = 12)]
    public bool BypassLootDefenderLocks { get; set; }

    [JsonProperty(PropertyName = "Block Restore Upon Death", Order = 13)]
    public bool BlockRestoreUponDeath { get; set; }

    public abstract BaseDynamicZone GetDynamicZone();

    public abstract string GetName();
  }

  // dome features
  // NOTE: reserve order 20-24
  public abstract class DomeEvent : BaseEvent
  {
    // obsolete field
    [JsonProperty(PropertyName = "Enable Domes", NullValueHandling = NullValueHandling.Ignore)]
    public bool? ObeDomesEnabled { get; set; }

    // obsolete field
    [JsonProperty(PropertyName = "Domes Darkness", NullValueHandling = NullValueHandling.Ignore)]
    public int? ObeDomesDarkness { get; set; }

    [JsonProperty(PropertyName = "Dome Settings", Order = 20)]
    public DomeSettings DomeData { get; set; } = new();

    // this is a temporary list to support migration from obsolete dome settings
    [JsonIgnore] public static List<DomeEvent> _domeEventsToCheck;

    // self-register all instances for obsolete data migration check
    protected DomeEvent()
    {
      // lazily instantiate
      _domeEventsToCheck ??= new List<DomeEvent>();
      _domeEventsToCheck?.Add(this);
    }

    public static void Migrate()
    {
      // 4.9.0 dome settings migration check
      foreach (var domeEvent in _domeEventsToCheck)
      {
        switch (domeEvent.ObeDomesEnabled)
        {
          case null:
            // only care about darkness if domes were enabled
            domeEvent.ObeDomesDarkness = null;
            continue;
          case false:
            domeEvent.DomeData.Darkness = 0;
            break;
          case true:
            domeEvent.DomeData.Darkness = domeEvent.ObeDomesDarkness is > 0 ?
              domeEvent.ObeDomesDarkness.Value : 0;
            break;
        }

        domeEvent.ObeDomesEnabled = null;
        domeEvent.ObeDomesDarkness = null;
      }
      // clear the list; it may get reused for both config and data files
      _domeEventsToCheck.Clear();
    }
  }

  // NOTE: reserve order 25-34
  public sealed class DomeSettings
  {
    [JsonProperty(PropertyName = "Dome Darkness (0 to disable)", Order = 25)]
    public int Darkness { get; set; } = 8;

    [JsonProperty(PropertyName = "Enable Red Ring", Order = 26)]
    public bool RedRing { get; set; }

    [JsonProperty(PropertyName = "Enable Green Ring", Order = 27)]
    public bool GreenRing { get; set; }

    [JsonProperty(PropertyName = "Enable Blue Ring", Order = 28)]
    public bool BlueRing { get; set; }

    [JsonProperty(PropertyName = "Enable Purple Ring", Order = 29)]
    public bool PurpleRing { get; set; }

    public bool DomeCreateAllowed(ISphereZone sphereZone) =>
      sphereZone?.Radius > 0f &&
      (Darkness > 0 || RedRing || GreenRing || BlueRing || PurpleRing);
  }

  // bot features
  // NOTE: reserve order 35-39
  public abstract class BotDomeEvent : DomeEvent
  {
    [JsonProperty(PropertyName = "Enable Bots (Need BotSpawn Plugin)", Order = 35)]
    public bool BotsEnabled { get; set; }

    [JsonProperty(PropertyName = "BotSpawn Profile Name", Order = 36)]
    public string BotProfileName { get; set; } = "";
  }

  // Excavator Ignition general event (split off from MonumentEvent because it
  //  doesn't support auto-geo)
  // NOTE: reserve order 40-44
  public class IgnitionEvent : DomeEvent, IGeneralEvent
  {
    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 40)]
    public SphereCubeDynamicZone DynamicZone { get; set; } = new();

    [JsonProperty(PropertyName = "Zone ID", Order = 41)]
    public string ZoneId { get; set; } = "";

    [JsonProperty(PropertyName = "Transform Position", Order = 42)]
    public Vector3 TransformPosition { get; set; }

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;

    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.ExcavatorIgnition;

    public override string GetName() =>
      nameof(GeneralEventType.ExcavatorIgnition);
  }

  // NOTE: reserve order 45-49
  // Monument event
  public class MonumentEvent : DomeEvent
  {
    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 45)]
    public SphereCubeAutoGeoDynamicZone DynamicZone { get; set; } = new();

    [JsonProperty(PropertyName = "Zone ID", Order = 46)]
    public string ZoneId { get; set; } = "";

    [JsonProperty(PropertyName = "Transform Position", Order = 47)]
    public Vector3 TransformPosition { get; set; }

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;

    [JsonIgnore] public string EventName { get; set; }

    public override string GetName() => EventName;
  }

  // user-defined "auto" event
  // NOTE: reserve order 50-59
  public class AutoEvent : BotDomeEvent
  {
    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 50)]
    public SphereCubeDynamicZone DynamicZone { get; set; } = new();

    [JsonProperty(PropertyName = "Auto Start", Order = 51)]
    public bool AutoStart { get; set; }

    [JsonProperty(PropertyName = "Zone ID", Order = 52)]
    public string ZoneId { get; set; } = "";

    [JsonProperty(PropertyName = "Position", Order = 53)]
    public Vector3 Position { get; set; }

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;

    [JsonIgnore] public string EventName { get; set; }

    public override string GetName() => EventName;
  }

  // base class for events that support a duration
  // NOTE: reserve order 60-64
  public abstract class BaseTimedEvent : BotDomeEvent
  {
    [JsonProperty(PropertyName = "Event Duration", Order = 60)]
    public float Duration { get; set; } = 600f;
  }

  // Base class for Bradley / Patrol Helicopter general events and user-defined
  //  "timed" events
  // NOTE: reserve order 65-69
  public abstract class SphereCubeTimedEvent : BaseTimedEvent
  {
    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 65)]
    public SphereCubeDynamicZone DynamicZone { get; set; } = new();

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;
  }

  public class CustomTimedEvent : SphereCubeTimedEvent
  {
    [JsonIgnore] public string EventName { get; set; }

    public override string GetName() => EventName;
  }

  public class BradleyEvent : SphereCubeTimedEvent, IGeneralEvent
  {
    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.Bradley;

    public override string GetName() => nameof(GeneralEventType.Bradley);
  }

  public class HelicopterEvent : SphereCubeTimedEvent, IGeneralEvent
  {
    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.Helicopter;

    public override string GetName() => nameof(GeneralEventType.Helicopter);
  }

  // Hackable Crate general event
  // NOTE: reserve order 70-79
  public class HackableCrateEvent :
    SphereCubeTimedEvent, ITimedDisable, IGeneralEvent
  {
    [JsonProperty(PropertyName = "Start Event When Spawned (If false, the event starts when unlocking)", Order = 71)]
    public bool StartWhenSpawned { get; set; } = true;

    [JsonProperty(PropertyName = "Stop Event When Killed", Order = 72)]
    public bool StopWhenKilled { get; set; }

    [JsonProperty(PropertyName = "Event Timer Starts When Looted", Order = 73)]
    public bool TimerStartWhenLooted { get; set; }

    [JsonProperty(PropertyName = "Event Timer Starts When Unlocked", Order = 74)]
    public bool TimerStartWhenUnlocked { get; set; }

    [JsonProperty(PropertyName = "Excluding Hackable Crate On OilRig", Order = 75)]
    public bool ExcludeOilRig { get; set; } = true;

    [JsonProperty(PropertyName = "Excluding Hackable Crate on Cargo Ship", Order = 76)]
    public bool ExcludeCargoShip { get; set; } = true;

    [JsonProperty(PropertyName = "Excluding Hackable Crate on Ghost Ship", Order = 76)]
    public bool ExcludeGhostShip { get; set; } = true;

    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.HackableCrate;

    public override string GetName() => nameof(GeneralEventType.HackableCrate);

    public bool IsTimedDisabled()
    {
      return StopWhenKilled || TimerStartWhenLooted || TimerStartWhenUnlocked;
    }
  }

  // Supply Signal / Timed Supply general event
  // NOTE: reserve order 80-89
  public abstract class SupplyDropEvent : BaseTimedEvent, ITimedDisable
  {
    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 80)]
    public SphereCubeParentDynamicZone DynamicZone { get; set; } = new();

    [JsonProperty(PropertyName = "Start Event When Spawned (If false, the event starts when landed)", Order = 81)]
    public bool StartWhenSpawned { get; set; } = true;

    [JsonProperty(PropertyName = "Stop Event When Killed", Order = 82)]
    public bool StopWhenKilled { get; set; }

    [JsonProperty(PropertyName = "Event Timer Starts When Looted", Order = 83)]
    public bool TimerStartWhenLooted { get; set; }

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;

    public bool IsTimedDisabled()
    {
      return StopWhenKilled || TimerStartWhenLooted;
    }
  }

  public class SupplySignalEvent : SupplyDropEvent, IGeneralEvent
  {
    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.SupplySignal;

    public override string GetName() => nameof(GeneralEventType.SupplySignal);
  }

  public class TimedSupplyEvent : SupplyDropEvent, IGeneralEvent
  {
    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.TimedSupply;

    public override string GetName() => nameof(GeneralEventType.TimedSupply);
  }

  // Cargo Ship general event
  // NOTE: reserve order 90-99
  public class CargoShipEvent : DomeEvent, IGeneralEvent
  {
    [JsonProperty(PropertyName = "Event State On Spawn (true=enabled, false=disabled)", Order = 90)]
    public bool SpawnState { get; set; } = true;

    [JsonProperty(PropertyName = "Event State On Harbor Approach", Order = 91)]
    public bool ApproachState { get; set; } = true;

    [JsonProperty(PropertyName = "Event State On Harbor Docking", Order = 92)]
    public bool DockState { get; set; } = true;

    [JsonProperty(PropertyName = "Event State On Harbor Departure", Order = 93)]
    public bool DepartState { get; set; } = true;

    [JsonProperty(PropertyName = "Event State On Map Egress", Order = 94)]
    public bool EgressState { get; set; } = true;

    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 95)]
    public SphereCubeParentDynamicZone DynamicZone { get; set; } = new()
    {
      Size = new Vector3(25.9f, 43.3f, 152.8f),
      Center = new Vector3(0f, 21.6f, 6.6f)
    };

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;

    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.CargoShip;

    public override string GetName() => nameof(GeneralEventType.CargoShip);
  }

  // Deep Sea ghost ship general event
  // NOTE: reserve order 100-104
  public class GhostShipEvent : DomeEvent, IGeneralEvent
  {
    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 100)]
    public SphereCubeDynamicZone DynamicZone { get; set; } = new()
    {
      Radius = 200.0f
    };

    [JsonProperty(PropertyName = "Transform Position", Order = 101)]
    public Vector3 TransformPosition { get; set; }

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;

    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.GhostShip;

    public override string GetName() => nameof(GeneralEventType.GhostShip);
  }

  // Deep Sea Island general event
  // NOTE: reserve order 105-109
  public class DeepSeaIslandEvent : DomeEvent, IGeneralEvent
  {
    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 105)]
    public SphereCubeAutoGeoDynamicZone DynamicZone { get; set; } = new();

    [JsonProperty(PropertyName = "Transform Position", Order = 106)]
    public Vector3 TransformPosition { get; set; }

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;

    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.DeepSeaIsland;

    public override string GetName() => nameof(GeneralEventType.DeepSeaIsland);
  }

  // Deep Sea island cannon general event
  // NOTE: reserve order 110-114
  public class IslandCannonEvent : DomeEvent, IGeneralEvent
  {
    [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 110)]
    public SphereCubeDynamicZone DynamicZone { get; set; } = new()
    {
      Radius = 10.0f
    };

    [JsonProperty(PropertyName = "Transform Position", Order = 111)]
    public Vector3 TransformPosition { get; set; }

    public override BaseDynamicZone GetDynamicZone() => DynamicZone;

    [JsonIgnore] public GeneralEventType GeneralEventType =>
      GeneralEventType.IslandCannon;

    public override string GetName() => nameof(GeneralEventType.IslandCannon);
  }

  #region Interface

  public interface IGeneralEvent
  {
    [JsonIgnore] public GeneralEventType GeneralEventType { get; }
  }

  public interface ITimedDisable
  {
    bool IsTimedDisabled();
  }

  #endregion Interface

  #endregion Event

  #region Zone

  // NOTE: reserve order 200-299
  public abstract class BaseDynamicZone
  {
    [JsonProperty(PropertyName = "Zone Comfort", Order = 200)]
    public float Comfort { get; set; }

    [JsonProperty(PropertyName = "Zone Radiation", Order = 201)]
    public float Radiation { get; set; }

    [JsonProperty(PropertyName = "Zone Temperature", Order = 202)]
    public float Temperature { get; set; }

    [JsonProperty(PropertyName = "Enable Safe Zone", Order = 203)]
    public bool SafeZone { get; set; }

    [JsonProperty(PropertyName = "Eject Spawns", Order = 204)]
    public string EjectSpawns { get; set; } = "";

    [JsonProperty(PropertyName = "Zone Parent ID", Order = 205)]
    public string ParentId { get; set; } = "";

    [JsonProperty(PropertyName = "Enter Message", Order = 206)]
    public string EnterMessage { get; set; } = "Entering a PVP area!";

    [JsonProperty(PropertyName = "Leave Message", Order = 207)]
    public string LeaveMessage { get; set; } = "Leaving a PVP area.";

    [JsonProperty(PropertyName = "Permission Required To Enter Zone", Order = 208)]
    public string Permission { get; set; } = "";

    [JsonProperty(PropertyName = "Extra Zone Flags", Order = 209)]
    public List<string> ExtraZoneFlags { get; set; } = new();

    private string[] _zoneSettings;

    public virtual string[] ZoneSettings(Transform transform = null) =>
      _zoneSettings ??= GetZoneSettings();

    protected void GetBaseZoneSettings(List<string> zoneSettings)
    {
      zoneSettings.Add("name");
      zoneSettings.Add(ZoneName);
      if (Comfort > 0f)
      {
        zoneSettings.Add("comfort");
        zoneSettings.Add(Comfort.ToString(CultureInfo.InvariantCulture));
      }
      if (Radiation > 0f)
      {
        zoneSettings.Add("radiation");
        zoneSettings.Add(Radiation.ToString(CultureInfo.InvariantCulture));
      }
      if (Mathf.Abs(Temperature) < 1e-8f)
      {
        zoneSettings.Add("temperature");
        zoneSettings.Add(Temperature.ToString(CultureInfo.InvariantCulture));
      }
      if (SafeZone)
      {
        zoneSettings.Add("safezone");
        zoneSettings.Add(SafeZone.ToString());
      }
      if (!string.IsNullOrEmpty(EnterMessage))
      {
        zoneSettings.Add("enter_message");
        zoneSettings.Add(EnterMessage);
      }
      if (!string.IsNullOrEmpty(LeaveMessage))
      {
        zoneSettings.Add("leave_message");
        zoneSettings.Add(LeaveMessage);
      }
      if (!string.IsNullOrEmpty(EjectSpawns))
      {
        zoneSettings.Add("ejectspawns");
        zoneSettings.Add(EjectSpawns);
      }
      if (!string.IsNullOrEmpty(Permission))
      {
        zoneSettings.Add("permission");
        zoneSettings.Add(Permission);
      }
      if (!string.IsNullOrEmpty(ParentId))
      {
        zoneSettings.Add("parentid");
        zoneSettings.Add(ParentId);
      }
      foreach (var flag in ExtraZoneFlags)
      {
        if (string.IsNullOrEmpty(flag)) continue;
        zoneSettings.Add(flag);
        zoneSettings.Add("true");
      }
    }

    protected abstract string[] GetZoneSettings(Transform transform = null);
  }

  // NOTE: reserve order 300-399
  public class SphereCubeDynamicZone : BaseDynamicZone, ISphereZone, ICubeZone, IRotateZone
  {
    [JsonProperty(PropertyName = "Zone Radius", Order = 300)]
    public float Radius { get; set; }

    [JsonProperty(PropertyName = "Zone Size", Order = 301)]
    public Vector3 Size { get; set; }

    [JsonProperty(PropertyName = "Zone Rotation", Order = 302)]
    public float Rotation { get; set; }

    [JsonProperty(PropertyName = "Fixed Rotation", Order = 303)]
    public bool FixedRotation { get; set; }

    public override string[] ZoneSettings(Transform transform = null) =>
      GetZoneSettings(transform);

    protected override string[] GetZoneSettings(Transform transform = null)
    {
      var zoneSettings = new List<string>();
      if (Radius > 0f)
      {
        zoneSettings.Add("radius");
        zoneSettings.Add(Radius.ToString(CultureInfo.InvariantCulture));
      }
      else
      {
        zoneSettings.Add("size");
        zoneSettings.Add($"{Size.x} {Size.y} {Size.z}");
      }
      zoneSettings.Add("rotation");
      var transformedRotation = Rotation;
      if (transform is not null && transform && !FixedRotation)
      {
        transformedRotation += transform.rotation.eulerAngles.y;
      }
      zoneSettings.Add(transformedRotation.ToString(CultureInfo.InvariantCulture));
      GetBaseZoneSettings(zoneSettings);
      return zoneSettings.ToArray();
    }
  }

  // NOTE: reserve order 400-499
  public class SphereCubeParentDynamicZone
    : BaseDynamicZone, ISphereZone, ICubeZone, IParentZone
  {
    [JsonProperty(PropertyName = "Zone Radius", Order = 400)]
    public float Radius { get; set; }

    [JsonProperty(PropertyName = "Zone Size", Order = 401)]
    public Vector3 Size { get; set; }

    [JsonProperty(PropertyName = "Transform Position", Order = 402)]
    public Vector3 Center { get; set; }

    public override string[] ZoneSettings(Transform transform = null) =>
      GetZoneSettings(transform);

    protected override string[] GetZoneSettings(Transform transform = null)
    {
      var zoneSettings = new List<string>();
      if (Radius > 0f)
      {
        zoneSettings.Add("radius");
        zoneSettings.Add(Radius.ToString(CultureInfo.InvariantCulture));
      }
      else
      {
        zoneSettings.Add("size");
        zoneSettings.Add($"{Size.x} {Size.y} {Size.z}");
      }
      GetBaseZoneSettings(zoneSettings);
      return zoneSettings.ToArray();
    }
  }

  // NOTE: reserve order 500-599
  public class SphereCubeAutoGeoDynamicZone
    : SphereCubeDynamicZone
  {
    [JsonProperty(PropertyName = "Auto-calculate zone geometry (overwrites existing values)", Order = 500)]
    public bool DoAutoGeo { get; set; }
  }

  #region Interface

  public interface ISphereZone
  {
    float Radius { get; set; }
  }

  public interface ICubeZone
  {
    Vector3 Size { get; set; }
  }

  public interface IParentZone
  {
    Vector3 Center { get; set; }
  }

  public interface IRotateZone
  {
    float Rotation { get; set; }

    bool FixedRotation { get; set; }
  }

  #endregion Interface

  #endregion Zone

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
        UpdateConfigValues();
      }
    }
    catch (Exception ex)
    {
      PrintError($"The configuration file is corrupted. \n{ex}");
      LoadDefaultConfig();
    }
    SaveConfig();
    _configData?.PopulateEventNames();
  }

  protected override void LoadDefaultConfig()
  {
    PrintWarning("Creating a new configuration file");
    _configData = new ConfigData
    {
      Version = Version
    };
  }

  protected override void SaveConfig()
  {
    Config.WriteObject(_configData);
  }

  private void UpdateConfigValues()
  {
    // handle plugin version updates
    // ...unless config file indicates no version change
    if (_configData.Version >= Version) return;

    if (_configData.Version <= new VersionNumber(4, 2, 0))
    {
      _configData.Global.CompareRadius = 2f;
    }

    if (_configData.Version <= new VersionNumber(4, 2, 4))
    {
      LoadData();
      SaveData();
    }

    if (_configData.Version <= new VersionNumber(4, 2, 6))
    {
      if (GetConfigValue(out bool value, "General Event Settings", "Supply Signal Event", "Supply Drop Event Start When Spawned (If false, the event starts when landed)"))
      {
        _configData.GeneralEvents.SupplySignal.StartWhenSpawned = value;
      }
      if (GetConfigValue(out value, "General Event Settings", "Timed Supply Event", "Supply Drop Event Start When Spawned (If false, the event starts when landed)"))
      {
        _configData.GeneralEvents.TimedSupply.StartWhenSpawned = value;
      }
      if (GetConfigValue(out value, "General Event Settings", "Hackable Crate Event", "Hackable Crate Event Start When Spawned (If false, the event starts when unlocking)"))
      {
        _configData.GeneralEvents.HackableCrate.StartWhenSpawned = value;
      }
    }

    // 4.9.0 dome settings migration check
    DomeEvent.Migrate();

    _configData.Version = Version;
  }

  private bool GetConfigValue<T>(out T value, params string[] path)
  {
    var configValue = Config.Get(path);
    if (configValue != null)
    {
      if (configValue is T t)
      {
        value = t;
        return true;
      }
      try
      {
        value = Config.ConvertValue<T>(configValue);
        return true;
      }
      catch (Exception ex)
      {
        PrintError($"GetConfigValue ERROR: path: {string.Join("\\", path)}\n{ex}");
      }
    }

    value = default;
    return false;
  }

  #endregion ConfigurationFile

  #region DataFile

  private StoredData _storedData;

  private sealed class StoredData
  {
    // user-defined timed events by event name
    public readonly Dictionary<string, CustomTimedEvent> timedEvents = new();
    // user-defined auto events by event name
    public readonly Dictionary<string, AutoEvent> autoEvents = new();

    [JsonIgnore]
    public int CustomEventsCount => timedEvents.Count + autoEvents.Count;

    public bool EventDataExists(string eventName) =>
      timedEvents.ContainsKey(eventName) || autoEvents.ContainsKey(eventName);

    public BaseEvent GetEvent(string eventName) =>
      timedEvents.TryGetValue(eventName, out var timedEvent) ?
        timedEvent : autoEvents.GetValueOrDefault(eventName);

    public bool RemoveEventData(string eventName)
    {
      var removed = false;
      removed |= timedEvents.Remove(eventName);
      removed |= autoEvents.Remove(eventName);
      return removed;
    }

    // copy each dictionary event's key into its name field to support
    //  BaseEvent.GetName() API, which is needed for hook calls and logging
    public void PopulateEventNames()
    {
      foreach (var (autoEventName, autoEvent) in autoEvents)
      {
        autoEvent.EventName = autoEventName;
      }
      foreach (var (timedEventName, timedEvent) in timedEvents)
      {
        timedEvent.EventName = timedEventName;
      }
    }
  }

  private void LoadData()
  {
    try
    {
      _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
      // 4.9.0 dome settings migration check
      DomeEvent.Migrate();
    }
    catch
    {
      _storedData = null;
    }
    if (null == _storedData) ClearData();
    _storedData?.PopulateEventNames();
  }

  private void ClearData()
  {
    _storedData = new StoredData();
    SaveData();
  }

  private void SaveData() =>
    Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);

  #endregion DataFile

  #region LanguageFile

  private void Print(IPlayer iPlayer, string message)
  {
    if (iPlayer == null)
    {
      return;
    }
    if (iPlayer.Id == "server_console")
    {
      iPlayer.Reply(message, _configData.Chat.Prefix);
      return;
    }
    var player = iPlayer.Object as BasePlayer;
    if (player != null)
    {
      Player.Message(player, message, $"<color={_configData.Chat.PrefixColor}>{_configData.Chat.Prefix}</color>", _configData.Chat.SteamIdIcon);
      return;
    }
    iPlayer.Reply(message, $"<color={_configData.Chat.PrefixColor}>{_configData.Chat.Prefix}</color>");
  }

  private void Print(BasePlayer player, string message)
  {
    if (string.IsNullOrEmpty(message))
    {
      return;
    }
    Player.Message(player, message, string.IsNullOrEmpty(_configData.Chat.Prefix) ?
      null : $"<color={_configData.Chat.PrefixColor}>{_configData.Chat.Prefix}</color>", _configData.Chat.SteamIdIcon);
  }

  private string Lang(string key, string id = null, params object[] args)
  {
    try
    {
      return string.Format(lang.GetMessage(key, this, id), args);
    }
    catch (Exception)
    {
      PrintError($"Error in the language formatting of '{key}'. (userid: {id}. lang: {lang.GetLanguage(id)}. args: {string.Join(" ,", args)})");
      throw;
    }
  }

  protected override void LoadDefaultMessages()
  {
    lang.RegisterMessages(new Dictionary<string, string>
    {
      ["NotAllowed"] = "You do not have permission to use this command",
      ["NoCustomEvent"] = "There is no custom event data",
      ["CustomEvents"] = "There are {0} custom event data",
      ["AutoEvent"] = "{0}.[AutoEvent]: '{1}'. AutoStart: {2}. Position: {3}",
      ["TimedEvent"] = "{0}.[TimedEvent]: '{1}'. Duration: {2}",
      ["NoEventName"] = "Please type event name",
      ["EventNameExist"] = "The event name {0} already exists",
      ["EventNameNotExist"] = "The event name {0} does not exist",
      ["EventDataAdded"] = "'{0}' event data was added successfully",
      ["EventDataRemoved"] = "'{0}' event data was removed successfully",
      ["EventStarted"] = "'{0}' event started successfully",
      ["EventStopped"] = "'{0}' event stopped successfully",
      ["Holster"] = "Ready your weapons!",
      ["ShowingZones"] = "Showing active zones within range {0} for {1} second(s)",

      ["AutoEventAutoStart"] = "'{0}' event auto start is {1}",
      ["AutoEventMove"] = "'{0}' event moves to your current location",
      ["TimedEventDuration"] = "'{0}' event duration is changed to {1} seconds",

      ["SyntaxError"] = "Syntax error, please type '<color=#ce422b>/{0} <help | h></color>' to view help",
      ["Syntax"] = "<color=#ce422b>/{0} add <eventName> [timed]</color> - Add event data. If added 'timed', it will be a timed event",
      ["Syntax1"] = "<color=#ce422b>/{0} remove <eventName></color> - Remove event data",
      ["Syntax2"] = "<color=#ce422b>/{0} start <eventName></color> - Start event",
      ["Syntax3"] = "<color=#ce422b>/{0} stop <eventName></color> - Stop event",
      ["Syntax4"] = "<color=#ce422b>/{0} edit <eventName> <true/false></color> - Changes auto start state of auto event",
      ["Syntax5"] = "<color=#ce422b>/{0} edit <eventName> <move></color> - Move auto event to your current location",
      ["Syntax6"] = "<color=#ce422b>/{0} edit <eventName> <time(seconds)></color> - Changes the duration of a timed event",
      ["Syntax7"] = "<color=#ce422b>/{0} list</color> - Display all custom events",
      ["Syntax8"] = "<color=#ce422b>/{0} show</color> - Show geometries for all active zones"
    }, this);

    lang.RegisterMessages(new Dictionary<string, string>
    {
      ["NotAllowed"] = "您没有权限使用该命令",
      ["NoCustomEvent"] = "您没有创建任何自定义事件数据",
      ["CustomEvents"] = "当前自定义事件数有 {0}个",
      ["AutoEvent"] = "{0}.[自动事件]: '{1}'. 自动启用: {2}. 位置: {3}",
      ["TimedEvent"] = "{0}.[定时事件]: '{1}'. 持续时间: {2}",
      ["NoEventName"] = "请输入事件名字",
      ["EventNameExist"] = "'{0}' 事件名字已存在",
      ["EventNameNotExist"] = "'{0}' 事件名字不存在",
      ["EventDataAdded"] = "'{0}' 事件数据添加成功",
      ["EventDataRemoved"] = "'{0}' 事件数据删除成功",
      ["EventStarted"] = "'{0}' 事件成功开启",
      ["EventStopped"] = "'{0}' 事件成功停止",
      ["Holster"] = "准备好武器!",
      ["ShowingZones"] = "显示 {0} 范围内的活动区域，持续 {1} 秒",

      ["AutoEventAutoStart"] = "'{0}' 事件自动开启状态为 {1}",
      ["AutoEventMove"] = "'{0}' 事件移到了您的当前位置",
      ["TimedEventDuration"] = "'{0}' 事件的持续时间改为了 {1}秒",

      ["SyntaxError"] = "语法错误, 输入 '<color=#ce422b>/{0} <help | h></color>' 查看帮助",
      ["Syntax"] = "<color=#ce422b>/{0} add <eventName> [timed]</color> - 添加事件数据。如果后面加上'timed'，将添加定时事件数据",
      ["Syntax1"] = "<color=#ce422b>/{0} remove <eventName></color> - 删除事件数据",
      ["Syntax2"] = "<color=#ce422b>/{0} start <eventName></color> - 开启事件",
      ["Syntax3"] = "<color=#ce422b>/{0} stop <eventName></color> - 停止事件",
      ["Syntax4"] = "<color=#ce422b>/{0} edit <eventName> <true/false></color> - 改变自动事件的自动启动状态",
      ["Syntax5"] = "<color=#ce422b>/{0} edit <eventName> <move></color> - 移动自动事件的位置到您的当前位置",
      ["Syntax6"] = "<color=#ce422b>/{0} edit <eventName> <time(seconds)></color> - 修改定时事件的持续时间",
      ["Syntax7"] = "<color=#ce422b>/{0} list</color> - 显示所有自定义事件",
      ["Syntax8"] = "<color=#ce422b>/{0} show</color> - 显示所有活动区域的几何形"
    }, this, "zh-CN");
  }

  #endregion LanguageFile
}

//Requires: ZoneManager

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins
{
  [Info("Dynamic PVP", "HunterZ/CatMeat/Arainrr", "4.7.0", ResourceId = 2728)]
  [Description("Creates temporary PvP zones on certain actions/events")]
  public class DynamicPVP : RustPlugin
  {
    #region Fields

    [PluginReference] private readonly Plugin BotReSpawn, TruePVE, ZoneManager;

    private const string PermissionAdmin = "dynamicpvp.admin";
    private const string PrefabLargeOilRig =
      "assets/bundled/prefabs/autospawn/monument/offshore/oilrig_1.prefab";
    private const string PrefabOilRig =
      "assets/bundled/prefabs/autospawn/monument/offshore/oilrig_2.prefab";
    private const string PrefabSphere =
      "assets/prefabs/visualization/sphere.prefab";
    private const string ZoneName = "DynamicPVP";

    private readonly Dictionary<string, Timer> _eventTimers = new();
    private readonly Dictionary<ulong, LeftZone> _pvpDelays = new();
    //ID -> EventName
    private readonly Dictionary<string, string> _activeDynamicZones = new();
    // ZoneIDs of active DynamicPVP events with BypassLootDefenderLocks == true
    // used for managing hook subscriptions and for faster lookups
    private readonly HashSet<string> _activeLootDefenderBypassZones = new();

    private Vector3 _oilRigPosition = Vector3.zero;
    private Vector3 _largeOilRigPosition = Vector3.zero;
    private bool _useExcludePlayer;
    private bool _subscribedCommands;
    private bool _subscribedDamage;
    private bool _subscribedZones;
    private bool _brokenLabs;
    private bool _brokenTunnels;
    private bool _dataChanged;
    private Coroutine _createEventsCoroutine;
    private readonly YieldInstruction _fastYield = null;
    private readonly YieldInstruction _throttleYield =
      CoroutineEx.waitForSeconds(0.1f);
    private readonly YieldInstruction _pauseYield =
      CoroutineEx.waitForSeconds(0.5f);
    private float _targetFps = -1.0f;

    public static DynamicPVP Instance { get; private set; }

    private sealed class LeftZone : Pool.IPooled
    {
      public string zoneId;
      public string eventName;
      public Timer zoneTimer;

      public void EnterPool()
      {
        zoneId = null;
        eventName = null;
        zoneTimer?.Destroy();
        zoneTimer = null;
      }

      public void LeavePool()
      {
      }
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

    private enum GeneralEventType
    {
      Bradley,
      Helicopter,
      SupplyDrop,
      SupplySignal,
      CargoShip,
      HackableCrate,
      ExcavatorIgnition
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
      _brokenLabs = _brokenTunnels = false;

      Instance = this;
      LoadData();
      permission.RegisterPermission(PermissionAdmin, this);
      AddCovalenceCommand(configData.Chat.Command, nameof(CmdDynamicPVP));
      Unsubscribe(nameof(CanEntityTakeDamage));
      Unsubscribe(nameof(OnCargoPlaneSignaled));
      Unsubscribe(nameof(OnCargoShipEgress));
      Unsubscribe(nameof(OnCargoShipHarborApproach));
      Unsubscribe(nameof(OnCargoShipHarborArrived));
      Unsubscribe(nameof(OnCargoShipHarborLeave));
      Unsubscribe(nameof(OnCrateHack));
      Unsubscribe(nameof(OnCrateHackEnd));
      Unsubscribe(nameof(OnDieselEngineToggled));
      Unsubscribe(nameof(OnEnterZone));
      Unsubscribe(nameof(OnEntityDeath));
      Unsubscribe(nameof(OnEntityKill));
      Unsubscribe(nameof(OnEntitySpawned));
      Unsubscribe(nameof(OnExitZone));
      Unsubscribe(nameof(OnLootEntity));
      Unsubscribe(nameof(OnLootLockedEntity));
      Unsubscribe(nameof(OnPlayerCommand));
      Unsubscribe(nameof(OnServerCommand));
      Unsubscribe(nameof(OnSupplyDropLanded));
      if (configData.Global.LogToFile)
      {
        _debugStringBuilder = Pool.Get<StringBuilder>();
      }

      // setup new TruePVE "ExcludePlayer" support
      _useExcludePlayer = configData.Global.UseExcludePlayer;
      // if ExcludePlayer is disabled in config but is supported...
      if (!_useExcludePlayer &&
          null != TruePVE &&
          TruePVE.Version >= new VersionNumber(2, 2, 3))
      {
        // ...and all PVP delays are enabled, auto-enable internally and warn
        if ((PvpDelayTypes.ZonePlayersCanDamageDelayedPlayers |
             PvpDelayTypes.DelayedPlayersCanDamageZonePlayers |
             PvpDelayTypes.DelayedPlayersCanDamageDelayedPlayers) ==
            configData.Global.PvpDelayFlags)
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

      _subscribedCommands = _subscribedDamage = _subscribedZones = false;
    }

    private void OnServerInitialized()
    {
      DeleteOldDynamicZones();
      if (configData.GeneralEvents.ExcavatorIgnition.Enabled)
      {
        Subscribe(nameof(OnDieselEngineToggled));
      }
      if (configData.GeneralEvents.PatrolHelicopter.Enabled ||
          configData.GeneralEvents.BradleyApc.Enabled)
      {
        Subscribe(nameof(OnEntityDeath));
      }
      if (configData.GeneralEvents.SupplySignal.Enabled ||
          configData.GeneralEvents.TimedSupply.Enabled)
      {
        Subscribe(nameof(OnCargoPlaneSignaled));
        // this is now subscribed regardless of start on spawn-vs-landing, as we
        //  need to tether the zone to the drop on landing in both cases
        Subscribe(nameof(OnSupplyDropLanded));
      }
      if (configData.GeneralEvents.HackableCrate.Enabled &&
          configData.GeneralEvents.HackableCrate.TimerStartWhenUnlocked)
      {
        Subscribe(nameof(OnCrateHackEnd));
      }
      if ((configData.GeneralEvents.TimedSupply.Enabled &&
           configData.GeneralEvents.TimedSupply.TimerStartWhenLooted) ||
          (configData.GeneralEvents.SupplySignal.Enabled &&
           configData.GeneralEvents.SupplySignal.TimerStartWhenLooted) ||
          (configData.GeneralEvents.HackableCrate.Enabled &&
           configData.GeneralEvents.HackableCrate.TimerStartWhenLooted))
      {
        Subscribe(nameof(OnLootEntity));
      }
      if (configData.GeneralEvents.HackableCrate.Enabled &&
          !configData.GeneralEvents.HackableCrate.StartWhenSpawned)
      {
        Subscribe(nameof(OnCrateHack));
      }
      if ((configData.GeneralEvents.TimedSupply.Enabled &&
           configData.GeneralEvents.TimedSupply.StartWhenSpawned) ||
          (configData.GeneralEvents.SupplySignal.Enabled &&
           configData.GeneralEvents.SupplySignal.StartWhenSpawned) ||
          (configData.GeneralEvents.HackableCrate.Enabled &&
           configData.GeneralEvents.HackableCrate.StartWhenSpawned))
      {
        Subscribe(nameof(OnEntitySpawned));
      }
      if ((configData.GeneralEvents.TimedSupply.Enabled &&
           configData.GeneralEvents.TimedSupply.StopWhenKilled) ||
          (configData.GeneralEvents.SupplySignal.Enabled &&
           configData.GeneralEvents.SupplySignal.StopWhenKilled) ||
          (configData.GeneralEvents.HackableCrate.Enabled &&
           configData.GeneralEvents.HackableCrate.StopWhenKilled))
      {
        Subscribe(nameof(OnEntityKill));
      }
      if (configData.GeneralEvents.CargoShip.Enabled)
      {
        Subscribe(nameof(OnCargoShipEgress));
        Subscribe(nameof(OnCargoShipHarborApproach));
        Subscribe(nameof(OnCargoShipHarborArrived));
        Subscribe(nameof(OnCargoShipHarborLeave));
        Subscribe(nameof(OnEntityKill));
        Subscribe(nameof(OnEntitySpawned));
      }

      NextTick(() =>
      {
        _createEventsCoroutine =
          ServerMgr.Instance.StartCoroutine(CreateEvents());
      });
    }

    private void Unload()
    {
      if (_createEventsCoroutine != null)
      {
        ServerMgr.Instance.StopCoroutine(_createEventsCoroutine);
      }

      if (_activeDynamicZones.Count > 0)
      {
        PrintDebug($"Deleting {_activeDynamicZones.Count} active zones");
        foreach (var entry in _activeDynamicZones.ToArray())
        {
          DeleteDynamicZone(entry.Key);
        }
      }

      var leftZones = _pvpDelays.Values.ToArray();
      for (var i = leftZones.Length - 1; i >= 0; i--)
      {
        var value = leftZones[i];
        Pool.Free(ref value);
      }

      var spheres = _zoneSpheres.Values.ToArray();
      for (var i = spheres.Length - 1; i >= 0; i--)
      {
        var sphereEntities = spheres[i];
        foreach (var sphereEntity in sphereEntities)
        {
          if (sphereEntity && !sphereEntity.IsDestroyed)
          {
            sphereEntity.KillMessage();
          }
        }

        Pool.FreeUnmanaged(ref sphereEntities);
      }

      SaveData();
      SaveDebug();
      Pool.Directory.TryRemove(typeof(LeftZone), out _);
      Instance = null;

      if (null == _debugStringBuilder) return;
      Pool.FreeUnmanaged(ref _debugStringBuilder);
      _debugStringBuilder = null;

      _originalMonumentGeometries.Clear();

      _activeLootDefenderBypassZones.Clear();
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

    private void TryRemoveEventTimer(string zoneId)
    {
      if (_eventTimers.Remove(zoneId, out var value))
      {
        value?.Destroy();
      }
    }

    private LeftZone GetOrAddPVPDelay(
      BasePlayer player, string zoneId, string eventName)
    {
      PrintDebug($"Adding {player.displayName} to pvp delay");
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
      leftZone.eventName = eventName;
      if (added)
      {
        CheckHooks(HookCheckReasons.DelayAdded);
      }

      return leftZone;
    }

    private bool TryRemovePVPDelay(BasePlayer player)
    {
      PrintDebug($"Removing {player.displayName} from pvp delay");
      var playerId = player.userID.Get();
      if (!_pvpDelays.Remove(playerId, out var leftZone)) return false;
      Interface.CallHook("OnPlayerRemovedFromPVPDelay",
        playerId, leftZone.zoneId, player);
      Pool.Free(ref leftZone);
      CheckHooks(HookCheckReasons.DelayRemoved);
      return true;
    }

    private bool CheckEntityOwner(BaseEntity baseEntity)
    {
      if (!configData.Global.CheckEntityOwner ||
          !baseEntity.OwnerID.IsSteamId() ||
          // HeliSignals and BradleyDrops exception
          baseEntity.skinID != 0)
      {
        return true;
      }
      PrintDebug($"Skipping event creation because baseEntity={baseEntity} is owned by player={baseEntity.OwnerID}");
      return false;
    }

    private bool CanCreateDynamicPVP(string eventName, BaseEntity entity)
    {
      if (Interface.CallHook("OnCreateDynamicPVP", eventName, entity) == null)
      {
        return true;
      }
      PrintDebug($"Skipping event creation for eventName={eventName} due to OnCreateDynamicPVP hook result");
      return false;
    }

    private bool HasCommands()
    {
      // track which events we've checked, to avoid redundant calls to
      //  GetBaseEvent(); note that use of pool API means we need to free this
      //  on every return
      var checkedEvents = Pool.Get<HashSet<string>>();
      // check for command-containing zones referenced by PVP delays, which
      //  either work when PVP delayed, or are an active zone
      // HZ: I guess this is really trying to catch the corner case of players
      //  in PVP delay because a zone expired?
      foreach (var leftZone in _pvpDelays.Values)
      {
        var baseEvent = GetBaseEvent(leftZone.eventName);
        if (baseEvent == null || baseEvent.CommandList.Count <= 0)
        {
          continue;
        }

        if (baseEvent.CommandWorksForPVPDelay ||
            _activeDynamicZones.ContainsValue(leftZone.eventName))
        {
          Pool.FreeUnmanaged(ref checkedEvents);
          return true;
        }

        checkedEvents.Add(leftZone.eventName);
      }

      foreach (var eventName in _activeDynamicZones.Values)
      {
        // optimization: skip if we've already checked this in the other loop
        if (checkedEvents.Contains(eventName))
        {
          continue;
        }

        var baseEvent = GetBaseEvent(eventName);
        if (null == baseEvent || baseEvent.CommandList.Count <= 0) continue;
        Pool.FreeUnmanaged(ref checkedEvents);
        return true;
      }

      Pool.FreeUnmanaged(ref checkedEvents);
      return false;
    }

    private void CheckCommandHooks(bool added)
    {
      // optimization: abort if adding a delay/zone and already subscribed, or
      //  if removing a delay/zone and already unsubscribed
      if (added == _subscribedCommands)
      {
        return;
      }

      // also return if subscription status already matches command status
      if (HasCommands() == _subscribedCommands)
      {
        return;
      }

      // subscription status needs to change, so toggle things
      if (_subscribedCommands)
      {
        Unsubscribe(nameof(OnPlayerCommand));
        Unsubscribe(nameof(OnServerCommand));
      }
      else
      {
        Subscribe(nameof(OnPlayerCommand));
        Subscribe(nameof(OnServerCommand));
      }

      _subscribedCommands = !_subscribedCommands;
    }

    private void CheckPvpDelayHooks(bool added)
    {
      // optimization: abort if using TruePVE's ExcludePlayer API
      if (_useExcludePlayer)
      {
        // ...ensure we're unsubscribed from damage hook, just in case
        if (!_subscribedDamage) return;
        Unsubscribe(nameof(CanEntityTakeDamage));
        _subscribedDamage = false;
        return;
      }

      // optimization: abort if adding a delay and already subscribed, or if
      //  removing a delay and already unsubscribed
      if (added == _subscribedDamage) return;

      // also return if subscription status already matches delay status
      if (_pvpDelays.Count > 0 == _subscribedDamage) return;

      // subscription status needs to change, so toggle things
      if (_subscribedDamage)
      {
        Unsubscribe(nameof(CanEntityTakeDamage));
      }
      else
      {
        Subscribe(nameof(CanEntityTakeDamage));
      }

      _subscribedDamage = !_subscribedDamage;
    }

    private void CheckZoneHooks(bool added)
    {
      // optimization: abort if adding a zone and already subscribed, or if
      //  removing a zone and already unsubscribed
      if (added == _subscribedZones)
      {
        return;
      }

      // also return if subscription status already matches zone status
      if (_activeDynamicZones.Count > 0 == _subscribedZones)
      {
        return;
      }

      // subscription status needs to change, so toggle things
      if (_subscribedZones)
      {
        Unsubscribe(nameof(OnEnterZone));
        Unsubscribe(nameof(OnExitZone));
      }
      else
      {
        Subscribe(nameof(OnEnterZone));
        Subscribe(nameof(OnExitZone));
      }

      _subscribedZones = !_subscribedZones;
    }

    private void CheckHooks(HookCheckReasons reasons)
    {
      if (reasons.HasFlag(HookCheckReasons.DelayAdded))
      {
        CheckPvpDelayHooks(true);
      }
      else if (reasons.HasFlag(HookCheckReasons.DelayRemoved))
      {
        CheckPvpDelayHooks(false);
      }

      if (reasons.HasFlag(HookCheckReasons.ZoneAdded))
      {
        CheckZoneHooks(true);
      }
      else if (reasons.HasFlag(HookCheckReasons.ZoneRemoved))
      {
        CheckZoneHooks(false);
      }

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
    }

    private BaseEvent GetBaseEvent(string eventName)
    {
      if (string.IsNullOrEmpty(eventName))
      {
        throw new ArgumentNullException(nameof(eventName));
      }

      if (Interface.CallHook("OnGetBaseEvent", eventName)
          is BaseEvent externalEvent)
      {
        return externalEvent;
      }

      if (Enum.IsDefined(typeof(GeneralEventType), eventName) &&
          Enum.TryParse(eventName, true, out GeneralEventType generalEventType))
      {
        switch (generalEventType)
        {
          case GeneralEventType.Bradley:
            return configData.GeneralEvents.BradleyApc;
          case GeneralEventType.HackableCrate:
            return configData.GeneralEvents.HackableCrate;
          case GeneralEventType.Helicopter:
            return configData.GeneralEvents.PatrolHelicopter;
          case GeneralEventType.SupplyDrop:
            return configData.GeneralEvents.TimedSupply;
          case GeneralEventType.SupplySignal:
            return configData.GeneralEvents.SupplySignal;
          case GeneralEventType.ExcavatorIgnition:
            return configData.GeneralEvents.ExcavatorIgnition;
          case GeneralEventType.CargoShip:
            return configData.GeneralEvents.CargoShip;
          default:
            PrintDebug(
              $"ERROR: Missing BaseEvent lookup for generalEventType={generalEventType} for eventName={eventName}.",
              DebugLevel.Error);
            return null;
        }
      }

      if (storedData.autoEvents.TryGetValue(eventName, out var autoEvent))
      {
        return autoEvent;
      }

      if (storedData.timedEvents.TryGetValue(eventName, out var timedEvent))
      {
        return timedEvent;
      }

      if (configData.MonumentEvents.TryGetValue(
            eventName, out var monumentEvent))
      {
        return monumentEvent;
      }

      PrintDebug($"ERROR: Failed to get base event settings for {eventName}", DebugLevel.Error);
      return null;
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
        pause && configData.Global.DebugEnabled ? _pauseYield :
        Performance.report.frameRate >= _targetFps ? _fastYield :
        _throttleYield;
    }

    // coroutine to orchestrate creation of all relevant events on startup
    private IEnumerator CreateEvents()
    {
      var startTime = DateTime.UtcNow;
      Puts("Creating General Events");
      yield return CreateGeneralEvents();
      // this will get logged at a lower level
      yield return CreateMonumentEvents();
      Puts("Creating Auto Events");
      yield return CreateAutoEvents();
      _createEventsCoroutine = null;
      Puts($"Startup event creation completed in {(DateTime.UtcNow - startTime).TotalSeconds} seconds");
    }

    #endregion Startup

    #region General Event

    // coroutine to determine whether any General Events should be created based
    //  on currently existing entities of interest
    // this is expected to only be called on startup
    private IEnumerator CreateGeneralEvents()
    {
      // determine up-front whether there are any general events to create,
      //  because iterating over all net entities is not cheap
      var checkGeneralEvents = false;
      // TODO: Bradley, Patrol Helicopter, Supply Drop, Timed Supply
      checkGeneralEvents |= configData.GeneralEvents.CargoShip.Enabled;
      // NOTE: StopWhenKilled is checked because we don't want to start events
      //  whose end is determined by a timer, as we don't know elapsed times
      checkGeneralEvents |=
        configData.GeneralEvents.HackableCrate.Enabled &&
        configData.GeneralEvents.HackableCrate.StopWhenKilled;
      checkGeneralEvents |= configData.GeneralEvents.ExcavatorIgnition.Enabled;
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
        PrintDebug("DieselEngine is null");
        return;
      }

      if (!configData.GeneralEvents.ExcavatorIgnition.Enabled)
      {
        PrintDebug("Excavator Ignition Event is disabled");
        return;
      }

      if (!dieselEngine.IsOn())
      {
        PrintDebug("DieselEngine is off");
        return;
      }

      PrintDebug("Found activated Giant Excavator");
      OnDieselEngineToggled(dieselEngine);
    }

    private void OnDieselEngineToggled(DieselEngine dieselEngine)
    {
      if (!dieselEngine || null == dieselEngine.net)
      {
        PrintDebug("ERROR: OnDieselEngineToggled(): Engine or Net is null", DebugLevel.Error);
        return;
      }

      var zoneId = dieselEngine.net.ID.ToString();
      if (dieselEngine.IsOn())
      {
        PrintDebug(
          $"OnDieselEngineToggled(): Requesting 'just-in-case' delete of zoneId={zoneId} due to excavator enable");
        DeleteDynamicZone(zoneId);
        HandleGeneralEvent(
          GeneralEventType.ExcavatorIgnition, dieselEngine, true);
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
        PrintDebug("HackableLockedCrate is null");
        return;
      }

      if (!configData.GeneralEvents.HackableCrate.Enabled)
      {
        PrintDebug("Hackable Crate Event is disabled");
        return;
      }

      if (!configData.GeneralEvents.HackableCrate.StopWhenKilled)
      {
        PrintDebug("Hackable Crate Event doesn't stop when killed");
        return;
      }

      if (0 != hackableLockedCrate.FirstLooterId &&
          configData.GeneralEvents.HackableCrate.TimerStartWhenLooted)
      {
        // looted and stop after time since loot enabled
        // we don't know elapsed time, so err on the side of assuming the event
        //  has already ended
        PrintDebug(
          "Found looted hackable locked crate, and TimerStartWhenLooted set; ignoring because elapsed time unknown");
      }
      else if (
        hackableLockedCrate.HasFlag(HackableLockedCrate.Flag_FullyHacked) &&
        configData.GeneralEvents.HackableCrate.TimerStartWhenUnlocked)
      {
        // unlocked and stop after time since unlock enabled
        // we don't know elapsed time, so err on the side of assuming the event
        //  has already ended
        PrintDebug(
          "Found unlocked hackable locked crate and TimerStartWhenUnlocked set; ignoring because elapsed time unknown");
      }
      else if (hackableLockedCrate.HasFlag(HackableLockedCrate.Flag_Hacking) &&
               !configData.GeneralEvents.HackableCrate.StartWhenSpawned)
      {
        // hacking and start on hacking enabled
        PrintDebug("Found hacking hackable locked crate and StartWhenSpawned NOT set; triggering OnCrateHack()");
        OnCrateHack(hackableLockedCrate);
      }
      else if (configData.GeneralEvents.HackableCrate.StartWhenSpawned)
      {
        // any other state and start on spawn + stop when killed enabled
        PrintDebug("Found hackable locked crate, and StartWhenSpawned set; triggering OnEntitySpawned()");
        OnEntitySpawned(hackableLockedCrate);
      }
      else
      {
        PrintDebug(
          "Found hackable locked crate, but ignoring because of either start on hack, or stop on timer with elapsed time unknown");
      }
    }

    private void OnEntitySpawned(HackableLockedCrate hackableLockedCrate)
    {
      if (!hackableLockedCrate || null == hackableLockedCrate.net)
      {
        PrintDebug("ERROR: OnEntitySpawned(HackableLockedCrate): Crate or Net is null", DebugLevel.Error);
        return;
      }

      if (!configData.GeneralEvents.HackableCrate.Enabled ||
          !configData.GeneralEvents.HackableCrate.StartWhenSpawned)
      {
        PrintDebug("OnEntitySpawned(HackableLockedCrate): Ignoring due to event or spawn start disabled");
        return;
      }

      PrintDebug("Trying to create hackable crate spawn event");
      NextTick(() => LockedCrateEvent(hackableLockedCrate));
    }

    private void OnCrateHack(HackableLockedCrate hackableLockedCrate)
    {
      if (!hackableLockedCrate || null == hackableLockedCrate.net)
      {
        PrintDebug("ERROR: OnCrateHack(): Crate or Net is null", DebugLevel.Error);
        return;
      }

      PrintDebug("OnCrateHack(): Trying to create hackable crate hack event");
      NextTick(() => LockedCrateEvent(hackableLockedCrate));
    }

    private void OnCrateHackEnd(HackableLockedCrate hackableLockedCrate)
    {
      if (!hackableLockedCrate || null == hackableLockedCrate.net)
      {
        PrintDebug("ERROR: OnCrateHackEnd(): Crate or Net is null", DebugLevel.Error);
        return;
      }

      var zoneId = hackableLockedCrate.net.ID.ToString();
      PrintDebug(
        $"OnCrateHackEnd(): Scheduling delete of zoneId={zoneId} in {configData.GeneralEvents.HackableCrate.Duration}s");
      HandleDeleteDynamicZone(
        zoneId,
        configData.GeneralEvents.HackableCrate.Duration,
        nameof(GeneralEventType.HackableCrate));
    }

    private void OnLootEntity(
      BasePlayer player, HackableLockedCrate hackableLockedCrate)
    {
      if (!hackableLockedCrate || null == hackableLockedCrate.net)
      {
        PrintDebug("ERROR: OnLootEntity(HackableLockedCrate): Crate or Net is null", DebugLevel.Error);
        return;
      }

      if (!configData.GeneralEvents.HackableCrate.Enabled ||
          !configData.GeneralEvents.HackableCrate.TimerStartWhenLooted)
      {
        PrintDebug("OnLootEntity(HackableLockedCrate): Ignoring due to event or loot delay disabled");
        return;
      }

      var zoneId = hackableLockedCrate.net.ID.ToString();
      PrintDebug(
        $"OnLootEntity(HackableLockedCrate): Scheduling delete of zoneId={zoneId} in {configData.GeneralEvents.HackableCrate.Duration}s");
      HandleDeleteDynamicZone(
        zoneId,
        configData.GeneralEvents.HackableCrate.Duration,
        nameof(GeneralEventType.HackableCrate));
    }

    private void OnEntityKill(HackableLockedCrate hackableLockedCrate)
    {
      if (!hackableLockedCrate || null == hackableLockedCrate.net)
      {
        PrintDebug("ERROR: OnEntityKill(HackableLockedCrate): Crate or Net is null", DebugLevel.Error);
        return;
      }

      if (!configData.GeneralEvents.HackableCrate.Enabled ||
          !configData.GeneralEvents.HackableCrate.StopWhenKilled)
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

      if (configData.GeneralEvents.HackableCrate.ExcludeOilRig &&
          IsOnTheOilRig(hackableLockedCrate))
      {
        PrintDebug("The hackable locked crate is on an oil rig. Skipping event creation.");
        return;
      }

      if (configData.GeneralEvents.HackableCrate.ExcludeCargoShip &&
          IsOnTheCargoShip(hackableLockedCrate))
      {
        PrintDebug("The hackable locked crate is on a cargo ship. Skipping event creation.");
        return;
      }

      // call this here, because otherwise it's difficult to ensure that we call
      //  it exactly once
      const string eventName = nameof(GeneralEventType.HackableCrate);
      if (!CanCreateDynamicPVP(eventName, hackableLockedCrate))
      {
        return;
      }

      // NOTE: we are already NextTick() protected here
      HandleParentedEntityEvent(
        eventName, hackableLockedCrate, parentOnCreate: true);
    }

    private static bool IsOnTheCargoShip(
      HackableLockedCrate hackableLockedCrate)
    {
      var crateParent = hackableLockedCrate?.transform.parent;
      return crateParent && crateParent.HasComponent<CargoShip>();
    }

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
      if (!configData.GeneralEvents.PatrolHelicopter.Enabled)
      {
        return;
      }

      PrintDebug("Trying to create Patrol Helicopter killed event.");
      if (!CheckEntityOwner(patrolHelicopter))
      {
        return;
      }

      HandleGeneralEvent(GeneralEventType.Helicopter, patrolHelicopter, false);
    }

    private void BradleyApcEvent(BradleyAPC bradleyAPC)
    {
      if (!configData.GeneralEvents.BradleyApc.Enabled)
      {
        return;
      }

      PrintDebug("Trying to create Bradley APC killed event.");
      if (!CheckEntityOwner(bradleyAPC))
      {
        return;
      }

      HandleGeneralEvent(GeneralEventType.Bradley, bradleyAPC, false);
    }

    #endregion PatrolHelicopter And BradleyAPC Event

    #region SupplyDrop And SupplySignal Event

    // TODO: seems dodgy that Vector3 is being used as a key, because comparing
    //  floats is fraught; consider using network ID or something instead, and
    //  storing the location as data if needed
    private readonly Dictionary<Vector3, Timer> activeSupplySignals = new();

    private void OnCargoPlaneSignaled(
      CargoPlane cargoPlane, SupplySignal supplySignal) => NextTick(() =>
    {
      if (!supplySignal || !cargoPlane)
      {
        return;
      }

      Vector3 dropPosition = cargoPlane.dropPosition;
      if (activeSupplySignals.ContainsKey(dropPosition))
      {
        return;
      }

      // TODO: why is this a hard-coded 15-minute delay?
      activeSupplySignals.Add(dropPosition,
        timer.Once(900f, () => activeSupplySignals.Remove(dropPosition)));
      PrintDebug($"A supply signal is thrown at {dropPosition}");
    });

    private void OnEntitySpawned(SupplyDrop supplyDrop) => NextTick(() =>
      OnSupplyDropEvent(supplyDrop, false));

    private void OnSupplyDropLanded(SupplyDrop supplyDrop)
    {
      if (!supplyDrop || null == supplyDrop.net)
      {
        return;
      }

      var zoneId = supplyDrop.net.ID.ToString();
      if (_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        // event was already created on spawn; parent the event to the entity,
        //  so that they move together
        // NOTES:
        // - don't delete on failure, because leaving the existing zone on the
        //    ground is better than deleting it
        // - no need to delay parenting, as the zone presumably has already
        //    existed for a bit
        ParentEventToEntity(
          zoneId, GetBaseEvent(eventName), supplyDrop, deleteOnFailure: false,
          delay: false);
        return;
      }

      NextTick(() => OnSupplyDropEvent(supplyDrop, true));
    }

    private void OnLootEntity(BasePlayer _, SupplyDrop supplyDrop)
    {
      if (!supplyDrop || null == supplyDrop.net)
      {
        return;
      }

      var zoneId = supplyDrop.net.ID.ToString();
      if (!_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        // no active zone for this supply drop
        return;
      }

      var eventConfig = eventName switch
      {
        nameof(GeneralEventType.SupplySignal) =>
          configData.GeneralEvents.SupplySignal,
        nameof(GeneralEventType.SupplyDrop) =>
          configData.GeneralEvents.TimedSupply,
        _ => null
      };
      if (null == eventConfig)
      {
        // pathological
        PrintDebug($"Unknown SupplyDrop eventName={eventName} for zoneId={zoneId}", DebugLevel.Warning);
        return;
      }

      if (!eventConfig.Enabled || !eventConfig.TimerStartWhenLooted)
      {
        return;
      }

      HandleDeleteDynamicZone(zoneId, eventConfig.Duration, eventName);
    }

    private void OnEntityKill(SupplyDrop supplyDrop)
    {
      if (!supplyDrop || null == supplyDrop.net)
      {
        PrintDebug("ERROR: OnEntityKill(SupplyDrop): Drop or Net is null", DebugLevel.Error);
        return;
      }

      var zoneId = supplyDrop.net.ID.ToString();
      if (!_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        // no active zone for this supply drop
        return;
      }

      // untether everything that we may have parented to the SupplyDrop so that
      //  they don't get killed along with it
      ZM_GetZoneByID(zoneId)?.transform.SetParent(null, true);
      ParentDome(zoneId, Vector3.zero);

      var eventConfig = eventName switch
      {
        nameof(GeneralEventType.SupplySignal) =>
          configData.GeneralEvents.SupplySignal,
        nameof(GeneralEventType.SupplyDrop) =>
          configData.GeneralEvents.TimedSupply,
        _ => null
      };
      if (eventConfig is not { Enabled: true } || !eventConfig.StopWhenKilled)
      {
        return;
      }

      //When the timer starts, don't stop the event immediately
      if (_eventTimers.ContainsKey(zoneId))
      {
        PrintDebug(
          $"OnEntityKill(SupplyDrop): Ignoring due to event timer already active for zoneId={zoneId}");
        return;
      }

      PrintDebug($"OnEntityKill(SupplyDrop): Scheduling delete of zoneId={zoneId}");
      HandleDeleteDynamicZone(zoneId);
    }

    private static string GetSupplyDropStateName(bool isLanded) =>
      isLanded ? "Landed" : "Spawned";

    private void OnSupplyDropEvent(SupplyDrop supplyDrop, bool isLanded)
    {
      if (!supplyDrop || null == supplyDrop.net)
      {
        return;
      }

      PrintDebug(
        $"Trying to create supply drop {GetSupplyDropStateName(isLanded)} event at {supplyDrop.transform.position}.");
      if (!CheckEntityOwner(supplyDrop))
      {
        return;
      }

      var supplySignal = GetSupplySignalNear(supplyDrop.transform.position);
      if (null != supplySignal)
      {
        PrintDebug("Supply drop is probably from supply signal");
        if (!configData.GeneralEvents.SupplySignal.Enabled)
        {
          PrintDebug("Event for supply signals disabled. Skipping event creation.");
          return;
        }

        if (isLanded == configData.GeneralEvents.SupplySignal.StartWhenSpawned)
        {
          PrintDebug($"{GetSupplyDropStateName(isLanded)} for supply signals disabled.");
          return;
        }

        var entry = supplySignal.Value;
        entry.Value?.Destroy();
        activeSupplySignals.Remove(entry.Key);
        PrintDebug(
          $"Removing Supply signal from active list. Active supply signals remaining: {activeSupplySignals.Count}");
        const string eventNameSS = nameof(GeneralEventType.SupplySignal);
        if (!CanCreateDynamicPVP(eventNameSS, supplyDrop))
        {
          return;
        }

        HandleParentedEntityEvent(
          eventNameSS, supplyDrop, parentOnCreate: isLanded);
        return;
      }

      PrintDebug("Supply drop is probably NOT from supply signal");
      if (!configData.GeneralEvents.TimedSupply.Enabled)
      {
        PrintDebug("Event for timed supply disabled. Skipping event creation.");
        return;
      }

      if (isLanded == configData.GeneralEvents.TimedSupply.StartWhenSpawned)
      {
        PrintDebug($"{GetSupplyDropStateName(isLanded)} for timed supply disabled.");
        return;
      }

      const string eventNameSD = nameof(GeneralEventType.SupplyDrop);
      if (!CanCreateDynamicPVP(eventNameSD, supplyDrop))
      {
        return;
      }

      HandleParentedEntityEvent(
        eventNameSD, supplyDrop, parentOnCreate: isLanded);
    }

    private KeyValuePair<Vector3, Timer>? GetSupplySignalNear(Vector3 position)
    {
      PrintDebug($"Checking {activeSupplySignals.Count} active supply signals");
      if (activeSupplySignals.Count <= 0)
      {
        PrintDebug("No active signals, must be from a timed event cargo plane");
        return null;
      }

      foreach (var entry in activeSupplySignals)
      {
        var distance = Vector3Ex.Distance2D(entry.Key, position);
        PrintDebug($"Found a supply signal at {entry.Key} located {distance}m away.");
        if (distance > configData.Global.CompareRadius) continue;
        PrintDebug("Found matching a supply signal.");
        return entry;
      }

      PrintDebug("No matches found, probably from a timed event cargo plane");
      return null;
    }

    #endregion SupplyDrop And SupplySignal Event

    #region CargoShip Event

    // invoke appropriate hook handler for current CargoShip state
    // this is only used on startup, to (re)create events for already-existing
    //  CargoShip entities
    private void StartupCargoShip(CargoShip cargoShip)
    {
      if (!cargoShip)
      {
        PrintDebug("CargoShip is null");
        return;
      }

      if (!configData.GeneralEvents.CargoShip.Enabled)
      {
        PrintDebug("Cargo Ship Event is disabled");
        return;
      }

      if (cargoShip.IsShipDocked)
      {
        // docked
        PrintDebug("Found docked cargo ship");
        OnCargoShipHarborArrived(cargoShip);
      }
      else if (cargoShip.HasFlag(CargoShip.Egressing))
      {
        // leaving the map
        PrintDebug("Found egressing cargo ship");
        OnCargoShipEgress(cargoShip);
      }
      else if (
        cargoShip.isDoingHarborApproach && cargoShip.currentHarborApproachNode <
        cargoShip.harborApproachPath.nodes.Count / 2)
      {
        // approaching a Harbor
        PrintDebug("Found cargo ship approaching Harbor");
        OnCargoShipHarborApproach(cargoShip, null);
      }
      else if (cargoShip.HasFlag(CargoShip.HasDocked))
      {
        // not in any other state, but has previously docked, so this is
        //  equivalent to having most recently received an
        //  OnCargoShipHarborLeave hook call
        PrintDebug("Found cargo ship leaving Harbor");
        OnCargoShipHarborLeave(cargoShip);
      }
      else
      {
        // not docked, not egressing, not approaching, has never docked
        // this implies it has most recently spawned
        PrintDebug("Found spawned cargo ship");
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

      if (!configData.GeneralEvents.CargoShip.Enabled)
      {
        return;
      }

      // create/update or attempt to delete zone, based on desired state
      var zoneId = cargoShip.net.ID.ToString();
      var zoneExists = _activeDynamicZones.ContainsKey(zoneId);
      if (zoneExists == state)
      {
        PrintDebug($"CargoShip event {zoneId} is already in desired state={state}");
        return;
      }

      if (state)
      {
        PrintDebug($"Trying to create CargoShip post-spawn event {zoneId}");
        if (!CheckEntityOwner(cargoShip))
        {
          return;
        }

        const string eventName = nameof(GeneralEventType.CargoShip);
        // call this here, because otherwise it's difficult to ensure that we
        //  call it exactly once
        if (!CanCreateDynamicPVP(eventName, cargoShip))
        {
          return;
        }

        NextTick(() => HandleParentedEntityEvent(
          eventName, cargoShip, parentOnCreate: true));
      }
      else
      {
        PrintDebug($"Trying to delete CargoShip post-spawn event {zoneId}");
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

      if (!configData.GeneralEvents.CargoShip.Enabled ||
          !configData.GeneralEvents.CargoShip.SpawnState)
      {
        // not configured to create event on spawn
        return;
      }

      PrintDebug("Trying to create CargoShip spawn event");
      if (!CheckEntityOwner(cargoShip))
      {
        return;
      }

      const string eventName = nameof(GeneralEventType.CargoShip);
      // call this here, because otherwise it's difficult to ensure that we call
      //  it exactly once
      if (!CanCreateDynamicPVP(eventName, cargoShip))
      {
        return;
      }

      NextTick(() =>
        HandleParentedEntityEvent(eventName, cargoShip, parentOnCreate: true));
    }

    private void OnEntityKill(CargoShip cargoShip)
    {
      if (!cargoShip || null == cargoShip.net)
      {
        return;
      }

      if (!configData.GeneralEvents.CargoShip.Enabled)
      {
        return;
      }

      HandleDeleteDynamicZone(cargoShip.net.ID.ToString());
    }

    private void OnCargoShipEgress(CargoShip cargoShip) => HandleCargoState(
      cargoShip, configData.GeneralEvents.CargoShip.EgressState);

    private void OnCargoShipHarborApproach(
      CargoShip cargoShip, CargoNotifier _) => HandleCargoState(
      cargoShip, configData.GeneralEvents.CargoShip.ApproachState);

    private void OnCargoShipHarborArrived(
      CargoShip cargoShip) => HandleCargoState(
      cargoShip, configData.GeneralEvents.CargoShip.DockState);

    private void OnCargoShipHarborLeave(
      CargoShip cargoShip) => HandleCargoState(
      cargoShip, configData.GeneralEvents.CargoShip.DepartState);

    #endregion CargoShip Event

    #endregion General Event

    #region Monument Event

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
    // if attached=true, monument rotation is subtracted from volume rotation
    //  prior to normalizing (NOTE: custom monuments have global PB colliders,
    //  while vanilla ones attach them to their transform hierarchies)
    // returns all zeroes if something went wrong
    private static (Vector3 size, float rotation, Vector3 offset)
      GetPreventBuildingParams(Collider collider, Transform tMonument)
    {
      if (!collider || !tMonument)
      {
        return (Vector3.zero, 0.0f, Vector3.zero);
      }

      // if the collider's transform is parented to the root of its hierarchy,
      //  then it's a global collider - probably for a custom map monument
      var global = collider.transform.root == collider.transform.parent;
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
        GetPreventBuildingParams(pbCollider, monumentTransform);
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
      GetMonumentGeometry(Transform monumentTransform, MonumentEventType type)
    {
      switch (type)
      {
        case MonumentEventType.Default:
          return GetPreventBuildingParams(monumentTransform, false);
        case MonumentEventType.Custom:
          return GetPreventBuildingParams(monumentTransform, true);
        case MonumentEventType.TunnelEntrance:
          return GetTunnelLinkParams(monumentTransform, true);
        case MonumentEventType.TunnelLink:
          return GetTunnelLinkParams(monumentTransform, false);
        case MonumentEventType.TunnelSection:
          return GetPreventBuildingParams(monumentTransform, false);
        case MonumentEventType.UnderwaterLabs:
          return GetLabLinkParams(monumentTransform);
      }
      throw new ArgumentOutOfRangeException(nameof(type), type, null);
    }

    // return whether a geometry check should occur for the given monument event
    private static bool ShouldCheckGeometry(MonumentEvent monumentEvent) =>
      monumentEvent.DynamicZone.DoAutoGeo;

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
        if (!configData.MonumentEvents.TryGetValue(name, out var monumentEvent))
        {
          // pathological: cached original geometry for a monument event that
          //  doesn't exist
          continue;
        }
        if (changed || oGeo.HasSameData(monumentEvent)) continue;
        // geometry data changed from original config values; request save
        PrintDebug($"Geometry changed for monument event {name}; subsequent changes may not be reported");
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
      if (!configData.MonumentEvents.TryGetValue(
            monumentName, out var monumentEvent))
      {
        monumentEvent = new MonumentEvent();
        configData.MonumentEvents.Add(monumentName, monumentEvent);
        addedEvents.Add(monumentName);
      }

      if (monumentEvent.Enabled)
      {
        if (ShouldCheckGeometry(monumentEvent))
        {
          PrintDebug($"Calculating geometry for monument event {monumentName} with type {type}");
          CheckMonumentGeometry(
            monumentName, transform, type,
            !createdEvents.Contains(monumentName), monumentEvent);
        }
        if (HandleMonumentEvent(monumentName, transform, monumentEvent))
        {
          createdEvents.Add(monumentName);
        }
      }

      yield return DynamicYield();
    }

    private static (string monumentName, bool custom) GetLandmarkName(
      LandmarkInfo landmarkInfo)
    {
      var monumentName = landmarkInfo.displayPhrase.english.Trim();
      return
        string.IsNullOrEmpty(monumentName) &&
        landmarkInfo.name.Contains("monument_marker.prefab") ?
          (landmarkInfo.transform.root.name, true) :
          (monumentName, false);
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
          PrintDebug($"Skipping visible landmark because it has no map title: {landmarkInfo}");
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
          custom ? MonumentEventType.Custom :
          "Underwater Lab" == monumentName ? MonumentEventType.UnderwaterLabs :
          MonumentEventType.Default;
        yield return CreateMonumentEvent(
          monumentName, landmarkInfo.transform, type,
          addedEvents, createdEvents);
      }

      yield return DynamicYield(true);
    }

    // derive a user-friendly event name from a Train Tunnel section prefab name
    // returns empty string on failure, which is debug logged
    private string ToTunnelSectionEventName(string cellName)
    {
      if (!cellName.StartsWith("assets/bundled/prefabs/autospawn/tunnel"))
      {
        PrintDebug($"Skipping unsupported DungeonGridCell type due to non-tunnel: {cellName}");
        return "";
      }
      // NOTE: this must be returned to pool on any return from this method
      var stringBuilder = Pool.Get<StringBuilder>();
      stringBuilder.Clear().Append("Train Tunnel");
      // get the "tunnelXYZ" part of the name
      var slashSplit = cellName.Split("/");
      if (slashSplit.Length < 6)
      {
        PrintDebug($"Skipping unsupported DungeonGridCell type due to path components: {cellName}");
        Pool.FreeUnmanaged(ref stringBuilder);
        return "";
      }
      var upwards = "tunnel-upwards" == slashSplit[4];
      var dashSplit = slashSplit[5].Split("-");
      if (dashSplit.Length < 2)
      {
        PrintDebug($"Skipping unsupported DungeonGridCell type due to name components: {cellName}");
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
        PrintDebug($"Skipping unsupported DungeonGridCell type due to feature component '{feature}': {cellName}");
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
          PrintDebug($"Skipping unsupported DungeonGridCell type due to unknown direction '{direction}': {cellName}");
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
        PrintDebug($"Skipping DungeonGridInfo with empty Links list: {dungeonGridInfo.name}");
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
        PrintDebug($"Skipping DungeonGridInfo with unknown bounds: {dungeonGridInfo.name}@{dungeonGridInfo.transform.position}");
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
        var (landmarkName, _) = GetLandmarkName(entrance);
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
        PrintDebug($"{addedEvents.Count} new monument event(s) added to config: {string.Join(", ", addedEvents)}");
        save = true;
      }
      if (ShouldSaveGeometry())
      {
        PrintDebug("Recording geometry change(s) to config file");
        save = true;
      }
      if (save) SaveConfig();

      if (createdEvents.Count > 0)
      {
        PrintDebug($"{createdEvents.Count} monument event(s) successfully created: {string.Join(", ", createdEvents)}");
      }

      Pool.FreeUnmanaged(ref addedEvents);
      Pool.FreeUnmanaged(ref createdEvents);

      yield return DynamicYield(true);
    }

    #endregion Monument Event

    #region Auto Event

    // coroutine to create user-defined auto events
    private IEnumerator CreateAutoEvents()
    {
      var createdEvents = Pool.Get<List<string>>();

      // create auto events from data file
      foreach (var entry in storedData.autoEvents)
      {
        if (!entry.Value.AutoStart || !CreateDynamicZone(
            entry.Key, entry.Value.Position, entry.Value.ZoneId))
        {
          continue;
        }
        createdEvents.Add(entry.Key);
        yield return DynamicYield();
      }
      if (createdEvents.Count > 0)
      {
        PrintDebug($"{createdEvents.Count} auto event(s) successfully created: {string.Join(", ", createdEvents)}");
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
        var baseEvent = GetBaseEvent(leftZone.eventName);
        if (baseEvent?.CommandWorksForPVPDelay == true &&
            IsBlockedCommand(baseEvent, command, isChat))
        {
          return false;
        }
      }

      var result = ZM_GetPlayerZoneIDs(player);
      if (result == null || result.Length == 0)
      {
        return null;
      }

      foreach (var zoneId in result)
      {
        if (string.IsNullOrEmpty(zoneId) ||
            !_activeDynamicZones.TryGetValue(zoneId, out var eventName))
        {
          continue;
        }
        PrintDebug($"Checking command: {command} , zoneId: {zoneId}");
        var baseEvent = GetBaseEvent(eventName);
        if (null != baseEvent && IsBlockedCommand(baseEvent, command, isChat))
        {
          return false;
        }
      }

      return null;
    }

    private bool IsBlockedCommand(
      BaseEvent baseEvent, string command, bool isChat)
    {
      if (null == baseEvent || baseEvent.CommandList.Count <= 0) return false;
      var commandExist = baseEvent.CommandList.Exists(entry =>
        entry.StartsWith('/') && isChat ?
          entry.Substring(1).Equals(command) : command.Contains(entry));
      if (baseEvent.UseBlacklistCommands != commandExist) return false;
      PrintDebug($"Use {(baseEvent.UseBlacklistCommands ? "blacklist" : "whitelist")}, Blocked command: {command}");
      return true;
    }

    #endregion Chat/Console Command Handler

    #region DynamicZone Handler

    // create a zone that is parented to an entity, such that they move together
    // NOTE: caller is responsible for calling CheckEntityOwner() and
    //  CanCreateDynamicPVP() first, because this method can't easily implement
    //  calling them exactly once
    private void HandleParentedEntityEvent(
      string eventName, BaseEntity parentEntity, bool parentOnCreate,
      bool delay = true)
    {
      if (!parentEntity || null == parentEntity.net)
      {
        return;
      }
      var baseEvent = GetBaseEvent(eventName);
      if (baseEvent == null)
      {
        return;
      }
      if (delay && baseEvent.EventStartDelay > 0f)
      {
        timer.Once(baseEvent.EventStartDelay, () => HandleParentedEntityEvent(
          eventName, parentEntity, parentOnCreate, false));
        return;
      }
      PrintDebug($"Trying to create parented entity eventName={eventName} on parentEntity={parentEntity}.");
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
      if (!CreateDynamicZone(eventName, zonePosition, zoneId, delay: false))
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

    private bool HandleMonumentEvent(
      string eventName, Transform transform, MonumentEvent monumentEvent)
    {
      var position = monumentEvent.TransformPosition == Vector3.zero ?
        transform.position :
        monumentEvent.DynamicZone.FixedRotation ?
          transform.position + monumentEvent.TransformPosition :
          transform.TransformPoint(monumentEvent.TransformPosition);
      return CreateDynamicZone(
        eventName, position, monumentEvent.ZoneId,
        monumentEvent.GetDynamicZone().ZoneSettings(transform));
    }

    private void HandleGeneralEvent(
      GeneralEventType generalEventType, BaseEntity baseEntity, bool useEntityId)
    {
      var eventName = generalEventType.ToString();
      if (useEntityId)
      {
        if (!baseEntity || null == baseEntity.net)
        {
          PrintDebug($"Aborting creation of eventName={eventName}, because entity is null", DebugLevel.Warning);
          return;
        }
        if (_activeDynamicZones.ContainsKey(baseEntity.net.ID.ToString()))
        {
          PrintDebug($"Aborting creation of redundant eventName={eventName} for baseEntity={baseEntity} with baseEntity.net.ID={baseEntity.net.ID}", DebugLevel.Warning);
          return;
        }
      }
      if (!CanCreateDynamicPVP(eventName, baseEntity))
      {
        return;
      }
      var baseEvent = GetBaseEvent(eventName);
      if (baseEvent == null)
      {
        return;
      }
      var position = baseEntity.transform.position;
      position.y = TerrainMeta.HeightMap.GetHeight(position);
      CreateDynamicZone(
        eventName, position,
        useEntityId ? baseEntity.net.ID.ToString() : null,
        baseEvent.GetDynamicZone().ZoneSettings(baseEntity.transform));
    }

    private bool CreateDynamicZone(
      string eventName, Vector3 position, string zoneId = "",
      string[] zoneSettings = null, bool delay = true)
    {
      if (position == Vector3.zero)
      {
        PrintDebug($"CreateDynamicZone(): ERROR: Invalid location, zone creation failed for eventName={eventName}.", DebugLevel.Error);
        return false;
      }
      var baseEvent = GetBaseEvent(eventName);
      if (baseEvent == null)
      {
        PrintDebug($"CreateDynamicZone(): ERROR: No baseEvent for eventName={eventName}.", DebugLevel.Error);
        return false;
      }
      if (delay && baseEvent.EventStartDelay > 0f)
      {
        timer.Once(baseEvent.EventStartDelay, () =>
          CreateDynamicZone(eventName, position, zoneId, zoneSettings, false));
        PrintDebug($"CreateDynamicZone(): Delaying zone creation for eventName={eventName} by {baseEvent.EventStartDelay}s.");
        return false;
      }

      float duration = -1;
      if (baseEvent is BaseTimedEvent timedEvent &&
          (baseEvent is not ITimedDisable timedDisable ||
           !timedDisable.IsTimedDisabled()))
      {
        duration = timedEvent.Duration;
      }

      if (string.IsNullOrEmpty(zoneId))
      {
        // TODO: prefix with plugin name or event type?
        zoneId = DateTime.Now.ToString("HHmmssffff");
      }

      var dynamicZone = baseEvent.GetDynamicZone();
      zoneSettings ??= dynamicZone.ZoneSettings();

      PrintDebug($"Trying to create zoneId={zoneId} for eventName={eventName} at position={position}{(dynamicZone is ISphereZone zone ? $", radius={zone.Radius}m" : null)}{(dynamicZone is ICubeZone cubeZone ? $", size={cubeZone.Size}" : null)}{(dynamicZone is IParentZone parentZone ? $", center={parentZone.Center}" : null)}{(dynamicZone is IRotateZone rotateZone ? $", rotation={rotateZone.Rotation}, fixedRotation={rotateZone.FixedRotation}" : null)}, duration={duration}s.");
      var zoneRadius = dynamicZone is ISphereZone sz ? sz.Radius : 0;
      var zoneSize = dynamicZone is ICubeZone cz ? cz.Size.magnitude : 0;
      if (zoneRadius <= 0 && zoneSize <= 0)
      {
        PrintError($"ERROR: Cannot create zone for eventName={eventName} because both radius and size are less than or equal to zero");
        return false;
      }
      if (!ZM_CreateOrUpdateZone(zoneId, zoneSettings, position))
      {
        PrintDebug($"ERROR: Zone NOT created for eventName={eventName}.", DebugLevel.Error);
        return false;
      }

      if (_activeDynamicZones.TryAdd(zoneId, eventName))
      {
        CheckHooks(HookCheckReasons.ZoneAdded);
      }

      if (baseEvent.BypassLootDefenderLocks &&
          _activeLootDefenderBypassZones.Count == 0 &&
          _activeLootDefenderBypassZones.Add(zoneId))
      {
        // added Loot Defender bypass zone to an empty set, so subscribe to hook
        Subscribe(nameof(OnLootLockedEntity));
      }

      var stringBuilder = Pool.Get<StringBuilder>();
      stringBuilder.Clear();
      if (baseEvent is DomeEvent domeEvent &&
          dynamicZone is ISphereZone sphereZone &&
          DomeCreateAllowed(domeEvent, sphereZone))
      {
        if (CreateDome(
          zoneId, position, sphereZone.Radius, domeEvent.DomesDarkness))
        {
          stringBuilder.Append("Dome,");
        }
        else
        {
          PrintDebug($"ERROR: Dome NOT created for zoneId={zoneId}.", DebugLevel.Error);
        }
      }

      if (baseEvent is BotDomeEvent botEvent &&
          BotReSpawnAllowed(botEvent))
      {
        if (SpawnBots(position, botEvent.BotProfileName, zoneId))
        {
          stringBuilder.Append("Bots,");
        }
        else
        {
          PrintDebug($"ERROR: Bot(s) NOT spawned for zoneId={zoneId}.", DebugLevel.Error);
        }
      }

      if (TP_AddOrUpdateMapping(zoneId, baseEvent.Mapping))
      {
        stringBuilder.Append("Mapping,");
      }
      else
      {
        PrintDebug($"ERROR: Mapping NOT created for zoneId={zoneId}.", DebugLevel.Error);
      }

      PrintDebug($"Created zoneId={zoneId} for eventName={eventName} with properties: {stringBuilder.ToString().TrimEnd(',')}.");
      HandleDeleteDynamicZone(zoneId, duration, eventName);

      stringBuilder.Clear();
      Pool.FreeUnmanaged(ref stringBuilder);
      Interface.CallHook("OnCreatedDynamicPVP",
        zoneId, eventName, position, duration);
      return true;
    }

    private void HandleDeleteDynamicZone(
      string zoneId, float duration, string eventName)
    {
      if (duration <= 0f) return;
      TryRemoveEventTimer(zoneId);
      PrintDebug($"Scheduling deletion of zoneId={zoneId} for eventName={eventName} in {duration} second(s).");
      _eventTimers.Add(
        zoneId, timer.Once(duration, () => HandleDeleteDynamicZone(zoneId)));
    }

    private void HandleDeleteDynamicZone(string zoneId)
    {
      if (string.IsNullOrEmpty(zoneId) ||
          !_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        // this isn't an error, because sometimes deletion is requested "just in
        //  case", and/or because multiple delete stimuli occurred
        PrintDebug($"HandleDeleteDynamicZone(): Skipping delete for unknown zoneId={zoneId}.");
        return;
      }
      if (Interface.CallHook("OnDeleteDynamicPVP", zoneId, eventName) != null)
      {
        return;
      }
      var baseEvent = GetBaseEvent(eventName);
      if (null == baseEvent)
      {
        return;
      }
      if (baseEvent.EventStopDelay > 0f)
      {
        TryRemoveEventTimer(zoneId);
        if (baseEvent.GetDynamicZone() is IParentZone)
        {
          // untether zone from parent entity
          ZM_GetZoneByID(zoneId)?.transform.SetParent(null, true);
          // also untether any domes
          ParentDome(zoneId, Vector3.zero);
        }
        _eventTimers.Add(zoneId, timer.Once(
          baseEvent.EventStopDelay, () => DeleteDynamicZone(zoneId)));
      }
      else
      {
        DeleteDynamicZone(zoneId);
      }
    }

    private bool DeleteDynamicZone(string zoneId)
    {
      if (string.IsNullOrEmpty(zoneId) ||
          !_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        // this isn't an error, because sometimes deletion is requested "just in
        //  case", and/or because multiple delete stimuli occurred
        PrintDebug($"DeleteDynamicZone(): Skipping delete for unknown zoneId={zoneId}.");
        return false;
      }

      TryRemoveEventTimer(zoneId);
      var baseEvent = GetBaseEvent(eventName);
      if (baseEvent == null)
      {
        return false;
      }

      if (baseEvent.BypassLootDefenderLocks &&
          _activeLootDefenderBypassZones.Remove(zoneId) &&
          _activeLootDefenderBypassZones.Count == 0)
      {
        // removed only Loot Defender bypass zone, so unsubscribe from hook
        Unsubscribe(nameof(OnLootLockedEntity));
      }

      var stringBuilder = Pool.Get<StringBuilder>();
      stringBuilder.Clear();
      if (DomeCreateAllowed(
        baseEvent as DomeEvent, baseEvent.GetDynamicZone() as ISphereZone))
      {
        if (RemoveDome(zoneId))
        {
          stringBuilder.Append("Dome,");
        }
        else
        {
          PrintDebug($"ERROR: Dome NOT removed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.Error);
        }
      }

      if (BotReSpawnAllowed(baseEvent as BotDomeEvent))
      {
        if (KillBots(zoneId))
        {
          stringBuilder.Append("Bots,");
        }
        else
        {
          PrintDebug($"ERROR: Bot(s) NOT killed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.Error);
        }
      }

      if (TP_RemoveMapping(zoneId))
      {
        stringBuilder.Append("Mapping,");
      }
      else
      {
        PrintDebug($"ERROR: Mapping NOT removed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.Error);
      }

      var players = ZM_GetPlayersInZone(zoneId);
      var zoneRemoved = ZM_EraseZone(zoneId, eventName);
      if (zoneRemoved)
      {
        // Release zone players immediately
        PrintDebug($"Releasing zone players: {string.Join(",", players.Select(x => x.displayName))}");
        foreach (var player in players)
        {
          OnExitZone(zoneId, player);
        }
        if (_activeDynamicZones.Remove(zoneId))
        {
          CheckHooks(HookCheckReasons.ZoneRemoved);
        }
        PrintDebug($"Deleted zoneId={zoneId} with eventName={eventName} and properties: {stringBuilder.ToString().TrimEnd(',')}.");
        Interface.CallHook("OnDeletedDynamicPVP", zoneId, eventName);
      }
      else
      {
        PrintDebug($"ERROR: Zone NOT removed for zoneId={zoneId} with eventName={eventName} and properties: {stringBuilder.ToString().TrimEnd(',')}.", DebugLevel.Error);
      }

      stringBuilder.Clear();
      Pool.FreeUnmanaged(ref stringBuilder);
      return zoneRemoved;
    }

    private void DeleteOldDynamicZones()
    {
      var zoneIds = ZM_GetZoneIDs();
      if (zoneIds is not { Length: > 0 }) return;
      int attempts = 0, successes = 0;
      foreach (var zoneId in zoneIds)
      {
        if (ZM_GetZoneName(zoneId) != ZoneName) continue;
        attempts++;
        if (ZM_EraseZone(zoneId)) successes++;
        TP_RemoveMapping(zoneId);
      }
      PrintDebug($"Deleted {successes} of {attempts} obsolete DynamicPVP zone(s)", DebugLevel.Warning);
    }

    #endregion DynamicZone Handler

    #region Domes

    private readonly Dictionary<string, List<SphereEntity>> _zoneSpheres = new();

    private static bool DomeCreateAllowed(
      DomeEvent domeEvent, ISphereZone sphereZone) =>
      domeEvent?.DomesEnabled == true && sphereZone?.Radius > 0f;

    private bool CreateDome(
      string zoneId, Vector3 position, float radius, int darkness)
    {
      // Method for spherical dome creation
      if (radius <= 0) return false;

      var sphereEntities = Pool.Get<List<SphereEntity>>();
      for (var i = 0; i < darkness; ++i)
      {
        var sphereEntity = GameManager.server.CreateEntity(
          PrefabSphere, position) as SphereEntity;
        if (!sphereEntity)
        {
          PrintDebug("ERROR: Failed to create SphereEntity", DebugLevel.Error);
          return false;
        }
        sphereEntity.enableSaving = false;
        sphereEntity.Spawn();
        sphereEntity.LerpRadiusTo(radius * 2f, radius);
        sphereEntities.Add(sphereEntity);
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
        if (parentEntity)
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
      if (!attacker || !attacker.userID.IsSteamId())
      {
        //The attacker cannot be fully captured
        return null;
      }
      if (_pvpDelays.TryGetValue(victim.userID, out var victimLeftZone))
      {
        if (configData.Global.PvpDelayFlags.HasFlag(
              PvpDelayTypes.ZonePlayersCanDamageDelayedPlayers) &&
            !string.IsNullOrEmpty(victimLeftZone.zoneId) &&
            ZM_IsPlayerInZone(victimLeftZone, attacker))
        {
          //ZonePlayer attack DelayedPlayer
          return true;
        }
        if (configData.Global.PvpDelayFlags.HasFlag(
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
          configData.Global.PvpDelayFlags.HasFlag(
            PvpDelayTypes.DelayedPlayersCanDamageZonePlayers) &&
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
        PrintDebug("AddGroupSpawn returned invalid response.");
        return false;
      }
      switch (result[0])
      {
        case "true":
          return true;
        case "false":
          return false;
        case "error":
          PrintDebug($"ERROR: AddGroupSpawn failed: {result[1]}", DebugLevel.Error);
          return false;
      }
      PrintDebug($"AddGroupSpawn returned unknown response: {result[0]}.");
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
        PrintDebug("RemoveGroupSpawn returned invalid response.");
        return false;
      }
      if (result[0] == "error")
      {
        PrintDebug($"ERROR: RemoveGroupSpawn failed: {result[1]}", DebugLevel.Error);
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
      if (!_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        return;
      }
      Interface.CallHook("OnPlayerEnterPVP", player, zoneId);
      PrintDebug($"{player.displayName} has entered PVP zoneId={zoneId} with eventName={eventName}.");

      if (TryRemovePVPDelay(player)) return;
      // if player is not re-entering zone while in PVP delay, check for
      //  weapon holster
      var baseEvent = GetBaseEvent(eventName);
      if (null == baseEvent || baseEvent.HolsterTime <= 0)
      {
        return;
      }
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
      if (!_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        return;
      }
      PrintDebug($"{player.displayName} has left PVP zoneId={zoneId} with eventName={eventName}.");

      var baseEvent = GetBaseEvent(eventName);
      if (baseEvent is not { PvpDelayEnabled: true } ||
          baseEvent.PvpDelayTime <= 0)
      {
        Interface.CallHook("OnPlayerExitPVP", player, zoneId);
        return;
      }
      Interface.CallHook("OnPlayerExitPVP",
        player, zoneId, baseEvent.PvpDelayTime);

      var leftZone = GetOrAddPVPDelay(player, zoneId, eventName);
      leftZone.zoneTimer = timer.Once(baseEvent.PvpDelayTime, () =>
      {
        TryRemovePVPDelay(player);
      });
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
        ZoneManager.Call("CreateOrUpdateZone", zoneId, zoneArgs, location));

    private bool ZM_EraseZone(string zoneId, string eventName = "")
    {
      try
      {
        return Convert.ToBoolean(ZoneManager.Call("EraseZone", zoneId));
      }
      catch (Exception exception)
      {
        PrintDebug($"ERROR: EraseZone(zoneId={zoneId}) for eventName={eventName} failed: {exception}");
        return true;
      }
    }

    private string[] ZM_GetZoneIDs() =>
      ZoneManager.Call("GetZoneIDs") as string[];

    private string ZM_GetZoneName(string zoneId) =>
      Convert.ToString(ZoneManager.Call("GetZoneName", zoneId));

    private ZoneManager.Zone ZM_GetZoneByID(string zoneId) =>
      ZoneManager.Call("GetZoneByID", zoneId) as ZoneManager.Zone;

    private string[] ZM_GetPlayerZoneIDs(BasePlayer player) =>
      ZoneManager.Call("GetPlayerZoneIDs", player) as string[];

    private bool ZM_IsPlayerInZone(LeftZone leftZone, BasePlayer player) =>
      Convert.ToBoolean(
        ZoneManager.Call("IsPlayerInZone", leftZone.zoneId, player));

    private List<BasePlayer> ZM_GetPlayersInZone(string zoneId) =>
      ZoneManager.Call("GetPlayersInZone", zoneId) as List<BasePlayer>;

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
        PrintDebug($"ERROR: The zoneId={zoneId} has null zone={zone} and/or parentEntity={parentEntity}.", DebugLevel.Error);
        if (deleteOnFailure) DeleteDynamicZone(zoneId);
        return;
      }
      // only support parenting if event implements IParentZone
      if (baseEvent.GetDynamicZone() is not IParentZone parentZone)
      {
        PrintDebug($"ERROR: Not parenting zoneId={zoneId} to parentEntity={parentEntity} because event's DynamicZone does not implement IParentZone.", DebugLevel.Error);
        if (deleteOnFailure) DeleteDynamicZone(zoneId);
        return;
      }
      var zoneTransform = zone.transform;
      var position = parentEntity.transform.TransformPoint(parentZone.Center);
      zoneTransform.SetParent(parentEntity.transform);
      zoneTransform.rotation = parentEntity.transform.rotation;
      zoneTransform.position = position;
      PrintDebug($"Parented zoneId={zoneId} to parentEntity={parentEntity}.");
      // also parent any domes
      ParentDome(zoneId, position, parentEntity);
    }

    #endregion ZoneManager Integration

    #region LootDefender Integration

    // return true to bypass Loot Defender locks if player is in a
    //  bypass-enabled zone, else return null to take no action
    private object OnLootLockedEntity(BasePlayer player, BaseEntity entity)
    {
      if (!player || !entity)
      {
        return null;
      }

      // don't bother checking PVP delay, since this is event bounds based

      var result = ZM_GetPlayerZoneIDs(player);
      if (result == null || result.Length == 0)
      {
        return null;
      }

      foreach (var zoneId in result)
      {
        if (!string.IsNullOrEmpty(zoneId) &&
            _activeLootDefenderBypassZones.Contains(zoneId))
        {
          return true;
        }
      }

      return null;
    }

    #endregion LootDefender Integration

    #region Debug

    private StringBuilder _debugStringBuilder;

    private enum DebugLevel { Error, Warning, Info };

    private void PrintDebug(string message, DebugLevel level = DebugLevel.Info)
    {
      if (configData.Global.DebugEnabled)
      {
        switch (level)
        {
          case DebugLevel.Error:   PrintError(message);   break;
          case DebugLevel.Warning: PrintWarning(message); break;
          case DebugLevel.Info:    Puts(message);         break;
        }
      }

      if (configData.Global.LogToFile)
      {
        _debugStringBuilder.AppendLine($"[{DateTime.Now.ToString(CultureInfo.InstalledUICulture)}] | {message}");
      }
    }

    private void SaveDebug()
    {
      if (!configData.Global.LogToFile)
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

    private string[] AllDynamicPVPZones() => _activeDynamicZones.Keys.ToArray();

    private bool IsDynamicPVPZone(string zoneId) =>
      _activeDynamicZones.ContainsKey(zoneId);

    private bool EventDataExists(string eventName) =>
      storedData.EventDataExists(eventName);

    private bool IsPlayerInPVPDelay(ulong playerId) =>
      _pvpDelays.ContainsKey(playerId);

    private string GetPlayerPVPDelayedZoneID(ulong playerId) =>
      _pvpDelays.TryGetValue(playerId, out var leftZone) ?
        leftZone.zoneId : null;

    private string GetEventName(string zoneId) =>
      _activeDynamicZones.GetValueOrDefault(zoneId);

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
        TimedEvent timedEvent;
        try
        {
          timedEvent = JsonConvert.DeserializeObject<TimedEvent>(eventData);
        }
        catch
        {
          return false;
        }
        storedData.timedEvents.Add(eventName, timedEvent);
      }
      else
      {
        AutoEvent autoEvent;
        try
        {
          autoEvent = JsonConvert.DeserializeObject<AutoEvent>(eventData);
        }
        catch
        {
          return false;
        }
        storedData.autoEvents.Add(eventName, autoEvent);
        if (autoEvent.AutoStart)
        {
          CreateDynamicZone(eventName, autoEvent.Position, autoEvent.ZoneId);
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
        storedData.timedEvents.Add(eventName, new TimedEvent());
      }
      else
      {
        storedData.autoEvents.Add(
          eventName, new AutoEvent { Position = position });
      }
      _dataChanged = true;
      return true;
    }

    private bool RemoveEventData(string eventName, bool forceClose = true)
    {
      if (!EventDataExists(eventName))
      {
        return false;
      }
      storedData.RemoveEventData(eventName);
      if (forceClose)
      {
        ForceCloseZones(eventName);
      }
      _dataChanged = true;
      return true;
    }

    private bool StartEvent(string eventName, Vector3 position)
    {
      if (!EventDataExists(eventName))
      {
        return false;
      }
      var baseEvent = GetBaseEvent(eventName);
      return baseEvent switch
      {
        AutoEvent autoEvent => CreateDynamicZone(
          eventName, position == default ? autoEvent.Position : position,
          autoEvent.ZoneId),
        BaseTimedEvent => CreateDynamicZone(eventName, position),
        _ => false
      };
    }

    private bool StopEvent(string eventName) =>
      EventDataExists(eventName) && ForceCloseZones(eventName);

    private bool ForceCloseZones(string eventName)
    {
      var closed = false;
      foreach (var entry in _activeDynamicZones.ToArray())
      {
        if (entry.Value == eventName && DeleteDynamicZone(entry.Key))
        {
          closed = true;
        }
      }
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
      for (int i = 0; i < vertices.Length; ++i)
      {
        vertices[i] = (rotQ * (vertices[i] - pos)) + pos;
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
      for (int i = 0; i < vertices.Length; ++i)
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
        "ddraw.text", duration, Color.red,    vertices[1], "+x");
      player.SendConsoleCommand(
        "ddraw.text", duration, Color.green,  vertices[2], "+y");
      player.SendConsoleCommand(
        "ddraw.text", duration, Color.blue,   vertices[3], "+z");
    }

    private void CommandHelp(IPlayer iPlayer)
    {
      var stringBuilder = Pool.Get<StringBuilder>();
      var result = stringBuilder
        .Clear()
        .AppendLine()
        .AppendLine(Lang("Syntax",  iPlayer.Id, configData.Chat.Command))
        .AppendLine(Lang("Syntax1", iPlayer.Id, configData.Chat.Command))
        .AppendLine(Lang("Syntax2", iPlayer.Id, configData.Chat.Command))
        .AppendLine(Lang("Syntax3", iPlayer.Id, configData.Chat.Command))
        .AppendLine(Lang("Syntax4", iPlayer.Id, configData.Chat.Command))
        .AppendLine(Lang("Syntax5", iPlayer.Id, configData.Chat.Command))
        .AppendLine(Lang("Syntax6", iPlayer.Id, configData.Chat.Command))
        .AppendLine(Lang("Syntax7", iPlayer.Id, configData.Chat.Command))
        .AppendLine(Lang("Syntax8", iPlayer.Id, configData.Chat.Command))
        .ToString()
      ;
      stringBuilder.Clear();
      Pool.FreeUnmanaged(ref stringBuilder);
      Print(iPlayer, result);
    }

    private void CommandList(IPlayer iPlayer)
    {
      var customEventCount = storedData.CustomEventsCount;
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
      foreach (var entry in storedData.autoEvents)
      {
        i++;
        stringBuilder.AppendLine(Lang("AutoEvent",
          iPlayer.Id, i,
          entry.Key, entry.Value.AutoStart, entry.Value.Position));
      }
      foreach (var entry in storedData.timedEvents)
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

      Print(player, Lang("ShowingZones", player.UserIDString, configData.Chat.ShowDistance, configData.Chat.ShowDuration));

      var playerPosition2D = player.transform.position.XZ2D();
      foreach (var activeEvent in _activeDynamicZones)
      {
        var zoneData = ZM_GetZoneByID(activeEvent.Key);
        if (!zoneData) continue;
        var position = zoneData.transform.position;
        if (Vector2.Distance(playerPosition2D, position.XZ2D()) >
            configData.Chat.ShowDistance)
        {
          continue;
        }
        var baseZone = GetBaseEvent(activeEvent.Value)?.GetDynamicZone();
        var zoneColor = baseZone switch
        {
          SphereCubeDynamicZone => Color.yellow,
          SphereCubeParentDynamicZone => Color.blue,
          _ => Color.red
        };

        switch (zoneData.collider)
        {
          case BoxCollider b:
            DrawCube(
              player, configData.Chat.ShowDuration, zoneColor,
              zoneData.transform.position, b.size,
              zoneData.transform.eulerAngles.y);
            break;

          case SphereCollider s:
            DrawSphere(
              player, configData.Chat.ShowDuration, zoneColor,
              zoneData.transform.position, s.radius,
              zoneData.transform.eulerAngles.y);
            break;
        }

        player.SendConsoleCommand(
          "ddraw.text", configData.Chat.ShowDuration, zoneColor,
          zoneData.transform.position,
          $"{activeEvent.Key}\n{activeEvent.Value}");
      }
    }

    private void CommandEdit(
      IPlayer iPlayer, string eventName, Vector3 position, string arg)
    {
      if (storedData.autoEvents.TryGetValue(eventName, out var autoEvent))
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
      else if (storedData.timedEvents.TryGetValue(eventName, out var timedEvent)
                && float.TryParse(arg, out var duration))
      {
        timedEvent.Duration = duration;
        Print(iPlayer, Lang("TimedEventDuration",
          iPlayer.Id, eventName, duration));
        _dataChanged = true;
        return;
      }
      Print(iPlayer, Lang("SyntaxError", iPlayer.Id, configData.Chat.Command));
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
        Print(iPlayer, Lang("SyntaxError", iPlayer.Id, configData.Chat.Command));
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
      Print(iPlayer, Lang("SyntaxError", iPlayer.Id, configData.Chat.Command));
    }

    #endregion Commands

    #region ConfigurationFile

    private ConfigData configData;

    private sealed class ConfigData
    {
      [JsonProperty(PropertyName = "Global Settings")]
      public GlobalSettings Global { get; set; } = new();

      [JsonProperty(PropertyName = "Chat Settings")]
      public ChatSettings Chat { get; set; } = new();

      [JsonProperty(PropertyName = "General Event Settings")]
      public GeneralEventSettings GeneralEvents { get; set; } = new();

      [JsonProperty(PropertyName = "Monument Event Settings")]
      public SortedDictionary<string, MonumentEvent>
        MonumentEvents { get; set; } = new();

      [JsonProperty(PropertyName = "Version")]
      public VersionNumber Version { get; set; }
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
      public TimedEvent BradleyApc { get; set; } = new();

      [JsonProperty(PropertyName = "Patrol Helicopter Event")]
      public TimedEvent PatrolHelicopter { get; set; } = new();

      [JsonProperty(PropertyName = "Supply Signal Event")]
      public SupplyDropEvent SupplySignal { get; set; } = new();

      [JsonProperty(PropertyName = "Timed Supply Event")]
      public SupplyDropEvent TimedSupply { get; set; } = new();

      [JsonProperty(PropertyName = "Hackable Crate Event")]
      public HackableCrateEvent HackableCrate { get; set; } = new();

      [JsonProperty(PropertyName = "Excavator Ignition Event")]
      public IgnitionEvent ExcavatorIgnition { get; set; } = new();

      [JsonProperty(PropertyName = "Cargo Ship Event")]
      public CargoShipEvent CargoShip { get; set; } = new();
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

      [JsonProperty(PropertyName = "Bypass Loot Defender locks", Order = 11)]
      public bool BypassLootDefenderLocks { get; set; }

      public abstract BaseDynamicZone GetDynamicZone();
    }

    // dome features
    // NOTE: reserve order 20-24
    public abstract class DomeEvent : BaseEvent
    {
      [JsonProperty(PropertyName = "Enable Domes", Order = 20)]
      public bool DomesEnabled { get; set; } = true;

      [JsonProperty(PropertyName = "Domes Darkness", Order = 21)]
      public int DomesDarkness { get; set; } = 8;
    }

    // bot features
    // NOTE: reserve order 25-29
    public abstract class BotDomeEvent : DomeEvent
    {
      [JsonProperty(PropertyName = "Enable Bots (Need BotSpawn Plugin)", Order = 25)]
      public bool BotsEnabled { get; set; }

      [JsonProperty(PropertyName = "BotSpawn Profile Name", Order = 26)]
      public string BotProfileName { get; set; } = string.Empty;
    }

    // Excavator Ignition general event (split off from MonumentEvent because it
    //  doesn't support auto-geo)
    // NOTE: reserve order 30-39
    public class IgnitionEvent : DomeEvent
    {
      [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 30)]
      public SphereCubeDynamicZone DynamicZone { get; set; } = new();

      [JsonProperty(PropertyName = "Zone ID", Order = 31)]
      public string ZoneId { get; set; } = string.Empty;

      [JsonProperty(PropertyName = "Transform Position", Order = 32)]
      public Vector3 TransformPosition { get; set; }

      public override BaseDynamicZone GetDynamicZone() => DynamicZone;
    }

    // NOTE: reserve order 40-49
    // Monument event
    public class MonumentEvent : DomeEvent
    {
      [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 40)]
      public SphereCubeAutoGeoDynamicZone DynamicZone { get; set; } = new();

      [JsonProperty(PropertyName = "Zone ID", Order = 41)]
      public string ZoneId { get; set; } = string.Empty;

      [JsonProperty(PropertyName = "Transform Position", Order = 42)]
      public Vector3 TransformPosition { get; set; }

      public override BaseDynamicZone GetDynamicZone() => DynamicZone;
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
      public string ZoneId { get; set; } = string.Empty;

      [JsonProperty(PropertyName = "Position", Order = 53)]
      public Vector3 Position { get; set; }

      public override BaseDynamicZone GetDynamicZone() => DynamicZone;
    }

    // base class for events that support a duration
    // NOTE: reserve order 60-64
    public abstract class BaseTimedEvent : BotDomeEvent
    {
      [JsonProperty(PropertyName = "Event Duration", Order = 60)]
      public float Duration { get; set; } = 600f;
    }

    // Bradley / Patrol Helecopter general events & user-defined "timed" event
    // NOTE: reserve order 65-69
    public class TimedEvent : BaseTimedEvent
    {
      [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 65)]
      public SphereCubeDynamicZone DynamicZone { get; set; } = new();

      public override BaseDynamicZone GetDynamicZone() => DynamicZone;
    }

    // Hackable Crate general event
    // NOTE: reserve order 70-79
    public class HackableCrateEvent : BaseTimedEvent, ITimedDisable
    {
      [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 70)]
      public SphereCubeParentDynamicZone DynamicZone { get; set; } = new();

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

      public override BaseDynamicZone GetDynamicZone()
      {
        return DynamicZone;
      }

      public bool IsTimedDisabled()
      {
        return StopWhenKilled || TimerStartWhenLooted || TimerStartWhenUnlocked;
      }
    }

    // Supply Signal / Timed Supply general event
    // NOTE: reserve order 80-89
    public class SupplyDropEvent : BaseTimedEvent, ITimedDisable
    {
      [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 80)]
      public SphereCubeParentDynamicZone DynamicZone { get; set; } = new();

      [JsonProperty(PropertyName = "Start Event When Spawned (If false, the event starts when landed)", Order = 81)]
      public bool StartWhenSpawned { get; set; } = true;

      [JsonProperty(PropertyName = "Stop Event When Killed", Order = 82)]
      public bool StopWhenKilled { get; set; }

      [JsonProperty(PropertyName = "Event Timer Starts When Looted", Order = 83)]
      public bool TimerStartWhenLooted { get; set; }

      public override BaseDynamicZone GetDynamicZone()
      {
        return DynamicZone;
      }

      public bool IsTimedDisabled()
      {
        return StopWhenKilled || TimerStartWhenLooted;
      }
    }

    // Cargo Ship general event
    // NOTE: reserve order 90-99
    public class CargoShipEvent : DomeEvent
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
    }

    #region Interface

    public interface ITimedDisable
    {
      bool IsTimedDisabled();
    }

    #endregion Interface

    #endregion Event

    #region Zone

    // NOTE: reserve order 100-199
    public abstract class BaseDynamicZone
    {
      [JsonProperty(PropertyName = "Zone Comfort", Order = 100)]
      public float Comfort { get; set; }

      [JsonProperty(PropertyName = "Zone Radiation", Order = 101)]
      public float Radiation { get; set; }

      [JsonProperty(PropertyName = "Zone Temperature", Order = 102)]
      public float Temperature { get; set; }

      [JsonProperty(PropertyName = "Enable Safe Zone", Order = 103)]
      public bool SafeZone { get; set; }

      [JsonProperty(PropertyName = "Eject Spawns", Order = 104)]
      public string EjectSpawns { get; set; } = string.Empty;

      [JsonProperty(PropertyName = "Zone Parent ID", Order = 105)]
      public string ParentId { get; set; } = string.Empty;

      [JsonProperty(PropertyName = "Enter Message", Order = 106)]
      public string EnterMessage { get; set; } = "Entering a PVP area!";

      [JsonProperty(PropertyName = "Leave Message", Order = 107)]
      public string LeaveMessage { get; set; } = "Leaving a PVP area.";

      [JsonProperty(PropertyName = "Permission Required To Enter Zone", Order = 108)]
      public string Permission { get; set; } = string.Empty;

      [JsonProperty(PropertyName = "Extra Zone Flags", Order = 109)]
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

    // NOTE: reserve order 200-299
    public class SphereCubeDynamicZone : BaseDynamicZone, ISphereZone, ICubeZone, IRotateZone
    {
      [JsonProperty(PropertyName = "Zone Radius", Order = 200)]
      public float Radius { get; set; }

      [JsonProperty(PropertyName = "Zone Size", Order = 201)]
      public Vector3 Size { get; set; }

      [JsonProperty(PropertyName = "Zone Rotation", Order = 202)]
      public float Rotation { get; set; }

      [JsonProperty(PropertyName = "Fixed Rotation", Order = 203)]
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
        if (transform && !FixedRotation)
        {
          transformedRotation += transform.rotation.eulerAngles.y;
        }
        zoneSettings.Add(transformedRotation.ToString(CultureInfo.InvariantCulture));
        GetBaseZoneSettings(zoneSettings);
        return zoneSettings.ToArray();
      }
    }

    // NOTE: reserve order 300-399
    public class SphereCubeParentDynamicZone
      : BaseDynamicZone, ISphereZone, ICubeZone, IParentZone
    {
      [JsonProperty(PropertyName = "Zone Radius", Order = 300)]
      public float Radius { get; set; }

      [JsonProperty(PropertyName = "Zone Size", Order = 301)]
      public Vector3 Size { get; set; }

      [JsonProperty(PropertyName = "Transform Position", Order = 302)]
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

    // NOTE: reserve order 400-499
    public class SphereCubeAutoGeoDynamicZone
      : SphereCubeDynamicZone
    {
      [JsonProperty(PropertyName = "Auto-calculate zone geometry (overwrites existing values)", Order = 400)]
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
        configData = Config.ReadObject<ConfigData>();
        if (configData == null)
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
    }

    protected override void LoadDefaultConfig()
    {
      PrintWarning("Creating a new configuration file");
      configData = new ConfigData
      {
        Version = Version
      };
    }

    protected override void SaveConfig()
    {
      Config.WriteObject(configData);
    }

    private void UpdateConfigValues()
    {
      // handle plugin version updates
      // ...unless config file indicates no version change
      if (configData.Version >= Version) return;

      if (configData.Version <= new VersionNumber(4, 2, 0))
      {
        configData.Global.CompareRadius = 2f;
      }

      if (configData.Version <= new VersionNumber(4, 2, 4))
      {
        LoadData();
        SaveData();
      }

      if (configData.Version <= new VersionNumber(4, 2, 6))
      {
        if (GetConfigValue(out bool value, "General Event Settings", "Supply Signal Event", "Supply Drop Event Start When Spawned (If false, the event starts when landed)"))
        {
          configData.GeneralEvents.SupplySignal.StartWhenSpawned = value;
        }
        if (GetConfigValue(out value, "General Event Settings", "Timed Supply Event", "Supply Drop Event Start When Spawned (If false, the event starts when landed)"))
        {
          configData.GeneralEvents.TimedSupply.StartWhenSpawned = value;
        }
        if (GetConfigValue(out value, "General Event Settings", "Hackable Crate Event", "Hackable Crate Event Start When Spawned (If false, the event starts when unlocking)"))
        {
          configData.GeneralEvents.HackableCrate.StartWhenSpawned = value;
        }
      }

      configData.Version = Version;
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

    private StoredData storedData;

    private sealed class StoredData
    {
      public readonly Dictionary<string, TimedEvent> timedEvents = new();
      public readonly Dictionary<string, AutoEvent> autoEvents = new();

      public bool EventDataExists(string eventName) =>
        timedEvents.ContainsKey(eventName) || autoEvents.ContainsKey(eventName);

      public void RemoveEventData(string eventName)
      {
        if (!timedEvents.Remove(eventName)) autoEvents.Remove(eventName);
      }

      [JsonIgnore]
      public int CustomEventsCount => timedEvents.Count + autoEvents.Count;
    }

    private void LoadData()
    {
      try
      {
        storedData =
          Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
      }
      catch
      {
        storedData = null;
      }
      if (storedData == null)
      {
        ClearData();
      }
    }

    private void ClearData()
    {
      storedData = new StoredData();
      SaveData();
    }

    private void SaveData() =>
      Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

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
        iPlayer.Reply(message, configData.Chat.Prefix);
        return;
      }
      var player = iPlayer.Object as BasePlayer;
      if (player != null)
      {
        Player.Message(player, message, $"<color={configData.Chat.PrefixColor}>{configData.Chat.Prefix}</color>", configData.Chat.SteamIdIcon);
        return;
      }
      iPlayer.Reply(message, $"<color={configData.Chat.PrefixColor}>{configData.Chat.Prefix}</color>");
    }

    private void Print(BasePlayer player, string message)
    {
      if (string.IsNullOrEmpty(message))
      {
        return;
      }
      Player.Message(player, message, string.IsNullOrEmpty(configData.Chat.Prefix) ?
          null : $"<color={configData.Chat.PrefixColor}>{configData.Chat.Prefix}</color>", configData.Chat.SteamIdIcon);
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
}

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
using Random = UnityEngine.Random;

namespace Oxide.Plugins
{
  [Info("Dynamic PVP", "HunterZ/CatMeat/Arainrr", "4.3.0", ResourceId = 2728)]
  [Description("Creates temporary PvP zones on certain actions/events")]
  public class DynamicPVP : RustPlugin
  {
    #region Fields

    [PluginReference]
    private readonly Plugin BotReSpawn, TruePVE, ZoneManager;

    private const string PermissionAdmin = "dynamicpvp.admin";
    private const string PrefabSphere = "assets/prefabs/visualization/sphere.prefab";
    private const string ZoneName = "DynamicPVP";

    private readonly Dictionary<string, Timer> _eventTimers = new();
    private readonly Dictionary<ulong, LeftZone> _pvpDelays = new();
    //ID -> EventName
    private readonly Dictionary<string, string> _activeDynamicZones = new();

    private bool _dataChanged;
    private Vector3 _oilRigPosition;
    private Vector3 _largeOilRigPosition;
    private Coroutine _createEventsCoroutine;
    private bool _useExcludePlayer;
    private bool _subscribedCommands;
    private bool _subscribedDamage;
    private bool _subscribedZones;

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

    #endregion Fields

    #region Oxide Hooks

    private void Init()
    {
      Instance = this;
      LoadData();
      permission.RegisterPermission(PermissionAdmin, this);
      AddCovalenceCommand(configData.Chat.Command, nameof(CmdDynamicPVP));
      Unsubscribe(nameof(CanEntityTakeDamage));
      Unsubscribe(nameof(OnCargoPlaneSignaled));
      Unsubscribe(nameof(OnCrateHack));
      Unsubscribe(nameof(OnCrateHackEnd));
      Unsubscribe(nameof(OnDieselEngineToggled));
      Unsubscribe(nameof(OnEnterZone));
      Unsubscribe(nameof(OnEntityDeath));
      Unsubscribe(nameof(OnEntityKill));
      Unsubscribe(nameof(OnEntitySpawned));
      Unsubscribe(nameof(OnExitZone));
      Unsubscribe(nameof(OnLootEntity));
      Unsubscribe(nameof(OnPlayerCommand));
      Unsubscribe(nameof(OnServerCommand));
      Unsubscribe(nameof(OnSupplyDropLanded));
      if (configData.Global.LogToFile)
      {
        _debugStringBuilder = new StringBuilder();
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
      _createEventsCoroutine = ServerMgr.Instance.StartCoroutine(CreateMonumentEvents());
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
           !configData.GeneralEvents.TimedSupply.StartWhenSpawned) ||
          (configData.GeneralEvents.SupplySignal.Enabled &&
           !configData.GeneralEvents.SupplySignal.StartWhenSpawned))
      {
        Subscribe(nameof(OnSupplyDropLanded));
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
        Subscribe(nameof(OnEntityKill));
        Subscribe(nameof(OnEntitySpawned));
        foreach (var serverEntity in BaseNetworkable.serverEntities)
        {
          if (serverEntity is CargoShip cargoShip)
          {
            OnEntitySpawned(cargoShip);
          }
        }
      }
    }

    private void Unload()
    {
      if (_createEventsCoroutine != null)
      {
        ServerMgr.Instance.StopCoroutine(_createEventsCoroutine);
      }

      if (_activeDynamicZones.Count > 0)
      {
        PrintDebug($"Deleting {_activeDynamicZones.Count} active zones.");
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
          if (sphereEntity != null && !sphereEntity.IsDestroyed)
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
    }

    private void OnServerSave() => timer.Once(Random.Range(0f, 60f), () =>
    {
      SaveDebug();
      if (_dataChanged)
      {
        SaveData();
        _dataChanged = false;
      }
    });

    private void OnPlayerRespawned(BasePlayer player)
    {
      if (player == null || !player.userID.IsSteamId())
      {
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
      PrintDebug($"Adding {player.displayName} to pvp delay.");
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
      PrintDebug($"Removing {player.displayName} from pvp delay.");
      var playerId = player.userID.Get();
      if (_pvpDelays.Remove(playerId, out var leftZone))
      {
        Interface.CallHook(
          "OnPlayerRemovedFromPVPDelay", playerId, leftZone.zoneId, player);
        Pool.Free(ref leftZone);
        CheckHooks(HookCheckReasons.DelayRemoved);
        return true;
      }
      return false;
    }

    private bool CheckEntityOwner(BaseEntity baseEntity)
    {
      if (configData.Global.CheckEntityOwner &&
          baseEntity.OwnerID.IsSteamId() &&
          // HeliSignals and BradleyDrops exception
          baseEntity.skinID == 0)
      {
        PrintDebug($"{baseEntity} is owned by the player {baseEntity.OwnerID}. Skipping event creation.");
        return false;
      }
      return true;
    }

    private bool CanCreateDynamicPVP(string eventName, BaseEntity entity)
    {
      if (Interface.CallHook("OnCreateDynamicPVP", eventName, entity) != null)
      {
        PrintDebug($"There are other plugins that prevent {eventName} events from being created.", DebugLevel.WARNING);
        return false;
      }
      return true;
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
        if (baseEvent != null && baseEvent.CommandList.Count > 0)
        {
          Pool.FreeUnmanaged(ref checkedEvents);
          return true;
        }
      }
      Pool.FreeUnmanaged(ref checkedEvents);
      return false;
    }

    private void CheckCommandHooks(bool added)
    {
      // optimization: abort if adding a delayzone and already subscribed, or if
      //  removing a delay/zone and already unsubscribed
      if (added == _subscribedCommands)
      {
        return;
      }
      bool hasCommands = HasCommands();
      if (hasCommands)
      {
        if (!_subscribedCommands)
        {
          Subscribe(nameof(OnPlayerCommand));
          Subscribe(nameof(OnServerCommand));
          _subscribedCommands = true;
        }
        return;
      }
      if (_subscribedCommands)
      {
        Unsubscribe(nameof(OnPlayerCommand));
        Unsubscribe(nameof(OnServerCommand));
        _subscribedCommands = false;
      }
    }

    private void CheckPvpDelayHooks(bool added)
    {
      // if using TruePVE's ExcludePlayer API, just ensure we're unsubscribed
      if (_useExcludePlayer)
      {
        if (_subscribedDamage)
        {
          Unsubscribe(nameof(CanEntityTakeDamage));
          _subscribedDamage = false;
        }
        return;
      }
      // optimization: abort if adding a delay and already subscribed, or if
      //  removing a delay and already unsubscribed
      if (added == _subscribedDamage)
      {
        return;
      }
      bool haveDelays = _pvpDelays.Count > 0;
      if (haveDelays)
      {
        if (!_subscribedDamage)
        {
          Subscribe(nameof(CanEntityTakeDamage));
          _subscribedDamage = true;
        }
        return;
      }
      if (_subscribedDamage)
      {
        Unsubscribe(nameof(CanEntityTakeDamage));
        _subscribedDamage = false;
      }
    }

    private void CheckZoneHooks(bool added)
    {
      // optimization: abort if adding a zone and already subscribed, or if
      //  removing a zone and already unsubscribed
      if (added == _subscribedZones)
      {
        return;
      }
      bool haveZones = _activeDynamicZones.Count > 0;
      if (haveZones)
      {
        if (!_subscribedZones)
        {
          Subscribe(nameof(OnEnterZone));
          Subscribe(nameof(OnExitZone));
          _subscribedZones = true;
        }
        return;
      }
      if (_subscribedZones)
      {
        Unsubscribe(nameof(OnEnterZone));
        Unsubscribe(nameof(OnExitZone));
        _subscribedZones = false;
      }
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
      if (Interface.CallHook(
        "OnGetBaseEvent", eventName) is BaseEvent externalEvent)
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
            PrintDebug($"ERROR: Missing BaseEvent lookup for generalEventType={generalEventType} for eventName={eventName}.", DebugLevel.ERROR);
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
      PrintDebug($"ERROR: Failed to get base event settings for {eventName}.", DebugLevel.ERROR);
      return null;
    }

    #endregion Methods

    #region Events

    #region General Event

    #region ExcavatorIgnition Event

    private void OnDieselEngineToggled(DieselEngine dieselEngine)
    {
      if (dieselEngine == null || dieselEngine.net == null)
      {
        return;
      }
      var zoneId = dieselEngine.net.ID.ToString();
      if (dieselEngine.IsOn())
      {
        DeleteDynamicZone(zoneId);
        HandleGeneralEvent(
          GeneralEventType.ExcavatorIgnition, dieselEngine, true);
      }
      else
      {
        HandleDeleteDynamicZone(zoneId);
      }
    }

    #endregion ExcavatorIgnition Event

    #region HackableLockedCrate Event

    private void OnEntitySpawned(HackableLockedCrate hackableLockedCrate)
    {
      if (hackableLockedCrate == null || hackableLockedCrate.net == null)
      {
        return;
      }

      if (!configData.GeneralEvents.HackableCrate.Enabled ||
          !configData.GeneralEvents.HackableCrate.StartWhenSpawned)
      {
        return;
      }

      PrintDebug("Trying to create hackable crate spawn event.");
      NextTick(() => LockedCrateEvent(hackableLockedCrate));
    }

    private void OnCrateHack(HackableLockedCrate hackableLockedCrate)
    {
      if (hackableLockedCrate == null || hackableLockedCrate.net == null)
      {
        return;
      }
      PrintDebug("Trying to create hackable crate hack event.");
      NextTick(() => LockedCrateEvent(hackableLockedCrate));
    }

    private void OnCrateHackEnd(HackableLockedCrate hackableLockedCrate)
    {
      if (hackableLockedCrate == null || hackableLockedCrate.net == null)
      {
        return;
      }
      HandleDeleteDynamicZone(
        hackableLockedCrate.net.ID.ToString(),
        configData.GeneralEvents.HackableCrate.Duration,
        GeneralEventType.HackableCrate.ToString());
    }

    private void OnLootEntity(BasePlayer player, HackableLockedCrate hackableLockedCrate)
    {
      if (hackableLockedCrate == null || hackableLockedCrate.net == null)
      {
        return;
      }
      if (!configData.GeneralEvents.HackableCrate.Enabled ||
          !configData.GeneralEvents.HackableCrate.TimerStartWhenLooted)
      {
        return;
      }
      HandleDeleteDynamicZone(
        hackableLockedCrate.net.ID.ToString(),
        configData.GeneralEvents.HackableCrate.Duration,
        GeneralEventType.HackableCrate.ToString());
    }

    private void OnEntityKill(HackableLockedCrate hackableLockedCrate)
    {
      if (hackableLockedCrate == null || hackableLockedCrate.net == null)
      {
        return;
      }

      if (!configData.GeneralEvents.HackableCrate.Enabled ||
          !configData.GeneralEvents.HackableCrate.StopWhenKilled)
      {
        return;
      }
      var zoneId = hackableLockedCrate.net.ID.ToString();
      //When the timer starts, don't stop the event immediately
      if (!_eventTimers.ContainsKey(zoneId))
      {
        HandleDeleteDynamicZone(zoneId);
      }
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
      HandleGeneralEvent(
        GeneralEventType.HackableCrate, hackableLockedCrate, true);
    }

    private static bool IsOnTheCargoShip(
      HackableLockedCrate hackableLockedCrate)
    {
      return hackableLockedCrate.GetComponentInParent<CargoShip>() != null;
    }

    private bool IsOnTheOilRig(HackableLockedCrate hackableLockedCrate)
    {
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
      if (patrolHelicopter == null || patrolHelicopter.net == null)
      {
        return;
      }
      PatrolHelicopterEvent(patrolHelicopter);
    }

    private void OnEntityDeath(BradleyAPC bradleyApc, HitInfo info)
    {
      if (bradleyApc == null || bradleyApc.net == null)
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
      if (supplySignal == null || cargoPlane == null)
      {
        return;
      }
      var dropPosition = cargoPlane.dropPosition;
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
      if (supplyDrop == null || supplyDrop.net == null)
      {
        return;
      }
      if (_activeDynamicZones.ContainsKey(supplyDrop.net.ID.ToString()))
      {
        return;
      }
      NextTick(() => OnSupplyDropEvent(supplyDrop, true));
    }

    private void OnLootEntity(BasePlayer _, SupplyDrop supplyDrop)
    {
      if (supplyDrop == null || supplyDrop.net == null)
      {
        return;
      }
      var zoneId = supplyDrop.net.ID.ToString();
      if (!_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        // no active zone for this supply drop
        return;
      }

      SupplyDropEvent eventConfig = null;
      if (nameof(GeneralEventType.SupplySignal) == eventName)
          eventConfig = configData.GeneralEvents.SupplySignal;
      else if (nameof(GeneralEventType.SupplyDrop) == eventName)
          eventConfig = configData.GeneralEvents.TimedSupply;
      if (null == eventConfig)
      {
        // pathological
        PrintDebug($"Unknown SupplyDrop eventName={eventName} for zoneId={zoneId}", DebugLevel.WARNING);
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
      if (supplyDrop == null || supplyDrop.net == null)
      {
        return;
      }
      var zoneId = supplyDrop.net.ID.ToString();
      if (!_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        // no active zone for this supply drop
        return;
      }

      SupplyDropEvent eventConfig = null;
      if (nameof(GeneralEventType.SupplySignal) == eventName)
          eventConfig = configData.GeneralEvents.SupplySignal;
      else if (nameof(GeneralEventType.SupplyDrop) == eventName)
          eventConfig = configData.GeneralEvents.TimedSupply;
      if (null == eventConfig)
      {
        // pathological
        PrintDebug($"Unknown SupplyDrop eventName={eventName} for zoneId={zoneId}", DebugLevel.WARNING);
        return;
      }

      if (!eventConfig.Enabled || !eventConfig.StopWhenKilled)
      {
        return;
      }

      //When the timer starts, don't stop the event immediately
      if (!_eventTimers.ContainsKey(zoneId))
      {
        HandleDeleteDynamicZone(zoneId);
      }
    }

    private static string GetSupplyDropStateName(bool isLanded) =>
      isLanded ? "Landed" : "Spawned";

    private void OnSupplyDropEvent(SupplyDrop supplyDrop, bool isLanded)
    {
      if (supplyDrop == null || supplyDrop.net == null)
      {
        return;
      }
      PrintDebug($"Trying to create supply drop {GetSupplyDropStateName(isLanded)} event at {supplyDrop.transform.position}.");
      if (!CheckEntityOwner(supplyDrop))
      {
        return;
      }

      var supplySignal = GetSupplySignalNear(supplyDrop.transform.position);
      if (null != supplySignal)
      {
        PrintDebug($"Supply drop is probably from supply signal");
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
        PrintDebug($"Removing Supply signal from active list. Active supply signals remaining: {activeSupplySignals.Count}");
        HandleGeneralEvent(GeneralEventType.SupplySignal, supplyDrop, true);
        return;
      }

      PrintDebug($"Supply drop is probably NOT from supply signal");
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

      HandleGeneralEvent(GeneralEventType.SupplyDrop, supplyDrop, true);
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
        if (distance <= configData.Global.CompareRadius)
        {
          PrintDebug("Found matching a supply signal.");
          return entry;
        }
      }

      PrintDebug("No matches found, probably from a timed event cargo plane");
      return null;
    }

    #endregion SupplyDrop And SupplySignal Event

    #region CargoShip Event

    private void OnEntitySpawned(CargoShip cargoShip)
    {
      if (cargoShip == null || cargoShip.net == null)
      {
        return;
      }
      if (!configData.GeneralEvents.CargoShip.Enabled)
      {
        return;
      }
      PrintDebug("Trying to create CargoShip spawn event.");
      if (!CheckEntityOwner(cargoShip))
      {
        return;
      }
      var eventName = GeneralEventType.CargoShip.ToString();
      if (!CanCreateDynamicPVP(eventName, cargoShip))
      {
        return;
      }

      NextTick(() => HandleParentedEntityEvent(eventName, cargoShip));
    }

    private void OnEntityKill(CargoShip cargoShip)
    {
      if (cargoShip == null || cargoShip.net == null)
      {
        return;
      }
      if (!configData.GeneralEvents.CargoShip.Enabled)
      {
        return;
      }
      HandleDeleteDynamicZone(cargoShip.net.ID.ToString());
    }

    #endregion CargoShip Event

    #endregion General Event

    #region Monument Event

    private IEnumerator CreateMonumentEvents()
    {
      var changed = false;
      var createdEvents = new List<string>();
      foreach (var landmarkInfo in TerrainMeta.Path.Landmarks)
      {
        if (!landmarkInfo.shouldDisplayOnMap)
        {
          continue;
        }
        var monumentName = landmarkInfo.displayPhrase.english.Trim();
        if (string.IsNullOrEmpty(monumentName))
        {
          // not a vanilla map monument; see if it's a custom one
          if (landmarkInfo.name.Contains("monument_marker.prefab"))
          {
            monumentName = landmarkInfo.transform.root.name;
          }
          if (string.IsNullOrEmpty(monumentName))
          {
            // TODO: this seems to trigger for moonpool modules at Underwater
            //  Labs - do we maybe want to support these as a special case?
            PrintDebug($"Skipping displayable landmark because it has no map title: {landmarkInfo}");
            continue;
          }
        }
        switch (landmarkInfo.name)
        {
          case "assets/bundled/prefabs/autospawn/monument/harbor/harbor_1.prefab":
            monumentName += " A";
            break;
          case "assets/bundled/prefabs/autospawn/monument/harbor/harbor_2.prefab":
            monumentName += " B";
            break;
          case "assets/bundled/prefabs/autospawn/monument/offshore/oilrig_1.prefab":
            _largeOilRigPosition = landmarkInfo.transform.position;
            break;
          case "assets/bundled/prefabs/autospawn/monument/offshore/oilrig_2.prefab":
            _oilRigPosition = landmarkInfo.transform.position;
            break;
        }
        if (!configData.MonumentEvents.TryGetValue(
              monumentName, out var monumentEvent))
        {
          changed = true;
          monumentEvent = new();
          configData.MonumentEvents.Add(monumentName, monumentEvent);
          Puts($"A new monument {monumentName} was found and added to the config.");
        }
        if (monumentEvent.Enabled && HandleMonumentEvent(
              monumentName, landmarkInfo.transform, monumentEvent))
        {
          createdEvents.Add(monumentName);
          yield return CoroutineEx.waitForSeconds(0.5f);
        }
      }
      if (changed)
      {
        SaveConfig();
      }
      if (createdEvents.Count > 0)
      {
        PrintDebug($"{createdEvents.Count} monument event(s) successfully created: {string.Join(", ", createdEvents)}");
        createdEvents.Clear();
      }

      foreach (var entry in storedData.autoEvents)
      {
        if (entry.Value.AutoStart && CreateDynamicZone(
              entry.Key, entry.Value.Position, entry.Value.ZoneId))
        {
          createdEvents.Add(entry.Key);
          yield return CoroutineEx.waitForSeconds(0.5f);
        }
      }
      if (createdEvents.Count > 0)
      {
        PrintDebug($"{createdEvents.Count} auto event(s) successfully created: {string.Join(", ", createdEvents)}");
      }
      _createEventsCoroutine = null;
    }

    #endregion Monument Event

    #region Chat/Console Command Handler

    private object OnPlayerCommand(
      BasePlayer player, string command, string[] args) =>
      CheckCommand(player, command, true);

    private object OnServerCommand(ConsoleSystem.Arg arg) =>
      CheckCommand(arg?.Player(), arg?.cmd?.FullName, false);

    private object CheckCommand(BasePlayer player, string command, bool isChat)
    {
      if (player == null || string.IsNullOrEmpty(command))
      {
        return null;
      }
      command = command.ToLower().TrimStart('/');
      if (string.IsNullOrEmpty(command))
      {
        return null;
      }

      if (_pvpDelays.TryGetValue(player.userID, out LeftZone leftZone))
      {
        var baseEvent = GetBaseEvent(leftZone.eventName);
        if (baseEvent != null &&
            baseEvent.CommandWorksForPVPDelay &&
            IsBlockedCommand(baseEvent, command, isChat))
        {
          return false;
        }
      }

      var result = GetPlayerZoneIds(player);
      if (result == null ||
          result.Length == 0 ||
          (result.Length == 1 && string.IsNullOrEmpty(result[0])))
      {
        return null;
      }

      foreach (var zoneId in result)
      {
        if (_activeDynamicZones.TryGetValue(zoneId, out string eventName))
        {
          PrintDebug($"Checking command: {command} , zoneId: {zoneId}");
          var baseEvent = GetBaseEvent(eventName);
          if (baseEvent != null &&
              IsBlockedCommand(baseEvent, command, isChat))
          {
            return false;
          }
        }
      }
      return null;
    }

    private bool IsBlockedCommand(
      BaseEvent baseEvent, string command, bool isChat)
    {
      if (baseEvent != null && baseEvent.CommandList.Count > 0)
      {
        var commandExist = baseEvent.CommandList.Exists(entry =>
          entry.StartsWith('/') && isChat ?
            entry.Substring(1).Equals(command) : command.Contains(entry));
        if (baseEvent.UseBlacklistCommands == commandExist)
        {
          PrintDebug($"Use {(baseEvent.UseBlacklistCommands ? "blacklist" : "whitelist")}, Blocked command: {command}");
          return true;
        }
      }
      return false;
    }

    #endregion Chat/Console Command Handler

    #endregion Events

    #region DynamicZone Handler

    private void HandleParentedEntityEvent(
      string eventName, BaseEntity parentEntity, bool delay = true)
    {
      if (parentEntity == null || parentEntity.net == null)
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
        timer.Once(baseEvent.EventStartDelay, () =>
          HandleParentedEntityEvent(eventName, parentEntity, false));
        return;
      }
      PrintDebug($"Trying to create parented entity eventName={eventName} on parentEntity={parentEntity}.");
      var zoneId = parentEntity.net.ID.ToString();
      if (!CreateDynamicZone(
        eventName, parentEntity.transform.position, zoneId, delay: false))
      {
        return;
      }
      timer.Once(0.25f, () =>
      {
        var zone = GetZoneById(zoneId);
        if (parentEntity == null || zone == null)
        {
          PrintDebug($"ERROR: The zoneId={zoneId} created by eventName={eventName} has null zone={zone} and/or parentEntity={parentEntity}.", DebugLevel.ERROR);
          DeleteDynamicZone(zoneId);
          return;
        }
        var zoneTransform = zone.transform;
        zoneTransform.SetParent(parentEntity.transform);
        zoneTransform.rotation = parentEntity.transform.rotation;
        zoneTransform.position =
          baseEvent.GetDynamicZone() is IParentZone parentZone ?
            parentEntity.transform.TransformPoint(parentZone.Center) :
            parentEntity.transform.position;
        PrintDebug($"The zone={zone} with zoneId={zoneId} for eventName={eventName} was parented to parentEntity={parentEntity}.");
      });
    }

    private bool HandleMonumentEvent(
      string eventName, Transform transform, MonumentEvent monumentEvent)
    {
      var position = monumentEvent.TransformPosition == Vector3.zero ?
        transform.position :
        transform.TransformPoint(monumentEvent.TransformPosition);
      return CreateDynamicZone(
        eventName, position, monumentEvent.ZoneId,
        monumentEvent.GetDynamicZone().ZoneSettings(transform));
    }

    private void HandleGeneralEvent(
      GeneralEventType generalEventType, BaseEntity baseEntity, bool useEntityId)
    {
      var eventName = generalEventType.ToString();
      if (useEntityId &&
          _activeDynamicZones.ContainsKey(baseEntity.net.ID.ToString()))
      {
        PrintDebug($"Aborting creation of redundant eventName={eventName} for baseEntity={baseEntity} with baseEntity.net.ID={baseEntity.net.ID}", DebugLevel.WARNING);
        return;
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
        PrintDebug($"ERROR: Invalid location, zone creation failed for eventName={eventName}.", DebugLevel.ERROR);
        return false;
      }
      var baseEvent = GetBaseEvent(eventName);
      if (baseEvent == null)
      {
        return false;
      }
      if (delay && baseEvent.EventStartDelay > 0f)
      {
        timer.Once(baseEvent.EventStartDelay, () =>
          CreateDynamicZone(eventName, position, zoneId, zoneSettings, false));
        return false;
      }

      float duration = -1;
      if (baseEvent is TimedEvent timedEvent &&
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

      PrintDebug($"Trying to create zoneId={zoneId} for eventName={eventName} at position={position}{(dynamicZone is ISphereZone ? $", radius={(dynamicZone as ISphereZone).Radius}m" : null)}{(dynamicZone is ICubeZone ? $", size={(dynamicZone as ICubeZone)?.Size}" : null)}{(dynamicZone is IParentZone ? $", center={(dynamicZone as IParentZone).Center}" : null)}, duration={duration}s.");
      var zoneRadius = dynamicZone is ISphereZone sz ? sz.Radius : 0;
      var zoneSize = dynamicZone is ICubeZone cz ? cz.Size.magnitude : 0;
      if (zoneRadius <= 0 && zoneSize <= 0)
      {
        PrintError($"ERROR: Cannot create zone for eventName={eventName} because both radius and size are less than or equal to zero");
        return false;
      }
      if (!CreateZone(zoneId, zoneSettings, position))
      {
        PrintDebug($"ERROR: Zone NOT created for eventName={eventName}.", DebugLevel.ERROR);
        return false;
      }

      if (!_activeDynamicZones.ContainsKey(zoneId))
      {
        _activeDynamicZones.Add(zoneId, eventName);
        CheckHooks(HookCheckReasons.ZoneAdded);
      }

      var stringBuilder = Pool.Get<StringBuilder>();
      stringBuilder.Clear();
      var domeEvent = baseEvent as DomeMixedEvent;
      var sphereZone = dynamicZone as ISphereZone;
      if (DomeCreateAllowed(domeEvent, sphereZone))
      {
        if (CreateDome(
          zoneId, position, sphereZone.Radius, domeEvent.DomesDarkness))
        {
          stringBuilder.Append("Dome,");
        }
        else
        {
          PrintDebug($"ERROR: Dome NOT created for zoneId={zoneId}.", DebugLevel.ERROR);
        }
      }

      var botEvent = baseEvent as BotDomeMixedEvent;
      if (BotReSpawnAllowed(botEvent))
      {
        if (SpawnBots(position, botEvent.BotProfileName, zoneId))
        {
          stringBuilder.Append("Bots,");
        }
        else
        {
          PrintDebug($"ERROR: Bot(s) NOT spawned for zoneId={zoneId}.", DebugLevel.ERROR);
        }
      }

      if (TryCreateMapping(zoneId, baseEvent.Mapping))
      {
        stringBuilder.Append("Mapping,");
      }
      else
      {
        PrintDebug($"ERROR: Mapping NOT created for zoneId={zoneId}.", DebugLevel.ERROR);
      }

      PrintDebug($"Created zoneId={zoneId} for eventName={eventName} with properties: {stringBuilder.ToString().TrimEnd(',')}.");
      HandleDeleteDynamicZone(zoneId, duration, eventName);

      stringBuilder.Clear();
      Pool.FreeUnmanaged(ref stringBuilder);
      Interface.CallHook(
        "OnCreatedDynamicPVP", zoneId, eventName, position, duration);
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
        PrintDebug($"ERROR: Invalid zoneId={zoneId}.", DebugLevel.ERROR);
        return;
      }
      if (Interface.CallHook("OnDeleteDynamicPVP", zoneId, eventName) != null)
      {
        return;
      }
      var baseEvent = GetBaseEvent(eventName);
      if (baseEvent == null)
      {
        return;
      }
      if (baseEvent.EventStopDelay > 0f)
      {
        TryRemoveEventTimer(zoneId);
        if (baseEvent.GetDynamicZone() is IParentZone)
        {
          GetZoneById(zoneId)?.transform.SetParent(null, true);
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
        PrintDebug($"ERROR: Invalid zoneId={zoneId}.", DebugLevel.ERROR);
        return false;
      }

      TryRemoveEventTimer(zoneId);
      var baseEvent = GetBaseEvent(eventName);
      if (baseEvent == null)
      {
        return false;
      }

      var stringBuilder = Pool.Get<StringBuilder>();
      stringBuilder.Clear();
      if (DomeCreateAllowed(
        baseEvent as DomeMixedEvent, baseEvent.GetDynamicZone() as ISphereZone))
      {
        if (RemoveDome(zoneId))
        {
          stringBuilder.Append("Dome,");
        }
        else
        {
          PrintDebug($"ERROR: Dome NOT removed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.ERROR);
        }
      }

      if (BotReSpawnAllowed(baseEvent as BotDomeMixedEvent))
      {
        if (KillBots(zoneId))
        {
          stringBuilder.Append("Bots,");
        }
        else
        {
          PrintDebug($"ERROR: Bot(s) NOT killed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.ERROR);
        }
      }

      if (TryRemoveMapping(zoneId))
      {
        stringBuilder.Append("Mapping,");
      }
      else
      {
        PrintDebug($"ERROR: Mapping NOT removed for zoneId={zoneId} with eventName={eventName}.", DebugLevel.ERROR);
      }

      var players = GetPlayersInZone(zoneId);
      var zoneRemoved = RemoveZone(zoneId, eventName);
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
        PrintDebug($"ERROR: Zone NOT removed for zoneId={zoneId} with eventName={eventName} and properties: {stringBuilder.ToString().TrimEnd(',')}.", DebugLevel.ERROR);
      }

      stringBuilder.Clear();
      Pool.FreeUnmanaged(ref stringBuilder);
      return zoneRemoved;
    }

    private void DeleteOldDynamicZones()
    {
      var zoneIds = GetZoneIds();
      if (zoneIds == null || zoneIds.Length <= 0)
      {
        return;
      }
      int attempts = 0, successes = 0;
      foreach (var zoneId in zoneIds)
      {
        if (GetZoneName(zoneId) == ZoneName)
        {
          attempts++;
          if (RemoveZone(zoneId))
          {
            successes++;
          }
          TryRemoveMapping(zoneId);
        }
      }
      PrintDebug($"Deleted {successes} of {attempts} obsolete DynamicPVP zone(s)", DebugLevel.WARNING);
    }

    #endregion DynamicZone Handler

    #region Domes

    private readonly Dictionary<string, List<SphereEntity>> _zoneSpheres = new();

    private static bool DomeCreateAllowed(
      DomeMixedEvent domeEvent, ISphereZone sphereZone) =>
      domeEvent != null && domeEvent.DomesEnabled && sphereZone?.Radius > 0f;

    private bool CreateDome(
      string zoneId, Vector3 position, float radius, int darkness)
    {
      // Method for spherical dome creation
      if (radius <= 0) return false;

      var sphereEntities = Pool.Get<List<SphereEntity>>();
      for (var i = 0; i < darkness; ++i)
      {
        var sphereEntity =
          GameManager.server.CreateEntity(PrefabSphere, position) as SphereEntity;
        if (sphereEntity == null)
        {
          PrintDebug("ERROR: Failed to create SphereEntity", DebugLevel.ERROR);
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
          if (sphereEntity != null && !sphereEntity.IsDestroyed)
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

    #region TruePVE/NextGenPVE Integration

    private object CanEntityTakeDamage(BasePlayer victim, HitInfo info)
    {
      if (info == null || victim == null || !victim.userID.IsSteamId())
      {
        return null;
      }
      var attacker = info.InitiatorPlayer ??
        (info.Initiator != null && info.Initiator.OwnerID.IsSteamId() ?
          BasePlayer.FindByID(info.Initiator.OwnerID) : null);
      if (attacker == null || !attacker.userID.IsSteamId())
      {
        //The attacker cannot be fully captured
        return null;
      }
      if (_pvpDelays.TryGetValue(victim.userID, out var victimLeftZone))
      {
        if (configData.Global.PvpDelayFlags.HasFlag(
              PvpDelayTypes.ZonePlayersCanDamageDelayedPlayers) &&
            !string.IsNullOrEmpty(victimLeftZone.zoneId) &&
            IsPlayerInZone(victimLeftZone, attacker))
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
          IsPlayerInZone(attackerLeftZone2, victim))
      {
        //DelayedPlayer attack ZonePlayer
        return true;
      }
      return null;
    }

    private static bool TryCreateMapping(string zoneId, string mapping) =>
        Convert.ToBoolean(
          Interface.CallHook("AddOrUpdateMapping", zoneId, mapping));

    private static bool TryRemoveMapping(string zoneId) =>
        Convert.ToBoolean(Interface.CallHook("RemoveMapping", zoneId));

    #endregion TruePVE/NextGenPVE Integration

    #region BotReSpawn/MonBots Integration

    private bool BotReSpawnAllowed(BotDomeMixedEvent botEvent)
    {
      if (botEvent == null || string.IsNullOrEmpty(botEvent.BotProfileName))
      {
        return false;
      }
      if (BotReSpawn == null)
      {
        return false;
      }
      return botEvent.BotsEnabled;
    }

    private bool SpawnBots(Vector3 location, string profileName, string groupId)
    {
      if (BotReSpawn == null)
      {
        return false;
      }
      var result = CreateGroupSpawn(location, profileName, groupId);
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
          PrintDebug($"ERROR: AddGroupSpawn failed: {result[1]}", DebugLevel.ERROR);
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
      var result = RemoveGroupSpawn(groupId);
      if (result == null || result.Length < 2)
      {
        PrintDebug("RemoveGroupSpawn returned invalid response.");
        return false;
      }
      if (result[0] == "error")
      {
        PrintDebug($"ERROR: RemoveGroupSpawn failed: {result[1]}", DebugLevel.ERROR);
        return false;
      }
      return true;
    }

    private string[] CreateGroupSpawn(
      Vector3 location, string profileName, string groupId, int quantity = 0) =>
      BotReSpawn?.Call(
        "AddGroupSpawn", location, profileName, groupId, quantity) as string[];

    private string[] RemoveGroupSpawn(string groupId) =>
      BotReSpawn?.Call("RemoveGroupSpawn", groupId) as string[];

    #endregion BotReSpawn/MonBots Integration

    #region ZoneManager Integration

    private void OnEnterZone(string zoneId, BasePlayer player)
    {
      if (player == null || !player.userID.IsSteamId())
      {
        return;
      }
      if (!_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        return;
      }
      PrintDebug($"{player.displayName} has entered PVP zoneId={zoneId} with eventName={eventName}.");

      if (!TryRemovePVPDelay(player))
      {
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
    }

    private void OnExitZone(string zoneId, BasePlayer player)
    {
      if (player == null || !player.userID.IsSteamId())
      {
        return;
      }
      if (!_activeDynamicZones.TryGetValue(zoneId, out var eventName))
      {
        return;
      }
      PrintDebug($"{player.displayName} has left PVP zoneId={zoneId} with eventName={eventName}.");

      var baseEvent = GetBaseEvent(eventName);
      if (null == baseEvent ||
          !baseEvent.PvpDelayEnabled ||
          baseEvent.PvpDelayTime <= 0)
      {
        return;
      }

      var leftZone = GetOrAddPVPDelay(player, zoneId, eventName);
      leftZone.zoneTimer = timer.Once(baseEvent.PvpDelayTime, () =>
      {
        TryRemovePVPDelay(player);
      });
      var playerID = player.userID.Get();
      Interface.CallHook(
        "OnPlayerAddedToPVPDelay",
        playerID, zoneId, baseEvent.PvpDelayTime, player);
      // also notify TruePVE if we're using its API to implement the delay
      if (_useExcludePlayer)
      {
        Interface.CallHook(
          "ExcludePlayer", playerID, baseEvent.PvpDelayTime, this);
      }
    }

    private bool CreateZone(
      string zoneId, string[] zoneArgs, Vector3 location) =>
      Convert.ToBoolean(ZoneManager.Call("CreateOrUpdateZone", zoneId, zoneArgs, location));

    private bool RemoveZone(string zoneId, string eventName = "")
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

    private string[] GetZoneIds() => ZoneManager.Call("GetZoneIDs") as string[];

    private string GetZoneName(string zoneId) =>
      Convert.ToString(ZoneManager.Call("GetZoneName", zoneId));

    private ZoneManager.Zone GetZoneById(string zoneId) =>
      ZoneManager.Call("GetZoneByID", zoneId) as ZoneManager.Zone;

    private string[] GetPlayerZoneIds(BasePlayer player) =>
      ZoneManager.Call("GetPlayerZoneIDs", player) as string[];

    private bool IsPlayerInZone(LeftZone leftZone, BasePlayer player) =>
      Convert.ToBoolean(
        ZoneManager.Call("IsPlayerInZone", leftZone.zoneId, player));

    private List<BasePlayer> GetPlayersInZone(string zoneId) =>
      ZoneManager.Call("GetPlayersInZone", zoneId) as List<BasePlayer>;

    #endregion ZoneManager Integration

    #region Debug

    private StringBuilder _debugStringBuilder;

    enum DebugLevel { ERROR, WARNING, INFO };

    private void PrintDebug(string message, DebugLevel level = DebugLevel.INFO)
    {
      if (configData.Global.DebugEnabled)
      {
        switch (level)
        {
          case DebugLevel.ERROR:   PrintError(message);   break;
          case DebugLevel.WARNING: PrintWarning(message); break;
          case DebugLevel.INFO:    Puts(message);         break;
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
      _activeDynamicZones.TryGetValue(zoneId, out var eventName) ?
        eventName : null;

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
        storedData.timedEvents.Add(eventName, new());
      }
      else
      {
        storedData.autoEvents.Add(eventName, new() { Position = position });
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
      if (baseEvent is AutoEvent autoEvent)
      {
        return CreateDynamicZone(
          eventName,
          position == default ? autoEvent.Position : position,
          autoEvent.ZoneId);
      }
      if (baseEvent is TimedEvent)
      {
        return CreateDynamicZone(eventName, position);
      }
      return false;
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
        new(pos.x - halfSize.x, pos.y, pos.z),
        new(pos.x + halfSize.x, pos.y, pos.z),
        new(pos.x, pos.y - halfSize.y, pos.z),
        new(pos.x, pos.y + halfSize.y, pos.z),
        new(pos.x, pos.y, pos.z - halfSize.z),
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
        "ddraw.line", duration, color, vertices[0], vertices[1]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[0], vertices[2]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[0], vertices[4]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[1], vertices[3]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[1], vertices[5]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[2], vertices[3]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[2], vertices[6]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[3], vertices[7]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[4], vertices[5]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[4], vertices[6]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[5], vertices[7]);
      player.SendConsoleCommand(
        "ddraw.line", duration, color, vertices[6], vertices[7]);
      // axes
      player.SendConsoleCommand(
        "ddraw.arrow", duration, Color.red,   vertices[8],  vertices[9],  5);
      player.SendConsoleCommand(
        "ddraw.arrow", duration, Color.green, vertices[10], vertices[11], 5);
      player.SendConsoleCommand(
        "ddraw.arrow", duration, Color.blue,  vertices[12], vertices[13], 5);
      player.SendConsoleCommand(
        "ddraw.text", duration, Color.red,   vertices[9] , "+x");
      player.SendConsoleCommand(
        "ddraw.text", duration, Color.green, vertices[11], "+y");
      player.SendConsoleCommand(
        "ddraw.text", duration, Color.blue,  vertices[13], "+z");
    }

    private static void DrawSphere(
      BasePlayer player, float duration, Color color,
      Vector3 position, float radius) =>
      player.SendConsoleCommand(
        "ddraw.sphere", duration, color, position, radius);

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
      if (null == player)
      {
        PrintDebug("CommandShow(): Got null player; aborting", DebugLevel.ERROR);
        return;
      }

      Print(player, Lang("Showing", player.UserIDString, configData.Chat.ShowDuration));

      foreach (var activeEvent in _activeDynamicZones)
      {
        var zoneData = GetZoneById(activeEvent.Key);
        if (null == zoneData) continue;
        var zonePosition = zoneData.transform.position;
        var baseZone = GetBaseEvent(activeEvent.Value)?.GetDynamicZone();
        Color zoneColor = Color.red;
        switch (baseZone)
        {
          case SphereCubeDynamicZone scdZone:
          {
            zoneColor = Color.yellow;
            if (scdZone.Radius > 0)
            {
              DrawSphere(
                player, configData.Chat.ShowDuration,zoneColor,
                zonePosition, scdZone.Radius);
            }
            else if (scdZone.Size.sqrMagnitude > 0)
            {
              var rotation = scdZone.Rotation;
              if (!scdZone.FixedRotation)
              {
                rotation += zoneData.transform.eulerAngles.y;
              }
              DrawCube(
                player, configData.Chat.ShowDuration, zoneColor,
                zonePosition, scdZone.Size, rotation);
            }
            break;
          }

          case CubeDynamicZone cdZone:
          {
            zoneColor = Color.cyan;
            if (cdZone.Size.sqrMagnitude > 0)
            {
              var rotation = cdZone.Rotation;
              if (!cdZone.FixedRotation)
              {
                rotation += zoneData.transform.eulerAngles.y;
              }
              DrawCube(
                player, configData.Chat.ShowDuration, zoneColor,
                zonePosition, cdZone.Size, rotation);
            }
            break;
          }

          case SphereDynamicZone sdZone:
          {
            zoneColor = Color.magenta;
            if (sdZone.Radius > 0)
            {
              DrawSphere(
                player, configData.Chat.ShowDuration, zoneColor,
                zonePosition, sdZone.Radius);
            }
            break;
          }
        }
        player.SendConsoleCommand(
          "ddraw.text", configData.Chat.ShowDuration, zoneColor,
          zonePosition, $"{activeEvent.Key}\n{activeEvent.Value}");
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
      public bool UseExcludePlayer { get; set; } = false;

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
      public ulong SteamIdIcon { get; set; } = 0;

      [JsonProperty(PropertyName = "Zone Show Duration (in seconds)")]
      public float ShowDuration { get; set; } = 15f;
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
      public MonumentEvent ExcavatorIgnition { get; set; } = new();

      [JsonProperty(PropertyName = "Cargo Ship Event")]
      public CargoShipEvent CargoShip { get; set; } = new();
    }

    #region Event

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
      public bool CommandWorksForPVPDelay { get; set; } = false;

      [JsonProperty(PropertyName = "Command List (If there is a '/' at the front, it is a chat command)", Order = 10)]
      public List<string> CommandList { get; set; } = new();

      public abstract BaseDynamicZone GetDynamicZone();
    }

    // NOTE: reserve order 20-29
    public class DomeMixedEvent : BaseEvent
    {
      [JsonProperty(PropertyName = "Enable Domes", Order = 20)]
      public bool DomesEnabled { get; set; } = true;

      [JsonProperty(PropertyName = "Domes Darkness", Order = 21)]
      public int DomesDarkness { get; set; } = 8;

      [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 22)]
      public SphereCubeDynamicZone DynamicZone { get; set; } = new();

      public override BaseDynamicZone GetDynamicZone()
      {
        return DynamicZone;
      }
    }

    // NOTE: reserve order 30-39
    public class BotDomeMixedEvent : DomeMixedEvent
    {
      [JsonProperty(PropertyName = "Enable Bots (Need BotSpawn Plugin)", Order = 30)]
      public bool BotsEnabled { get; set; }

      [JsonProperty(PropertyName = "BotSpawn Profile Name", Order = 31)]
      public string BotProfileName { get; set; } = string.Empty;
    }

    // NOTE: reserve order 40-49
    public class MonumentEvent : DomeMixedEvent
    {
      [JsonProperty(PropertyName = "Zone ID", Order = 40)]
      public string ZoneId { get; set; } = string.Empty;

      [JsonProperty(PropertyName = "Transform Position", Order = 41)]
      public Vector3 TransformPosition { get; set; }
    }

    // NOTE: reserve order 50-59
    public class AutoEvent : BotDomeMixedEvent
    {
      [JsonProperty(PropertyName = "Auto Start", Order = 50)]
      public bool AutoStart { get; set; }

      [JsonProperty(PropertyName = "Zone ID", Order = 51)]
      public string ZoneId { get; set; } = string.Empty;

      [JsonProperty(PropertyName = "Position", Order = 52)]
      public Vector3 Position { get; set; }
    }

    // NOTE: reserve order 60-69
    public class TimedEvent : BotDomeMixedEvent
    {
      [JsonProperty(PropertyName = "Event Duration", Order = 60)]
      public float Duration { get; set; } = 600f;
    }

    // NOTE: reserve order 70-79
    public class HackableCrateEvent : TimedEvent, ITimedDisable
    {
      [JsonProperty(PropertyName = "Start Event When Spawned (If false, the event starts when unlocking)", Order = 70)]
      public bool StartWhenSpawned { get; set; } = true;

      [JsonProperty(PropertyName = "Stop Event When Killed", Order = 71)]
      public bool StopWhenKilled { get; set; }

      [JsonProperty(PropertyName = "Event Timer Starts When Looted", Order = 72)]
      public bool TimerStartWhenLooted { get; set; }

      [JsonProperty(PropertyName = "Event Timer Starts When Unlocked", Order = 73)]
      public bool TimerStartWhenUnlocked { get; set; }

      [JsonProperty(PropertyName = "Excluding Hackable Crate On OilRig", Order = 74)]
      public bool ExcludeOilRig { get; set; } = true;

      [JsonProperty(PropertyName = "Excluding Hackable Crate on Cargo Ship", Order = 75)]
      public bool ExcludeCargoShip { get; set; } = true;

      public bool IsTimedDisabled()
      {
        return StopWhenKilled || TimerStartWhenLooted || TimerStartWhenUnlocked;
      }
    }

    // NOTE: reserve order 80-89
    public class SupplyDropEvent : TimedEvent, ITimedDisable
    {
      [JsonProperty(PropertyName = "Start Event When Spawned (If false, the event starts when landed)", Order = 24)]
      public bool StartWhenSpawned { get; set; } = true;

      [JsonProperty(PropertyName = "Stop Event When Killed", Order = 80)]
      public bool StopWhenKilled { get; set; }

      [JsonProperty(PropertyName = "Event Timer Starts When Looted", Order = 81)]
      public bool TimerStartWhenLooted { get; set; }

      public bool IsTimedDisabled()
      {
        return StopWhenKilled || TimerStartWhenLooted;
      }
    }

    // NOTE: reserve order 90-99
    public class CargoShipEvent : BaseEvent
    {
      [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 90)]
      public CubeParentDynamicZone DynamicZone { get; set; } = new()
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

    // NOTE: reserve order 100-119
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
        if (Math.Abs(Temperature) < 1e-8f)
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
          if (string.IsNullOrEmpty(flag))
          {
            continue;
          }
          zoneSettings.Add(flag);
          zoneSettings.Add("true");
        }
      }

      protected abstract string[] GetZoneSettings(Transform transform = null);
    }

    // NOTE: reserve order 120-129
    public class SphereDynamicZone : BaseDynamicZone, ISphereZone
    {
      [JsonProperty(PropertyName = "Zone Radius", Order = 120)]
      public float Radius { get; set; } = 100;

      protected override string[] GetZoneSettings(Transform transform = null)
      {
        var zoneSettings = new List<string>
        {
          "radius", Radius.ToString(CultureInfo.InvariantCulture)
        };
        GetBaseZoneSettings(zoneSettings);
        return zoneSettings.ToArray();
      }
    }

    // NOTE: reserve order 130-139
    public class CubeDynamicZone : BaseDynamicZone, ICubeZone
    {
      [JsonProperty(PropertyName = "Zone Size", Order = 130)]
      public Vector3 Size { get; set; }

      [JsonProperty(PropertyName = "Zone Rotation", Order = 131)]
      public float Rotation { get; set; }

      [JsonProperty(PropertyName = "Fixed Rotation", Order = 132)]
      public bool FixedRotation { get; set; }

      public override string[] ZoneSettings(Transform transform = null) =>
        transform == null || FixedRotation ?
          base.ZoneSettings(transform) : GetZoneSettings(transform);

      protected override string[] GetZoneSettings(Transform transform = null)
      {
        var transformedRotation = Rotation;
        if (transform != null && !FixedRotation)
        {
          transformedRotation += transform.rotation.eulerAngles.y;
        }

        var zoneSettings = new List<string>
        {
          "size", $"{Size.x} {Size.y} {Size.z}",
          "rotation", transformedRotation.ToString(CultureInfo.InvariantCulture)
        };

        GetBaseZoneSettings(zoneSettings);
        return zoneSettings.ToArray();
      }
    }

    // NOTE: reserve order 140-149
    public class SphereCubeDynamicZone : BaseDynamicZone, ICubeZone, ISphereZone
    {
      [JsonProperty(PropertyName = "Zone Radius", Order = 140)]
      public float Radius { get; set; }

      [JsonProperty(PropertyName = "Zone Size", Order = 141)]
      public Vector3 Size { get; set; }

      [JsonProperty(PropertyName = "Zone Rotation", Order = 142)]
      public float Rotation { get; set; }

      [JsonProperty(PropertyName = "Fixed Rotation", Order = 143)]
      public bool FixedRotation { get; set; }

      public override string[] ZoneSettings(Transform transform = null) =>
        transform == null || FixedRotation || Radius > 0f ?
          base.ZoneSettings(transform) : GetZoneSettings(transform);

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

          zoneSettings.Add("rotation");
          var transformedRotation = Rotation;
          if (transform != null && !FixedRotation)
          {
            transformedRotation += transform.rotation.eulerAngles.y;
          }
          zoneSettings.Add(transformedRotation.ToString(CultureInfo.InvariantCulture));
        }
        GetBaseZoneSettings(zoneSettings);
        return zoneSettings.ToArray();
      }
    }

    // NOTE: reserve order 150-159
    public class SphereParentDynamicZone : SphereDynamicZone, IParentZone
    {
      [JsonProperty(PropertyName = "Transform Position", Order = 150)]
      public Vector3 Center { get; set; }
    }

    // NOTE: reserve order 160-169
    public class CubeParentDynamicZone : CubeDynamicZone, IParentZone
    {
      [JsonProperty(PropertyName = "Transform Position", Order = 160)]
      public Vector3 Center { get; set; }

      protected override string[] GetZoneSettings(Transform transform = null)
      {
        var zoneSettings = new List<string>
        {
          "size",
          $"{Size.x} {Size.y} {Size.z}"
        };
        GetBaseZoneSettings(zoneSettings);
        var array = zoneSettings.ToArray();
        return array;
      }
    }

    #region Interface

    public interface ISphereZone
    {
      float Radius { get; set; }
    }

    public interface ICubeZone
    {
      Vector3 Size { get; set; }

      float Rotation { get; set; }

      bool FixedRotation { get; set; }
    }

    public interface IParentZone
    {
      Vector3 Center { get; set; }
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
        ["Showing"] = "Showing active zones for {0} second(s)",

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
        ["Showing"] = "显示活动区域 {0} 秒",

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

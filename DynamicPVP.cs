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
    [Info("Dynamic PVP", "CatMeat/Arainrr", "4.2.17", ResourceId = 2728)]
    [Description("Creates temporary PvP zones on certain actions/events")]
    public class DynamicPVP : RustPlugin
    {
        #region Fields

        [PluginReference]
        private readonly Plugin ZoneManager, BotReSpawn;

        private const string PermissionAdmin = "dynamicpvp.admin";
        private const string PrefabSphere = "assets/prefabs/visualization/sphere.prefab";
        private const string ZoneName = "DynamicPVP";
        private static object _true, _false;

        private readonly Dictionary<string, Timer> _eventTimers = new Dictionary<string, Timer>();
        private readonly Dictionary<ulong, LeftZone> _pvpDelays = new Dictionary<ulong, LeftZone>();
        private readonly Dictionary<string, string> _activeDynamicZones = new Dictionary<string, string>(); //ID -> EventName

        private bool _dataChanged;
        private Vector3 _oilRigPosition;
        private Vector3 _largeOilRigPosition;
        private Coroutine _createEventsCoroutine;

        public static DynamicPVP Instance { get; private set; }

        private class LeftZone : Pool.IPooled
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
        private enum PVPDelayFlags
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

        #endregion Fields

        #region Oxide Hooks

        private void Init()
        {
            Instance = this;
            LoadData();
            _true = true;
            _false = false;
            permission.RegisterPermission(PermissionAdmin, this);
            AddCovalenceCommand(configData.Chat.Command, nameof(CmdDynamicPVP));
            Unsubscribe(nameof(OnEntitySpawned));
            Unsubscribe(nameof(OnCargoPlaneSignaled));
            Unsubscribe(nameof(OnCrateHack));
            Unsubscribe(nameof(OnDieselEngineToggled));
            Unsubscribe(nameof(OnEntityDeath));
            Unsubscribe(nameof(OnLootEntity));
            Unsubscribe(nameof(OnCrateHackEnd));
            Unsubscribe(nameof(OnSupplyDropLanded));
            Unsubscribe(nameof(OnEntityKill));
            Unsubscribe(nameof(OnPlayerCommand));
            Unsubscribe(nameof(OnServerCommand));
            Unsubscribe(nameof(CanEntityTakeDamage));
            Unsubscribe(nameof(OnEnterZone));
            Unsubscribe(nameof(OnExitZone));
            if (configData.Global.LogToFile)
            {
                _debugStringBuilder = new StringBuilder();
            }
        }

        private void OnServerInitialized()
        {
            //Pool.FillBuffer<LeftZone>();
            DeleteOldDynamicZones();
            _createEventsCoroutine = ServerMgr.Instance.StartCoroutine(CreateMonumentEvents());
            if (configData.GeneralEvents.ExcavatorIgnition.Enabled)
            {
                Subscribe(nameof(OnDieselEngineToggled));
            }
            if (configData.GeneralEvents.PatrolHelicopter.Enabled || configData.GeneralEvents.BradleyApc.Enabled)
            {
                Subscribe(nameof(OnEntityDeath));
            }
            if (configData.GeneralEvents.SupplySignal.Enabled || configData.GeneralEvents.TimedSupply.Enabled)
            {
                Subscribe(nameof(OnCargoPlaneSignaled));
            }
            if (configData.GeneralEvents.HackableCrate.Enabled && configData.GeneralEvents.HackableCrate.TimerStartWhenUnlocked)
            {
                Subscribe(nameof(OnCrateHackEnd));
            }
            if (configData.GeneralEvents.TimedSupply.Enabled && configData.GeneralEvents.TimedSupply.TimerStartWhenLooted ||
                    configData.GeneralEvents.SupplySignal.Enabled && configData.GeneralEvents.SupplySignal.TimerStartWhenLooted ||
                    configData.GeneralEvents.HackableCrate.Enabled && configData.GeneralEvents.HackableCrate.TimerStartWhenLooted)
            {
                Subscribe(nameof(OnLootEntity));
            }
            if (configData.GeneralEvents.HackableCrate.Enabled && !configData.GeneralEvents.HackableCrate.StartWhenSpawned)
            {
                Subscribe(nameof(OnCrateHack));
            }
            if (configData.GeneralEvents.TimedSupply.Enabled && !configData.GeneralEvents.TimedSupply.StartWhenSpawned ||
                    configData.GeneralEvents.SupplySignal.Enabled && !configData.GeneralEvents.SupplySignal.StartWhenSpawned)
            {
                Subscribe(nameof(OnSupplyDropLanded));
            }
            if (configData.GeneralEvents.TimedSupply.Enabled && configData.GeneralEvents.TimedSupply.StartWhenSpawned ||
                    configData.GeneralEvents.SupplySignal.Enabled && configData.GeneralEvents.SupplySignal.StartWhenSpawned ||
                    configData.GeneralEvents.HackableCrate.Enabled && configData.GeneralEvents.HackableCrate.StartWhenSpawned)
            {
                Subscribe(nameof(OnEntitySpawned));
            }
            if (configData.GeneralEvents.TimedSupply.Enabled && configData.GeneralEvents.TimedSupply.StopWhenKilled ||
                    configData.GeneralEvents.SupplySignal.Enabled && configData.GeneralEvents.SupplySignal.StopWhenKilled ||
                    configData.GeneralEvents.HackableCrate.Enabled && configData.GeneralEvents.HackableCrate.StopWhenKilled)
            {
                Subscribe(nameof(OnEntityKill));
            }
            if (configData.GeneralEvents.CargoShip.Enabled)
            {
                Subscribe(nameof(OnEntityKill));
                Subscribe(nameof(OnEntitySpawned));
                foreach (var serverEntity in BaseNetworkable.serverEntities)
                {
                    var cargoShip = serverEntity as CargoShip;
                    if (cargoShip == null)
                    {
                        continue;
                    }
                    OnEntitySpawned(cargoShip);
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
                PrintDebug($"Deleting {_activeDynamicZones.Count} active zones.", true);
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
                Pool.FreeList(ref sphereEntities);
            }

            SaveData();
            SaveDebug();
            Pool.IPoolCollection removedCollection;
            Pool.Directory.TryRemove(typeof(LeftZone), out removedCollection);
            _true = _false = null;
            Instance = null;
        }

        private void OnServerSave()
        {
            timer.Once(Random.Range(0f, 60f), () =>
            {
                SaveDebug();
                if (_dataChanged)
                {
                    SaveData();
                    _dataChanged = false;
                }
            });
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId())
            {
                return;
            }
            TryRemovePVPDelay(player.userID, player.displayName);
        }

        #endregion Oxide Hooks

        #region Methods

        private void TryRemoveEventTimer(string zoneId)
        {
            Timer value;
            if (_eventTimers.TryGetValue(zoneId, out value))
            {
                value?.Destroy();
                _eventTimers.Remove(zoneId);
            }
        }

        private LeftZone GetOrAddPVPDelay(BasePlayer player, string zoneId, string eventName)
        {
            PrintDebug($"Adding {player.displayName} to pvp delay.");
            var added = false;
            LeftZone leftZone;
            if (_pvpDelays.TryGetValue(player.userID, out leftZone))
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
                CheckHooks(true);
            }
            return leftZone;
        }

        private void TryRemovePVPDelay(ulong playerId, string playerName)
        {
            PrintDebug($"Removing {playerName} from pvp delay.");
            LeftZone leftZone;
            if (_pvpDelays.TryGetValue(playerId, out leftZone))
            {
                _pvpDelays.Remove(playerId);
                Interface.CallHook("OnPlayerRemovedFromPVPDelay", playerId, leftZone.zoneId);
                Pool.Free(ref leftZone);
                CheckHooks(true);
            }
        }

        private bool CheckEntityOwner(BaseEntity baseEntity)
        {
            if (configData.Global.CheckEntityOwner && baseEntity.OwnerID.IsSteamId())
            {
                if (baseEntity.skinID == 0) // HeliSignals and BradleyDrops exception
                {
                    PrintDebug($"{baseEntity} is owned by the player({baseEntity.OwnerID}). Skipping event creation.");
                    return false;
                }
            }
            return true;
        }

        private bool CanCreateDynamicPVP(string eventName, BaseEntity entity)
        {
            if (Interface.CallHook("OnCreateDynamicPVP", eventName, entity) != null)
            {
                PrintDebug($"There are other plugins that prevent {eventName} events from being created.", true);
                return false;
            }
            return true;
        }

        private void CheckHooks(bool pvpDelay = false)
        {
            if (pvpDelay)
            {
                if (_pvpDelays.Count > 0)
                {
                    Subscribe(nameof(CanEntityTakeDamage));
                }
                else
                {
                    Unsubscribe(nameof(CanEntityTakeDamage));
                }
            }
            else
            {
                if (_activeDynamicZones.Count > 0)
                {
                    Subscribe(nameof(OnEnterZone));
                    Subscribe(nameof(OnExitZone));
                }
                else
                {
                    Unsubscribe(nameof(OnEnterZone));
                    Unsubscribe(nameof(OnExitZone));
                }
            }

            var hasCommands = false;
            foreach (var eventName in _activeDynamicZones.Values)
            {
                var baseEvent = GetBaseEvent(eventName);
                if (baseEvent != null && baseEvent.CommandList.Count > 0)
                {
                    hasCommands = true;
                    break;
                }
            }
            if (!hasCommands)
            {
                foreach (var leftZone in _pvpDelays.Values)
                {
                    var baseEvent = GetBaseEvent(leftZone.eventName);
                    if (baseEvent != null && baseEvent.CommandList.Count > 0 && baseEvent.CommandWorksForPVPDelay)
                    {
                        hasCommands = true;
                        break;
                    }
                }
            }
            if (hasCommands)
            {
                Subscribe(nameof(OnPlayerCommand));
                Subscribe(nameof(OnServerCommand));
            }
            else
            {
                Unsubscribe(nameof(OnPlayerCommand));
                Unsubscribe(nameof(OnServerCommand));
            }
        }

        private BaseEvent GetBaseEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName))
            {
                throw new ArgumentNullException(nameof(eventName));
            }
            var externalEvent = Interface.CallHook("OnGetBaseEvent", eventName) as BaseEvent;
            if (externalEvent != null)
            {
                return externalEvent;
            }
            if (Enum.IsDefined(typeof(GeneralEventType), eventName))
            {
                GeneralEventType generalEventType;
                if (Enum.TryParse(eventName, true, out generalEventType))
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
                            PrintDebug($"ERROR: Unknown GeneralEventType: {generalEventType} | {eventName}.", error: true);
                            break;
                    }
                }
            }
            AutoEvent autoEvent;
            if (storedData.autoEvents.TryGetValue(eventName, out autoEvent))
            {
                return autoEvent;
            }
            TimedEvent timedEvent;
            if (storedData.timedEvents.TryGetValue(eventName, out timedEvent))
            {
                return timedEvent;
            }
            MonumentEvent monumentEvent;
            if (configData.MonumentEvents.TryGetValue(eventName, out monumentEvent))
            {
                return monumentEvent;
            }
            PrintDebug($"ERROR: Failed to get base event settings for {eventName}.", error: true);
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
                HandleGeneralEvent(GeneralEventType.ExcavatorIgnition, dieselEngine, true);
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

            PrintDebug("Trying to create the event when the hackable locked crate is spawned.");
            NextTick(() => LockedCrateEvent(hackableLockedCrate));
        }

        private void OnCrateHack(HackableLockedCrate hackableLockedCrate)
        {
            if (hackableLockedCrate == null || hackableLockedCrate.net == null)
            {
                return;
            }
            PrintDebug("Trying to create the event when the hackable locked crate is starting hack.");
            NextTick(() => LockedCrateEvent(hackableLockedCrate));
        }

        private void OnCrateHackEnd(HackableLockedCrate hackableLockedCrate)
        {
            if (hackableLockedCrate == null || hackableLockedCrate.net == null)
            {
                return;
            }
            HandleDeleteDynamicZone(hackableLockedCrate.net.ID.ToString(), configData.GeneralEvents.HackableCrate.Duration, GeneralEventType.HackableCrate.ToString());
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
            HandleDeleteDynamicZone(hackableLockedCrate.net.ID.ToString(), configData.GeneralEvents.HackableCrate.Duration, GeneralEventType.HackableCrate.ToString());
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
                HandleDeleteDynamicZone(hackableLockedCrate.net.ID.ToString());
            }
        }

        private void LockedCrateEvent(HackableLockedCrate hackableLockedCrate)
        {
            if (!CheckEntityOwner(hackableLockedCrate))
            {
                return;
            }
            if (configData.GeneralEvents.HackableCrate.ExcludeOilRig && IsOnTheOilRig(hackableLockedCrate))
            {
                PrintDebug("The hackable locked crate is on the oil rig. Skipping event creation.");
                return;
            }
            if (configData.GeneralEvents.HackableCrate.ExcludeCargoShip && IsOnTheCargoShip(hackableLockedCrate))
            {
                PrintDebug("The hackable locked crate is on the cargo ship. Skipping event creation.");
                return;
            }
            HandleGeneralEvent(GeneralEventType.HackableCrate, hackableLockedCrate, true);
        }

        private bool IsOnTheCargoShip(HackableLockedCrate hackableLockedCrate)
        {
            return hackableLockedCrate.GetComponentInParent<CargoShip>() != null;
        }

        private bool IsOnTheOilRig(HackableLockedCrate hackableLockedCrate)
        {
            if (_oilRigPosition != Vector3.zero &&
                    Vector3Ex.Distance2D(hackableLockedCrate.transform.position, _oilRigPosition) < 50f)
            {
                return true;
            }

            if (_largeOilRigPosition != Vector3.zero &&
                    Vector3Ex.Distance2D(hackableLockedCrate.transform.position, _largeOilRigPosition) < 50f)
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
            PrintDebug("Trying to create the event when the helicopter is dead.");
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
            PrintDebug("Trying to create the event when the bradley apc is dead.");
            if (!CheckEntityOwner(bradleyAPC))
            {
                return;
            }
            HandleGeneralEvent(GeneralEventType.Bradley, bradleyAPC, false);
        }

        #endregion PatrolHelicopter And BradleyAPC Event

        #region SupplyDrop And SupplySignal Event

        private readonly Dictionary<Vector3, Timer> activeSupplySignals = new Dictionary<Vector3, Timer>();

        private void OnCargoPlaneSignaled(CargoPlane cargoPlane, SupplySignal supplySignal)
        {
            NextTick(() =>
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
                activeSupplySignals.Add(dropPosition, timer.Once(900f, () => activeSupplySignals.Remove(dropPosition)));
                PrintDebug($"A supply signal is thrown at {dropPosition}");
            });
        }

        private void OnEntitySpawned(SupplyDrop supplyDrop)
        {
            NextTick(() => OnSupplyDropEvent(supplyDrop, false));
        }

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

        private void OnLootEntity(BasePlayer player, SupplyDrop supplyDrop)
        {
            if (supplyDrop == null || supplyDrop.net == null)
            {
                return;
            }
            var zoneId = supplyDrop.net.ID.ToString();
            string eventName;
            if (_activeDynamicZones.TryGetValue(zoneId, out eventName))
            {
                switch (eventName)
                {
                    case nameof(GeneralEventType.SupplySignal):
                        if (!configData.GeneralEvents.SupplySignal.Enabled || !configData.GeneralEvents.SupplySignal.TimerStartWhenLooted)
                        {
                            return;
                        }
                        HandleDeleteDynamicZone(zoneId, configData.GeneralEvents.SupplySignal.Duration, eventName);
                        break;

                    case nameof(GeneralEventType.SupplyDrop):
                        if (!configData.GeneralEvents.TimedSupply.Enabled || !configData.GeneralEvents.TimedSupply.TimerStartWhenLooted)
                        {
                            return;
                        }
                        HandleDeleteDynamicZone(zoneId, configData.GeneralEvents.TimedSupply.Duration, eventName);
                        break;

                    default:
                        return;
                }
            }
        }

        private void OnEntityKill(SupplyDrop supplyDrop)
        {
            if (supplyDrop == null || supplyDrop.net == null)
            {
                return;
            }
            var zoneId = supplyDrop.net.ID.ToString();
            string eventName;
            if (_activeDynamicZones.TryGetValue(zoneId, out eventName))
            {
                switch (eventName)
                {
                    case nameof(GeneralEventType.SupplySignal):
                        if (!configData.GeneralEvents.SupplySignal.Enabled || !configData.GeneralEvents.SupplySignal.StopWhenKilled)
                        {
                            return;
                        }
                        break;

                    case nameof(GeneralEventType.SupplyDrop):
                        if (!configData.GeneralEvents.TimedSupply.Enabled || !configData.GeneralEvents.TimedSupply.StopWhenKilled)
                        {
                            return;
                        }
                        break;

                    default:
                        return;
                }
                //When the timer starts, don't stop the event immediately
                if (!_eventTimers.ContainsKey(zoneId))
                {
                    HandleDeleteDynamicZone(zoneId);
                }
            }
        }

        private void OnSupplyDropEvent(SupplyDrop supplyDrop, bool isLanded)
        {
            if (supplyDrop == null || supplyDrop.net == null)
            {
                return;
            }
            PrintDebug($"Trying to create the event when the supply drop is {(isLanded ? "landed" : "spawned")} at {supplyDrop.transform.position}.");
            if (!CheckEntityOwner(supplyDrop))
            {
                return;
            }

            Action action = null;
            var isFromSupplySignal = IsProbablySupplySignal(supplyDrop.transform.position, ref action);
            PrintDebug($"Supply drop is from supply signal: {isFromSupplySignal}");
            if (isFromSupplySignal)
            {
                if (!configData.GeneralEvents.SupplySignal.Enabled)
                {
                    PrintDebug("Event for supply signals disabled. Skipping event creation.");
                    return;
                }

                if (isLanded ? configData.GeneralEvents.SupplySignal.StartWhenSpawned : !configData.GeneralEvents.SupplySignal.StartWhenSpawned)
                {
                    PrintDebug($"{(isLanded ? "Landed" : "Spawned")} for supply signals disabled.");
                    return;
                }
                action?.Invoke();
                HandleGeneralEvent(GeneralEventType.SupplySignal, supplyDrop, true);
            }
            else
            {
                if (!configData.GeneralEvents.TimedSupply.Enabled)
                {
                    PrintDebug("Event for timed supply disabled. Skipping event creation.");
                    return;
                }
                if (isLanded ? configData.GeneralEvents.TimedSupply.StartWhenSpawned : !configData.GeneralEvents.TimedSupply.StartWhenSpawned)
                {
                    PrintDebug($"{(isLanded ? "Landed" : "Spawned")} for timed supply disabled.");
                    return;
                }

                HandleGeneralEvent(GeneralEventType.SupplyDrop, supplyDrop, true);
            }
        }

        private bool IsProbablySupplySignal(Vector3 position, ref Action action)
        {
            PrintDebug($"Checking {activeSupplySignals.Count} active supply signals");
            if (activeSupplySignals.Count > 0)
            {
                foreach (var entry in activeSupplySignals)
                {
                    var distance = Vector3Ex.Distance2D(entry.Key, position);
                    PrintDebug($"Found a supply signal at {entry.Key} located {distance}m away.");
                    if (distance <= configData.Global.CompareRadius)
                    {
                        action = () =>
                        {
                            entry.Value?.Destroy();
                            activeSupplySignals.Remove(entry.Key);
                            PrintDebug($"Removing Supply signal from active list. Active supply signals remaining: {activeSupplySignals.Count}");
                        };
                        PrintDebug("Found matching a supply signal.");
                        return true;
                    }
                }
                PrintDebug("No matches found, probably from a timed event cargo plane");
                return false;
            }
            PrintDebug("No active signals, must be from a timed event cargo plane");
            return false;
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
            PrintDebug("Trying to create the event when the cargo ship is spawned.");
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
                var monumentName = landmarkInfo.displayPhrase.english.Replace("\n", "");
                if (string.IsNullOrEmpty(monumentName))
                {
                    continue;
                }
                switch (landmarkInfo.name)
                {
                    case "OilrigAI":
                        _oilRigPosition = landmarkInfo.transform.position;
                        break;
                    case "OilrigAI2":
                        _largeOilRigPosition = landmarkInfo.transform.position;
                        break;
                    case "assets/bundled/prefabs/autospawn/monument/harbor/harbor_1.prefab":
                        monumentName += " A";
                        break;
                    case "assets/bundled/prefabs/autospawn/monument/harbor/harbor_2.prefab":
                        monumentName += " B";
                        break;
                    //case "assets/bundled/prefabs/autospawn/monument/harbor/fishing_village_b.prefab": monumentName += " A"; break;
                    //case "assets/bundled/prefabs/autospawn/monument/harbor/fishing_village_c.prefab": monumentName += " B"; break;
                }
                MonumentEvent monumentEvent;
                if (!configData.MonumentEvents.TryGetValue(monumentName, out monumentEvent))
                {
                    changed = true;
                    monumentEvent = new MonumentEvent();
                    configData.MonumentEvents.Add(monumentName, monumentEvent);
                    Puts($"A new monument {monumentName} was found and added to the config.");
                }
                if (monumentEvent.Enabled)
                {
                    if (HandleMonumentEvent(monumentName, landmarkInfo.transform, monumentEvent))
                    {
                        createdEvents.Add(monumentName);
                    }

                    yield return CoroutineEx.waitForSeconds(0.5f);
                }
            }
            if (changed)
            {
                SaveConfig();
            }
            if (createdEvents.Count > 0)
            {
                PrintDebug($"{createdEvents.Count}({string.Join(", ", createdEvents)}) monument events were successfully created.", true);
            }

            createdEvents.Clear();
            foreach (var entry in storedData.autoEvents)
            {
                if (entry.Value.AutoStart)
                {
                    if (CreateDynamicZone(entry.Key, entry.Value.Position, entry.Value.ZoneId))
                    {
                        createdEvents.Add(entry.Key);
                    }
                    yield return CoroutineEx.waitForSeconds(0.5f);
                }
            }
            if (createdEvents.Count > 0)
            {
                PrintDebug($"{createdEvents.Count}({string.Join(", ", createdEvents)}) auto events were successfully created.", true);
            }
            _createEventsCoroutine = null;
        }

        #endregion Monument Event

        #region Chat/Console Command Handler

        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            return CheckCommand(player, command, true);
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            return CheckCommand(arg?.Player(), arg?.cmd?.FullName, false);
        }

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

            LeftZone leftZone;
            if (_pvpDelays.TryGetValue(player.userID, out leftZone))
            {
                var baseEvent = GetBaseEvent(leftZone.eventName);
                if (baseEvent != null && baseEvent.CommandWorksForPVPDelay)
                {
                    if (IsBlockedCommand(baseEvent, command, isChat))
                    {
                        return _false;
                    }
                }
            }

            var result = GetPlayerZoneIds(player);
            if (result == null || result.Length == 0 || result.Length == 1 && string.IsNullOrEmpty(result[0]))
            {
                return null;
            }

            foreach (var zoneId in result)
            {
                string eventName;
                if (_activeDynamicZones.TryGetValue(zoneId, out eventName))
                {
                    PrintDebug($"Checking command: {command} , zoneId: {zoneId}");
                    var baseEvent = GetBaseEvent(eventName);
                    if (baseEvent != null)
                    {
                        if (IsBlockedCommand(baseEvent, command, isChat))
                        {
                            return _false;
                        }
                    }
                }
            }
            return null;
        }

        private bool IsBlockedCommand(BaseEvent baseEvent, string command, bool isChat)
        {
            if (baseEvent != null && baseEvent.CommandList.Count > 0)
            {
                var commandExist = baseEvent.CommandList.Any(entry =>
                                                                     isChat
                                                                             ? entry.StartsWith("/") && entry.Substring(1).Equals(command)
                                                                             : !entry.StartsWith("/") && command.Contains(entry));
                if (baseEvent.UseBlacklistCommands)
                {
                    if (commandExist)
                    {
                        PrintDebug($"Use blacklist, Blocked command: {command}", true);
                        return true;
                    }
                }
                else
                {
                    if (!commandExist)
                    {
                        PrintDebug($"Use whitelist, Blocked command: {command}", true);
                        return true;
                    }
                }
            }
            return false;
        }

        #endregion Chat/Console Command Handler

        #endregion Events

        #region DynamicZone Handler

        private void HandleParentedEntityEvent(string eventName, BaseEntity parentEntity, bool delay = true)
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
                timer.Once(baseEvent.EventStartDelay, () => HandleParentedEntityEvent(eventName, parentEntity, false));
                return;
            }
            PrintDebug($"Trying to create parented entity event {eventName} on entity({parentEntity}).");
            var zoneId = parentEntity.net.ID.ToString();
            var parentZone = baseEvent.GetDynamicZone() as IParentZone;
            if (CreateDynamicZone(eventName, parentEntity.transform.position, zoneId, delay: false))
            {
                timer.Once(0.25f, () =>
                {
                    var zone = GetZoneById(zoneId);
                    PrintDebug($"Gets the zone({zoneId} | {zone}) created by the {eventName} event.", true);
                    if (parentEntity != null && zone != null)
                    {
                        var zoneTransform = zone.transform;
                        zoneTransform.SetParent(parentEntity.transform);
                        zoneTransform.rotation = parentEntity.transform.rotation;
                        zoneTransform.position = parentZone != null ? parentEntity.transform.TransformPoint(parentZone.Center) : parentEntity.transform.position;
                        PrintDebug($"The zone({zoneId} | {eventName}) was parented to entity({parentEntity}).", true);
                    }
                    else
                    {
                        PrintDebug($"ERROR: The zone({zoneId} | {zone} | {parentEntity}) created by the {eventName} event is empty.", error: true);
                        DeleteDynamicZone(zoneId);
                    }
                });
            }
        }

        private bool HandleMonumentEvent(string eventName, Transform transform, MonumentEvent monumentEvent)
        {
            var position = monumentEvent.TransformPosition != Vector3.zero ? transform.TransformPoint(monumentEvent.TransformPosition) : transform.position;
            return CreateDynamicZone(eventName, position, monumentEvent.ZoneId, monumentEvent.GetDynamicZone().ZoneSettings(transform));
        }

        private bool HandleGeneralEvent(GeneralEventType generalEventType, BaseEntity baseEntity, bool useEntityId)
        {
            var eventName = generalEventType.ToString();
            if (useEntityId && _activeDynamicZones.ContainsKey(baseEntity.net.ID.ToString()))
            {
                PrintDebug($"The event({eventName} | {baseEntity.net.ID}) created by entity({baseEntity}) already exists");
                return false;
            }
            if (!CanCreateDynamicPVP(eventName, baseEntity))
            {
                return false;
            }
            var baseEvent = GetBaseEvent(eventName);
            if (baseEvent == null)
            {
                return false;
            }
            var position = baseEntity.transform.position;
            position.y = TerrainMeta.HeightMap.GetHeight(position);
            return CreateDynamicZone(eventName, position, useEntityId ? baseEntity.net.ID.ToString() : null, baseEvent.GetDynamicZone().ZoneSettings(baseEntity.transform));
        }

        private bool CreateDynamicZone(string eventName, Vector3 position, string zoneId = "", string[] zoneSettings = null, bool delay = true)
        {
            if (position == Vector3.zero)
            {
                PrintDebug($"ERROR: Invalid location, zone({eventName}) creation failed. Skipping event creation.", error: true);
                return false;
            }
            var baseEvent = GetBaseEvent(eventName);
            if (baseEvent == null)
            {
                return false;
            }
            if (delay && baseEvent.EventStartDelay > 0f)
            {
                timer.Once(baseEvent.EventStartDelay, () => CreateDynamicZone(eventName, position, zoneId, zoneSettings, false));
                return false;
            }

            float duration = -1;
            var timedEvent = baseEvent as ITimedEvent;
            if (timedEvent != null)
            {
                var timedDisable = baseEvent as ITimedDisable;
                if (timedDisable == null || !timedDisable.IsTimedDisabled())
                {
                    duration = timedEvent.Duration;
                }
            }

            if (string.IsNullOrEmpty(zoneId))
            {
                zoneId = DateTime.Now.ToString("HHmmssffff");
            }

            var dynamicZone = baseEvent.GetDynamicZone();
            zoneSettings = zoneSettings ?? dynamicZone.ZoneSettings();

            PrintDebug($"Trying create zone: {eventName}({zoneId} | {position}) {(dynamicZone is ISphereZone ? $"(Radius: {(dynamicZone as ISphereZone).Radius}m)" : $"(Size: {(dynamicZone as ICubeZone)?.Size})")} {(dynamicZone is IParentZone ? $"(Center: {(dynamicZone as IParentZone).Center}) " : null)}(Duration: {duration}s).");
            var zoneAdded = CreateZone(zoneId, zoneSettings, position);
            if (zoneAdded)
            {
                if (!_activeDynamicZones.ContainsKey(zoneId))
                {
                    _activeDynamicZones.Add(zoneId, eventName);
                    CheckHooks();
                }

                var stringBuilder = Pool.Get<StringBuilder>();
                stringBuilder.Clear();
                var domeEvent = baseEvent as IDomeEvent;
                var sphereZone = dynamicZone as ISphereZone;
                if (DomeCreateAllowed(domeEvent, sphereZone))
                {
                    var domeAdded = CreateDome(zoneId, position, sphereZone.Radius, domeEvent.DomesDarkness);
                    if (!domeAdded)
                    {
                        PrintDebug($"ERROR: Dome NOT added for zone({zoneId}).", error: true);
                    }
                    else
                    {
                        stringBuilder.Append("Dome,");
                    }
                }

                var botEvent = baseEvent as IBotEvent;
                if (BotReSpawnAllowed(botEvent))
                {
                    var botsSpawned = SpawnBots(position, botEvent.BotProfileName, zoneId);
                    if (!botsSpawned)
                    {
                        PrintDebug($"ERROR: Bot NOT spawned for zone({zoneId}).", error: true);
                    }
                    else
                    {
                        stringBuilder.Append("Bots,");
                    }
                }

                var mappingAdded = TryCreateMapping(zoneId, baseEvent.Mapping);
                if (!mappingAdded)
                {
                    PrintDebug($"ERROR: Mapping Not added for zone({zoneId}).", error: true);
                }
                else
                {
                    stringBuilder.Append("Mapping,");
                }

                PrintDebug($"Created zone({zoneId} | {eventName}) ({stringBuilder.ToString().TrimEnd(',')}).", true);
                HandleDeleteDynamicZone(zoneId, duration, eventName);

                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                return true;
            }

            PrintDebug($"ERROR: Create zone({eventName}) failed.", error: true);
            return false;
        }

        private void HandleDeleteDynamicZone(string zoneId, float duration, string eventName)
        {
            if (duration > 0f)
            {
                TryRemoveEventTimer(zoneId);
                PrintDebug($"The zone({zoneId} | {eventName}) will be deleted after {duration} seconds.", true);
                _eventTimers.Add(zoneId, timer.Once(duration, () => HandleDeleteDynamicZone(zoneId)));
            }
        }

        private void HandleDeleteDynamicZone(string zoneId)
        {
            string eventName;
            if (string.IsNullOrEmpty(zoneId) || !_activeDynamicZones.TryGetValue(zoneId, out eventName))
            {
                PrintDebug($"ERROR: Invalid zoneID: {zoneId}.", error: true);
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
                    var zone = GetZoneById(zoneId);
                    if (zone != null)
                    {
                        zone.transform.SetParent(null, true);
                    }
                }
                _eventTimers.Add(zoneId, timer.Once(baseEvent.EventStopDelay, () => DeleteDynamicZone(zoneId)));
            }
            else
            {
                DeleteDynamicZone(zoneId);
            }
        }

        private bool DeleteDynamicZone(string zoneId)
        {
            string eventName;
            if (string.IsNullOrEmpty(zoneId) || !_activeDynamicZones.TryGetValue(zoneId, out eventName))
            {
                PrintDebug($"ERROR: Invalid zoneID: {zoneId}.", error: true);
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
            var dynamicZone = baseEvent.GetDynamicZone();
            if (DomeCreateAllowed(baseEvent as IDomeEvent, dynamicZone as ISphereZone))
            {
                var domeRemoved = RemoveDome(zoneId);
                if (!domeRemoved)
                {
                    PrintDebug($"ERROR: Dome NOT removed for zone({zoneId} | {eventName}).", error: true);
                }
                else
                {
                    stringBuilder.Append("Dome,");
                }
            }

            if (BotReSpawnAllowed(baseEvent as IBotEvent))
            {
                var botsRemoved = KillBots(zoneId);
                if (!botsRemoved)
                {
                    PrintDebug($"ERROR: Bot NOT killed for zone({zoneId} | {eventName}).", error: true);
                }
                else
                {
                    stringBuilder.Append("Bots,");
                }
            }

            var mappingRemoved = TryRemoveMapping(zoneId);
            if (!mappingRemoved)
            {
                PrintDebug($"ERROR: Mapping NOT removed for zone({zoneId} | {eventName}).", error: true);
            }
            else
            {
                stringBuilder.Append("Mapping,");
            }

            var players = GetPlayersInZone(zoneId);
            var zoneRemoved = RemoveZone(zoneId, eventName);
            if (zoneRemoved)
            {
                // Release zone players immediately
                PrintDebug($"Release zone players : {string.Join(",", players.Select(x => x.displayName))}");
                foreach (var player in players)
                {
                    OnExitZone(zoneId, player);
                }
                if (_activeDynamicZones.Remove(zoneId))
                {
                    CheckHooks();
                }
                PrintDebug($"Deleted zone({zoneId} | {eventName}) ({stringBuilder.ToString().TrimEnd(',')}).", true);
            }
            else
            {
                PrintDebug($"ERROR: Delete zone({zoneId} | {eventName} | {stringBuilder.ToString().TrimEnd(',')}) failed.", error: true);
            }

            stringBuilder.Clear();
            Pool.Free(ref stringBuilder);
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
                var zoneName = GetZoneName(zoneId);
                if (zoneName == ZoneName)
                {
                    attempts++;
                    var zoneRemoved = RemoveZone(zoneId);
                    if (zoneRemoved)
                    {
                        successes++;
                    }
                    TryRemoveMapping(zoneId);
                }
            }
            PrintDebug($"Deleted {successes} of {attempts} existing DynamicPVP zones", true);
        }

        #endregion DynamicZone Handler

        #region ZoneDome Integration

        private readonly Dictionary<string, List<SphereEntity>> _zoneSpheres = new Dictionary<string, List<SphereEntity>>();

        private static bool DomeCreateAllowed(IDomeEvent domeEvent, ISphereZone sphereZone)
        {
            return domeEvent != null && domeEvent.DomesEnabled && sphereZone?.Radius > 0f;
        }

        private bool CreateDome(string zoneId, Vector3 position, float radius, int darkness)
        {
            if (radius <= 0)
            {
                return false;
            }
            var sphereEntities = Pool.GetList<SphereEntity>();
            for (var i = 0; i < darkness; i++)
            {
                var sphereEntity = GameManager.server.CreateEntity(PrefabSphere, position) as SphereEntity;
                if (sphereEntity == null)
                {
                    PrintDebug("ERROR: sphere entity is null", error: true);
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
            List<SphereEntity> sphereEntities;
            if (!_zoneSpheres.TryGetValue(zoneId, out sphereEntities))
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
                Pool.FreeList(ref sphereEntities);
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
            var attacker = info.InitiatorPlayer ?? (info.Initiator != null && info.Initiator.OwnerID.IsSteamId() ? BasePlayer.FindByID(info.Initiator.OwnerID) : null); //The attacker cannot be fully captured
            if (attacker == null || !attacker.userID.IsSteamId())
            {
                return null;
            }
            LeftZone victimLeftZone;
            if (_pvpDelays.TryGetValue(victim.userID, out victimLeftZone))
            {
                if (configData.Global.PvpDelayFlags.HasFlag(PVPDelayFlags.ZonePlayersCanDamageDelayedPlayers) && !string.IsNullOrEmpty(victimLeftZone.zoneId) && IsPlayerInZone(victimLeftZone, attacker)) //ZonePlayer attack DelayedPlayer
                {
                    return _true;
                }
                LeftZone attackerLeftZone;
                if (configData.Global.PvpDelayFlags.HasFlag(PVPDelayFlags.DelayedPlayersCanDamageDelayedPlayers) && _pvpDelays.TryGetValue(attacker.userID, out attackerLeftZone) && victimLeftZone.zoneId == attackerLeftZone.zoneId) //DelayedPlayer attack DelayedPlayer
                {
                    return _true;
                }

                return null;
            }
            else
            {
                LeftZone attackerLeftZone;
                if (_pvpDelays.TryGetValue(attacker.userID, out attackerLeftZone))
                {
                    if (configData.Global.PvpDelayFlags.HasFlag(PVPDelayFlags.DelayedPlayersCanDamageZonePlayers) && !string.IsNullOrEmpty(attackerLeftZone.zoneId) && IsPlayerInZone(attackerLeftZone, victim)) //DelayedPlayer attack ZonePlayer
                    {
                        return _true;
                    }

                    return null;
                }
            }
            return null;
        }

        private bool TryCreateMapping(string zoneId, string mapping)
        {
            var result = Interface.CallHook("AddOrUpdateMapping", zoneId, mapping);
            if (result != null && result is bool)
            {
                return (bool)result;
            }
            return false;
        }

        private bool TryRemoveMapping(string zoneId)
        {
            var result = Interface.CallHook("RemoveMapping", zoneId);
            if (result != null && result is bool)
            {
                return (bool)result;
            }
            return false;
        }

        #endregion TruePVE/NextGenPVE Integration

        #region BotReSpawn/MonBots Integration

        private bool BotReSpawnAllowed(IBotEvent botEvent)
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
            var result = CreateGroupSpawn(location, profileName, groupId);
            if (result == null)
            {
                PrintDebug("AddGroupSpawn returned invalid response.");
                return false;
            }

            if (BotReSpawn != null)
            {
                if (result.Length < 2)
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
                        PrintDebug($"ERROR: AddGroupSpawn failed: {result[1]}", error: true);
                        return false;
                }
            }
            return false;
        }

        private bool KillBots(string groupId)
        {
            var result = RemoveGroupSpawn(groupId);
            if (result == null)
            {
                PrintDebug("RemoveGroupSpawn returned invalid response.");
                return false;
            }

            if (BotReSpawn != null)
            {
                if (result.Length < 2)
                {
                    PrintDebug("RemoveGroupSpawn returned invalid response.");
                    return false;
                }
                if (result[0] == "error")
                {
                    PrintDebug($"ERROR: RemoveGroupSpawn failed: {result[1]}", error: true);
                    return false;
                }
            }
            return true;
        }

        private string[] CreateGroupSpawn(Vector3 location, string profileName, string groupId, int quantity = 0)
        {
            if (BotReSpawn != null)
            {
                return (string[])BotReSpawn?.Call("AddGroupSpawn", location, profileName, groupId, quantity);
            }
            return null;
        }

        private string[] RemoveGroupSpawn(string groupId)
        {
            if (BotReSpawn != null)
            {
                return (string[])BotReSpawn?.Call("RemoveGroupSpawn", groupId);
            }
            return null;
        }

        #endregion BotReSpawn/MonBots Integration

        #region ZoneManager Integration

        private void OnEnterZone(string zoneId, BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId())
            {
                return;
            }
            string eventName;
            if (!_activeDynamicZones.TryGetValue(zoneId, out eventName))
            {
                return;
            }
            PrintDebug($"{player.displayName} has entered a pvp zone({zoneId} | {eventName}).", true);

            TryRemovePVPDelay(player.userID, player.displayName);
        }

        private void OnExitZone(string zoneId, BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId())
            {
                return;
            }
            string eventName;
            if (!_activeDynamicZones.TryGetValue(zoneId, out eventName))
            {
                return;
            }
            PrintDebug($"{player.displayName} has left a pvp zone({zoneId} | {eventName}).", true);

            var baseEvent = GetBaseEvent(eventName);
            if (!baseEvent.PvpDelayEnabled || baseEvent.PvpDelayTime <= 0)
            {
                return;
            }

            var playerId = player.userID;
            var playerName = player.displayName;
            var leftZone = GetOrAddPVPDelay(player, zoneId, eventName);
            leftZone.zoneTimer = timer.Once(baseEvent.PvpDelayTime, () =>
            {
                TryRemovePVPDelay(playerId, playerName);
            });
            Interface.CallHook("OnPlayerAddedToPVPDelay", player.userID, zoneId, baseEvent.PvpDelayTime);
        }

        private bool CreateZone(string zoneId, string[] zoneArgs, Vector3 location)
        {
            return (bool)ZoneManager.Call("CreateOrUpdateZone", zoneId, zoneArgs, location);
        }

        private bool RemoveZone(string zoneId, string eventName = "")
        {
            try
            {
                return (bool)ZoneManager.Call("EraseZone", zoneId);
            }
            catch (Exception exception)
            {
                PrintDebug($"ERROR: EraseZone({eventName}) failed. {exception}");
                return true;
            }
        }

        private string[] GetZoneIds()
        {
            return (string[])ZoneManager.Call("GetZoneIDs");
        }

        private string GetZoneName(string zoneId)
        {
            return (string)ZoneManager.Call("GetZoneName", zoneId);
        }

        private ZoneManager.Zone GetZoneById(string zoneId)
        {
            return (ZoneManager.Zone)ZoneManager.Call("GetZoneByID", zoneId);
        }

        private string[] GetPlayerZoneIds(BasePlayer player)
        {
            return (string[])ZoneManager.Call("GetPlayerZoneIDs", player);
        }

        private bool IsPlayerInZone(LeftZone leftZone, BasePlayer player)
        {
            return (bool)ZoneManager.Call("IsPlayerInZone", leftZone.zoneId, player);
        }

        private List<BasePlayer> GetPlayersInZone(string zoneId)
        {
            return (List<BasePlayer>)ZoneManager.Call("GetPlayersInZone", zoneId);
        }

        #endregion ZoneManager Integration

        #region Debug

        private StringBuilder _debugStringBuilder;

        private void PrintDebug(string message, bool warning = false, bool error = false)
        {
            if (configData.Global.DebugEnabled)
            {
                if (error)
                {
                    PrintError(message);
                }
                else if (warning)
                {
                    PrintWarning(message);
                }
                else
                {
                    Puts(message);
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

        private string[] AllDynamicPVPZones()
        {
            return _activeDynamicZones.Keys.ToArray();
        }

        private bool IsDynamicPVPZone(string zoneId)
        {
            return _activeDynamicZones.ContainsKey(zoneId);
        }

        private bool EventDataExists(string eventName)
        {
            return storedData.EventDataExists(eventName);
        }

        private bool IsPlayerInPVPDelay(ulong playerId)
        {
            return _pvpDelays.ContainsKey(playerId);
        }

        private string GetPlayerPVPDelayedZoneID(ulong playerId)
        {
            LeftZone leftZone;
            if (!_pvpDelays.TryGetValue(playerId, out leftZone))
            {
                return null;
            }
            return leftZone.zoneId;
        }

        private string GetEventName(string zoneId)
        {
            string eventName;
            if (_activeDynamicZones.TryGetValue(zoneId, out eventName))
            {
                return eventName;
            }
            return null;
        }

        private bool CreateOrUpdateEventData(string eventName, string eventData, bool isTimed = false)
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

        private bool CreateEventData(string eventName, Vector3 position, bool isTimed)
        {
            if (EventDataExists(eventName))
            {
                return false;
            }
            if (isTimed)
            {
                var timedEvent = new TimedEvent();
                storedData.timedEvents.Add(eventName, timedEvent);
            }
            else
            {
                var autoEvent = new AutoEvent { Position = position };
                storedData.autoEvents.Add(eventName, autoEvent);
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
            var autoEvent = GetBaseEvent(eventName) as AutoEvent;
            if (autoEvent != null)
            {
                CreateDynamicZone(eventName, position == default(Vector3) ? autoEvent.Position : position, autoEvent.ZoneId);
            }
            else
            {
                CreateDynamicZone(eventName, position);
            }
            return true;
        }

        private bool StopEvent(string eventName)
        {
            if (!EventDataExists(eventName))
            {
                return false;
            }
            return ForceCloseZones(eventName);
        }

        private bool ForceCloseZones(string eventName)
        {
            var closed = false;
            foreach (var entry in _activeDynamicZones.ToArray())
            {
                if (entry.Value == eventName)
                {
                    if (DeleteDynamicZone(entry.Key))
                    {
                        closed = true;
                    }
                }
            }
            return closed;
        }

        #endregion API

        #region Commands

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
            if (args[0].ToLower() == "list")
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
                stringBuilder.AppendLine(Lang("CustomEvents", iPlayer.Id, customEventCount));
                foreach (var entry in storedData.autoEvents)
                {
                    i++;
                    stringBuilder.AppendLine(Lang("AutoEvent", iPlayer.Id, i, entry.Key, entry.Value.AutoStart, entry.Value.Position));
                }
                foreach (var entry in storedData.timedEvents)
                {
                    i++;
                    stringBuilder.AppendLine(Lang("TimedEvent", iPlayer.Id, i, entry.Key, entry.Value.Duration));
                }
                var result = stringBuilder.ToString();
                stringBuilder.Clear();
                Pool.Free(ref stringBuilder);
                Print(iPlayer, result);
                return;
            }
            if (args.Length < 2)
            {
                Print(iPlayer, Lang("NoEventName", iPlayer.Id));
                return;
            }
            var eventName = args[1];
            var position = (iPlayer.Object as BasePlayer)?.transform.position ?? Vector3.zero;
            switch (args[0].ToLower())
            {
                case "add":
                    var isTimed = args.Length >= 3;
                    Print(iPlayer,
                          !CreateEventData(eventName, position, isTimed)
                                  ? Lang("EventNameExist", iPlayer.Id, eventName)
                                  : Lang("EventDataAdded", iPlayer.Id, eventName));
                    return;

                case "remove":
                    Print(iPlayer,
                          !RemoveEventData(eventName)
                                  ? Lang("EventNameNotExist", iPlayer.Id, eventName)
                                  : Lang("EventDataRemoved", iPlayer.Id, eventName));
                    return;

                case "start":
                    Print(iPlayer,
                          !StartEvent(eventName, position)
                                  ? Lang("EventNameNotExist", iPlayer.Id, eventName)
                                  : Lang("EventStarted", iPlayer.Id, eventName));
                    return;

                case "stop":
                    Print(iPlayer,
                          !StopEvent(eventName)
                                  ? Lang("EventNameExist", iPlayer.Id, eventName)
                                  : Lang("EventStopped", iPlayer.Id, eventName));
                    return;

                case "edit":
                    if (args.Length >= 3)
                    {
                        AutoEvent autoEvent;
                        if (storedData.autoEvents.TryGetValue(eventName, out autoEvent))
                        {
                            switch (args[2].ToLower())
                            {
                                case "1":
                                case "true":
                                    autoEvent.AutoStart = true;
                                    Print(iPlayer, Lang("AutoEventAutoStart", iPlayer.Id, eventName, true));
                                    _dataChanged = true;
                                    return;

                                case "0":
                                case "false":
                                    autoEvent.AutoStart = false;
                                    Print(iPlayer, Lang("AutoEventAutoStart", iPlayer.Id, eventName, false));
                                    _dataChanged = true;
                                    return;

                                case "move":
                                    autoEvent.Position = position;
                                    Print(iPlayer, Lang("AutoEventMove", iPlayer.Id, eventName));
                                    _dataChanged = true;
                                    return;
                            }
                        }
                        else
                        {
                            TimedEvent timedEvent;
                            if (storedData.timedEvents.TryGetValue(eventName, out timedEvent))
                            {
                                float duration;
                                if (float.TryParse(args[2], out duration))
                                {
                                    timedEvent.Duration = duration;
                                    Print(iPlayer, Lang("TimedEventDuration", iPlayer.Id, eventName, duration));
                                    _dataChanged = true;
                                    return;
                                }
                            }
                        }
                    }
                    Print(iPlayer, Lang("SyntaxError", iPlayer.Id, configData.Chat.Command));
                    return;

                case "h":
                case "help":
                    var stringBuilder = Pool.Get<StringBuilder>();
                    stringBuilder.Clear();
                    stringBuilder.AppendLine();
                    stringBuilder.AppendLine(Lang("Syntax", iPlayer.Id, configData.Chat.Command));
                    stringBuilder.AppendLine(Lang("Syntax1", iPlayer.Id, configData.Chat.Command));
                    stringBuilder.AppendLine(Lang("Syntax2", iPlayer.Id, configData.Chat.Command));
                    stringBuilder.AppendLine(Lang("Syntax3", iPlayer.Id, configData.Chat.Command));
                    stringBuilder.AppendLine(Lang("Syntax4", iPlayer.Id, configData.Chat.Command));
                    stringBuilder.AppendLine(Lang("Syntax5", iPlayer.Id, configData.Chat.Command));
                    stringBuilder.AppendLine(Lang("Syntax6", iPlayer.Id, configData.Chat.Command));
                    var result = stringBuilder.ToString();
                    stringBuilder.Clear();
                    Pool.Free(ref stringBuilder);
                    Print(iPlayer, result);
                    return;

                default:
                    Print(iPlayer, Lang("SyntaxError", iPlayer.Id, configData.Chat.Command));
                    return;
            }
        }

        #endregion Commands

        #region ConfigurationFile

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Global Settings")]
            public GlobalSettings Global { get; set; } = new GlobalSettings();

            [JsonProperty(PropertyName = "Chat Settings")]
            public ChatSettings Chat { get; set; } = new ChatSettings();

            [JsonProperty(PropertyName = "General Event Settings")]
            public GeneralEventSettings GeneralEvents { get; set; } = new GeneralEventSettings();

            [JsonProperty(PropertyName = "Monument Event Settings")]
            public Dictionary<string, MonumentEvent> MonumentEvents { get; set; } = new Dictionary<string, MonumentEvent>();

            [JsonProperty(PropertyName = "Version")]
            public VersionNumber Version { get; set; }
        }

        private class GlobalSettings
        {
            [JsonProperty(PropertyName = "Enable Debug Mode")]
            public bool DebugEnabled { get; set; }

            [JsonProperty(PropertyName = "Log Debug To File")]
            public bool LogToFile { get; set; }

            [JsonProperty(PropertyName = "Compare Radius (Used to determine if it is a SupplySignal)")]
            public float CompareRadius { get; set; } = 2f;

            [JsonProperty(PropertyName = "If the entity has an owner, don't create a PVP zone")]
            public bool CheckEntityOwner { get; set; } = true;

            [JsonProperty(PropertyName = "PVP Delay Flags")]
            public PVPDelayFlags PvpDelayFlags { get; set; } = PVPDelayFlags.ZonePlayersCanDamageDelayedPlayers | PVPDelayFlags.DelayedPlayersCanDamageDelayedPlayers | PVPDelayFlags.DelayedPlayersCanDamageZonePlayers;
        }

        private class ChatSettings
        {
            [JsonProperty(PropertyName = "Command")]
            public string Command { get; set; } = "dynpvp";

            [JsonProperty(PropertyName = "Chat Prefix")]
            public string Prefix { get; set; } = "[DynamicPVP]: ";

            [JsonProperty(PropertyName = "Chat Prefix Color")]
            public string PrefixColor { get; set; } = "#00FFFF";

            [JsonProperty(PropertyName = "Chat SteamID Icon")]
            public ulong SteamIdIcon { get; set; } = 0;
        }

        private class GeneralEventSettings
        {
            [JsonProperty(PropertyName = "Bradley Event")]
            public TimedEvent BradleyApc { get; set; } = new TimedEvent();

            [JsonProperty(PropertyName = "Patrol Helicopter Event")]
            public TimedEvent PatrolHelicopter { get; set; } = new TimedEvent();

            [JsonProperty(PropertyName = "Supply Signal Event")]
            public SupplyDropEvent SupplySignal { get; set; } = new SupplyDropEvent();

            [JsonProperty(PropertyName = "Timed Supply Event")]
            public SupplyDropEvent TimedSupply { get; set; } = new SupplyDropEvent();

            [JsonProperty(PropertyName = "Hackable Crate Event")]
            public HackableCrateEvent HackableCrate { get; set; } = new HackableCrateEvent();

            [JsonProperty(PropertyName = "Excavator Ignition Event")]
            public MonumentEvent ExcavatorIgnition { get; set; } = new MonumentEvent();

            [JsonProperty(PropertyName = "Cargo Ship Event")]
            public CargoShipEvent CargoShip { get; set; } = new CargoShipEvent();
        }

        #region Event

        public abstract class BaseEvent
        {
            [JsonProperty(PropertyName = "Enable Event", Order = 1)]
            public bool Enabled { get; set; }

            [JsonProperty(PropertyName = "Enable PVP Delay", Order = 2)]
            public bool PvpDelayEnabled { get; set; }

            [JsonProperty(PropertyName = "PVP Delay Time", Order = 3)]
            public float PvpDelayTime { get; set; } = 10f;

            [JsonProperty(PropertyName = "Delay In Starting Event", Order = 6)]
            public float EventStartDelay { get; set; }

            [JsonProperty(PropertyName = "Delay In Stopping Event", Order = 7)]
            public float EventStopDelay { get; set; }

            [JsonProperty(PropertyName = "TruePVE Mapping", Order = 8)]
            public string Mapping { get; set; } = "exclude";

            // [JsonProperty(PropertyName = "Do not create a new zone when in the same event type of pvp zone", Order = 9)]
            // public bool dontCreateWhenInSameType = false;

            [JsonProperty(PropertyName = "Use Blacklist Commands (If false, a whitelist is used)", Order = 10)]
            public bool UseBlacklistCommands { get; set; } = true;

            [JsonProperty(PropertyName = "Command works for PVP delayed players", Order = 11)]
            public bool CommandWorksForPVPDelay { get; set; } = false;

            [JsonProperty(PropertyName = "Command List (If there is a '/' at the front, it is a chat command)", Order = 12)]
            public List<string> CommandList { get; set; } = new List<string>();

            public abstract BaseDynamicZone GetDynamicZone();
        }

        public class DomeMixedEvent : BaseEvent, IDomeEvent
        {
            public bool DomesEnabled { get; set; } = true;
            public int DomesDarkness { get; set; } = 8;

            [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 20)]
            public SphereCubeDynamicZone DynamicZone { get; set; } = new SphereCubeDynamicZone();

            public override BaseDynamicZone GetDynamicZone()
            {
                return DynamicZone;
            }
        }

        public class BotDomeMixedEvent : DomeMixedEvent, IBotEvent
        {
            public bool BotsEnabled { get; set; }
            public string BotProfileName { get; set; } = string.Empty;
        }

        public class MonumentEvent : DomeMixedEvent
        {
            [JsonProperty(PropertyName = "Zone ID", Order = 21)]
            public string ZoneId { get; set; } = string.Empty;

            [JsonProperty(PropertyName = "Transform Position", Order = 22)]
            public Vector3 TransformPosition { get; set; }
        }

        public class AutoEvent : BotDomeMixedEvent
        {
            [JsonProperty(PropertyName = "Auto Start", Order = 23)]
            public bool AutoStart { get; set; }

            [JsonProperty(PropertyName = "Zone ID", Order = 24)]
            public string ZoneId { get; set; } = string.Empty;

            [JsonProperty(PropertyName = "Position", Order = 25)]
            public Vector3 Position { get; set; }
        }

        public class TimedEvent : BotDomeMixedEvent, ITimedEvent
        {
            public float Duration { get; set; } = 600f;
        }

        public class HackableCrateEvent : TimedEvent, ITimedDisable
        {
            [JsonProperty(PropertyName = "Start Event When Spawned (If false, the event starts when unlocking)", Order = 24)]
            public bool StartWhenSpawned { get; set; } = true;

            [JsonProperty(PropertyName = "Stop Event When Killed", Order = 25)]
            public bool StopWhenKilled { get; set; }

            [JsonProperty(PropertyName = "Event Timer Starts When Looted", Order = 26)]
            public bool TimerStartWhenLooted { get; set; }

            [JsonProperty(PropertyName = "Event Timer Starts When Unlocked", Order = 27)]
            public bool TimerStartWhenUnlocked { get; set; }

            [JsonProperty(PropertyName = "Excluding Hackable Crate On OilRig", Order = 28)]
            public bool ExcludeOilRig { get; set; } = true;

            [JsonProperty(PropertyName = "Excluding Hackable Crate on Cargo Ship", Order = 29)]
            public bool ExcludeCargoShip { get; set; } = true;

            public bool IsTimedDisabled()
            {
                return StopWhenKilled || TimerStartWhenLooted || TimerStartWhenUnlocked;
            }
        }

        public class SupplyDropEvent : TimedEvent, ITimedDisable
        {
            [JsonProperty(PropertyName = "Start Event When Spawned (If false, the event starts when landed)", Order = 24)]
            public bool StartWhenSpawned { get; set; } = true;

            [JsonProperty(PropertyName = "Stop Event When Killed", Order = 25)]
            public bool StopWhenKilled { get; set; }

            [JsonProperty(PropertyName = "Event Timer Starts When Looted", Order = 26)]
            public bool TimerStartWhenLooted { get; set; }

            public bool IsTimedDisabled()
            {
                return StopWhenKilled || TimerStartWhenLooted;
            }
        }

        public class CargoShipEvent : BaseEvent
        {
            [JsonProperty(PropertyName = "Dynamic PVP Zone Settings", Order = 20)]
            public CubeParentDynamicZone DynamicZone { get; set; } = new CubeParentDynamicZone
            {
                Size = new Vector3(25.9f, 43.3f, 152.8f),
                Center = new Vector3(0f, 21.6f, 6.6f)
            };

            public override BaseDynamicZone GetDynamicZone()
            {
                return DynamicZone;
            }
        }

        #region Interface

        public interface ITimedDisable
        {
            bool IsTimedDisabled();
        }

        public interface ITimedEvent
        {
            [JsonProperty(PropertyName = "Event Duration", Order = 23)]
            float Duration { get; set; }
        }

        public interface IBotEvent
        {
            [JsonProperty(PropertyName = "Enable Bots (Need BotSpawn Plugin)", Order = 21)]
            bool BotsEnabled { get; set; }

            [JsonProperty(PropertyName = "BotSpawn Profile Name", Order = 22)]
            string BotProfileName { get; set; }
        }

        public interface IDomeEvent
        {
            [JsonProperty(PropertyName = "Enable Domes", Order = 4)]
            bool DomesEnabled { get; set; }

            [JsonProperty(PropertyName = "Domes Darkness", Order = 5)]
            int DomesDarkness { get; set; }
        }

        #endregion Interface

        #endregion Event

        #region Zone

        public abstract class BaseDynamicZone
        {
            [JsonProperty(PropertyName = "Zone Comfort", Order = 10)]
            public float Comfort { get; set; }

            [JsonProperty(PropertyName = "Zone Radiation", Order = 11)]
            public float Radiation { get; set; }

            [JsonProperty(PropertyName = "Zone Temperature", Order = 12)]
            public float Temperature { get; set; }

            [JsonProperty(PropertyName = "Enable Safe Zone", Order = 13)]
            public bool SafeZone { get; set; }

            [JsonProperty(PropertyName = "Eject Spawns", Order = 14)]
            public string EjectSpawns { get; set; } = string.Empty;

            [JsonProperty(PropertyName = "Zone Parent ID", Order = 15)]
            public string ParentId { get; set; } = string.Empty;

            [JsonProperty(PropertyName = "Enter Message", Order = 16)]
            public string EnterMessage { get; set; } = "Entering a PVP area!";

            [JsonProperty(PropertyName = "Leave Message", Order = 17)]
            public string LeaveMessage { get; set; } = "Leaving a PVP area.";

            [JsonProperty(PropertyName = "Permission Required To Enter Zone", Order = 18)]
            public string Permission { get; set; } = string.Empty;

            [JsonProperty(PropertyName = "Extra Zone Flags", Order = 20)]
            public List<string> ExtraZoneFlags { get; set; } = new List<string>();

            private string[] _zoneSettings;

            public virtual string[] ZoneSettings(Transform transform = null)
            {
                return _zoneSettings ?? (_zoneSettings = GetZoneSettings());
            }

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
                if (Temperature != 0f)
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

        public class SphereDynamicZone : BaseDynamicZone, ISphereZone
        {
            public float Radius { get; set; } = 100;

            protected override string[] GetZoneSettings(Transform transform = null)
            {
                var zoneSettings = new List<string> { "radius", Radius.ToString(CultureInfo.InvariantCulture) };
                GetBaseZoneSettings(zoneSettings);
                var array = zoneSettings.ToArray();
                return array;
            }
        }

        public class CubeDynamicZone : BaseDynamicZone, ICubeZone
        {
            public Vector3 Size { get; set; }
            public float Rotation { get; set; }
            public bool FixedRotation { get; set; }

            public override string[] ZoneSettings(Transform transform = null)
            {
                return transform == null || FixedRotation ? base.ZoneSettings(transform) : GetZoneSettings(transform);
            }

            protected override string[] GetZoneSettings(Transform transform = null)
            {
                var zoneSettings = new List<string> { "size", $"{Size.x} {Size.y} {Size.z}" };
                if (transform == null || FixedRotation)
                {
                    zoneSettings.Add(Rotation.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    zoneSettings.Add((transform.rotation.eulerAngles.y + Rotation).ToString(CultureInfo.InvariantCulture));
                }
                GetBaseZoneSettings(zoneSettings);
                var array = zoneSettings.ToArray();
                return array;
            }
        }

        public class SphereCubeDynamicZone : BaseDynamicZone, ICubeZone, ISphereZone
        {
            public float Radius { get; set; }
            public Vector3 Size { get; set; }
            public float Rotation { get; set; }
            public bool FixedRotation { get; set; }

            public override string[] ZoneSettings(Transform transform = null)
            {
                return transform == null || FixedRotation || Radius > 0f ? base.ZoneSettings(transform) : GetZoneSettings(transform);
            }

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
                    if (transform == null || FixedRotation)
                    {
                        zoneSettings.Add(Rotation.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        zoneSettings.Add((transform.rotation.eulerAngles.y + Rotation).ToString(CultureInfo.InvariantCulture));
                    }
                }
                GetBaseZoneSettings(zoneSettings);
                var array = zoneSettings.ToArray();
                return array;
            }
        }

        public class SphereParentDynamicZone : SphereDynamicZone, IParentZone
        {
            public Vector3 Center { get; set; }
        }

        public class CubeParentDynamicZone : CubeDynamicZone, IParentZone
        {
            public Vector3 Center { get; set; }

            protected override string[] GetZoneSettings(Transform transform = null)
            {
                var zoneSettings = new List<string> { "size", $"{Size.x} {Size.y} {Size.z}" };
                GetBaseZoneSettings(zoneSettings);
                var array = zoneSettings.ToArray();
                return array;
            }
        }

        #region Interface

        public interface ISphereZone
        {
            [JsonProperty(PropertyName = "Zone Radius", Order = 0)]
            float Radius { get; set; }
        }

        public interface ICubeZone
        {
            [JsonProperty(PropertyName = "Zone Size", Order = 1)]
            Vector3 Size { get; set; }

            [JsonProperty(PropertyName = "Zone Rotation", Order = 2)]
            float Rotation { get; set; }

            [JsonProperty(PropertyName = "Fixed Rotation", Order = 3)]
            bool FixedRotation { get; set; }
        }

        public interface IParentZone
        {
            [JsonProperty(PropertyName = "Transform Position", Order = 5)]
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
            configData = new ConfigData();
            configData.Version = Version;
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(configData);
        }

        private void UpdateConfigValues()
        {
            if (configData.Version < Version)
            {
                if (configData.Version <= default(VersionNumber))
                {
                    //string prefix, prefixColor;
                    //if (GetConfigValue(out prefix, "Chat Settings", "Chat Prefix") && GetConfigValue(out prefixColor, "Chat Settings", "Chat Prefix Color"))
                    //{
                    //    configData.chatS.prefix = $"<color={prefixColor}>{prefix}</color>: ";
                    //}
                }

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
                    bool value;
                    if (GetConfigValue(out value, "General Event Settings", "Supply Signal Event", "Supply Drop Event Start When Spawned (If false, the event starts when landed)"))
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
        }

        private bool GetConfigValue<T>(out T value, params string[] path)
        {
            var configValue = Config.Get(path);
            if (configValue != null)
            {
                if (configValue is T)
                {
                    value = (T)configValue;
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

            value = default(T);
            return false;
        }

        #endregion ConfigurationFile

        #region DataFile

        private StoredData storedData;

        private class StoredData
        {
            public readonly Dictionary<string, TimedEvent> timedEvents = new Dictionary<string, TimedEvent>();
            public readonly Dictionary<string, AutoEvent> autoEvents = new Dictionary<string, AutoEvent>();

            public bool EventDataExists(string eventName)
            {
                return timedEvents.ContainsKey(eventName) || autoEvents.ContainsKey(eventName);
            }

            public bool RemoveEventData(string eventName)
            {
                return timedEvents.Remove(eventName) || autoEvents.Remove(eventName);
            }

            [JsonIgnore]
            public int CustomEventsCount => timedEvents.Count + autoEvents.Count;
        }

        private void LoadData()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
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

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
        }

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
            }
            else
            {
                var player = iPlayer.Object as BasePlayer;
                if (player != null)
                {
                    Player.Message(player, message, $"<color={configData.Chat.PrefixColor}>{configData.Chat.Prefix}</color>", configData.Chat.SteamIdIcon);
                }
                else
                {
                    iPlayer.Reply(message, $"<color={configData.Chat.PrefixColor}>{configData.Chat.Prefix}</color>");
                }
            }
        }

        private void Print(BasePlayer player, string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            Player.Message(player, message, string.IsNullOrEmpty(configData.Chat.Prefix) ? null : $"<color={configData.Chat.PrefixColor}>{configData.Chat.Prefix}</color>", configData.Chat.SteamIdIcon);
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
                ["Syntax6"] = "<color=#ce422b>/{0} edit <eventName> <time(seconds)></color> - Changes the duration of a timed event"
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
                ["Syntax6"] = "<color=#ce422b>/{0} edit <eventName> <time(seconds)></color> - 修改定时事件的持续时间"
            }, this, "zh-CN");
        }

        #endregion LanguageFile
    }
}
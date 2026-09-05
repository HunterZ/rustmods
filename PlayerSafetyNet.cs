using Facepunch;
using Newtonsoft.Json;
using Rust;
using System.Collections.Generic;
using System.Text;
using System;
using UnityEngine;
using Random = Oxide.Core.Random;

namespace Oxide.Plugins;

[Info("Player Safety Net", "HunterZ", "1.2.0")]
public class PlayerSafetyNet : RustPlugin
{
  #region Data

  private const float WorldTop = 1000.0f;
  private const float WorldBottom = -500.0f;
  private const int RaycastHitsMax = 64;
  private readonly RaycastHit[] _raycastHits = new RaycastHit[RaycastHitsMax];
  private const float MaxDist = 2000f;
  private const float MinSelfHitDistance = 0.001f;
  private const float MinUpHitDistance = 0.1f;
  private const int SolidLayerMask = Layers.Solid;

  private Collider _terrainCollider;
  private Collider _deepSeaBottomCollider;

  private readonly SortedDictionary<string, int> _statsIndexes = new();
  private readonly List<int> _statsData = new(300);
  private readonly int[] _newIgnored = { 1, 0, 0 };
  private readonly int[] _newUnmoved = { 0, 1, 0 };
  private readonly int[] _newBounced = { 0, 0, 1 };
  private readonly StringBuilder _sb = new();
  private const string CanDropActiveItemKey = "CanDropActiveItem";
  private const string TerrainViolationKey = "OnPlayerViolation:InsideTerrain";

  private string _ignorePermission;
  private string _reportPermission;

  // between being string-based, and having to rifle through groups, Oxide's
  //  permissions API is pretty expensive; a cache is used to ameliorate this
  private readonly Dictionary<ulong, bool> _userIgnoreStates = new();

  private enum StatColumn
  {
    HookAndPrefab,
    Ignored,
    Unmoved,
    Bounced
  }

  private enum StatType
  {
    Ignored = 0,
    Unmoved = 1,
    Bounced = 2
  }

  private readonly int[] _columnWidths =
  {
    nameof(StatColumn.HookAndPrefab).Length,
    nameof(StatColumn.Ignored).Length,
    nameof(StatColumn.Unmoved).Length,
    nameof(StatColumn.Bounced).Length
  };

  private struct NonNull{}

  private readonly NonNull _nonNull = default;

  private PluginConfig _config;

  #endregion

  #region Helpers

  // return whether a raycast is of interest
  private (bool, Collider) ProcessHit(
    RaycastHit hit, bool up, Collider startCollider, BaseEntity startEntity)
  {
    var collider = hit.collider;
    if (!collider) return (false, null);

    // ignore bounces back onto the starting collider
    // this prevents scientists from sticking to the ceiling on large oilrig lol
    //
    // NOTE: the distance limit seems needed to allow stuff to bounce to the top
    //  of a collider that has another one immediately on top of it, which is
    //  usually preferable to leaving it untouched
    if (collider == startCollider && hit.distance < MinSelfHitDistance)
    {
      // PrintWarning($"Ignoring {(up ? "upward" : "downward")} hit on startCollider={startCollider}@{startCollider.transform.position} because hitDistance={hit.distance} is less than minDistance={MinSelfHitDistance}");
      return (false, collider);
    }

    // ignore bounces back onto the starting entity or its corpse parent
    // this prevents v2 scientists from doing sick flips when they die, loot
    //  containers getting moved on top of themselves, etc.
    BaseEntity cEntity = null;
    if (startEntity)
    {
      cEntity = collider.ToBaseEntity();
      if (SameOrCorpseOrigin(cEntity, startEntity)) return (false, collider);
    }

    switch (up)
    {
      // ignore terrain colliders when looking up
      case true when
        collider == _terrainCollider || collider == _deepSeaBottomCollider:
      // ignore terrain if downward hit is inside a terrain ignore volume
      // NOTE: GetIgnore() handles checking whether the hit is a terrain one, so
      //  there's no point doing it here as well
      case false when TerrainMeta.Collision.GetIgnore(hit):
      {
        return (false, collider);
      }
    }

    // check for blacklisted names
    var colliderName = collider.name;
    if (colliderName.Length > 0 &&
        colliderName is "Add_To_Height" or "Collider")
    {
      return (false, collider);
    }

    // after this are upward-only cases
    if (!up) return (true, collider);

    // don't bounce things off the bottom of living entities or containers
    cEntity ??= collider.ToBaseEntity();
    return cEntity ? (!IgnoredBottom(cEntity), collider) : (true, collider);
  }

  // return whether source entity is candidate entity or its corpse
  private static bool SameOrCorpseOrigin(
    BaseEntity cEntity, BaseEntity sEntity) =>
    cEntity && sEntity &&
    (cEntity == sEntity ||
     sEntity is BaseCorpse corpse && corpse.parentEnt == cEntity);

  // return whether candidate entity is something whose bottom other entities
  //  should be allowed to bounce up through
  private static bool IgnoredBottom(BaseEntity entity) =>
    entity
      .HasTrait(BaseEntity.TraitFlag.Alive) ||
    entity
      is BaseCombatEntity { IsNpc: true }
      or BaseCorpse or BaseVehicle or LootContainer or ServerGib;

  // get Y coordinate of appropriate prefab, terrain, or world bound that would
  //  stop movement in the given direction from the given position
  //
  // returns null on invalid maxDistance or appropriate height not found
  private (float?, Collider) GetTerminalY(
    Vector3 position, float maxDistance, bool up, Collider startCollider,
    BaseEntity startEntity)
  {
    var direction = up ? Vector3.up : Vector3.down;
    var cDist = MaxDist;
    float? cY = null;
    var cCollider = startCollider;
    // determine distance from position to terrain on upward raycast only
    var terrainY = up ? TerrainMeta.HeightMap.GetHeight(position) : 0f;
    var terrainDist = terrainY - position.y;
    var minUpHitDist =
      Mathf.Max(MinUpHitDistance, startEntity.bounds.extents.y);

    // optimization: abort if distance is non-positive
    if (maxDistance <= 0)
    {
      return (null, null);
    }

    var hitCount = Physics.RaycastNonAlloc(
      position, direction, _raycastHits, maxDistance, SolidLayerMask);
    if (hitCount >= _raycastHits.Length)
    {
      PrintWarning($"GetTerminalY(): Raycast hit count at or above configured maximum ({hitCount}/{_raycastHits.Length})");
    }

    for (var i = 0; i < hitCount; ++i)
    {
      var hit = _raycastHits[i];
      if (hit.distance >= cDist) continue;
      // on upward scans, skip anything that's too close to the terrain
      if (up && Mathf.Abs(hit.distance - terrainDist) < minUpHitDist)
      {
        continue;
      }
      var (use, collider) = ProcessHit(hit, up, startCollider, startEntity);
      if (!use) continue;
      // best match so far; record it as a candidate
      cDist = hit.distance;
      cY = position.y + cDist * direction.y;
      cCollider = collider;
    }

    return (cY, cCollider);
  }

  private (float?, Collider) GetClosestSolidAbove(
    Vector3 position, Collider startCollider, BaseEntity startEntity) =>
    GetTerminalY(
      position, WorldTop - position.y, true, startCollider, startEntity);

  private (float?, Collider) GetClosestSolidBelow(
    Vector3 position, Collider startCollider, BaseEntity startEntity) =>
    GetTerminalY(
      position, -WorldBottom + position.y, false, startCollider, startEntity);

  // use raycasts to simulate bouncing something from the given position off of
  //  a suitable ceiling (including top of the world), then letting it fall back
  //  down onto a suitable floor (including terrain and prefabs)
  //
  // if a suitable floor was found, and it is above the given position, return
  //  its height; else return null
  private float? ShouldMove(BaseEntity entity)
  {
    if (!entity) return null;

    var position = entity.transform.position;

    // abort if in a holiday dungeon etc. for now
    // NOTE: need to allow < WorldBottom because kill check happens late
    if (position.y >= WorldTop) return null;

    var originalY = position.y;

    // allow only 2 iterations (see last-ditch effort comment below)
    for (var i = 0; i < 2; ++i)
    {
      if (i > 0)
      {
        // try again starting from detected ceiling
        // this is a last-ditch effort in case player fell out of a hole in the
        //  world with a ceiling above, e.g. train tunnels entrance on a primitive
        //  map in non-primitive mode
        PrintWarning($"No floor found, but ceiling is below world top; trying again starting at ceiling position={position}");
      }

      // find ceiling or top of world above position
      var (upY, upCollider) = GetClosestSolidAbove(
        position, entity.GetComponent<Collider>(), entity);
      var ceilingY = upY ?? WorldTop;
      var ceilingPos = new Vector3(position.x, ceilingY, position.z);
      // find closest useful floor/terrain below ceiling
      var (floorY, downCollider) = GetClosestSolidBelow(
        ceilingPos, upCollider, entity);
      if (floorY <= ceilingY && downCollider)
      {
        // return floor if above original position, else null
        return floorY > originalY ? floorY : null;
      }
      // else no floor of interest found

      // abort if ceiling is top of world
      if (ceilingY >= WorldTop)
      {
        PrintWarning("No floor found, but ceiling is world top; giving up");
        return null;
      }

      position = ceilingPos;
    }

    PrintWarning("No suitable landing found; giving up");
    return null;
  }

  // move a non-player entity to a new Y position
  private void MoveEntityY(BaseEntity entity, float newY)
  {
    // prevent clients from trying to interpolate movement, which can cause
    //  things to get stuck under colliders that we're trying to elevate through
    if (!entity.limitNetworking)
    {
      entity.limitNetworking = true;
      NextFrame(() =>
      {
        if (!entity) return;
        entity.limitNetworking = false;
        entity.SendNetworkUpdate_Position();
      });
    }
    var newPos = entity.ServerWorldPosition.XZ(newY);
    entity.ServerWorldPosition = newPos;
  }

  // record an occurrence of the given stat type for the given key string
  private void RecordData(string key, StatType type)
  {
    var typeAsInt = (int)type;
    var statColumn = typeAsInt + 1;

    if (_statsIndexes.TryGetValue(key, out var index))
    {
      // increment existing data
      var newCount = ++_statsData[index + typeAsInt];
      // check if new value should increase stat column width
      var digits = newCount.Digits();
      if (digits > _columnWidths[statColumn])
      {
        _columnWidths[statColumn] = digits;
      }
      return;
    }

    // add new data triplet with appropriate values
    _statsData.AddRange(type switch
    {
      StatType.Ignored => _newIgnored,
      StatType.Unmoved => _newUnmoved,
      StatType.Bounced => _newBounced,
      _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    });
    // record new key-index pair
    index = _statsIndexes.Count * 3;
    _statsIndexes.Add(key, index);
    // check if new key should increase stat column width
    if (key.Length > _columnWidths[0]) _columnWidths[0] = key.Length;
  }

  private void RecordIgnored(string key) => RecordData(key, StatType.Ignored);

  private void RecordUnmoved(string key) => RecordData(key, StatType.Unmoved);

  private void RecordBounced(string key) => RecordData(key, StatType.Bounced);

  private bool ShouldIgnorePlayer(BasePlayer player) =>
    !player || HasIgnorePermission(player.userID.Get(), player.UserIDString);

  private bool ShouldIgnorePlayer(ulong id) =>
    HasIgnorePermission(id);

  private void BounceEntity<T>(T entity) where T : BaseEntity
  {
    if (!entity) return;
    var prefabName = entity.ShortPrefabName;
    if (prefabName is "generic_world") prefabName = entity.name;
    _sb
      .Clear().Append(entity.GetType()).Append(':').Append(prefabName);
    var entitySignature = _sb.ToString();
    _sb.Clear();
    if (entity.HasParent() ||
        _config.BounceIgnorePrefabs.Contains(prefabName) ||
        (entity is DroppedItem droppedItem &&
         ShouldIgnorePlayer(droppedItem.DroppedBy)) ||
        (entity is PlayerCorpse { parentEnt: BasePlayer player } &&
         ShouldIgnorePlayer(player)))
    {
      RecordIgnored(entitySignature);
      return;
    }
    var position = entity.transform.position;
    if (ShouldMove(entity) is not { } newY)
    {
      RecordUnmoved(entitySignature);
      return;
    }
    if (_config.BounceLog && !_config.LogIgnorePrefabs.Contains(prefabName))
    {
      // use StringBuilder scratchpad to build bounce logs, because string
      //  interpolation would perform extra heap allocations when converting
      //  numeric primitive values to string representations
      _sb
        .Clear().Append("BounceEntity(): Moving ").Append(entitySignature)
        .Append('[').Append(entity.net?.ID.Value ?? 0).Append(']')
        .Append(" at ").AppendPosition(position).Append(" to new Y=")
        .Append(newY).Append(" (").Append(newY - position.y).Append(')');
      Puts(_sb.ToString());
      _sb.Clear();
    }
    MoveEntityY(entity, newY);
    RecordBounced(entitySignature);
  }

  private static Collider GetDeepSeaCollider(DeepSeaManager deepSeaManager)
  {
    var childCount = deepSeaManager.sharedCollidersParent.childCount;
    for (var i = 0; i < childCount; ++i)
    {
      var child = deepSeaManager.sharedCollidersParent.GetChild(i);
      if (child.TryGetComponent(out Collider collider) &&
          collider is { name: "DeepSea_Bottom" })
      {
        return collider;
      }
    }
    return null;
  }

  private bool HasIgnorePermission(ulong id, string idString = null)
  {
    // never ignore NPC players
    if (!id.IsSteamId()) return false;
    // if player has a cached known state, return that
    if (_userIgnoreStates.TryGetValue(id, out var state))
    {
      return state;
    }
    // populate cache and return result
    if (string.IsNullOrEmpty(idString)) idString = id.ToString();
    state =
      permission.UserHasPermission(idString, _ignorePermission);
    _userIgnoreStates[id] = state;
    return state;
  }

  private bool HasReportPermission(BasePlayer player) =>
    permission.UserHasPermission(player.UserIDString, _reportPermission);

  #endregion

  #region Oxide

  protected override void LoadConfig()
  {
    base.LoadConfig();

    try
    {
      _config = Config.ReadObject<PluginConfig>();
    }
    catch (Exception ex)
    {
      PrintWarning($"LoadConfig(): Error loading config file:\n{ex}");
      _config = null;
    }

    if (null == _config) LoadDefaultConfig();

    SaveConfig();
  }

  protected override void LoadDefaultConfig()
  {
    Puts("LoadDefaultConfig(): Creating default config data");
    _config = new PluginConfig();
  }

  protected override void SaveConfig()
  {
    Puts("SaveConfig(): Writing config file");
    Config.WriteObject(_config);
  }

  private void Init()
  {
    if (null == _config)
    {
      PrintError("Config data is null");
      return;
    }

    if (!_config.PreventDrop)
    {
      Unsubscribe(nameof(CanDropActiveItem));
    }
    if (!_config.BounceTerrainViolation)
    {
      Unsubscribe(nameof(OnPlayerViolation));
    }
    if (!_config.BounceBaseCorpse &&
        !_config.BounceDroppedItemContainer &&
        !_config.BounceHackableLockedCrate &&
        !_config.BounceHelicopterDebris &&
        !_config.BounceHorseCorpse &&
        !_config.BounceLockedByEntCrate &&
        !_config.BounceLootableCorpse &&
        !_config.BounceNpcPlayerCorpse &&
        !_config.BouncePlayerCorpse)
    {
      Unsubscribe(nameof(OnEntitySpawned));
    }

    _ignorePermission = $"{Name.ToLower()}.ignore";
    permission.RegisterPermission(_ignorePermission, this);
    _reportPermission = $"{Name.ToLower()}.report";
    permission.RegisterPermission(_reportPermission, this);
  }

  private void OnServerInitialized()
  {
    _terrainCollider = TerrainMeta.Collider;
    var deepSeaManager = DeepSeaManager.ServerInstance;
    if (deepSeaManager && deepSeaManager.IsOpen())
    {
      _deepSeaBottomCollider = GetDeepSeaCollider(deepSeaManager);
    }
  }

  private void Unload()
  {
    _terrainCollider = null;
    _deepSeaBottomCollider = null;
  }

  private void OnDeepSeaOpened(DeepSeaManager deepSeaManager)
  {
    if (!deepSeaManager) return;
    _deepSeaBottomCollider = GetDeepSeaCollider(deepSeaManager);
  }

  private void OnDeepSeaClose(DeepSeaManager deepSeaManager)
  {
    _deepSeaBottomCollider = null;
  }

  private void OnGroupPermissionGranted(string group, string perm)
  {
    if (perm != _ignorePermission) return;
    // invalidate cache so that it can be rebuilt piecemeal
    Puts($"Dropping {_userIgnoreStates.Count} cached permission state(s)");
    _userIgnoreStates.Clear();
  }

  private void OnGroupPermissionRevoked(string group, string perm) =>
    OnGroupPermissionGranted(group, perm);

  private void OnUserPermissionGranted(string playerID, string perm)
  {
    if (perm != _ignorePermission) return;
    if (!ulong.TryParse(playerID, out var id)) return;
    _userIgnoreStates[id] = true;
  }

  private void OnUserPermissionRevoked(string playerID, string perm)
  {
    if (perm != _ignorePermission) return;
    if (!ulong.TryParse(playerID, out var id)) return;
    _userIgnoreStates[id] = false;
  }

  [ConsoleCommand("psn.report")]
  private void CmdReport(ConsoleSystem.Arg arg)
  {
    if (arg is null) return;
    var player = arg.Player();
    if (player && !HasReportPermission(player)) return;

    _sb.Clear().Append(Name).Append(" statistics:");
    if (_statsIndexes.IsEmpty())
    {
      _sb.AppendLine(" [none yet]");
      arg.ReplyWith(_sb.ToString());
      _sb.Clear();
      return;
    }

    _sb.AppendLine().AppendLine();

    _sb.Append(' ').AppendPadded(
      nameof(StatColumn.HookAndPrefab), _columnWidths[0]);
    _sb.Append('|').AppendPadded(
      nameof(StatColumn.Ignored), _columnWidths[1]);
    _sb.Append('|').AppendPadded(
      nameof(StatColumn.Unmoved), _columnWidths[2]);
    _sb.Append('|').Append(nameof(StatColumn.Bounced)).AppendLine();
    for (var i = 0; i < _columnWidths.Length; ++i)
    {
      _sb.Append(i > 0 ? '+' : ' ').Append('-', _columnWidths[i]);
    }
    _sb.AppendLine();

    foreach (var (key, index) in _statsIndexes)
    {
      _sb.Append(' ').AppendPadded(key, _columnWidths[0]);
      for (var i = 0; i < 3; ++i)
      {
        _sb.Append('|').AppendPadded(
          _statsData[index + i], _columnWidths[i + 1]);
      }
      _sb.AppendLine();
    }

    _sb.AppendLine();
    arg.ReplyWith(_sb.ToString());
    _sb.Clear();
  }

  private object OnPlayerViolation(BasePlayer player, AntiHackType type)
  {
    // don't even acknowledge non-terrain violations
    if (AntiHackType.InsideTerrain != type) return null;
    if (ShouldIgnorePlayer(player))
    {
      RecordIgnored(TerrainViolationKey);
      return null;
    }
    var position = player.transform.position;
    if (ShouldMove(player) is not { } newY)
    {
      RecordUnmoved(TerrainViolationKey);
      return null;
    }
    if (_config.BounceLog)
    {
      _sb
        .Clear().Append("OnPlayerViolation(): Moving BasePlayer=")
        .Append(player.displayName).Append('(').Append(player.ToString())
        .Append(")@").AppendPosition(position).Append(" to new Y=").Append(newY)
        .Append(" (").Append(newY - position.y).Append(')');
      Puts(_sb.ToString());
      _sb.Clear();
    }
    player.Teleport(new Vector3(position.x, newY, position.z));
    RecordBounced(TerrainViolationKey);
    return _config.CancelTerrainViolation ? _nonNull : null;
  }

  private bool? CanDropActiveItem(BasePlayer player)
  {
    if (ShouldIgnorePlayer(player))
    {
      RecordIgnored(CanDropActiveItemKey);
      return null;
    }
    var position = player.transform.position;
    if (ShouldMove(player) is null)
    {
      RecordUnmoved(CanDropActiveItemKey);
      return null;
    }
    if (_config.BounceLog)
    {
      _sb
        .Clear()
        .Append("CanDropActiveItem(): Preventing item drop for BasePlayer=")
        .Append(player.displayName).Append('(').Append(player.ToString())
        .Append(")@").AppendPosition(position);
      Puts(_sb.ToString());
      _sb.Clear();
    }
    RecordBounced(CanDropActiveItemKey);
    return false;
  }

  private void OnEntitySpawned(BaseCorpse baseCorpse)
  {
    if (!_config.BounceBaseCorpse) return;
    // corpses must be checked on current frame, because parent entity will be
    //  lost after
    BounceEntity(baseCorpse);
  }

  private void OnEntitySpawned(DroppedItem droppedItem)
  {
    if (!_config.BounceDroppedItem) return;
    // dropped items must be checked on the next frame, because DroppedBy isn't
    //  set yet
    NextFrame(() => BounceEntity(droppedItem));
  }

  private void OnEntitySpawned(DroppedItemContainer droppedItemContainer)
  {
    if (!_config.BounceDroppedItemContainer) return;
    NextFrame(() => BounceEntity(droppedItemContainer));
  }

  private void OnEntitySpawned(HackableLockedCrate hackableLockedCrate)
  {
    if (!_config.BounceHackableLockedCrate) return;
    NextFrame(() => BounceEntity(hackableLockedCrate));
  }

  private void OnEntitySpawned(HelicopterDebris helicopterDebris)
  {
    if (!_config.BounceHelicopterDebris) return;
    timer.Once(Random.Range(0.5f, 1.5f), () => BounceEntity(helicopterDebris));
  }

  private void OnEntitySpawned(HorseCorpse horseCorpse)
  {
    if (!_config.BounceHorseCorpse) return;
    // corpses must be checked on current frame, because parent entity will be
    //  lost after
    BounceEntity(horseCorpse);
  }

  private void OnEntitySpawned(LockedByEntCrate lockedByEntCrate)
  {
    if (!_config.BounceLockedByEntCrate) return;
    timer.Once(Random.Range(0.5f, 1.5f), () => BounceEntity(lockedByEntCrate));
  }

  private void OnEntitySpawned(LootableCorpse lootableCorpse)
  {
    if (!_config.BounceLootableCorpse) return;
    // corpses must be checked on current frame, because parent entity will be
    //  lost after
    BounceEntity(lootableCorpse);
  }

  private void OnEntitySpawned(NPCPlayerCorpse npcPlayerCorpse)
  {
    if (!_config.BounceNpcPlayerCorpse) return;
    // corpses must be checked on current frame, because parent entity will be
    //  lost after
    BounceEntity(npcPlayerCorpse);
  }

  private void OnEntitySpawned(PlayerCorpse playerCorpse)
  {
    if (!_config.BouncePlayerCorpse) return;
    // corpses must be checked on current frame, because parent entity will be
    //  lost after
    BounceEntity(playerCorpse);
  }

  #endregion

  #region Config

  private sealed class PluginConfig
  {
    [JsonProperty(PropertyName = "Prevent held item/backpack drops on player bounce")]
    public bool PreventDrop { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce players on terrain violations")]
    public bool BounceTerrainViolation { get; set; } = true;

    [JsonProperty(PropertyName = "Cancel terrain violations on bounce")]
    public bool CancelTerrainViolation { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce BaseCorpse entities on spawn")]
    public bool BounceBaseCorpse { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce DroppedItem entities on spawn")]
    public bool BounceDroppedItem { get; set; } = false;

    [JsonProperty(PropertyName = "Bounce DroppedItemContainer entities on spawn")]
    public bool BounceDroppedItemContainer { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce HackableLockedCrate entities on spawn")]
    public bool BounceHackableLockedCrate { get; set; } = false;

    [JsonProperty(PropertyName = "Bounce HelicopterDebris entities on spawn")]
    public bool BounceHelicopterDebris { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce HorseCorpse entities on spawn")]
    public bool BounceHorseCorpse { get; set; } = false;

    [JsonProperty(PropertyName = "Bounce LockedByEntCrate entities on spawn")]
    public bool BounceLockedByEntCrate { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce LootableCorpse entities on spawn")]
    public bool BounceLootableCorpse { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce NPCPlayerCorpse entities on spawn")]
    public bool BounceNpcPlayerCorpse { get; set; } = true; //= false;

    [JsonProperty(PropertyName = "Bounce PlayerCorpse entities on spawn")]
    public bool BouncePlayerCorpse { get; set; } = true;

    [JsonProperty(PropertyName = "Log bounces")]
    public bool BounceLog { get; set; } = true;

    [JsonProperty(PropertyName = "Suppress bounce for entity prefabs")]
    public SortedSet<string> BounceIgnorePrefabs { get; set; } = new()
    {
      "item_drop_buoyant"
      // "player_corpse"
    };

    [JsonProperty(PropertyName = "Suppress logging for entity prefabs")]
    public SortedSet<string> LogIgnorePrefabs { get; set; } = new();
  }

  #endregion
}

file static class StringBuilderEx
{
  internal static StringBuilder AppendPosition(
    this StringBuilder sb, Vector3 position) =>
    sb.Append(position).Append('/').Append(
      MapHelper.PositionToString(position));
}

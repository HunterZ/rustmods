using Newtonsoft.Json;
using Rust;
using System.Collections.Generic;
using System;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Player Safety Net", "HunterZ", "0.0.6")]
public class PlayerSafetyNet : RustPlugin
{
  #region Data

  private const float WorldTop = 1000.0f;
  private const float WorldBottom = -500.0f;

  private const int RaycastHitsMax = 64;
  private readonly RaycastHit[] _raycastHits = new RaycastHit[RaycastHitsMax];
  private const float MaxDist = 2000f;

  private const int SolidLayerMask = Layers.Solid;

  private Collider _terrainCollider;
  private Collider _deepSeaBottomCollider;

  private readonly SortedDictionary<string, int> _statsIndexes = new();
  private readonly List<int> _statsData = new(300);
  private readonly int[] _newIgnored = { 1, 0, 0 };
  private readonly int[] _newUnmoved = { 0, 1, 0 };
  private readonly int[] _newBounced = { 0, 0, 1 };
  private readonly StringBuilder _sb = new();
  private const string OnPlayerDeathKey = "OnPlayerDeath";
  private const string CanDropActiveItemKey = "CanDropActiveItem";

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

  private PluginConfig _config;

  #endregion

  #region Helpers

  private const float MinSelfHitDistance = 0.001f;

  // return whether a raycast is of interest
  private (bool, Collider) ProcessHit(
    RaycastHit hit, bool up, Collider startCollider)
  {
    var collider = hit.collider;
    if (!collider) return (false, null);

    // special case: ignore bounces back onto the starting collider
    // this prevents scientists from sticking to the ceiling on large oilrig lol
    //
    // NOTE: the distance limit seems needed to allow stuff to bounce to the top
    //  of a collider that has another one immediately on top of it, which is
    //  usually preferable to leaving it untouched
    if (collider == startCollider && hit.distance < MinSelfHitDistance)
    {
      // PrintWarning($"Ignoring {(up ? "upward" : "downward")} hit on startCollider={startCollider}@{startCollider.transform.position} because hitDistance={hit.distance} is less than minDistance={MinSelfHitDistance}");
      return (false, startCollider);
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
        colliderName is "Add_To_Height" or "Collider" or "junkpile_base")
    {
      return (false, collider);
    }

    // try to ignore any entities that could be a corpse or its source
    // this prevents v2 scientists from doing sick flips when they die lol
    var cEntity = collider.ToBaseEntity();
    if (!cEntity) return (true, collider);

    var useEntity =
      !cEntity ||                                         // use non-Entity
      !cEntity.HasTrait(BaseEntity.TraitFlag.Alive) &&  // ignore living
      cEntity is not BaseCombatEntity { IsNpc: true }     // ignore NPCs
        and not BaseCorpse                                // ignore corpses
        and not RidableHorse;                             // ignore horses
    return (useEntity, collider);
  }

  // get Y coordinate of appropriate prefab, terrain, or world bound that would
  //  stop movement in the given direction from the given position
  //
  // returns null on invalid maxDistance or appropriate height not found
  private (float?, Collider) GetTerminalY(
    Vector3 position, float maxDistance, bool up, Collider startCollider)
  {
    var direction = up ? Vector3.up : Vector3.down;
    var cDist = MaxDist;
    float? cY = null;
    var cCollider = startCollider;

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
      var (use, collider) = ProcessHit(hit, up, startCollider);
      if (!use) continue;
      // best match so far; record it as a candidate
      cDist = hit.distance;
      cY = position.y + cDist * direction.y;
      cCollider = collider;
    }

    return (cY, cCollider);
  }

  private (float?, Collider) GetClosestSolidAbove(
    Vector3 position, Collider startCollider) =>
    GetTerminalY(position, WorldTop - position.y, true, startCollider);

  private (float?, Collider) GetClosestSolidBelow(
    Vector3 position, Collider startCollider) =>
    GetTerminalY(position, -WorldBottom + position.y, false, startCollider);

  // use raycasts to simulate bouncing something from the given position off of
  //  a suitable ceiling (including top of the world), then letting it fall back
  //  down onto a suitable floor (including terrain and prefabs)
  //
  // if a suitable floor was found, and it is above the given position, return
  //  its height; else return null
  private float? ShouldMove(Vector3 position)
  {
    // abort if in a holiday dungeon etc. for now
    // NOTE: need to allow < WorldBottom because kill check happens late
    if (position.y >= WorldTop) return null;

    var originalY = position.y;

    // allow only 2 iterations (see last-ditch effort comment below)
    for (var i = 0; i < 2; ++i)
    {
      // find ceiling or top of world above position
      var (upY, upCollider) = GetClosestSolidAbove(position, null);
      var ceilingY = upY ?? WorldTop;
      var ceilingPos = new Vector3(position.x, ceilingY, position.z);
      // find closest useful floor/terrain below ceiling
      var (floorY, downCollider) = GetClosestSolidBelow(ceilingPos, upCollider);
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

      // try again starting from detected ceiling
      // this is a last-ditch effort in case player fell out of a hole in the
      //  world with a ceiling above, e.g. train tunnels entrance on a primitive
      //  map in non-primitive mode
      PrintWarning($"No floor found, but ceiling is below world top; trying again starting at ceiling position={ceilingPos}");
      position = ceilingPos;
    }

    return null;
  }

  // move a non-player entity to a new Y position
  private static void MoveEntityY(BaseCombatEntity entity, float newY)
  {
    var oldPos = entity.ServerWorldPosition;
    // note: this should trigger a network update automatically if appropriate
    entity.ServerWorldPosition = new Vector3(oldPos.x, newY, oldPos.z);
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

  private void BounceEntity<T>(T entity) where T : BaseCombatEntity
  {
    if (!entity) return;
    var entitySignature = $"{entity.GetType()}:{entity.ShortPrefabName}";
    if (entity.HasParent() ||
        _config.BounceIgnorePrefabs.Contains(entity.PrefabName))
    {
      RecordIgnored(entitySignature);
      return;
    }
    var position = entity.transform.position;
    if (ShouldMove(position) is not { } newY)
    {
      RecordUnmoved(entitySignature);
      return;
    }
    if (_config.BounceLog &&
        !_config.LogIgnorePrefabs.Contains(entity.PrefabName))
    {
      // use StringBuilder scratchpad to build bounce logs, because string
      //  interpolation would perform extra heap allocations when converting
      //  numeric primitive values to string representations
      _sb
        .Clear().Append("Moving ").Append(entitySignature).Append(" at ")
        .AppendPosition(position).Append(" to new Y=").Append(newY).Append(" (")
        .Append(newY - position.y).Append(')');
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

    if (!_config.BouncePlayer)
    {
      Unsubscribe(nameof(OnPlayerDeath));
    }
    if (!_config.PreventDrop)
    {
      Unsubscribe(nameof(CanDropActiveItem));
    }
    if (!_config.BounceBaseCorpse &&
        !_config.BounceDroppedItemContainer &&
        !_config.BounceHelicopterDebris &&
        !_config.BounceHorseCorpse &&
        !_config.BounceLootableCorpse &&
        !_config.BounceNpcPlayerCorpse &&
        !_config.BouncePlayerCorpse)
    {
      Unsubscribe(nameof(OnEntitySpawned));
    }
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

  [ConsoleCommand("psn.report")]
  private void CmdReport(ConsoleSystem.Arg arg)
  {
    _sb.Clear().Append(Name).Append(" statistics:");
    if (_statsIndexes.IsEmpty())
    {
      _sb.AppendLine(" [none yet]");
      Puts(_sb.ToString());
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
    Puts(_sb.ToString());
    _sb.Clear();
  }

  private object OnPlayerDeath(BasePlayer player, HitInfo info)
  {
    if (player?.userID.IsSteamId() is not true)
    {
      RecordIgnored(OnPlayerDeathKey);
      return null;
    }
    var position = player.transform.position;
    if (ShouldMove(position) is not { } newY)
    {
      RecordUnmoved(OnPlayerDeathKey);
      return null;
    }
    if (_config.BounceLog)
    {
      _sb
        .Clear().Append("Moving BasePlayer=").Append(player.displayName)
        .Append('(').Append(player.ToString()).Append(")@")
        .AppendPosition(position).Append(" to new Y=").Append(newY).Append(" (")
        .Append(newY - position.y).Append(')');
      Puts(_sb.ToString());
      _sb.Clear();
    }
    player.Teleport(new Vector3(position.x, newY, position.z));
    RecordBounced(OnPlayerDeathKey);
    return null;
  }

  // silently ignore NPCPlayer deaths to prevent triggering
  //  OnPlayerDeath(BasePlayer)
  private object OnPlayerDeath(NPCPlayer npcPlayer, HitInfo info) => null;

  private bool? CanDropActiveItem(BasePlayer player)
  {
    if (player?.userID.IsSteamId() is not true)
    {
      RecordIgnored(CanDropActiveItemKey);
      return null;
    }
    var position = player.transform.position;
    if (ShouldMove(position) is null)
    {
      RecordUnmoved(CanDropActiveItemKey);
      return null;
    }
    if (_config.BounceLog)
    {
      _sb
        .Clear().Append("Preventing item drop for BasePlayer=")
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
    if (_config.BounceBaseCorpse) return;
    NextTick(() => BounceEntity(baseCorpse));
  }

  private void OnEntitySpawned(DroppedItemContainer droppedItemContainer)
  {
    if (!_config.BounceDroppedItemContainer) return;
    NextTick(() => BounceEntity(droppedItemContainer));
  }

  private void OnEntitySpawned(HelicopterDebris helicopterDebris)
  {
    if (!_config.BounceHelicopterDebris) return;
    NextTick(() => BounceEntity(helicopterDebris));
  }

  private void OnEntitySpawned(HorseCorpse horseCorpse)
  {
    if (!_config.BounceHorseCorpse) return;
    NextTick(() => BounceEntity(horseCorpse));
  }

  private void OnEntitySpawned(LootableCorpse lootableCorpse)
  {
    if (!_config.BounceLootableCorpse) return;
    NextTick(() => BounceEntity(lootableCorpse));
  }

  private void OnEntitySpawned(NPCPlayerCorpse npcPlayerCorpse)
  {
    if (!_config.BounceNpcPlayerCorpse) return;
    NextTick(() => BounceEntity(npcPlayerCorpse));
  }

  private void OnEntitySpawned(PlayerCorpse playerCorpse)
  {
    if (!_config.BouncePlayerCorpse) return;
    NextTick(() => BounceEntity(playerCorpse));
  }

  #endregion

  #region Config

  private sealed class PluginConfig
  {
    [JsonProperty(PropertyName = "Bounce players on death")]
    public bool BouncePlayer { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce held item/backpack drops on player death")]
    public bool PreventDrop { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce BaseCorpse entities on spawn")]
    public bool BounceBaseCorpse { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce DroppedItemContainer entities on spawn")]
    public bool BounceDroppedItemContainer { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce HelicopterDebris entities on spawn")]
    public bool BounceHelicopterDebris { get; set; } = true;

    [JsonProperty(PropertyName = "Bounce HorseCorpse entities on spawn")]
    public bool BounceHorseCorpse { get; set; } = false;

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

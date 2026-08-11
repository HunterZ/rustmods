using Newtonsoft.Json;
using Rust.Ai.Gen2;
using Rust;
using System.Collections.Generic;
using System;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Player Safety Net", "HunterZ", "0.0.2")]
public class PlayerSafetyNet : RustPlugin
{
  #region Data

  private const float WorldTop = 1000.0f;
  private const float WorldBottom = -500.0f;

  private const int RaycastHitsMax = 64;
  private readonly RaycastHit[] _raycastHits = new RaycastHit[RaycastHitsMax];
  private const float MaxDist = 10000f;

  private const int SolidLayerMask =
    Layers.Solid;

  private enum Match { Any, Closest, Farthest }

  private enum Blocking { Ignore, IgnoreCliff, Include, Require, RequireCliff }

  private readonly SortedDictionary<string, (ulong, ulong)> _stats = new();
  private readonly StringBuilder _sb = new();

  private PluginConfig _config;

  #endregion

  #region Helpers

  private static bool IsFormation(string name) =>
    true == name?.StartsWith("cliff") ||
    true == name?.StartsWith("rock_formation_");

  private static bool IsBlocking(RaycastHit hit, string name) =>
    IsFormation(name) ||
    hit.IsOnLayer(Layer.Construction) ||
    hit.IsOnLayer(Layer.Deployed);

  // get Y coordinate of appropriate prefab, terrain, or world bound that would
  //  stop movement in the given direction from the given position
  //
  // returns null on invalid maxDistance or appropriate height not found
  private float? GetTerminalY(
    Vector3 position, Match match, float maxDistance, Blocking blocking,
    int mask, bool up)
  {
    var down = !up;
    var direction = up ? Vector3.up : Vector3.down;
    var cDist = Match.Closest == match ? MaxDist : -MaxDist;
    float? cY = null;
    // optimization: abort if distance is non-positive
    if (maxDistance <= 0)
    {
      return null;
    }
    var hitCount = Physics.RaycastNonAlloc(
      position, direction, _raycastHits, maxDistance, mask);
    if (hitCount >= _raycastHits.Length)
    {
      PrintWarning($"GetWorldDistanceAndY(): Raycast hit count at or above configured maximum ({hitCount}/{_raycastHits.Length})");
    }
    for (var i = 0; i < hitCount; ++i)
    {
      var hit = _raycastHits[i];
      var collider = hit.collider;
      // try to ignore any entities that could be the source of a corpse that is
      //  being spawned - otherwise oil rig scientists tend to do flips, stick
      //  to ceilings, etc. for some reason lol
      var cEntity = collider?.ToBaseEntity();
      if (cEntity && cEntity is
          BaseCombatEntity {lifestate: BaseCombatEntity.LifeState.Dead} or
          BaseNpc or BaseNPC2 or BasePlayer or RidableHorse)
      {
        continue;
      }
      // ignore terrain if hit is inside a terrain ignore volume or whatever
      // NOTE: GetIgnore() handles checking whether the hit is a terrain one, so
      //  there's no point doing it here as well
      if (down && TerrainMeta.Collision.GetIgnore(hit))
      {
        continue;
      }
      var colliderName = collider?.name;
      switch (colliderName)
      {
        case null:
        case { Length: 0 }:
          break;
        case "Add_To_Height":
        case "Collider":
        // only ignore terrain colliders when looking up
        case "DeepSea_Bottom" when up:
        case "Terrain"        when up:
        case "assets/prefabs/player/player.prefab":
        case "junkpile_base":
          continue;
      }
      switch (blocking)
      {
        case Blocking.Ignore:
          if (IsBlocking(hit, colliderName)) continue;
          break;
        case Blocking.IgnoreCliff:
          if (IsFormation(colliderName)) continue;
          break;
        case Blocking.Include:
          break;
        case Blocking.Require:
          if (!IsBlocking(hit, colliderName)) continue;
          break;
        case Blocking.RequireCliff:
          if (!IsFormation(colliderName)) continue;
          break;
      }
      switch (match)
      {
        case Match.Any:
          cDist = hit.distance;
          cY = position.y + cDist * direction.y;
          return cY;
        case Match.Closest when hit.distance >= cDist:
        case Match.Farthest when hit.distance <= cDist:
          continue;
      }
      // best match so far; record it as a candidate
      cDist = hit.distance;
      cY = position.y + cDist * direction.y;
    }

    return cY;
  }

  private float GetClosestSolidAbove(Vector3 position) => GetTerminalY(
    position, Match.Closest, WorldTop - position.y, Blocking.Include,
    SolidLayerMask, true) ?? WorldTop;

  private float? GetClosestSolidBelow(Vector3 position) => GetTerminalY(
    position, Match.Closest, -WorldBottom + position.y, Blocking.Include,
    SolidLayerMask, false);

  // finds closest ceiling or top of world above position, then finds closest
  //  appropriate floor below that
  //
  // returns null unless a floor was found whose Y coordinate is above
  //  position.y
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
      var ceilingY = GetClosestSolidAbove(position);
      var ceilingPos = new Vector3(position.x, ceilingY, position.z);
      // find closest useful floor/terrain below ceiling
      if (GetClosestSolidBelow(ceilingPos) is { } floorY && floorY <= ceilingY)
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

  private void RecordLook(string key, bool bounced = false)
  {
    var bouncedNum = bounced ? 1UL : 0UL;
    var oldLooked = 0UL;
    var oldBounced = 0UL;
    if (_stats.TryGetValue(key, out var value))
    {
      (oldLooked, oldBounced) = value;
    }
    _stats[key] = (oldLooked + 1UL, oldBounced + bouncedNum);
  }

  private void RecordBounce(string key) => RecordLook(key, true);

  private static string ToString(Vector3 position) =>
    $"{position}/{MapHelper.PositionToString(position)}";

  private void BounceEntity<T>(T entity) where T : BaseCombatEntity
  {
    if (!entity || entity.HasParent() ||
        _config.BounceIgnorePrefabs.Contains(entity.PrefabName))
    {
      return;
    }
    var position = entity.transform.position;
    var entitySignature = $"{entity.GetType()}:{entity.ShortPrefabName}";
    if (ShouldMove(position) is not { } newY)
    {
      RecordLook(entitySignature);
      return;
    }
    if (_config.BounceLog &&
        !_config.LogIgnorePrefabs.Contains(entity.PrefabName))
    {
      Puts($"Moving {entitySignature} at {ToString(position)} to new Y={newY} ({newY - position.y})");
    }
    MoveEntityY(entity, newY);
    RecordBounce(entitySignature);
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
  }

  private void Unload()
  {
  }

  [ConsoleCommand("psn.report")]
  private void CmdReport(ConsoleSystem.Arg arg)
  {
    _sb.Clear().Append(Name).Append(" statistics (looked/bounced):");
    if (_stats.IsEmpty())
    {
      _sb.AppendLine(" <none yet>");
      Puts(_sb.ToString());
      _sb.Clear();
      return;
    }
    foreach (var (key, (looked, bounced)) in _stats)
    {
      _sb
        .AppendLine().Append(' ')
        .Append(key).Append(":\t").Append(looked).Append(" / ").Append(bounced);
    }
    _sb.AppendLine();
    Puts(_sb.ToString());
    _sb.Clear();
  }

  private object OnPlayerDeath(BasePlayer player, HitInfo info)
  {
    if (player?.userID.IsSteamId() is not true) return null;
    var position = player.transform.position;
    if (ShouldMove(position) is not { } newY)
    {
      RecordLook("OnPlayerDeath");
      return null;
    }
    if (_config.BounceLog)
    {
      Puts($"Moving BasePlayer={player.displayName}({player})@{ToString(position)} to new Y={newY} ({newY - position.y})");
    }
    player.Teleport(new Vector3(position.x, newY, position.z));
    RecordBounce("OnPlayerDeath");
    return null;
  }

  private bool? CanDropActiveItem(BasePlayer player)
  {
    if (player?.userID.IsSteamId() is not true) return null;
    var position = player.transform.position;
    if (ShouldMove(position) is null)
    {
      RecordLook("CanDropActiveItem");
      return null;
    }
    if (_config.BounceLog)
    {
      Puts($"Preventing item drop for BasePlayer={player.displayName}({player})@{ToString(position)}");
    }
    RecordBounce("CanDropActiveItem");
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
    public bool BounceHorseCorpse { get; set; } = true; //= false;

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
      // "item_drop_buoyant",
      // "player_corpse"
    };

    [JsonProperty(PropertyName = "Suppress logging for entity prefabs")]
    public SortedSet<string> LogIgnorePrefabs { get; set; } = new();
  }

  #endregion
}

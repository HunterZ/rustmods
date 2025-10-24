using Facepunch;
using System.Collections.Generic;
using UnityEngine;

// ReSharper disable once CheckNamespace
namespace Oxide.Plugins;

[Info("ScarecrowWrangler", "HunterZ", "1.0.0")]
public class ScarecrowWrangler : RustPlugin
{
  // TODO: move to config file, and also add safe zone wrangling toggle there
  private static readonly int Mask =
    (int)TerrainTopology.Enum.Ocean |
    (int)TerrainTopology.Enum.Tier0;

  private static readonly HashSet<ScarecrowNPC> WatchedScarecrows = new();
  private static readonly List<Vector3> ValidLocations = new();
  private static ushort ValidMissCount { get; set; }

  #region Plugin API

  private void Init()
  {
    WatchedScarecrows.Clear();
    ValidLocations.Clear();
    ValidMissCount = 0;
    ScarecrowWatcher.Instance = this;
  }

  private void OnServerInitialized()
  {
    ulong count = 0;
    // TODO: this should probably be a coroutine
    foreach (var serverEntity in BaseNetworkable.serverEntities)
    {
      if (serverEntity is not ScarecrowNPC scarecrow) continue;
      OnEntitySpawned(scarecrow);
      ++count;
    }
    Puts($"Completed startup processing for {count} scarecrow(s)");
  }

  private void Unload()
  {
    ulong count = 0;
    foreach (var serverEntity in BaseNetworkable.serverEntities)
    {
      if (serverEntity is not ScarecrowNPC scarecrow) continue;
      DestroyWatcher(scarecrow);
      ++count;
    }
    Puts($"Completed shutdown processing for {count} scarecrow(s)");
    ScarecrowWatcher.Instance = null;
    WatchedScarecrows.Clear();
    ValidLocations.Clear();
    ValidMissCount = 0;
  }

  #endregion Plugin API

  #region Hook Handlers

  private void OnEntitySpawned(ScarecrowNPC scarecrow)
  {
    if (scarecrow is null || scarecrow.IsDestroyed || scarecrow.IsDead())
    {
      return;
    }

    // ignore scarecrows that look like they're in portal dungeons, because we
    //  don't want to do any of the following:
    // - teleport them out of dungeons
    // - waste resources tracking them
    // - teleport other zombies into dungeons or empty space
    // this is done up-front because it's cheap to detect
    if (IsInDungeon(scarecrow.transform.position))
    {
      Puts($"Ignoring scarecrow at location {scarecrow.transform.position} because it's probably in a portal dungeon");
      return;
    }
    if (IsInForbiddenLocation(scarecrow))
    {
      // abort if scarecrow was destroyed
      if (!Wrangle(scarecrow)) return;
      // scarecrow was moved
    }
    else
    {
      // scarecrow spawned in or moved to a good spot; record position
      AddValidLocation(scarecrow.transform.position);
    }
    // keep an eye on this scarecrow as it wanders the map
    NextTick(() => CreateWatcher(scarecrow));
  }

  #endregion Hook Handlers

  #region Utilities

  private static void AddValidLocation(Vector3 pos)
  {
    // don't even check if we've failed to add a new location 100 times since
    //  the last success
    if (ValidMissCount >= 100) return;
    // see if this is a new location of interest
    foreach (var goodPos in ValidLocations)
    {
      // skip anything far enough away
      if (Vector3.Distance(goodPos, pos) >= 100.0f) continue;
      // too close; add to miss count and abort
      ++ValidMissCount;
      return;
    }
    // add new known-good location
    ValidLocations.Add(pos);
    ValidMissCount = 0;
  }

  private static void CreateWatcher(ScarecrowNPC scarecrow)
  {
    if (!scarecrow.TryGetComponent(out ScarecrowWatcher watcher))
    {
      watcher = scarecrow.gameObject.AddComponent<ScarecrowWatcher>();
    }
    watcher.Init(scarecrow);
    watcher.StartWatching();
  }

  private static void DestroyWatcher(ScarecrowNPC scarecrow) =>
    scarecrow.GetComponent<ScarecrowWatcher>()?.OnDestroy();

  private static string GetGrid(Vector3 pos) =>
    MapHelper.PositionToString(pos);

  private static bool IsInDungeon(Vector3 location) => location.y > 1000.0f;

  private static bool IsInForbiddenLocation(ScarecrowNPC scarecrow) =>
    0 != (TerrainMeta.TopologyMap.GetTopology(
      scarecrow.transform.position) & Mask) ||
    scarecrow.InSafeZone();

  private void Relocate(
    ScarecrowNPC scarecrow, Vector3 newPosition, bool quiet)
  {
    if (!quiet)
    {
      Effect.server.Run("assets/prefabs/npc/patrol helicopter/effects/rocket_fire.prefab", scarecrow.transform.position);
    }
    // this is needed to prevent a scarecrow from teleporting back if it's
    //  chasing a player
    scarecrow.MovePosition(newPosition);
    ResetBrain(scarecrow, false);
    NextTick(() =>
    {
      if (!quiet)
      {
        Effect.server.Run("assets/prefabs/npc/patrol helicopter/effects/rocket_fire.prefab", newPosition);
      }
      ResetBrain(scarecrow, true);
    });
  }

  private static void ResetBrain(ScarecrowNPC scarecrow, bool movementTick)
  {
    if (scarecrow is null) return;
    scarecrow.StopAttacking();
    var brain = scarecrow.Brain;
    if (brain is null) return;
    if (!movementTick) brain.StopMovementTick();
    var defaultStateContainer = brain.AIDesign?.GetDefaultStateContainer();
    if (null != defaultStateContainer)
    {
      brain.SwitchToState(
        defaultStateContainer.State, defaultStateContainer.ID);
    }
    if (movementTick) brain.StartMovementTick();
  }

  private bool Wrangle(ScarecrowNPC scarecrow, bool quiet = true)
  {
    // abort if scarecrow is in a valid location
    if (!IsInForbiddenLocation(scarecrow)) return true;

    // kill if no known-good positions available
    // NOTE: this should only happen during initial plugin load, and more
    //  scarecrows will eventually spawn to recover things
    if (ValidLocations.Count <= 0)
    {
      PrintWarning($"Killing scarecrow {scarecrow.net.ID} at forbidden location {scarecrow.transform.position}/{GetGrid(scarecrow.transform.position)} due to failure to no known-good location(s) available");
      if (!quiet)
      {
        Effect.server.Run("assets/prefabs/npc/murderer/sound/death.prefab", scarecrow.transform.position);
      }
      // NextTick is needed to avoid errors
      NextTick(scarecrow.KillMessage);
      return false;
    }

    // find known-good location with the largest minimum distance from all
    //  other zombies
    //
    //  first, get the current locations of all watched zombies
    var watchedLocs = Pool.Get<List<Vector3>>();
    foreach (var watchedScarecrow in WatchedScarecrows)
    {
      // skip non-scarecrows, plus the one being moved
      if (watchedScarecrow.Equals(scarecrow)) continue;
      watchedLocs.Add(watchedScarecrow.transform.position);
    }
    // now find the distance from each known-good point to its closest zombie,
    //  and record it as a candidate if that distance is the farthest seen
    var farLoc = scarecrow.transform.position;
    var farDist = 0.0;
    foreach (var validLoc in ValidLocations)
    {
      // ignore any known-good locations that are too close to the zombie
      //  under evaluation
      // otherwise zombies tend to keep wandering into a bad location from a
      //  nearby spawn location (TODO: maybe just remove such locations from
      //  ValidLocations?)
      if (Vector3.Distance(scarecrow.transform.position, validLoc) < 100.0f)
      {
        continue;
      }
      var closestDist = double.MaxValue;
      foreach (var watchedLoc in watchedLocs)
      {
        var curDist = Vector3.Distance(watchedLoc, validLoc);
        // Puts($"***** \tlocation={validLoc}: distance to scarecrow@{sLoc}={curDist}");
        if (curDist < closestDist)
        {
          // this zombie is closer to the known-good location than any we've
          //  seen so far; record distance
          closestDist = curDist;
        }
      }
      // Puts($"***** location={validLoc}: closest scarecrow distance={closestDist}");

      if (closestDist <= farDist) continue;

      // this known-good location is the farthest from its closest zombie
      //  that we've seen so far; record it as a candidate
      farLoc = validLoc;
      farDist = closestDist;
    }
    Pool.FreeUnmanaged(ref watchedLocs);

    if (farDist >= 100.0f)
    {
      PrintWarning($"Relocating scarecrow {scarecrow.net.ID} from forbidden location {scarecrow.transform.position}/{GetGrid(scarecrow.transform.position)} to known-good location {farLoc}/{GetGrid(farLoc)} with distance {farDist} to closest scarecrow");
      Relocate(scarecrow, farLoc, quiet);
      return true;
    }

    // pick a random known-good location
    var randomIndex = Random.Range(0, ValidLocations.Count - 1);
    var newLoc = ValidLocations[randomIndex];
    PrintWarning($"Relocating scarecrow {scarecrow.net.ID} from forbidden location {scarecrow.transform.position}/{GetGrid(scarecrow.transform.position)} to known-good location {newLoc}/{GetGrid(newLoc)} at random index {randomIndex}/{ValidLocations.Count}");
    Relocate(scarecrow, newLoc, quiet);
    return true;
  }

  #endregion Utilities

  #region Subclasses

  private sealed class ScarecrowWatcher : FacepunchBehaviour
  {
    public static ScarecrowWrangler Instance { get; set; }

    private ScarecrowNPC _scarecrow;

    public void Init(ScarecrowNPC scarecrow = null)
    {
      _scarecrow = scarecrow;
      if (scarecrow)
      {
        WatchedScarecrows.Add(scarecrow);
      }
      else if (_scarecrow)
      {
        WatchedScarecrows.Remove(_scarecrow);
      }
      else
      {
        Instance.PrintWarning("ScarecrowWatcher.Init(): Failed to update WatchedScarecrows because both scarecrow references are null");
      }
    }

    public void OnDestroy()
    {
      CancelInvoke();
      Init();
      Destroy(this);
    }

    public void StartWatching(float repeatRate = 2.0f) =>
      InvokeRepeating(nameof(Watch), 0.0f, repeatRate);

    public void Watch() => Instance?.Wrangle(_scarecrow);
  }

  #endregion Subclasses
}

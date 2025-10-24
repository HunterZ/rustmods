using System.Collections.Generic;
using Facepunch;
using UnityEngine;

namespace Oxide.Plugins
{
  [Info("ScarecrowWrangler", "HunterZ", "1.0.0")]
  public class ScarecrowWrangler : RustPlugin
  {
    private static readonly int Mask =
      (int)TerrainTopology.Enum.Ocean |
      (int)TerrainTopology.Enum.Tier0;

    private static readonly HashSet<Vector3> _validLocations = new();

    #region Plugin API

    private void Init()
    {
      _validLocations.Clear();
      ScarecrowWatcher.Instance = this;
    }

    private void OnServerInitialized()
    {
      ulong count = 0;
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
      _validLocations.Clear();
    }

    #endregion Plugin API

    #region Hook Handlers

    private void OnEntitySpawned(ScarecrowNPC scarecrow)
    {
      if (null == scarecrow || scarecrow.IsDestroyed || scarecrow.IsDead())
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
      }
      else
      {
        // scarecrow spawned in a good spot; record it for reuse
        _validLocations.Add(scarecrow.transform.position);
      }
      // keep an eye on this scarecrow as it wanders the map
      NextTick(() => CreateWatcher(scarecrow));
    }

    #endregion Hook Handlers

    #region Utilities

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

    // Credit: Lorenzo - https://umod.org/community/rust/4861-calculate-current-coordinate-of-player?page=1#post-3
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
      if (null == scarecrow) return;
      scarecrow.StopAttacking();
      var brain = scarecrow.Brain;
      if (null == brain) return;
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
      if (_validLocations.Count <= 0)
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
      //  first, get the locations of all zombies on the server
      var sLocs = Pool.Get<List<Vector3>>();
      foreach (var serverEntity in BaseNetworkable.serverEntities)
      {
        // skip non-scarecrows, plus the one being moved
        if (serverEntity is not ScarecrowNPC sc || sc.Equals(scarecrow))
        {
          continue;
        }
        sLocs.Add(sc.transform.position);
      }
      // now find the distance from each known-good point to its closest zombie,
      //  and record it as a candidate if that distance is the farthest seen
      var farLoc = scarecrow.transform.position;
      var farDist = 0.0;
      foreach (var validLoc in _validLocations)
      {
        var closestDist = double.MaxValue;
        foreach (var sLoc in sLocs)
        {
          var curDist = Vector3.Distance(sLoc, validLoc);
          // Puts($"***** \tlocation={validLoc}, distance to scarecrow@{sLoc}={curDist}");
          if (curDist < closestDist)
          {
            // this zombie is closer to the known-good location than any we've
            //  seen so far; record distance
            closestDist = curDist;
          }
        }
        // Puts($"***** location={validLoc} closest scarecrow distance={closestDist}");

        if (closestDist <= farDist) continue;

        // this known-good location is the farthest from its closest zombie
        //  that we've seen so far; record it as a candidate
        farLoc = validLoc;
        farDist = closestDist;
      }

      if (farDist > 0.0)
      {
        PrintWarning($"Relocating scarecrow {scarecrow.net.ID} from forbidden location {scarecrow.transform.position}/{GetGrid(scarecrow.transform.position)} to known-good location {farLoc}/{GetGrid(farLoc)} with distance {farDist} to closest scarecrow");
        Relocate(scarecrow, farLoc, quiet);
        return true;
      }

/*
      // find known-good location with the furthest average distance from all
      //  zombies
      //
      //  first, get the locations of all zombies on the server
      var sLocs = Pool.Get<List<Vector3>>();
      foreach (var serverEntity in BaseNetworkable.serverEntities)
      {
        // skip non-scarecrows, plus the one being moved
        if (serverEntity is not ScarecrowNPC sc || sc.Equals(scarecrow))
        {
          continue;
        }
        sLocs.Add(sc.transform.position);
      }
      // now calculate the average zombie distance to each known-good location
      var farLoc = scarecrow.transform.position;
      var farDist = -1.0;
      foreach (var vLoc in _validLocations)
      {
        var curDist = 0.0;
        foreach (var sLoc in sLocs)
        {
          curDist += Vector3.Distance(vLoc, sLoc) / sLocs.Count;
        }
        if (curDist <= farDist) continue;
        farLoc = vLoc;
        farDist = curDist;
      }
      Pool.FreeUnmanaged(ref sLocs);

      if (farDist > 0.0)
      {
        PrintWarning($"Relocating scarecrow {scarecrow.net.ID} from forbidden location {scarecrow.transform.position}/{GetGrid(scarecrow.transform.position)} to known-good location {farLoc}/{GetGrid(farLoc)} with average distance {farDist} from all other zombies");
        Relocate(scarecrow, farLoc, quiet);
        return true;
      }
*/
      // pick a random known-good location
      var randomIndex = Random.Range(0, _validLocations.Count - 1);
      var i = 0;
      foreach (var location in _validLocations)
      {
        if (i == randomIndex)
        {
          PrintWarning($"Relocating scarecrow {scarecrow.net.ID} from forbidden location {scarecrow.transform.position}/{GetGrid(scarecrow.transform.position)} to known-good location {location}/{GetGrid(location)} at random index {i}/{_validLocations.Count}");
          Relocate(scarecrow, location, quiet);
          return true;
        }
        ++i;
      }

      // pathological
      PrintWarning("Failed to determine a scarecrow relocation target");
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
}

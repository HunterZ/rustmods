using Facepunch;
using System.Collections.Generic;
using UnityEngine;

// ReSharper disable once CheckNamespace
namespace Oxide.Plugins;

[Info("ScarecrowWrangler", "HunterZ", "1.0.0")]
public class ScarecrowWrangler : RustPlugin
{
  // mask containing forbidden topologies
  // TODO: move to config file, and also add safe zone wrangling toggle there
  private static readonly int Mask =
    (int)TerrainTopology.Enum.Ocean |
    (int)TerrainTopology.Enum.Tier0;

  private class ValidLocation
  {
    public float            closestDistance = float.MaxValue;
    public ScarecrowWatcher closestWatcher;
    public readonly Vector3 position        = Vector3.zero;

    public ValidLocation() { }

    public ValidLocation(Vector3 pos) => position = pos;
  }

  // all scarecrow watchers being managed by this plugin
  private readonly HashSet<ScarecrowWatcher> _watchers = new();
  // all known-good scarecrow spawn locations, for use as teleport destinations
  private readonly List<ValidLocation> _validLocations = new();
  // number of scarecrow spawns since last new known-good location was recorded
  private ushort _validMissCount;

  #region Plugin API

  private void Init()
  {
    _watchers.Clear();
    _validLocations.Clear();
    _validMissCount = 0;
  }

  private void OnServerInitialized()
  {
    // scan all server entities to find and watch scarecrows as appropriate
    // TODO: this should probably be a coroutine
    ulong count = 0;
    foreach (var serverEntity in BaseNetworkable.serverEntities)
    {
      if (serverEntity is not ScarecrowNPC scarecrow) continue;
      // treat as a spawn, since it's new to us
      // note that this does mean that current arbitrary scarecrow locations may
      //  be recorded as teleport destinations - seems fine idk
      OnEntitySpawned(scarecrow);
      ++count;
    }
    Puts($"Completed startup processing for {count} scarecrow(s); recorded {_validLocations.Count} initial known-good location(s)");
  }

  private void Unload()
  {
    // schedule all managed watchers for destruction
    // copy list because the watchers will modify the main one on destruction
    var watcherList = Pool.Get<List<ScarecrowWatcher>>();
    watcherList.AddRange(_watchers);
    var countIn = _watchers.Count;
    foreach (var watcher in watcherList)
    {
      if (!watcher) continue;
      DestroyWatcher(watcher);
    }
    var countOut = _watchers.Count;
    Pool.FreeUnmanaged(ref watcherList);
    Puts($"Completed shutdown processing; watcher count: {countIn} -> {countOut}");
    _watchers.Clear();
    _validLocations.Clear();
    _validMissCount = 0;
  }

  #endregion Plugin API

  #region Hook Handlers

  private void OnEntityDeath(ScarecrowNPC scarecrow)
  {
    if (!IsValid(scarecrow)) return;
    DestroyWatchers(scarecrow);
  }

  private void OnEntityKill(ScarecrowNPC scarecrow) => OnEntityDeath(scarecrow);

  private void OnEntitySpawned(ScarecrowNPC scarecrow)
  {
    if (!IsAlive(scarecrow)) return;

    // holiday dungeon scarecrows must be exempted from all processing
    if (IsInDungeon(scarecrow.transform.position))
    {
      // Puts($"Ignoring scarecrow {Print(scarecrow)} because it's probably in a portal dungeon");
      return;
    }
    if (IsInForbiddenLocation(scarecrow))
    {
      // attempt to move scarecrow immediately, and abort if it was destroyed
      // this avoids the overhead of creating a watcher only for it to get
      //  immediately destroyed, in the corner case that no suitable teleport
      //  location could be found
      if (!Wrangle(scarecrow)) return;
      // else scarecrow was moved
    }
    else
    {
      // spawned in a good position; record if appropriate
      AddValidLocation(scarecrow.transform.position);
    }
    // keep an eye on this scarecrow as it wanders the map!
    NextTick(() => CreateWatcher(scarecrow));
  }

  #endregion Hook Handlers

  #region Utilities

  // evaluate scarecrow spawn location, recording it as a known-good teleport
  // destination if appropriate
  // note that it is not necessary to call IsInForbiddenLocation() because this
  //  will have already been done by OnEntitySpawned()
  private void AddValidLocation(Vector3 pos)
  {
    // don't even check if we've failed to add a new location 100 times since
    //  the last success, because we've probably found them all
    if (_validMissCount >= 100) return;
    // see if this is a new location of interest
    foreach (var goodPos in _validLocations)
    {
      // move on if far enough away
      if (Vector3.Distance(goodPos.position, pos) >= 100.0f) continue;
      // too close; add to miss count and abort
      ++_validMissCount;
      return;
    }
    // add new known-good location and reset miss counter
    _validLocations.Add(new ValidLocation(pos));
    _validMissCount = 0;
  }

  // add a watcher to given scarecrow
  private void CreateWatcher(ScarecrowNPC scarecrow)
  {
    if (!IsAlive(scarecrow)) return;

    // destroy any old watchers in case of prior dirty unload / plugin crash
    DestroyWatchers(scarecrow);

    var watcher = scarecrow.gameObject.AddComponent<ScarecrowWatcher>();
    // Puts($"Adding new watcher {watcher.GetInstanceID()} to scarecrow {Print(scarecrow)}");
    watcher.Destroying = false;
    watcher.Instance = this;
    watcher.StartWatching();
  }

  // destroy all watchers on given scarecrow
  private void DestroyWatchers(ScarecrowNPC scarecrow)
  {
    if (!IsValid(scarecrow)) return;
    var watchers = Pool.Get<List<ScarecrowWatcher>>();
    scarecrow.GetComponents(watchers);
    foreach (var watcher in watchers)
    {
      DestroyWatcher(watcher);
    }
    Pool.FreeUnmanaged(ref watchers);
  }

  // destroy given watcher
  private void DestroyWatcher(ScarecrowWatcher watcher)
  {
    if (!watcher || watcher.Destroying) return;
    Puts($"Destroying watcher {watcher.GetInstanceID()} on scarecrow {Print(watcher.GetScarecrow())}");
    // remove watcher from valid location data if present
    // this avoids the location getting stuck on a defunct scarecrow
    foreach (var validLoc in _validLocations)
    {
      if (validLoc.closestWatcher != watcher) continue;
      validLoc.closestDistance = float.MaxValue;
      validLoc.closestWatcher = null;
    }
    // Destroy() is not immediate, so take some steps now to avoid confusion
    watcher.CancelInvoke();
    _watchers.Remove(watcher);
    watcher.Destroying = true;
    watcher.Instance = null; // set to null here to prevent callbacks / log spam
    // schedule for destruction
    Object.Destroy(watcher);
  }

  // return grid name for given world location
  private static string GetGrid(Vector3 pos) => MapHelper.PositionToString(pos);

  // return whether location is in a holiday dungeon
  private static bool IsInDungeon(Vector3 location) => location.y > 1000.0f;

  // return whether scarecrow is in a forbidden location (forbidden topology or
  //  safe zone)
  private static bool IsInForbiddenLocation(ScarecrowNPC scarecrow) =>
    0 != (TerrainMeta.TopologyMap.GetTopology(
      scarecrow.transform.position) & Mask) ||
    scarecrow.InSafeZone();

  // relocate the given scarecrow to the given new location
  private void Relocate(
    ScarecrowNPC scarecrow, Vector3 newPosition, bool quiet)
  {
    if (!IsAlive(scarecrow)) return;

    if (!quiet)
    {
      Effect.server.Run("assets/prefabs/npc/patrol helicopter/effects/rocket_fire.prefab", scarecrow.transform.position);
    }
    scarecrow.MovePosition(newPosition);
    // this seems to be needed to prevent a scarecrow from teleporting back if
    //  it's chasing something
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

  // return string containing ID, position, and grid name of given scarecrow
  private static string Print(ScarecrowNPC scarecrow) => IsValid(scarecrow) ?
    $"{scarecrow}/{scarecrow.net.ID}@{Print(scarecrow.transform.position)}" :
    "<null>";

  private static string Print(Vector3 position) =>
    $"{position}/{GetGrid(position)}";

  // try to reset scarecrow AI in case it's chasing something, so that it
  //  doesn't teleport back when moved
  // this was developed via trial-and-error and may not be optimal
  private static void ResetBrain(ScarecrowNPC scarecrow, bool movementTick)
  {
    if (!IsAlive(scarecrow)) return;
    scarecrow.StopAttacking();
    var brain = scarecrow.Brain;
    if (!brain) return;
    if (!movementTick) brain.StopMovementTick();
    var defaultStateContainer = brain.AIDesign?.GetDefaultStateContainer();
    if (null != defaultStateContainer)
    {
      brain.SwitchToState(
        defaultStateContainer.State, defaultStateContainer.ID);
    }
    if (movementTick) brain.StartMovementTick();
  }

  // return whether given scarecrow is valid AND alive
  private static bool IsAlive(ScarecrowNPC scarecrow) =>
    IsValid(scarecrow) && !scarecrow.IsDead();

  // return whether given scarecrow is valid
  private static bool IsValid(ScarecrowNPC scarecrow) =>
    scarecrow &&
    !scarecrow.IsDestroyed &&
    scarecrow.gameObject;

  // check if scarecrow is in forbidden location, and, if so, move or kill it
  private bool Wrangle(ScarecrowNPC scarecrow, bool quiet = true)
  {
    if (!IsAlive(scarecrow))
    {
      PrintWarning("Wrangle(): Scarecrow is invalid; aborting");
      return false;
    }

    // if scarecrow is in a valid location, update valid locations and abort
    if (!IsInForbiddenLocation(scarecrow))
    {
      // get watcher for scarecrow, aborting if none found
      var myWatcher = scarecrow.gameObject.GetComponent<ScarecrowWatcher>();
      if (!myWatcher) return true;
      // update each valid location if/as appropriate
      foreach (var validLoc in _validLocations)
      {
        // if no closest scarecrow recorded, record this one
        if (!validLoc.closestWatcher)
        {
          validLoc.closestWatcher = myWatcher;
        }
        // if this scarecrow is the last-recorded closest one, update distance
        //  and continue
        if (myWatcher == validLoc.closestWatcher)
        {
          validLoc.closestDistance =
            Vector3.Distance(scarecrow.transform.position, validLoc.position);
          continue;
        }
        // if this scarecrow is further than the current closest one, skip it
        var myDist =
          Vector3.Distance(scarecrow.transform.position, validLoc.position);
        if (myDist > validLoc.closestDistance) continue;
        // record this scarecrow as the new closest one
        validLoc.closestWatcher = myWatcher;
        validLoc.closestDistance = myDist;
      }
      return true;
    }

    // find known-good location that's furthest from any scarecrow
    ValidLocation bestLoc = null;
    var myWatcher2 = scarecrow.gameObject.GetComponent<ScarecrowWatcher>();
    foreach (var validLoc in _validLocations)
    {
      // if this scarecrow is recorded as the closest one, ignore location
      if (validLoc.closestWatcher == myWatcher2) continue;
      // also ignore if closest scarecrow is too close
      if (validLoc.closestDistance < 25.0f) continue;
      // if no best location recorded, choose validLoc as initial candidate
      if (null == bestLoc)
      {
        bestLoc = validLoc;
        continue;
      }
      // if validLoc is a better candidate, record it as bestLoc
      if (validLoc.closestDistance > bestLoc.closestDistance)
      {
        bestLoc = validLoc;
      }
    }

    // kill if no usable known-good positions available
    // NOTE: this should only happen during initial plugin load, and more
    //  scarecrows will eventually spawn to recover things
    if (null == bestLoc)
    {
      PrintWarning($"Killing scarecrow {Print(scarecrow)} due to no suitable known-good location(s) available (count={_validLocations.Count})");
      if (!quiet)
      {
        Effect.server.Run("assets/prefabs/npc/murderer/sound/death.prefab", scarecrow.transform.position);
      }
      // NextTick is needed to avoid errors
      NextTick(scarecrow.KillMessage);
      return false;
    }

    Puts($"Relocating scarecrow {Print(scarecrow)} to known-good location {Print(bestLoc.position)} with distance {bestLoc.closestDistance} to closest scarecrow");
    Relocate(scarecrow, bestLoc.position, quiet);
    // record scarecrow as zero distance from bestLoc
    // this prevents multiple scarecrows from getting teleported there if they
    //  spawn around the same time
    bestLoc.closestDistance = 0.0f;
    bestLoc.closestWatcher = myWatcher2;
    return true;
  }

  #endregion Utilities

  #region Subclasses

  // behavior for triggering periodic location checks on an associated scarecrow
  private sealed class ScarecrowWatcher : FacepunchBehaviour
  {
    // whether watcher has been scheduled for destruction
    public bool Destroying { get; set; }

    // reference back to plugin, for logging and watcher list self-management
    public ScarecrowWrangler Instance { get; set; }

    // return scarecrow associated with watcher
    public ScarecrowNPC GetScarecrow() =>
      gameObject ? gameObject.GetComponent<ScarecrowNPC>() : null;

    // unity handler called when watcher is destroyed
    // the logic here is really just a safety net in case watcher destruction
    //  gets triggered by some means not otherwise caught by the plugin
    public void OnDestroy()
    {
      Destroying = true;
      CancelInvoke();
      if (!Instance) return;
      Instance.Puts($"Watcher {GetInstanceID()}: Destroying from scarecrow {Print(GetScarecrow())}");
      Instance._watchers.Remove(this);
      Instance = null;
    }

    // start watching associated scarecrow, by scheduling repeating invocation
    //  of Watch() method
    public void StartWatching(float repeatRate = 2.0f)
    {
      if (Destroying || !Instance) return;
      Instance.Puts($"Watcher {GetInstanceID()}: Watching scarecrow {Print(GetScarecrow())} with repeatRate={repeatRate}");
      Instance._watchers.Add(this);
      InvokeRepeating(nameof(Watch), 0.0f, repeatRate);
    }

    public void Watch()
    {
      if (Destroying) return;
      var scarecrow = GetScarecrow();
      if (!Instance || !IsAlive(scarecrow))
      {
        Destroy(this);
        return;
      }
      Instance?.Wrangle(scarecrow);
    }
  }

  #endregion Subclasses
}

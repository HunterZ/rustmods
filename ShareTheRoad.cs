using System;
using Facepunch;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins;

[Info("ShareTheRoad", "HunterZ", "1.0.0")]
public class ShareTheRoad : RustPlugin
{
  // set of currently-active Bradleys
  private static readonly HashSet<NetworkableId> ActiveBradleys = new();
  // set of currently-active Vendors
  private static readonly HashSet<NetworkableId> ActiveVendors = new();
  // set of defunct Bradleys to be removed from ActiveBradleys and/or
  //  ActiveWatches
  private static readonly HashSet<NetworkableId> DeadBradleys = new();
  // set of defunct Vendors to be removed from ActiveVendors and/or
  //  ActiveWatches
  private static readonly HashSet<NetworkableId> DeadVendors = new();
  // set of defunct Bradley-Vendor pairs to be removed from ActiveWatches
  private static readonly HashSet<(NetworkableId, NetworkableId)> DeadWatches
    = new();
  // Bradley-Vendor pairs that are close to each other
  private static readonly HashSet<(NetworkableId, NetworkableId)>
    ActiveWatches = new();
  // timer used to drive CheckEntities() processing
  private static Timer _watchTimer;

  #region Plugin API

  private void Init()
  {
    Reset();
    Unsubscribe(nameof(OnEntityKill));
    Unsubscribe(nameof(OnEntitySpawned));
  }

  private void OnServerInitialized()
  {
    foreach (var serverEntity in BaseNetworkable.serverEntities)
    {
      switch (serverEntity)
      {
        case BradleyAPC bradley:      OnEntitySpawned(bradley); break;
        case TravellingVendor vendor: OnEntitySpawned(vendor);  break;
      }
    }
    Subscribe(nameof(OnEntitySpawned));
    Puts($"OnServerInitialized(): Found {ActiveBradleys.Count} active bradley(s) and {ActiveVendors.Count} vendor(s).");
  }

  private void Unload()
  {
    Reset();

    foreach (var serverEntity in BaseNetworkable.serverEntities)
    {
      if (serverEntity is not BradleyAPC bradley) continue;
      bradley.CancelInvoke(nameof(STR_TargetScarecrows));
    }
  }

  #endregion Plugin API

  #region Hook Handlers

  private void OnEntitySpawned(BradleyAPC bradley)
  {
    if (!bradley || null == bradley.net) return;
    ActiveBradleys.Add(bradley.net.ID);
    if (1 == ActiveBradleys.Count && 0 == ActiveVendors.Count)
    {
      Puts("Subscribing to OnEntityKill hook");
      Subscribe(nameof(OnEntityKill));
    }
    ManageTimer();
    CH47AIBrain p;
    bradley.InvokeRepeating(
      () => STR_TargetScarecrows(bradley), 0.0f, bradley.searchFrequency);
  }

  private void OnEntitySpawned(TravellingVendor vendor)
  {
    if (!vendor || null == vendor.net) return;
    ActiveVendors.Add(vendor.net.ID);
    if (1 == ActiveVendors.Count && 0 == ActiveBradleys.Count)
    {
      Puts("Subscribing to OnEntityKill hook");
      Subscribe(nameof(OnEntityKill));
    }
    ManageTimer();
  }

  private void OnEntityKill(BradleyAPC bradley)
  {
    if (!bradley || null == bradley.net) return;
    var bradleyID = bradley.net.ID;
    ActiveBradleys.Remove(bradleyID);
    DeadBradleys.Add(bradleyID);
    CheckDeadEntities(false);
    if (0 == ActiveBradleys.Count && 0 == ActiveVendors.Count)
    {
      Puts("Unsubscribing from OnEntityKill hook");
      Unsubscribe(nameof(OnEntityKill));
    }
    ManageTimer();

    bradley.CancelInvoke(nameof(STR_TargetScarecrows));
  }

  private void OnEntityKill(TravellingVendor vendor)
  {
    if (!vendor || null == vendor.net) return;
    var vendorID = vendor.net.ID;
    ActiveVendors.Remove(vendorID);
    DeadVendors.Add(vendorID);
    CheckDeadEntities(false);
    if (0 == ActiveBradleys.Count && 0 == ActiveVendors.Count)
    {
      Puts("Unsubscribing from OnEntityKill hook");
      Unsubscribe(nameof(OnEntityKill));
    }
    ManageTimer();
  }

  #endregion Hook Handlers

  #region Utilities

  // check for any data records referencing net IDs in the dead entity sets
  // the sets are cleared at the end
  private void CheckDeadEntities(bool warn)
  {
    //  bradleys
    foreach (var deadBradleyID in DeadBradleys)
    {
      if (ActiveBradleys.Remove(deadBradleyID) && warn)
      {
        Puts($"WARNING: Removed defunct BradleyID {deadBradleyID} from active Bradleys set");
      }

      foreach (var watchPair in ActiveWatches)
      {
        if (watchPair.Item1 == deadBradleyID)
        {
          DeadWatches.Add(watchPair);
        }
      }
    }
    //  vendors
    foreach (var deadVendorID in DeadVendors)
    {
      if (ActiveVendors.Remove(deadVendorID) && warn)
      {
        Puts($"WARNING: Removed defunct VendorID {deadVendorID} from active Vendors set");
      }

      foreach (var watchPair in ActiveWatches)
      {
        if (watchPair.Item2 == deadVendorID)
        {
          DeadWatches.Add(watchPair);
        }
      }
    }
    //  pairs
    foreach (var deadWatchPair in DeadWatches)
    {
      if (ActiveWatches.Remove(deadWatchPair) && warn)
      {
        Puts($"WARNING: Removed defunct watch pair {deadWatchPair} from watch set");
      }
    }
    DeadBradleys.Clear();
    DeadVendors.Clear();
    DeadWatches.Clear();
  }

  private void CheckEntities()
  {
    DeadBradleys.Clear();
    DeadVendors.Clear();
    DeadWatches.Clear();
    // check each Bradley-Vendor pair to see if they're too close
    foreach (var bradleyID in ActiveBradleys)
    {
      var bradley =
        BaseNetworkable.serverEntities.Find(bradleyID) as BradleyAPC;
      if (!bradley)
      {
        DeadBradleys.Add(bradleyID);
        continue;
      }
      foreach (var vendorID in ActiveVendors)
      {
        var vendor =
          BaseNetworkable.serverEntities.Find(vendorID) as TravellingVendor;
        if (!vendor)
        {
          DeadVendors.Add(vendorID);
          continue;
        }
        // we now have a valid Bradley-Vendor entity pair
        var watchPair = (bradleyID, vendorID);
        // check whether they're currently too close
        var distance = Vector3.Distance(
          bradley.transform.position, vendor.transform.position);
        var close = distance < 10.0f;
        // check whether this pair was previously too close
        var watching = ActiveWatches.Remove(watchPair);
        // not close - do nothing (already removed from watch set)
        if (!close)
        {
          if (watching)
          {
            Puts($"Stopped watching Bradley-Vendor pair {watchPair} near grid {GetGrid(bradley.transform.position)} due to distance={distance}");
          }
          // else
          // {
          //   Puts($"Not watching Bradley-Vendor pair {watchPair} near grid {GetGrid(bradley.transform.position)} due to distance={distance}");
          // }
          continue;
        }
        if (watching)
        {
          // close for 2 consecutive checks; swap positions
          (bradley.transform.position, vendor.transform.position) =
            (vendor.transform.position, bradley.transform.position);
          Puts($"Swapped Bradley-Vendor pair {watchPair} positions near grid {GetGrid(bradley.transform.position)} due to distance={distance}");
          // also ensure they don't fall under the terrain
          FixEntityPosition(bradley);
          FixEntityPosition(vendor);
          // NOTE: already removed from watch set, which is what we want
          //  because otherwise they might erroneously keep swapping
          continue;
        }
        // just became close; add to watch set
        ActiveWatches.Add(watchPair);
        Puts($"Started watching Bradley-Vendor pair {watchPair} near grid {GetGrid(bradley.transform.position)} due to distance={distance}");
      }
      // handle any dead entities, with warnings since there shouldn't be any
      CheckDeadEntities(true);
    }
  }

  // ensure entity is at or above terrain height (hopefully avoids falling
  //  through the world on swap
  private void FixEntityPosition(BaseEntity entity)
  {
    if (!entity || null == entity.net) return;
    var groundY = TerrainMeta.HeightMap.GetHeight(entity.transform.position);
    if (entity.transform.position.y >= groundY) return;
    Puts($"Moving entity {entity} near grid {GetGrid(entity.transform.position)} to terrain height={groundY} from current height={entity.transform.position.y}");
    entity.transform.position.Set(
      entity.transform.position.x, groundY, entity.transform.position.z);
  }

  private void StartWatching() =>
    _watchTimer ??= timer.Every(10.0f, CheckEntities);

  private void StopWatching()
  {
    if (null == _watchTimer) return;
    _watchTimer.Destroy();
    _watchTimer = null;
    ActiveWatches.Clear();
  }

  // start or stop timer processing based on whether there is at least one
  //  Bradley and one Vendor active
  private void ManageTimer()
  {
    var currentTimerState = null != _watchTimer;
    var idealTimerState =
      ActiveBradleys.Count > 0 && ActiveVendors.Count > 0;
    // abort if timer is already in ideal state
    if (currentTimerState == idealTimerState) return;
    // not in ideal state; start or stop timer
    if (idealTimerState)
    {
      Puts("Starting proximity check timer");
      StartWatching();
    }
    else
    {
      Puts("Stopping proximity check timer");
      StopWatching();
    }
  }

  private static string GetGrid(Vector3 pos) =>
    MapHelper.PositionToString(pos);
/*
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
*/
  private void Reset()
  {
    StopWatching();
    ActiveBradleys.Clear();
    ActiveVendors.Clear();
    DeadBradleys.Clear();
    DeadVendors.Clear();
    DeadWatches.Clear();
    ActiveWatches.Clear();
  }

  private static void STR_TargetScarecrows(BradleyAPC bradley)
  {
    // find all scarecrows in search range
    var list = Pool.Get<List<ScarecrowNPC>>();
    Vis.Entities(
      bradley.transform.position, bradley.searchRange, list, 133120 /*0x020800*/);
    foreach (var scarecrow in list)
    {
      // method will protect from duplicates
      bradley.AddOrUpdateTarget(scarecrow, scarecrow.transform.position);
    }
    Pool.FreeUnmanaged(ref list);
  }

  #endregion Utilities
}

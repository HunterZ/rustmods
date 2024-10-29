using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
  [Info("ScarecrowWrangler", "HunterZ", "1.0.0")]
  public class ScarecrowWrangler : RustPlugin
  {
    private static readonly int Mask =
      (int)TerrainTopology.Enum.Ocean |
      (int)TerrainTopology.Enum.Tier0;

    private static HashSet<Vector3> _validLocations = new();

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

    // NOTE: no need to do this because the game automatically destroys
    //  components for a killed entity
    // private void OnEntityKill(ScarecrowNPC scarecrow) =>
    //   scarecrow.GetComponent<ScarecrowWatcher>()?.OnDestroy();

    private void OnEntitySpawned(ScarecrowNPC scarecrow)
    {
      if (null == scarecrow || scarecrow.IsDestroyed || scarecrow.IsDead())
      {
        return;
      }

      var topologies = GetTopologies(TerrainMeta.TopologyMap.GetTopology(
        scarecrow.transform.position));
      string ts = "";
      foreach (var t in topologies)
      {
        if (!string.IsNullOrEmpty(ts))
        {
          ts += ", ";
        }
        ts += t.ToString();
      }
      Puts($"Processing new scarecrow {scarecrow.net.ID} at location {scarecrow.transform.position}/{GetGrid(scarecrow.transform.position)} with topologies: {ts}");

      // ignore scarecrows that look like they're in portal dungeons, because we
      //  don't want to do any of the following:
      // - teleport them out of dungeons
      // - waste resources tracking them
      // - teleport other zombies into dungeons or empty space
      if (IsInDungeon(scarecrow.transform.position))
      {
        Puts($"Ignoring scarecrow at location {scarecrow.transform.position} because it's probably in a portal dungeon");
        return;
      }
      if (IsForbidden(scarecrow.transform.position))
      {
        // abort if scarecrow was destroyed
        if (!Wrangle(scarecrow)) return;
      }
      else
      {
        // scarecrow is in a good spot; record it for reuse
        _validLocations.Add(scarecrow.transform.position);
      }
      NextTick(() => CreateWatcher(scarecrow));
    }

    #endregion Hook Handlers

    #region Utilities

    private void CreateWatcher(ScarecrowNPC scarecrow)
    {
      if (!scarecrow.TryGetComponent(out ScarecrowWatcher watcher))
      {
        watcher = scarecrow.gameObject.AddComponent<ScarecrowWatcher>();
      }
      watcher.Init(scarecrow);
      watcher.StartWatching();
    }

    private void DestroyWatcher(ScarecrowNPC scarecrow) =>
      scarecrow.GetComponent<ScarecrowWatcher>()?.OnDestroy();

    private static bool IsForbidden(Vector3 location) =>
      0 != (TerrainMeta.TopologyMap.GetTopology(location) & Mask);

    private static bool IsInDungeon(Vector3 location) => location.y > 1000.0f;

    // Credit: Lorenzo - https://umod.org/community/rust/4861-calculate-current-coordinate-of-player?page=1#post-3
    private static string GetGrid(Vector3 pos) =>
      PhoneController.PositionToGridCoord(pos);
/*
    private static string GetNearestMonumentName(Vector3 pos)
    {
      string name = "UNKNOWN";
      float minDist = -1f;
      foreach (var monument in TerrainMeta.Path.Monuments)
      {
        var dist = Vector3.Distance(pos, monument.transform.position);
        if (minDist < 0 || dist < minDist)
        {
          minDist = dist;
          name = monument.displayPhrase.english;
          if (string.IsNullOrEmpty(name))
          {
            name = monument.transform.root.name;
          }
        }
      }
      return name.Replace("\n", "");
    }
*/

    private static HashSet<TerrainTopology.Enum> GetTopologies(int topologies)
    {
      HashSet<TerrainTopology.Enum> h = new();
      foreach (TerrainTopology.Enum e in System.Enum.GetValues(typeof(
        TerrainTopology.Enum)))
      {
        if (0 != (topologies & (int)e))
        {
          h.Add(e);
        }
      }
      return h;
    }

/*
    private static void RandomizeLocation(ref Vector3 location)
    {
      location.x = Random.Range(
        -TerrainMeta.Size.x / 2, TerrainMeta.Size.x / 2);
      location.y = 0;
      location.z = Random.Range(
        -TerrainMeta.Size.z / 2, TerrainMeta.Size.z / 2);

      location.y = TerrainMeta.HeightMap.GetHeight(location);
    }
*/
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

    private void ResetBrain(ScarecrowNPC scarecrow, bool movementTick)
    {
      if (null == scarecrow) return;
      scarecrow.StopAttacking();
      var brain = scarecrow.Brain;
      if (null == brain) return;
      if (!movementTick) brain.StopMovementTick();
      // scarecrow.Brain?.SwitchToState(AIState.Idle);
      // scarecrow.Brain?.CurrentState?.Reset();
      var defaultStateContainer = brain.AIDesign?.GetDefaultStateContainer();
      if (null != defaultStateContainer)
      {
        brain.SwitchToState(
          defaultStateContainer.State, defaultStateContainer.ID);
      }
      // brain.CurrentState?.Reset();
      if (movementTick) brain.StartMovementTick();
    }

    private bool Wrangle(ScarecrowNPC scarecrow, bool quiet = true)
    {
      if (!IsForbidden(scarecrow.transform.position)) return true;

      // Puts($"Scarecrow {scarecrow.net.ID} current AI state: {scarecrow.Brain?.CurrentState}");

      // kill if no known-good positions available
      if (_validLocations.Count <= 0)
      {
        PrintWarning($"Killing scarecrow {scarecrow.net.ID} at forbidden location {GetGrid(scarecrow.transform.position)} due to failure to no known-good location(s) available");
        if (!quiet)
        {
          Effect.server.Run("assets/prefabs/npc/murderer/sound/death.prefab", scarecrow.transform.position);
        }
        // NextTick is needed to avoid errors
        NextTick(scarecrow.KillMessage);
        return false;
      }

      var randomIndex = Random.Range(0, _validLocations.Count - 1);
      var i = 0;
      foreach (var location in _validLocations)
      {
        if (i == randomIndex)
        {
          PrintWarning($"Relocating scarecrow {scarecrow.net.ID} from forbidden location {GetGrid(scarecrow.transform.position)} to known-good location {GetGrid(location)} at index {i}/{_validLocations.Count}");
          Relocate(scarecrow, location, quiet);
          // Puts($"Scarecrow {scarecrow.net.ID} new AI state: {scarecrow.Brain?.CurrentState}");
          return true;
        }
        ++i;
      }

      // pathological
      PrintError("Ran off end of valid locations database?");
      return true;
    }

    #endregion Utilities

    #region Subclasses

    private sealed class ScarecrowWatcher : FacepunchBehaviour
    {
      public static ScarecrowWrangler Instance { get; set; }

      private ScarecrowNPC _scarecrow;
      // private ulong _scarecrowID;

      public void Init(ScarecrowNPC scarecrow = null)
      {
        _scarecrow = scarecrow;
        // _scarecrowID = null == scarecrow ? 0 : scarecrow.net.ID.Value;
      }

      public void OnDestroy()
      {
        // Instance?.Puts($"Destroying watcher for scarecrow {_scarecrowID}");
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

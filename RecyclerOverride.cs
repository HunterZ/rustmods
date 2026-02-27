using Facepunch;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using UnityEngine;

namespace Oxide.Plugins
{
  [Info("Recycler Override", "HunterZ", "0.0.1", ResourceId = 2728)]
  [Description("Allows overriding green versus yellow recycler state by monument")]
  public class RecyclerOverride : RustPlugin
  {
    #region Core Logic

    // collection of original recycler states for cleanup purposes
    private Dictionary<Recycler, (bool, string)> _recyclers = new();

    // override recycler to the given state, if different from current state
    // NOTE: true => yellow/downgrade, false => green/upgrade
    private void OverrideRecycler(
      Recycler recycler, bool newState, string mName)
    {
      // credit: sickness0666 for digging up the flag stuff
      var currentState = recycler.HasFlag(BaseEntity.Flags.Reserved9);

      // prevent redundant changes
      if (currentState == newState)
      {
        Puts($"Ignoring redundant {(newState ? "down" : "up")}grade for {mName} recycler at {recycler.transform.position}");
        return;
      }

      var managed = _recyclers.TryGetValue(recycler, out var data);
      var oState = data.Item1;
      if (!managed)
      {
        // record new override
        Puts($"Applying {(newState ? "down" : "up")}grade override to {mName} recycler at {recycler.transform.position}");
        _recyclers.Add(recycler, (currentState, mName));
      }
      else if (newState == oState)
      {
        // remove existing override
        Puts($"Canceling {(currentState ? "down" : "up")}grade override of {mName} recycler at {recycler.transform.position}");
        _recyclers.Remove(recycler);
      }
      else
      {
        // this shouldn't be possible
        Puts($"Unexpected {(newState ? "down" : "up")}grade state change for {mName} recycler at {recycler.transform.position}");
      }

      recycler.SetFlag(BaseEntity.Flags.Reserved9, newState);
    }

    private void OverrideRecyclersNear(
      Vector3 position, float radius, bool newState, string mName)
    {
      var list = Pool.Get<List<Recycler>>();
      Vis.Entities(position, radius, list);
      foreach (var recycler in list)
      {
        OverrideRecycler(recycler, newState, mName);
      }
      Pool.FreeUnmanaged(ref list);
    }

    private static string GetMonumentName(MonumentInfo monumentInfo)
    {
      var monumentName = monumentInfo.displayPhrase?.english?.Trim();
      // if no name found, check for custom monument name
      if (string.IsNullOrEmpty(monumentName) &&
          monumentInfo.name.Contains("monument_marker.prefab"))
      {
        return monumentInfo.transform.root.name;
      }
      // stupid hack: for Harbor monuments, try to extract a differentiator
      //  from the prefab name
      if ("Harbor" == monumentName)
      {
        monumentName +=
          monumentInfo.name
            .Replace(
              "assets/bundled/prefabs/autospawn/monument/harbor/harbor_",
              " ")
            .Replace(".prefab", string.Empty);
      }
      return monumentName;
    }

    private void CheckMonuments()
    {
      // check all monuments, both to add newly discovered ones to config, and
      //  to apply configured overrides to any contained recyclers
      var saveConfig = false;
      foreach (var monumentInfo in TerrainMeta.Path.Monuments)
      {
        var monumentName = GetMonumentName(monumentInfo);

        // skip invisible or unnamed monuments
        if (!monumentInfo.shouldDisplayOnMap ||
            string.IsNullOrEmpty(monumentName))
        {
          continue;
        }

        // record and skip new monuments
        if (!_config.Monuments.ContainsKey(monumentName))
        {
          Puts($"Adding new monument to config: {monumentName}");
          _config.Monuments.Add(
            monumentName, new ConfigMonument{Override = 0, Radius = -1});
          saveConfig = true;
          continue;
        }

        // get monument data, but skip if disabled
        if (!_config.Monuments.TryGetValue(
              monumentName, out var monumentConfig) ||
            0 == monumentConfig.Override ||
            monumentConfig.Radius < 0.0)
        {
          continue;
        }

        // apply monument override to any recyclers
        OverrideRecyclersNear(
          monumentInfo.transform.position,
          monumentConfig.Radius,
          monumentConfig.Override < 0,
          monumentName);
      }

      if (saveConfig) SaveConfig();

      // now start listening for recycler spawns/kills by other plugins
      Subscribe(nameof(OnEntityKill));
      Subscribe(nameof(OnEntitySpawned));
    }

    #endregion Core Logic

    #region Hooks

    private void Init()
    {
      // unsubscribe from hooks at first, because otherwise we end up
      //  redundantly processing vanilla recyclers during server startup
      Unsubscribe(nameof(OnEntityKill));
      Unsubscribe(nameof(OnEntitySpawned));
    }

    private void OnServerInitialized()
    {
      NextTick(CheckMonuments);
    }

    private void Unload()
    {
      foreach (var (recycler, data) in _recyclers)
      {
        if (!recycler) continue;
        var (oState, mName) = data;
        Puts($"Restoring {mName} recycler to {(oState ? "yellow" : "green")} at {recycler.transform.position}");
        recycler.SetFlag(BaseEntity.Flags.Reserved9, oState);
      }
      _recyclers.Clear();
    }

    private void OnEntityKill(Recycler recycler)
    {
      if (!_recyclers.Remove(recycler, out var data)) return;
      var mName = data.Item2;
      Puts($"Stopped managing {mName} recycler at {recycler.transform.position} because it was killed");
    }

    private void OnEntitySpawned(Recycler recycler)
    {
      // try to find recycler in an enabled monument's radius
      // if in the radius of more than one monument, go with the closest one
      var rDist = float.MaxValue;
      var overrideState = 0;
      var mName = string.Empty;
      foreach (var monumentInfo in TerrainMeta.Path.Monuments)
      {
        // skip monument if not enabled in config
        var monumentName = GetMonumentName(monumentInfo);
        if (!_config.Monuments.TryGetValue(
              monumentName, out var monumentConfig) ||
            0 == monumentConfig.Override ||
            monumentConfig.Radius < 0.0f)
        {
          continue;
        }

        // get distance from recycler to monument
        var mDist = Vector3.Distance(
          recycler.transform.position, monumentInfo.transform.position);
        // skip if not within configured radius, or not closer than closest
        //  found so far
        if (mDist > monumentConfig.Radius || mDist >= rDist) continue;
        // this monument is closest so far; record it
        rDist = mDist;
        overrideState = monumentConfig.Override;
        mName = monumentName;
      }

      // Puts(string.IsNullOrEmpty(mName)
      //   ? $"Recycler at {recycler.transform.position} is not near an active monument"
      //   : $"Recycler at {recycler.transform.position} is within radius={rDist} of active monument {mName} with configured override state {overrideState}");

      if (0 != overrideState)
      {
        OverrideRecycler(recycler, overrideState < 0, mName);
      }
    }

    #endregion Hooks

    #region Config

    private ConfigMain _config;

    private sealed class ConfigMonument
    {
      [JsonProperty(PropertyName = "Override Direction (negative=yellow, zero=none, positive=green")]
      public int Override { get; set; }

      [JsonProperty(PropertyName = "Override Radius (negative=disable")]
      public float Radius { get; set; }
    }

    private sealed class ConfigMain
    {
      [JsonProperty(PropertyName = "Monument Settings")]
      public SortedDictionary<string, ConfigMonument> Monuments { get; set; } =
        new();
    }

    protected override void LoadConfig()
    {
      base.LoadConfig();
      try
      {
        _config = Config.ReadObject<ConfigMain>();
        if (null == _config)
        {
          LoadDefaultConfig();
        }
      }
      catch (Exception e)
      {
        PrintWarning($"Error reading config file: {e}");
        LoadDefaultConfig();
      }
    }

    protected override void LoadDefaultConfig()
    {
      Puts("Creating a new config file");
      _config = new ConfigMain();
      SaveConfig();
    }

    protected override void SaveConfig()
    {
      Config.WriteObject(_config);
    }

    #endregion Config
  }
}

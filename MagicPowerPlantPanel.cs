using HarmonyLib;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Reflection;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Magic Power Plant Panel", "HunterZ", "1.0.1")]
[Description("Provides a Magic Panel that displays Power Plant grid power status")]
public class MagicPowerPlantPanel : RustPlugin
{
  #region Class Fields

  // ReSharper disable once InconsistentNaming
  [PluginReference] private Plugin MagicPanel;

  private DynamicConfigFile _configFile;
  private PluginConfig _pluginConfig;
  private int _phase = -1;

  private readonly string[] _defaultColors =
  {
    "#FFFFFF0F",
    "#FFFFFF7F",
    "#FFFFFF7F",
    "#FFFFFF7F",
    "#FFFFFF7F"
  };

  // private readonly string[] _defaultUrls =
  // {
  //   "https://i.postimg.cc/sD0cJRDv/powerplant0.png",
  //   "https://i.postimg.cc/dtSB9KtZ/powerplant1.png",
  //   "https://i.postimg.cc/J4dqQW4B/powerplant2.png",
  //   "https://i.postimg.cc/wTbVkdTJ/powerplant3.png",
  //   "https://i.postimg.cc/zXPjk1XK/powerplant4.png"
  // };

  private readonly string[] _defaultUrls =
  {
    "https://i.postimg.cc/pd1PBZ1w/powerplant0.png",
    "https://i.postimg.cc/xdF9PR4V/powerplant1.png",
    "https://i.postimg.cc/mr6TSw5G/powerplant2.png",
    "https://i.postimg.cc/QMnjqbyD/powerplant3.png",
    "https://i.postimg.cc/W3FTYkQf/powerplant4.png"
  };

  private enum UpdateType
  {
    // All   = 1,
    // Panel = 2,
    Image = 3
    // Text  = 4
  }

  #endregion

  #region Oxide API

  protected override void LoadDefaultConfig()
  {
    Puts("Creating new config data");
    _pluginConfig = new PluginConfig();
  }

  protected override void LoadConfig()
  {
    var path = $"{Manager.ConfigPath}/MagicPanel/{Name}.json";
    _configFile = new DynamicConfigFile(path);
    if (_configFile?.Exists() is not true)
    {
      LoadDefaultConfig();
      return;
    }

    try
    {
      _pluginConfig = _configFile.ReadObject<PluginConfig>();
    }
    catch (System.Exception ex)
    {
      PrintWarning($"Exception reading config file {path}: " + ex.Message);
      LoadDefaultConfig();
    }
  }

  private void Init()
  {
    Unsubscribe(nameof(OnPowergridStageChanged));

    if (null == _configFile)
    {
      PrintError("Config file handle is null; aborting");
      return;
    }

    if (null == _pluginConfig)
    {
      PrintError("Config object is null; aborting");
      return;
    }

    // need 5 panel layouts (one for each power grid phase)
    // if config file contains something different, pad out with defaults or
    //  truncate as necessary
    // TODO: is there a less stupid way to do enforce a fixed-size array in a
    //  config file?
    var oldLength = _pluginConfig.PanelLayouts?.Length ?? -1;
    if (oldLength is not 5)
    {
      switch (oldLength)
      {
        case -1:
          Puts("LoadConfig(): New/reset config - populating with default layout values");
          break;
        case <= 5:
          PrintWarning($"LoadConfig(): Loaded config has {oldLength} layout(s) but expected 5 - populating missing entries with default values");
          break;
        default:
          PrintWarning($"LoadConfig(): Loaded config has {oldLength} layout(s) but expected 5 - truncating list to expected size");
          break;
      }

      var newLayout = new PanelLayout[5];

      for (var i = 0; i < 5 && i < oldLength; ++i)
      {
        Puts($"LoadConfig():  Populating layout index {i} with config values");
        newLayout[i] = _pluginConfig.PanelLayouts![i];
      }

      if (oldLength < 0) oldLength = 0;
      for (var j = oldLength; j < 5; ++j)
      {
        Puts($"LoadConfig():  Populating layout index {j} with default URL {_defaultUrls[j]}");
        newLayout[j] = new PanelLayout
        {
          Image = new PanelImage
          {
            Color = _defaultColors[j],
            Url = _defaultUrls[j]
          }
        };
      }

      _pluginConfig.PanelLayouts = newLayout;
    }

    Puts($"Writing config file {_configFile.Filename}");
    _configFile.WriteObject(_pluginConfig);
  }

  private void OnServerInitialized()
  {
    MagicPanelRegisterPanels();
  }

  private void Unload()
  {
    _configFile = null;
    _pluginConfig = null;
    _phase = -1;
  }

  private void OnPowergridStageChanged(
    PowergridManager powergridManager, int newPhase) => NextTick(() =>
  {
    if (newPhase == _phase) return;
    _phase = newPhase;
    MagicPanel?.Call("UpdatePanel", Name, (int)UpdateType.Image);
  });

  #endregion

  #region MagicPanel API

  private void MagicPanelRegisterPanels()
  {
    Unsubscribe(nameof(OnPowergridStageChanged));

    if (MagicPanel?.IsLoaded is not true)
    {
      PrintError("Missing plugin dependency MagicPanel: https://umod.org/plugins/magic-panel");
      return;
    }

    // set _phase to current server state
    _phase = Powergrid.enabled ? PowergridManager.GetCurrentStage(true) : 0;

    // NOTE: this will trigger an initial call to GetPanel()
    MagicPanel.Call("RegisterGlobalPanel",
      this, Name, JsonConvert.SerializeObject(_pluginConfig.PanelSettings),
      nameof(GetPanel));

    Subscribe(nameof(OnPowergridStageChanged));
  }

  private Hash<string, object> GetPanel() =>
    _pluginConfig?.PanelLayouts?.Length is >= 0 &&
    _phase >= 0 && _phase < _pluginConfig.PanelLayouts.Length ?
      _pluginConfig.PanelLayouts[_phase].ToHash() :
      null;

  #endregion

  #region Classes

  private sealed class PluginConfig
  {
    [JsonProperty(PropertyName = "Panel Settings")]
    public PanelRegistration PanelSettings { get; set; } = new();

    [JsonProperty(PropertyName = "Panel Layout By Power Grid Phase")]
    public PanelLayout[] PanelLayouts { get; set; }
  }

  private sealed class PanelRegistration
  {
    [UsedImplicitly] public string Dock { get; set; } = "center";
    [UsedImplicitly] public float Width { get; set; } = 0.02f;
    [UsedImplicitly] public int Order { get; set; } = 1;
    [UsedImplicitly] public string BackgroundColor { get; set; } = "#FFFFFF08";
  }

  private sealed class PanelLayout
  {
    [UsedImplicitly] public PanelImage Image { get; set; } = new();

    // cache hash instead of regenerating it on every call/change
    [JsonIgnore]
    private Hash<string, object> _hash;

    public Hash<string, object> ToHash()
    {
      // only create new hash if none exists yet
      _hash ??= new Hash<string, object>
      {
        [nameof(Image)] = Image.ToHash()
      };
      return _hash;
    }
  }

  private abstract class PanelBase
  {
    [UsedImplicitly] public bool Enabled { get; set; } = true;
    [UsedImplicitly] public string Color { get; set; } = "#FFFFFF0F";
    [UsedImplicitly] public int Order { get; set; } = 0;
    [UsedImplicitly] public float Width { get; set; } = 1.0f;
    [UsedImplicitly] public TypePadding Padding { get; set; } = new();

    public virtual Hash<string, object> ToHash() => new()
    {
      [nameof(Enabled)] = Enabled,
      [nameof(Color)] = Color,
      [nameof(Order)] = Order,
      [nameof(Width)] = Width,
      [nameof(Padding)] = Padding.ToHash()
    };
  }

  private sealed class PanelImage : PanelBase
  {
    [UsedImplicitly] public string Url { get; set; }

    public override Hash<string, object> ToHash()
    {
      var hash = base.ToHash();
      hash[nameof(Url)] = Url;
      return hash;
    }
  }

  private sealed class TypePadding
  {
    [UsedImplicitly] public float Left { get; set; } = 0.05f;
    [UsedImplicitly] public float Right { get; set; } = 0.05f;
    [UsedImplicitly] public float Top { get; set; } = 0.05f;
    [UsedImplicitly] public float Bottom { get; set; } = 0.05f;

    public Hash<string, object> ToHash() => new()
    {
      [nameof(Left)] = Left,
      [nameof(Right)] = Right,
      [nameof(Top)] = Top,
      [nameof(Bottom)] = Bottom
    };
  }

  #endregion
}

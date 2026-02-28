using Newtonsoft.Json;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using System.Collections.Generic;

namespace Oxide.Plugins;

[Info("Magic Giant Excavator Panel", "HunterZ", "1.0.0")]
[Description("Provides a Magic Panel that displays Giant Excavator status")]
public class MagicGiantExcavatorPanel : RustPlugin
{
  #region Class Fields

  [PluginReference] Plugin MagicPanel;

  private PluginConfig _pluginConfig;
  private readonly HashSet<ExcavatorArm> _activeExcavators = new();

  private enum UpdateType
  {
    // All   = 1,
    // Panel = 2,
    Image = 3
    // Text  = 4
  }

  #endregion

  #region Oxide API

  private void Init()
  {
    // unsubscribe hook until after loaded, as we will instead proactively scan
    //  in case of post-start plugin (re)load
    Unsubscribe(nameof(OnExcavatorMiningToggled));
  }

  protected override void LoadDefaultConfig() =>
    _pluginConfig = new PluginConfig();

  protected override void LoadConfig()
  {
    var path = $"{Manager.ConfigPath}/MagicPanel/{Name}.json";
    var configFile = new DynamicConfigFile(path);
    if (!configFile.Exists())
    {
      Puts($"No config file {path}; creating a new one");
      LoadDefaultConfig();
      configFile.WriteObject(_pluginConfig);
      return;
    }

    try
    {
      _pluginConfig = configFile.ReadObject<PluginConfig>();
    }
    catch (System.Exception ex)
    {
      RaiseError($"Exception reading config file {path}: " + ex.Message);
      LoadDefaultConfig();
    }

    configFile.WriteObject(_pluginConfig);
  }

  private void OnServerInitialized()
  {
    foreach (var entity in BaseNetworkable.serverEntities)
    {
      if (entity is not ExcavatorArm excavatorArm || !excavatorArm) continue;
      if (excavatorArm.IsMining()) _activeExcavators.Add(excavatorArm);
    }

    MagicPanelRegisterPanels();
  }

  private void Unload()
  {
    _pluginConfig = null;
    _activeExcavators.Clear();
  }

  private void OnExcavatorMiningToggled(ExcavatorArm excavatorArm)
  {
    if (!excavatorArm) return;

    var wasEnabled = _activeExcavators.Count > 0;

    if (excavatorArm.IsOn())
    {
      _activeExcavators.Add(excavatorArm);
    }
    else
    {
      _activeExcavators.Remove(excavatorArm);
    }

    // only care about transitions between empty and nonempty HashSet
    var isEnabled = _activeExcavators.Count > 0;
    if (isEnabled == wasEnabled) return;

    _pluginConfig.PanelLayout.Image.Color =
      isEnabled ? _pluginConfig.ActiveColor : _pluginConfig.InactiveColor;

    MagicPanel?.Call("UpdatePanel", Name, (int)UpdateType.Image);
  }

  #endregion

  #region MagicPanel API

  private void MagicPanelRegisterPanels()
  {
    if (MagicPanel?.IsLoaded is not true)
    {
      PrintError("Missing plugin dependency MagicPanel: https://umod.org/plugins/magic-panel");
      Unsubscribe(nameof(OnExcavatorMiningToggled));
      return;
    }

    if (!_pluginConfig.PanelLayout.Image.Enabled)
    {
      PrintWarning("Not registering panel because all items are disabled in config");
      Unsubscribe(nameof(OnExcavatorMiningToggled));
      return;
    }

    // NOTE: this will trigger an initial call to GetPanel()
    MagicPanel.Call("RegisterGlobalPanel",
      this, Name, JsonConvert.SerializeObject(_pluginConfig.PanelSettings),
      nameof(GetPanel));

    Subscribe(nameof(OnExcavatorMiningToggled));
  }

  private Hash<string, object> GetPanel() => _pluginConfig.PanelLayout.ToHash();

  #endregion

  #region Classes

  private sealed class PluginConfig
  {
    [JsonProperty(PropertyName = "Active Color")]
    public string ActiveColor { get; set; } = "#FFFFFF7F";

    [JsonProperty(PropertyName = "Inactive Color")]
    public string InactiveColor { get; set; } = "#FFFFFF0F";

    [JsonProperty(PropertyName = "Panel Settings")]
    public PanelRegistration PanelSettings { get; set; } = new();

    [JsonProperty(PropertyName = "Panel Layout")]
    public PanelLayout PanelLayout { get; set; } = new();
  }

  private sealed class PanelRegistration
  {
    public string Dock { get; set; } = "center";
    public float Width { get; set; } = 0.02f;
    public int Order { get; set; } = 1;
    public string BackgroundColor { get; set; } = "#FFFFFF08";
  }

  private sealed class PanelLayout
  {
    public PanelImage Image { get; set; } = new();

    // cache hash instead of regenerating it on every call/change
    [JsonIgnore]
    private Hash<string, object> _panelHash;

    public Hash<string, object> ToHash()
    {
      // only create new hash if none exists yet
      _panelHash ??= new Hash<string, object>();
      _panelHash[nameof(Image)] = Image.ToHash();
      return _panelHash;
    }
  }

  private abstract class PanelBase
  {
    public bool Enabled { get; set; } = true;
    public int Order { get; set; } = 0;
    public float Width { get; set; } = 1.0f;
    public TypePadding Padding { get; set; } = new();

    // this is not exposed to the config file, because it gets overwritten by
    //  the appropriate higher-level settings
    [JsonIgnore] public string Color { get; set; } = "#FFFFFF0F";

    [JsonIgnore] private Hash<string, object> _hash;

    public virtual Hash<string, object> ToHash()
    {
      // only create new hash if one doesn't yet exist
      // populate it with non-changing data only on creation
      _hash ??= new Hash<string, object>
      {
        [nameof(Enabled)] = Enabled,
        [nameof(Order)] = Order,
        [nameof(Width)] = Width,
        [nameof(Padding)] = Padding.ToHash()
      };
      // update this every time
      _hash[nameof(Color)] = Color;
      return _hash;
    }
  }

  private sealed class PanelImage : PanelBase
  {
    public string Url { get; set; } =
      "https://i.postimg.cc/DZBNY3Tb/excavator.png";

    public override Hash<string, object> ToHash()
    {
      var hash = base.ToHash();
      hash.TryAdd(nameof(Url), Url);
      return hash;
    }
  }

  private sealed class TypePadding
  {
    public float Left { get; set; } = 0.05f;
    public float Right { get; set; } = 0.05f;
    public float Top { get; set; } = 0.05f;
    public float Bottom { get; set; } = 0.05f;

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

using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Oxide.Plugins
{
[Info("Magic Traveling Vendor Panel", "HunterZ", "1.0.0")]
[Description("Displays if the Traveling Vendor event is active")]
public class MagicTravelingVendorPanel : RustPlugin
{
  #region Class Fields
  [PluginReference] private readonly Plugin? MagicPanel;

  private PluginConfig? _pluginConfig; //Plugin Config
  private HashSet<TravellingVendor> _activeVendors = new();
  private bool _init;

  private enum UpdateEnum { All = 1, Panel = 2, Image = 3, Text = 4 }
  #endregion

  // this stuff that I got from other Magic Panel plugins seems
  //  overengineered, but I'm not touching it for now
  #region Setup & Loading
  protected override void LoadDefaultConfig() =>
    PrintWarning("Loading Default Config");

  protected override void LoadConfig()
  {
    // this replicates the base config logic, because that doesn't support
    //  custom paths
    string path = $"{Manager.ConfigPath}/MagicPanel/{Name}.json";
    var newConfig = new DynamicConfigFile(path);
    if (!newConfig.Exists())
    {
      LoadDefaultConfig();
      newConfig.Save();
    }

    try
    {
      newConfig.Load();
    }
    catch (Exception ex)
    {
      RaiseError("Failed to load config file (is the config file corrupt?) (" + ex.Message + ")");
      return;
    }

    newConfig.Settings.DefaultValueHandling = DefaultValueHandling.Populate;
    _pluginConfig = AdditionalConfig(newConfig.ReadObject<PluginConfig>());
    newConfig.WriteObject(_pluginConfig);
  }

  private PluginConfig AdditionalConfig(PluginConfig config)
  {
    config.Panel = new Panel
    {
      Image = new PanelImage
      {
        Enabled = config.Panel?.Image?.Enabled ?? true,
        Color = config.Panel?.Image?.Color ?? "#FFFFFFFF",
        Order = config.Panel?.Image?.Order ?? 6,
        Width = config.Panel?.Image?.Width ?? 1f,
        Url = config.Panel?.Image?.Url ?? "https://i.postimg.cc/cHsSN8t7/icecream.png",
        Padding = config.Panel?.Image?.Padding ?? new TypePadding(0.05f, 0.05f, 0.05f, 0.05f)
      }
    };
    config.PanelSettings = new PanelRegistration
    {
      BackgroundColor = config.PanelSettings?.BackgroundColor ?? "#FFF2DF08",
      Dock = config.PanelSettings?.Dock ?? "center",
      Order = config.PanelSettings?.Order ?? 0,
      Width = config.PanelSettings?.Width ?? 0.02f
    };
    return config;
  }

  private void OnServerInitialized()
  {
    _init = true;
    NextTick(() =>
    {
      _activeVendors = BaseNetworkable.serverEntities.OfType<TravellingVendor>().Where(CanShowPanel).ToHashSet();
      MagicPanelRegisterPanels();
    });
  }

  private void MagicPanelRegisterPanels()
  {
    if (null == MagicPanel)
    {
      PrintError("Missing plugin dependency MagicPanel: https://umod.org/plugins/magic-panel");
      UnsubscribeAll();
      return;
    }

    MagicPanel?.Call("RegisterGlobalPanel", this, Name, JsonConvert.SerializeObject(_pluginConfig?.PanelSettings), nameof(GetPanel));
  }

  private void CheckEvent()
  {
    // only care about changes to 0 or 1, because changes to/from other values
    //  don't require an icon update
    if (_activeVendors.Count >= 0 && _activeVendors.Count <= 1)
    {
      MagicPanel?.Call("UpdatePanel", Name, (int)UpdateEnum.Image);
    }
  }

  private void UnsubscribeAll()
  {
    Unsubscribe(nameof(OnEntitySpawned));
    Unsubscribe(nameof(OnEntityKill));
  }
  #endregion

  #region uMod Hooks

  private void OnEntitySpawned(TravellingVendor vendor)
  {
    if (!_init)
    {
      PrintWarning($"OnEntitySpawned(): Not initialized");
      return;
    }

    NextTick(() =>
    {
      if (!CanShowPanel(vendor)) return;
      _activeVendors.Add(vendor);
      CheckEvent();
    });
  }

  private void OnEntityKill(TravellingVendor vendor)
  {
    if (!_activeVendors.Remove(vendor))
    {
      PrintWarning("OnEntityKill(): Attempted to remove unknown Traveling Vendor?");
      return;
    }

    CheckEvent();
  }
  #endregion

  #region MagicPanel Hook
  private Hash<string, object> GetPanel()
  {
    if (null == _pluginConfig) return new(); // pathological
    var panel = _pluginConfig.Panel;
    var image = panel.Image;
    if (image != null)
    {
      image.Color = _activeVendors.Count != 0 ? _pluginConfig.ActiveColor : _pluginConfig.InactiveColor;
    }

    return panel.ToHash();
  }
  #endregion

  #region Helper Methods

  private bool CanShowPanel(TravellingVendor vendor) =>
    Interface.Call("MagicPanelCanShow", Name, vendor) is not bool r || r;
  #endregion

  #region Classes

  private class PluginConfig
  {
    [DefaultValue("#00FF00FF")]
    [JsonProperty(PropertyName = "Active Color")]
    public string ActiveColor { get; set; } = "";

    [DefaultValue("#FFFFFF1A")]
    [JsonProperty(PropertyName = "Inactive Color")]
    public string InactiveColor { get; set; } = "";

    [JsonProperty(PropertyName = "Panel Settings")]
    public PanelRegistration PanelSettings { get; set; } = new();

    [JsonProperty(PropertyName = "Panel Layout")]
    public Panel Panel { get; set; } = new();
  }

  private class PanelRegistration
  {
    public string Dock { get; set; } = "";
    public float Width { get; set; }
    public int Order { get; set; }
    public string BackgroundColor { get; set; } = "";
  }

  private class Panel
  {
    public PanelImage Image { get; set; } = new();

    public Hash<string, object> ToHash() =>
      new(){ [nameof(Image)] = Image.ToHash() };
  }

  private abstract class PanelType
  {
    public bool Enabled { get; set; }
    public string Color { get; set; } = "";
    public int Order { get; set; }
    public float Width { get; set; }
    public TypePadding Padding { get; set; } = new(0, 0, 0, 0);

    public virtual Hash<string, object> ToHash() =>
      new()
      {
        [nameof(Enabled)] = Enabled,
        [nameof(Color)] = Color,
        [nameof(Order)] = Order,
        [nameof(Width)] = Width,
        [nameof(Padding)] = Padding.ToHash(),
      };
  }

  private class PanelImage : PanelType
  {
    public string Url { get; set; } = "";

    public override Hash<string, object> ToHash()
    {
      Hash<string, object> hash = base.ToHash();
      hash[nameof(Url)] = Url;
      return hash;
    }
  }

  private class TypePadding
  {
    public float Left { get; set; }
    public float Right { get; set; }
    public float Top { get; set; }
    public float Bottom { get; set; }

    public TypePadding(float left, float right, float top, float bottom)
    {
      Left = left;
      Right = right;
      Top = top;
      Bottom = bottom;
    }

    public Hash<string, object> ToHash()
    {
      return new Hash<string, object>
      {
        [nameof(Left)] = Left,
        [nameof(Right)] = Right,
        [nameof(Top)] = Top,
        [nameof(Bottom)] = Bottom
      };
    }
  }
  #endregion
}
}

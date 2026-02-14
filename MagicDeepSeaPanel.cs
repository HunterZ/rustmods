using Newtonsoft.Json.Converters;
using Newtonsoft.Json;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using System;
// using System.ComponentModel;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Magic Deep Sea Panel", "HunterZ", "1.0.0")]
[Description("Displays Deep Sea open/close countdown in Magic Panel")]
public class MagicDeepSeaPanel : RustPlugin
{
  #region Class Fields

  [PluginReference] private readonly Plugin MagicPanel;

  private PluginConfig _pluginConfig;

  private Hash<string, object> _hash;

  private Timer _timer;

  private string _lastImageColor = "";
  private string _lastTextColor  = "";
  private string _lastText       = "";

  private enum UpdateEnum
  {
    All   = 1,
    Panel = 2,
    Image = 3,
    Text  = 4
  }

  #endregion

  #region Setup & Loading

  private void Init()
  {
    // nothing for now
  }

  protected override void LoadDefaultConfig()
  {
    PrintWarning("Loading Default Config");
  }

  protected override void LoadConfig()
  {
    var path = $"{Manager.ConfigPath}/MagicPanel/{Name}.json";
    var configFile = new DynamicConfigFile(path);
    if (!configFile.Exists())
    {
      LoadDefaultConfig();
    }
    try
    {
      _pluginConfig = configFile.ReadObject<PluginConfig>();
    }
    catch (Exception ex)
    {
      RaiseError("Failed to load config file (is the config file corrupt?) (" + ex.Message + ")");
      _pluginConfig = new PluginConfig();
    }

    configFile.WriteObject(_pluginConfig);
  }

  private void OnServerInitialized()
  {
    // generate a mostly-static dict for reporting data to Magic Panel
    _hash = _pluginConfig.Panel.ToHash();
    SetData("", "", "");

    MagicPanelRegisterPanels();
    CheckUpdate();
    // check on a 1Hz timer in case admin/plugins manually change Deep Sea times
    _timer = timer.Every(1.0f, CheckUpdate);
  }

  private void MagicPanelRegisterPanels()
  {
    if (MagicPanel == null)
    {
      PrintError("Missing plugin dependency MagicPanel: https://umod.org/plugins/magic-panel");
      return;
    }

    MagicPanel?.Call("RegisterGlobalPanel",
      this, Name, JsonConvert.SerializeObject(_pluginConfig.PanelSettings),
      nameof(GetPanel));
  }

  private static void DestroyTimer(Timer t)
  {
    if (TimerValid(t)) t.Destroy();
  }

  private static bool TimerValid(Timer t) => false == t?.Destroyed;

  // store changes to _hash, versus rebuilding the whole thing from scratch
  private bool SetData(string imageColor, string textColor, string text)
  {
    // dig Panel.Image and Panel.Text (PanelText) data nodes out of hash tree
    if (_hash["Image"] is not Hash<string, object> panelImage)
    {
      PrintError("SetData(): Failed to find Image node in _hash");
      return false;
    }
    if (_hash["Text"] is not Hash<string, object> panelText)
    {
      PrintError("SetData(): Failed to find Text node in _hash");
      return false;
    }

    // update Panel.Image's Color data
    panelImage["Color"] = imageColor;
    // update Panel.Text's Color and Text data
    panelText["Color"] = textColor;
    panelText["Text"] = text;

    return true;
  }

  private void CheckUpdate()
  {
    // TODO: also handle icon change on deep sea open/close

    var deepSeaManager = DeepSeaManager.ServerInstance;
    if (!deepSeaManager)
    {
      PrintError("GetPanel(): DeepSeaManager.ServerInstance is null");
      return;
    }

    var isBusy = deepSeaManager.IsBusy();
    // is Deep Sea open?
    var isOpen = deepSeaManager.IsOpen();
    // is Deep Sea irradiated? (about to close)
    var isRad =
      isOpen && deepSeaManager.HasFlag(DeepSeaManager.Flag_AboutToClose);
    // time to open or close (as appropriate)
    var timeSec = (int)(isOpen ?
      deepSeaManager.TimeToWipe : deepSeaManager.TimeToNextOpening);
    // largest relevant time denomination, and increments remaining within it
    var (timeDenom, timeInc) =
      timeSec >= 3600 ? ('h', timeSec / 3600) : // at least 1 hour
      timeSec >=   60 ? ('m', timeSec /   60) : // at least 1 minute
                        ('s', timeSec       ) ; // less than 1 minute
    // Puts($"isOpen={isOpen}, timeSec={timeSec}, timeDenom={timeDenom}, timeInc={timeInc}, busy={deepSeaManager.IsBusy()}");

    // calculate new panel data
    var imageColor =
      isRad  ? _pluginConfig.Panel.Image.ColorRadiation :
      isOpen ? _pluginConfig.Panel.Image.ColorOpened :
               _pluginConfig.Panel.Image.ColorClosed;
    var textColor =
      isRad  ? _pluginConfig.Panel.Text.ColorRadiation :
      isOpen ? _pluginConfig.Panel.Text.ColorOpened :
               _pluginConfig.Panel.Text.ColorClosed;
    var text = isBusy ? "..." : $"{timeInc}{timeDenom}";

    var imageChanged = imageColor != _lastImageColor;
    var textChanged  = textColor  != _lastTextColor ||
                            text       != _lastText;
    // avoid requesting a Magic Panel update if nothing changed
    if (!imageChanged && !textChanged)
    {
      // Puts("No change");
      return;
    }
    // record changes
    _lastImageColor = imageColor;
    _lastTextColor  = textColor;
    _lastText       = text;

    var updateType =
      imageChanged && textChanged ? UpdateEnum.All :
      imageChanged                ? UpdateEnum.Image :
                                    UpdateEnum.Text;

    // update panel data
    // _hash = _pluginConfig.Panel.ToHash();
    if (!SetData(imageColor, textColor, text))
    {
      return;
    }

    // ***** FIX THIS ENUM *****
    MagicPanel?.Call("UpdatePanel", Name, (int)updateType);
  }

  private void Unload()
  {
    _pluginConfig = null;
    _hash = null;
    DestroyTimer(_timer);
    _timer = null;
    _lastImageColor = "";
    _lastTextColor = "";
    _lastText = "";
  }

  #endregion

  #region MagicPanel Hook

  private Hash<string, object> GetPanel() => _hash;

  #endregion

  #region Classes

  private class PluginConfig
  {
    [JsonProperty(PropertyName = "Panel Settings")]
    public PanelRegistration PanelSettings { get; set; } = new();

    [JsonProperty(PropertyName = "Panel Layout")]
    public Panel Panel { get; set; } = new();
  }

  private class PanelRegistration
  {
    public string Dock { get; set; } = "leftbottom";
    public float Width { get; set; } = 0.075f;
    public int Order { get; set; } = 1;
    public string BackgroundColor { get; set; } = "#FFF2DF08";
  }

  private class Panel
  {
    public PanelImage Image { get; set; } = new();
    public PanelText Text { get; set; } = new();

    public Hash<string, object> ToHash() => new()
    {
      [nameof(Image)] = Image.ToHash(),
      [nameof(Text)] = Text.ToHash()
    };
  }

  // confusingly-named base class for PanelImage and PanelText
  private abstract class PanelType
  {
    public bool Enabled { get; set; } = true;
    [JsonIgnore]
    public string Color { get; set; } = "";
    [JsonProperty(PropertyName = "Color When Deep Sea Closed")]
    public string ColorClosed { get; set; } = "#FFFFFFFF";
    [JsonProperty(PropertyName = "Color When Deep Sea Opened")]
    public string ColorOpened { get; set; } = "#FFFFFFFF";
    [JsonProperty(PropertyName = "Color When Deep Sea Irradiated")]
    public string ColorRadiation { get; set; } = "#FFFFFFFF";
    public int Order { get; set; } = 0;
    public float Width { get; set; } = 0.5f;
    public TypePadding Padding { get; set; } = new();

    public virtual Hash<string, object> ToHash() => new()
    {
      [nameof(Enabled)] = Enabled,
      [nameof(Color)] = Color,
      [nameof(Order)] = Order,
      [nameof(Width)] = Width,
      [nameof(Padding)] = Padding.ToHash()
    };
  }

  private class PanelImage : PanelType
  {
    public string Url { get; set; } =
      "https://i.postimg.cc/zBksfzhH/582-5826004-anchor-png-image-white-anchor-icon-transparent-background.png";

    public override Hash<string, object> ToHash()
    {
      var hash = base.ToHash();
      hash[nameof(Url)] = Url;
      return hash;
    }
  }

  private class PanelText : PanelType
  {
    [JsonIgnore]
    public string Text { get; set; } = "";
    public int FontSize { get; set; } = 14;

    [JsonConverter(typeof(StringEnumConverter))]
    public TextAnchor TextAnchor { get; set; } = TextAnchor.MiddleCenter;

    public override Hash<string, object> ToHash()
    {
      var hash = base.ToHash();
      hash[nameof(Text)] = Text;
      hash[nameof(FontSize)] = FontSize;
      hash[nameof(TextAnchor)] = TextAnchor;
      return hash;
    }
  }

  private class TypePadding
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

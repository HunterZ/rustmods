using Newtonsoft.Json.Converters;
using Newtonsoft.Json;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Magic Deep Sea Panel", "HunterZ", "1.0.0")]
[Description("Displays Deep Sea open/close countdown in Magic Panel")]
public class MagicDeepSeaPanel : RustPlugin
{
  #region Class Fields

  [PluginReference] Plugin MagicPanel;

  private PluginConfig _pluginConfig;

  private Hash<string, object> _hash;

  private Timer _timer;

  private string _lastImageColor = "";
  private string _lastTextColor  = "";
  private string _lastText       = "";

  private enum DeepSeaState { Open, Rads, Closed, Busy }

  private enum UpdateType
  {
    All   = 1,
    Panel = 2,
    Image = 3,
    Text  = 4
  }

  #endregion

  #region Oxide API

  private void Init()
  {
    // nothing for now
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
    // generate a mostly-static dict for reporting data to Magic Panel
    _hash = _pluginConfig.Panel.ToHash();
    SetData("", "", "");

    MagicPanelRegisterPanels();
    CheckUpdate();
    // check on a 1Hz timer in case admin/plugins manually change Deep Sea times
    _timer = timer.Every(1.0f, CheckUpdate);
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

  #region Helper Methods

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

  private static string GetColor(PanelType panel, DeepSeaState deepSeaState) =>
    deepSeaState switch
    {
      DeepSeaState.Open   => panel.ColorOpened,
      DeepSeaState.Rads   => panel.ColorRadiation,
      _                   => panel.ColorClosed
    };

  private void CheckUpdate()
  {
    var deepSeaManager = DeepSeaManager.ServerInstance;
    if (!deepSeaManager)
    {
      PrintError("GetPanel(): DeepSeaManager.ServerInstance is null");
      return;
    }

    DeepSeaState deepSeaState;
    if (deepSeaManager.IsBusy())
    {
      deepSeaState = DeepSeaState.Busy;
    }
    else if (deepSeaManager.IsOpen())
    {
      deepSeaState =
        deepSeaManager.HasFlag(DeepSeaManager.Flag_AboutToClose) ?
          DeepSeaState.Rads : DeepSeaState.Open;
    }
    else
    {
      deepSeaState = DeepSeaState.Closed;
    }
    var timeSec = (int)(DeepSeaState.Closed == deepSeaState ?
      deepSeaManager.TimeToNextOpening : deepSeaManager.TimeToWipe);
    // largest relevant time denomination, and increments remaining within it
    var (timeDenom, timeInc) = timeSec switch
    {
      >= 3600 => ('h', timeSec / 3600), // at least 1 hour
      >=   60 => ('m', timeSec /   60), // at least 1 minute
      _       => ('s', timeSec       )  // less than 1 minute
    };

    // calculate new panel data
    var imageColor = GetColor(_pluginConfig.Panel.Image, deepSeaState);
    var textColor = GetColor(_pluginConfig.Panel.Text, deepSeaState);
    var text =
      DeepSeaState.Busy == deepSeaState ? "..." : $"{timeInc}{timeDenom}";

    var imageChanged = imageColor != _lastImageColor;
    var textChanged  = textColor  != _lastTextColor ||
                            text       != _lastText;
    // avoid requesting a Magic Panel update if nothing changed
    if (!imageChanged && !textChanged)
    {
      return;
    }
    // record changes
    _lastImageColor = imageColor;
    _lastTextColor  = textColor;
    _lastText       = text;

    var updateType = (imageChanged, textChanged) switch
    {
      (false, _    ) => UpdateType.Text,
      (true,  false) => UpdateType.Image,
      (true,  true ) => UpdateType.All
    };

    // update panel data
    if (!SetData(imageColor, textColor, text))
    {
      return;
    }

    MagicPanel?.Call("UpdatePanel", Name, (int)updateType);
  }

  #endregion

  #region MagicPanel API

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

  private Hash<string, object> GetPanel() => _hash;

  #endregion

  #region Classes

  private sealed class PluginConfig
  {
    [JsonProperty(PropertyName = "Panel Settings")]
    public PanelRegistration PanelSettings { get; set; } = new();

    [JsonProperty(PropertyName = "Panel Layout")]
    public Panel Panel { get; set; } = new();
  }

  private sealed class PanelRegistration
  {
    public string Dock { get; set; } = "leftbottom";
    public float Width { get; set; } = 0.075f;
    public int Order { get; set; } = 1;
    public string BackgroundColor { get; set; } = "#FFF2DF08";
  }

  private sealed class Panel
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
      [nameof(Order)] = Order,
      [nameof(Width)] = Width,
      [nameof(Padding)] = Padding.ToHash()
    };
  }

  private sealed class PanelImage : PanelType
  {
    public string Url { get; set; } =
      "https://i.postimg.cc/MZhXzvW2/anchor-512.png";

    public override Hash<string, object> ToHash()
    {
      var hash = base.ToHash();
      hash[nameof(Url)] = Url;
      return hash;
    }
  }

  private sealed class PanelText : PanelType
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

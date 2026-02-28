using System;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Magic Deep Sea Panel", "HunterZ", "1.0.0")]
[Description("Provides a Magic Panel that displays Deep Sea status")]
public class MagicDeepSeaPanel : RustPlugin
{
  #region Class Fields

  [PluginReference] Plugin MagicPanel;

  private PluginConfig _pluginConfig;
  private Timer _timer;

  private enum DeepSeaState { Open, Rads, Closed, Busy }

  private enum UpdateType
  {
    All   = 1,
    // Panel = 2,
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
    catch (Exception ex)
    {
      RaiseError($"Exception reading config file {path}: " + ex.Message);
      LoadDefaultConfig();
    }

    configFile.WriteObject(_pluginConfig);
  }

  private void OnServerInitialized() => MagicPanelRegisterPanels();

  private void Unload()
  {
    _pluginConfig = null;
    DestroyTimer(ref _timer);
  }

  #endregion

  #region MagicPanel API

  private void MagicPanelRegisterPanels()
  {
    if (MagicPanel == null)
    {
      PrintError("Missing plugin dependency MagicPanel: https://umod.org/plugins/magic-panel");
      ToggleTimer(false);
      return;
    }

    if (!_pluginConfig.PanelLayout.Image.Enabled &&
        !_pluginConfig.PanelLayout.Text.Enabled)
    {
      PrintWarning("Not registering panel because all items are disabled in config");
      ToggleTimer(false);
      return;
    }

    MagicPanel?.Call("RegisterGlobalPanel",
      this, Name, JsonConvert.SerializeObject(_pluginConfig.PanelSettings),
      nameof(GetPanel));

    ToggleTimer(true);
  }
/*
  private void PrintHash(Hash<string, object> hash, int indent = 0)
  {
    var padding = string.Empty.PadLeft(indent);
    foreach (var (key, value) in hash)
    {
      switch (value)
      {
        case int    i: Puts($"{padding}[{key}]<int>={i}"); break;
        case float  f: Puts($"{padding}[{key}]<float>=float {f}"); break;
        case string s: Puts($"{padding}[{key}]<string>={s}"); break;
        case Hash<string, object> h:
        {
          Puts($"{padding}[{key}]<hash>=");
          PrintHash(h, indent + 1);
          break;
        }
      }
    }
  }
*/
  private Hash<string, object> GetPanel() => _pluginConfig.PanelLayout.ToHash();
  // {
  //   var hash = _pluginConfig.PanelLayout.ToHash();
  //   PrintHash(hash);
  //   return hash;
  // }

  #endregion

  #region Helper Methods

  private static void DestroyTimer(ref Timer t)
  {
    if (TimerValid(t)) t.Destroy();
    t = null;
  }

  private static bool TimerValid(Timer t) => false == t?.Destroyed;

  private void ToggleTimer(bool enable)
  {
    DestroyTimer(ref _timer);
    if (enable)
    {
      // check at 1Hz in case admin/plugins manually change Deep Sea times
      _timer = timer.Every(1.0f, CheckUpdate);
    }
  }

  private string GetColor(DeepSeaState deepSeaState) => deepSeaState switch
  {
    DeepSeaState.Open   => _pluginConfig.ActiveColor,
    DeepSeaState.Rads   => _pluginConfig.IrradiatedColor,
    _                   => _pluginConfig.InactiveColor
  };

  private string GetText(DeepSeaManager deepSeaManager)
  {
    var deepSeaState = GetDeepSeaState(deepSeaManager);
    if (DeepSeaState.Busy == deepSeaState) return _pluginConfig.TextBusy;
    var timeSec = GetDeepSeaTime(deepSeaManager, deepSeaState);
    // largest relevant time denomination, and increments remaining within it
    // var (timeInc, timeDenom) = timeSec switch
    // {
    //   >= 3600 => (timeSec / 3600, 'h'), // at least 1 hour
    //   >=   60 => (timeSec /   60, 'm'), // at least 1 minute
    //   _       => (timeSec       , 's')  // less than 1 minute
    // };
    // return
    //   DeepSeaState.Busy == deepSeaState ? "..." : $"{timeInc}{timeDenom}";
    var timeSpan = TimeSpan.FromSeconds(timeSec);
    return string.Format(timeSec switch
    {
      >= 3600 => _pluginConfig.FormatHours,
      >=   60 => _pluginConfig.FormatMinutes,
      _       => _pluginConfig.FormatSeconds,
    }, timeSpan.Hours, timeSpan.Minutes, timeSpan.Seconds);
  }

  private static DeepSeaState GetDeepSeaState(DeepSeaManager deepSeaManager)
  {
    if (deepSeaManager.IsBusy()) return DeepSeaState.Busy;
    if (!deepSeaManager.IsOpen()) return DeepSeaState.Closed;
    return deepSeaManager.HasFlag(DeepSeaManager.Flag_AboutToClose) ?
      DeepSeaState.Rads : DeepSeaState.Open;
  }

  private static int GetDeepSeaTime(
    DeepSeaManager deepSeaManager, DeepSeaState deepSeaState) =>
    (int)(DeepSeaState.Closed == deepSeaState ?
      deepSeaManager.TimeToNextOpening : deepSeaManager.TimeToWipe);

  private void CheckUpdate()
  {
    var deepSeaManager = DeepSeaManager.ServerInstance;
    if (!deepSeaManager)
    {
      PrintError("GetPanel(): DeepSeaManager.ServerInstance is null");
      return;
    }

    var deepSeaState = GetDeepSeaState(deepSeaManager);
    var color = GetColor(deepSeaState);

    // calculate new panel data
    var imageChanged = false;
    var imageConfig = _pluginConfig.PanelLayout.Image;
    if (imageConfig.Enabled &&
        _pluginConfig.ColoredImage && color != imageConfig.Color)
    {
      imageConfig.Color = color;
      imageChanged = true;
    }
    var textChanged = false;
    var textConfig = _pluginConfig.PanelLayout.Text;
    if (textConfig.Enabled)
    {
      if (_pluginConfig.ColoredText && color != textConfig.Color)
      {
        textConfig.Color = color;
        textChanged = true;
      }

      var text = GetText(deepSeaManager);
      if (text != textConfig.Text)
      {
        textConfig.Text = text;
        textChanged = true;
      }
    }
    // avoid requesting a Magic Panel update if nothing changed
    if (!imageChanged && !textChanged)
    {
      return;
    }

    var updateType = (imageChanged, textChanged) switch
    {
      (false, _    ) => UpdateType.Text,
      (true,  false) => UpdateType.Image,
      (true,  true ) => UpdateType.All
    };

    // Puts($"***** imageChanged={imageChanged}, imageColor={imageConfig.Color}, textChanged={textChanged}, textColor={textConfig.Color}, textText={textConfig.Text}, updateType={updateType}");
    MagicPanel?.Call("UpdatePanel", Name, (int)updateType);
  }

  #endregion

  #region Classes

  private sealed class PluginConfig
  {
    [JsonProperty(PropertyName = "Active Color")]
    public string ActiveColor { get; set; } = "#FFFFFFBF";
    [JsonProperty(PropertyName = "Inactive Color")]
    public string InactiveColor { get; set; } = "#BFBFBF7F";
    [JsonProperty(PropertyName = "Irradiated Color")]
    public string IrradiatedColor { get; set; } = "#FFFFFFBF";

    [JsonProperty(PropertyName = "Apply Color To Image")]
    public bool ColoredImage  { get; set; } = true;
    [JsonProperty(PropertyName = "Apply Color To Text")]
    public bool ColoredText  { get; set; } = true;

    [JsonProperty(PropertyName = "Text Format When Hour(s) Remaining")]
    public string FormatHours  { get; set; } = "{0}h {1}m";
    [JsonProperty(PropertyName = "Text Format When Minute(s) Remaining")]
    public string FormatMinutes  { get; set; } = "{1}m";
    [JsonProperty(PropertyName = "Text Format When Second(s) Remaining")]
    public string FormatSeconds  { get; set; } = "{2}s";
    [JsonProperty(PropertyName = "Text When Deep Sea Busy")]
    public string TextBusy { get; set; } = "BUSY";

    [JsonProperty(PropertyName = "Panel Settings")]
    public PanelRegistration PanelSettings { get; set; } = new();

    [JsonProperty(PropertyName = "Panel Layout")]
    public PanelLayout PanelLayout { get; set; } = new();
  }

  private sealed class PanelRegistration
  {
    public string Dock { get; set; } = "centerupper";
    public float Width { get; set; } = 0.05f;
    public int Order { get; set; } = 1;
    public string BackgroundColor { get; set; } = "#FFF2DF08";
  }

  private sealed class PanelLayout
  {
    public PanelImage Image { get; set; } = new();
    public PanelText Text { get; set; } = new();

    // cache hash instead of regenerating it on every call/change
    [JsonIgnore]
    private Hash<string, object> _panelHash;

    public Hash<string, object> ToHash()
    {
      // only create new hash if none exists yet
      _panelHash ??= new Hash<string, object>();
      _panelHash[nameof(Image)] = Image.ToHash();
      _panelHash[nameof(Text)] = Text.ToHash();
      return _panelHash;
    }
  }

  private abstract class PanelBase
  {
    public bool Enabled { get; set; } = true;
    public int Order { get; set; } = 0;
    public float Width { get; set; } = 0.5f;
    public TypePadding Padding { get; set; } = new();

    public string Color { get; set; } = "#FFFFFFFF";

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
      "https://i.postimg.cc/MZhXzvW2/anchor-512.png";

    public override Hash<string, object> ToHash()
    {
      var hash = base.ToHash();
      hash.TryAdd(nameof(Url), Url);
      return hash;
    }
  }

  private sealed class PanelText : PanelBase
  {
    // this is not exposed to the config file, because it gets overwritten by
    //  logic
    [JsonIgnore]
    public string Text { get; set; } = "";

    public int FontSize { get; set; } = 14;

    [JsonConverter(typeof(StringEnumConverter))]
    public TextAnchor TextAnchor { get; set; } = TextAnchor.MiddleCenter;

    public override Hash<string, object> ToHash()
    {
      var hash = base.ToHash();
      // update this every time
      hash[nameof(Text)] = Text;
      // only add these if not already present, as they won't change
      hash.TryAdd(nameof(FontSize), FontSize);
      hash.TryAdd(nameof(TextAnchor), TextAnchor);
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

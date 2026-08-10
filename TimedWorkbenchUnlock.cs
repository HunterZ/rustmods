using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Timed Workbench Unlock", "HunterZ", "2.4.0")]
[Description("Provides timed/manual/disabled unlocking of workbenches")]
class TimedWorkbenchUnlock : RustPlugin
{
  #region Vars

  private readonly int[][] _workbenchIDsByTier = {
    new[]{ 210787554, 1524187186 },
    new[]{ -41896755 },
    new[]{ -1607980696 }
  };

  // periodic global status broadcast timer
  private Timer _broadcastTimer;

  // data managed via config file
  private ConfigData _configData;

  // "can't craft" warning suppression timers by userID
  private readonly Dictionary<ulong, (int, Timer)> _craftWarned = new();

  // string builder for producing status messages
  private readonly StringBuilder _statusBuilder = new();

  // workbench unlock announcement broadcast timers
  private readonly Timer[] _unlockTimers = { null, null, null };

  private readonly Dictionary<string, string> _prefixedPerms = new();

  #region Permission Strings

  private const string PermissionAdmin = "admin";
  private const string PermissionBroadcast = "broadcast";
  private const string PermissionInfo = "info";
  private const string PermissionModify = "modify";
  private const string PermissionReload = "reload";
  private const string PermissionReset = "reset";
  private const string PermissionSkipLock = "skiplock";
  private const string PermissionWipe = "wipe";

  #endregion Permission Strings

  #endregion Vars

  #region Utilities

  private static void DestroyTimer(ref Timer t)
  {
    if (TimerValid(t)) t.Destroy();
    t = null;
  }

  private static bool TimerValid(Timer t) => t is { Destroyed: false };

  // get time since wipe in seconds
  // if positive optional parameter is specified, it is used as a passthrough
  private float GetWipeElapsedSeconds(float wipeElapsedSeconds = -1.0f)
  {
    if (null == _configData) return 0;
    return wipeElapsedSeconds > 0 ? wipeElapsedSeconds : Convert.ToSingle(
      (DateTime.UtcNow - _configData.LastWipeUtc).TotalSeconds);
  }

  // returns unlock status for given workbench index (0-2 => level 1-3)
  // -1 => locked forever (requires manual unlock)
  //  0 => unlocked
  // >0 => number of seconds until auto unlock
  //
  // wipe elapsed seconds can optionally be specified to avoid repeated
  //  lookups when calling from a loop
  private int GetUnlockStatus(int index, float wipeElapsedSeconds = -1.0f)
  {
    if (null == _configData || index < 0 || index > 2) return 0;
    wipeElapsedSeconds = GetWipeElapsedSeconds(wipeElapsedSeconds);
    var unlockDelaySeconds = _configData.WbConfig[index];

    switch (unlockDelaySeconds)
    {
      // check for permanent lock
      case < 0:
        return -1;
      // determine auto unlock status
      case > 0:
      {
        var unlockSecondsRemaining = unlockDelaySeconds - wipeElapsedSeconds;
        return unlockSecondsRemaining > 0 ?
          Mathf.CeilToInt(unlockSecondsRemaining) : 0;
      }
      default:
        // unlockDelaySeconds == 0 => always unlocked
        return 0;
    }
  }

  // returns an array of unlock times for workbenches
  // see GetUnlockTime() for value meanings
  private int[] GetUnlockStatus()
  {
    var wipeElapsedSeconds = GetWipeElapsedSeconds();
    // unrolled loop for simplicity
    return new[]
    {
      GetUnlockStatus(0, wipeElapsedSeconds),
      GetUnlockStatus(1, wipeElapsedSeconds),
      GetUnlockStatus(2, wipeElapsedSeconds)
    };
  }

  // create a timer that will fire when the given workbench should unlock, or
  //  null if manually locked or already unlocked
  private Timer GetTimer(int index, float wipeElapsedSeconds = -1.0f)
  {
    if (index is < 0 or > 2) { return null; }

    wipeElapsedSeconds = GetWipeElapsedSeconds(wipeElapsedSeconds);
    var status = GetUnlockStatus(index, wipeElapsedSeconds);

    return status > 0 ?
      timer.Once(status, () => { ReportUnlock(index); }) : null;
  }

  // (re)set broadcast timer to fire at configured interval
  private void SetBroadcastTimer()
  {
    DestroyTimer(ref _broadcastTimer);
    // only set new timer if config value is positive (i.e. broadcast period
    //  in seconds)
    var broadcastConfig = _configData?.BroadcastConfig ?? 0;
    if (broadcastConfig > 0)
    {
      _broadcastTimer =
        timer.Every(broadcastConfig, () => { ReportStatus(null); });
    }
  }

  // destroy all existing timers managed by unlockTimers
  private void DestroyUnlockTimers()
  {
    for (var i = 0; i < _unlockTimers.Length; ++i)
    {
      DestroyTimer(ref _unlockTimers[i]);
    }
  }

  // (re)set all unlock announcement timers as appropriate
  // this should be called whenever unlock times might have changed
  private void SetUnlockTimers()
  {
    // timers don't auto-destruct, so wipe them to avoid double-firing
    DestroyUnlockTimers();
    var wipeElapsedSeconds = GetWipeElapsedSeconds();
    for (var i = 0; i < _unlockTimers.Length; ++i)
    {
      _unlockTimers[i] = GetTimer(i, wipeElapsedSeconds);
    }
  }

  private void DestroyWarnTimer(ulong userId)
  {
    if (!_craftWarned.Remove(userId, out var data)) return;
    var warnTimer = data.Item2;
    DestroyTimer(ref warnTimer);
  }

  // generate color locked/unlocked status text for twinfo command
  private string UnlockStatusString(int status, IPlayer player) =>
    status == 0 ?
      Colorize(lang.GetMessage("Unlocked", this, player.Id), "green") :
      Colorize(lang.GetMessage("Locked", this, player.Id), "red");

  // return true if player is null, server, or admin, or has permission, else
  //  reply with "no permission" message and return false
  private bool HasPermission(IPlayer player, string perm)
  {
    if (null == player) return false;

    var hasPermission =
      player.IsServer ||
      player.HasPermission(PrefixPermission(PermissionAdmin)) ||
      player.HasPermission(PrefixPermission(perm));

    if (!hasPermission) SendMessage(player, "NoPermission");

    return hasPermission;
  }

  // return a prefixed version of the given permission string
  // this is done to avoid hard-coding it, which would be a maintenance issue
  private string PrefixPermission(string perm)
  {
    if (!_prefixedPerms.TryGetValue(perm, out var prefixedPerm))
    {
      _prefixedPerms[perm] = prefixedPerm = Name.ToLower() + "." + perm;
    }
    return prefixedPerm;
  }

  // report user-friendly detailed status
  private void ReportStatus(IPlayer player)
  {
    // don't report status if nobody is online
    if (null == player && BasePlayer.activePlayerList.IsNullOrEmpty()) return;

    var status = GetUnlockStatus();
    // don't report status if everything is unlocked
    if (0 == status[0] && 0 == status[1] && 0 == status[2]) return;

    _statusBuilder.Clear();
    _statusBuilder.AppendLine(FormatMessage(player, "StatusBanner"));
    for (var index = 0; index < 3; ++index)
    {
      var wbNumStr = (index + 1).ToString(CultureInfo.CurrentCulture);
      switch (status[index])
      {
        case < 0:
        {
          _statusBuilder.AppendLine(
            FormatMessage(player, "StatusManual", wbNumStr));
          break;
        }

        case 0:
        {
          _statusBuilder.AppendLine(
            FormatMessage(player, "StatusUnlocked", wbNumStr));
          break;
        }

        case > 0:
        {
          _statusBuilder.AppendLine(FormatMessage(
            player, "StatusTime", wbNumStr,
            TimeSpan.FromSeconds(status[index]).ToString(
              "g", CultureInfo.CurrentCulture)));
          break;
        }
      }
    }

    SendRawMessage(player, _statusBuilder.ToString());
  }

  // report that a workbench has unlocked
  private void ReportUnlock(int index)
  {
    if (index is < 0 or > 2) return;
    // don't report unlock if nobody is online
    if (BasePlayer.activePlayerList.IsNullOrEmpty()) return;
    SendMessage(
      null, "UnlockNotice", (index + 1).ToString(CultureInfo.CurrentCulture));
  }

  // return tier-1 for given workbench ID, or -1 if not a workbench
  private int WorkbenchIndex(int itemID)
  {
    for (var tm1 = 0; tm1 < _workbenchIDsByTier.Length; ++tm1)
    {
      var wbTm1 = _workbenchIDsByTier[tm1];
      if (Array.IndexOf(wbTm1, itemID) >= 0) return tm1;
    }

    return -1;
  }

  // return whether crafting of the given item ID should be allowed
  private bool AllowAttempt(BasePlayer player, int itemID)
  {
    if (player is not { IsInTutorial: false } || true ==
        player.IPlayer?.HasPermission(PrefixPermission(PermissionSkipLock)))
    {
      return true;
    }

    var wbIndex = WorkbenchIndex(itemID);
    if (wbIndex < 0) return true; // not a workbench

    var status = GetUnlockStatus(wbIndex);
    if (0 == status) return true; // unlocked

    Warn(player, wbIndex, status);

    // block crafting
    return false;
  }

  private void Warn(BasePlayer player, int index, int status)
  {
    if (null == player?.IPlayer) return;

    // warn player (no spam protect on this)
    if (_configData.ReportAsSound) WarnSound(player);

    // abort here if text reports disabled for performance
    if (!_configData.ReportAsChat && !_configData.ReportAsToast)
    {
      return;
    }

    // abort if warn spam suppression already active for player+index
    var userId = player.userID.Get();
    var warned = _craftWarned.TryGetValue(userId, out var data);
    if (warned && index == data.Item1) return;

    var timeString = status > 0 ?
      TimeSpan.FromSeconds(status).ToString("g", CultureInfo.CurrentCulture) :
      null;
    var message = null == timeString ? "CannotCraftManual" : "CannotCraft";

    if (_configData is { ReportAsChat: true })
    {
      SendMessage(player.IPlayer, message, timeString);
    }

    if (_configData is { ReportAsToast: true })
    {
      SendToast(player.IPlayer, message, timeString);
    }

    // (re)set chat spam suppression timer
    if (warned)
    {
      // warn suppress timer was active for different workbench level
      // update record and reset timer
      var oldTimer = data.Item2;
      _craftWarned[userId] = (index, oldTimer);
      oldTimer.Reset(5.0f);
    }
    else
    {
      // add new record
      _craftWarned.Add(userId,
        (index, timer.Once(5.0f, () => { DestroyWarnTimer(userId); })));
    }
  }

  private static void WarnSound(BasePlayer player)
  {
    Effect.server.Run(
      "assets/prefabs/locks/keypad/effects/lock.code.denied.prefab",
      player.transform.position);
  }

  #endregion Utilities

  #region Messaging

  // load default message text dictionary
  protected override void LoadDefaultMessages()
  {
    lang.RegisterMessages(new Dictionary<string, string>
    {
      ["BroadcastDisabled"] = "Status broadcast disabled",
      ["BroadcastSet"] = "Status broadcast period set to {0} second(s)",
      ["CannotCraft"] = "Cannot craft this item (unlocks in {0})",
      ["CannotCraftManual"] = "Cannot craft this item (unlocks manually/never)",
      ["InfoBanner"] = "Now @{0} / T1 {1} (@{2}/{3}) / T2 {4} (@{5}/{6}) / T3 {7} (@{8}/{9})",
      ["InvalidWorkbench"] = "Invalid workbench number specified!",
      ["Locked"] = "locked",
      ["ModifiedManual"] = "WB {0} is now always locked",
      ["ModifiedTime"] = "WB {0} now unlocks in {1} second(s) after wipe",
      ["ModifiedUnlocked"] = "WB {0} is now always unlocked",
      ["NoPermission"] = "You don't have permission to use this command",
      ["PluginWipe"] = "Wipe time reset to {0}",
      ["ReloadConfig"] = "Config has been reloaded",
      ["ResetConfig"] = "Config has been reset",
      ["StatusBanner"] = "Workbenches are currently on a timed unlock system. Current status:",
      ["StatusManual"] = "- Workbench Level {0}: Unlocks manually/never",
      ["StatusTime"] = "- Workbench Level {0}: Unlocks in {1}",
      ["StatusUnlocked"] = "- Workbench Level {0}: Unlocked!",
      ["SyntaxError"] = "Syntax Error!",
      ["Unlocked"] = "unlocked",
      ["UnlockNotice"] = "Workbench Level {0} has unlocked, and can now be crafted!"
    }, this);
  }

  // format a message based on language dictionary, arguments, and destination
  private string FormatMessage(
    IPlayer player, string langCode, params object[] args)
  {
    var playerId = player is { IsServer: false } ? player.Id : null;
    var msg = string.Format(lang.GetMessage(langCode, this, playerId), args);
    // strip color markings out of console messages
    if (null == playerId)
    {
      // note: cannot supply StringComparison enum value here, as it results
      //  in a "not implemented" exception in some cases
      msg = msg
        .Replace("<color=red>", string.Empty)
        .Replace("<color=green>", string.Empty)
        .Replace("</color>", string.Empty);
    }
    return msg;
  }

  // send a message to player or server without additional formatting
  private void SendRawMessage(IPlayer player, string message)
  {
    if (null == player)
    {
      Server.Broadcast(message);
    }
    else
    {
      player.Reply(message);
    }
  }

  // send a message to player or server based on language dictionary and
  //  arguments
  // this is the primary method that should be used to communicate to users
  private void SendMessage(
    IPlayer player, string langCode, params object[] args)
  {
    SendRawMessage(player, FormatMessage(player, langCode, args));
  }

  private void SendToast(
    IPlayer player, string langCode, params object[] args)
  {
    if (player?.Object is not BasePlayer basePlayer ||
        !basePlayer.userID.IsSteamId()) return;
    basePlayer.ShowToast(0, FormatMessage(player, langCode, args));
  }

  // decorate a string with color codes
  // note that only red or green should be used, as FormatMessage() only
  //  strips those
  private static string Colorize(string str, string color) =>
    "<color=" + color + ">" + str + "</color>";

  #endregion Messaging

  #region Hooks

  // called by Oxide after config load
  protected void Init()
  {
    if (null == _configData)
    {
      PrintError("Init(): ERROR: Config not loaded; aborting");
      return;
    }

    if (_configData.WbConfig.Length != _workbenchIDsByTier.Length)
    {
      PrintWarning($"Init(): Got {_configData.WbConfig.Length} workbench unlock settings, but expected {_workbenchIDsByTier.Length}; resetting to default list");
      _configData.WbConfig = ConfigData.DefaultWbSeconds;
    }

    var serverWipeTime = SaveRestore.SaveCreatedTime;
    if (_configData.LastWipeUtc < serverWipeTime)
    {
      _configData.LastWipeUtc = serverWipeTime;
      Puts("Init(): Wipe detected - reset wipe time to " + serverWipeTime.ToString("R", CultureInfo.CurrentCulture));
    }

    // unconditionally save config here - this handles changes to the above,
    //  changes on version update, etc.
    SaveConfig();

    SetBroadcastTimer();
    SetUnlockTimers();

    // Permissions
    permission.RegisterPermission(
      PrefixPermission(PermissionAdmin), this);
    permission.RegisterPermission(
      PrefixPermission(PermissionBroadcast), this);
    permission.RegisterPermission(
      PrefixPermission(PermissionInfo), this);
    permission.RegisterPermission(
      PrefixPermission(PermissionModify), this);
    permission.RegisterPermission(
      PrefixPermission(PermissionReload), this);
    permission.RegisterPermission(
      PrefixPermission(PermissionReset), this);
    permission.RegisterPermission(
      PrefixPermission(PermissionSkipLock), this);
    permission.RegisterPermission(
      PrefixPermission(PermissionWipe), this);

    AddCovalenceCommand("twbroadcast", nameof(CommandBroadcast));
    AddCovalenceCommand("twinfo", nameof(CommandInfo));
    AddCovalenceCommand("twmodify", nameof(CommandModify));
    AddCovalenceCommand("twreload", nameof(CommandReload));
    AddCovalenceCommand("twreset", nameof(CommandReset));
    AddCovalenceCommand("twwipe", nameof(CommandWipe));
  }

  // called by Oxide on plugin unload
  protected void Unload()
  {
    // clean up any timers
    DestroyTimer(ref _broadcastTimer);
    DestroyUnlockTimers();
    foreach (var (_, (_, warnTimer)) in _craftWarned)
    {
      var temp = warnTimer;
      DestroyTimer(ref temp);
    }
    _craftWarned.Clear();
  }

  private object CanCraft(
    PlayerBlueprints playerBlueprints, ItemDefinition itemDefinition) =>
    _configData is not { BlockCraft: true } ||
    AllowAttempt(playerBlueprints.baseEntity, itemDefinition.itemid) ?
      null : false;

  // TODO: on Carbon, CanBuild is called instead - check with Oxide to see if
  //  this hook is effectively obsolete
  private object CanDeployItem(BasePlayer player, Deployer instance) =>
    _configData is not { BlockDeploy: true } ||
    AllowAttempt(player, instance?.GetOwnerItemDefinition()?.itemid ?? 0) ?
      null : new NonNull();

  private object CanResearchItem(BasePlayer player, Item item) =>
    _configData is not { BlockResearch: true } ||
    AllowAttempt(player, item?.info?.itemid ?? 0) ?
      null : new NonNull();

  private object CanBuild(Planner instance) =>
    _configData is not { BlockDeploy: true } ||
    AllowAttempt(
      instance?.GetOwnerPlayer(),
      instance?.GetOwnerItemDefinition()?.itemid ?? 0) ?
      null : new NonNull();

  private void OnPlayerConnected(BasePlayer player) =>
    ReportStatus(player.IPlayer);

  private void OnPlayerDisconnected(BasePlayer player, string reason) =>
    DestroyWarnTimer(player.userID.Get());

  #endregion Hooks

  #region Commands

  private void CommandBroadcast(IPlayer player, string command, string[] args)
  {
    if (null == _configData || !HasPermission(player, PermissionBroadcast))
    {
      return;
    }

    if (args.Length < 1)
    {
      player.Reply(string.Format(
        lang.GetMessage("SyntaxError", this, player.Id), command));
      return;
    }

    var newBroadcastConfig = Math.Max(Convert.ToInt32(args[0]), 0);
    if (newBroadcastConfig != _configData.BroadcastConfig)
    {
      SaveConfig();
      SetBroadcastTimer();
    }

    if (_configData.BroadcastConfig > 0)
    {
      SendMessage(
        player, "BroadcastSet", _configData.BroadcastConfig.ToString());
    }
    else
    {
      SendMessage(player, "BroadcastDisabled");
    }
  }

  private void CommandInfo(IPlayer player)
  {
    if (null == _configData || !HasPermission(player, PermissionInfo))
    {
      return;
    }

    var status = GetUnlockStatus();

    SendMessage(player, "InfoBanner",
      GetWipeElapsedSeconds().ToString(CultureInfo.CurrentCulture),
      UnlockStatusString(status[0], player),
      status[0].ToString(CultureInfo.CurrentCulture),
      _configData.WbConfig[0].ToString(CultureInfo.CurrentCulture),
      UnlockStatusString(status[1], player),
      status[1].ToString(CultureInfo.CurrentCulture),
      _configData.WbConfig[1].ToString(CultureInfo.CurrentCulture),
      UnlockStatusString(status[2], player),
      status[2].ToString(CultureInfo.CurrentCulture),
      _configData.WbConfig[2].ToString(CultureInfo.CurrentCulture)
    );
  }

  private void CommandModify(IPlayer player, string command, string[] args)
  {
    if (null == _configData || !HasPermission(player, PermissionModify))
    {
      return;
    }

    if (args.Length < 2)
    {
      player.Reply(string.Format(
        lang.GetMessage("SyntaxError", this, player.Id), command));
      return;
    }

    var wbIndex = Convert.ToInt32(args[0]) - 1;
    if (wbIndex is < 0 or > 2)
    {
      player.Reply(string.Format(
        lang.GetMessage("InvalidWorkbench", this, player.Id), command));
      return;
    }

    var wbConfig = Math.Max(Convert.ToInt32(args[1]), -1);
    if (wbConfig != _configData.WbConfig[wbIndex])
    {
      _configData.WbConfig[wbIndex] = wbConfig;
      SaveConfig();
      SetUnlockTimers();
    }

    switch (wbConfig)
    {
      case < 0:
        SendMessage(player, "ModifiedManual", args[0]);
        break;
      case > 0:
        SendMessage(player, "ModifiedTime", args[0], wbConfig.ToString(
          CultureInfo.CurrentCulture));
        break;
      default:
        SendMessage(player, "ModifiedUnlocked", args[0]);
        break;
    }
  }

  private void CommandReload(IPlayer player)
  {
    if (!HasPermission(player, PermissionReload)) return;

    LoadConfig();
    SetBroadcastTimer();
    SetUnlockTimers();
    SendMessage(player, "ReloadConfig");
    CommandInfo(player);
  }

  private void CommandReset(IPlayer player)
  {
    if (!HasPermission(player, PermissionReset)) return;

    LoadDefaultConfig();
    SaveConfig();
    SetBroadcastTimer();
    SetUnlockTimers();
    SendMessage(player, "ResetConfig");
    CommandInfo(player);
  }

  private void CommandWipe(IPlayer player)
  {
    if (null == _configData || !HasPermission(player, PermissionWipe))
    {
      return;
    }

    var currentTime = DateTime.UtcNow;
    _configData.LastWipeUtc = currentTime;
    SaveConfig();
    SetUnlockTimers();

    SendMessage(player, "PluginWipe", currentTime.ToString(
      "R", CultureInfo.CurrentCulture));
  }

  #endregion Commands

  #region Configuration

  private struct NonNull {}

  // need to append logic to check for map wipe since last load
  protected override void LoadConfig()
  {
    base.LoadConfig();
    try
    {
      _configData = Config.ReadObject<ConfigData>();
      if (null == _configData)
      {
        LoadDefaultConfig();
      }
    }
    catch (Exception ex)
    {
      PrintWarning($"LoadConfig(): Exception while loading configuration file:\n{ex}");
      LoadDefaultConfig();
    }
  }

  protected override void LoadDefaultConfig()
  {
    Puts("LoadDefaultConfig(): Creating a new configuration file");
    _configData = new ConfigData();
  }

  protected override void SaveConfig()
  {
    Puts("SaveConfig(): Saving config file");
    Config.WriteObject(_configData);
  }

  // config file data class
  private sealed class ConfigData
  {
    // default workbench unlock times
    [JsonIgnore]
    public static readonly int[] DefaultWbSeconds = { 86400, 172800, 259200 };

    [JsonProperty(PropertyName = "Global status broadcast interval in seconds (0 to disable)")]
    public int BroadcastConfig { get; set; } = 300;

    [JsonProperty(PropertyName = "Time that current wipe started (UTC)")]
    public DateTime LastWipeUtc { get; set; } = SaveRestore.SaveCreatedTime;

    [JsonProperty(PropertyName = "Workbench unlock times (seconds from start of wipe, or 0 for unlocked, or -1 for permanently locked)")]
    public int[] WbConfig { get; set; } = DefaultWbSeconds;

    [JsonProperty(PropertyName = "Block crafting of locked workbench(es)")]
    public bool BlockCraft = true;

    [JsonProperty(PropertyName = "Block deploying of locked workbench(es)")]
    public bool BlockDeploy = true;

    [JsonProperty(PropertyName = "Block researching of locked workbench(es)")]
    public bool BlockResearch = true;

    [JsonProperty(PropertyName = "Report blocking via chat message")]
    public bool ReportAsChat = false;

    [JsonProperty(PropertyName = "Report blocking via sound effect")]
    public bool ReportAsSound = true;

    [JsonProperty(PropertyName = "Report blocking via toast message")]
    public bool ReportAsToast = true;
  }

  #endregion Configuration
}

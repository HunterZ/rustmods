using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Oxide.Plugins
{
  [Info("Timed Workbench Unlock", "HunterZ", "2.2.1")]
  [Description("Provides timed/manual/disabled unlocking of workbenches")]
  class TimedWorkbenchUnlock : RustPlugin
  {
    #region Vars

    // ordered list of workbench IDs
    private static readonly int[] _WORKBENCH_ID = {
      1524187186, -41896755, -1607980696
    };

    // periodic global status broadcast timer
    private Timer _broadcastTimer;

    // data managed via config file
    private ConfigData _configData;

    // "can't craft" warning suppression timers by userID
    private readonly Dictionary<ulong, Timer> _craftWarned = new();

    // string builder for producing status messages
    private readonly StringBuilder _statusBuilder = new();

    // workbench unlock announcement broadcast timers
    private readonly Timer[] _unlockTimers = { null, null, null };

    #region Permission Strings

    private const string PERMISSION_ADMIN = "admin";
    private const string PERMISSION_BROADCAST = "broadcast";
    private const string PERMISSION_INFO = "info";
    private const string PERMISSION_MODIFY = "modify";
    private const string PERMISSION_RELOAD = "reload";
    private const string PERMISSION_RESET = "reset";
    private const string PERMISSION_SKIPLOCK = "skiplock";
    private const string PERMISSION_WIPE = "wipe";

    #endregion Permission Strings

    #endregion Vars

    #region Utilities

    // get time since wipe in seconds
    // if positive optional parameter is specified, it is used as a passthrough
    private double GetWipeElapsedSeconds(double wipeElapsedSeconds = -1.0)
    {
      if (null == _configData) return 0;
      return wipeElapsedSeconds > 0 ? wipeElapsedSeconds :
        (DateTime.UtcNow - _configData.LastWipeUTC).TotalSeconds;
    }

    // returns unlock status for given workbench index (0-2 => level 1-3)
    // -1 => locked forever (requires manual unlock)
    //  0 => unlocked
    // >0 => number of seconds until auto unlock
    //
    // wipe elapsed seconds can optionally be specified to avoid repeated
    //  lookups when calling from a loop
    private int GetUnlockStatus(int index, double wipeElapsedSeconds = -1.0)
    {
      if (null == _configData || index < 0 || index > 2) return 0;
      wipeElapsedSeconds = GetWipeElapsedSeconds(wipeElapsedSeconds);
      double unlockDelaySeconds = _configData.WBConfig[index];

      // check for permanent lock
      if (unlockDelaySeconds < 0.0) return -1;

      // determine auto unlock status
      if (unlockDelaySeconds > 0.0)
      {
        double unlockSecondsRemaining = unlockDelaySeconds - wipeElapsedSeconds;
        return unlockSecondsRemaining > 0 ?
          (int)Math.Ceiling(unlockSecondsRemaining) : 0;
      }

      // unlockDelaySeconds == 0.0 => always unlocked
      return 0;
    }

    // returns an array of unlock times for workbenches
    // see GetUnlockTime() for value meanings
    private int[] GetUnlockStatus()
    {
      double wipeElapsedSeconds = GetWipeElapsedSeconds();
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
    private Timer GetTimer(int index, double wipeElapsedSeconds = -1.0)
    {
      if (index < 0 || index > 2) { return null; }

      wipeElapsedSeconds = GetWipeElapsedSeconds(wipeElapsedSeconds);
      var status = GetUnlockStatus(index, wipeElapsedSeconds);

      if (status > 0) return timer.Once(status, () => { ReportUnlock(index); });

      return null;
    }

    // destroy existing broadcast timer (if any)
    private void DestroyBroadcastTimer()
    {
      if (null == _broadcastTimer) return;
      _broadcastTimer.Destroy();
      _broadcastTimer = null;
    }

    // (re)set broadcast timer to fire at configured interval
    private void SetBroadcastTimer()
    {
      DestroyBroadcastTimer();
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
      for (int i = 0; i < _unlockTimers.Length; ++i)
      {
        var unlockTimer = _unlockTimers[i];
        if (null == unlockTimer || unlockTimer.Destroyed) continue;
        unlockTimer.Destroy();
        _unlockTimers[i] = null;
      }
    }

    // (re)set all unlock announcement timers as appropriate
    // this should be called whenever unlock times might have changed
    private void SetUnlockTimers()
    {
      // timers don't auto-destruct, so wipe them to avoid double-firing
      DestroyUnlockTimers();
      var wipeElapsedSeconds = GetWipeElapsedSeconds();
      for (int i = 0; i < _unlockTimers.Length; ++i)
      {
        _unlockTimers[i] = GetTimer(i, wipeElapsedSeconds);
      }
    }

    private void DestroyWarnTimer(ulong userId)
    {
      if (_craftWarned.Remove(userId, out var wTimer) &&
          false == wTimer?.Destroyed)
      {
        wTimer.Destroy();
      }
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
        player.HasPermission(PrefixPermission(PERMISSION_ADMIN)) ||
        player.HasPermission(PrefixPermission(perm));

      if (!hasPermission) SendMessage(player, "NoPermission");

      return hasPermission;
    }

    // return a prefixed version of the given permission string
    // this is done to avoid hard-coding it, which would be a maintenance issue
    private string PrefixPermission(string perm) => Name.ToLower() + "." + perm;

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
      for (int index = 0; index < 3; ++index)
      {
        string wbNumStr = (index + 1).ToString(CultureInfo.CurrentCulture);
        switch (status[index])
        {
          case < 0:
          {
            _statusBuilder.AppendLine(
              FormatMessage(player, "StatusManual", wbNumStr));
          }
          break;

          case 0:
          {
            _statusBuilder.AppendLine(
              FormatMessage(player, "StatusUnlocked", wbNumStr));
          }
          break;

          case > 0:
          {
            _statusBuilder.AppendLine(FormatMessage(
              player, "StatusTime", wbNumStr,
              TimeSpan.FromSeconds(status[index]).ToString(
                "g", CultureInfo.CurrentCulture)));
          }
          break;
        }
      }

      SendRawMessage(player, _statusBuilder.ToString());
    }

    // report that a workbench has unlocked
    private void ReportUnlock(int index)
    {
      if (index < 0 || index > 2) return;
      // don't report unlock if nobody is online
      if (BasePlayer.activePlayerList.IsNullOrEmpty()) return;
      SendMessage(
        null, "UnlockNotice", (index + 1).ToString(CultureInfo.CurrentCulture));
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
      IPlayer player, string langCode, params string[] args)
    {
      var playerId = null == player || player.IsServer ? null : player.Id;
      var msg = string.Format(lang.GetMessage(langCode, this, playerId), args);
      // strip color markings out of console messages
      if (null == playerId)
        // note: cannot supply StringComparison enum value here, as it results
        //  in a "not implemented" exception in some cases
        msg = msg
          .Replace("<color=red>", string.Empty)
          .Replace("<color=green>", string.Empty)
          .Replace("</color>", string.Empty);
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
      IPlayer player, string langCode, params string[] args)
    {
      SendRawMessage(player, FormatMessage(player, langCode, args));
    }

    // decorate a string with color codes
    // note that only red or green should be used, as FormatMessage() only
    //  strips those
    private string Colorize(string str, string color) =>
      "<color=" + color + ">" + str + "</color>";

    #endregion Messaging

    #region Hooks

    // called by Oxide after config load
    protected void Init()
    {
      SetBroadcastTimer();
      SetUnlockTimers();

      // Permissions
      permission.RegisterPermission(
        PrefixPermission(PERMISSION_ADMIN), this);
      permission.RegisterPermission(
        PrefixPermission(PERMISSION_BROADCAST), this);
      permission.RegisterPermission(
        PrefixPermission(PERMISSION_INFO), this);
      permission.RegisterPermission(
        PrefixPermission(PERMISSION_MODIFY), this);
      permission.RegisterPermission(
        PrefixPermission(PERMISSION_RELOAD), this);
      permission.RegisterPermission(
        PrefixPermission(PERMISSION_RESET), this);
      permission.RegisterPermission(
        PrefixPermission(PERMISSION_SKIPLOCK), this);
      permission.RegisterPermission(
        PrefixPermission(PERMISSION_WIPE), this);

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
      DestroyBroadcastTimer();
      DestroyUnlockTimers();
      foreach (var (_, warnTimer) in _craftWarned)
      {
        if (null == warnTimer || warnTimer.Destroyed) continue;
        warnTimer.Destroy();
      }
      _craftWarned.Clear();
    }

    private object CanCraft(
      PlayerBlueprints playerBlueprints, ItemDefinition itemDefinition)
    {
      // suppress IDE null reference return warnings, as the method signature is
      //  imposed by Oxide
#pragma warning disable CS8603 // Possible null reference return.

      var player = playerBlueprints.baseEntity;
      // do nothing if any of the following are true:
      // - player reference invalid
      // - player in Tutorial Island
      // - player crafting something other than a workbench
      // - player has skiplock permission (note: admins not exempt by default)
      if (null == player ||
          player.IsInTutorial ||
          !_WORKBENCH_ID.Contains(itemDefinition.itemid) ||
          player.IPlayer.HasPermission(PrefixPermission(PERMISSION_SKIPLOCK)))
      {
        return null;
      }

      // get status
      var wbIndex = Array.IndexOf(_WORKBENCH_ID, itemDefinition.itemid);
      var status = GetUnlockStatus(wbIndex);

      // do nothing if workbench is unlocked
      if (0 == status) return null;

      var userId = player.userID.Get();

      // if player recently warned, block crafting silently
      if (_craftWarned.ContainsKey(userId)) return false;

      // warn player
      if (status > 0) // => timed lockout
      {
        var timeSpan = TimeSpan.FromSeconds(status);
        SendMessage(player.IPlayer, "CannotCraft", timeSpan.ToString(
          "g", CultureInfo.CurrentCulture));
      }
      else // status < 0 => permanent lockout
      {
        SendMessage(player.IPlayer, "CannotCraftManual");
      }

      // set warning suppression timer
      _craftWarned.Add(userId, timer.Once(5.0f, () =>
      {
        DestroyWarnTimer(userId);
      }));


      // block crafting
      return false;
#pragma warning restore CS8603 // Possible null reference return.
    }

    private void OnPlayerConnected(BasePlayer player) =>
      ReportStatus(player.IPlayer);

    private void OnPlayerDisconnected(BasePlayer player, string reason)
    {
      DestroyWarnTimer(player.userID.Get());
    }

    #endregion Hooks

    #region Commands

    private void CommandBroadcast(IPlayer player, string command, string[] args)
    {
      if (null == _configData || !HasPermission(player, PERMISSION_BROADCAST))
      {
        return;
      }

      if (args.Length < 1)
      {
        player.Reply(string.Format(
          lang.GetMessage("SyntaxError", this, player.Id), command));
        return;
      }

      _configData.BroadcastConfig = Convert.ToInt32(args[0]);
      if (_configData.BroadcastConfig < 0) _configData.BroadcastConfig = 0;
      SaveConfig();
      SetBroadcastTimer();

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
      if (null == _configData || !HasPermission(player, PERMISSION_INFO))
      {
        return;
      }

      var status = GetUnlockStatus();

      SendMessage(player, "InfoBanner",
        GetWipeElapsedSeconds().ToString(CultureInfo.CurrentCulture),
        UnlockStatusString(status[0], player),
        status[0].ToString(CultureInfo.CurrentCulture),
        _configData.WBConfig[0].ToString(CultureInfo.CurrentCulture),
        UnlockStatusString(status[1], player),
        status[1].ToString(CultureInfo.CurrentCulture),
        _configData.WBConfig[1].ToString(CultureInfo.CurrentCulture),
        UnlockStatusString(status[2], player),
        status[2].ToString(CultureInfo.CurrentCulture),
        _configData.WBConfig[2].ToString(CultureInfo.CurrentCulture)
      );
    }

    private void CommandModify(IPlayer player, string command, string[] args)
    {
      if (null == _configData || !HasPermission(player, PERMISSION_MODIFY))
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
      if (wbIndex < 0 || wbIndex > 2)
      {
        player.Reply(string.Format(
          lang.GetMessage("InvalidWorkbench", this, player.Id), command));
        return;
      }

      var wbConfig = Convert.ToInt32(args[1]);
      if (wbConfig < -1) wbConfig = -1;
      _configData.WBConfig[wbIndex] = wbConfig;
      SaveConfig();
      SetUnlockTimers();

      if (wbConfig < 0)
      {
        SendMessage(player, "ModifiedManual", args[0]);
      }
      else if (wbConfig > 0)
      {
        SendMessage(player, "ModifiedTime", args[0], wbConfig.ToString(
          CultureInfo.CurrentCulture));
      }
      else
      {
        SendMessage(player, "ModifiedUnlocked", args[0]);
      }
    }

    private void CommandReload(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_RELOAD)) return;

      LoadConfig();
      SetBroadcastTimer();
      SetUnlockTimers();
      SendMessage(player, "ReloadConfig");
      CommandInfo(player);
    }

    private void CommandReset(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_RESET)) return;

      LoadDefaultConfig();
      SaveConfig();
      SetBroadcastTimer();
      SetUnlockTimers();
      SendMessage(player, "ResetConfig");
      CommandInfo(player);
    }

    private void CommandWipe(IPlayer player)
    {
      if (null == _configData || !HasPermission(player, PERMISSION_WIPE))
      {
        return;
      }

      DateTime currentTime = DateTime.UtcNow;
      _configData.LastWipeUTC = currentTime;
      SaveConfig();
      SetUnlockTimers();

      SendMessage(player, "PluginWipe", currentTime.ToString(
        "R", CultureInfo.CurrentCulture));
    }

    #endregion Commands

    #region Configuration

    // need to append logic to check for map wipe since last load
    protected override void LoadConfig()
    {
      base.LoadConfig();
      try
      {
        _configData = Config.ReadObject<ConfigData>();
        if (null == _configData)
        {
          throw new JsonException("ReadObject() returned null");
        }
      }
      catch (Exception ex)
      {
        PrintWarning($"LoadConfig(): Exception while loading configuration file:\n{ex}");
        LoadDefaultConfig();
      }

      if (null == _configData) return;

      if (_configData.WBConfig.Length != 3)
      {
        PrintWarning($"LoadConfig(): Got {_configData.WBConfig.Length} workbench unlock settings, but expected 3; resetting to default list");
        _configData.WBConfig = ConfigData._DEFAULT_WB_SECONDS;
      }

      var serverWipeTime = SaveRestore.SaveCreatedTime;
      if (_configData.LastWipeUTC < serverWipeTime)
      {
        _configData.LastWipeUTC = serverWipeTime;
        Puts("LoadConfig(): Wipe detected - reset wipe time to " + serverWipeTime.ToString("R", CultureInfo.CurrentCulture));
      }

      SaveConfig();
    }

    protected override void LoadDefaultConfig()
    {
      Puts("LoadDefaultConfig(): Creating a new configuration file");
      _configData = new ConfigData();
    }

    protected override void SaveConfig() => Config.WriteObject(_configData);

    // config file data class
    private sealed class ConfigData
    {
      // default workbench unlock times
      [JsonIgnore]
      public static readonly int[] _DEFAULT_WB_SECONDS = {
        86400, 172800, 259200
      };

      [JsonProperty(PropertyName = "Global status broadcast interval in seconds (0 to disable)")]
      public int BroadcastConfig { get; set; } = 300;

      [JsonProperty(PropertyName = "Time that current wipe started (UTC)")]
      public DateTime LastWipeUTC { get; set; } = SaveRestore.SaveCreatedTime;

      [JsonProperty(PropertyName = "Workbench unlock times (seconds from start of wipe, or 0 for unlocked, or -1 for permanently locked)")]
      public int[] WBConfig { get; set; } = _DEFAULT_WB_SECONDS;
    }

    #endregion Configuration
  }
}

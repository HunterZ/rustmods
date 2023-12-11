using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Oxide.Plugins
{
  [Info("HZ Workbench", "HunterZ", "2.1.0")]
  [Description("Overhaul of original Timed Workbench plugin by DizzasTeR, which delays the ability to craft workbenches until some time has elapsed")]
  class HZWorkbench : RustPlugin
  {
    #region Vars
    // periodic global status broadcast timer
    protected Timer broadcastTimer;
    // workbench unlock announcement broadcast timers
    protected Timer[] unlockTimers = null;
    // set of users that have already received a "can't craft" warning
    private HashSet<int> craftWarnedSet;
    private static readonly List<int> DEFAULT_WB_SECONDS = new() {
      86400, 172800, 259200
    };
    // ordered list of workbench IDs
    private static readonly int[] ID_WORKBENCH = {
      1524187186, -41896755, -1607980696
    };

    # region Config Strings
    private const string CFG_KEY_BROADCAST = "BroadcastConfig";
    private const string CFG_KEY_WIPE = "LastWipeUTC";
    private static readonly string[] CFG_KEY_WB = {
      "WB1Config", "WB2Config", "WB3Config"
    };
    # endregion Config Strings

    # region Permission Strings
    private const string PERMISSION_ADMIN = "hzworkbench.admin";
    private const string PERMISSION_BROADCAST = "hzworkbench.broadcast";
    private const string PERMISSION_INFO = "hzworkbench.info";
    private const string PERMISSION_MODIFY = "hzworkbench.modify";
    private const string PERMISSION_RELOAD = "hzworkbench.reload";
    private const string PERMISSION_RESET = "hzworkbench.reset";
    private const string PERMISSION_SKIPLOCK = "hzworkbench.skiplock";
    private const string PERMISSION_WIPE = "hzworkbench.wipe";
    # endregion Permission Strings

    #endregion Vars

    #region Utilities

    // get time since wipe in seconds
    // if positive optional parameter is specified, it is used as a passthrough
    private double GetWipeElapsedSeconds(double wipeElapsedSeconds = -1.0)
    {
      return wipeElapsedSeconds > 0 ? wipeElapsedSeconds :
        (DateTime.UtcNow - GetLastWipe()).TotalSeconds;
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
      wipeElapsedSeconds = GetWipeElapsedSeconds(wipeElapsedSeconds);
      double unlockDelaySeconds = GetWbSeconds(index);

      if (unlockDelaySeconds < 0.0)
      {
        // no auto unlock (must be manually unlocked)
        return -1;
      }

      if (unlockDelaySeconds > 0.0)
      {
        // determine auto unlock status
        double unlockSecondsRemaining =
          unlockDelaySeconds - wipeElapsedSeconds;
        return unlockSecondsRemaining > 0 ?
          (int)unlockSecondsRemaining : 0;
      }

      // 0.0 => always unlocked
      return 0;
    }

    // returns an array of unlock times for workbenches
    // see GetUnlockTime() for value meanings
    private int[] GetUnlockStatus()
    {
      double wipeElapsedSeconds = GetWipeElapsedSeconds();
      // unrolled loop for simplicity
      return new int[]
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
      int status = GetUnlockStatus(index, wipeElapsedSeconds);

      if (status > 0)
      {
        return timer.Once(status, () => { ReportUnlock(index); });
      }

      return null;
    }

    private void SetBroadcastTimer()
    {
      // destroy existing timer (if any)
      if (broadcastTimer != null)
      {
        broadcastTimer.Destroy();
        broadcastTimer = null;
      }
      // only set new timer if config value is positive (i.e. broadcast period
      //  in seconds)
      int broadcastConfig = GetBroadcastConfig();
      if (broadcastConfig > 0)
      {
        broadcastTimer = timer.Every(broadcastConfig, () => { ReportStatus(null); });
      }
    }

    // destroy all existing timers managed by unlockTimers
    private void DestroyUnlockTimers()
    {
      if (unlockTimers == null) { return; }
      for (int index = 0; index < 3; ++index)
      {
        if (unlockTimers[index] == null) { continue; }
        unlockTimers[index].Destroy();
        unlockTimers[index] = null;
      }
    }

    // (re)set all unlock announcement timers as appropriate
    // this should be called whenever unlock times might have changed
    private void SetUnlockTimers()
    {
      // timers don't auto-destruct, so wipe them to avoid double-firing
      DestroyUnlockTimers();
      double wipeElapsedSeconds = GetWipeElapsedSeconds();
      unlockTimers = new Timer[]{
        GetTimer(0, wipeElapsedSeconds),
        GetTimer(1, wipeElapsedSeconds),
        GetTimer(2, wipeElapsedSeconds)
      };
    }

    // generate color locked/unlocked status text for twinfo command
    private string UnlockStatusString(int status) => status == 0 ?
      Colorize("unlocked", "green") : Colorize("locked", "red");

    // return true if player is null, server, or admin, or has permission, else
    //  reply with "no permission" message and return false
    private bool HasPermission(IPlayer player, string perm)
    {
      bool hasPermission = player == null ||
                           player.IsServer ||
                           player.HasPermission(PERMISSION_ADMIN) ||
                           player.HasPermission(perm);
      if (!hasPermission)
      {
        SendMessage(player, "NoPermission");
      }
      return hasPermission;
    }

    // report user-friendly detailed status
    private void ReportStatus(IPlayer player)
    {
      // don't report status if nobody is online
      if (player == null && BasePlayer.activePlayerList.IsNullOrEmpty()) { return; }
      var status = GetUnlockStatus();
      // don't report status if everything is unlocked
      if (status[0] == 0 && status[1] == 0 && status[2] == 0) { return; }
      StringBuilder sb = new();
      sb.AppendLine(FormatMessage(player, "StatusBanner"));
      for (int index = 0; index < 3; ++index)
      {
        string wbNumStr = (index + 1).ToString(CultureInfo.CurrentCulture);
        switch (status[index])
        {
          case < 0:
          {
            sb.AppendLine(FormatMessage(player, "StatusManual", wbNumStr));
          }
          break;

          case 0:
          {
            sb.AppendLine(FormatMessage(player, "StatusUnlocked", wbNumStr));
          }
          break;

          case > 0:
          {
            var timeSpan = TimeSpan.FromSeconds(status[index]);
            sb.AppendLine(FormatMessage(player, "StatusTime", wbNumStr, timeSpan.ToString("g", CultureInfo.CurrentCulture)));
          }
          break;
        }
      }
      SendRawMessage(player, sb.ToString());
    }

    // report that a workbench has unlocked
    private void ReportUnlock(int index)
    {
      // don't report unlock if nobody is online
      if (BasePlayer.activePlayerList.IsNullOrEmpty()) { return; }
      SendMessage(null, "UnlockNotice", (index + 1).ToString(CultureInfo.CurrentCulture));
    }

    #endregion Utilities

    #region Messaging

    // load default message text dictionary
    protected override void LoadDefaultMessages()
    {
      lang.RegisterMessages(new Dictionary<string, string>
      {
        ["BroadcastDisabled"]  = "Status broadcast disabled",
        ["BroadcastSet"] = "Status broadcast period set to {0} second(s)",
        ["CannotCraft"] = "Cannot craft this item (unlocks in {0})",
        ["CannotCraftManual"] = "Cannot craft this item (unlocks manually/never)",
        ["InfoBanner"] = "Now @{0} / T1 {1} (@{2}/{3}) / T2 {4} (@{5}/{6}) / T3 {7} (@{8}/{9})",
        ["InvalidWorkbench"] = "Invalid workbench number specified!",
        ["ModifiedManual"] = "WB {0} is now always locked",
        ["ModifiedTime"] = "WB {0} now unlocks in {1} second(s) after wipe",
        ["ModifiedUnlocked"] = "WB {0} is now always unlocked",
        ["NoPermission"] = "No access",
        ["PluginWipe"] = "Wipe time reset to {0}",
        ["ReloadConfig"] = "Config has been reloaded",
        ["ResetConfig"] = "Config has been reset",
        ["StatusBanner"] = "Workbenches are currently on a timed unlock system. Current status:",
        ["StatusManual"] = "- Workbench Level {0}: Unlocks manually/never",
        ["StatusTime"] = "- Workbench Level {0}: Unlocks in {1}",
        ["StatusUnlocked"] = "- Workbench Level {0}: Unlocked!",
        ["SyntaxError"] = "Syntax Error!",
        ["UnlockNotice"] = "Workbench Level {0} has unlocked, and can now be crafted!"
      }, this);
    }

    // format a message based on language dictionary, arguments, and
    //  destination
    private string FormatMessage(IPlayer player, string langCode, params string[] args)
    {
      bool isServer = player == null || player.IsServer;
      string msg = string.Format(lang.GetMessage(langCode, this, isServer ? null : player.Id), args);
      if (isServer)
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
      if (player == null)
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
    private void SendMessage(IPlayer player, string langCode, params string[] args)
    {
      SendRawMessage(player, FormatMessage(player, langCode, args));
    }

    // decorate a string with color codes
    // note that only red or green should be used, as FormatMessage() only
    //  strips those
    private string Colorize(string str, string color) => "<color=" + color + ">" + str + "</color>";

    #endregion Messaging

    #region Hooks

    // called by Oxide after config load
    protected void Init()
    {
      SetBroadcastTimer();
      SetUnlockTimers();
      craftWarnedSet = new();

      // Permissions
      permission.RegisterPermission(PERMISSION_ADMIN, this);
      permission.RegisterPermission(PERMISSION_BROADCAST, this);
      permission.RegisterPermission(PERMISSION_INFO, this);
      permission.RegisterPermission(PERMISSION_MODIFY, this);
      permission.RegisterPermission(PERMISSION_RELOAD, this);
      permission.RegisterPermission(PERMISSION_RESET, this);
      permission.RegisterPermission(PERMISSION_SKIPLOCK, this);
      permission.RegisterPermission(PERMISSION_WIPE, this);

      permission.GrantGroupPermission("admin", PERMISSION_ADMIN, this);

      // I think this ensures workbenches are craftable by default
      // not sure why this is needed, since it should already be true?
      foreach (var bp in from ItemBlueprint bp in ItemManager.GetBlueprints()
                         where ID_WORKBENCH.Contains(bp.targetItem.itemid)
                         select bp)
      {
        bp.defaultBlueprint = true;
        bp.userCraftable = true;
      }

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

      if (broadcastTimer != null)
      {
        broadcastTimer.Destroy();
        broadcastTimer = null;
      }

      DestroyUnlockTimers();
    }

    protected object CanCraft(PlayerBlueprints playerBlueprints, ItemDefinition itemDefinition)
    {
      // don't override craftable status for non-workbenches
      if (!ID_WORKBENCH.Contains(itemDefinition.itemid))
      {
        return null;
      }

      // don't override craftable status for players with skiplock
      IPlayer player = playerBlueprints?._baseEntity?.IPlayer;
      if (player == null || player.HasPermission(PERMISSION_SKIPLOCK))
      {
        return null;
      }

      double secondsLeft = GetUnlockStatus(Array.IndexOf(ID_WORKBENCH, itemDefinition.itemid));
      var playerHashCode = player.GetHashCode();
      var alreadyWarned = craftWarnedSet.Remove(playerHashCode);
      if (secondsLeft > 0)
      {
        // report "can't craft" due to delay in effect
        // ...but suppress every other call, as the game always checks twice?
        // TODO: maybe change to a per-user timed suppression to avoid DDoS?
        if (!alreadyWarned)
        {
          craftWarnedSet.Add(playerHashCode);
          var timeSpan = TimeSpan.FromSeconds(secondsLeft);
          SendMessage(player, "CannotCraft", timeSpan.ToString("g", CultureInfo.CurrentCulture));
        }
        return false;
      }

      if (secondsLeft < 0)
      {
        // report "can't craft" due to manual/permanent lockout
        // ...but suppress every other call, as the game always checks twice?
        // TODO: maybe change to a per-user timed suppression to avoid DDoS?
        if (!alreadyWarned)
        {
          craftWarnedSet.Add(playerHashCode);
          SendMessage(player, "CannotCraftManual");
        }
        return false;
      }

      // don't override - no lockout in effect
      return null;
    }

    protected void OnPlayerConnected(BasePlayer player)
    {
      ReportStatus(player.IPlayer);
    }

    #endregion Hooks

    #region Commands

    private void CommandBroadcast(IPlayer player, string command, string[] args)
    {
      if (!HasPermission(player, PERMISSION_BROADCAST)) { return; }

      if (args.Length < 1)
      {
        player.Reply(string.Format(lang.GetMessage("SyntaxError", this, player.Id), command));
        return;
      }

      int broadcastConfig = Convert.ToInt32(args[0]);
      SetBroadcastConfig(broadcastConfig);
      SetBroadcastTimer();

      if (broadcastConfig > 0)
      {
        SendMessage(player, "BroadcastSet", args[0]);
      }
      else
      {
        SendMessage(player, "BroadcastDisabled");
      }
    }

    private void CommandInfo(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_INFO)) { return; }

      var status = GetUnlockStatus();

      SendMessage(player, "InfoBanner",
        GetWipeElapsedSeconds().ToString(CultureInfo.CurrentCulture),
        UnlockStatusString(status[0]),
        status[0].ToString(CultureInfo.CurrentCulture),
        GetWbSeconds(0).ToString(CultureInfo.CurrentCulture),
        UnlockStatusString(status[1]),
        status[1].ToString(CultureInfo.CurrentCulture),
        GetWbSeconds(1).ToString(CultureInfo.CurrentCulture),
        UnlockStatusString(status[2]),
        status[2].ToString(CultureInfo.CurrentCulture),
        GetWbSeconds(2).ToString(CultureInfo.CurrentCulture)
      );
    }

    private void CommandModify(IPlayer player, string command, string[] args)
    {
      if (!HasPermission(player, PERMISSION_MODIFY)) { return; }

      if (args.Length < 2)
      {
        player.Reply(string.Format(lang.GetMessage("SyntaxError", this, player.Id), command));
        return;
      }

      int wbIndex;
      switch (args[0])
      {
        case "1": wbIndex = 0; break;
        case "2": wbIndex = 1; break;
        case "3": wbIndex = 2; break;
        default:
        {
          player.Reply(string.Format(lang.GetMessage("InvalidWorkbench", this, player.Id), command));
          return;
        }
      }

      int seconds = Convert.ToInt32(args[1]);
      SetWbSeconds(wbIndex, seconds);
      SetUnlockTimers();

      if (seconds < 0)
      {
        SendMessage(player, "ModifiedManual", args[0]);
      }
      else if (seconds > 0)
      {
        SendMessage(player, "ModifiedTime", args[0], seconds.ToString(CultureInfo.CurrentCulture));
      }
      else
      {
        SendMessage(player, "ModifiedUnlocked", args[0]);
      }
    }

    private void CommandReload(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_RELOAD)) { return; }

      LoadConfig();
      SetUnlockTimers();
      SendMessage(player, "ReloadConfig");
    }

    private void CommandReset(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_RESET)) { return; }

      SetWbSeconds(DEFAULT_WB_SECONDS);
      SetUnlockTimers();

      SendMessage(player, "ResetConfig");
      CommandInfo(player);
    }

    private void CommandWipe(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_WIPE)) { return; }

      DateTime wipeTime = DateTime.UtcNow;
      SetLastWipe(wipeTime);
      SetUnlockTimers();

      SendMessage(player, "PluginWipe", wipeTime.ToString("R", CultureInfo.CurrentCulture));
      Puts("CommandWipe(): Reset wipe time to " + wipeTime.ToString("R", CultureInfo.CurrentCulture));
    }

    #endregion Commands

    #region Configuration

    // need to append logic to check for map wipe since last load
    protected override void LoadConfig()
    {
      base.LoadConfig();
      var serverWipeTime = SaveRestore.SaveCreatedTime;
      if (GetLastWipe() < serverWipeTime)
      {
        SetLastWipe(serverWipeTime);
        Puts("LoadConfig(): Reset wipe time to " + serverWipeTime.ToString("R", CultureInfo.CurrentCulture));
      }
    }

    protected override void LoadDefaultConfig()
    {
      Puts("LoadDefaultConfig(): Creating a new configuration file");
      // note: don't use SetXYZ() here because they will cause redundant saves
      Config[CFG_KEY_BROADCAST] = 300;
      Config[CFG_KEY_WIPE] = DateTime.UtcNow;
      Config[CFG_KEY_WB[0]] = DEFAULT_WB_SECONDS[0];
      Config[CFG_KEY_WB[1]] = DEFAULT_WB_SECONDS[1];
      Config[CFG_KEY_WB[2]] = DEFAULT_WB_SECONDS[2];
    }

    int GetBroadcastConfig()
    {
      return (int)Config[CFG_KEY_BROADCAST];
    }

    void SetBroadcastConfig(int broadcastConfig)
    {
      Config[CFG_KEY_BROADCAST] = broadcastConfig;
      SaveConfig();
    }

    DateTime GetLastWipe()
    {
      return (DateTime)Config[CFG_KEY_WIPE];
    }

    void SetLastWipe(DateTime wipeTime)
    {
      Config[CFG_KEY_WIPE] = wipeTime;
      SaveConfig();
    }

    int GetWbSeconds(int index)
    {
      return (int)Config[CFG_KEY_WB[index]];
    }

    void SetWbSeconds(List<int> seconds)
    {
      Config[CFG_KEY_WB[0]] = seconds[0];
      Config[CFG_KEY_WB[1]] = seconds[1];
      Config[CFG_KEY_WB[2]] = seconds[2];
      SaveConfig();
    }

    void SetWbSeconds(int index, int seconds)
    {
      if (seconds < 0) { seconds = -1; }
      Config[CFG_KEY_WB[index]] = seconds;
      SaveConfig();
    }

    #endregion Configuration
  }
}

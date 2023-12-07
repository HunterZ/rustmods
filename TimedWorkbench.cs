using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Oxide.Plugins
{
  [Info("Timed Workbench Redux", "HunterZ", "2.0.0")]
  [Description("Overhaul of original plugin by DizzasTeR, which delays the ability to craft workbenches until some time has elapsed")]
  class TimedWorkbench : RustPlugin
  {
    #region Vars
    // data managed via config file
    private Configuration config;
    // periodic global status broadcast timer
    protected Timer broadcastTimer;
    // workbench unlock announcement broadcast timers
    protected Timer[] unlockTimers = null;
    // set of users that have already received a "can't craft" warning
    private HashSet<int> craftWarnedSet;
    private static readonly long[] DEFAULT_WB_SECONDS = { 86400L, 172800L, 259200L };
    // ordered list of workbench IDs
    private static readonly long[] ID_WORKBENCH = { 1524187186L, -41896755L, -1607980696L };

    # region Permission Strings

    const string PERMISSION_ADMIN = "timedworkbench.admin";
    const string PERMISSION_INFO = "timedworkbench.info";
    const string PERMISSION_MODIFY = "timedworkbench.modify";
    const string PERMISSION_RELOAD = "timedworkbench.reload";
    const string PERMISSION_RESET = "timedworkbench.reset";
    const string PERMISSION_SKIPLOCK = "timedworkbench.skiplock";
    const string PERMISSION_WIPE = "timedworkbench.wipe";

    # endregion Permission Strings

    #endregion Vars

    #region Utilities

    // get time since wipe in seconds
    // if positive optional parameter is specified, it is used as a passthrough
    private double GetWipeElapsedSeconds(double wipeElapsedSeconds = -1.0)
    {
      return wipeElapsedSeconds > 0 ?
        wipeElapsedSeconds : (DateTime.UtcNow - config.LastWipe).TotalSeconds;
    }

    // returns unlock status for given workbench index (0-2 => level 1-3)
    // -1 => locked forever (requires manual unlock)
    //  0 => unlocked
    // >0 => number of seconds until auto unlock
    //
    // wipe elapsed seconds can optionally be specified to avoid repeated
    //  lookups when calling from a loop
    private long GetUnlockStatus(int index, double wipeElapsedSeconds = -1.0)
    {
      wipeElapsedSeconds = GetWipeElapsedSeconds(wipeElapsedSeconds);
      double unlockDelaySeconds = config.WBSeconds[index];

      if (unlockDelaySeconds < 0.0)
      {
        // no auto unlock (must be manually unlocked)
        return -1L;
      }

      if (unlockDelaySeconds > 0.0)
      {
        // determine auto unlock status
        double unlockSecondsRemaining =
          unlockDelaySeconds - wipeElapsedSeconds;
        return unlockSecondsRemaining > 0 ?
          (long)unlockSecondsRemaining : 0L;
      }

      // 0.0 => always unlocked
      return 0L;
    }

    // returns an array of unlock times for workbenches
    // see GetUnlockTime() for value meanings
    private long[] GetUnlockStatus()
    {
      double wipeElapsedSeconds = GetWipeElapsedSeconds();
      // unrolled loop for simplicity
      return new long[]
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
      long status = GetUnlockStatus(index, wipeElapsedSeconds);

      if (status > 0)
      {
        return timer.Once(status, () => { ReportUnlock(index); });
      }

      return null;
    }

    // destroy all existing timers managed by unlockTimers
    private void DestroyTimers()
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
      DestroyTimers();
      double wipeElapsedSeconds = (DateTime.UtcNow - config.LastWipe).TotalSeconds;
      unlockTimers = new Timer[]{
        GetTimer(0, wipeElapsedSeconds),
        GetTimer(1, wipeElapsedSeconds),
        GetTimer(2, wipeElapsedSeconds)
      };
    }

    // generate color locked/unlocked status text for twinfo command
    private string UnlockStatusString(long status) => status == 0 ?
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
        string wbNumStr = (index + 1).ToString();
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
            sb.AppendLine(FormatMessage(player, "StatusTime", wbNumStr, timeSpan.ToString("g")));
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
      SendMessage(null, "UnlockNotice", (index + 1).ToString());
    }

    #endregion Utilities

    #region Messaging

    // load default message text dictionary
    protected override void LoadDefaultMessages()
    {
      lang.RegisterMessages(new Dictionary<string, string>
      {
        ["CannotCraft"] = "Cannot craft this item (unlocks in {0})",
        ["CannotCraftManual"] = "Cannot craft this item (unlocks manually/never)",
        ["InfoBanner"] = "Now @{0} / T1 {1} (@{2}) / T2 {3} (@{4}) / T3 {5} (@{6})",
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
        msg = msg.Replace("<color=red>", "").Replace("<color=green>", "").Replace("</color>", "");
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

    protected void Init()
    {
      broadcastTimer = timer.Repeat(300f, 0, () => { ReportStatus(null); });
      SetUnlockTimers();
      craftWarnedSet = new();

      // Permissions
      permission.RegisterPermission(PERMISSION_ADMIN, this);
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

      AddCovalenceCommand("twinfo", nameof(CommandInfo));
      AddCovalenceCommand("twmodify", nameof(CommandModify));
      AddCovalenceCommand("twreload", nameof(CommandReload));
      AddCovalenceCommand("twreset", nameof(CommandReset));
      AddCovalenceCommand("twwipe", nameof(CommandWipe));
    }

    protected void OnNewSave(string filename)
    {
      // Update the LastWipe in config as a new wipe was detected.
      config.LastWipe = DateTime.UtcNow;
      SaveConfig();

      Puts("OnNewSave(): Reset wipe time to " + config.LastWipe.ToString("R"));
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
          SendMessage(player, "CannotCraft", timeSpan.ToString("g"));
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

    private void CommandInfo(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_INFO)) { return; }

      var status = GetUnlockStatus();

      SendMessage(player, "InfoBanner",
        GetWipeElapsedSeconds().ToString(),
        UnlockStatusString(status[0]), status[0].ToString(),
        UnlockStatusString(status[1]), status[1].ToString(),
        UnlockStatusString(status[2]), status[2].ToString()
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

      long wbIndex;
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

      long seconds = Convert.ToInt64(args[1]);
      if (seconds < 0) { seconds = -1; }
      config.WBSeconds[wbIndex] = seconds;

      SaveConfig();

      if (seconds < 0)
      {
        SendMessage(player, "ModifiedManual", args[0]);
      }
      else if (seconds > 0)
      {
        SendMessage(player, "ModifiedTime", args[0], seconds.ToString());
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
      SendMessage(player, "ReloadConfig");
    }

    private void CommandReset(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_RESET)) { return; }

      config.WBSeconds = DEFAULT_WB_SECONDS;
      SaveConfig();
      SendMessage(player, "ResetConfig");
      CommandInfo(player);
    }

    private void CommandWipe(IPlayer player)
    {
      if (!HasPermission(player, PERMISSION_WIPE)) { return; }

      config.LastWipe = DateTime.UtcNow;
      SaveConfig();

      SendMessage(player, "PluginWipe", config.LastWipe.ToString("R"));
      Puts("CommandWipe(): Reset wipe time to " + config.LastWipe.ToString("R"));
    }

    #endregion Commands

    #region Configuration

    class Configuration
    {
      [JsonProperty(PropertyName = "Last wipe time in UTC")]
      public DateTime LastWipe { get; set; } = DateTime.UtcNow;

      [JsonProperty(PropertyName = "Workbench unlock delays in seconds (-1 for no auto unlock, 0 for always unlocked)")]
      public long[] WBSeconds { get; set; } = DEFAULT_WB_SECONDS;
    }

    protected override void LoadConfig()
    {
      base.LoadConfig();
      try
      {
        config = Config.ReadObject<Configuration>();
      }
      catch
      {
        config = null;
      }
      if (config == null)
      {
        LoadDefaultConfig();
      }
      var serverWipeTime = SaveRestore.SaveCreatedTime;
      if (config.LastWipe < serverWipeTime)
      {
        config.LastWipe = serverWipeTime;
        Puts("LoadConfig(): Reset wipe time to " + config.LastWipe.ToString("R"));
      }
      SaveConfig();
    }

    protected override void LoadDefaultConfig()
    {
      string configPath = $"{Interface.Oxide.ConfigDirectory}{Path.DirectorySeparatorChar}{Name}.json";
      Puts($"Config file not found, creating a new configuration file at {configPath}");
      config = new Configuration();
    }

    protected override void SaveConfig()
    {
      SetUnlockTimers();
      Config.WriteObject(config);
    }

    #endregion Configuration
  }
}

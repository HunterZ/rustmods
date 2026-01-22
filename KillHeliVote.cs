using Newtonsoft.Json;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Core;
using System.Collections.Generic;
using System;
using Facepunch;
using UnityEngine;

namespace Oxide.Plugins;

[Info("Kill Heli Vote", "HunterZ/Snaplatack", "2.0.1")]
[Description("Players can vote to kill all Patrol Helicopters")]
public class KillHeliVote : RustPlugin
{
  [PluginReference]
  private readonly Plugin HeliSignals, LootDefender;
  private readonly HashSet<string> _bannedPlayers = new();
  private readonly HashSet<string> _eligiblePlayers = new();
  private readonly HashSet<string> _votedPlayers = new();
  private readonly HashSet<PatrolHelicopter> _heliCache = new();
  private Timer _msgTimer;
  private string _adminPerm;  // backing for AdminPerm
  private string _bannedPerm; // backing for BannedPerm
  private string _votePerm;   // backing for VotePerm

  #region Setup

  private void Init()
  {
    Unsubscribe(nameof(OnEntityTakeDamage));
    Unsubscribe(nameof(OnLockedEntity));
    Unsubscribe(nameof(OnUnlockedEntity));
  }

  private void OnServerInitialized()
  {
    // setup hooks and commands
    AddCovalenceCommand(
      _config.CmdPerms.KillCommand, nameof(HeliKillCmd), AdminPerm);
    AddCovalenceCommand(
      _config.CmdPerms.VoteCommand, nameof(HeliVoteCmd), VotePerm);
    permission.RegisterPermission(BannedPerm, this);
    if (_config.Settings.ResetVoteOnDamage)
    {
      Subscribe(nameof(OnEntityTakeDamage));
    }
    if (_config.Settings.IgnoreLootDefender)
    {
      Subscribe(nameof(OnLockedEntity));
      Subscribe(nameof(OnUnlockedEntity));
    }

    // populate _bannedPlayers and _eligiblePlayers for all connected players
    foreach (var player in BasePlayer.activePlayerList)
    {
      UpdatePlayer(GetID(player));
    }

    // check for active helis
    // TODO: this should maybe be a coroutine
    foreach (var entity in BaseNetworkable.serverEntities)
    {
      if (entity is PatrolHelicopter heli && !ShouldIgnore(heli))
      {
        _heliCache.Add(heli);
      }
    }
    if (_heliCache.Count > 0)
    {
      StartHeliAnnouncements();
    }
  }

  private void Unload()
  {
    _bannedPlayers.Clear();
    _eligiblePlayers.Clear();
    _votedPlayers.Clear();
    _heliCache.Clear();
    DestroyTimer(_msgTimer);
    _adminPerm = _bannedPerm = _votePerm = null;
  }

  #endregion

  #region Hooks

  private void OnEntitySpawned(PatrolHelicopter heli)
  {
    if (!heli || ShouldIgnore(heli)) return;

    _heliCache.Add(heli);

    // only start announcing on 0->1 helis
    if (_heliCache.Count == 1) StartHeliAnnouncements();
  }

  private object OnEntityTakeDamage(PatrolHelicopter heli, HitInfo hitInfo)
  {
    // abort if heli invalid, no votes recorded, or not a tracked heli
    if (!heli || _votedPlayers.Count == 0 || !_heliCache.Contains(heli))
    {
      return null;
    }

    // prevent invalid or banned players from resetting votes
    var player = hitInfo.InitiatorPlayer;
    if (!player || _bannedPlayers.Contains(GetID(player))) return null;

    // NOTE: we don't need to check if damage reset is enabled in config,
    //  because the hook only gets executed if the option is enabled
    _votedPlayers.Clear();
    SendGlobalMsg(MsgKeys.HeliAttacked);
    return null;
  }

  private void OnEntityKill(PatrolHelicopter heli)
  {
    // NOTE: this is also called by OnLockedEntity(), so it should only do
    //  cleanup stuff for now
    if (!heli) return;

    _heliCache.Remove(heli);
    if (_heliCache.Count != 0) return;

    // that was the only heli, so clear votes and stop announcing
    _votedPlayers.Clear();
    DestroyTimer(_msgTimer);
  }

  private void OnPlayerConnected(BasePlayer player) =>
    UpdatePlayer(GetID(player));

  private void OnPlayerDisconnected(BasePlayer player, string reason)
  {
    if (!player) return;

    var playerID = GetID(player);
    if (string.IsNullOrEmpty(playerID)) return;

    _bannedPlayers.Remove(playerID);
    _eligiblePlayers.Remove(playerID);
    _votedPlayers.Remove(playerID);

    // check if player's removal should result in a heli kill
    CheckKillHelis();
  }

  private void OnGroupPermissionGranted(string group, string perm) =>
    OnGroupPermissionRevoked(group, perm);

  private void OnGroupPermissionRevoked(string group, string perm)
  {
    // abort if not a voting-related permission
    if (BannedPerm != perm && VotePerm != perm) return;

    // loop over all players, because we don't know who all got added/removed
    foreach (var player in BasePlayer.activePlayerList)
    {
      UpdatePlayer(GetID(player));
    }

    // invalidate any votes that were affected by permissions change
    RemoveDefunctVotes();

    // check for heli kill in case this pushed things over voting threshold
    CheckKillHelis();
  }

  private void OnUserPermissionGranted(string playerID, string perm) =>
    OnUserPermissionRevoked(playerID, perm);

  private void OnUserPermissionRevoked(string playerID, string perm)
  {
    // abort if not a voting-related permission
    if (BannedPerm != perm && VotePerm != perm) return;

    // abort if no valid playerID
    if (string.IsNullOrEmpty(playerID)) return;

    var oldEligibleCount = _eligiblePlayers.Count;

    // reevaluate kill conditions if non-voting player lost eligibility, as the
    //  voting pool shrinkage could push voting over a new threshold
    if (!UpdatePlayer(playerID) &&
        !_votedPlayers.Remove(playerID) &&
        _eligiblePlayers.Count != oldEligibleCount)
    {
      CheckKillHelis();
    }
  }

  #endregion

  #region Commands

  private void HeliKillCmd(IPlayer iPlayer, string command, string[] args) =>
    KillAllHelis();

  private void HeliVoteCmd(IPlayer iPlayer, string command, string[] args)
  {
    if (iPlayer == null || iPlayer.IsServer) return;

    var playerID = GetID(iPlayer);
    if (string.IsNullOrEmpty(playerID)) return;

    // banned players can't vote
    if (_bannedPlayers.Contains(playerID))
    {
      SendPlayerMsg(iPlayer, MsgKeys.PlayerBanned);
      return;
    }

    // can't vote when no heli active
    if (_heliCache.Count == 0)
    {
      SendPlayerMsg(iPlayer, MsgKeys.HeliNotActive);
      return;
    }

    // can't vote if already voted
    if (!_votedPlayers.Add(GetID(iPlayer)))
    {
      SendPlayerMsg(iPlayer, MsgKeys.PlayerAlreadyVoted,
        _votedPlayers.Count, NumVotesRequired(),
        _config.CmdPerms.VoteCommand);
      return;
    }

    // if this vote results in a heli kill, we're done
    if (CheckKillHelis()) return;

    // provide voting feedback to player
    SendPlayerMsg(iPlayer, MsgKeys.PlayerVoted,
      _votedPlayers.Count, NumVotesRequired(),
      _config.CmdPerms.VoteCommand);
  }

  #endregion

  #region Plugin Integrations

  private bool ShouldIgnore(PatrolHelicopter heli) =>
    !heli
    ||
    (_config.Settings.IgnoreHeliSignal &&
     HeliSignals?.CallHook("IsHeliSignalObject", heli.skinID) != null)
    ||
    (_config.Settings.IgnoreLootDefender &&
     LootDefender?.CallHook("IsDefended", heli) is true);

  private void OnLockedEntity(PatrolHelicopter heli, ulong _, ulong __)
  {
    // don't need to check anything else here, as this hook will only be called
    //  if we care about it
    if (heli) OnEntityKill(heli);
  }

  private void OnUnlockedEntity(PatrolHelicopter heli, ulong _, ulong __)
  {
    // don't need to check anything else here, as this hook will only be called
    //  if we care about it
    if (heli) OnEntitySpawned(heli);
  }

  #endregion

  #region Methods

  private static void DestroyTimer(Timer t)
  {
    if (TimerValid(t)) t.Destroy();
  }

  private static bool TimerValid(Timer t) => false == t?.Destroyed;

  private bool CanUse(string userIDString) =>
    permission.UserHasPermission(userIDString, VotePerm);

  private bool IsBanned(string userIDString) =>
    permission.UserHasPermission(userIDString, BannedPerm);

  // update _bannedPlayers and _eligiblePlayers for the given player's ID
  // returns whether player is eligible for voting
  // NOTE: do NOT update _votedPlayers from here, for various reasons!
  private bool UpdatePlayer(string userIDString)
  {
    if (string.IsNullOrEmpty(userIDString)) return false;

    if (IsBanned(userIDString))
    {
      _bannedPlayers.Add(userIDString);
      _eligiblePlayers.Remove(userIDString);
      return false;
    }
    _bannedPlayers.Remove(userIDString);

    if (CanUse(userIDString))
    {
      _eligiblePlayers.Add(userIDString);
      return true;
    }

    return false;
  }

  private static string GetID(BasePlayer player) => player?.UserIDString;

  private static string GetID(IPlayer iPlayer) => iPlayer?.Id;

  private int NumVotesRequired()
  {
    var votesRequired = (int)Mathf.Ceil(
      _eligiblePlayers.Count *
      (_config.Settings.PercentVotesRequired / 100.0f));
    if (votesRequired <= 0) votesRequired = 1;
    return votesRequired;
  }

  private void StartHeliAnnouncements()
  {
    DoHeliAnnouncement();
    DestroyTimer(_msgTimer);
    _msgTimer = timer.Every(_config.Settings.AnnounceTime, DoHeliAnnouncement);
  }

  private void DoHeliAnnouncement()
  {
    SendGlobalMsg(MsgKeys.HeliAnnouncement,
      _votedPlayers.Count, NumVotesRequired(),
      _config.CmdPerms.VoteCommand);
  }

  // check whether helis should be killed, and kill them if so
  // returns whether helis were killed
  private bool CheckKillHelis()
  {
    // abort if no voting-eligible players, no kill-eligible helis, or no votes
    if (_eligiblePlayers.Count == 0 ||
        _heliCache.Count == 0 ||
        _votedPlayers.Count == 0)
    {
      return false;
    }
    // abort if votes do not meet/exceed threshold
    if (_votedPlayers.Count < NumVotesRequired()) return false;

    KillAllHelis();
    return true;
  }

  private void KillAllHelis()
  {
    if (_heliCache.Count == 0) return;

    SendGlobalMsg(MsgKeys.HeliKilled, _heliCache.Count);

    // copy the cache to avoid modify-on-iterate issues
    var killList = Pool.Get<List<PatrolHelicopter>>();
    killList.Capacity = _heliCache.Count;
    foreach (var heli in _heliCache)
    {
      killList.Add(heli);
    }
    // now kill each heli in the list
    foreach (var heli in killList)
    {
      // NOTE: this will result in immediate OnEntityKill() callbacks, so we
      //  don't need to manually remove from _heliCache here
      heli.Kill();
    }
    Pool.FreeUnmanaged(ref killList);

    DestroyTimer(_msgTimer);
    _votedPlayers.Clear();
  }

  private string AdminPerm
  {
    get
    {
      _adminPerm ??= $"{Name}.{_config.CmdPerms.AdminPerm}";
      return _adminPerm;
    }
  }

  private string BannedPerm
  {
    get
    {
      _bannedPerm ??= $"{Name}.{_config.CmdPerms.BannedPerm}";
      return _bannedPerm;
    }
  }

  private string VotePerm
  {
    get
    {
      _votePerm ??= $"{Name}.{_config.CmdPerms.VotePerm}";
      return _votePerm;
    }
  }

  private bool IsOnline(string playerID) =>
    true == covalence.Players.FindPlayerById(playerID)?.IsConnected;

  // remove any defunct votes from _votedPlayers based on current _bannedPlayers
  //  and _eligiblePlayers data
  private void RemoveDefunctVotes()
  {
    var defunctVotes = Pool.Get<List<string>>();
    // scan vote list for players who are offline or are ineligible for voting
    foreach (var playerID in _votedPlayers)
    {
      if (!IsOnline(playerID))
      {
        _bannedPlayers.Remove(playerID);
        _eligiblePlayers.Remove(playerID);
        defunctVotes.Add(playerID);
        continue;
      }
      if (!_eligiblePlayers.Contains(playerID))
      {
        defunctVotes.Add(playerID);
      }
    }
    // now remove defunct players from voting list
    if (defunctVotes.Count > 0)
    {
      Puts($"Purging {defunctVotes.Count} defunct vote(s)");
      foreach (var defunctVote in defunctVotes)
      {
        _votedPlayers.Remove(defunctVote);
      }
    }
    Pool.FreeUnmanaged(ref defunctVotes);
  }

  #endregion

  #region Messaging

  private enum MsgKeys
  {
    HeliAnnouncement,
    HeliAttacked,
    HeliKilled,
    HeliNotActive,
    PlayerAlreadyVoted,
    PlayerBanned,
    PlayerVoted
  }

  protected override void LoadDefaultMessages()
  {
    lang.RegisterMessages(new Dictionary<string, string>
    {
      {
        nameof(MsgKeys.HeliAnnouncement),
        "A Patrol Helicopter is active!\nCast your vote with <color=#87a3ff>/{2}</color> to kill it!\n{0}/{1} needed players have voted!"
      },
      {
        nameof(MsgKeys.HeliAttacked),
        "Someone has attacked a Patrol Helicopter!\nVoting has been reset!"
      },
      {
        nameof(MsgKeys.HeliKilled),
        "{0} Patrol Helicopter(s) KILLED!!"
      },
      {
        nameof(MsgKeys.HeliNotActive),
        "No killable Patrol Helicopter active!"
      },
      {
        nameof(MsgKeys.PlayerAlreadyVoted),
        "You have already voted!\n{0}/{1} needed players have voted!"
      },
      {
        nameof(MsgKeys.PlayerBanned),
        "You are not allowed to vote!"
      },
      {
        nameof(MsgKeys.PlayerVoted),
        "Vote recorded!\n{0}/{1} needed players have voted!"
      }
    }, this);
  }

  private void UpdateMessageConfig()
  {
    // remove any obsolete messages from config
    var deadKeys = Pool.Get<List<string>>();
    foreach (var cfgKey in _config.MsgSettings.Messages.Keys)
    {
      if (!Enum.TryParse(cfgKey, out MsgKeys _))
      {
        deadKeys.Add(cfgKey);
      }
    }
    foreach (var deadKey in deadKeys)
    {
      _config.MsgSettings.Messages.Remove(deadKey);
    }
    Pool.FreeUnmanaged(ref deadKeys);
    // ensure all current messages are in config
    foreach (MsgKeys key in Enum.GetValues(typeof(MsgKeys)))
    {
      var msgName = GetMessageName(key);
      if (!_config.MsgSettings.Messages.ContainsKey(msgName))
      {
        _config.MsgSettings.Messages.Add(msgName, GetDefaultVars(key));
      }
    }
  }

  private void SendPlayerMsg(
    IPlayer iPlayer, MsgKeys msgKey, params object[] formatParams)
  {
    if (null == iPlayer) return;

    var msgName = GetMessageName(msgKey);
    if (!_config.MsgSettings.Messages.TryGetValue(msgName, out var settings))
    {
      return;
    }
    var message = GetMessage(msgName, null, formatParams);

    if (iPlayer.IsServer)
    {
      Puts(message);
      return;
    }

    var player = iPlayer.Object as BasePlayer;
    if (!player) return;

    if (settings.UseChat)
    {
      Player.Message(player, message, _config.MsgSettings.ChatMsgID);
    }

    if (!settings.UseToast) return;
    // player.ShowToast((GameTip.Styles)settings.Type, message);
    player.SendConsoleCommand(
      "gametip.showtoast", settings.Type, message, string.Empty, false);
  }

  private void SendGlobalMsg(MsgKeys msgKey, params object[] formatParams)
  {
    var msgName = GetMessageName(msgKey);
    if (!_config.MsgSettings.Messages.TryGetValue(msgName, out var settings))
    {
      return;
    }
    var message = GetMessage(msgName, null, formatParams);

    if (settings.UseChat)
    {
      Server.Broadcast(message, _config.MsgSettings.ChatMsgID);
    }

    if (!settings.UseToast) return;
    // TODO: this should probably be a coroutine, as it could cause hitches on
    //  high pop servers
    // use a temporary dict to cache the formatted message for each language
    //  encountered, so that we only have to retrieve and format it once
    var msgDict = Pool.Get<Dictionary<string, string>>();
    // seed dictionary with server's default language, since we already
    //  formatted a message for that
    msgDict[lang.GetLanguage(null)] = message;
    foreach (var player in BasePlayer.activePlayerList)
    {
      if (!player) continue;
      var playerID = GetID(player);
      if (string.IsNullOrEmpty(playerID) || _bannedPlayers.Contains(playerID))
      {
        continue;
      }
      var playerLang = lang.GetLanguage(playerID);
      if (!msgDict.TryGetValue(playerLang, out var playerMsg))
      {
        playerMsg = GetMessage(msgName, playerID, formatParams);
        msgDict.Add(playerLang, playerMsg);
      }
      // player.ShowToast((GameTip.Styles)settings.Type, playerMsg);
      player.SendConsoleCommand(
        "gametip.showtoast", settings.Type, message, string.Empty, false);
    }
    Pool.FreeUnmanaged(ref msgDict);
  }

  private string GetMessage(
    string msgName, string playerId = null, params object[] formatParams) =>
    string.IsNullOrEmpty(msgName) ?
      msgName :
      string.Format(lang.GetMessage(msgName, this, playerId), formatParams);

  private static string GetMessageName(MsgKeys msgKey) =>
    Enum.GetName(typeof(MsgKeys), msgKey) ?? string.Empty;

  private static MessageVars GetDefaultVars(MsgKeys msgKey) =>
    msgKey switch
    {
      MsgKeys.HeliAnnouncement =>
        new MessageVars { UseChat = true, UseToast = false, Type = 2 },
      MsgKeys.HeliAttacked =>
        new MessageVars { UseChat = true, UseToast = false, Type = 4 },
      MsgKeys.HeliKilled =>
        new MessageVars { UseChat = true, UseToast = false, Type = 4 },
      MsgKeys.HeliNotActive =>
        new MessageVars { UseChat = true, UseToast = false, Type = 5 },
      MsgKeys.PlayerAlreadyVoted =>
        new MessageVars { UseChat = true, UseToast = false, Type = 3 },
      MsgKeys.PlayerBanned =>
        new MessageVars { UseChat = true, UseToast = false, Type = 5 },
      MsgKeys.PlayerVoted =>
        new MessageVars { UseChat = true, UseToast = false, Type = 3 },
      _ => new MessageVars()
    };

  #endregion

  #region Configuration

  private Configuration _config;

  private sealed class Configuration
  {
    [JsonProperty(PropertyName = "Plugin Settings")]
    public GeneralSettings Settings = new()
    {
      AnnounceTime = 150,
      PercentVotesRequired = 80,
      IgnoreHeliSignal = false,
      IgnoreLootDefender = false
    };

    [JsonProperty(PropertyName = "Commands & Permissions")]
    public CmdPerms CmdPerms = new()
    {
      VoteCommand = "voteheli",
      KillCommand = "killhelis",
      VotePerm = "use",
      BannedPerm = "banned",
      AdminPerm = "admin"
    };

    [JsonProperty(PropertyName = "Messages")]
    public MessageSettings MsgSettings = new()
    {
      ChatMsgID = 0,
      Messages = new SortedDictionary<string, MessageVars>()
    };

    public VersionNumber Version = new(0, 0, 0);
  }

  protected override void LoadDefaultConfig()
  {
    Puts("Creating a new config file");
    _config = new Configuration
    {
      Version = Version
    };
    UpdateMessageConfig();
    // NOTE: Oxide will call this followed by SaveConfig() if there's no config
    //  file, so don't call SaveConfig() from here
  }

  protected override void LoadConfig()
  {
    var saveConfig = false;
    try
    {
      base.LoadConfig();
      _config = Config.ReadObject<Configuration>();
      if (_config == null)
      {
        Puts("Config is null; loading default");
        LoadDefaultConfig();
        saveConfig = true;
      }
      else if (_config?.Version != Version)
      {
        UpdateConfig();
        saveConfig = true;
      }
    }
    catch (Exception exception)
    {
      PrintWarning($"Exception reading config file: {exception}");
      LoadDefaultConfig();
      saveConfig = true;
    }

    if (saveConfig)
    {
      SaveConfig();
    }
  }

  protected override void SaveConfig()
  {
    Puts($"Configuration changes saved to {Name}.json");
    Config.WriteObject(_config, true);
  }

  private void UpdateConfig()
  {
    if (_config.Version >= Version) return;
    if (_config.Version < new VersionNumber(2, 0, 0))
    {
      PrintWarning($"Old {_config.Version} config file will be overwritten due to being incompatible");
      LoadDefaultConfig();
    }

    UpdateMessageConfig();
    _config.Version = Version;

    PrintWarning($"[CONFIG UPDATE] Updated to Version {Version}");
  }

  #endregion

  #region Fields

  private sealed class GeneralSettings
  {
    [JsonProperty(PropertyName = "Frequency of vote announcements [in seconds]")]
    public float AnnounceTime;
    [JsonProperty(PropertyName = "Percentage of votes required [0 - 100]")]
    public float PercentVotesRequired;
    [JsonProperty(PropertyName = "Ignore helis spawned by Heli Signals")]
    public bool IgnoreHeliSignal;
    [JsonProperty(PropertyName = "Ignore helis locked by Loot Defender")]
    public bool IgnoreLootDefender;
    [JsonProperty(PropertyName = "Reset vote when a heli takes damage")]
    public bool ResetVoteOnDamage;
  }

  private sealed class CmdPerms
  {
    [JsonProperty(PropertyName = "Heli vote command")]
    public string VoteCommand;
    [JsonProperty(PropertyName = "Heli kill command [Requires admin perm]")]
    public string KillCommand;
    [JsonProperty(PropertyName = "Voting permission")]
    public string VotePerm;
    [JsonProperty(PropertyName = "Banned permission")]
    public string BannedPerm;
    [JsonProperty(PropertyName = "Admin Permission")]
    public string AdminPerm;
  }

  private sealed class MessageSettings
  {
    [JsonProperty(PropertyName = "Steam ID to use for the image for messages [0 = default]")]
    public ulong ChatMsgID;
    [JsonProperty(PropertyName = "Messages")]
    public SortedDictionary<string, MessageVars> Messages;
  }

  private sealed class MessageVars
  {
    [JsonProperty(PropertyName = "Use chat messages")]
    public bool UseChat;
    [JsonProperty(PropertyName = "Use native toast messages")]
    public bool UseToast;
    [JsonProperty(PropertyName = "Toast type")]
    public int Type;
  }

  #endregion
}

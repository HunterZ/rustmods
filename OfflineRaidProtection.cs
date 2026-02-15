#if CARBON
using Carbon.Extensions;
using Carbon.Plugins.OfflineRaidProtectionEx;
#else
using Facepunch.Extend;
using Oxide.Plugins.OfflineRaidProtectionEx;
#endif

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace
#if CARBON
  Carbon.Plugins
#else
  Oxide.Plugins
#endif
{
  [Info("Offline Raid Protection", "realedwin/HunterZ", "1.3.0"), Description("Prevents/reduces offline raids by other players")]
  public sealed class OfflineRaidProtection :
#if CARBON
    CarbonPlugin
#else
    RustPlugin
#endif
  {
    #region Fields

    [PluginReference] private Plugin Clans;

    private static OfflineRaidProtection Instance { get; set; }
    private static ConfigData Configuration { get; set; }

    private readonly Dictionary<ulong, LastOnlineData> _lastOnline = new();
    private readonly Dictionary<ulong, PlayerScaleCache> _scaleCache = new();
    private readonly Dictionary<string, List<ulong>> _clanMemberCache = new();
    private readonly Dictionary<ulong, string> _clanTagCache = new();
    private readonly Dictionary<uint, bool> _prefabCache = new();
    private readonly Dictionary<uint, CupboardPrivilege> _tcCache = new();
    private readonly List<float> _damageScaleKeys = new();
    private readonly List<int> _absolutTimeScaleKeys = new();

#pragma warning disable IDE0044
    // default to UTC
    private System.TimeZoneInfo _timeZone = System.TimeZoneInfo.Utc;
#pragma warning restore IDE0044

    #region Temp

    // TODO: Refactor; some of these seem to be used to pass ephemeral state
    //  across methods, which is prone to bugs -HZ
    private readonly List<ulong> _tmpList = new();
    private readonly HashSet<ulong> _tmpHashSet = new();
    private readonly HashSet<ulong> _tmpHashSet2 = new();
    private readonly StringBuilder _sb = new();
    private readonly TextTable _textTable = new();

    #endregion Temp

    #region Constants

    private const string
      // NOTE: Facepunch has broken this implementation, so we will just show
      //  normal toasts for now; these expire on their own, but I've kept the
      //  timing code in effect so that players can't generate console spam -HZ
      // COMMAND_HIDEGAMETIP = "gametip.hidegametip",
      // COMMAND_SHOWGAMETIP = "gametip.showgametip",
      COMMAND_SHOWTOAST = "gametip.showtoast",
#if !CARBON
      LANG_MESSAGE_NOPERMISSION = "You don't have the permission to use this command",
#endif
      LANG_PROTECTION_MESSAGE_BUILDING = "Protection Message Building",
      LANG_PROTECTION_MESSAGE_VEHICLE = "Protection Message Vehicle",
      MESSAGE_INVALID_SYNTAX = "Invalid Syntax",
      MESSAGE_PLAYER_NOT_FOUND = "No player found",
      MESSAGE_TITLE_SIZE = "15",
      TEXT_CLAN_MEMBER = "Clan Members",
      TEXT_TEAM_MEMBER = "Team Members";

    #region Colors

    private const string
      COLOR_AQUA = "#1ABC9C",
      COLOR_BLUE = "#3498DB",
      COLOR_DARK_GREEN = "#1F8B4C",
      COLOR_GREEN = "#57F287",
      COLOR_ORANGE = "#E67E22",
      COLOR_RED = "#ED4245",
      COLOR_WHITE = "white",
      COLOR_YELLOW = "#FFFF00";

    #endregion Colors

    #endregion Constants

    #endregion Fields

    #region Classes

    private sealed class ConfigData
    {
      [JsonProperty(PropertyName = "Raid Protection Options")]
      public RaidProtectionOptions RaidProtection { get; set; }

      [JsonProperty(PropertyName = "Team Options")]
      public TeamOptions Team { get; set; }

      [JsonProperty(PropertyName = "Command Options")]
      public CommandOptions Command { get; set; }

      [JsonProperty(PropertyName = "Permission Options")]
      public PermissionOptions Permission { get; set; }

      [JsonProperty(PropertyName = "Other Options")]
      public OtherOptions Other { get; set; }

      [JsonProperty(PropertyName = "Timezone Options")]
      public TimeZoneOptions TimeZone { get; set; }

      public VersionNumber Version { get; set; }

      public sealed class RaidProtectionOptions
      {
        [JsonProperty(PropertyName = "Only mitigate damage caused by players")]
        public bool OnlyPlayerDamage { get; set; }

        [JsonProperty(PropertyName = "Protect players that are online")]
        public bool OnlineRaidProtection { get; set; }

        [JsonProperty(PropertyName = "Scale of damage depending on the current hour of the real day")]
        public Dictionary<int, float> AbsoluteTimeScale { get; set; }

        [JsonProperty(PropertyName = "Scale of damage depending on the offline time in hours")]
        public Dictionary<float, float> DamageScale { get; set; }

        [JsonProperty(PropertyName = "Cooldown in minutes")]
        public int CooldownMinutes { get; set; }

        [JsonProperty(PropertyName = "Online time to qualify for offline raid protection in minutes")]
        public int CooldownQualifyMinutes { get; set; }

        [JsonProperty(PropertyName = "Scale of damage between the cooldown and the first configured time")]
        public float InterimDamage { get; set; }

        [JsonProperty(PropertyName = "Protect all prefabs")]
        public bool ProtectAll { get; set; }

        [JsonProperty(PropertyName = "Protect AI (animals, NPCs, Bradley and attack helicopters etc.) if 'Protect all Prefabs' is enabled")]
        public bool ProtectAi { get; set; }

        [JsonProperty(PropertyName = "Protect modular and tug boats")]
        public bool ProtectBaseBoats { get; set; }

        [JsonProperty(PropertyName = "Protect vehicles")]
        public bool ProtectVehicles { get; set; }

        [JsonProperty(PropertyName = "Protect twigs")]
        public bool ProtectTwigs { get; set; }

        [JsonProperty(PropertyName = "Protect decaying buildings")]
        public bool ProtectDecayingBase { get; set; }

        [JsonProperty(PropertyName = "Ignore wood decay if only due to twig")]
        public bool DecayIgnoreTwig { get; set; }

        [JsonProperty(PropertyName = "Prefabs to protect")]
        public HashSet<string> Prefabs { get; set; }

        [JsonProperty(PropertyName = "Prefabs blacklist")]
        public HashSet<string> PrefabsBlacklist { get; set; }
      }

      public sealed class TeamOptions
      {
        [JsonProperty(PropertyName = "Enable team offline protection sharing")]
        public bool TeamShare { get; set; }

        [JsonProperty(PropertyName = "Mitigate damage by the team-mate who was offline the longest")]
        public bool TeamFirstOffline { get; set; }

        [JsonProperty(PropertyName = "Include players that are whitelisted on Codelocks")]
        public bool IncludeWhitelistPlayers { get; set; }

        [JsonProperty(PropertyName = "Prevent players from leaving or disbanding their team if at least one team member is offline")]
        public bool TeamAvoidAbuse { get; set; }

        [JsonProperty(PropertyName = "Enable offline raid protection penalty for leaving or disbanding a team")]
        public bool TeamEnablePenalty { get; set; }

        [JsonProperty(PropertyName = "Penalty duration in hours")]
        public float TeamPenaltyDuration { get; set; }
      }

      public sealed class CommandOptions
      {
        [JsonProperty(PropertyName = "Commands to check offline protection status")]
        public string[] Commands { get; set; }

        [JsonProperty(PropertyName = "Command to display offline raid protection information")]
        public string CommandHelp { get; set; }

        [JsonProperty(PropertyName = "Command to fill the offline times of all players")]
        public string CommandFillOnlineTimes { get; set; }

        [JsonProperty(PropertyName = "Command to update the permission status for all players.")]
        public string CommandUpdatePermissions { get; set; }

        [JsonProperty(PropertyName = "Command to change a player's offline time")]
        public string CommandTestOffline { get; set; }

        [JsonProperty(PropertyName = "Command to change a player's offline time to the current time")]
        public string CommandTestOnline { get; set; }

        [JsonProperty(PropertyName = "Command to change a player's penalty duration")]
        public string CommandTestPenalty { get; set; }

        [JsonProperty(PropertyName = "Command to update the Prefabs to protect list")]
        public string CommandUpdatePrefabList { get; set; }

        [JsonProperty(PropertyName = "Command to dump the Prefabs to protect list")]
        public string CommandDumpPrefabList { get; set; }
#if CARBON
        [JsonIgnore]
        private int _commandCooldown;

        [JsonProperty(PropertyName = "Command cooldown in seconds")]
        public int CommandCooldown
        {
          get => _commandCooldown;
          set => _commandCooldown = System.Math.Max(0, value);
        }
#endif
        internal void RegisterCommands(Plugin plugin, OfflineRaidProtection offlineRaidProtection)
        {
          RegisterChatCommands(Commands, plugin, offlineRaidProtection.cmdStatus, Configuration.Permission.Check);
          RegisterChatCommands(new[] {CommandHelp}, plugin, offlineRaidProtection.cmdHelp, Configuration.Permission.Protect);
          RegisterChatCommands(new[] {CommandFillOnlineTimes}, plugin, offlineRaidProtection.cmdFillOnlineTimes, Configuration.Permission.Admin);
          RegisterChatCommands(new[] {CommandTestOffline}, plugin, offlineRaidProtection.cmdTestOffline, Configuration.Permission.Admin);
          RegisterChatCommands(new[] {CommandTestOnline}, plugin, offlineRaidProtection.cmdTestOnline, Configuration.Permission.Admin);
          RegisterChatCommands(new[] {CommandTestPenalty}, plugin, offlineRaidProtection.cmdTestPenalty, Configuration.Permission.Admin);

          RegisterConsoleCommands(new[] {CommandFillOnlineTimes}, plugin, nameof(Instance.ccFillOnlineTimes), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandUpdatePermissions}, plugin, nameof(Instance.ccUpdatePermissions), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandUpdatePrefabList}, plugin, nameof(Instance.ccUpdatePrefabList), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandDumpPrefabList}, plugin, nameof(Instance.ccDumpPrefabList), Configuration.Permission.Admin);
        }

        private void RegisterChatCommands(string[] commands, Plugin plugin, System.Action<BasePlayer, string, string[]> callback, string permission)
        {
          foreach (var command in commands)
#if CARBON
            Community.Runtime.Core.cmd.AddChatCommand(command, plugin, callback, cooldown: CommandCooldown * 1000, permissions: new[] {permission});
#else
            Instance.cmd.AddChatCommand(command, plugin, callback);
#endif
        }

        private void RegisterConsoleCommands(string[] commands, Plugin plugin, string callback, string permission)
        {
          foreach (var command in commands)
#if CARBON
            Community.Runtime.Core.cmd.AddConsoleCommand(command, plugin, callback, cooldown: CommandCooldown * 1000, permissions: new[] {permission});
#else
            Instance.cmd.AddConsoleCommand(command, plugin, callback);
#endif
        }
      }

      public sealed class PermissionOptions
      {
        [JsonProperty(PropertyName = "Permission required to enable offline protection")]
        public string Protect { get; set; }

        [JsonProperty(PropertyName = "Permission required to check offline protection status")]
        public string Check { get; set; }

        [JsonProperty(PropertyName = "Permission required to use admin functions")]
        public string Admin { get; set; }


        internal void RegisterPermissions(Permission permission, Plugin plugin)
        {
          string[] permissions = {Protect, Check, Admin};

          foreach (var perm in permissions)
            permission.RegisterPermission(perm, plugin);
        }
      }

      public sealed class OtherOptions
      {
        [JsonProperty(PropertyName = "Play sound when damage is mitigated")]
        public bool PlaySound { get; set; }

        [JsonProperty(PropertyName = "Asset path of the sound to be played")]
        public string SoundPath { get; set; }

        [JsonProperty(PropertyName = "Display a game tip message when a prefab is protected")]
        public bool ShowMessage { get; set; }

        [JsonProperty(PropertyName = "Game tip message shows remaining protection time")]
        public bool ShowRemainingTime { get; set; }

        [JsonProperty(PropertyName = "Message duration in seconds")]
        public float MessageDuration { get; set; }
      }

      public sealed class TimeZoneOptions
      {
#if CARBON
        [JsonProperty(PropertyName = "Timezone for Windows")]
        public string WinTimeZone { get; set; }

        [JsonProperty(PropertyName = "Timezone for Linux")]
        public string UnixTimeZone { get; set; }
#else
        [JsonProperty(PropertyName = "Timezone")]
        public string TimeZone { get; set; }
#endif
      }
    }

    private sealed class LastOnlineData
    {
      [JsonProperty(PropertyName = "User ID")]
      public ulong UserID
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      [JsonProperty(PropertyName = "User Name")]
      public string UserName
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      [JsonProperty(PropertyName = "Last Online")]
      public long LastOnline
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      [JsonProperty(PropertyName = "End of Penalty")]
      public long PenaltyEnd
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      [JsonProperty(PropertyName = "Last Connect")]
      public long LastConnect
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      [JsonIgnore]
      public System.DateTime LastOnlineDT
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => System.DateTime.FromBinary(LastOnline);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => LastOnline = value.ToBinary();
      }

      [JsonIgnore]
      public System.DateTime PenaltyEndDT
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(PenaltyEnd);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private set => PenaltyEnd = value.Ticks;
      }

      [JsonIgnore]
      public System.DateTime LastConnectDT
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => System.DateTime.FromBinary(LastConnect);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => LastConnect = value.ToBinary();
      }

      [JsonConstructor]
      public LastOnlineData(
        in ulong userid, in string userName, in long lastOnline,
        in long lastConnect)
      {
        UserID = userid;
        UserName = userName;
        LastOnline = lastOnline;
        LastConnect = lastConnect;
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public LastOnlineData(
        in BasePlayer player, in System.DateTime currentTime,
        in bool connected = false) :
        this(player.userID.Get(), player.displayName, 0, 0)
      {
        LastOnlineDT = currentTime;
        LastConnectDT = connected ? currentTime : LastConnectDT;
      }

      // [JsonIgnore] public float Days => (float)TimeSpanSinceLastOnline.TotalDays;

      [JsonIgnore] public float Minutes =>
        (float)TimeSpanSinceLastOnline.TotalMinutes;

      [JsonIgnore] public float Hours =>
        (float)TimeSpanSinceLastOnline.TotalHours;

      [JsonIgnore] private System.TimeSpan TimeSpanSinceLastOnline =>
        System.DateTime.UtcNow - (!IsOnline ? LastOnlineDT : System.DateTime.UtcNow);

      [JsonIgnore] private BasePlayer Player => PlayerManager.GetPlayer(UserID);

      [JsonIgnore] public bool IsOnline => true == Player?.IsConnected;

      [JsonIgnore] public bool IsOffline =>
        !IsOnline && Minutes >= Configuration.RaidProtection.CooldownMinutes;

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void EnablePenalty(in float duration) =>
        PenaltyEndDT = System.DateTime.UtcNow.AddHours(duration);

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void DisablePenalty() => PenaltyEnd = 0L;
    }

    private sealed class PlayerScaleCache
    {
      public float Scale
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      public long Expires
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private set;
      }

      public bool ActiveGameTipMessage
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      public System.TimeSpan RemainingTime
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      public bool HasPermission
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set;
      }

      public System.Action HideGameTipAction
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private set;
      }

      public string ProtectionMessageBuilding
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private set;
      }

      public string ProtectionMessageVehicle
      {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private set;
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public PlayerScaleCache(
        System.DateTime expires, float scale, bool hasPermission)
      {
        ExpiresDT = expires;
        Scale = scale;
        ActiveGameTipMessage = false;
        HasPermission = hasPermission;
        HideGameTipAction = null;
      }

      public System.DateTime ExpiresDT
      {
        // get => new(Expires);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Expires = value.Ticks;
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void CacheAction(BasePlayer player) =>
        HideGameTipAction = GetAction(player, this);

      private void ClearAction() => HideGameTipAction = null;

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      private static System.Action GetAction(
        BasePlayer player, PlayerScaleCache playerScaleCache)
      {
        return () =>
        {
          playerScaleCache.ActiveGameTipMessage = false;
          // if (player)
          //   player.SendConsoleCommand(COMMAND_HIDEGAMETIP);
          // else
          if (!player)
            playerScaleCache.ClearAction();
        };
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void CacheMessages(in string userID)
      {
        ProtectionMessageBuilding =
          Instance.Msg(LANG_PROTECTION_MESSAGE_BUILDING, userID);
        ProtectionMessageVehicle =
          Instance.Msg(LANG_PROTECTION_MESSAGE_VEHICLE, userID);
      }
    }

    // TODO: Refactor - MrBlue says static data in plugins is terrifying -HZ
    private static class PlayerManager
    {
      private static readonly Dictionary<ulong, BasePlayer> PlayersByUserID =
        new();
      private static readonly Dictionary<string, BasePlayer> PlayersByName =
        new();

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public static void AddPlayer(in BasePlayer player)
      {
        PlayersByUserID[player.userID.Get()] = player;
        PlayersByName[player.displayName] = player;
      }

      // public static void RemovePlayer(in BasePlayer player)
      // {
      //   PlayersByUserID.Remove(player.userID.Get());
      //   PlayersByName.Remove(player.displayName);
      // }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public static BasePlayer GetPlayer(in ulong userID) =>
        PlayersByUserID.GetValueOrDefault(userID, null);

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public static BasePlayer GetPlayer(in string displayName) =>
        PlayersByName.TryGetValue(displayName, out var player) ||
        ulong.TryParse(displayName, out var userID) &&
          PlayersByUserID.TryGetValue(userID, out player) ?
            player : null;

      public static void Clear()
      {
        PlayersByUserID.Clear();
        PlayersByName.Clear();
      }
    }

    private sealed class CupboardPrivilege
    {
      // public BuildingPrivlidge BuildingPrivlidge { get; set; }
      // public float LastProtectedMinutes { get; set; }
      public bool IsDecaying { get; set; }

      public CupboardPrivilege(
        in BuildingPrivlidge buildingPrivlidge, in float lastProtectedMinutes,
        in bool isDecaying = false)
      {
        // BuildingPrivlidge = buildingPrivlidge;
        // LastProtectedMinutes = lastProtectedMinutes;
        IsDecaying = isDecaying;
      }
    }

    #endregion Classes

    #region Data

    private void SaveData() =>
      Interface.Oxide.DataFileSystem.WriteObject(
        $"{Name}/{nameof(LastOnlineData)}", _lastOnline);

    private void Save()
    {
      UpdateLastOnlineAll();
      SaveData();
    }

    private void LoadData()
    {
      try
      {
        var data =
          Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, LastOnlineData>>(
            $"{Name}/{nameof(LastOnlineData)}");
        _lastOnline.ClearAndMergeWith(data);
      }
      catch (System.Exception ex)
      {
        PrintError(ex.ToString());
      }
    }

    #endregion Data

    #region Config

    protected override void LoadDefaultConfig() =>
      Configuration = GetBaseConfig(Version);

    protected override void SaveConfig() =>
      Config.WriteObject(Configuration, true);

    protected override void LoadConfig()
    {
      base.LoadConfig();
      try
      {
        Configuration = Config.ReadObject<ConfigData>();

        if (Configuration.Version < Version)
          UpdateConfigValues();

        Config.WriteObject(Configuration, true);

        SetTimeZone();
      }
      catch (System.Exception ex)
      {
        PrintError($"There is an error in your configuration file. Using default settings\n{ex}");
        LoadDefaultConfig();
      }
    }

    private void UpdateConfigValues()
    {
      PrintWarning("Config update detected! Update config values...");
      var baseConfig = GetBaseConfig(Version);

      if (Configuration.Version < new VersionNumber(1, 1, 8))
        Configuration.Command.CommandUpdatePermissions =
          baseConfig.Command.CommandUpdatePermissions;

      if (Configuration.Version < new VersionNumber(1, 1, 15))
      {
        Configuration.Command.CommandUpdatePrefabList =
          baseConfig.Command.CommandUpdatePrefabList;
        Configuration.Command.CommandDumpPrefabList =
          baseConfig.Command.CommandDumpPrefabList;
        Configuration.RaidProtection.CooldownQualifyMinutes =
          baseConfig.RaidProtection.CooldownQualifyMinutes;
      }

      if (Configuration.Version < new VersionNumber(1, 1, 16))
      {
        DeleteMessages();
        LoadDefaultMessages();
        Configuration.RaidProtection.ProtectDecayingBase =
          baseConfig.RaidProtection.ProtectDecayingBase;
      }

      Configuration.Version = Version;

      SaveConfig();
      PrintWarning("Config update has been completed!");
    }
    private void SetTimeZone()
    {
      var id =
#if !CARBON
        Configuration.TimeZone.TimeZone;
#elif WIN
        Configuration.TimeZone.WinTimeZone;
#elif UNIX
        Configuration.TimeZone.UnixTimeZone;
#endif
      if (!string.IsNullOrEmpty(id)) _timeZone = GetTimeZoneByID(id);
    }

    private static System.TimeZoneInfo GetTimeZoneByID(string id) =>
      System.TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(
        tz => tz.Id == id) ?? System.TimeZoneInfo.Utc;

    private static ConfigData GetBaseConfig(VersionNumber version) => new()
    {
      RaidProtection = new()
      {
        OnlyPlayerDamage = false,
        OnlineRaidProtection = false,
        AbsoluteTimeScale = new(),
        CooldownMinutes = 10,
        CooldownQualifyMinutes = 0,
        DamageScale = new()
        {
          { 12f, 0.25f },
          { 24f, 0.5f },
          { 48f, 1f },
        },
        InterimDamage = 0f,
        ProtectAll = false,
        ProtectAi = false,
        ProtectBaseBoats = false,
        ProtectVehicles = true,
        ProtectTwigs = false,
        ProtectDecayingBase = true,
        DecayIgnoreTwig = false,
        Prefabs = GetPrefabNames(),
        PrefabsBlacklist = new()
      },
      Team = new()
      {
        TeamShare = true,
        TeamFirstOffline = false,
        IncludeWhitelistPlayers = false,
        TeamAvoidAbuse = false,
        TeamEnablePenalty = false,
        TeamPenaltyDuration = 24f
      },
      Command = new()
      {
        Commands = new[] { "ao", "orp" },
        CommandHelp = "raidprot",
        CommandFillOnlineTimes = "orp.fill.onlinetimes",
        CommandUpdatePermissions = "orp.update.permissions",
        CommandTestOffline = "orp.test.offline",
        CommandTestOnline = "orp.test.online",
        CommandTestPenalty = "orp.test.penalty",
        CommandUpdatePrefabList = "orp.update.prefabs",
        CommandDumpPrefabList = "orp.dump.prefabs",
#if CARBON
        CommandCooldown = 1
#endif
      },
      Permission = new()
      {
        Protect = "offlineraidprotection.protect",
        Check = "offlineraidprotection.check",
        Admin = "offlineraidprotection.admin"
      },
      Other = new()
      {
        PlaySound = false,
        SoundPath = "assets/prefabs/locks/keypad/effects/lock.code.denied.prefab",
        ShowMessage = true,
        ShowRemainingTime = false,
        MessageDuration = 3f
      },
      TimeZone = new()
      {
#if CARBON
        WinTimeZone = "W. Europe Standard Time",
        UnixTimeZone = "Europe/Berlin"
#else
        TimeZone = ""
#endif
      },
      Version = version
    };

    private static HashSet<string> GetPrefabNames()
    {
      var prefabNames = new HashSet<string>(
        ItemManager.GetItemDefinitions()
          .Select(itemDefinition => itemDefinition.GetComponent<ItemModDeployable>())
          .Where(itemModDeployable => itemModDeployable is not null)
          .Select(itemModDeployable => GetShortName(itemModDeployable.entityPrefab.resourcePath))
          .OrderBy(name => name));

      var manifest = GameManifest.Current;
      prefabNames.UnionWith(manifest.entities
        .Select(entity => GameManager.server.FindPrefab(entity).GetComponent<BaseVehicle>())
        .Where(vehicle => vehicle is not null)
        .Select(vehicle => vehicle.ShortPrefabName)
        .OrderBy(name => name));

      return prefabNames;
    }

    #endregion Config

    #region Hooks

    private void Loaded()
    {
      // This seems sus; why not just overwrite unconditionally? -HZ
      Instance ??= this;

      Configuration.Permission.RegisterPermissions(permission, this);
      Configuration.Command.RegisterCommands(this, this);

      LoadData();
      UnsubscribeHooks();
    }

    private void OnServerInitialized() => CacheData();

    private void OnNewSave(string _filename)
    {
      _lastOnline.Clear();
      SaveData();
    }

    // TODO: Refactor - this is problematic for a couple of reasons:
    // 1. Saves even when nothing has changed
    // 2. Saving around when the server does has a higher change of causing lag
    private void OnServerSave() =>
      ServerMgr.Instance.Invoke(Instance.Save, 10f);

    private void OnServerShutdown() => Save();

    private void Unload()
    {
      Save();

      Configuration = null;
      Instance = null;
      Clans = null;

      _prefabCache.Clear();
      _scaleCache.Clear();
      _lastOnline.Clear();
      _damageScaleKeys.Clear();
      _absolutTimeScaleKeys.Clear();
      _tcCache.Clear();

      _tmpList.Clear();
      _tmpHashSet.Clear();
      _tmpHashSet2.Clear();
      _sb.Clear();
      _textTable.Clear();

      FreeAllClanPoolLists();
      _clanMemberCache.Clear();
      _clanTagCache.Clear();

      PlayerManager.Clear();

      // foreach (var player in BasePlayer.activePlayerList)
      // {
      //   if (!player)
      //     return;
      //
      //   player.SendConsoleCommand(COMMAND_HIDEGAMETIP);
      // }
    }

    private void OnPluginLoaded(Plugin plugin)
    {
      if (plugin.Name is nameof(Clans))
        Clans = plugin;
    }

    private void OnPluginUnloaded(Plugin plugin)
    {
      if (plugin.Name is nameof(Clans))
        Clans = null;
    }

    private void OnPlayerConnected(BasePlayer player)
    {
      if (!player)
        return;

      var currentTime = System.DateTime.UtcNow;
      UpdateLastOnline(player, currentTime);
      UpdateLastConnect(player, currentTime);

      PlayerManager.AddPlayer(player);

      if (_scaleCache.TryGetValue(player.userID.Get(), out var scaleCache))
      {
        scaleCache.CacheAction(player);
      }
      else
      {
        scaleCache = new(
          currentTime, -1f,
          player.userID.Get().HasPermission(Configuration.Permission.Protect));
        _scaleCache[player.userID.Get()] = scaleCache;
      }
      scaleCache.CacheMessages(player.UserIDString);
    }

    private void OnPlayerDisconnected(BasePlayer player)
    {
      if (!player)
        return;

      var currentTime = System.DateTime.UtcNow;
      UpdateLastOnline(player, currentTime);
    }

    private void OnUserNameUpdated(string id, string _oldName, string newName)
    {
      if (_lastOnline.TryGetValue(ulong.Parse(id), out var lastOnline))
        lastOnline.UserName = newName;
    }

    private void OnCupboardProtectionCalculated(
      BuildingPrivlidge buildingPrivlidge, float cachedProtectedMinutes)
    {
      if (!buildingPrivlidge || buildingPrivlidge.buildingID is 0U)
        return;

      if (!_tcCache.TryGetValue(buildingPrivlidge.buildingID, out var tc))
      {
        _tcCache[buildingPrivlidge.buildingID] =
          new CupboardPrivilege(buildingPrivlidge, cachedProtectedMinutes)
          {
            IsDecaying =
              IsBuildingDecaying(buildingPrivlidge, cachedProtectedMinutes > 0)
          };
        return;
      }

      // **** The following line was not in the Carbon version for some reason;
      //  looks like it and LastProtectedMinutes aren't being used, so I've
      //  commented them out for now -HZ
      // tc.BuildingPrivlidge = buildingPrivlidge;
      // tc.LastProtectedMinutes = cachedProtectedMinutes;
      tc.IsDecaying =
        IsBuildingDecaying(buildingPrivlidge, cachedProtectedMinutes > 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo hitInfo)
    {
      if (hitInfo is null || !entity)
        return null;

      // Abort on non-player damage if non-player damage mitigation disabled
      if (Configuration.RaidProtection.OnlyPlayerDamage &&
          !hitInfo.InitiatorPlayer)
        return null;

      // Abort on self-damage
      if (hitInfo.Initiator == entity)
        return null;

      // Abort if decay damage or non-protected entity
      if (hitInfo.damageTypes.Has(Rust.DamageType.Decay) ||
          !IsProtected(entity))
        return null;

      return OnStructureAttack(entity, ref hitInfo);
    }

    #endregion Hooks

    #region Hook Subscribtion

    private void UnsubscribeHooks()
    {
      if (Configuration.RaidProtection.ProtectDecayingBase)
        Unsubscribe(nameof(OnCupboardProtectionCalculated));

      if (Configuration.Team.TeamAvoidAbuse || Configuration.Team.TeamEnablePenalty)
        return;

      Unsubscribe(nameof(OnTeamDisband));
      Unsubscribe(nameof(OnTeamKick));
      Unsubscribe(nameof(OnTeamLeave));
    }

    #endregion Hook Subscribtion

    #region Cache Methods

    private void CacheData()
    {
      CachePrefabs();
      CacheAllClans();
      CacheDamageScaleKeys();
      CacheAllPlayerScale();
      CacheAllPlayers();

      if (!Configuration.RaidProtection.ProtectDecayingBase)
        CacheAllCupboards();
    }

    private static readonly System.Type[] ComponentTypes =
    {
      typeof(BaseNpc),
      typeof(NPCPlayer),
      typeof(BradleyAPC),
      typeof(AttackHelicopter),
      typeof(CH47Helicopter),
      typeof(BaseVehicle),
      typeof(BasePlayer)
    };

    private void CachePrefabs()
    {
      foreach (var itemDefinition in ItemManager.GetItemDefinitions())
      {
        var itemModDeployable =
          itemDefinition.GetComponent<ItemModDeployable>();
        if (!itemModDeployable)
          continue;

        var resourcePath = itemModDeployable.entityPrefab.resourcePath;
        if (string.IsNullOrEmpty(resourcePath))
          continue;

        var prefabID = itemModDeployable.entityPrefab.GetEntity().prefabID;
        var shortName = GetShortName(resourcePath);

        _prefabCache[prefabID] = IsEntityProtected(shortName);
      }

      var manifest = GameManifest.Current;
      foreach (var entity in manifest.entities)
      {
        var prefab = GameManager.server.FindPrefab(entity);
        if (!prefab)
          continue;

        UnityEngine.Component activeComponent = null;
        foreach (var type in ComponentTypes)
        {
          activeComponent = prefab.GetComponent(type);
          if (activeComponent)
            break;
        }
        if (!activeComponent)
          continue;

        var baseEntity = activeComponent as BaseEntity;
        if (!baseEntity)
          continue;

        var prefabId = baseEntity.prefabID;
        var shortName = baseEntity.ShortPrefabName;
        var isAi = activeComponent is
          BaseNpc or NPCPlayer or BradleyAPC or AttackHelicopter or
          CH47Helicopter or BasePlayer;
        var isVehicle = activeComponent is BaseVehicle && !isAi;

        _prefabCache[prefabId] = IsEntityProtected(shortName, isVehicle, isAi);
      }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetShortName(in string resourcePath) =>
      System.IO.Path.GetFileNameWithoutExtension(resourcePath) ?? string.Empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsEntityProtected(
      in string shortName, in bool isVehicle = false, in bool isAi = false)
    {
      var raidProtection = Configuration.RaidProtection;

      if (raidProtection.PrefabsBlacklist.Contains(shortName))
        return false;

      if (raidProtection.ProtectVehicles && isVehicle)
        return true;

      switch (raidProtection.ProtectAll)
      {
        case true when !isAi || raidProtection.ProtectAi:
        case false when raidProtection.Prefabs.Contains(shortName):
          return true;

        default:
          return false;
      }
    }

    private void CacheAllClans()
    {
      // Call the "GetAllClans" method and retrieve all clan tags
      var clans = Clans?.Call<JArray>("GetAllClans");
      if (clans is null || clans.Count is 0)
        return;

      foreach (var tagToken in clans)
      {
        var clanTag = tagToken.ToString();
        if (!string.IsNullOrEmpty(clanTag))
          CacheClan(clanTag);
      }
    }

    private List<ulong> CacheClan(in string tag)
    {
      if (string.IsNullOrEmpty(tag))
        return null;

      // Call the "GetClan" method and retrieve the clan data
      var clan = Clans?.Call<JObject>("GetClan", tag);
      if (clan?["members"] is null)
        return null;

      if (_clanMemberCache.TryGetValue(tag, out var clanMemberList))
      {
        clanMemberList.Clear();
      }
      else
      {
        clanMemberList = Facepunch.Pool.Get<List<ulong>>();
        _clanMemberCache[tag] = clanMemberList;
      }

      foreach (var memberToken in clan["members"])
      {
        if (memberToken.Type is not JTokenType.String)
          continue;

        if (!ulong.TryParse(memberToken.ToString(), out var memberID) ||
            memberID is 0)
          continue;

        clanMemberList.Add(memberID);
        _clanTagCache[memberID] = tag;
      }

      return clanMemberList;
    }

    private void CacheDamageScaleKeys()
    {
      _damageScaleKeys.Clear();
      _damageScaleKeys.AddRange(Configuration.RaidProtection.DamageScale.Keys);
      _damageScaleKeys.Sort();

      _absolutTimeScaleKeys.Clear();
      _absolutTimeScaleKeys.AddRange(
        Configuration.RaidProtection.AbsoluteTimeScale.Keys);
      _absolutTimeScaleKeys.Sort();
    }

    private void CacheAllPlayerScale()
    {
      foreach (var lastOnline in _lastOnline.Values)
        CacheDamageScale(lastOnline.UserID, -1f);
    }

    private void CacheDamageScale(in ulong targetID, in float scale)
    {
      var currentTime = System.DateTime.UtcNow;

      if (_scaleCache.TryGetValue(targetID, out var scaleCache))
      {
        scaleCache.ExpiresDT = currentTime;
        scaleCache.Scale = scale;
      }
      else
      {
        scaleCache = new(
          currentTime, scale,
          targetID.HasPermission(Configuration.Permission.Protect));
        _scaleCache[targetID] = scaleCache;
      }

      scaleCache.CacheMessages(targetID.ToString());
    }

    private static void CacheAllPlayers()
    {
      foreach (var player in BasePlayer.allPlayerList)
        PlayerManager.AddPlayer(player);
    }

    private void CacheAllCupboards()
    {
      // scan all buildings
      foreach (var (buildingID, building)
               in BuildingManager.server.buildingDictionary)
      {
        // scan all TCs whose build privileges overlap building
        foreach (var toolCupboard in building.buildingPrivileges)
        {
          // skip any TC that is not the physically connected to building
          if (toolCupboard?.buildingID != buildingID)
            continue;

          var protectedMinutes = toolCupboard.GetProtectedMinutes();
          _tcCache[toolCupboard.buildingID] =
            new(toolCupboard, protectedMinutes)
            {
              IsDecaying =
                IsBuildingDecaying(toolCupboard, protectedMinutes > 0)
            };
          // only one TC can be physically connected
          break;
        }
      }
    }

    #endregion Cache Methods

    #region Core Methods

    private void UpdateLastOnlineAll()
    {
      var currentTime = System.DateTime.UtcNow;

      foreach (var player in BasePlayer.activePlayerList)
      {
        if (player.IsConnected)
          UpdateLastOnline(player, currentTime);
      }
    }

    private void UpdateLastOnline(
      in BasePlayer player, in System.DateTime currentTime)
    {
      if (_lastOnline.TryGetValue(player.userID.Get(), out var lastOnline))
      {
        lastOnline.LastOnlineDT = currentTime;
        lastOnline.UserName = player.displayName ?? lastOnline.UserName;
      }
      else
        _lastOnline[player.userID.Get()] = new(player, currentTime);
    }

    private void UpdateLastConnect(
      in BasePlayer player, in System.DateTime currentTime)
    {
      if (_lastOnline.TryGetValue(player.userID.Get(), out var lastOnline))
        lastOnline.LastConnectDT = currentTime;
      else
        _lastOnline[player.userID.Get()] = new(player, currentTime, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsProtected(in BaseCombatEntity entity)
    {
      // Boat building block is protected if associated with a boat, and boat
      //  protection is enabled
      // NOTE: Boat building blocks in edit mode will not be associated with a
      //  boat, and will thus not be protected
      if (entity is BoatBuildingBlock && PlayerBoat.GetParentPlayerBoat(entity))
      {
        return Configuration.RaidProtection.ProtectBaseBoats;
      }

      // BuildingBlock is protected, except twig when twig protection disabled
      if (entity is BuildingBlock buildingBlock and not BoatBuildingBlock)
      {
        return buildingBlock.grade != BuildingGrade.Enum.Twigs ||
               Configuration.RaidProtection.ProtectTwigs;
      }

      // If ProtectAll is enabled, only check the blacklist
      if (Configuration.RaidProtection.ProtectAll)
      {
        return
          !_prefabCache.TryGetValue(entity.prefabID, out var isNotProtected) ||
          isNotProtected;
      }

      // If the entity's ID is in the cache, return its cached protection status
      if (_prefabCache.TryGetValue(entity.prefabID, out var isProtected))
        return isProtected;

      // Check the whitelist
      var result =
        Configuration.RaidProtection.Prefabs.Contains(entity.ShortPrefabName);

      // Cache and return result
      _prefabCache[entity.prefabID] = result;
      return result;
    }

    // TODO: Refactor; some of these seem to be used to pass ephemeral state
    //  across methods, which is prone to bugs -HZ
    private bool _isVehicle;
    private LastOnlineData _targetLastOnline;
    private PlayerScaleCache _targetScaleCache;
    private System.DateTime _currentDateTime;
    private BuildingPrivlidge _privilege;

    // return reference to appropriate vehicle type associated with entity
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Tugboat, PlayerBoat, BaseVehicle) GetVehicle(
      in BaseCombatEntity entity) => entity switch
    {
      Tugboat tugboat =>
        (tugboat, null, null),
      PlayerBoat playerBoat =>
        (null, playerBoat, null),
      BaseVehicle vehicle =>
        (null, null, vehicle),
      _ => entity.GetParentEntity() switch
      {
        Tugboat tugboatParent =>
          (tugboatParent, null, null),
        PlayerBoat playerBoatParent =>
          (null, playerBoatParent, null),
        BaseVehicle vehicleParent =>
          (null, null, vehicleParent),
        _ =>
          (null, PlayerBoat.GetParentPlayerBoat(entity), null)
      }
    };

    private object OnStructureAttack(
      in BaseCombatEntity entity, ref HitInfo hitInfo)
    {
      // Get authorized players for the entity
      var (tugboat, modularBoat, vehicle) = GetVehicle(entity);
      _isVehicle = vehicle;
      var authorizedPlayers =
        GetAuthorizedPlayers(entity, tugboat, modularBoat, vehicle);

      // Abort if no authorized players
      if (authorizedPlayers is null)
        return null;

      // Abort if the TC has either no players authed, or an NPC authed
      // Note: Mixed auth is possible, but we still want to ignore it, because
      //  it probably indicates a Raidable Bases base or something
      using var e = authorizedPlayers.GetEnumerator();
      if (!e.MoveNext() || !e.Current.IsSteamID())
        return null;

      // Abort if damage is from is an authorized player
      if (hitInfo.InitiatorPlayer &&
          authorizedPlayers.Contains(hitInfo.InitiatorPlayer.userID.Get()))
        return null;

      // Abort if the building is decaying
      if (!Configuration.RaidProtection.ProtectDecayingBase &&
          !_isVehicle && !tugboat && !modularBoat &&
          _tcCache.TryGetValue(_privilege.buildingID, out var tc) &&
          tc.IsDecaying)
        return null;

      // Determine targetID (either the entity's owner or an authorized player)
      var targetID = entity.OwnerID;
      if (targetID is 0UL || !authorizedPlayers.Contains(targetID))
        targetID = e.Current;

      // Get the most recent team member based on the configuration setting
      targetID = GetRecentActiveMemberAll(targetID, authorizedPlayers);
      if (_targetLastOnline?.UserID != targetID &&
          !_lastOnline.TryGetValue(targetID, out _targetLastOnline))
        return null;

      // TOOD: refactor GetCachedDamageScale() to take current time as a
      //  parameter, and evaporate _currentDateTime as a cross-method variable
      _currentDateTime = System.DateTime.UtcNow;
      var isOnlineRaidProtectionEnabled =
        Configuration.RaidProtection.OnlineRaidProtection;

      if ((!isOnlineRaidProtectionEnabled && _targetLastOnline.IsOnline) ||
          _currentDateTime.Ticks <= _targetLastOnline.PenaltyEnd)
        return null;

      if (!isOnlineRaidProtectionEnabled && AnyPlayersOnline(authorizedPlayers))
        return null;

      return
        MitigateDamage(ref hitInfo, GetCachedDamageScale(targetID), targetID);
    }

    private HashSet<ulong> GetAuthorizedPlayers(
      in BaseCombatEntity entity, in Tugboat tugboat, in PlayerBoat modularBoat,
      in BaseVehicle vehicle)
    {
      _privilege = null;
      _tmpHashSet.Clear();

      // 1. Base boat checks
      if (tugboat || modularBoat)
      {
        // Abort if base boat protection disabled
        if (!Configuration.RaidProtection.ProtectBaseBoats)
          return null;

        // Wheel authed players check
        var vehiclePrivilege =
          tugboat ? tugboat.GetChildPrivilege() :
          modularBoat ? modularBoat.GetChildPrivilege() :
          null;
        if (!vehiclePrivilege)
          return null;
        AddAuthorizedPlayers(vehiclePrivilege.authorizedPlayers, _tmpHashSet);

        // Abort if code lock checks disabled
        if (!Configuration.Team.IncludeWhitelistPlayers)
          return ReturnPopulatedOrNull(_tmpHashSet);

        // Deployable code locks check
        var boatChildren =
          tugboat ? tugboat.children :
          modularBoat ? modularBoat.Deployables.Cached :
          null;
        if (boatChildren is null)
          return ReturnPopulatedOrNull(_tmpHashSet);
        AddCodeLockWhitelistPlayers(boatChildren, _tmpHashSet);

        // Don't fall through to TC check, because boats are their own base
        return ReturnPopulatedOrNull(_tmpHashSet);
      }

      // 2. Vehicle checks
      if (vehicle)
      {
        // Abort if vehicle protection disabled
        if (!Configuration.RaidProtection.ProtectVehicles)
          return null;

        // Modular car code lock check
        if (vehicle is ModularCar modularCar)
        {
          foreach (var whitelistPlayer in modularCar.CarLock.WhitelistPlayers)
            _tmpHashSet.Add(whitelistPlayer);
          // Don't fall through to TC check if we found player(s)
          if (_tmpHashSet.Count is not 0)
            return _tmpHashSet;
          // Else fall through to TC check
        }

        // Fall through to TC privilege check for unlocked/unlockable vehicles
      }

      // 3. Building privilege (Tool Cupboard) checks
      _privilege = entity.GetBuildingPrivilege();
      if (!_privilege)
        return ReturnPopulatedOrNull(_tmpHashSet);

      // TC-authed players check
      AddAuthorizedPlayers(_privilege.authorizedPlayers, _tmpHashSet);

      // Abort if code lock checks disabled
      if (!Configuration.Team.IncludeWhitelistPlayers)
        return ReturnPopulatedOrNull(_tmpHashSet);

      // Check for code locks in the building's decay entities
      // could do just doors, but I guess it's good to check boxes too? -HZ
      var decayEntities = _privilege.GetBuilding()?.decayEntities;
      if (decayEntities is not null)
        AddCodeLockWhitelistPlayers(decayEntities, _tmpHashSet);

      return ReturnPopulatedOrNull(_tmpHashSet);


      static void AddAuthorizedPlayers(
        in HashSet<ulong> authorizedPlayers, in HashSet<ulong> targetSet)
      {
        if (authorizedPlayers is null)
          return;

        foreach (var userid in authorizedPlayers)
          targetSet.Add(userid);
      }

      static void AddCodeLockWhitelistPlayers(
        in IEnumerable<BaseEntity> entities, in HashSet<ulong> targetSet)
      {
        if (entities is null)
          return;

        foreach (var entity in entities)
        {
          if (entity is not Door door || door.children is null)
            continue;

          foreach (var doorChild in door.children)
          {
            if (doorChild is not CodeLock codeLock ||
                codeLock.whitelistPlayers is null)
              continue;

            foreach (var playerId in codeLock.whitelistPlayers)
              targetSet.Add(playerId);
          }
        }
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      static HashSet<ulong> ReturnPopulatedOrNull(HashSet<ulong> hashSet) =>
        hashSet.Count is not 0 ? hashSet : null;
    }

    private ulong GetRecentActiveMemberAll(
      in ulong targetID, in HashSet<ulong> players = null)
    {
      if (!Configuration.Team.TeamShare)
        return targetID;

      if (players is null || players.Count is 0)
        return GetRecentActiveMember(targetID);

      _tmpHashSet2.Clear();

      if (Clans is not null)
      {
        foreach (var playerID in players)
        {
          if (_tmpHashSet2.Contains(playerID))
            continue;

          var tag = GetClanTag(playerID);
          if (string.IsNullOrEmpty(tag))
          {
            _tmpHashSet2.Add(playerID);
            continue;
          }

          var clanMembers = GetClanMembers(tag);
          if (clanMembers is null || clanMembers.Count is 0)
          {
            _tmpHashSet2.Add(playerID);
            continue;
          }

          _tmpHashSet2.UnionWith(clanMembers);
        }

        return GetOfflineMember(_tmpHashSet2);
      }

      foreach (var playerID in players)
      {
        if (_tmpHashSet2.Contains(playerID))
          continue;

        var teamMembers = GetTeamMembers(playerID);
        if (teamMembers is null || teamMembers.Count is 0)
        {
          _tmpHashSet2.Add(playerID);
          continue;
        }

        _tmpHashSet2.UnionWith(teamMembers);
      }

      return GetOfflineMember(_tmpHashSet2);

      ulong GetRecentActiveMember(in ulong targetID)
      {
        if (Clans is not null)
        {
          var tag = GetClanTag(targetID);
          if (string.IsNullOrEmpty(tag))
            return targetID;

          var clanMembers = GetClanMembers(tag);
          if (clanMembers is null || clanMembers.Count is 0)
            return targetID;

          return GetOfflineMember(clanMembers);
        }

        var teamMembers = GetTeamMembers(targetID);
        if (teamMembers is null || teamMembers.Count is 0)
          return targetID;

        return GetOfflineMember(teamMembers);
      }
    }

    private bool AnyPlayersOffline(in List<ulong> playerIDs)
    {
      foreach (var player in playerIDs)
      {
        if (IsOffline(player))
          return true;
      }

      return false;
    }

    private bool AnyPlayersOnline(in HashSet<ulong> playerIDs)
    {
      foreach (var player in playerIDs)
      {
        if (IsOnline(player))
          return true;
      }

      return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsOffline(in ulong playerID) =>
      _lastOnline.TryGetValue(playerID, out var lastOnlinePlayer) ?
        lastOnlinePlayer.IsOffline :
        PlayerManager.GetPlayer(playerID)?.IsConnected is not true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsOnline(in ulong playerID) =>
      _lastOnline.TryGetValue(playerID, out var lastOnlinePlayer) ?
        lastOnlinePlayer.IsOnline :
        PlayerManager.GetPlayer(playerID)?.IsConnected is true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetCachedDamageScale(in ulong targetID)
    {
      return !_scaleCache.TryGetValue(targetID, out _targetScaleCache) ||
             _currentDateTime.Ticks > _targetScaleCache.Expires ?
        CacheDamageScaleTarget(
          _targetScaleCache, _currentDateTime,
          _targetScaleCache?.HasPermission is true ?
            GetDamageScale(targetID) : -1f) :
        _targetScaleCache.Scale;

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      static float CacheDamageScaleTarget(
        in PlayerScaleCache scaleCache, in System.DateTime currentTime,
        in float scale)
      {
        scaleCache.ExpiresDT = currentTime.AddTicks(System.TimeSpan.TicksPerMinute);
        scaleCache.Scale = scale;
        return scale;
      }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetDamageScale(
      in ulong targetID, in PlayerScaleCache scaleCache = null)
    {
      _currentDateTime = System.DateTime.UtcNow;

      if (!_lastOnline.TryGetValue(targetID, out _targetLastOnline))
        return -1f;

      if (!Configuration.RaidProtection.OnlineRaidProtection &&
          _targetLastOnline.IsOnline)
        return -1f;

      if (_targetLastOnline?.UserID != targetID &&
          (!_lastOnline.TryGetValue(targetID, out _targetLastOnline) ||
           (!Configuration.RaidProtection.OnlineRaidProtection &&
            !_targetLastOnline.IsOffline)))
        return -1f;

      UpdateRemainingTime(scaleCache ?? _targetScaleCache);

      if (Configuration.RaidProtection.AbsoluteTimeScale.Count > 0 &&
          _absolutTimeScaleKeys.Count > 0)
      {
        var absoluteTimeScale = GetAbsoluteTimeScale();
        if (absoluteTimeScale is not -1f)
          return absoluteTimeScale;
      }

      if (Configuration.RaidProtection.DamageScale.Count > 0 &&
          _damageScaleKeys.Count > 0)
        return GetOfflineTimeScale();

      return -1f;

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      void UpdateRemainingTime(in PlayerScaleCache scaleCache = null)
      {
        if (!Configuration.Other.ShowRemainingTime || scaleCache is null)
          return;

        if (_damageScaleKeys.Count > 0)
        {
          var remainingHours = _damageScaleKeys[^1] - _targetLastOnline.Hours;
          scaleCache.RemainingTime =
            System.TimeSpan.FromHours(remainingHours > 0 ? remainingHours : 0d);
        }
        else
          scaleCache.RemainingTime = System.TimeSpan.Zero;
      }

      float GetOfflineTimeScale()
      {
        if (!_targetLastOnline.IsOffline)
          return -1f;

        var duration = System.DateTime.FromBinary(
          _targetLastOnline.LastOnline - _targetLastOnline.LastConnect);
        var minutes = duration.Minute;
        var hours = _targetLastOnline.Hours;

        if (minutes < Configuration.RaidProtection.CooldownQualifyMinutes &&
            _targetLastOnline.LastConnect <= 0L)
          return -1f;

        if (hours < _damageScaleKeys[0])
          return Configuration.RaidProtection.InterimDamage;

        var lastValidScale =
          Configuration.RaidProtection.DamageScale[_damageScaleKeys[0]];

        foreach (var key in _damageScaleKeys)
        {
          if (hours >= key)
            lastValidScale = Configuration.RaidProtection.DamageScale[key];
        }

        return lastValidScale;
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      float GetAbsoluteTimeScale() =>
        Configuration.RaidProtection.AbsoluteTimeScale.GetValueOrDefault(
          System.TimeZoneInfo.ConvertTimeFromUtc(
            _currentDateTime, _timeZone).Hour,
          -1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object MitigateDamage(
      ref HitInfo hitInfo, in float scale, in ulong targetID)
    {
      if (scale is <= -1f or 1f)
        return null;

      var isFire = hitInfo.damageTypes.GetMajorityDamageType()
        is Rust.DamageType.Heat or Rust.DamageType.Fun_Water;
      var showMessage =
        Configuration.Other.ShowMessage &&
        (!isFire || hitInfo.WeaponPrefab is not null);
      var playSound = Configuration.Other.PlaySound && !isFire;
      bool initiatorValid = hitInfo.InitiatorPlayer;

      if (scale is 0f)
      {
        if (showMessage && initiatorValid)
          SendMessage(hitInfo, targetID);

        PlaySound(ref hitInfo);

        return true;
      }

      hitInfo.damageTypes.ScaleAll(scale);

      if (scale >= 1)
        return null;

      if (showMessage && initiatorValid)
        SendMessage(hitInfo, targetID, scale.ToPercent());

      PlaySound(ref hitInfo);

      return null;

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      void PlaySound(ref HitInfo hitInfo)
      {
        if (!playSound || !initiatorValid)
          return;

        Effect.server.Run(
          Configuration.Other.SoundPath,
          hitInfo.InitiatorPlayer.transform.position,
          UnityEngine.Vector3.zero);
      }
    }

    #endregion Core Methods

    #region Game Tip Message

    private void SendMessage(
      in HitInfo hitInfo, in ulong targetID, in float amount = 100f)
    {
      var initiator = hitInfo.InitiatorPlayer;
      if (!_scaleCache.TryGetValue(
            initiator.userID.Get(), out var playerScaleCache))
      {
        playerScaleCache = new(
          _currentDateTime, -1f,
          targetID.HasPermission(Configuration.Permission.Protect));
        _scaleCache[initiator.userID.Get()] = playerScaleCache;
      }

      if (playerScaleCache.ActiveGameTipMessage)
        return;

      if (playerScaleCache.HideGameTipAction is null)
        playerScaleCache.CacheAction(initiator);

      ShowMessageTip(initiator, amount);
      playerScaleCache.ActiveGameTipMessage = true;

      return;


      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      void ShowMessageTip(in BasePlayer player, in float amount = 100f)
      {
        _sb.Clear();
        _sb.Append(_isVehicle ? playerScaleCache.ProtectionMessageVehicle : playerScaleCache.ProtectionMessageBuilding)
          .Append("<color=").Append(GetColor(amount)).Append(">").Append(amount).Append("%</color>");

        if (Configuration.Other.ShowRemainingTime)
        {
          var remainingTime = _targetScaleCache.RemainingTime;
          _sb.Append(" (")
            .Append(remainingTime.Days).Append("d:")
            .Append(remainingTime.Hours).Append("h:")
            .Append(remainingTime.Minutes).Append("m)");
        }

        ServerMgr.Instance.Invoke(ShowGameTipAction(player, _sb.ToString()), 0);
        ServerMgr.Instance.Invoke(playerScaleCache.HideGameTipAction, Configuration.Other.MessageDuration);

        return;


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static System.Action ShowGameTipAction(BasePlayer player, string msg) =>
          () =>
          {
            // if (player)
            //     player.SendConsoleCommand(COMMAND_SHOWGAMETIP, msg);
            player?.SendConsoleCommand(
              COMMAND_SHOWTOAST, GameTip.Styles.Blue_Short, msg, string.Empty,
              false);
          };
      }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetColor(in float amount) =>
      amount switch
      {
        100f             => COLOR_RED,
        > 50f and < 100f => COLOR_ORANGE,
        > 25f and <= 50f => COLOR_YELLOW,
        > 0f and <= 25f  => COLOR_AQUA,
        0f               => COLOR_GREEN,
        _                => COLOR_WHITE
      };

    #endregion Game Tip Message

    #region Clans/Teams Integration

    #region Clans/Teams Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetClanTag(in ulong userID)
    {
      if (_clanTagCache.TryGetValue(userID, out var tag))
        return tag;

      var team = GetTeam(userID);
      if (!(team?.members.Count > 0))
        return null;

      tag = Clans?.Call<string>("GetClanOf", userID);
      _clanTagCache[userID] = tag;
      return tag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private List<ulong> GetClanMembers(in string tag) =>
      string.IsNullOrEmpty(tag) ? null :
      _clanMemberCache.TryGetValue(tag, out var members) ? members :
      CacheClan(tag);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static RelationshipManager.PlayerTeam GetTeam(in ulong userID) =>
      RelationshipManager.ServerInstance.FindPlayersTeam(userID);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private List<ulong> GetTeamMembers(in ulong userID)
    {
      _tmpList.Clear();
      var team = GetTeam(userID);
      if (team?.members.Count > 0)
        _tmpList.AddRange(team.members);

      return _tmpList.Count > 0 ? _tmpList : null;
    }

    // TODO: This seems to currently be the busiest method in this plugin
    //  according to Carbon's profiler - possibly due to being called a lot as
    //  much as due to anything it's actually doing -HZ
    private ulong GetOfflineMember(in IReadOnlyCollection<ulong> members)
    {
      if (members is null || members.Count is 0)
        return 0UL;

      var result = 0UL;
      var comparisonValue =
        Configuration.Team.TeamFirstOffline ? float.MinValue : float.MaxValue;

      foreach (var memberID in members)
      {
        if (!_lastOnline.TryGetValue(memberID, out var lastOnlineMember))
          continue;

        var memberMinutes = lastOnlineMember.Minutes;

        // If ClanFirstOffline is true, find the member who has been offline the longest
        // Else, find the member who has been offline the shortest
        if ((!Configuration.Team.TeamFirstOffline ||
             memberMinutes <= comparisonValue) &&
            (Configuration.Team.TeamFirstOffline ||
             memberMinutes >= comparisonValue))
          continue;

        comparisonValue = memberMinutes;
        result = memberID;
      }

      return result;
    }

    private void FreeAllClanPoolLists()
    {
      foreach (var list in _clanMemberCache.Values)
      {
        var tmpList = list;
        Facepunch.Pool.FreeUnmanaged(ref tmpList);
      }
    }

    #endregion Clans/Teams Methods

    #region Clans Hooks


    private void OnClanCreate(string tag) => CacheClan(tag);

    private void OnClanUpdate(string tag) => CacheClan(tag);

    private void OnClanMemberJoined(string userID, string tag)
    {
      if (_clanMemberCache.TryGetValue(tag, out var clan))
        clan.Add(ulong.Parse(userID));
      else
        CacheClan(tag);
    }

    private void OnClanMemberGone(string userID, string tag)
    {
      if (_clanMemberCache.TryGetValue(tag, out var clan))
        clan.Remove(ulong.Parse(userID));
      else
        CacheClan(tag);
    }

    private void OnClanDisbanded(string tag, List<string> _memberUserIDs) =>
      OnClanDestroy(tag);

    private void OnClanDestroy(string tag)
    {
      if (_clanMemberCache.Remove(tag, out var list))
      {
        Facepunch.Pool.FreeUnmanaged(ref list);
      }
    }

    #endregion Clans Hooks

    #region Team Hooks

    private object OnTeamDisband(RelationshipManager.PlayerTeam team)
    {
      if (team is null || team.members.Count is 0)
        return null;

      if (Configuration.Team.TeamAvoidAbuse && AnyPlayersOffline(team.members))
        return true;

      if (!Configuration.Team.TeamEnablePenalty)
        return null;

      foreach (var memberID in team.members)
      {
        if (_lastOnline.TryGetValue(memberID, out var member))
          member.EnablePenalty(Configuration.Team.TeamPenaltyDuration);
      }

      return null;
    }

    private object OnTeamKick(
      RelationshipManager.PlayerTeam team, BasePlayer player, ulong _target) =>
      OnTeamLeave(team, player);

    private object OnTeamLeave(
      RelationshipManager.PlayerTeam team, BasePlayer _player)
    {
      if (team is null || team.members.Count is 0)
        return null;

      if (Configuration.Team.TeamAvoidAbuse && AnyPlayersOffline(team.members))
        return true;

      if (!Configuration.Team.TeamEnablePenalty)
        return null;

      foreach (var memberID in team.members)
      {
        if (_lastOnline.TryGetValue(memberID, out var member))
          member.EnablePenalty(Configuration.Team.TeamPenaltyDuration);
      }

      return null;
    }

    #endregion Team Hooks

    #endregion Clans/Teams Integration

    #region Commands

    #region ChatCommands

    private static readonly UnityEngine.RaycastHit[] RaycastHits =
      new UnityEngine.RaycastHit[1];

    private static UnityEngine.Ray _ray;

    private const int LayerMask =
      Rust.Layers.Mask.Construction |  // building blocks
      Rust.Layers.Mask.Deployed |      // deployable items
      Rust.Layers.Mask.Vehicle_World | // modular cars
      Rust.Layers.Mask.Vehicle_Large;  // buildable boats

#if !CARBON
    private static bool CheckChatCmdPerm(BasePlayer player, string perm)
    {
      if (player.HasPermission(perm))
        return true;

      player.ChatMessage(LANG_MESSAGE_NOPERMISSION);
        return false;
    }
#endif

    private void cmdStatus(BasePlayer player, string _command, string[] args)
    {
      if (!player)
        return;
#if !CARBON
      if (!CheckChatCmdPerm(player, Configuration.Permission.Check))
        return;
#endif
      if (args?.Length is not 0)
      {
        player.ChatMessage(GetStatusText(args));
        return;
      }
/*
      var maskDict = new Dictionary<string, int>
      {
        {"Default", Rust.Layers.Mask.Default},
        {"TransparentFX", Rust.Layers.Mask.TransparentFX},
        {"Ignore_Raycast", Rust.Layers.Mask.Ignore_Raycast},
        {"Reserved1", Rust.Layers.Mask.Reserved1},
        {"Water", Rust.Layers.Mask.Water},
        {"UI", Rust.Layers.Mask.UI},
        {"Reserved2", Rust.Layers.Mask.Reserved2},
        {"Reserved3", Rust.Layers.Mask.Reserved3},
        {"Deployed", Rust.Layers.Mask.Deployed},
        {"Ragdoll", Rust.Layers.Mask.Ragdoll},
        {"Invisible", Rust.Layers.Mask.Invisible},
        {"AI", Rust.Layers.Mask.AI},
        {"Player_Movement", Rust.Layers.Mask.Player_Movement},
        {"Vehicle_Detailed", Rust.Layers.Mask.Vehicle_Detailed},
        {"Game_Trace", Rust.Layers.Mask.Game_Trace},
        {"Vehicle_World", Rust.Layers.Mask.Vehicle_World},
        {"World", Rust.Layers.Mask.World},
        {"Player_Server", Rust.Layers.Mask.Player_Server},
        {"Trigger", Rust.Layers.Mask.Trigger},
        {"Harvestable", Rust.Layers.Mask.Harvestable},
        {"Physics_Projectile", Rust.Layers.Mask.Physics_Projectile},
        {"Construction", Rust.Layers.Mask.Construction},
        {"Construction_Socket", Rust.Layers.Mask.Construction_Socket},
        {"Terrain", Rust.Layers.Mask.Terrain},
        {"Transparent", Rust.Layers.Mask.Transparent},
        {"Clutter", Rust.Layers.Mask.Clutter},
        {"Bush", Rust.Layers.Mask.Bush},
        {"Vehicle_Large", Rust.Layers.Mask.Vehicle_Large},
        {"Prevent_Movement", Rust.Layers.Mask.Prevent_Movement},
        {"Prevent_Building", Rust.Layers.Mask.Prevent_Building},
        {"Tree", Rust.Layers.Mask.Tree},
        {"Physics_Debris", Rust.Layers.Mask.Physics_Debris},
      };
      foreach (var (maskName, maskValue) in maskDict)
      {
        _ray.origin = player.eyes.position;
        _ray.direction = player.eyes.HeadForward();
        var hc = UnityEngine.Physics.RaycastNonAlloc(
          _ray, RaycastHits, 50f, maskValue);
        if (hc <= 0)
        {
          Puts($"***** {maskName}: None");
        }
        else if (RaycastHits[0].GetEntity() is { } tempEntity)
        {
          Puts($"***** {maskName}: {tempEntity}");
        }
        else
        {
          Puts($"***** {maskName}: Null");
        }
      }
*/
      _ray.origin = player.eyes.position;
      _ray.direction = player.eyes.HeadForward();
      var hitCount = UnityEngine.Physics.RaycastNonAlloc(
        _ray, RaycastHits, 50f, LayerMask);
      if (hitCount <= 0)
      {
        player.ChatMessage("You are looking at nothing or you are too far away");
        return;
      }

      var baseEntity = RaycastHits[0].GetEntity();
      if (!baseEntity)
      {
        player.ChatMessage("You are not looking at an entity");
        return;
      }

      if (baseEntity is not BaseCombatEntity entity)
      {
        player.ChatMessage($"{baseEntity.GetType()}/{baseEntity} is not a combat entity");
        return;
      }

      if (!IsProtected(entity))
      {
        player.ChatMessage($"{entity.GetType()}/{entity} is not a protected player entity");
        return;
      }

      // TODO: this is mostly a duplicate of OnStructureAttack()...maybe move
      //  that logic into an intermediate method that returns an enum, and have
      //  both OnStructureAttack() and this call it for consistency
      var (tugboat, modularBoat, vehicle) = GetVehicle(entity);
      // player.ChatMessage($"***** tugobat={tugboat}, modularBoat={modularBoat}, vehicle={vehicle}");
      _isVehicle = vehicle;
      var authorizedPlayers =
        GetAuthorizedPlayers(entity, tugboat, modularBoat, vehicle);
      var firstPlayer = authorizedPlayers?.FirstOrDefault() ?? 0;

      if (null == authorizedPlayers || !firstPlayer.IsSteamID())
      {
        player.ChatMessage($"{entity.GetType()}/{entity} has no owner");
        return;
      }

      player.ChatMessage($"{entity.GetType()}/{entity} report:");

      var targetID = entity.OwnerID;
      if (entity.OwnerID is 0UL ||
          !authorizedPlayers.Contains(entity.OwnerID))
        targetID = firstPlayer;

      targetID = GetRecentActiveMemberAll(targetID, authorizedPlayers);

      if (!Configuration.RaidProtection.ProtectDecayingBase &&
          !_isVehicle && !tugboat && !modularBoat &&
          _tcCache.TryGetValue(_privilege.buildingID, out var tc))
      {
        player.ChatMessage(
          GetStatusText(new[] { targetID.ToString() }, tc.IsDecaying));
        return;
      }

      player.ChatMessage(GetStatusText(new[] { targetID.ToString() }));
    }

    private void cmdHelp(BasePlayer player, string _command, string[] _args)
    {
      if (!player)
        return;

      player.ChatMessage(GetHelpText(player.userID.Get()));
    }

    private void cmdFillOnlineTimes(
      BasePlayer player, string command, string[] args)
    {
      if (!player)
        return;
#if !CARBON
      if (!CheckChatCmdPerm(player, Configuration.Permission.Admin))
        return;
#endif
      var currentTime = System.DateTime.UtcNow;
      var playerCount = 0;
      foreach (var currentPlayer in BasePlayer.allPlayerList)
      {
        UpdateLastOnline(currentPlayer, currentTime);
        CacheDamageScale(currentPlayer.userID.Get(), -1f);
        playerCount++;
      }

      SaveData();

      var msg = $"Updated the {nameof(LastOnlineData)}.json file for {playerCount} players.";
      player.ChatMessage(msg);
    }

    private void cmdTestOffline(
      BasePlayer player, string _command, string[] args)
    {
      if (!player)
        return;
#if !CARBON
      if (!CheckChatCmdPerm(player, Configuration.Permission.Admin))
        return;
#endif
      if (args is null || args.Length is 0 || args.Length > 2)
      {
        player.ChatMessage(MESSAGE_INVALID_SYNTAX);
        return;
      }

      var userID = player.userID.Get();
      if (args.Length is 2)
      {
        userID = PlayerManager.GetPlayer(args[0])?.userID.Get() ?? 0UL;
        if (userID is 0UL && !ulong.TryParse(args[0], out userID))
        {
          player.ChatMessage(MESSAGE_PLAYER_NOT_FOUND);
          return;
        }
      }

      if (!double.TryParse(args[^1], out var hours))
      {
        player.ChatMessage(MESSAGE_INVALID_SYNTAX);
        return;
      }

      if (_lastOnline.TryGetValue(userID, out var target))
      {
        target.LastOnlineDT =
          target.LastOnlineDT.Subtract(System.TimeSpan.FromHours(hours));
        player.ChatMessage($"{target.UserName} | {System.TimeZoneInfo.ConvertTimeFromUtc(target.LastOnlineDT, _timeZone)}");
      }
      else
      {
        player.ChatMessage(MESSAGE_PLAYER_NOT_FOUND);
        return;
      }

      CacheDamageScale(userID, -1f);
    }

    private void cmdTestOnline(
      BasePlayer player, string _command, string[] args)
    {
      if (!player)
        return;
#if !CARBON
      if (!CheckChatCmdPerm(player, Configuration.Permission.Admin))
        return;
#endif
      if (args is null || args.Length is 0 || args.Length > 1)
      {
        if (player)
          player.ChatMessage(MESSAGE_INVALID_SYNTAX);
        return;
      }

      var userID = player.userID.Get();
      if (args.Length is 1)
      {
        userID = PlayerManager.GetPlayer(args[0])?.userID.Get() ?? 0UL;
        if (userID is 0UL && !ulong.TryParse(args[0], out userID))
        {
          player.ChatMessage(MESSAGE_PLAYER_NOT_FOUND);
          return;
        }
      }

      if (!_lastOnline.TryGetValue(userID, out var target))
      {
        player.ChatMessage(MESSAGE_PLAYER_NOT_FOUND);
        return;
      }
      target.LastOnlineDT = System.DateTime.UtcNow;
      player.ChatMessage(
        $"{target.UserName} | {System.TimeZoneInfo.ConvertTimeFromUtc(target.LastOnlineDT, _timeZone)}");

      CacheDamageScale(userID, -1f);
    }

    private void cmdTestPenalty(
      BasePlayer player, string _command, string[] args)
    {
      if (!player)
        return;
#if !CARBON
      if (!CheckChatCmdPerm(player, Configuration.Permission.Admin))
        return;
#endif
      if (args is null || args.Length is 0 || args.Length > 2)
      {
        if (player)
          player.ChatMessage(MESSAGE_INVALID_SYNTAX);

        return;
      }

      var userID = player.userID.Get();
      if (args.Length is 2)
      {
        userID = PlayerManager.GetPlayer(args[0])?.userID.Get() ?? 0UL;
        if (userID is 0UL && !ulong.TryParse(args[0], out userID))
        {
          player.ChatMessage(MESSAGE_PLAYER_NOT_FOUND);
          return;
        }
      }

      if (!float.TryParse(args[^1], out var duration))
      {
        player.ChatMessage(MESSAGE_INVALID_SYNTAX);
        return;
      }

      if (!_lastOnline.TryGetValue(userID, out var target))
      {
        player.ChatMessage(MESSAGE_PLAYER_NOT_FOUND);
        return;
      }

      if (duration > 0f)
      {
        target.EnablePenalty(duration);
        player.ChatMessage(
          $"{target.UserName} | Penalty until {System.TimeZoneInfo.ConvertTimeFromUtc(target.PenaltyEndDT, _timeZone)}");
      }
      else
      {
        target.DisablePenalty();
        player.ChatMessage($"{target.UserName} | Penalty disabled");
      }

      CacheDamageScale(userID, -1f);
    }

    #endregion ChatCommands

    #region ConsoleCommands

#if !CARBON
    private bool CheckConCmdPerm(ConsoleSystem.Arg arg, string perm)
    {
      if (arg.IsAdmin || arg.Connection?.userid.HasPermission(perm) is true)
        return true;

      SendReply(arg, LANG_MESSAGE_NOPERMISSION);
      return false;
    }
#endif

    // TODO: de-duplicate from chat command logic (code is debt) -HZ
    private void ccFillOnlineTimes(ConsoleSystem.Arg arg)
    {
      if (arg is null)
      {
        PrintError("ccFillOnlineTimes(): arg is null");
        return;
      }
#if !CARBON
      if (!CheckConCmdPerm(arg, Configuration.Permission.Admin))
        return;
#endif
      var currentTime = System.DateTime.UtcNow;
      var playerCount = 0;
      foreach (var currentPlayer in BasePlayer.allPlayerList)
      {
        UpdateLastOnline(currentPlayer, currentTime);
        CacheDamageScale(currentPlayer.userID.Get(), -1f);
        playerCount++;
      }

      SaveData();

      var msg = $"Updated the {nameof(LastOnlineData)}.json file for {playerCount} players.";
      SendReply(arg, msg);
    }

    private void ccUpdatePermissions(ConsoleSystem.Arg arg)
    {
      if (arg is null)
      {
        PrintError("ccUpdatePermissions(): arg is null");
        return;
      }
#if !CARBON
      if (!CheckConCmdPerm(arg, Configuration.Permission.Admin))
        return;
#endif
      foreach (var key in _scaleCache.Keys)
      {
        _scaleCache[key].HasPermission =
          key.HasPermission(Configuration.Permission.Protect);
      }
      SendReply(arg, "Updated the permission status for all players.");
    }

    private void ccUpdatePrefabList(ConsoleSystem.Arg arg)
    {
      if (arg is null)
      {
        PrintError("ccUpdatePermissions(): arg is null");
        return;
      }
#if !CARBON
      if (!CheckConCmdPerm(arg, Configuration.Permission.Admin))
        return;
#endif
      var count = Configuration.RaidProtection.Prefabs.Count;

      if (arg.Args?.Length is 1 && arg.Args[0] is "true")
        Configuration.RaidProtection.Prefabs = GetPrefabNames();
      else
        Configuration.RaidProtection.Prefabs.UnionWith(GetPrefabNames());

      count = Configuration.RaidProtection.Prefabs.Count - count;
      CachePrefabs();
      SaveConfig();

      SendReply(arg, $"Updated the Prefabs to protect list in the configuration. {(count >= 0 ? $"Added {count}" : $"Removed {-count}")} Prefab(s)");
    }

    private void ccDumpPrefabList(ConsoleSystem.Arg arg)
    {
      if (arg is null)
      {
        PrintError("ccFillOnlineTimes(): arg is null");
        return;
      }
#if !CARBON
      if (!CheckConCmdPerm(arg, Configuration.Permission.Admin))
        return;
#endif
      Configuration.RaidProtection.Prefabs.Clear();
      CachePrefabs();
      SaveConfig();

      SendReply(arg, "Cleared the Prefabs to protect list in the configuration.");
    }

    #endregion ConsoleCommands

    #endregion Commands

    #region Lang

    protected override void LoadDefaultMessages() => LoadMessages();

    private void LoadMessages()
    {
      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "This building is protected: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "This vehicle is protected: " }
      }, this);

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Hierdie gebou is beskerm: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Hierdie voertuig is beskerm: " }
      }, this, "af");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "هذا المبنى محمي: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "هذه السيارة محمية: " }
      }, this, "ar");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Aquest edifici està protegit: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Aquest vehicle està protegit: " }
      }, this, "ca");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Tato budova je chráněna: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Toto vozidlo je chráněno: " }
      }, this, "cs");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Denne bygning er beskyttet: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Dette køretøj er beskyttet: " }
      }, this, "da");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Dieses Gebäude ist geschützt: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Dieses Fahrzeug ist geschützt: " }
      }, this, "de");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "הבניין הזה מוגן: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "הרכב הזה מוגן: " }
      }, this, "he");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Ez az épület védett: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Ez a jármű védett: " }
      }, this, "hu");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Αυτό το κτίριο είναι προστατευμένο: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Αυτό το όχημα είναι προστατευμένο: " }
      }, this, "el");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Este edificio está protegido: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Este vehículo está protegido: " }
      }, this, "es-ES");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Tämä rakennus on suojattu: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Tämä ajoneuvo on suojattu: " }
      }, this, "fi");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Ce bâtiment est protégé: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Ce véhicule est protégé: " }
      }, this, "fr");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Questo edificio è protetto: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Questo veicolo è protetto: " }
      }, this, "it");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "この建物は保護されています: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "この車両は保護されています: " }
      }, this, "ja");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "이 건물은 보호되고 있습니다: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "이 차량은 보호되고 있습니다: " }
      }, this, "ko");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Dit gebouw is beschermd: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Dit voertuig is beschermd: " }
      }, this, "nl");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Denne bygningen er beskyttet: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Dette kjøretøyet er beskyttet: " }
      }, this, "no");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Ten budynek jest chroniony: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "To pojazd jest chroniony: " }
      }, this, "pl");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Este edifício está protegido: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Este veículo está protegido: " }
      }, this, "pt");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Această clădire este protejată: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Acest vehicul este protejat: " }
      }, this, "ro");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Ова зграда је заштићена: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Ово возило је заштићено: " }
      }, this, "sr");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Denna byggnad är skyddad: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Detta fordon är skyddat: " }
      }, this, "sv-SE");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Ця будівля захищена: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Цей транспортний засіб захищено: " }
      }, this, "uk");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Tòa nhà này được bảo vệ: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Phương tiện này được bảo vệ: " }
      }, this, "vi");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "该建筑受到保护: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "该车辆受到保护: " }
      }, this, "zh-CN");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "該建築受到保護: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "該車輛受到保護: " }
      }, this, "zh-TW");

      lang.RegisterMessages(new Dictionary<string, string>
      {
        { LANG_PROTECTION_MESSAGE_BUILDING, "Arrr! This here stronghold be fortified: " },
        { LANG_PROTECTION_MESSAGE_VEHICLE, "Yo ho ho! This here ship be secured: " }
      }, this, "en-PT");
    }

    private void DeleteMessages()
    {
      var langDirectory = System.IO.Path.Combine(Interface.Oxide.LangDirectory);
      if (!System.IO.Directory.Exists(langDirectory))
        return;

      foreach (
        var langFolder in System.IO.Directory.GetDirectories(langDirectory))
      {
        var langFilePath = System.IO.Path.Combine(langFolder, $"{Name}.json");
        if (!System.IO.File.Exists(langFilePath))
          continue;

        PrintWarning($"Deleting old language file: {langFilePath}");
        System.IO.File.Delete(langFilePath);
      }
    }

    #endregion Lang

    #region Texts

    private string GetStatusText(in string[] args, in bool isDecaying = false)
    {
      if (args?.Length is not 1)
        return MESSAGE_INVALID_SYNTAX;

      var userID = PlayerManager.GetPlayer(args[0])?.userID.Get() ?? 0UL;
      if (userID is 0UL && !ulong.TryParse(args[0], out userID) ||
          !_lastOnline.TryGetValue(userID, out var lastOnline))
        return MESSAGE_PLAYER_NOT_FOUND;

      var isOnline = lastOnline.IsOnline;
      var onlineColor = isOnline ? COLOR_GREEN : COLOR_RED;

      _sb.Clear();
      _sb.AppendLine($"<color={COLOR_BLUE}><size={MESSAGE_TITLE_SIZE}>Offline Raid Protection Status</size></color> {lastOnline.UserName}");
      _sb.AppendLine($"<color={COLOR_YELLOW}>Player Status</color> <color={onlineColor}>{(isOnline ? "Online</color>" : $"Offline</color> {System.TimeZoneInfo.ConvertTimeFromUtc(lastOnline.LastOnlineDT, _timeZone)}")}");

      AppendTeamOrClanMembersStatus(userID);

      var penaltyEnabled =
        lastOnline.PenaltyEnd >= System.DateTime.UtcNow.Ticks;
      if (Configuration.Team.TeamEnablePenalty)
        _sb.AppendLine($"<color={COLOR_YELLOW}>Penalty Status</color> {(penaltyEnabled ? $"<color={COLOR_RED}>Enabled</color> {System.TimeZoneInfo.ConvertTimeFromUtc(lastOnline.PenaltyEndDT, _timeZone)}" : $"<color={COLOR_GREEN}>Disabled</color>")}");

      if (penaltyEnabled)
        return _sb.ToString();

      if (isDecaying)
      {
        _sb.AppendLine($"<color={COLOR_AQUA}>Scale</color> 0 (Decaying)");
      }
      else
      {
        var scale = GetDamageScale(
          GetRecentActiveMemberAll(userID),
          _scaleCache.GetValueOrDefault(userID, null));
        var prot = scale.ToPercent();
        if (scale is not -1)
        {
          _sb.AppendLine(
            $"<color={COLOR_AQUA}>Scale</color> {scale} ({(prot >= 0f ? $"{prot}% Protection" : $"+{-prot}% Damage")})");
        }
      }

      return _sb.ToString();
    }

    private void AppendTeamOrClanMembersStatus(in ulong userID)
    {
      if (!Configuration.Team.TeamShare)
        return;

      var tag = Clans is not null ? GetClanTag(userID) : null;
      var members = string.IsNullOrEmpty(tag) ?
        GetTeamMembers(userID) : GetClanMembers(tag);

      if (!(members?.Count > 1))
        return;

      _textTable.Clear();
      _textTable.AddColumn($"<color={COLOR_DARK_GREEN}>{(Clans is not null ? TEXT_CLAN_MEMBER : TEXT_TEAM_MEMBER)}</color>");

      foreach (var member in members)
      {
        if (userID == member)
          continue;

        if (!_lastOnline.TryGetValue(member, out var m))
          continue;

        var memberOnline = m.IsOnline;
        var newRow = $"{m.UserName} | {(memberOnline ? $"<color={COLOR_GREEN}>Online</color>" : $"<color={COLOR_RED}>Offline</color> | {System.TimeZoneInfo.ConvertTimeFromUtc(m.LastOnlineDT, _timeZone)}")}";
        _textTable.AddRow(newRow);
      }

      _sb.AppendLine(_textTable.ToString());
    }

    private string GetHelpText(in ulong userID)
    {
      _sb.Clear();
      _sb.AppendLine($"<color={COLOR_BLUE}><size={MESSAGE_TITLE_SIZE}>Offline Raid Protection Info</size></color> {System.TimeZoneInfo.ConvertTimeFromUtc(System.DateTime.UtcNow, _timeZone):HH:mm:ss} {_timeZone.DisplayName.Split(' ')[0]}");

      if (Configuration.RaidProtection.AbsoluteTimeScale.Keys.Count > 0)
      {
        foreach (var key in _absolutTimeScaleKeys)
        {
          var scalePercent = $"{Configuration.RaidProtection.AbsoluteTimeScale[key].ToPercent()}";
          var hours = key.ToString();

          _sb.AppendLine($"<color={COLOR_ORANGE}>At {hours} o'clock</color>: {(scalePercent.ToFloat() >= 0f ? $"{scalePercent}% Protection" : $"+{-scalePercent.ToFloat()}% Damage")}");
        }
      }

      if (Configuration.RaidProtection.DamageScale.Keys.Count > 0)
      {
        var interimDamageScalePercent =
          Configuration.RaidProtection.InterimDamage.ToPercent();
        if (Configuration.RaidProtection.CooldownMinutes > 0)
        {
          _sb.AppendLine($"<color={COLOR_ORANGE}>First {Configuration.RaidProtection.CooldownMinutes} minutes</color>: 0% Protection")
            .AppendLine($"<color={COLOR_ORANGE}>Between {Configuration.RaidProtection.CooldownMinutes} minutes and {_damageScaleKeys[0]} hours</color>: {interimDamageScalePercent}% Protection");
        }
        else
          _sb.AppendLine($"<color={COLOR_ORANGE}>First {_damageScaleKeys[0]} hour(s)</color>: {interimDamageScalePercent}% Protection");

        foreach (var key in _damageScaleKeys)
        {
          var scalePercent = $"{Configuration.RaidProtection.DamageScale[key].ToPercent()}";
          _sb.AppendLine($"<color={COLOR_ORANGE}>After {key} hours</color>: {(scalePercent.ToFloat() >= 0f ? $"{scalePercent}% Protection" : $"+{-scalePercent.ToFloat()}% Damage")}");
        }
      }

      if (!Configuration.Team.TeamEnablePenalty ||
          !_lastOnline.TryGetValue(userID, out var lastOnline))
        return _sb.ToString();

      var penaltyEnabled =
        lastOnline.PenaltyEnd >= System.DateTime.UtcNow.Ticks;
      _sb.AppendLine($"<color={COLOR_YELLOW}>Penalty Status</color> {(penaltyEnabled ? $"<color={COLOR_RED}>Enabled</color> {System.TimeZoneInfo.ConvertTimeFromUtc(lastOnline.PenaltyEndDT, _timeZone):HH:mm:ss}" : $"<color={COLOR_GREEN}>Disabled</color>")}");

      return _sb.ToString();
    }

    #endregion Texts

    #region Helper Methods

    private string Msg(in string key, in string userID = null) =>
      lang.GetMessage(key, this, userID);

    private const int ItemIdWood  = -151838493;
    private const int ItemIdStone = -2099697608;
    private const int ItemIdMetal =  69511070;
    private const int ItemIdHqm   =  317398316;

    private bool IsBuildingDecaying(
        in BuildingPrivlidge toolCupboard, in bool hasProtectedMinutes)
    {
      if (!toolCupboard)
        return true;

      var itemList = toolCupboard.inventory?.itemList;
      var building = toolCupboard.GetBuilding();
      var buildingBlocks = building?.buildingBlocks;
      var decayEntities = building?.decayEntities;
      if (itemList is null || buildingBlocks is null || decayEntities is null)
        return true;

      var hasBlockTwig = false;
      var hasBlockWood = false;
      var hasBlockStone = false;
      var hasBlockMetal = false;
      var hasBlockHqm = false;

      var totalBlocks = 0;
      var damagedBlocks = 0;
      const float damageThreshold = 0.9f; // e.g., below 90% health is "damaged"

      // scan building's blocks to see which building grades are present, and
      //  how many are of interest to damage threshold calculations
      foreach (var block in buildingBlocks)
      {
        // NOTE: `break` versus `continue` determines whether this block should
        //  be counted in damage calculations
        switch (block.grade)
        {
          case BuildingGrade.Enum.None:
            continue;
          case BuildingGrade.Enum.Twigs:
            hasBlockTwig = true;
            // don't count twig block damage, because these can be added to a
            //  base by players without build privilege
            continue;
          case BuildingGrade.Enum.Wood:
            hasBlockWood = true;
            break;
          case BuildingGrade.Enum.Stone:
            hasBlockStone = true;
            break;
          case BuildingGrade.Enum.Metal:
            hasBlockMetal = true;
            break;
          case BuildingGrade.Enum.TopTier:
            hasBlockHqm = true;
            break;
          case BuildingGrade.Enum.Count:
          default:
            continue;
        }

        ++totalBlocks;
        if (block.healthFraction < damageThreshold)
          ++damagedBlocks;
      }

      // if over 50% of eligible blocks have health below the damage threshold,
      //  consider the building to be decaying
      if (totalBlocks > 0 && (float)damagedBlocks / totalBlocks > 0.5f)
        return true;

      // if the building is not actually decaying, report that
      if (hasProtectedMinutes)
        return false;

      // at this point the building is decaying; report that unless there's a
      //  possibility that decay is due to twig, and the config option to ignore
      //  twig is enabled
      if (!hasBlockTwig || hasBlockWood ||
          !Configuration.RaidProtection.DecayIgnoreTwig)
        return true;

      // check for each required resource in the building TC's inventory
      // NOTE: remember that the building is decaying, so at least one resource
      //  is missing!
      var satisfiedStone = !hasBlockStone;
      var satisfiedMetal = !hasBlockMetal;
      var satisfiedHqm   = !hasBlockHqm;

      foreach (var item in itemList)
      {
        switch (item.info.itemid)
        {
          case ItemIdWood: // wood
            // there is wood in the TC, so the building is not decaying due to
            //  twig, and we can immediately report that the building is
            //  decaying here
            return true;

          case ItemIdStone: // stone
            satisfiedStone = true;
            break;

          case ItemIdMetal: // metal fragments
            satisfiedMetal = true;
            break;

          case ItemIdHqm: // high quality metal
            satisfiedHqm = true;
            break;
        }
      }

      // if something other than wood is missing, the building is not decaying
      //  solely due to twig, so we can immediately report that the building is
      //  decaying here
      if (!satisfiedStone || !satisfiedMetal || !satisfiedHqm)
        return true;

      // at this point the building has twig, no upgraded wood blocks, and only
      //  decay is solely due to a lack of wood in the TC; determine whether
      //  there are any other wood-based decay entities in the building
      foreach (var decayEntity in decayEntities)
      {
        if (decayEntity is BuildingBlock)
          continue;

        if (decayEntity.Upkeep?.upkeepMultiplier is not > 0)
          continue;

        var buildCost = decayEntity.BuildCost();
        if (buildCost is null)
          continue;

        foreach (var itemAmount in buildCost)
        {
          if (ItemIdWood == itemAmount.itemid)
            return true; // found another item requiring wood upkeep
        }
      }

      // twig is solely responsible for decay, so ignore that
      return false;
    }

    #endregion Helper Methods
  }
}

#region Extension Methods

#if CARBON
namespace Carbon.Plugins.OfflineRaidProtectionEx
#else
namespace Oxide.Plugins.OfflineRaidProtectionEx
#endif
{
  public static class ExtensionMethods
  {
    private static readonly Permission P;

    static ExtensionMethods() => P = Interface.Oxide.GetLibrary<Permission>();

    private static bool HasPermission(this string userID, in string permission)
      => !string.IsNullOrEmpty(userID) &&
         P.UserHasPermission(userID, permission);

    public static bool HasPermission(
      this BasePlayer player, in string permission) =>
      player.UserIDString.HasPermission(permission);

    public static bool HasPermission(this in ulong userID, in string permission)
      => userID.ToString().HasPermission(permission);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToPercent(this in float value) => 100f - value * 100f;

    public static void ClearAndMergeWith<TKey, TValue>(
      this Dictionary<TKey, TValue> first,
      params Dictionary<TKey, TValue>[] others)
    {
      if (first is null)
        return;

      first.Clear();

      foreach (var dictionary in others)
      {
        if (dictionary is null)
          continue;

        foreach (var kvp in dictionary)
          first.TryAdd(kvp.Key, kvp.Value);
      }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSteamID(this in ulong id) => id > 76561197960265728UL;
  }
}

#endregion Extension Methods

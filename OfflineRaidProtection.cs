#if CARBON
using Carbon.Components;
using Carbon.Plugins.OfflineRaidProtectionEx;
using System.Runtime.InteropServices;
#if !MINIMAL
using Carbon.Modules;
#endif
#else
using Oxide.Game.Rust.Cui;
using Oxide.Plugins.OfflineRaidProtectionEx;
#endif

using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using PluginTimer = Oxide.Plugins.Timer;
using Random = Oxide.Core.Random;

namespace
#if CARBON
  Carbon.Plugins
#else
  Oxide.Plugins
#endif
{
  [Info("Offline Raid Protection", "realedwin/HunterZ", "1.8.0"), Description("Prevents/reduces offline raids by other players")]
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
    private readonly Dictionary<uint, bool> _prefabProtection = new();
    private readonly Dictionary<uint, float> _prefabProtectionMultipliers = new();
    private readonly Dictionary<uint, TcState> _tcCache = new();
    private readonly Dictionary<uint, CodeLockWhitelistIndex> _codeLockWhitelistCache = new();
    private readonly Dictionary<ulong, uint> _codeLockBuildingIds = new();
    private readonly List<CodeLock> _queuedSpawnedCodeLocks = new();
    private readonly Queue<uint> _queuedTcCacheRefreshes = new();
    private readonly HashSet<uint> _queuedTcCacheRefreshIds = new();
    private readonly HashSet<uint> _queuedPhysicalTcCacheRefreshIds = new();
    private readonly HashSet<uint> _tcCacheRefreshVisualBuildingIdsScratch = new();
    private readonly Dictionary<ulong, TcCreationData> _tcCreationData = new();
    private readonly HashSet<ulong> _griefCupboardIds = new();
    private readonly Dictionary<uint, HashSet<ulong>>
      _griefOverlapCupboardIdsByBuilding = new();
    private readonly Dictionary<ulong, HashSet<uint>>
      _griefOverlapBuildingIdsByCupboard = new();
    private readonly HashSet<ulong> _griefAffectedCupboardIdsScratch = new();
    private readonly HashSet<ulong> _griefCurrentOverlapCupboardIdsScratch = new();
    private readonly HashSet<ulong> _adminIDCache = new();
    private readonly PlayerRuntimeIndex _players = new();
    private readonly DamageScratchSlot _damageScratch = new();
    private readonly RelatedPlayerGroupsScratch _relatedPlayerGroupsScratch = new();
    private readonly PlayerIdSet _teamMembersScratch =
      new(PlayerIdSet.TeamMembersInitialCapacity);
    private bool _dataDirty;
    private bool _saveQueued;
    private bool _serverInitialized;
    private bool _spawnedCodeLocksQueued;
    private System.Action _processQueuedSpawnedCodeLocksAction;
    private bool _tcCacheRefreshQueued;
    private System.Action _processQueuedTcCacheRefreshesAction;

    private static readonly object
      BoxedFalse = false,
      BoxedTrue = true,
      BoxedGameTipStyleBlueShort = GameTip.Styles.Blue_Short;

    private readonly object[] _gameTipArgs = new object[4];

    private System.TimeZoneInfo _timeZone;

#region Temp

    private readonly StringBuilder _sb = new(2048);
    private readonly HashSet<ulong> _tmpIdsScratch = new();
    private readonly PlayerIdSet _tmpIdSetScratch =
      new(PlayerIdSet.AuthorizationInitialCapacity);

#endregion Temp

#region Constants

    private const int TC_CACHE_REFRESH_BATCH_SIZE = 16;

    private const string
      ORP_PREFIX = "[ORP] ",
      ORP_PREFIX_COLORED = "<color=" + COLOR_BLUE + ">" + ORP_PREFIX + "</color>",
      COMMAND_SHOWTOAST = "gametip.showtoast",
#if !CARBON
      LANG_MESSAGE_NOPERMISSION = "You don't have the permission to use this command",
#endif
      LANG_PROTECTION_MESSAGE_BUILDING = "Protection Message Building",
      LANG_PROTECTION_MESSAGE_VEHICLE = "Protection Message Vehicle",
      MESSAGE_INVALID_SYNTAX = "Invalid Syntax",
      MESSAGE_PLAYER_NOT_FOUND = "No player found",
      FALLBACK_MESSAGE =
        "No profile is active; using configuration values.",
      EMPTY_FALLBACK_MESSAGE =
        "No profile is active; configuration values are empty.",
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
      COLOR_WHITE = "#FFFFFF",
      COLOR_YELLOW = "#FFFF00";

#endregion Colors

#endregion Constants

#endregion Fields

#region Classes

    private sealed class ConfigData
    {
      [JsonProperty(PropertyName = "Raid Protection Options")]
      public RaidProtectionOptions RaidProtection { get; set; }

      [JsonProperty(PropertyName = "Tax Protection Options")]
      public TaxProtectionOptions TaxProtection { get; set; }

      [JsonProperty(PropertyName = "Apartment Complex Options")]
      public ApartmentOptions ApartmentProtection { get; set; }

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

      [JsonProperty(PropertyName = "Status HUD Options")]
      public StatusHudOptions StatusHud { get; set; }

      [JsonProperty(PropertyName = "Map Marker Options")]
      public MapMarkerOptions MapMarker { get; set; }

      public VersionNumber Version { get; set; }

      public sealed class RaidProtectionOptions
      {
        [JsonProperty(PropertyName = "Only mitigate damage caused by players")]
        public bool OnlyPlayerDamage { get; set; }

        [JsonProperty(PropertyName = "Protect players that are online")]
        public bool OnlineRaidProtection { get; set; }

        [JsonProperty(PropertyName = "Enable scheduled timescales")]
        public bool EnableScheduledTimescales { get; set; }

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

        [JsonProperty(PropertyName = "Protect decaying modular boats")]
        public bool ProtectDecayingModularBoats { get; set; }

        [JsonProperty(PropertyName = "Protect grief TCs")]
        public bool ProtectGriefTcs { get; set; }

        [JsonProperty(PropertyName = "Prefabs to protect")]
        public HashSet<string> Prefabs { get; set; }

        [JsonProperty(PropertyName = "Prefabs blacklist")]
        public HashSet<string> PrefabsBlacklist { get; set; }

        [JsonProperty(PropertyName = "Protection multipliers by prefab")]
        public Dictionary<string, float> PrefabProtectionMultipliers { get; set; }
      }

      public sealed class TaxProtectionOptions
      {
        [JsonProperty(PropertyName = "Enabled")]
        public bool Enabled { get; set; }

        [JsonProperty(PropertyName = "Enable for modular boats")]
        public bool EnableForModularBoats { get; set; }

        [JsonProperty(PropertyName = "Currency item ID")]
        public int CurrencyItemID { get; set; }

        [JsonProperty(PropertyName = "Cost per hour")]
        public int CostPerHour { get; set; }

        [JsonProperty(PropertyName = "Group size hourly cost scaling options")]
        public GroupSizeCostScalingOptions GroupSizeCostScaling { get; set; }

        [JsonProperty(PropertyName = "Refund unused tax protection on Tool Cupboard destruction")]
        public bool RefundOnDestruction { get; set; }

        [JsonIgnore]
        private int _maxCurrencyReserves;

        [JsonProperty(PropertyName = "Maximum tax currency reserves per Tool Cupboard")]
        public int MaxCurrencyReserves
        {
          get => _maxCurrencyReserves;
          set => _maxCurrencyReserves = System.Math.Max(-1, value);
        }

        [JsonIgnore]
        public bool TaxCurrencyReservesEnabled => _maxCurrencyReserves >= 0;

        [JsonIgnore]
        private int _maxPurchaseHours;

        [JsonProperty(PropertyName = "Maximum total purchased protection hours")]
        public int MaxPurchaseHours
        {
          get => _maxPurchaseHours;
          set => _maxPurchaseHours = System.Math.Max(1, System.Math.Min(value,
            (int)(System.DateTime.MaxValue.Ticks /
              System.TimeSpan.TicksPerHour)));
        }

        [JsonProperty(PropertyName = "Tax Overlay Options")]
        public TaxOverlayOptions TaxOverlay { get; set; } = new();

        public sealed class TaxOverlayOptions
        {
          [JsonProperty(PropertyName = "Enabled")]
          public bool Enabled { get; set; }

          [JsonProperty(PropertyName = "Anchor minimum")]
          public string AnchorMin { get; set; }

          [JsonProperty(PropertyName = "Anchor maximum")]
          public string AnchorMax { get; set; }

          [JsonProperty(PropertyName = "Offset minimum")]
          public string OffsetMin { get; set; }

          [JsonProperty(PropertyName = "Offset maximum")]
          public string OffsetMax { get; set; }
        }

        public sealed class GroupSizeCostScalingOptions
        {
          [JsonProperty(PropertyName = "Enabled")]
          public bool Enabled { get; set; }

          [JsonProperty(PropertyName = "Players included in the base hourly cost")]
          public int BaseCostPlayerCount { get; set; }

          [JsonProperty(PropertyName = "Additional players in the small group tier")]
          public int SmallGroupPlayerCount { get; set; }

          [JsonProperty(PropertyName = "Hourly cost increase per additional small group player (percent)")]
          public decimal SmallGroupIncreasePercent { get; set; }

          [JsonProperty(PropertyName = "Hourly cost increase per player above the small group tier (percent)")]
          public decimal LargeGroupIncreasePercent { get; set; }

          [JsonProperty(PropertyName = "Maximum hourly cost multiplier")]
          public decimal MaximumCostMultiplier { get; set; }
        }
      }

      public sealed class ApartmentOptions
      {
        [JsonProperty(PropertyName = "Protect apartments from break-ins")]
        public bool ProtectApartments { get; set; }

        [JsonProperty(PropertyName = "Protect apartment even when owner absent")]
        public bool WhenAbsent { get; set; }

        [JsonProperty(PropertyName = "Protect apartment even when rent due")]
        public bool WhenRentDue { get; set; }

        [JsonProperty(PropertyName = "Protect shops from break-ins")]
        public bool ProtectShops { get; set; }

        [JsonProperty(PropertyName = "Protect only when damage scale below")]
        public float WhenDamageBelow { get; set; }

        [JsonProperty(PropertyName = "Use damage scale as break-in success chance")]
        public bool DamageAsChance { get; set; }
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

        [JsonProperty(PropertyName = "Command to toggle a TC's forced grief status")]
        public string CommandTestGrief { get; set; }

        [JsonProperty(PropertyName = "Command to edit scheduled timescale profiles")]
        public string CommandScheduledTimescales { get; set; } = "orp.schedule";

        [JsonProperty(PropertyName = "Command to update the Prefabs to protect list")]
        public string CommandUpdatePrefabList { get; set; }

        [JsonProperty(PropertyName = "Command to dump the Prefabs to protect list")]
        public string CommandDumpPrefabList { get; set; }

        [JsonProperty(PropertyName = "Command to manually manage tax protection")]
        public string CommandTaxProtection { get; set; }

        [JsonProperty(PropertyName = "Command to display ORP DDraw")]
        public string CommandOrpDdraw { get; set; }

#if CARBON
        [JsonProperty(PropertyName = "Command cooldown in seconds")]
        public int CommandCooldown
        {
          get;
          set => field = System.Math.Max(0, value);
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
          RegisterChatCommands(new[] {CommandTestGrief}, plugin, offlineRaidProtection.cmdTestGrief, Configuration.Permission.Admin);
          RegisterChatCommands(new[] {CommandScheduledTimescales}, plugin, offlineRaidProtection.cmdScheduledTimescales, Configuration.Permission.Admin);
          RegisterChatCommands(new[] {CommandTaxProtection}, plugin, offlineRaidProtection.cmdBuyTaxProtection, Configuration.Permission.TaxProtection);

          RegisterConsoleCommands(new[] {CommandFillOnlineTimes}, plugin, nameof(Instance.ccFillOnlineTimes), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandUpdatePermissions}, plugin, nameof(Instance.ccUpdatePermissions), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandUpdatePrefabList}, plugin, nameof(Instance.ccUpdatePrefabList), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandDumpPrefabList}, plugin, nameof(Instance.ccDumpPrefabList), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandOrpDdraw}, plugin, nameof(Instance.ccOrpDdraw), Configuration.Permission.Admin);
#if !CARBON
          RegisterConsoleCommands(new[] {TAX_OVERLAY_COMMAND}, plugin, nameof(Instance.ccTaxOverlay), string.Empty);
#endif
        }

        // NOTE: Carbon path is non-static
        private void RegisterChatCommands(
          string[] commands, Plugin plugin,
          System.Action<BasePlayer, string, string[]> callback,
          string permission)
        {
          foreach (var command in commands)
          {
            if (string.IsNullOrEmpty(command))
              continue;
#if CARBON
            if (string.IsNullOrEmpty(permission))
              Community.Runtime.Core.cmd.AddChatCommand(
                command, plugin, callback,
                cooldown: CommandCooldown * 1000);
            else
              Community.Runtime.Core.cmd.AddChatCommand(
                command, plugin, callback,
                cooldown: CommandCooldown * 1000,
                permissions: [permission]);
#else
            Instance.cmd.AddChatCommand(command, plugin, callback);
#endif
          }
        }

        // NOTE: Carbon path is non-static
        private void RegisterConsoleCommands(
          string[] commands, Plugin plugin, string callback, string permission)
        {
          foreach (var command in commands)
          {
            if (string.IsNullOrEmpty(command))
              continue;
#if CARBON
            if (string.IsNullOrEmpty(permission))
              Community.Runtime.Core.cmd.AddConsoleCommand(
                command, plugin, callback,
                cooldown: CommandCooldown * 1000);
            else
              Community.Runtime.Core.cmd.AddConsoleCommand(
                command, plugin, callback,
                cooldown: CommandCooldown * 1000,
                permissions: [permission]);
#else
            Instance.cmd.AddConsoleCommand(command, plugin, callback);
#endif
          }
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

        [JsonProperty(PropertyName = "Permission required to manage tax protection")]
        public string TaxProtection { get; set; }

        [JsonProperty(PropertyName = "Permission to force online protection for specific players")]
        public string OnlineProtect { get; set; }

        internal void RegisterPermissions(Permission permission, Plugin plugin)
        {
          string[] permissions = {Protect, Check, Admin, TaxProtection, OnlineProtect};

          foreach (var perm in permissions)
          {
            if (!string.IsNullOrEmpty(perm))
              permission.RegisterPermission(perm, plugin);
          }
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

        [JsonProperty(PropertyName = "Weapon categories that trigger game tip messages")]
        public HashSet<GameTipWeaponCategory> GameTipWeaponCategories { get; set; }

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

      public sealed class StatusHudOptions
      {
        [JsonProperty(PropertyName = "Enabled")]
        public bool Enabled { get; set; }

        [JsonProperty(PropertyName = "Anchor minimum")]
        public string AnchorMin { get; set; }

        [JsonProperty(PropertyName = "Anchor maximum")]
        public string AnchorMax { get; set; }

        [JsonProperty(PropertyName = "Offset minimum")]
        public string OffsetMin { get; set; }

        [JsonProperty(PropertyName = "Offset maximum")]
        public string OffsetMax { get; set; }

        [JsonProperty(PropertyName = "Refresh interval in seconds")]
        public float RefreshInterval { get; set; }

        [JsonProperty(PropertyName = "Display inside trusted privilege")]
        public bool DisplayInTrustedPrivilege { get; set; }

        [JsonProperty(PropertyName = "Display only when protection is active")]
        public bool DisplayOnlyWhenProtectionActive { get; set; }

        [JsonProperty(PropertyName = "Display after status command")]
        public bool DisplayOnStatusCommand { get; set; }

        [JsonProperty(PropertyName = "Status command display duration in seconds")]
        public float Duration { get; set; }

        [JsonProperty(PropertyName = "Show protection percentage")]
        public bool ShowProtectionPercentage { get; set; }

        [JsonProperty(PropertyName = "Show remaining protection time")]
        public bool ShowRemainingTime { get; set; }

        [JsonProperty(PropertyName = "Show penalty timer")]
        public bool ShowPenaltyTimer { get; set; }
      }

      public sealed class MapMarkerOptions
      {
        [JsonProperty(PropertyName = "Enabled")]
        public bool Enabled { get; set; }

        [JsonProperty(PropertyName = "Refresh interval in seconds")]
        public float RefreshInterval { get; set; }

        [JsonProperty(PropertyName = "Enable boat live circle")]
        public bool EnableBoatLiveCircle { get; set; }

        [JsonProperty(PropertyName = "Visual radius in metres")]
        public float Radius { get; set; }

        [JsonProperty(PropertyName = "Alpha")]
        public float Alpha { get; set; }

        [JsonProperty(PropertyName = "Protected color")]
        public string ProtectedColor { get; set; }

        [JsonProperty(PropertyName = "Partial protection color")]
        public string PartialColor { get; set; }

        [JsonProperty(PropertyName = "Vulnerable color")]
        public string VulnerableColor { get; set; }

        [JsonProperty(PropertyName = "Decaying color")]
        public string DecayingColor { get; set; }

        [JsonProperty(PropertyName = "Grief color")]
        public string GriefColor { get; set; }

        [JsonProperty(PropertyName = "Outline color")]
        public string OutlineColor { get; set; }

        [JsonProperty(PropertyName = "Tooltip marker text max. players")]
        public int TooltipMaxPlayers { get; set; }
      }
    }

    private sealed class LastOnlineData
    {
      private long _lastOnline;
      private long _penaltyEnd;
      private long _lastConnect;

      [JsonProperty(PropertyName = "User ID")]
      public ulong UserID { get; set; }

      [JsonProperty(PropertyName = "User Name")]
      public string UserName { get; set; }

      [JsonProperty(PropertyName = "Last Online")]
      public long LastOnline
      {
        get => _lastOnline;
        set
        {
          _lastOnline = value;
          LastOnlineTicks = value & 0x3FFFFFFFFFFFFFFF; // Clear top 2 bits (Kind flags)
        }
      }

      [JsonProperty(PropertyName = "End of Penalty")]
      public long PenaltyEnd
      {
        get => _penaltyEnd;
        set
        {
          _penaltyEnd = value;
          PenaltyEndTicks = value;
        }
      }

      [JsonProperty(PropertyName = "Last Connect")]
      public long LastConnect
      {
        get => _lastConnect;
        set
        {
          _lastConnect = value;
          LastConnectTicks = value & 0x3FFFFFFFFFFFFFFF; // Clear top 2 bits (Kind flags)
        }
      }

      [JsonIgnore]
      public long LastOnlineTicks { get; private set; }

      [JsonIgnore]
      public long PenaltyEndTicks { get; private set; }

      [JsonIgnore]
      public long LastConnectTicks { get; private set; }

      [JsonIgnore]
      public System.DateTime LastOnlineDT
      {
        get => System.DateTime.FromBinary(LastOnline);
        set => LastOnline = value.ToBinary();

      }

      [JsonIgnore]
      public System.DateTime PenaltyEndDT
      {
        get => new(PenaltyEndTicks);
        private set => PenaltyEnd = value.Ticks;
      }

      [JsonIgnore]
      public System.DateTime LastConnectDT
      {
        get => System.DateTime.FromBinary(LastConnect);
        set => LastConnect = value.ToBinary();
      }

      [JsonConstructor]
      public LastOnlineData(
        ulong userid, string userName, long lastOnline,
        long lastConnect)
      {
        UserID = userid;
        UserName = userName;
        LastOnline = lastOnline;
        LastConnect = lastConnect;
      }

      public LastOnlineData(
        BasePlayer player, System.DateTime currentTime,
        bool connected = false) :
        this(player.userID.Get(), player.displayName, 0, 0)
      {
        LastOnlineDT = currentTime;
        if (connected)
          LastConnectDT = currentTime;
      }

      public void EnablePenalty(System.DateTime penaltyEndUtc) =>
        PenaltyEndDT = penaltyEndUtc;

      public void DisablePenalty() => PenaltyEnd = 0L;

      public void RefreshRuntimeTicks()
      {
        LastOnlineTicks = System.DateTime.FromBinary(LastOnline).Ticks;
        LastConnectTicks = System.DateTime.FromBinary(LastConnect).Ticks;
        PenaltyEndTicks = PenaltyEnd;
      }
    }

    private sealed class PlayerScaleCache
    {
      public string UserIDText { get; }
      public System.Action HideGameTipAction { get; }
      public string ProtectionMessageBuilding { get; private set; }
      public string ProtectionMessageVehicle { get; private set; }
      public long ExpiresTicks { get; set; }
      public System.TimeSpan RemainingTime { get; set; }
      public float Scale { get; set; }
      public bool ActiveGameTipMessage { get; set; }
      public bool HasProtectPermission { get; set; }
      public bool HasTaxPermission { get; set; }
      public bool HasOnlineProtectPermission { get; set; }

      public PlayerScaleCache(
        string userIDText, System.DateTime expires, float scale,
        bool hasProtectPermission,
        bool hasTaxPermission, bool hasOnlineProtectPermission)
      {
        UserIDText = userIDText;
        ExpiresDT = expires;
        Scale = scale;
        ActiveGameTipMessage = false;
        HasProtectPermission = hasProtectPermission;
        HasTaxPermission = hasTaxPermission;
        HasOnlineProtectPermission = hasOnlineProtectPermission;
        HideGameTipAction = HideGameTip;
      }

      public System.DateTime ExpiresDT
      {
        // get => new(Expires);
        set => ExpiresTicks = value.Ticks;
      }

      private void HideGameTip() => ActiveGameTipMessage = false;

      public void CacheMessages(OfflineRaidProtection plugin)
      {
        ProtectionMessageBuilding =
          PrefixMessage(plugin.Msg(
            LANG_PROTECTION_MESSAGE_BUILDING, UserIDText), true);
        ProtectionMessageVehicle =
          PrefixMessage(plugin.Msg(
            LANG_PROTECTION_MESSAGE_VEHICLE, UserIDText), true);
      }
    }

    private sealed class PlayerRuntimeIndex
    {
      // Runtime-only player lookup to avoid touching global player managers in
      // the hot path; kept instance-bound so connect/disconnect state stays local
      private readonly Dictionary<ulong, BasePlayer> _playersByUserID = new();
      private readonly Dictionary<string, BasePlayer> _playersByName = new();

      public void AddPlayer(BasePlayer player)
      {
        if (!player)
          return;

        _playersByUserID[player.userID.Get()] = player;
        if (!string.IsNullOrEmpty(player.displayName))
          _playersByName[player.displayName] = player;
      }

      public void UpdateName(
        ulong userID, string oldName, string newName)
      {
        if (!string.IsNullOrEmpty(oldName))
          _playersByName.Remove(oldName);

        if (!_playersByUserID.TryGetValue(userID, out var player))
          return;

        if (!string.IsNullOrEmpty(newName))
          _playersByName[newName] = player;
      }

      public BasePlayer GetPlayer(ulong userID) =>
        _playersByUserID.GetValueOrDefault(userID, null);

      public BasePlayer GetPlayer(string displayName)
      {
        if (string.IsNullOrEmpty(displayName))
          return null;

        return _playersByName.TryGetValue(displayName, out var player) ||
          ulong.TryParse(displayName, out var userID) &&
            _playersByUserID.TryGetValue(userID, out player) ?
              player : null;
      }

      public void Clear()
      {
        _playersByUserID.Clear();
        _playersByName.Clear();
      }
    }

    private sealed class PlayerIdSet : Facepunch.Pool.IPooled
    {
      // Fixed-capacity insertion-ordered ID set for hot-path authorization
      // expansion. HashSet handles dedupe; List preserves existing iteration order
      private const int Capacity = 1024;

      // Initial backing sizes by workload; all profiles retain Capacity as
      // their shared logical limit and grow normally when a larger set occurs
      public const int
        CodeLockWhitelistInitialCapacity = 16,
        AuthorizationInitialCapacity = 32,
        TeamMembersInitialCapacity = 16,
        RelatedPlayersInitialCapacity = 32;

      private readonly HashSet<ulong> _lookup;
      private readonly List<ulong> _items;

      // Facepunch.Pool constructs this type through its parameterless path
      public PlayerIdSet() : this(CodeLockWhitelistInitialCapacity) { }

      public PlayerIdSet(int initialCapacity)
      {
        _lookup = new(initialCapacity);
        _items = new(initialCapacity);
      }

      public int Count => _items.Count;

      public bool Overflowed { get; private set; }

      public ulong First => _items.Count > 0 ? _items[0] : 0UL;

      public ulong this[int index] => _items[index];

      public List<ulong> GetList() => _items;

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void Clear()
      {
        _items.Clear();
        _lookup.Clear();
        Overflowed = false;
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public bool Contains(ulong value) => _lookup.Contains(value);

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void Add(ulong value)
      {
        if (value is 0UL)
          return;

        if (_items.Count >= Capacity)
        {
          if (!_lookup.Contains(value))
            Overflowed = true;
          return;
        }

        if (!_lookup.Add(value))
          return;

        _items.Add(value);
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void Remove(ulong value)
      {
        if (_lookup.Remove(value))
          _items.Remove(value);
      }

      public void AddRange(HashSet<ulong> values)
      {
        if (values is null)
          return;

        foreach (var value in values)
          Add(value);
      }

      public void AddRange(List<ulong> values)
      {
        if (values is null)
          return;

        for (var i = 0; i < values.Count; i++)
          Add(values[i]);
      }

      public void AddRange(PlayerIdSet values)
      {
        if (values is null)
          return;

        for (var i = 0; i < values.Count; i++)
          Add(values[i]);
      }

      public void EnterPool() => Clear();

      public void LeavePool() { }
    }

    private sealed class CodeLockWhitelistSnapshot : Facepunch.Pool.IPooled
    {
      public readonly HashSet<ulong> PlayerIds = new(32);

      public void EnterPool() => PlayerIds.Clear();

      public void LeavePool() { }
    }

    private sealed class CodeLockWhitelistIndex : Facepunch.Pool.IPooled
    {
      public readonly Dictionary<ulong, CodeLockWhitelistSnapshot> Locks = new();
      public readonly Dictionary<ulong, int> PlayerReferences = new();
      public PlayerIdSet AuthorizedPlayers;

      public void EnterPool()
      {
        foreach (var snapshot in Locks.Values)
        {
          var pooledSnapshot = snapshot;
          Facepunch.Pool.Free(ref pooledSnapshot);
        }

        Locks.Clear();
        PlayerReferences.Clear();

        if (AuthorizedPlayers is not null)
          Facepunch.Pool.Free(ref AuthorizedPlayers);
      }

      public void LeavePool() =>
        AuthorizedPlayers = Facepunch.Pool.Get<PlayerIdSet>();
    }

    private sealed class DamageScratchSlot
    {
      // Single-owner scratch state for the damage/evaluation path. The
      // evaluator clears this slot before each run to stay allocation-free
      public readonly PlayerIdSet AuthorizedIds =
        new(PlayerIdSet.AuthorizationInitialCapacity);

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void Clear() => AuthorizedIds.Clear();
    }

    private sealed class RelatedPlayerGroupsScratch
    {
      public readonly PlayerIdSet Players =
        new(PlayerIdSet.RelatedPlayersInitialCapacity);
      public readonly HashSet<string> ClanTags =
        new(8, System.StringComparer.Ordinal);
      public readonly HashSet<ulong> TeamIds = new(8);
      public readonly HashSet<long> VanillaClanIds = new(8);

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void Clear()
      {
        Players.Clear();
        ClanTags.Clear();
        TeamIds.Clear();
        VanillaClanIds.Clear();
      }
    }

    private enum TcGriefState : byte
    {
      None,
      ForceTrue,
      ForceFalse
    }

    private sealed class TcCreationData
    {
      [JsonProperty(PropertyName = "Creation time")]
      public long CreatedUtcTicks { get; set; }

      [JsonProperty(PropertyName = "Trusted")]
      public bool HasTrustedCreationTime { get; set; }

      [JsonProperty(PropertyName = "Force grief status")]
      public TcGriefState GriefState { get; set; } = TcGriefState.None;
    }

    private readonly struct TcState
    {
      public readonly BuildingPrivlidge Privilege;
      public readonly ulong CupboardNetworkID;
      public readonly bool IsDecaying;

      public TcState(BuildingPrivlidge privilege, ulong cupboardNetworkID, bool isDecaying)
      {
        Privilege = privilege;
        CupboardNetworkID = cupboardNetworkID;
        IsDecaying = isDecaying;
      }
    }

    private enum DamageDecisionKind : byte
    {
      Allow,
      ApplyScale
    }

    [System.Flags]
    private enum DamageDecisionFlags : byte
    {
      None = 0,
      Vehicle = 1,
      Decaying = 2,
      Grief = 4
    }

    private readonly struct DamageDecision
    {
      // Decision object returned by the damage pipeline so the hot path can
      // branch once after all evaluation has completed
      public readonly ulong TargetID;
      public readonly PlayerScaleCache TargetScaleCache;
      public readonly long PurchasedProtectionEndTicks;
      public readonly float Scale;
      public readonly DamageDecisionKind Kind;
      public readonly bool TaxProtectionGated;
      private readonly DamageDecisionFlags _flags;

      public bool IsVehicle => (_flags & DamageDecisionFlags.Vehicle) is not 0;

      public bool IsDecaying => (_flags & DamageDecisionFlags.Decaying) is not 0;

      public bool IsGrief => (_flags & DamageDecisionFlags.Grief) is not 0;

      public DamageDecision(
        DamageDecisionKind kind, ulong targetID = 0UL, float scale = -1f,
        bool isVehicle = false, bool isDecaying = false, bool isGrief = false,
        PlayerScaleCache targetScaleCache = null,
        long purchasedProtectionEndTicks = 0L,
        bool taxProtectionGated = false)
      {
        TargetID = targetID;
        TargetScaleCache = targetScaleCache;
        PurchasedProtectionEndTicks = purchasedProtectionEndTicks;
        Scale = scale;
        Kind = kind;
        TaxProtectionGated = taxProtectionGated;

        var flags = DamageDecisionFlags.None;
        if (isVehicle)
          flags |= DamageDecisionFlags.Vehicle;

        if (isDecaying)
          flags |= DamageDecisionFlags.Decaying;

        if (isGrief)
          flags |= DamageDecisionFlags.Grief;

        _flags = flags;
      }

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public static DamageDecision Allow(
        ulong targetID = 0UL,
        bool isVehicle = false,
        bool isDecaying = false,
        bool isGrief = false,
        PlayerScaleCache targetScaleCache = null,
        long purchasedProtectionEndTicks = 0L,
        bool taxProtectionGated = false) =>
          new(
            DamageDecisionKind.Allow, targetID, -1f, isVehicle,
            isDecaying, isGrief, targetScaleCache,
            purchasedProtectionEndTicks, taxProtectionGated);
    }

#endregion Classes

#region Data

    private sealed class StoredData
    {
      public Dictionary<ulong, LastOnlineData> LastOnline { get; init; } = new();
      public Dictionary<ulong, TcCreationData> TcCreation { get; init; } = new();
      public Dictionary<ulong, TaxProtectionState> TaxProtection { get; init; } = new();
    }

    private void MarkDataDirty() => _dataDirty = true;

    private void SaveData()
    {
      Interface.Oxide.DataFileSystem.WriteObject(
        $"{Name}/{nameof(StoredData)}",
        new StoredData
        {
          LastOnline = _lastOnline,
          TcCreation = _tcCreationData,
          TaxProtection = _taxProtection
        });
      _dataDirty = false;
    }

    private void Save()
    {
      UpdateLastOnlineAll();
      SaveData();
    }

    private void SaveIfDirty()
    {
      _saveQueued = false;
      if (_dataDirty)
        Save();
    }

    private void LoadData()
    {
      try
      {
        var dataFileName = $"{Name}/{nameof(StoredData)}";
        if (Interface.Oxide.DataFileSystem.ExistsDatafile(dataFileName))
        {
          var data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(dataFileName);
          _lastOnline.ClearAndMergeWith(data.LastOnline);
          _tcCreationData.ClearAndMergeWith(data.TcCreation);
          _taxProtection.ClearAndMergeWith(data.TaxProtection);
        }
        else
          LoadLegacyData();

        NormalizeLastOnlineData();
      }
      catch (System.Exception ex)
      {
        PrintError(ex.ToString());
      }
    }

    private void LoadLegacyData()
    {
      var lastOnlineFileName = $"{Name}/{nameof(LastOnlineData)}";
      var tcCreationFileName = $"{Name}/{nameof(TcCreationData)}";
      var hasLastOnlineData = Interface.Oxide.DataFileSystem.ExistsDatafile(lastOnlineFileName);
      var hasTcCreationData = Interface.Oxide.DataFileSystem.ExistsDatafile(tcCreationFileName);

      switch (hasLastOnlineData)
      {
        case false when !hasTcCreationData:
          return;
        case true:
          {
            var lastOnline =
              Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, LastOnlineData>>(
                lastOnlineFileName);
            _lastOnline.ClearAndMergeWith(lastOnline);
            break;
          }
      }

      if (hasTcCreationData)
      {
        var tcCreation =
          Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, TcCreationData>>(
            tcCreationFileName);
        _tcCreationData.ClearAndMergeWith(tcCreation);
      }

      SaveData();

      if (hasLastOnlineData)
        Interface.Oxide.DataFileSystem.DeleteDataFile(lastOnlineFileName);
      if (hasTcCreationData)
        Interface.Oxide.DataFileSystem.DeleteDataFile(tcCreationFileName);
    }

    private void NormalizeLastOnlineData()
    {
      _tmpIdsScratch.Clear();
      var requiresSave = false;
      foreach (var (userID, lastOnline) in _lastOnline)
      {
        if (!userID.IsSteamID() || lastOnline is null)
        {
          _tmpIdsScratch.Add(userID);
          continue;
        }

        try
        {
          lastOnline.RefreshRuntimeTicks();
        }
        catch (System.ArgumentException)
        {
          _tmpIdsScratch.Add(userID);
          continue;
        }

        if (lastOnline.UserID != userID)
        {
          lastOnline.UserID = userID;
          requiresSave = true;
        }

        if (lastOnline.PenaltyEndTicks < 0L ||
            lastOnline.PenaltyEndTicks > System.DateTime.MaxValue.Ticks)
        {
          lastOnline.DisablePenalty();
          requiresSave = true;
        }
      }

      if (_tmpIdsScratch.Count is not 0)
      {
        foreach (var userID in _tmpIdsScratch)
          _lastOnline.Remove(userID);
        requiresSave = true;
      }

      if (requiresSave)
        MarkDataDirty();
      _tmpIdsScratch.Clear();
    }

    private void RecordCupboardCreation(BuildingPrivlidge buildingPrivlidge)
    {
      var cupboardNetworkID = GetNetworkID(buildingPrivlidge);
      if (cupboardNetworkID is 0UL)
        return;

      if (!_tcCreationData.TryGetValue(cupboardNetworkID, out var creationData))
      {
        creationData = new();
        _tcCreationData[cupboardNetworkID] = creationData;
      }

      if (creationData.HasTrustedCreationTime)
        return;

      creationData.CreatedUtcTicks = System.DateTime.UtcNow.Ticks;
      creationData.HasTrustedCreationTime = true;
      MarkDataDirty();
    }

    private void EnsureCupboardCreationData(ulong cupboardNetworkID)
    {
      if (cupboardNetworkID is 0UL ||
          _tcCreationData.TryGetValue(cupboardNetworkID, out _))
        return;

      // A TC found after plugin loading has no trustworthy build timestamp
      // It can establish that a later TC is griefing it, but can never itself
      // lose protection solely because of this fallback observation
      _tcCreationData[cupboardNetworkID] = new();
      MarkDataDirty();
    }

    private void RemoveCupboardCreationData(ulong cupboardNetworkID)
    {
      if (cupboardNetworkID is not 0UL &&
          _tcCreationData.Remove(cupboardNetworkID))
        MarkDataDirty();
    }

#endregion Data

#region Config

    protected override void LoadDefaultConfig()
    {
      Configuration = GetBaseConfig(Version);
      SetTimeZone();
      CacheTaxProtectionLimits();
      CacheTaxOverlayEnabled();
    }

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

        var baseConfig = GetBaseConfig(Version);
        NormalizeConfig(baseConfig);
        Config.WriteObject(Configuration, true);

        SetTimeZone();
        CacheTaxProtectionLimits();
        CacheTaxOverlayEnabled();
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

      if (Configuration.Version < new VersionNumber(1, 4, 0))
      {
        Configuration.ApartmentProtection =
          baseConfig.ApartmentProtection;
      }

      if (Configuration.Version < new VersionNumber(1, 5, 0))
      {
        Configuration.RaidProtection.ProtectGriefTcs =
          baseConfig.RaidProtection.ProtectGriefTcs;

        Configuration.Command.CommandTestGrief =
          baseConfig.Command.CommandTestGrief;

        Configuration.Other.GameTipWeaponCategories =
          baseConfig.Other.GameTipWeaponCategories;
      }

      if (Configuration.Version < new VersionNumber(1, 6, 0))
      {
        Configuration.Command.CommandScheduledTimescales =
          baseConfig.Command.CommandScheduledTimescales;

        Configuration.RaidProtection.EnableScheduledTimescales =
          baseConfig.RaidProtection.EnableScheduledTimescales;

        Configuration.StatusHud = baseConfig.StatusHud;
        Configuration.MapMarker = baseConfig.MapMarker;
      }

      if (Configuration.Version < new VersionNumber(1, 7, 0))
      {
        Configuration.TaxProtection = baseConfig.TaxProtection;
        Configuration.Permission.TaxProtection = baseConfig.Permission.TaxProtection;
        Configuration.Command.CommandTaxProtection = baseConfig.Command.CommandTaxProtection;
      }

      if (Configuration.Version < new VersionNumber(1, 7, 1))
        Configuration.Permission.OnlineProtect = baseConfig.Permission.OnlineProtect;

      if (Configuration.Version < new VersionNumber(1, 7, 2))
        Configuration.TaxProtection.TaxOverlay = baseConfig.TaxProtection.TaxOverlay;

      if (Configuration.Version < new VersionNumber(1, 7, 3))
        Configuration.Command.CommandOrpDdraw = baseConfig.Command.CommandOrpDdraw;

      if (Configuration.Version < new VersionNumber(1, 8, 0))
      {
        Configuration.RaidProtection.ProtectDecayingModularBoats =
          baseConfig.RaidProtection.ProtectDecayingModularBoats;
        Configuration.TaxProtection.EnableForModularBoats =
          baseConfig.TaxProtection.EnableForModularBoats;
        Configuration.TaxProtection.GroupSizeCostScaling =
          baseConfig.TaxProtection.GroupSizeCostScaling;
      }

      Configuration.Version = Version;

      SaveConfig();
      PrintWarning("Config update has been completed!");
    }

    private void SetTimeZone()
    {
      _timeZone = System.TimeZoneInfo.Utc;
      var id =
#if CARBON
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
          Configuration.TimeZone.WinTimeZone :
          Configuration.TimeZone.UnixTimeZone;
#else
        Configuration.TimeZone.TimeZone;
#endif
      if (string.IsNullOrWhiteSpace(id))
        return;

      try
      {
        _timeZone = System.TimeZoneInfo.FindSystemTimeZoneById(id);
      }
      catch (System.TimeZoneNotFoundException)
      {
        PrintWarning($"Timezone '{id}' was not found; using UTC.");
      }
      catch (System.InvalidTimeZoneException)
      {
        PrintWarning($"Timezone '{id}' is invalid; using UTC.");
      }
    }

    private static ConfigData GetBaseConfig(VersionNumber version) => new()
    {
      RaidProtection = new()
      {
        OnlyPlayerDamage = false,
        OnlineRaidProtection = false,
        EnableScheduledTimescales = false,
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
        ProtectDecayingModularBoats = true,
        ProtectGriefTcs = true,
        Prefabs = GetPrefabNames(),
        PrefabsBlacklist = new(),
        PrefabProtectionMultipliers = new()
      },
      TaxProtection = new()
      {
        Enabled = false,
        EnableForModularBoats = false,
        CurrencyItemID = -932201673,
        CostPerHour = 100,
        GroupSizeCostScaling = new()
        {
          Enabled = false,
          BaseCostPlayerCount = 4,
          SmallGroupPlayerCount = 6,
          SmallGroupIncreasePercent = 2m,
          LargeGroupIncreasePercent = 4m,
          MaximumCostMultiplier = 3m
        },
        RefundOnDestruction = true,
        MaxCurrencyReserves = 1000,
        MaxPurchaseHours = 48,
        TaxOverlay = new()
        {
          Enabled = false,
          AnchorMin = "0.5 0.5",
          AnchorMax = "0.5 0.5",
          OffsetMin = "-130 35",
          OffsetMax = "130 180"
        }
      },
      ApartmentProtection = new()
      {
        ProtectApartments = false,
        WhenAbsent = false,
        WhenRentDue = false,
        ProtectShops = false,
        WhenDamageBelow = 1f,
        DamageAsChance = false
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
        CommandTestGrief = "orp.test.grief",
        CommandScheduledTimescales = "orp.schedule",
        CommandUpdatePrefabList = "orp.update.prefabs",
        CommandDumpPrefabList = "orp.dump.prefabs",
        CommandTaxProtection = "orp.tax",
        CommandOrpDdraw = "orp.ddraw",
#if CARBON
        CommandCooldown = 1
#endif
      },
      Permission = new()
      {
        Protect = "offlineraidprotection.protect",
        Check = "offlineraidprotection.check",
        Admin = "offlineraidprotection.admin",
        TaxProtection = "offlineraidprotection.tax",
        OnlineProtect = "offlineraidprotection.onlineprotect"
      },
      Other = new()
      {
        PlaySound = false,
        SoundPath = "assets/prefabs/locks/keypad/effects/lock.code.denied.prefab",
        ShowMessage = true,
        GameTipWeaponCategories = new()
        {
          GameTipWeaponCategory.Melee,
          GameTipWeaponCategory.Ranged,
          GameTipWeaponCategory.Explosive
        },
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
      StatusHud = new()
      {
        Enabled = false,
        AnchorMin = "1 1",
        AnchorMax = "1 1",
        OffsetMin = "-200 -70",
        OffsetMax = "-20 -20",
        RefreshInterval = 3f,
        DisplayInTrustedPrivilege = false,
        DisplayOnStatusCommand = false,
        DisplayOnlyWhenProtectionActive = true,
        Duration = 5f,
        ShowProtectionPercentage = true,
        ShowRemainingTime = true,
        ShowPenaltyTimer = true
      },
      MapMarker = new()
      {
        Enabled = false,
        RefreshInterval = 30f,
        EnableBoatLiveCircle = false,
        Radius = 1f,
        Alpha = 0.35f,
        ProtectedColor = COLOR_RED,
        PartialColor = COLOR_ORANGE,
        VulnerableColor = COLOR_GREEN,
        DecayingColor = COLOR_WHITE,
        GriefColor = COLOR_YELLOW,
        OutlineColor = "#000000",
        TooltipMaxPlayers = 8
      },
      Version = version
    };

    private static HashSet<string> GetPrefabNames()
    {
      var prefabNames = new HashSet<string>();
      foreach (var itemDef in ItemManager.GetItemDefinitions())
      {
        var shortName = GetShortName(
          itemDef?.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath);
        if (!string.IsNullOrEmpty(shortName))
          prefabNames.Add(shortName);
      }

      var manifest = GameManifest.Current;
      if (!manifest)
        return prefabNames;
      foreach (var entity in manifest.entities)
      {
        if (string.IsNullOrEmpty(entity))
          continue;

        var shortName =
          GameManager.server.FindPrefab(entity.ToLowerInvariant())?
          .GetComponent<BaseVehicle>()?.ShortPrefabName;

        if (!string.IsNullOrEmpty(shortName))
          prefabNames.Add(shortName);
      }

      return prefabNames;
    }

    private static void NormalizeConfig(ConfigData baseConfig)
    {
      Configuration.RaidProtection ??= baseConfig.RaidProtection;
      Configuration.ApartmentProtection ??= baseConfig.ApartmentProtection;
      Configuration.Team ??= baseConfig.Team;
      Configuration.Command ??= baseConfig.Command;
      Configuration.Permission ??= baseConfig.Permission;
      Configuration.Other ??= baseConfig.Other;
      Configuration.TimeZone ??= baseConfig.TimeZone;
      Configuration.StatusHud ??= baseConfig.StatusHud;
      Configuration.MapMarker ??= baseConfig.MapMarker;
      Configuration.TaxProtection ??= baseConfig.TaxProtection;
      Configuration.TaxProtection.GroupSizeCostScaling ??=
        baseConfig.TaxProtection.GroupSizeCostScaling;
      Configuration.TaxProtection.TaxOverlay ??= baseConfig.TaxProtection.TaxOverlay;
      var raidProtection = Configuration.RaidProtection;
      raidProtection.AbsoluteTimeScale = NormalizeAbsoluteTimeScale(
        raidProtection.AbsoluteTimeScale);
      raidProtection.DamageScale = NormalizeDamageScale(
        raidProtection.DamageScale);
      raidProtection.CooldownMinutes = System.Math.Max(
        0, raidProtection.CooldownMinutes);
      raidProtection.CooldownQualifyMinutes = System.Math.Max(
        0, raidProtection.CooldownQualifyMinutes);
      raidProtection.InterimDamage = NormalizeFinite(
        raidProtection.InterimDamage, baseConfig.RaidProtection.InterimDamage,
        0f, float.MaxValue);
      raidProtection.PrefabProtectionMultipliers =
        NormalizePrefabProtectionMultipliers(
          raidProtection.PrefabProtectionMultipliers);

      var taxProtection = Configuration.TaxProtection;
      taxProtection.CostPerHour = System.Math.Max(0, taxProtection.CostPerHour);
      NormalizeGroupSizeCostScaling(
        taxProtection.GroupSizeCostScaling, taxProtection.CostPerHour);

      var apartmentProtection = Configuration.ApartmentProtection;
      apartmentProtection.WhenDamageBelow = NormalizeFinite(
        apartmentProtection.WhenDamageBelow,
        baseConfig.ApartmentProtection.WhenDamageBelow,
        0f, float.MaxValue);

      Configuration.Team.TeamPenaltyDuration = NormalizeFinite(
        Configuration.Team.TeamPenaltyDuration,
        baseConfig.Team.TeamPenaltyDuration, 0f, float.MaxValue);
      Configuration.Other.MessageDuration = NormalizeFinite(
        Configuration.Other.MessageDuration,
        baseConfig.Other.MessageDuration, 0f, float.MaxValue);

      var hud = Configuration.StatusHud;
      hud.RefreshInterval = NormalizeFinite(
        hud.RefreshInterval, baseConfig.StatusHud.RefreshInterval, 0.5f, 60f);
      hud.Duration = NormalizeFinite(
        hud.Duration,
        baseConfig.StatusHud.Duration, 0.5f, 60f);
      var hudAnchorMin = hud.AnchorMin;
      var hudAnchorMax = hud.AnchorMax;
      var hudOffsetMin = hud.OffsetMin;
      var hudOffsetMax = hud.OffsetMax;
      ValidatePointAnchoredBounds(
        ref hudAnchorMin, ref hudAnchorMax, ref hudOffsetMin, ref hudOffsetMax,
        baseConfig.StatusHud.AnchorMin, baseConfig.StatusHud.AnchorMax,
        baseConfig.StatusHud.OffsetMin, baseConfig.StatusHud.OffsetMax);
      hud.AnchorMin = hudAnchorMin;
      hud.AnchorMax = hudAnchorMax;
      hud.OffsetMin = hudOffsetMin;
      hud.OffsetMax = hudOffsetMax;

      var taxOverlay = Configuration.TaxProtection.TaxOverlay;
      var taxAnchorMin = taxOverlay.AnchorMin;
      var taxAnchorMax = taxOverlay.AnchorMax;
      var taxOffsetMin = taxOverlay.OffsetMin;
      var taxOffsetMax = taxOverlay.OffsetMax;
      ValidatePointAnchoredBounds(
        ref taxAnchorMin, ref taxAnchorMax, ref taxOffsetMin, ref taxOffsetMax,
        baseConfig.TaxProtection.TaxOverlay.AnchorMin,
        baseConfig.TaxProtection.TaxOverlay.AnchorMax,
        baseConfig.TaxProtection.TaxOverlay.OffsetMin,
        baseConfig.TaxProtection.TaxOverlay.OffsetMax);
      taxOverlay.AnchorMin = taxAnchorMin;
      taxOverlay.AnchorMax = taxAnchorMax;
      taxOverlay.OffsetMin = taxOffsetMin;
      taxOverlay.OffsetMax = taxOffsetMax;

      var marker = Configuration.MapMarker;
      marker.RefreshInterval = NormalizeFinite(
        marker.RefreshInterval,
        baseConfig.MapMarker.RefreshInterval, 1f, 600f);
      marker.Radius = NormalizeFinite(
        marker.Radius, baseConfig.MapMarker.Radius, 0.1f, 200f);
      marker.Alpha = NormalizeFinite(
        marker.Alpha, baseConfig.MapMarker.Alpha, 0f, 1f);
      marker.TooltipMaxPlayers = System.Math.Max(0, marker.TooltipMaxPlayers);

      marker.ProtectedColor = NormalizeColor(
        marker.ProtectedColor, baseConfig.MapMarker.ProtectedColor);
      marker.PartialColor = NormalizeColor(
        marker.PartialColor, baseConfig.MapMarker.PartialColor);
      marker.VulnerableColor = NormalizeColor(
        marker.VulnerableColor, baseConfig.MapMarker.VulnerableColor);
      marker.DecayingColor = NormalizeColor(
        marker.DecayingColor, baseConfig.MapMarker.DecayingColor);
      marker.GriefColor = NormalizeColor(
        marker.GriefColor, baseConfig.MapMarker.GriefColor);
      marker.OutlineColor = NormalizeColor(
        marker.OutlineColor, baseConfig.MapMarker.OutlineColor);
    }

    private static float NormalizeFinite(
      float value, float fallback, float minimum, float maximum) =>
      float.IsNaN(value) || float.IsInfinity(value) ||
      value < minimum || value > maximum
        ? fallback
        : value;

    private static Dictionary<int, float> NormalizeAbsoluteTimeScale(
      Dictionary<int, float> scales)
    {
      var normalized = new Dictionary<int, float>();
      if (scales is null)
        return normalized;

      foreach (var (hour, scale) in scales)
      {
        if (hour is >= 0 and <= 23 && IsFinite(scale))
          normalized[hour] = scale;
      }

      return normalized;
    }

    private static Dictionary<float, float> NormalizeDamageScale(
      Dictionary<float, float> scales)
    {
      var normalized = new Dictionary<float, float>();
      if (scales is null)
        return normalized;

      foreach (var (hours, scale) in scales)
      {
        if (IsFinite(hours) && hours >= 0f && IsFinite(scale))
          normalized[hours] = scale;
      }

      return normalized;
    }

    private static void NormalizeGroupSizeCostScaling(
      ConfigData.TaxProtectionOptions.GroupSizeCostScalingOptions scaling,
      int baseCost)
    {
      scaling.BaseCostPlayerCount = System.Math.Max(
        0, scaling.BaseCostPlayerCount);
      scaling.SmallGroupPlayerCount = System.Math.Max(
        0, scaling.SmallGroupPlayerCount);
      if (baseCost <= 0)
        return;

      var maximumMultiplier = (decimal)int.MaxValue / baseCost;
      scaling.MaximumCostMultiplier = System.Math.Max(
        1m, System.Math.Min(scaling.MaximumCostMultiplier, maximumMultiplier));

      var maximumIncreasePercent =
        (scaling.MaximumCostMultiplier - 1m) * 100m;
      scaling.SmallGroupIncreasePercent = System.Math.Max(0m,
        System.Math.Min(scaling.SmallGroupIncreasePercent, maximumIncreasePercent));
      scaling.LargeGroupIncreasePercent = System.Math.Max(0m,
        System.Math.Min(scaling.LargeGroupIncreasePercent, maximumIncreasePercent));
    }

    private static Dictionary<string, float> NormalizePrefabProtectionMultipliers(
      Dictionary<string, float> multipliers)
    {
      if (multipliers is null || multipliers.Count is 0)
        return new();

      var normalized = new Dictionary<string, float>(multipliers.Count);
      foreach (var (shortName, multiplier) in multipliers)
      {
        if (!string.IsNullOrEmpty(shortName) &&
            !float.IsNaN(multiplier) && !float.IsInfinity(multiplier) &&
            multiplier is >= 0f and <= 1f)
          normalized[shortName] = multiplier;
      }

      return normalized;
    }

    private static bool TryParseAnchor(
      string value, out float x, out float y)
    {
      x = 0f;
      y = 0f;
      if (string.IsNullOrWhiteSpace(value))
        return false;

      var separator = value.IndexOf(' ');
      System.ReadOnlySpan<char> span = value;
      return separator > 0 && separator < value.Length - 1 &&
             float.TryParse(
               span[..separator],
               NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
             float.TryParse(
               span[(separator + 1)..],
               NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
             !float.IsNaN(x) && !float.IsInfinity(x) &&
             !float.IsNaN(y) && !float.IsInfinity(y) &&
             x is >= 0f and <= 1f && y is >= 0f and <= 1f;
    }

    private static bool TryParseOffset(
      string value, out float x, out float y)
    {
      x = 0f;
      y = 0f;
      if (string.IsNullOrWhiteSpace(value))
        return false;

      var separator = value.IndexOf(' ');
      System.ReadOnlySpan<char> span = value;
      return separator > 0 && separator < value.Length - 1 &&
             float.TryParse(
               span[..separator],
               NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
             float.TryParse(
               span[(separator + 1)..],
               NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
             !float.IsNaN(x) && !float.IsInfinity(x) &&
             !float.IsNaN(y) && !float.IsInfinity(y);
    }

    private static bool HasMinimumPointAnchoredBounds(
      float minAnchorX, float minAnchorY, float maxAnchorX, float maxAnchorY,
      float minOffsetX, float minOffsetY, float maxOffsetX, float maxOffsetY) =>
      (minAnchorX != maxAnchorX || maxOffsetX - minOffsetX > 10f) &&
      (minAnchorY != maxAnchorY || maxOffsetY - minOffsetY > 10f);

    private static void ValidatePointAnchoredBounds(
      ref string anchorMin, ref string anchorMax,
      ref string offsetMin, ref string offsetMax,
      string defaultAnchorMin, string defaultAnchorMax,
      string defaultOffsetMin, string defaultOffsetMax)
    {
      var minAnchorX = 0f;
      var minAnchorY = 0f;
      var maxAnchorX = 0f;
      var maxAnchorY = 0f;
      var hasValidAnchors =
        TryParseAnchor(anchorMin, out minAnchorX, out minAnchorY) &&
        TryParseAnchor(anchorMax, out maxAnchorX, out maxAnchorY) &&
        minAnchorX <= maxAnchorX && minAnchorY <= maxAnchorY;
      if (!hasValidAnchors)
      {
        anchorMin = defaultAnchorMin;
        anchorMax = defaultAnchorMax;
        hasValidAnchors =
          TryParseAnchor(anchorMin, out minAnchorX, out minAnchorY) &&
          TryParseAnchor(anchorMax, out maxAnchorX, out maxAnchorY) &&
          minAnchorX <= maxAnchorX && minAnchorY <= maxAnchorY;
      }

      if (!hasValidAnchors ||
          !TryParseOffset(offsetMin, out var minOffsetX, out var minOffsetY) ||
          !TryParseOffset(offsetMax, out var maxOffsetX, out var maxOffsetY) ||
          !HasMinimumPointAnchoredBounds(
            minAnchorX, minAnchorY, maxAnchorX, maxAnchorY,
            minOffsetX, minOffsetY, maxOffsetX, maxOffsetY))
      {
        offsetMin = defaultOffsetMin;
        offsetMax = defaultOffsetMax;
      }
    }

    private static string NormalizeColor(string value, string fallback) =>
      ColorUtility.TryParseHtmlString(value, out _) ? value : fallback;

#endregion Config

#region Hooks

    private void Loaded()
    {
      Instance = this;

      CacheMapMarkerColors();
      CacheDdrawColors();

      // fixes possible null path during server boot
      CacheDefaultTimescales();

      Configuration.Permission.RegisterPermissions(permission, this);
      Configuration.Command.RegisterCommands(this, this);

      LoadData();
      UnsubscribeHooks();
    }

    private void OnServerInitialized()
    {
      ClearCodeLockWhitelistCache();
      CacheData();
      CacheTaxProtectionCurrency();

      _serverInitialized = true;
      InitializeStatusHud();
      InitializeMapMarkers();
      InitializeScheduledTimescales();
      InitializeTaxProtection();
    }

    private void OnNewSave(string _filename)
    {
      ClearDdrawWorldState();
      _lastOnline.Clear();
      _tcCache.Clear();
      ClearCodeLockWhitelistCache();
      _tcCreationData.Clear();
      ClearGriefCupboardIndex();
      ClearQueuedTcCacheRefreshes();
      _taxProtection.Clear();
      _taxProtectionSyncBuildingIds.Clear();
      ClearTaxProtectionRefunds();
      RemoveAllMapMarkers();
      MaterializeQueuedWipeTemplate(System.DateTime.UtcNow.Ticks);
      MarkDataDirty();
      SaveData();
    }

    private void OnServerSave()
    {
      if (!_dataDirty || _saveQueued)
        return;

      _saveQueued = true;
      ServerMgr.Instance.Invoke(SaveIfDirty, 1f);
    }

    private void Unload()
    {
      _serverInitialized = false;
      try
      {
        PauseActivePurchasedProtection(System.DateTime.UtcNow);
        Save();
      }
      finally
      {
        UnloadResources();
      }
    }

    private void UnloadResources()
    {
      _dataDirty = false;
      _saveQueued = false;

      UnloadScheduledTimescales();
      UnloadStatusHud();
      UnloadMapMarkers();
      UnloadTaxProtection();
      UnloadTaxOverlay();
      UnloadDdraw();

      Configuration = null;
      Instance = null;
      Clans = null;

      _prefabProtection.Clear();
      _prefabProtectionMultipliers.Clear();
      _scaleCache.Clear();
      _lastOnline.Clear();
      _tcCache.Clear();
      ClearCodeLockWhitelistCache();
      _queuedSpawnedCodeLocks.Clear();
      _spawnedCodeLocksQueued = false;
      _processQueuedSpawnedCodeLocksAction = null;
      ClearQueuedTcCacheRefreshes();
      _tcCacheRefreshQueued = false;
      _processQueuedTcCacheRefreshesAction = null;
      _tcCreationData.Clear();
      _taxProtection.Clear();
      _taxProtectionCurrencyDefinition = null;
      _taxProtectionCurrencyName = string.Empty;
      ClearGriefCupboardIndex();
      _damageScratch.Clear();
      _relatedPlayerGroupsScratch.Clear();
      _teamMembersScratch.Clear();
      _tmpIdsScratch.Clear();
      _tmpIdSetScratch.Clear();
      _adminIDCache.Clear();
      _sb.Clear();
      System.Array.Clear(
        _gameTipArgs, 0, _gameTipArgs.Length);
      _timeZone = null;
      _ray = default;
      System.Array.Clear(RaycastHits, 0, RaycastHits.Length);

      FreeAllClanPoolLists();
      _clanMemberCache.Clear();
      _clanTagCache.Clear();

      _players.Clear();
    }

    private void OnPluginLoaded(Plugin plugin)
    {
      if (plugin.Name is not nameof(Clans))
        return;

      Clans = plugin;
      CacheAllClans();
    }

    private void OnPluginUnloaded(Plugin plugin)
    {
      if (plugin.Name is not nameof(Clans))
        return;

      Clans = null;
      FreeAllClanPoolLists();
      _clanMemberCache.Clear();
      _clanTagCache.Clear();
    }

    private void OnPlayerConnected(BasePlayer player)
    {
      if (!player)
        return;

      var currentTime = System.DateTime.UtcNow;
      UpdateLastOnline(player, currentTime);
      UpdateLastConnect(player, currentTime);

      _players.AddPlayer(player);
      CacheAdmin(player);

      var playerID = player.userID.Get();
      var scaleCache = GetOrCreateScaleCache(
        playerID, userIDText: player.UserIDString);
      RefreshPlayerPermissionState(scaleCache);
      scaleCache.ExpiresDT = System.DateTime.MinValue;
      scaleCache.CacheMessages(this);

      if (!Configuration.StatusHud.Enabled)
      {
        QueueMapMarkerRefresh(
          _adminIDCache.Contains(playerID));
        return;
      }

      if (!_hudStates.TryGetValue(playerID, out var hudState))
      {
        hudState = Facepunch.Pool.Get<HudPlayerState>();
        _hudStates[playerID] = hudState;
      }
      hudState.PrivilegeRefreshAt =
        UnityEngine.Time.realtimeSinceStartup +
        UnityEngine.Random.Range(
          0f, Configuration.StatusHud.RefreshInterval);
      ScheduleStatusHudRefresh(playerID, hudState);

      QueueStatusHudRefresh(player);
      QueueMapMarkerRefresh(
        _adminIDCache.Contains(playerID));
    }

    private void OnPlayerDisconnected(BasePlayer player)
    {
      if (!player)
        return;

#if CARBON && !MINIMAL
      CloseScheduledTimescaleEditor(player);
#endif
      var playerID = player.userID.Get();
      CloseTaxOverlay(player, playerID);

      if (_hudStates.TryGetValue(playerID, out var hudState))
        RemoveStatusHud(player, playerID, hudState);
      else
        HideStatusHud(player);

      var currentTime = System.DateTime.UtcNow;
      UpdateLastOnline(player, currentTime);
      if (_scaleCache.TryGetValue(playerID, out var scaleCache))
        scaleCache.ExpiresDT = System.DateTime.MinValue;

      _players.AddPlayer(player);

      QueueMapMarkerRefresh();
      EndDdrawSession(playerID);
      _adminIDCache.Remove(playerID);
    }

    private void OnUserPermissionGranted(string id, string permissionName) =>
      RefreshUserPermissionState(id, permissionName);

    private void OnUserPermissionRevoked(string id, string permissionName) =>
      RefreshUserPermissionState(id, permissionName);

    private void OnUserGroupAdded(string id, string _groupName) =>
      RefreshUserPermissionState(id);

    private void OnUserGroupRemoved(string id, string _groupName) =>
      RefreshUserPermissionState(id);

    private void OnGroupPermissionGranted(
      string _groupName, string permissionName) =>
      RefreshGroupPermissionState(permissionName);

    private void OnGroupPermissionRevoked(
      string _groupName, string permissionName) =>
      RefreshGroupPermissionState(permissionName);

    private void OnUserNameUpdated(string id, string _oldName, string newName)
    {
      var userID = ulong.Parse(id);
      _players.UpdateName(userID, _oldName, newName);
      InvalidateMapMarkerAuthorizedPlayers();

      if (!_lastOnline.TryGetValue(userID, out var lastOnline))
        return;

      lastOnline.UserName = newName;
      MarkDataDirty();
    }

    private object CanNetworkTo(
      MapMarkerGenericRadius entity, BasePlayer target)
    {
      if (!Configuration.MapMarker.Enabled || !entity || !target)
        return null;

      var markerRadiusNetworkID = GetNetworkID(entity);
      if (!_pendingMapMarkers.Contains(entity) &&
          (markerRadiusNetworkID is 0UL ||
           !_activeMarkerNetIds.Contains(markerRadiusNetworkID)))
        return null;

      return _adminIDCache.Contains(target.userID.Get()) ?
        null : BoxedFalse;
    }

    private object CanNetworkTo(
      VendingMachineMapMarker entity, BasePlayer target)
    {
      if (!Configuration.MapMarker.Enabled || !entity || !target)
        return null;

      var markerVendingNetworkID = GetNetworkID(entity);
      if (!_pendingMapMarkers.Contains(entity) &&
          (markerVendingNetworkID is 0UL ||
           !_activeMarkerNetIds.Contains(markerVendingNetworkID)))
        return null;

      return _adminIDCache.Contains(target.userID.Get()) ?
        null : BoxedFalse;
    }

    private void OnCupboardProtectionCalculated(
      BuildingPrivlidge buildingPrivlidge, float cachedProtectedMinutes)
    {
      if (!buildingPrivlidge || buildingPrivlidge.buildingID is 0U)
        return;

      var cupboardNetworkID = GetNetworkID(buildingPrivlidge);
      if (!Configuration.RaidProtection.ProtectGriefTcs)
        EnsureCupboardCreationData(cupboardNetworkID);

      _tcCache[buildingPrivlidge.buildingID] =
        new TcState(
          buildingPrivlidge,
          cupboardNetworkID,
          IsCachedCupboardDecaying(
            buildingPrivlidge, cachedProtectedMinutes > 0));

      if (Configuration.TaxProtection.Enabled)
        SyncPurchasedProtection(
          buildingPrivlidge, cupboardNetworkID, System.DateTime.UtcNow);

      if (Configuration.MapMarker.Enabled)
      {
        QueueBuildingMapMarkerSync(buildingPrivlidge.buildingID);
        QueueModularBoatMapMarkerSync(buildingPrivlidge);
      }
    }

    private void OnCupboardAuthorize(
      BuildingPrivlidge buildingPrivlidge, BasePlayer player) =>
      RefreshCupboardAuthorizationViews(buildingPrivlidge, player);

    private void OnCupboardAssign(
      BuildingPrivlidge buildingPrivlidge, ulong _userID,
      BasePlayer _player)
    {
      if (buildingPrivlidge)
        QueueGriefTopologyRefresh(buildingPrivlidge.buildingID);
      InvalidateMapMarkerAuthorizedPlayers();
      UpdateTcMarkerLabel(buildingPrivlidge);
    }

    private void OnCupboardDeauthorize(
      BuildingPrivlidge buildingPrivlidge, BasePlayer player) =>
      RefreshCupboardAuthorizationViews(buildingPrivlidge, player);

    private void OnCupboardClearList(
      BuildingPrivlidge buildingPrivlidge, BasePlayer player) =>
      RefreshCupboardAuthorizationViews(buildingPrivlidge, player);

    private void OnEntitySpawned(Tugboat tugboat)
    {
      TrackDdrawBoat(tugboat);
      QueueBoatMapMarkerSync(tugboat);
    }

    private void OnEntitySpawned(PlayerBoat modularBoat)
    {
      TrackDdrawBoat(modularBoat);
      QueueBoatMapMarkerSync(modularBoat);
    }

    private void OnEntitySpawned(BuildingPrivlidge buildingPrivlidge)
    {
      if (!buildingPrivlidge)
        return;

      if (ShouldMaintainCupboardCreationData)
        RecordCupboardCreation(buildingPrivlidge);

      QueueTcCacheRefresh(buildingPrivlidge.buildingID);
    }

    private void OnEntitySpawned(CodeLock codeLock)
    {
      if (!Configuration.Team.IncludeWhitelistPlayers || !codeLock)
        return;

      _queuedSpawnedCodeLocks.Add(codeLock);
      if (_spawnedCodeLocksQueued)
        return;

      _spawnedCodeLocksQueued = true;
      _processQueuedSpawnedCodeLocksAction ??= ProcessQueuedSpawnedCodeLocks;
      NextFrame(_processQueuedSpawnedCodeLocksAction);
    }

    private void OnEntityKill(BaseEntity entity)
    {
      if (entity is DroppedItemContainer &&
          _taxProtectionRefundPouches.Remove(
            GetNetworkID(entity), out var pouchRefund))
      {
        Facepunch.Pool.Free(ref pouchRefund);
        if (_taxProtectionRefundPouches.Count is 0)
          Unsubscribe(nameof(CanLootEntity));
        return;
      }

      if (Configuration.Team.IncludeWhitelistPlayers)
      {
        var codeLock = entity as CodeLock ??
          entity.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
        if (codeLock)
        {
          if (!RemoveTrackedCodeLock(codeLock))
            QueueBoatMapMarkerAuthorizationRefresh(codeLock);
        }
      }

      if (entity is BuildingPrivlidge buildingPrivlidge)
      {
        QueueModularBoatMapMarkerSync(buildingPrivlidge);
        var cupboardNetworkID = GetNetworkID(entity);
        CloseTaxOverlayViewers(cupboardNetworkID);
        if (cupboardNetworkID is not 0UL)
        {
          _ddrawLabelCache.Remove(cupboardNetworkID);

          if (ShouldMaintainCupboardCreationData)
            RemoveCupboardCreationData(cupboardNetworkID);

          if (_pendingTaxProtectionRefunds.Remove(
                cupboardNetworkID, out var refund))
          {
            if (Configuration.TaxProtection.RefundOnDestruction)
              QueueTaxProtectionRefund(refund);
            else
              Facepunch.Pool.Free(ref refund);
          }
          if (_taxProtection.Remove(cupboardNetworkID))
            MarkDataDirty();
          RemoveMapMarker(cupboardNetworkID);
        }

        _taxProtectionSyncBuildingIds.Remove(buildingPrivlidge.buildingID);
        QueueTcCacheRefresh(buildingPrivlidge.buildingID);
        return;
      }

      switch (entity)
      {
        case BaseVehicle boat and (Tugboat or PlayerBoat):
          RemoveDdrawLabelCacheEntry(boat);
          UntrackDdrawBoat(boat);
          RemoveBoatMapMarker(boat);
          break;
        case VehiclePrivilege { ParentVehicle: Tugboat or PlayerBoat } vehiclePrivilege:
          RemoveDdrawLabelCacheEntry(vehiclePrivilege.ParentVehicle);
          RemoveMapMarker(GetNetworkID(vehiclePrivilege));
          break;
      }
    }

    private void OnCodeEntered(CodeLock codeLock, BasePlayer _player, string _code)
      => RefreshCodeLockWhitelistAndMapMarker(codeLock);

    private void OnCodeChanged(
      BasePlayer _player, CodeLock codeLock, string _code, bool _isGuestCode)
      => RefreshCodeLockWhitelistAndMapMarker(codeLock);

    private void OnBuildingSplit(
      BuildingManager.Building oldBuilding, uint newBuildingId)
    {
      var oldBuildingID = oldBuilding?.ID ?? 0U;
      RemoveCodeLockWhitelistCache(oldBuildingID);
      RemoveCodeLockWhitelistCache(newBuildingId);
      InvalidateMapMarkerAuthorizedPlayers();
      QueueTcCacheRefresh(oldBuildingID);
      QueueTcCacheRefresh(newBuildingId);
    }

    private void OnBuildingMerge(
      ServerBuildingManager _serverBuildingManager,
      BuildingManager.Building toBuilding,
      BuildingManager.Building fromBuilding)
    {
      var toBuildingID = toBuilding?.ID ?? 0U;
      var fromBuildingID = fromBuilding?.ID ?? 0U;
      RemoveCodeLockWhitelistCache(toBuildingID);
      RemoveCodeLockWhitelistCache(fromBuildingID);
      InvalidateMapMarkerAuthorizedPlayers();
      QueueTcCacheRefresh(toBuildingID);
      QueueTcCacheRefresh(fromBuildingID);
    }

    // provide feedback when a player knocks on a protected apartment door
    private void OnDoorKnocked(ApartmentDoor apartmentDoor, BasePlayer player)
    {
      if (!Configuration.ApartmentProtection.ProtectApartments ||
          (!Configuration.Other.ShowMessage &&
           !Configuration.Other.PlaySound) ||
          !apartmentDoor || player?.userID.IsSteamId() is null or false)
        return;

      CheckNotifyApartment(apartmentDoor, player);
    }

    // provide feedback when a player hits a protected apartment door
    private object OnEntityTakeDamage(
      ApartmentDoor apartmentDoor, HitInfo hitInfo)
    {
      // abort if:
      // - apartment protection disabled
      // - notifications disabled
      // - door invalid
      // - hit by something/someone other than Steam player
      if (!Configuration.ApartmentProtection.ProtectApartments ||
          (!Configuration.Other.ShowMessage &&
           !Configuration.Other.PlaySound) ||
          !apartmentDoor ||
          hitInfo?.InitiatorPlayer?.userID.IsSteamId() is null or false)
        return null;

      CheckNotifyApartment(apartmentDoor, hitInfo.InitiatorPlayer);

      return null;
    }

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

    private object OnApartmentRoomBreakInComplete(
      ApartmentRoom room, BasePlayer player, ApartmentDoor door)
    {
      if (!Configuration.ApartmentProtection.ProtectApartments ||
          !room || player?.userID.IsSteamId() is null or false || !door)
        return null;

      var (protection, ownerID, damageScale) = GetApartmentProtection(door);
      return TryBlockApartmentOrShopBreakIn(
        player, protection, ownerID, damageScale);
    }

    private object OnRentableShopBreakInComplete(
      RentableShop shop, BasePlayer player)
    {
      if (!Configuration.ApartmentProtection.ProtectShops ||
          !shop || player?.userID.IsSteamId() is null or false)
        return null;

      var (protection, ownerID, damageScale) = GetShopProtection(shop);
      return TryBlockApartmentOrShopBreakIn(
        player, protection, ownerID, damageScale);
    }

    private object TryBlockApartmentOrShopBreakIn(
      BasePlayer player, ApartmentProtectionState protection,
      ulong ownerID, float damageScale)
    {
      // allow if unprotected
      if (ApartmentProtectionState.Protected != protection)
        return null;

      // allow if random chance enabled and roll succeeds
      if (Configuration.ApartmentProtection.DamageAsChance &&
          Random.Range(0f, 1f) <= damageScale)
        return null;

      // block and notify
      NotifyApartmentOrShop(player, ownerID, damageScale);
      return BoxedTrue;
    }

#endregion Hooks

#region Hook Subscribtion

    private void UnsubscribeHooks()
    {
      var needsTcTracking = NeedsTcTracking;
      if (Configuration.RaidProtection.ProtectDecayingBase &&
          Configuration.RaidProtection.ProtectGriefTcs &&
          !needsTcTracking)
      {
        Unsubscribe(nameof(OnCupboardProtectionCalculated));
      }
      var needsTaxProtectionRefunds = Configuration.TaxProtection.Enabled &&
        Configuration.TaxProtection.RefundOnDestruction;
      if (Configuration.RaidProtection.ProtectGriefTcs &&
          !needsTcTracking &&
          !Configuration.Team.IncludeWhitelistPlayers &&
          !needsTaxProtectionRefunds)
      {
        Unsubscribe(nameof(OnEntitySpawned));
        Unsubscribe(nameof(OnEntityKill));
        Unsubscribe(nameof(OnBuildingSplit));
        Unsubscribe(nameof(OnBuildingMerge));
      }
      if (!Configuration.ApartmentProtection.ProtectApartments)
      {
        Unsubscribe(nameof(OnApartmentRoomBreakInComplete));
        Unsubscribe(nameof(OnDoorKnocked));
      }
      if (!Configuration.ApartmentProtection.ProtectShops)
      {
        Unsubscribe(nameof(OnRentableShopBreakInComplete));
      }
      if (!Configuration.Team.TeamAvoidAbuse &&
          !Configuration.Team.TeamEnablePenalty)
      {
        Unsubscribe(nameof(OnTeamDisband));
        Unsubscribe(nameof(OnTeamKick));
        Unsubscribe(nameof(OnTeamLeave));
      }

      var needsStatusHudCupboardTracking =
        Configuration.StatusHud.Enabled;
      var needsGriefCupboardTracking =
        !Configuration.RaidProtection.ProtectGriefTcs;

      if (!Configuration.MapMarker.Enabled)
      {
        Unsubscribe(nameof(CanNetworkTo));
        if (!needsGriefCupboardTracking)
          Unsubscribe(nameof(OnCupboardAssign));
      }
      if (!Configuration.MapMarker.Enabled && !needsStatusHudCupboardTracking &&
          !needsGriefCupboardTracking)
      {
        Unsubscribe(nameof(OnCupboardAuthorize));
        Unsubscribe(nameof(OnCupboardDeauthorize));
        Unsubscribe(nameof(OnCupboardClearList));
      }
      if (!Configuration.Team.IncludeWhitelistPlayers)
      {
        Unsubscribe(nameof(OnCodeEntered));
        Unsubscribe(nameof(OnCodeChanged));
      }

      if (!needsTaxProtectionRefunds)
        Unsubscribe(nameof(OnEntityDeath));


      if (!Configuration.TaxProtection.Enabled ||
          !Configuration.TaxProtection.TaxCurrencyReservesEnabled ||
          Configuration.TaxProtection.MaxCurrencyReserves is 0 ||
          Configuration.TaxProtection.CurrencyItemID is 0)
        Unsubscribe(nameof(CanAcceptItem));

      if (!_isTaxOverlayEnabled)
        Unsubscribe(nameof(OnLootEntity));
      StopTaxOverlayViewerTracking();

      Unsubscribe(nameof(CanLootEntity));
    }

#endregion Hook Subscribtion

#region Cache Methods

    private void CacheData()
    {
      CachePrefabs();
      CacheDefaultTimescales();
      CacheAllPlayerScale();
      CacheAllPlayers();

      if (RequiresTcCacheWithoutDdraw)
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
      _prefabProtection.Clear();
      _prefabProtectionMultipliers.Clear();

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

        CachePrefabProtection(
          prefabID, shortName, IsEntityProtected(shortName));
      }

      var manifest = GameManifest.Current;
      foreach (var entity in manifest.entities)
      {
        var prefab = GameManager.server.FindPrefab(entity.ToLowerInvariant());
        if (!prefab)
          continue;

        var baseCombatEntity =
          prefab.GetComponent(typeof(BaseCombatEntity)) as BaseCombatEntity;
        if (baseCombatEntity)
          CachePrefabProtectionMultiplier(
            baseCombatEntity.prefabID, baseCombatEntity.ShortPrefabName);

        Component activeComponent = null;
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

        var prefabID = baseEntity.prefabID;
        var shortName = baseEntity.ShortPrefabName;
        var isAi = activeComponent is
          BaseNpc or NPCPlayer or BradleyAPC or AttackHelicopter or
          CH47Helicopter or BasePlayer;
        var isVehicle = activeComponent is BaseVehicle && !isAi;

        CachePrefabProtection(
          prefabID, shortName,
          IsEntityProtected(shortName, isVehicle, isAi));
      }
    }

    private void CachePrefabProtection(
      uint prefabID, string shortName, bool isProtected)
    {
      if (isProtected || !_prefabProtection.ContainsKey(prefabID))
        _prefabProtection[prefabID] = isProtected;

      CachePrefabProtectionMultiplier(prefabID, shortName);
    }

    private void CachePrefabProtectionMultiplier(
      uint prefabID, string shortName)
    {
      if (Configuration.RaidProtection.PrefabProtectionMultipliers.TryGetValue(
            shortName, out var multiplier) && multiplier is not 1f)
        _prefabProtectionMultipliers[prefabID] = multiplier;
    }

    private static string GetShortName(string resourcePath) =>
      System.IO.Path.GetFileNameWithoutExtension(resourcePath) ?? string.Empty;

    private static bool IsEntityProtected(
      string shortName, bool isVehicle = false, bool isAi = false)
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

    private List<ulong> CacheClan(string tag)
    {
      if (string.IsNullOrEmpty(tag))
        return null;

      // Call the "GetClan" method and retrieve the clan data
      var clan = Clans?.Call<JObject>("GetClan", tag);
      if (clan?["members"] is null)
        return null;

      if (_clanMemberCache.TryGetValue(tag, out var clanMemberList))
      {
        for (var i = 0; i < clanMemberList.Count; i++)
          _clanTagCache.Remove(clanMemberList[i]);

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

    private void CacheAllPlayerScale()
    {
      foreach (var lastOnline in _lastOnline.Values)
        CacheDamageScale(lastOnline.UserID, -1f);
    }

    private bool CacheAllPlayerPermissions()
    {
      var permissionsChanged = false;
      foreach (var scaleCache in _scaleCache.Values)
        permissionsChanged |= RefreshPlayerPermissionState(scaleCache);

      return permissionsChanged;
    }

    private void RefreshGroupPermissionState(string permissionName)
    {
      var permissionsChanged = false;
      if (permissionName == Configuration.Permission.Protect ||
          permissionName == Configuration.Permission.TaxProtection ||
          permissionName == Configuration.Permission.OnlineProtect)
        permissionsChanged = CacheAllPlayerPermissions();

      if (permissionsChanged)
        RefreshAllProtectionViews();

      if (permissionName == Configuration.Permission.TaxProtection)
        CloseTaxOverlayViewersWithoutPermission();

      if (permissionName != Configuration.Permission.Admin ||
          !CacheAllAdmins(out var hasNewAdmin))
        return;

      RemoveUnauthorizedDdrawSessions();
      QueueMapMarkerRefresh(hasNewAdmin);
    }

    private void RefreshUserPermissionState(
      string id, string permissionName = null)
    {
      ulong userID = 0UL;
      var permissionsChanged = false;
      if ((permissionName is null ||
           permissionName == Configuration.Permission.Protect ||
           permissionName == Configuration.Permission.TaxProtection ||
           permissionName == Configuration.Permission.OnlineProtect) &&
           ulong.TryParse(id, out userID) &&
           _scaleCache.TryGetValue(userID, out var scaleCache))
        permissionsChanged =
          RefreshPlayerPermissionState(scaleCache);

      if (permissionsChanged)
        RefreshProtectionViews(userID);

      if (permissionsChanged && userID is not 0UL)
      {
        var player = _players.GetPlayer(userID);
        if (player && !HasTaxProtectionPermission(player))
          CloseTaxOverlay(player, userID);
      }

      if (permissionName is not null &&
          permissionName != Configuration.Permission.Admin)
        return;

      if (!RefreshAdmin(id, out var becameAdmin))
        return;

      if (!becameAdmin)
        EndDdrawSession(userID);

      QueueMapMarkerRefresh(becameAdmin);
    }

    private static bool RefreshPlayerPermissionState(
      PlayerScaleCache scaleCache)
    {
      var userIDText = scaleCache.UserIDText;
      var hasProtectPerm = userIDText.HasPermission(
        Configuration.Permission.Protect);
      var hasTaxPerm = Configuration.TaxProtection.Enabled &&
        userIDText.HasPermission(Configuration.Permission.TaxProtection);
      var hasOnlinePerm = userIDText.HasPermission(
        Configuration.Permission.OnlineProtect);

      if (scaleCache.HasProtectPermission == hasProtectPerm &&
          scaleCache.HasTaxPermission == hasTaxPerm &&
          scaleCache.HasOnlineProtectPermission == hasOnlinePerm)
        return false;

      scaleCache.HasProtectPermission = hasProtectPerm;
      scaleCache.HasTaxPermission = hasTaxPerm;
      scaleCache.HasOnlineProtectPermission = hasOnlinePerm;
      scaleCache.ExpiresDT = System.DateTime.MinValue;
      return true;
    }

    private void CacheDamageScale(ulong targetID, float scale)
    {
      var scaleCache = GetOrCreateScaleCache(targetID, scale);
      scaleCache.ExpiresDT = System.DateTime.MinValue;
      scaleCache.Scale = scale;
    }

    private void CacheAllPlayers()
    {
      foreach (var player in BasePlayer.allPlayerList)
      {
        _players.AddPlayer(player);
        CacheAdmin(player);
      }
    }

    private bool CacheAllAdmins(out bool hasNewAdmin)
    {
      hasNewAdmin = false;
      var hasAdminChange = false;
      foreach (var player in BasePlayer.activePlayerList)
      {
        var wasAdmin = _adminIDCache.Contains(player.userID.Get());
        var isAdmin = player.HasPermission(Configuration.Permission.Admin);
        if (wasAdmin == isAdmin)
          continue;

        hasAdminChange = true;
        hasNewAdmin |= isAdmin;
      }

      _adminIDCache.Clear();
      foreach (var player in BasePlayer.activePlayerList)
        CacheAdmin(player);

      return hasAdminChange;
    }

    private bool RefreshAdmin(string id, out bool becameAdmin)
    {
      becameAdmin = false;
      if (!ulong.TryParse(id, out var userID))
        return false;

      var wasAdmin = _adminIDCache.Contains(userID);
      var player = _players.GetPlayer(userID);
      if (!player || !player.IsConnected)
      {
        _adminIDCache.Remove(userID);
        return false;
      }

      CacheAdmin(player);
      var isAdmin = _adminIDCache.Contains(userID);
      becameAdmin = !wasAdmin && isAdmin;
      return wasAdmin != isAdmin;
    }

    private void CacheAdmin(BasePlayer player)
    {
      if (!player)
        return;

      var userID = player.userID.Get();
      if (player.HasPermission(Configuration.Permission.Admin))
        _adminIDCache.Add(userID);
      else
        _adminIDCache.Remove(userID);
    }

    private void QueueTcCacheRefresh(uint buildingID)
    {
      if (buildingID is 0U)
        return;

      _queuedPhysicalTcCacheRefreshIds.Add(buildingID);
      QueueTcRefresh(buildingID);
    }

    private void QueueGriefTopologyRefresh(uint buildingID)
    {
      if (Configuration is null ||
          Configuration.RaidProtection.ProtectGriefTcs ||
          buildingID is 0U)
        return;

      QueueTcRefresh(buildingID);
    }

    private void QueueTcRefresh(uint buildingID)
    {
      if (!_queuedTcCacheRefreshIds.Add(buildingID))
        return;

      _queuedTcCacheRefreshes.Enqueue(buildingID);
      if (_tcCacheRefreshQueued)
        return;

      _tcCacheRefreshQueued = true;
      _processQueuedTcCacheRefreshesAction ??= ProcessQueuedTcCacheRefreshes;
      NextFrame(_processQueuedTcCacheRefreshesAction);
    }

    private void ProcessQueuedTcCacheRefreshes()
    {
      if (Configuration is null || !RequiresTcCache)
      {
        ClearQueuedTcCacheRefreshes();
        _griefAffectedCupboardIdsScratch.Clear();
        _tcCacheRefreshQueued = false;
        return;
      }

      _tcCacheRefreshVisualBuildingIdsScratch.Clear();
      _griefAffectedCupboardIdsScratch.Clear();
      var refreshGriefCupboards =
        !Configuration.RaidProtection.ProtectGriefTcs;
      var processed = 0;
      while (processed < TC_CACHE_REFRESH_BATCH_SIZE &&
             _queuedTcCacheRefreshes.Count is not 0)
      {
        var buildingID = _queuedTcCacheRefreshes.Dequeue();
        _queuedTcCacheRefreshIds.Remove(buildingID);
        var refreshPhysicalCupboard =
          _queuedPhysicalTcCacheRefreshIds.Remove(buildingID);
        var previousCupboardNetworkID =
          _tcCache.TryGetValue(buildingID, out var previousTcState) ?
            previousTcState.CupboardNetworkID : 0UL;

        if (refreshGriefCupboards && refreshPhysicalCupboard &&
            previousCupboardNetworkID is not 0UL &&
            _griefOverlapBuildingIdsByCupboard.TryGetValue(
              previousCupboardNetworkID, out var previousOverlapBuildingIds))
        {
          foreach (var overlapBuildingID in previousOverlapBuildingIds)
            QueueGriefTopologyRefresh(overlapBuildingID);
        }

        var building = BuildingManager.server.GetBuilding(buildingID);
        if (refreshPhysicalCupboard)
        {
          CachePhysicalCupboard(buildingID, building);
          _tcCacheRefreshVisualBuildingIdsScratch.Add(buildingID);
          QueueModularBoatMapMarkerSync(previousTcState.Privilege);
          if (_tcCache.TryGetValue(buildingID, out var refreshedTcState))
            QueueModularBoatMapMarkerSync(refreshedTcState.Privilege);
        }

        if (refreshGriefCupboards)
        {
          RebuildGriefBuildingOverlaps(
            buildingID, building, _griefAffectedCupboardIdsScratch);
          if (previousCupboardNetworkID is not 0UL)
            _griefAffectedCupboardIdsScratch.Add(
              previousCupboardNetworkID);
          if (_tcCache.TryGetValue(buildingID, out var currentTcState) &&
              currentTcState.CupboardNetworkID is not 0UL)
          {
            _griefAffectedCupboardIdsScratch.Add(
              currentTcState.CupboardNetworkID);
          }

          if (refreshPhysicalCupboard)
            QueueGriefOverlapNeighbors(buildingID, building);
        }
        processed++;
      }

      if (refreshGriefCupboards)
      {
        RefreshGriefCupboardStates(
          _griefAffectedCupboardIdsScratch,
          _tcCacheRefreshVisualBuildingIdsScratch);
      }

      foreach (var buildingID in _tcCacheRefreshVisualBuildingIdsScratch)
      {
        QueueBuildingStatusHudRefresh(buildingID);
        if (Configuration.MapMarker.Enabled)
          SyncBuildingMapMarker(buildingID);
      }
      _tcCacheRefreshVisualBuildingIdsScratch.Clear();

      if (_queuedTcCacheRefreshes.Count is not 0)
      {
        NextFrame(_processQueuedTcCacheRefreshesAction);
        return;
      }

      _tcCacheRefreshQueued = false;
    }

    private void CacheAllCupboards()
    {
      ClearQueuedTcCacheRefreshes();
      _tcCache.Clear();
      _taxProtectionSyncBuildingIds.Clear();
      ClearCodeLockWhitelistCache();
      RebuildAllCupboardStates();
    }

    private void CacheAllCupboardsPreservingCodeLockWhitelist()
    {
      ClearQueuedTcCacheRefreshes();
      _tcCache.Clear();
      _taxProtectionSyncBuildingIds.Clear();
      RebuildAllCupboardStates();
    }

    private void RebuildAllCupboardStates()
    {
      var protectGriefTcs =
        Configuration.RaidProtection.ProtectGriefTcs;

      // scan all buildings
      foreach (var (buildingID, building)
               in BuildingManager.server.buildingDictionary)
      {
        if (building is not null &&
            TryBuildPhysicalCupboardState(
              buildingID, building, out var tcState))
          _tcCache[buildingID] = tcState;
      }

      if (protectGriefTcs)
      {
        ClearGriefCupboardIndex();
        return;
      }

      RebuildGriefCupboardIndex();
      RemoveStaleCupboardCreationData();
      return;

      void RemoveStaleCupboardCreationData()
      {
        _tmpIdsScratch.Clear();
        foreach (var cupboardNetworkID in _tcCreationData.Keys)
        {
          if (BaseNetworkable.serverEntities.Find(
                new NetworkableId(cupboardNetworkID)) is BuildingPrivlidge)
            continue;

          _tmpIdsScratch.Add(cupboardNetworkID);
        }

        foreach (var cupboardNetworkID in _tmpIdsScratch)
        {
          _tcCreationData.Remove(cupboardNetworkID);
          MarkDataDirty();
        }
      }
    }

    private void CachePhysicalCupboard(
      uint buildingID, BuildingManager.Building building)
    {
      _tcCache.Remove(buildingID);
      _taxProtectionSyncBuildingIds.Remove(buildingID);
      RemoveCodeLockWhitelistCache(buildingID);

      if (TryBuildPhysicalCupboardState(
            buildingID, building, out var tcState))
      {
        _tcCache[buildingID] = tcState;
        if (Configuration.TaxProtection.Enabled && tcState.Privilege)
          UpdateTaxProtectionSyncIndex(
            tcState.Privilege, tcState.CupboardNetworkID,
            System.DateTime.UtcNow.Ticks);
      }
    }

    private bool TryBuildPhysicalCupboardState(
      uint buildingID, BuildingManager.Building building,
      out TcState tcState)
    {
      tcState = default;

      var buildingPrivileges = building?.buildingPrivileges;
      if (buildingPrivileges is null)
        return false;

      BuildingPrivlidge physicalCupboard = null;

      // scan all TCs whose build privileges overlap building
      // Only a matching building ID proves physical membership. Every other
      // entry merely has a privilege zone overlapping this building
      foreach (var buildingPrivlidge in buildingPrivileges)
      {
        if (!buildingPrivlidge)
          continue;

        if (!Configuration.RaidProtection.ProtectGriefTcs)
          EnsureCupboardCreationData(GetNetworkID(buildingPrivlidge));

        // Only one TC can be physically attached to a building ID
        if (!physicalCupboard && buildingPrivlidge.buildingID == buildingID)
          physicalCupboard = buildingPrivlidge;

        if (physicalCupboard)
          break;
      }

      if (!physicalCupboard)
        return false;

      var cupboardNetworkID = GetNetworkID(physicalCupboard);
      var protectedMinutes = physicalCupboard.GetProtectedMinutes();
      tcState = new TcState(
        physicalCupboard,
        cupboardNetworkID,
        IsCachedCupboardDecaying(
          physicalCupboard, protectedMinutes > 0));
      return true;
    }

    private void RebuildGriefCupboardIndex()
    {
      ClearGriefCupboardIndex();

      foreach (var (buildingID, building)
               in BuildingManager.server.buildingDictionary)
      {
        RebuildGriefBuildingOverlaps(buildingID, building, null);
      }

      foreach (var (id, data) in _tcCreationData)
      {
        if (data.GriefState is TcGriefState.ForceTrue)
          _griefCupboardIds.Add(id);
      }

      foreach (var cupboardNetworkID in
               _griefOverlapBuildingIdsByCupboard.Keys)
      {
        if (IsGriefCupboard(cupboardNetworkID))
          _griefCupboardIds.Add(cupboardNetworkID);
      }
    }

    private void ClearGriefCupboardIndex()
    {
      _griefCupboardIds.Clear();

      foreach (var overlapCupboardIds in
               _griefOverlapCupboardIdsByBuilding.Values)
      {
        var pooledOverlapCupboardIds = overlapCupboardIds;
        Facepunch.Pool.FreeUnmanaged(ref pooledOverlapCupboardIds);
      }
      _griefOverlapCupboardIdsByBuilding.Clear();

      foreach (var overlapBuildingIds in
               _griefOverlapBuildingIdsByCupboard.Values)
      {
        var pooledOverlapBuildingIds = overlapBuildingIds;
        Facepunch.Pool.FreeUnmanaged(ref pooledOverlapBuildingIds);
      }
      _griefOverlapBuildingIdsByCupboard.Clear();
      _griefAffectedCupboardIdsScratch.Clear();
      _griefCurrentOverlapCupboardIdsScratch.Clear();
    }

    private void ClearQueuedTcCacheRefreshes()
    {
      // A pending NextFrame callback owns resetting _tcCacheRefreshQueued
      _queuedTcCacheRefreshes.Clear();
      _queuedTcCacheRefreshIds.Clear();
      _queuedPhysicalTcCacheRefreshIds.Clear();
      _tcCacheRefreshVisualBuildingIdsScratch.Clear();
    }

    private void RebuildGriefBuildingOverlaps(
      uint buildingID, BuildingManager.Building building,
      HashSet<ulong> affectedCupboardIds)
    {
      _griefCurrentOverlapCupboardIdsScratch.Clear();
      if (_tcCache.TryGetValue(buildingID, out var buildingTc) &&
          buildingTc.Privilege &&
          building?.buildingPrivileges is { } overlappingPrivileges)
      {
        foreach (var overlappingTc in overlappingPrivileges)
        {
          if (!overlappingTc || overlappingTc.buildingID == buildingID)
            continue;

          var cupboardNetworkID = GetNetworkID(overlappingTc);
          EnsureCupboardCreationData(cupboardNetworkID);
          if (cupboardNetworkID is not 0UL &&
              _tcCache.TryGetValue(
                overlappingTc.buildingID, out var overlappingTcState) &&
              overlappingTcState.CupboardNetworkID == cupboardNetworkID)
          {
            _griefCurrentOverlapCupboardIdsScratch.Add(cupboardNetworkID);
          }
        }
      }

      _griefOverlapCupboardIdsByBuilding.TryGetValue(
        buildingID, out var overlapCupboardIds);
      if (overlapCupboardIds is not null)
      {
        foreach (var cupboardNetworkID in overlapCupboardIds)
        {
          affectedCupboardIds?.Add(cupboardNetworkID);
          if (_griefCurrentOverlapCupboardIdsScratch.Contains(
                cupboardNetworkID) ||
              !_griefOverlapBuildingIdsByCupboard.TryGetValue(
                cupboardNetworkID, out var overlapBuildingIds))
            continue;

          overlapBuildingIds.Remove(buildingID);
          if (overlapBuildingIds.Count is 0)
          {
            _griefOverlapBuildingIdsByCupboard.Remove(cupboardNetworkID);
            Facepunch.Pool.FreeUnmanaged(ref overlapBuildingIds);
          }
        }
      }

      foreach (var cupboardNetworkID in
               _griefCurrentOverlapCupboardIdsScratch)
      {
        affectedCupboardIds?.Add(cupboardNetworkID);
        if (overlapCupboardIds?.Contains(cupboardNetworkID) is true &&
            _griefOverlapBuildingIdsByCupboard.TryGetValue(
              cupboardNetworkID, out var trackedBuildingIds) &&
            trackedBuildingIds.Contains(buildingID))
          continue;

        if (!_griefOverlapBuildingIdsByCupboard.TryGetValue(
              cupboardNetworkID, out var overlapBuildingIds))
        {
          overlapBuildingIds = Facepunch.Pool.Get<HashSet<uint>>();
          _griefOverlapBuildingIdsByCupboard[cupboardNetworkID] =
            overlapBuildingIds;
        }
        overlapBuildingIds.Add(buildingID);
      }

      if (_griefCurrentOverlapCupboardIdsScratch.Count is not 0)
      {
        overlapCupboardIds ??= Facepunch.Pool.Get<HashSet<ulong>>();
        overlapCupboardIds.Clear();
        overlapCupboardIds.UnionWith(
          _griefCurrentOverlapCupboardIdsScratch);
        _griefOverlapCupboardIdsByBuilding[buildingID] = overlapCupboardIds;
      }
      else
      {
        if (_griefOverlapCupboardIdsByBuilding.Remove(
              buildingID, out var removedOverlapCupboardIds))
          Facepunch.Pool.FreeUnmanaged(ref removedOverlapCupboardIds);
      }
      _griefCurrentOverlapCupboardIdsScratch.Clear();
    }

    private void QueueGriefOverlapNeighbors(
      uint buildingID, BuildingManager.Building building)
    {
      var overlappingPrivileges = building?.buildingPrivileges;
      if (overlappingPrivileges is null)
        return;

      foreach (var overlappingTc in overlappingPrivileges)
      {
        if (overlappingTc && overlappingTc.buildingID != buildingID)
          QueueGriefTopologyRefresh(overlappingTc.buildingID);
      }
    }

    private void RefreshGriefCupboardStates(
      HashSet<ulong> cupboardNetworkIds,
      HashSet<uint> visualBuildingIds = null)
    {
      foreach (var cupboardNetworkID in cupboardNetworkIds)
        RefreshGriefCupboardState(cupboardNetworkID, visualBuildingIds);
    }

    private void RefreshGriefCupboardState(
      ulong cupboardNetworkID, HashSet<uint> visualBuildingIds = null)
    {
      if (cupboardNetworkID is 0UL)
        return;

      var wasGriefCupboard = _griefCupboardIds.Contains(cupboardNetworkID);
      var isGriefCupboard = IsGriefCupboard(cupboardNetworkID);
      if (wasGriefCupboard == isGriefCupboard)
        return;

      if (isGriefCupboard)
        _griefCupboardIds.Add(cupboardNetworkID);
      else
        _griefCupboardIds.Remove(cupboardNetworkID);

      if (BaseNetworkable.serverEntities.Find(
            new NetworkableId(cupboardNetworkID)) is BuildingPrivlidge privilege &&
          privilege.buildingID is not 0U)
      {
        QueueTaxProtectionSync(privilege.buildingID);
        if (visualBuildingIds is not null)
        {
          visualBuildingIds.Add(privilege.buildingID);
          return;
        }

        QueueBuildingStatusHudRefresh(privilege.buildingID);
        if (Configuration.MapMarker.Enabled)
          SyncBuildingMapMarker(privilege.buildingID);
      }
    }

    private bool IsGriefCupboard(ulong cupboardNetworkID)
    {
      if (_tcCreationData.TryGetValue(
            cupboardNetworkID, out var creationData))
      {
        if (creationData.GriefState is TcGriefState.ForceTrue)
          return true;
        if (creationData.GriefState is TcGriefState.ForceFalse)
          return false;
      }

      if (!_griefOverlapBuildingIdsByCupboard.TryGetValue(
            cupboardNetworkID, out var overlapBuildingIds) ||
          BaseNetworkable.serverEntities.Find(
            new NetworkableId(cupboardNetworkID)) is not
            BuildingPrivlidge overlappingCupboard ||
          !_tcCache.TryGetValue(
            overlappingCupboard.buildingID, out var overlappingTcState) ||
          overlappingTcState.CupboardNetworkID != cupboardNetworkID)
        return false;

      foreach (var buildingID in overlapBuildingIds)
      {
        if (!_tcCache.TryGetValue(buildingID, out var buildingTc) ||
            !buildingTc.Privilege ||
            HaveSharedCupboardIdentity(
              buildingTc.Privilege, overlappingTcState.Privilege) ||
            !IsCupboardNewer(
              cupboardNetworkID, buildingTc.CupboardNetworkID))
          continue;

        return true;
      }
      return false;
    }

    private bool IsCupboardNewer(
      ulong firstCupboardNetworkId, ulong otherCupboardNetworkId)
    {
      if (!_tcCreationData.TryGetValue(firstCupboardNetworkId,
            out var firstCreation) ||
          !firstCreation.HasTrustedCreationTime ||
          !_tcCreationData.TryGetValue(otherCupboardNetworkId,
            out var otherCreation))
        return false;

      return !otherCreation.HasTrustedCreationTime ||
            firstCreation.CreatedUtcTicks > otherCreation.CreatedUtcTicks;
    }

    private bool HaveSharedCupboardIdentity(
      BuildingPrivlidge firstCupboard, BuildingPrivlidge secondCupboard)
    {
      if (!firstCupboard || !secondCupboard)
        return true;

      if (firstCupboard.OwnerID.IsSteamID() &&
          firstCupboard.OwnerID == secondCupboard.OwnerID)
        return true;

      _tmpIdsScratch.Clear();
      if (secondCupboard.OwnerID.IsSteamID())
        _tmpIdsScratch.Add(secondCupboard.OwnerID);

      if (secondCupboard.authorizedPlayers is not null)
      {
        foreach (var authorizedPlayerID in secondCupboard.authorizedPlayers)
        {
          if (authorizedPlayerID.IsSteamID())
            _tmpIdsScratch.Add(authorizedPlayerID);
        }
      }

      if (firstCupboard.OwnerID.IsSteamID() &&
          _tmpIdsScratch.Contains(firstCupboard.OwnerID))
        return true;

      if (firstCupboard.authorizedPlayers is null)
        return false;

      foreach (var authorizedPlayerID in firstCupboard.authorizedPlayers)
      {
        if (authorizedPlayerID.IsSteamID() &&
            _tmpIdsScratch.Contains(authorizedPlayerID))
          return true;
      }

      return false;
    }

    private bool RemoveCodeLockWhitelistCache(uint buildingID)
    {
      if (buildingID is 0U ||
          !_codeLockWhitelistCache.Remove(buildingID, out var cacheEntry))
        return false;

      foreach (var lockNetworkID in cacheEntry.Locks.Keys)
      {
        if (_codeLockBuildingIds.TryGetValue(lockNetworkID, out var trackedBuildingID) &&
            trackedBuildingID == buildingID)
          _codeLockBuildingIds.Remove(lockNetworkID);
      }

      Facepunch.Pool.Free(ref cacheEntry);
      return true;
    }

    private void ClearCodeLockWhitelistCache()
    {
      foreach (var cacheEntry in _codeLockWhitelistCache.Values)
      {
        var pooledCacheEntry = cacheEntry;
        Facepunch.Pool.Free(ref pooledCacheEntry);
      }

      _codeLockWhitelistCache.Clear();
      _codeLockBuildingIds.Clear();
    }

    private void ProcessQueuedSpawnedCodeLocks()
    {
      _spawnedCodeLocksQueued = false;
      var count = _queuedSpawnedCodeLocks.Count;
      for (var i = 0; i < count; i++)
        TrackSpawnedCodeLock(_queuedSpawnedCodeLocks[i]);
      _queuedSpawnedCodeLocks.Clear();
    }

    private void TrackSpawnedCodeLock(CodeLock codeLock)
    {
      if (!codeLock || !TryGetCodeLockBuildingID(
          codeLock, out var buildingID))
        return;

      var lockNetworkID = GetNetworkID(codeLock);
      if (lockNetworkID is 0UL)
        return;

      if (_codeLockBuildingIds.TryGetValue(
            lockNetworkID, out var trackedBuildingID) &&
          trackedBuildingID != buildingID)
      {
        RemoveCodeLockWhitelistCache(trackedBuildingID);
        RemoveCodeLockWhitelistCache(buildingID);
        return;
      }

      if (_codeLockWhitelistCache.TryGetValue(buildingID, out var cacheEntry))
        RegisterCodeLockWhitelistSnapshot(buildingID, codeLock, cacheEntry);

      QueueCodeLockMapMarkerAuthorizationRefresh(codeLock, buildingID);
    }

    private void RefreshCodeLockWhitelistAndMapMarker(CodeLock codeLock)
    {
      var lockNetworkID = GetNetworkID(codeLock);
      var trackedBuildingID = lockNetworkID is not 0UL &&
        _codeLockBuildingIds.TryGetValue(lockNetworkID, out var buildingID) ?
          buildingID : 0U;
      if (!RefreshCodeLockWhitelistSnapshot(codeLock))
        return;

      QueueCodeLockMapMarkerAuthorizationRefresh(codeLock, trackedBuildingID);
    }

    private bool RefreshCodeLockWhitelistSnapshot(CodeLock codeLock)
    {
      var lockNetworkID = GetNetworkID(codeLock);
      if (lockNetworkID is 0UL ||
          !_codeLockBuildingIds.TryGetValue(lockNetworkID, out var buildingID))
      {
        return TryGetCodeLockBuildingID(codeLock, out buildingID) &&
          RemoveCodeLockWhitelistCache(buildingID);
      }

      var changed = false;
      if (!TryGetCodeLockBuildingID(codeLock, out var currentBuildingID) ||
          currentBuildingID != buildingID)
      {
        changed = RemoveCodeLockWhitelistCache(buildingID);
        changed |= RemoveCodeLockWhitelistCache(currentBuildingID);
        return changed;
      }

      if (!_codeLockWhitelistCache.TryGetValue(buildingID, out var cacheEntry) ||
          !cacheEntry.Locks.TryGetValue(lockNetworkID, out var snapshot))
      {
        return RemoveCodeLockWhitelistCache(buildingID);
      }

      var whitelistPlayers = codeLock.whitelistPlayers;
      foreach (var playerID in snapshot.PlayerIds)
      {
        if (whitelistPlayers?.Contains(playerID) is true)
          continue;

        changed |= RemoveCodeLockWhitelistPlayer(cacheEntry, playerID);
      }

      if (whitelistPlayers is not null)
      {
        foreach (var playerID in whitelistPlayers)
        {
          if (snapshot.PlayerIds.Contains(playerID))
            continue;

          changed |= AddCodeLockWhitelistPlayer(cacheEntry, playerID);
        }
      }

      snapshot.PlayerIds.Clear();
      if (whitelistPlayers is not null)
        snapshot.PlayerIds.UnionWith(whitelistPlayers);
      return changed;
    }

    private bool RemoveTrackedCodeLock(CodeLock codeLock)
    {
      var lockNetworkID = GetNetworkID(codeLock);
      if (lockNetworkID is 0UL ||
          !_codeLockBuildingIds.Remove(lockNetworkID, out var buildingID) ||
          !_codeLockWhitelistCache.TryGetValue(buildingID, out var cacheEntry) ||
          !cacheEntry.Locks.Remove(lockNetworkID, out var snapshot))
        return false;

      foreach (var playerID in snapshot.PlayerIds)
        RemoveCodeLockWhitelistPlayer(cacheEntry, playerID);

      Facepunch.Pool.Free(ref snapshot);
      if (!QueueMapMarkerAuthorizationRefresh(buildingID))
        QueueBoatMapMarkerAuthorizationRefresh(codeLock);
      return true;
    }

    private void RegisterCodeLockWhitelistSnapshot(
      uint buildingID, CodeLock codeLock, CodeLockWhitelistIndex cacheEntry)
    {
      var lockNetworkID = GetNetworkID(codeLock);
      if (lockNetworkID is 0UL)
        return;

      if (_codeLockBuildingIds.TryGetValue(lockNetworkID, out var trackedBuildingID) &&
          trackedBuildingID != buildingID)
        RemoveCodeLockWhitelistCache(trackedBuildingID);

      if (cacheEntry.Locks.ContainsKey(lockNetworkID))
      {
        RefreshCodeLockWhitelistSnapshot(codeLock);
        return;
      }

      var snapshot = Facepunch.Pool.Get<CodeLockWhitelistSnapshot>();
      var whitelistPlayers = codeLock.whitelistPlayers;
      if (whitelistPlayers is not null)
      {
        foreach (var playerID in whitelistPlayers)
        {
          snapshot.PlayerIds.Add(playerID);
          AddCodeLockWhitelistPlayer(cacheEntry, playerID);
        }
      }

      cacheEntry.Locks[lockNetworkID] = snapshot;
      _codeLockBuildingIds[lockNetworkID] = buildingID;
    }

    private static bool AddCodeLockWhitelistPlayer(
      CodeLockWhitelistIndex cacheEntry, ulong playerID)
    {
      if (playerID is 0UL)
        return false;

      if (!cacheEntry.PlayerReferences.TryGetValue(playerID, out var references))
      {
        cacheEntry.PlayerReferences[playerID] = 1;
        cacheEntry.AuthorizedPlayers.Add(playerID);
        return true;
      }

      cacheEntry.PlayerReferences[playerID] = references + 1;
      return false;
    }

    private static bool RemoveCodeLockWhitelistPlayer(
      CodeLockWhitelistIndex cacheEntry, ulong playerID)
    {
      if (!cacheEntry.PlayerReferences.TryGetValue(playerID, out var references))
        return false;

      if (references > 1)
      {
        cacheEntry.PlayerReferences[playerID] = references - 1;
        return false;
      }

      cacheEntry.PlayerReferences.Remove(playerID);
      cacheEntry.AuthorizedPlayers.Remove(playerID);
      return true;
    }

    private static bool TryGetCodeLockBuildingID(CodeLock codeLock,
      out uint buildingID)
    {
      var parentEntity = codeLock?.GetParentEntity();
      var (_, modularBoat, _) = GetVehicle(parentEntity);
      if (modularBoat)
      {
        var privilege = GetModularBoatBuildingPrivilege(
          modularBoat, modularBoat.GetChildPrivilege());
        buildingID = privilege ? privilege.buildingID : 0U;
        return buildingID is not 0U;
      }

      buildingID = (parentEntity as DecayEntity)?.buildingID ?? 0U;
      return buildingID is not 0U;
    }

#region TimeScale Caching & Resolution

    private void CacheDefaultTimescales() =>
      _defaultTimeScales = new(
        Configuration.RaidProtection.AbsoluteTimeScale,
        Configuration.RaidProtection.DamageScale);

    private (Dictionary<int, float> absScale, int[] absKeys,
      Dictionary<float, float> dmgScale, float[] dmgKeys, long boundaryTicks)
      GetActiveTimeScales(long utcTicks)
    {
      TimeScaleSet timeScales;
      long boundaryTicks;
      if (!Configuration.RaidProtection.EnableScheduledTimescales)
      {
        timeScales = _defaultTimeScales;
        boundaryTicks = 0L;
      }
      else
        timeScales = ResolveTimeScaleSet(utcTicks, out boundaryTicks);

      return (
        timeScales.AbsoluteTimeScale,
        timeScales.AbsoluteTimeScaleKeys,
        timeScales.DamageScale,
        timeScales.DamageScaleKeys,
        boundaryTicks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CalcExpiryTicks(
      long nowUtcTicks, long boundaryTicks)
    {
      var expiresTicks = nowUtcTicks + System.TimeSpan.TicksPerMinute;
      return boundaryTicks > nowUtcTicks && boundaryTicks <= expiresTicks ?
        boundaryTicks - 1L : expiresTicks;
    }

#endregion TimeScale Caching & Resolution

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
      BasePlayer player, System.DateTime currentTime)
    {
      if (_lastOnline.TryGetValue(player.userID.Get(), out var lastOnline))
      {
        lastOnline.LastOnlineDT = currentTime;
        lastOnline.UserName = player.displayName ?? lastOnline.UserName;
      }
      else
        _lastOnline[player.userID.Get()] = new(player, currentTime);

      MarkDataDirty();
    }

    private void UpdateLastConnect(
      BasePlayer player, System.DateTime currentTime)
    {
      if (_lastOnline.TryGetValue(player.userID.Get(), out var lastOnline))
        lastOnline.LastConnectDT = currentTime;
      else
        _lastOnline[player.userID.Get()] = new(player, currentTime, true);

      MarkDataDirty();
    }

    private int FillOnlineTimes()
    {
      var currentTime = System.DateTime.UtcNow;
      var playerCount = 0;
      foreach (var currentPlayer in BasePlayer.allPlayerList)
      {
        UpdateLastOnline(currentPlayer, currentTime);
        CacheDamageScale(currentPlayer.userID.Get(), -1f);
        playerCount++;
      }

      SaveData();
      return playerCount;
    }

    private bool IsProtected(BaseCombatEntity entity)
    {
      switch (entity)
      {
        // Boat building block is protected if associated with a boat, and boat
        // protection is enabled
        // NOTE: Boat building blocks in edit mode will not be associated with a
        // boat, and will thus not be protected
        case BoatBuildingBlock boatBuildingBlock:
          if (!PlayerBoat.GetParentPlayerBoat(boatBuildingBlock)) break;
          return Configuration.RaidProtection.ProtectBaseBoats;

        // BuildingBlock is protected, except twig when twig protection disabled
        case BuildingBlock buildingBlock:
          return Configuration.RaidProtection.ProtectTwigs ||
                 buildingBlock.grade is not BuildingGrade.Enum.Twigs;
      }

      if (_prefabProtection.TryGetValue(entity.prefabID, out var isProtected))
        return isProtected;

      // If ProtectAll is enabled, only check the blacklist
      isProtected = Configuration.RaidProtection.ProtectAll
        ? !Configuration.RaidProtection.PrefabsBlacklist.Contains(entity.ShortPrefabName)
        : Configuration.RaidProtection.Prefabs.Contains(entity.ShortPrefabName);

      // Cache result dynamically for future checks, if this ever happens
      _prefabProtection[entity.prefabID] = isProtected;
      return isProtected;
    }

    // return reference to appropriate vehicle type associated with entity
    private static (Tugboat, PlayerBoat, BaseVehicle) GetVehicle(
      BaseEntity entity, bool isParent = false) =>
      entity ?
        entity switch
        {
          Tugboat tugboat =>
            (tugboat, null, null),
          PlayerBoat playerBoat =>
            (null, playerBoat, null),
          BaseVehicle vehicle =>
            (null, null, vehicle),
          _ => isParent ?
            (null, PlayerBoat.GetParentPlayerBoat(entity), null) :
            GetVehicle(entity.GetParentEntity(), true)
        } :
        (null, null, null);

    private static bool IsModularBoatDecaying(PlayerBoat modularBoat)
    {
      if (!modularBoat || modularBoat.healthFraction is 0f ||
          (float)modularBoat.timeSinceLastUsed <
          PlayerBoat.decaystartdelayminutes * 60f)
        return false;

      return !modularBoat.preventDecayIndoors || modularBoat.IsOutside();
    }

    private bool IsCachedModularBoatDecaying(
      PlayerBoat modularBoat, uint buildingID)
    {
      if (buildingID is not 0U &&
          _tcCache.TryGetValue(buildingID, out var tcState) &&
          tcState.Privilege &&
          GetParentModularBoat(tcState.Privilege) == modularBoat)
        return tcState.IsDecaying;

      return IsModularBoatDecaying(modularBoat);
    }

    private bool TryGetTcState(
      BaseCombatEntity entity, out TcState tcState)
    {
      tcState = default;
      if (entity is not DecayEntity decayEntity)
        return false;

      var buildingID = decayEntity.buildingID;
      return buildingID is not 0U &&
             _tcCache.TryGetValue(buildingID, out tcState) &&
             tcState.Privilege;
    }

    private object OnStructureAttack(
      BaseCombatEntity entity, ref HitInfo hitInfo)
    {
      var nowUtc = System.DateTime.UtcNow;
      var decision =
        EvaluateProtection(entity, hitInfo.InitiatorPlayer, nowUtc);

      var result = decision.Kind is DamageDecisionKind.ApplyScale ?
        MitigateDamage(entity, ref hitInfo, in decision) :
        null;

      return result;
    }

    private DamageDecision EvaluateProtection(
      BaseCombatEntity entity, BasePlayer attacker,
      System.DateTime nowUtc, bool ignoreTaxProtection = false,
      PlayerIdSet playerIdSetScratch = null) =>
      EvaluateProtection(
        entity, attacker, nowUtc, ignoreTaxProtection,
        playerIdSetScratch, out _);

    private DamageDecision EvaluateProtection(
      BaseCombatEntity entity, BasePlayer attacker,
      System.DateTime nowUtc, bool ignoreTaxProtection,
      PlayerIdSet playerIdSetScratch,
      out bool authorizedPlayersCollected)
    {
      authorizedPlayersCollected = false;
      var (tugboat, modularBoat, vehicle) = GetVehicle(entity);
      bool isVehicle = vehicle;
      BuildingPrivlidge physicalPrivilege = null;

      // Allow if it is recognised as a grief-building
      if (!Configuration.RaidProtection.ProtectGriefTcs &&
          !isVehicle && !tugboat && !modularBoat &&
          TryGetTcState(entity, out var physicalTc))
      {
        physicalPrivilege = physicalTc.Privilege;
        if (_griefCupboardIds.Contains(physicalTc.CupboardNetworkID))
          return DamageDecision.Allow(entity.OwnerID, isGrief: true);
      }

      var authorizedPlayers =
        playerIdSetScratch ?? _damageScratch.AuthorizedIds;
      authorizedPlayers.Clear();
      if (!GetAuthorizedPlayers(
            entity, tugboat, modularBoat, vehicle,
            physicalPrivilege, authorizedPlayers, out var privilege) || authorizedPlayers.Overflowed)
        return DamageDecision.Allow(isVehicle: isVehicle);

      authorizedPlayersCollected = true;

      // Allow if the TC has either no players authed, or an NPC authed
      // Note: Mixed auth is possible, but we still want to ignore it, because
      //  it probably indicates a Raidable Bases base or something
      var firstPlayer = authorizedPlayers.First;
      if (!firstPlayer.IsSteamID())
        return DamageDecision.Allow(firstPlayer, isVehicle);

      // Allow if damage is from is an authorized player
      if (attacker && authorizedPlayers.Contains(attacker.userID.Get()))
        return DamageDecision.Allow(firstPlayer, isVehicle);

      // Modular boats use their native inactivity/outdoor decay model rather
      // than land-building upkeep and twig checks. A matching physical TC
      // reuses its cached result
      if (modularBoat &&
          !Configuration.RaidProtection.ProtectDecayingModularBoats &&
          IsCachedModularBoatDecaying(
            modularBoat, entity is DecayEntity component ?
              component.buildingID : 0U))
        return DamageDecision.Allow(firstPlayer, isDecaying: true);

      // Allow if the building is decaying
      if (!Configuration.RaidProtection.ProtectDecayingBase &&
          !isVehicle && !tugboat && !modularBoat && privilege &&
          _tcCache.TryGetValue(privilege.buildingID, out var tc) &&
          tc.IsDecaying)
        return DamageDecision.Allow(firstPlayer, false, true);

      // Determine targetID (either the entity's owner or an authorized player)
      var targetID = entity.OwnerID;
      if (targetID is 0UL || !authorizedPlayers.Contains(targetID))
        targetID = firstPlayer;

      // Get the most recent team member based on the configuration setting
      targetID = GetRecentActiveMemberAll(
        targetID, authorizedPlayers, nowUtc);

      // Penalty status should be checked first
      if (!_lastOnline.TryGetValue(targetID, out var targetLastOnline) ||
          IsApiPenaltyActive(targetLastOnline, nowUtc))
        return DamageDecision.Allow(targetID, isVehicle);

      var targetScaleCache = GetOrCreateScaleCache(targetID);

      // --- Tax Protection ---
      long taxProtEndTicks = 0L;
      var appliesTaxProtection = !ignoreTaxProtection &&
        Configuration.TaxProtection.Enabled &&
        (privilege || modularBoat &&
          Configuration.TaxProtection.EnableForModularBoats) &&
        targetScaleCache.HasTaxPermission;
      var hasTaxProtection = appliesTaxProtection &&
        (modularBoat ?
          TryGetModularBoatTaxProtectionEndTicks(
            entity, modularBoat, nowUtc, out taxProtEndTicks) :
          TryGetPurchasedProtectionEndTicks(
            privilege, nowUtc, out taxProtEndTicks));

      float scale;
      if (hasTaxProtection)
      {

        scale = GetCachedDamageScale(
          targetID, targetLastOnline, targetScaleCache, nowUtc);
        return CreateDamageDecision(
          targetID, scale, isVehicle, targetScaleCache,
          taxProtEndTicks);
      }

      if (appliesTaxProtection)
        return DamageDecision.Allow(
          targetID, isVehicle, taxProtectionGated: true);
      // --- Tax Protection ---

      // Check the online status and the configuration setting / cached permission
      var isOnlineRaidProtectionEnabled = Configuration.RaidProtection.OnlineRaidProtection ||
        targetScaleCache.HasOnlineProtectPermission;

      if (!isOnlineRaidProtectionEnabled && AnyPlayersOnline(authorizedPlayers))
        return DamageDecision.Allow(targetID, isVehicle);

      if (!isOnlineRaidProtectionEnabled &&
          !authorizedPlayers.Contains(targetID) && IsOnline(targetID))
        return DamageDecision.Allow(targetID, isVehicle);

      scale = GetCachedDamageScale(targetID, targetLastOnline, targetScaleCache, nowUtc);

      return CreateDamageDecision(
        targetID, scale, isVehicle, targetScaleCache);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DamageDecision CreateDamageDecision(
      ulong targetID, float scale, bool isVehicle,
      PlayerScaleCache targetScaleCache,
      long purchasedProtectionEndTicks = 0L,
      bool taxProtectionGated = false) =>
        scale is <= -1f or 1f ?
        DamageDecision.Allow(
          targetID, isVehicle,
          targetScaleCache: targetScaleCache,
          purchasedProtectionEndTicks: purchasedProtectionEndTicks,
          taxProtectionGated: taxProtectionGated) :
        new DamageDecision(
          DamageDecisionKind.ApplyScale, targetID, scale, isVehicle,
          targetScaleCache: targetScaleCache,
          purchasedProtectionEndTicks: purchasedProtectionEndTicks,
          taxProtectionGated: taxProtectionGated);

    private bool GetAuthorizedPlayers(
      BaseCombatEntity entity, Tugboat tugboat, PlayerBoat modularBoat,
      BaseVehicle vehicle, BuildingPrivlidge physicalPrivilege,
      PlayerIdSet authorizedPlayers,
      out BuildingPrivlidge privilege)
    {
      privilege = null;

      // 1. Base boat checks
      if (tugboat || modularBoat)
      {
        // Abort if base boat protection disabled
        if (!Configuration.RaidProtection.ProtectBaseBoats)
          return false;

        // Don't fall through to TC check, because boats are their own base
        return CollectBoatAuthorizedPlayers(
          tugboat, modularBoat, authorizedPlayers);
      }

      // 2. Vehicle checks
      if (vehicle)
      {
        // Abort if vehicle protection disabled
        if (!Configuration.RaidProtection.ProtectVehicles)
          return false;

        // Modular car code lock check
        if (vehicle is ModularCar { CarLock.WhitelistPlayers: not null } modularCar)
        {
          foreach (var whitelistPlayer in modularCar.CarLock.WhitelistPlayers)
            authorizedPlayers.Add(whitelistPlayer);
          // Don't fall through to TC check if we found player(s)
          if (authorizedPlayers.Count is not 0)
            return true;
          // Else fall through to TC check
        }

        // Fall through to TC privilege check for unlocked/unlockable vehicles
      }

      // 3. Building privilege (Tool Cupboard) checks
      privilege = physicalPrivilege ?? entity?.GetBuildingPrivilege();
      if (!privilege)
        return authorizedPlayers.Count is not 0;

      // TC-authed players check
      authorizedPlayers.AddRange(privilege.authorizedPlayers);

      // Abort if code lock checks disabled
      if (!Configuration.Team.IncludeWhitelistPlayers)
        return authorizedPlayers.Count is not 0;

      authorizedPlayers.AddRange(GetCodeLockWhitelistPlayers(privilege));

      return authorizedPlayers.Count is not 0;
    }

    private bool CollectBoatAuthorizedPlayers(
      Tugboat tugboat, PlayerBoat modularBoat,
      PlayerIdSet authorizedPlayers)
    {
      BaseVehicle boat = tugboat ? tugboat : modularBoat;
      if (!boat)
        return false;

      var vehiclePrivilege = boat.GetChildPrivilege();
      if (!vehiclePrivilege)
        return false;

      authorizedPlayers.AddRange(vehiclePrivilege.authorizedPlayers);
      if (!Configuration.Team.IncludeWhitelistPlayers)
        return authorizedPlayers.Count is not 0;

      var buildingPrivilege = modularBoat ?
        GetModularBoatBuildingPrivilege(modularBoat, vehiclePrivilege) : null;
      if (buildingPrivilege && buildingPrivilege.buildingID is not 0U)
      {
        authorizedPlayers.AddRange(
          GetCodeLockWhitelistPlayers(buildingPrivilege, modularBoat));
      }
      else if (tugboat && tugboat.children is not null)
      {
        foreach (var boatChild in tugboat.children)
          AddCodeLockWhitelistPlayers(boatChild, authorizedPlayers);
      }
      else if (modularBoat && modularBoat.Deployables.Cached is not null)
      {
        foreach (var boatChild in modularBoat.Deployables.Cached)
          AddCodeLockWhitelistPlayers(boatChild, authorizedPlayers);
      }

      return authorizedPlayers.Count is not 0;
    }

    private PlayerIdSet GetCodeLockWhitelistPlayers(
      BuildingPrivlidge privilege, PlayerBoat modularBoat = null)
    {
      if (!modularBoat)
      {
        var parentBoat = GetParentModularBoat(privilege);
        var boatPrivilege = parentBoat ?
          GetModularBoatBuildingPrivilege(
            parentBoat, parentBoat.GetChildPrivilege()) : null;
        if (boatPrivilege)
        {
          privilege = boatPrivilege;
          modularBoat = parentBoat;
        }
      }

      var buildingID = privilege.buildingID;
      if (_codeLockWhitelistCache.TryGetValue(buildingID, out var cacheEntry))
        return cacheEntry.AuthorizedPlayers;

      cacheEntry = Facepunch.Pool.Get<CodeLockWhitelistIndex>();
      _codeLockWhitelistCache[buildingID] = cacheEntry;
      if (modularBoat)
      {
        var deployables = modularBoat.Deployables.Cached;
        if (deployables is not null)
        {
          foreach (var deployable in deployables)
            RegisterCodeLockWhitelistSnapshot(
              buildingID, deployable, cacheEntry);
        }
      }
      else
      {
        var decayEntities = privilege.GetBuilding()?.decayEntities;
        if (decayEntities is not null)
        {
          foreach (var decayEntity in decayEntities)
          {
            if (decayEntity is BuildingBlock)
              continue;

            RegisterCodeLockWhitelistSnapshot(
              buildingID, decayEntity, cacheEntry);
          }
        }
      }
      return cacheEntry.AuthorizedPlayers;
    }

    private void RegisterCodeLockWhitelistSnapshot(uint buildingID,
      BaseEntity entity, CodeLockWhitelistIndex cacheEntry)
    {
      if (!entity)
        return;

      var lockEntity = entity.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
      if (!lockEntity)
        return;

      RegisterCodeLockWhitelistSnapshot(buildingID, lockEntity, cacheEntry);
    }

    private static void AddCodeLockWhitelistPlayers(
      BaseEntity entity, PlayerIdSet targetSet)
    {
      if (!entity)
        return;

      var lockEntity = entity.GetSlot(BaseEntity.Slot.Lock) as CodeLock;
      if (lockEntity?.whitelistPlayers is null)
        return;

      foreach (var playerID in lockEntity.whitelistPlayers)
        targetSet.Add(playerID);
    }

    private ulong GetRecentActiveMemberAll(ulong targetID) =>
      GetRecentActiveMemberAll(targetID, null, System.DateTime.UtcNow);

    private ulong GetRecentActiveMemberAll(
      ulong targetID, PlayerIdSet players,
      System.DateTime nowUtc)
    {
      var playersValid = players?.Count > 0;

      // if not considering teams/clans, consider authorized users - or targetID
      //  if nobody authed
      if (!Configuration.Team.TeamShare)
        return playersValid ? GetOfflineMember(players.GetList(), nowUtc) : targetID;

      var relatedGroups = _relatedPlayerGroupsScratch;
      relatedGroups.Clear();
      if (!playersValid)
        return GetRecentActiveMember(targetID, nowUtc, relatedGroups);

      for (var i = 0; i < players.Count; i++)
      {
        var playerID = players[i];
        AddRelatedPlayerGroups(playerID, relatedGroups);
        relatedGroups.Players.Add(playerID);
      }

      return relatedGroups.Players.Overflowed ? 0UL :
        GetOfflineMember(relatedGroups.Players.GetList(), nowUtc);

      ulong GetRecentActiveMember(
        ulong relatedTargetID, System.DateTime relatedNowUtc,
        RelatedPlayerGroupsScratch scratch)
      {
        AddRelatedPlayerGroups(relatedTargetID, scratch);
        scratch.Players.Add(relatedTargetID);

        return scratch.Players.Overflowed ? relatedTargetID :
          GetOfflineMember(scratch.Players.GetList(), relatedNowUtc);
      }
    }

    private void AddRelatedPlayerGroups(
      ulong playerID, RelatedPlayerGroupsScratch relatedGroups)
    {
      if (Clans is not null)
      {
        var tag = GetCachedClanTag(playerID);
        if (!string.IsNullOrEmpty(tag) &&
            relatedGroups.ClanTags.Add(tag) &&
            GetCachedClanMembers(tag) is { Count: > 0 } clanMembers)
          relatedGroups.Players.AddRange(clanMembers);
      }

      var player = _players.GetPlayer(playerID);
      var team = GetTeam(player) ?? GetTeam(playerID);
      // GetTeamMembers combines these groups, but either one may be new
      // while the other was already expanded for this evaluation
      if (team is { members.Count: > 0 } &&
          relatedGroups.TeamIds.Add(team.teamID))
        relatedGroups.Players.AddRange(team.members);

      if (player is not { serverClan: not null, clanId: not 0 } ||
          !relatedGroups.VanillaClanIds.Add(player.clanId))
        return;

      foreach (var clanMember in player.serverClan.Members)
      {
        var memberID = clanMember.SteamId;
        if (memberID != playerID)
          relatedGroups.Players.Add(memberID);
      }
    }

    private bool AnyPlayersOffline(List<ulong> playerIDs)
    {
      for (var i = 0; i < playerIDs.Count; i++)
      {
        if (IsOffline(playerIDs[i]))
          return true;
      }

      return false;
    }

    private bool AnyPlayersOnline(PlayerIdSet playerIDs)
    {
      for (var i = 0; i < playerIDs.Count; i++)
      {
        if (IsOnline(playerIDs[i]))
          return true;
      }

      return false;
    }

    private bool IsOffline(ulong playerID) =>
      _lastOnline.TryGetValue(playerID, out var lastOnlinePlayer) ?
        IsOffline(playerID, lastOnlinePlayer, System.DateTime.UtcNow) :
        _players.GetPlayer(playerID)?.IsConnected is not true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsOnline(ulong playerID) =>
      _players.GetPlayer(playerID)?.IsConnected is true;

    private bool IsOffline(
      ulong playerID, LastOnlineData lastOnlinePlayer,
      System.DateTime nowUtc)
    {
      if (IsOnline(playerID))
        return false;

      return GetOfflineMinutesUnchecked(lastOnlinePlayer, nowUtc) >=
        Configuration.RaidProtection.CooldownMinutes;
    }

    private float GetOfflineMinutes(
      LastOnlineData lastOnlinePlayer, System.DateTime nowUtc) =>
      lastOnlinePlayer is null || IsOnline(lastOnlinePlayer.UserID) ?
        0f :
        GetOfflineMinutesUnchecked(lastOnlinePlayer, nowUtc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GetOfflineMinutesUnchecked(
      LastOnlineData lastOnlinePlayer, System.DateTime nowUtc) =>
      lastOnlinePlayer is null ? 0f :
        (nowUtc.Ticks - lastOnlinePlayer.LastOnlineTicks) /
          (float)System.TimeSpan.TicksPerMinute;

    private float GetOfflineHours(
      LastOnlineData lastOnlinePlayer, System.DateTime nowUtc) =>
      lastOnlinePlayer is null || IsOnline(lastOnlinePlayer.UserID) ?
        0f :
        GetOfflineHoursUnchecked(lastOnlinePlayer, nowUtc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GetOfflineHoursUnchecked(
      LastOnlineData lastOnlinePlayer, System.DateTime nowUtc) =>
      lastOnlinePlayer is null ? 0f :
        (nowUtc.Ticks - lastOnlinePlayer.LastOnlineTicks) /
          (float)System.TimeSpan.TicksPerHour;

    private PlayerScaleCache GetOrCreateScaleCache(
      ulong targetID, float initialScale = -1f,
      string userIDText = null)
    {
      if (_scaleCache.TryGetValue(targetID, out var scaleCache))
        return scaleCache;

      userIDText ??= userIDText ?? targetID.ToString();
      scaleCache = new(
        userIDText, System.DateTime.MinValue, initialScale,
        userIDText.HasPermission(Configuration.Permission.Protect),
        Configuration.TaxProtection.Enabled &&
        userIDText.HasPermission(Configuration.Permission.TaxProtection),
        userIDText.HasPermission(Configuration.Permission.OnlineProtect));
      _scaleCache[targetID] = scaleCache;

      return scaleCache;
    }

    private float GetCachedDamageScale(
      ulong targetID, LastOnlineData targetLastOnline,
      PlayerScaleCache targetScaleCache, System.DateTime nowUtc)
    {
      if (targetScaleCache is null)
        return -1f;

      var nowUtcTicks = nowUtc.Ticks;
      if (nowUtcTicks <= targetScaleCache.ExpiresTicks)
        return targetScaleCache.Scale;

      float scale;
      long boundaryTicks;
      if (targetScaleCache.HasProtectPermission)
      {
        scale = GetDamageScale(
          targetID, targetLastOnline, targetScaleCache, nowUtc,
          out boundaryTicks, out _);
      }
      else
      {
        scale = -1f;
        boundaryTicks = 0L;
      }

      var expiresTicks = CalcExpiryTicks(nowUtcTicks, boundaryTicks);
      targetScaleCache.ExpiresTicks = expiresTicks;
      targetScaleCache.Scale = scale;
      return scale;
    }

    private float GetCachedDamageScale(ulong targetID)
    {
      var nowUtc = System.DateTime.UtcNow;
      if (!_lastOnline.TryGetValue(targetID, out var targetLastOnline))
        return -1f;

      var targetScaleCache = GetOrCreateScaleCache(targetID);
      return GetCachedDamageScale(
        targetID, targetLastOnline, targetScaleCache, nowUtc);
    }

    private float GetDamageScale(
      ulong targetID, PlayerScaleCache scaleCache = null)
    {
      var nowUtc = System.DateTime.UtcNow;
      return !_lastOnline.TryGetValue(targetID, out var targetLastOnline) ?
        -1f :
        GetDamageScale(
          targetID, targetLastOnline, scaleCache, nowUtc, out _, out _);
    }

    private float GetDamageScale(
      ulong targetID, LastOnlineData targetLastOnline,
      PlayerScaleCache scaleCache, System.DateTime nowUtc) =>
      GetDamageScale(
        targetID, targetLastOnline, scaleCache, nowUtc, out _, out _);

    private float GetDamageScale(
      ulong targetID, LastOnlineData targetLastOnline,
      PlayerScaleCache scaleCache, System.DateTime nowUtc,
      out long boundaryTicks, out float[] damageScaleKeys)
    {
      var isOnlineRaidProtectionEnabled = Configuration.RaidProtection.OnlineRaidProtection ||
        (scaleCache is not null && scaleCache.HasOnlineProtectPermission);

      if (targetLastOnline is null || (!isOnlineRaidProtectionEnabled && IsOnline(targetID)))
      {
        boundaryTicks = 0L;
        damageScaleKeys = null;
        return -1f;
      }

      TimeScaleSet timeScales;
      if (!Configuration.RaidProtection.EnableScheduledTimescales)
      {
        timeScales = _defaultTimeScales;
        boundaryTicks = 0L;
      }
      else
        timeScales = ResolveTimeScaleSet(nowUtc.Ticks, out boundaryTicks);

      damageScaleKeys = timeScales.DamageScaleKeys;
      var scale = GetProfileDamageScale(
        timeScales.AbsoluteTimeScale, timeScales.AbsoluteTimeScaleKeys,
        timeScales.DamageScale, damageScaleKeys,
        targetID, targetLastOnline, nowUtc,
        scaleCache is not null && scaleCache.HasOnlineProtectPermission,
        out var offlineTimeScaleApplies);

      UpdateRemainingTime(
        scaleCache, targetLastOnline, damageScaleKeys,
        offlineTimeScaleApplies, nowUtc);

      return scale;
    }

    private void UpdateRemainingTime(
      PlayerScaleCache scaleCache, LastOnlineData targetLastOnline,
      float[] damageScaleKeys, bool offlineTimeScaleApplies,
      System.DateTime nowUtc)
    {
      if ((!Configuration.Other.ShowRemainingTime &&
           !Configuration.StatusHud.ShowRemainingTime &&
           !Configuration.MapMarker.Enabled &&
           _ddrawSessions.Count is 0) ||
          scaleCache is null)
        return;

      if (offlineTimeScaleApplies && damageScaleKeys.Length > 0)
      {
        var remainingHours =
          damageScaleKeys[^1] - GetOfflineHours(targetLastOnline, nowUtc);
        scaleCache.RemainingTime = GetClampedTimeSpanFromHours(remainingHours);
      }
      else
        scaleCache.RemainingTime = System.TimeSpan.Zero;
    }

    private float GetProfileDamageScale(
      Dictionary<int, float> absoluteTimeScale,
      int[] absoluteTimeScaleKeys,
      Dictionary<float, float> damageScale,
      float[] damageScaleKeys,
      ulong targetID, LastOnlineData targetLastOnline,
      System.DateTime nowUtc, bool allowOnlineProtection,
      out bool offlineTimeScaleApplies)
    {
      offlineTimeScaleApplies = false;
      if (absoluteTimeScaleKeys.Length > 0)
      {
        var scale = absoluteTimeScale.GetValueOrDefault(
          System.TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _timeZone).Hour,
          -1f);

        if (scale is not -1f)
          return scale;
      }

      if (damageScaleKeys.Length is 0)
        return -1f;

      var isOnline = IsOnline(targetID);
      if (!allowOnlineProtection &&
          (isOnline || GetOfflineMinutesUnchecked(targetLastOnline, nowUtc) <
           Configuration.RaidProtection.CooldownMinutes))
        return -1f;

      if (!isOnline && Configuration.RaidProtection.CooldownQualifyMinutes > 0)
      {
        var minutes =
          (targetLastOnline.LastOnlineTicks - targetLastOnline.LastConnectTicks) /
          (float)System.TimeSpan.TicksPerMinute;

        if (targetLastOnline.LastConnect <= 0L ||
            minutes < Configuration.RaidProtection.CooldownQualifyMinutes)
          return -1f;
      }

      var hours = isOnline ? 0f :
        GetOfflineHoursUnchecked(targetLastOnline, nowUtc);

      if (hours < damageScaleKeys[0])
      {
        offlineTimeScaleApplies = true;
        return Configuration.RaidProtection.InterimDamage;
      }

      var low = 0;
      var high = damageScaleKeys.Length - 1;
      while (low <= high)
      {
        var middle = low + ((high - low) >> 1);
        if (hours >= damageScaleKeys[middle])
          low = middle + 1;
        else
          high = middle - 1;
      }

      offlineTimeScaleApplies = true;
      return damageScale[damageScaleKeys[high]];
    }

    private object MitigateDamage(
      BaseCombatEntity entity, ref HitInfo hitInfo, in DamageDecision decision)
    {
      var scale = decision.Scale;
      if (scale < 1f &&
          _prefabProtectionMultipliers.TryGetValue(
            entity.prefabID, out var prefabMultiplier))
        scale = 1f - ((1f - scale) * prefabMultiplier);

      if (scale >= 1f)
      {
        if (scale > 1f)
          hitInfo.damageTypes.ScaleAll(scale);
        return null;
      }

      var initiator = hitInfo.InitiatorPlayer;
      var playSound = Configuration.Other.PlaySound;
      var gameTipWeaponCategories =
        Configuration.Other.GameTipWeaponCategories;
      var gameTipAvailable = Configuration.Other.ShowMessage &&
        gameTipWeaponCategories is { Count: > 0 };

      if (!initiator || (!gameTipAvailable && !playSound))
      {
        if (scale is not 0f)
          hitInfo.damageTypes.ScaleAll(scale);

        return scale is 0f ? BoxedTrue : null;
      }

      var majorityDamageType = hitInfo.damageTypes.GetMajorityDamageType();
      var isFire = majorityDamageType
        is Rust.DamageType.Heat or Rust.DamageType.Fun_Water;
      var showMessage = gameTipAvailable &&
          (!isFire || hitInfo.WeaponPrefab is not null) &&
          gameTipWeaponCategories.Contains(
            GetGameTipWeaponCategory(ref hitInfo, majorityDamageType));

      if (scale is not 0f)
        hitInfo.damageTypes.ScaleAll(scale);

      if (showMessage)
        SendMessage(
          ref hitInfo, in decision,
          scale is 0f ? 100f : scale.ToPercent());

      if (playSound && !isFire)
      {
        Effect.server.Run(
          Configuration.Other.SoundPath,
          initiator.transform.position,
          Vector3.zero);
      }

      return scale is 0f ? BoxedTrue : null;
    }

#endregion Core Methods

#region Apartment & Shop Methods

    private enum ApartmentProtectionState : byte
    {
      NotApplicable,
      UnprotectedRentDue,
      UnprotectedAbsent,
      UnprotectedDamageScale,
      Protected
    }

    // notify player if apartment door is protected
    private void CheckNotifyApartment(
      ApartmentDoor apartmentDoor, BasePlayer player)
    {
      var (protection, ownerID, damageScale) =
        GetApartmentProtection(apartmentDoor);

      if (ApartmentProtectionState.Protected == protection)
        NotifyApartmentOrShop(player, ownerID, damageScale);
    }

    // notify player of apartment or shop protection
    private void NotifyApartmentOrShop(
      BasePlayer player, ulong ownerID, float damageScale)
    {
      if (Configuration.Other.ShowMessage)
      {
        var percent = damageScale <= 0f ? 100f : damageScale.ToPercent();
        SendMessage(player, ownerID, percent);
      }

      if (Configuration.Other.PlaySound)
      {
        Effect.server.Run(
          Configuration.Other.SoundPath,
          player.transform.position,
          Vector3.zero);
      }
    }

    private static ulong GetApartmentOwnerID(ApartmentRoom apartmentRoom)
    {
      if (apartmentRoom.owners?.Count is not > 0)
        return 0UL;

      foreach (var ownerID in apartmentRoom.owners)
        return ownerID.IsSteamId() ? ownerID : 0UL;

      return 0UL;
    }

    private (ApartmentProtectionState, ulong, float) GetApartmentProtection(
      ApartmentDoor apartmentDoor)
    {
      // room must be rented to an offline player
      var ownerID = 0UL;
      var damageScale = -1f;
      if (!Configuration.ApartmentProtection.ProtectApartments ||
          !apartmentDoor || !BaseNetworkable.serverEntities.TryGetEntity(
            apartmentDoor.ApartmentId, out ApartmentRoom apartmentRoom) ||
          !apartmentRoom || !apartmentRoom.IsCurrentlyRented() ||
          apartmentRoom.owners.Count <= 0)
        return (ApartmentProtectionState.NotApplicable, ownerID, damageScale);

      // check if rent due voids protection
      if (!Configuration.ApartmentProtection.WhenRentDue &&
          apartmentRoom.timeRentOverdue > 0f)
        return (ApartmentProtectionState.UnprotectedRentDue, ownerID, damageScale);

      // remaining checks require apartment owner userID
      ownerID = GetApartmentOwnerID(apartmentRoom);
      if (ownerID is 0UL || !ownerID.IsSteamId() || IsOnline(ownerID))
        return (ApartmentProtectionState.NotApplicable, ownerID, damageScale);

      // check if absent owner voids protection
      if (!Configuration.ApartmentProtection.WhenAbsent)
      {
        var owner = _players.GetPlayer(ownerID);
        if (!owner || !apartmentRoom.IsInsideRoom(owner))
          return (ApartmentProtectionState.UnprotectedAbsent, ownerID, damageScale);
      }

      var (protection, evaluatedDamageScale) =
        EvaluateRentableProtection(ownerID);
      return (protection, ownerID, evaluatedDamageScale);
    }

    private (ApartmentProtectionState, ulong, float) GetShopProtection(
      RentableShop rentableShop)
    {
      // shop must be rented to an offline player
      var ownerID = 0UL;
      var damageScale = -1f;
      if (!Configuration.ApartmentProtection.ProtectShops ||
          rentableShop?.IsOn() is false or null)
        return (ApartmentProtectionState.NotApplicable, ownerID, damageScale);

      // remaining checks require apartment owner userID
      ownerID = rentableShop.ShopOwnerId;
      if (ownerID is 0UL || !ownerID.IsSteamId() || IsOnline(ownerID))
        return (ApartmentProtectionState.NotApplicable, ownerID, damageScale);

      var (protection, evaluatedDamageScale) =
        EvaluateRentableProtection(ownerID);
      return (protection, ownerID, evaluatedDamageScale);
    }

    private (ApartmentProtectionState, float) EvaluateRentableProtection(
      ulong ownerID)
    {
      var damageScale = GetCachedDamageScale(ownerID);
      if (damageScale < 0f)
        damageScale = GetDamageScale(ownerID);
      if (damageScale < 0f ||
          damageScale >= Configuration.ApartmentProtection.WhenDamageBelow ||
          damageScale >= 1f)
        return (ApartmentProtectionState.UnprotectedDamageScale, damageScale);

      // must be at least partially protected
      return (ApartmentProtectionState.Protected, damageScale);
    }

#endregion Apartment & Shop Methods

#region Game Tip Message

    private void SendMessage(
      ref HitInfo hitInfo, in DamageDecision decision,
      float amount = 100f)
    {
      var initiator = hitInfo.InitiatorPlayer;
      if (!_scaleCache.TryGetValue(
            initiator.userID.Get(), out var playerScaleCache))
        return;

      if (playerScaleCache.ActiveGameTipMessage)
        return;

      ShowMessageTip(
        initiator, amount, decision.IsVehicle,
        playerScaleCache, decision.TargetScaleCache);
      playerScaleCache.ActiveGameTipMessage = true;
    }

    private void SendMessage(
      BasePlayer player, ulong targetID, float amount,
      bool isVehicle = false)
    {
      if (!player ||
          !_scaleCache.TryGetValue(
            player.userID.Get(), out var playerScaleCache))
        return;

      if (playerScaleCache.ActiveGameTipMessage)
        return;

      var targetScaleCache = GetOrCreateScaleCache(targetID);
      ShowMessageTip(
        player, amount, isVehicle, playerScaleCache, targetScaleCache);
      playerScaleCache.ActiveGameTipMessage = true;
    }

    private void ShowMessageTip(
      BasePlayer player, float amount, bool isVehicle,
      PlayerScaleCache playerScaleCache,
      PlayerScaleCache targetScaleCache)
    {
      _sb.Clear();
      _sb.Append(
          isVehicle ?
            playerScaleCache.ProtectionMessageVehicle :
            playerScaleCache.ProtectionMessageBuilding)
        .Append("<color=").Append(GetColor(amount)).Append('>').Append(amount).Append("%</color>");

      if (Configuration.Other.ShowRemainingTime)
      {
        var remainingTime =
          targetScaleCache?.RemainingTime ?? System.TimeSpan.Zero;
        if (remainingTime != System.TimeSpan.Zero)
        {
          _sb.Append(" (");
          AppendFormattedDuration(
            _sb,
            remainingTime.Ticks / System.TimeSpan.TicksPerSecond,
            includeDays: true);
          _sb.Append(')');
        }
      }

      _gameTipArgs[0] = BoxedGameTipStyleBlueShort;
      _gameTipArgs[1] = _sb.ToString();
      _gameTipArgs[2] = string.Empty;
      _gameTipArgs[3] = BoxedFalse;
      player.SendConsoleCommand(
        COMMAND_SHOWTOAST, _gameTipArgs);
      ServerMgr.Instance.Invoke(
        playerScaleCache.HideGameTipAction,
        Configuration.Other.MessageDuration);
    }

    private static string GetColor(float amount) =>
      amount switch
      {
        100f => COLOR_RED,
        > 50f and < 100f => COLOR_ORANGE,
        > 25f and <= 50f => COLOR_YELLOW,
        > 0f and <= 25f => COLOR_AQUA,
        0f => COLOR_GREEN,
        _ => COLOR_WHITE
      };


    [JsonConverter(typeof(StringEnumConverter))]
    private enum GameTipWeaponCategory : byte
    {
      None,
      Melee,
      Ranged,
      Explosive
    }

    private static GameTipWeaponCategory GetGameTipWeaponCategory(
      ref HitInfo hitInfo, Rust.DamageType majorityDamageType)
    {
      var damageTypes = hitInfo.damageTypes;

      if (damageTypes.Has(Rust.DamageType.Explosion))
        return GameTipWeaponCategory.Explosive;

      if (Rust.DamageTypeEx.IsMeleeType(majorityDamageType))
        return GameTipWeaponCategory.Melee;

      return hitInfo.IsProjectile() ||
             hitInfo.Weapon is BaseProjectile ||
             hitInfo.WeaponPrefab is BaseProjectile ?
        GameTipWeaponCategory.Ranged :
        GameTipWeaponCategory.None;
    }

#endregion Game Tip Message

#region Status HUD

#region Fields

    private readonly Dictionary<ulong, HudPlayerState> _hudStates = new();
    private readonly Dictionary<uint, HashSet<ulong>>
      _statusHudPlayerIdsByBuilding = new();
    private readonly HashSet<ulong> _queuedStatusHudPlayerIds = new();
    private readonly SortedDictionary<int, List<HudScheduleEntry>> _statusHudDueQueue = new();
    private readonly Stack<List<HudScheduleEntry>> _statusHudDueListPool = new();
    private readonly StringBuilder _hudBuilder = new(512);
    private long _statusHudScheduleGeneration;
    private bool _statusHudRefreshQueued;
    private System.Action _refreshQueuedStatusHudsAction;
#if CARBON
    private LuiPosition _statusHudPosition;
    private LuiOffset _statusHudOffset;
#else
    private string _hudPayloadPrefix;
#endif
    private PluginTimer _statusHudScheduler;

#region Constants

    private const string FONT_ROBOTO_CONDENSED_BOLD = "robotocondensed-bold.ttf";
    private const string STATUS_HUD_NAME = "ORP_HUD_STATUS_BANNER";
    private const string STATUS_HUD_TEXT_NAME = STATUS_HUD_NAME + ".Text";
    private const string STATUS_HUD_BACKGROUND_COLOR = "0.06 0.08 0.11 0.82";
    private const string STATUS_HUD_TEXT_COLOR = "1 1 1 1";
    private const string STATUS_HUD_OUTLINE_COLOR = "0 0 0 0.85";
    private const string STATUS_HUD_SUBTEXT_COLOR = "#E0E0E0";
    private const string STATUS_HUD_PENALTY_COLOR = "#FF6B6B";
    private const string STATUS_HUD_PROTECTED_TEXT = "PROTECTED";
    private const string STATUS_HUD_VULNERABLE_TEXT = "VULNERABLE";
    private const string STATUS_HUD_DECAYING_TEXT = "DECAYING";
    private const string STATUS_HUD_GRIEF_TEXT = "GRIEF";
    private const string STATUS_HUD_INCREASED_DAMAGE_TEXT = "INCREASED DAMAGE";
    private const string STATUS_HUD_PENALTY_TEXT = "Penalty | ";
    private const int STATUS_HUD_HEADER_FONT_SIZE = 15;
    private const int STATUS_HUD_BODY_FONT_SIZE = 13;
    private static readonly string STATUS_HUD_BODY_PREFIX =
      "\n<size=" + STATUS_HUD_BODY_FONT_SIZE + "><color=" + STATUS_HUD_SUBTEXT_COLOR + ">";
    private static readonly string STATUS_HUD_INCREASED_DAMAGE_PREFIX =
      "\n<size=" + STATUS_HUD_BODY_FONT_SIZE + "><color=" + STATUS_HUD_SUBTEXT_COLOR + ">+";
    private static readonly string STATUS_HUD_PENALTY_PREFIX =
      "\n<size=" + STATUS_HUD_BODY_FONT_SIZE + "><color=" + STATUS_HUD_PENALTY_COLOR + ">" + STATUS_HUD_PENALTY_TEXT;
    private const float STATUS_HUD_SCHEDULER_INTERVAL = 0.5f;
#if !CARBON
    private const string STATUS_HUD_PAYLOAD_SUFFIX =
      "\",\"fontSize\":15,\"align\":\"MiddleCenter\"," +
      "\"verticalOverflow\":\"Overflow\",\"font\":\"" + FONT_ROBOTO_CONDENSED_BOLD + "\"," +
      "\"color\":\"1 1 1 1\"},{\"type\":\"UnityEngine.UI.Outline\"," +
      "\"color\":\"0 0 0 0.85\",\"distance\":\"1 -1\"},{\"type\":\"RectTransform\"," +
      "\"anchormin\":\"0 0\",\"anchormax\":\"1 1\",\"offsetmin\":\"4 2\"," +
      "\"offsetmax\":\"-4 -2\"}]}]";
#endif
#if CARBON
    private static readonly LuiOffset STATUS_HUD_TEXT_OFFSET =
      new(4f, 2f, -4f, -2f);
#endif

#endregion Constants

#endregion Fields

#region Types & Classes

    private enum HUDProtectionState : byte
    {
      Vulnerable,
      Protected,
      Partial,
      IncreasedDamage,
      Decaying,
      Grief
    }

    private sealed class HudPlayerState : Facepunch.Pool.IPooled
    {
      public BuildingPrivlidge CommandPrivilege;
      public long ScheduleGeneration;
      public HudStateSnapshot Snapshot;
      public uint BuildingID;
      public float HudExpiresAt;
      public float HudRefreshAt;
      public float PrivilegeRefreshAt;
      public bool HasSnapshot;
      public bool IsVisible;

      public void EnterPool()
      {
        CommandPrivilege = null;
        ScheduleGeneration = 0L;
        Snapshot = default;
        BuildingID = 0U;
        HudExpiresAt = 0f;
        HudRefreshAt = 0f;
        PrivilegeRefreshAt = 0f;
        HasSnapshot = false;
        IsVisible = false;
      }

      public void LeavePool() { }
    }

    private readonly struct HudScheduleEntry
    {
      public readonly ulong PlayerID;
      public readonly long Generation;

      public HudScheduleEntry(ulong playerID, long generation)
      {
        PlayerID = playerID;
        Generation = generation;
      }
    }

    private readonly struct HudStateSnapshot
    {
      private readonly ulong _targetNetworkID;
      private readonly long _remainingMinutes;
      private readonly long _penaltySeconds;
      private readonly float _scale;
      private readonly HUDProtectionState _state;
      private readonly bool _hasTaxProtection;
      private readonly bool _hasOnlineProtection;

      public HudStateSnapshot(
        ulong targetNetworkID, HUDProtectionState state, float scale,
        long remainingMinutes, long penaltySeconds, bool hasTaxProtection,
        bool hasOnlineProtection)
      {
        _targetNetworkID = targetNetworkID;
        _remainingMinutes = remainingMinutes;
        _penaltySeconds = penaltySeconds;
        _scale = scale;
        _state = state;
        _hasTaxProtection = hasTaxProtection;
        _hasOnlineProtection = hasOnlineProtection;
      }

      public bool Matches(in HudStateSnapshot other) =>
        _targetNetworkID == other._targetNetworkID &&
        _remainingMinutes == other._remainingMinutes &&
        _penaltySeconds == other._penaltySeconds &&
        _scale == other._scale &&
        _state == other._state &&
        _hasTaxProtection == other._hasTaxProtection &&
        _hasOnlineProtection == other._hasOnlineProtection;
    }

#endregion Types & Classes

#region Methods

    private void InitializeStatusHud()
    {
      var options = Configuration.StatusHud;
      if (!options.Enabled)
        return;

      _refreshQueuedStatusHudsAction = RefreshQueuedStatusHuds;

#if CARBON
      TryParseAnchor(options.AnchorMin, out var minX, out var minY);
      TryParseAnchor(options.AnchorMax, out var maxX, out var maxY);
      TryParseOffset(options.OffsetMin, out var minOffsetX, out var minOffsetY);
      TryParseOffset(options.OffsetMax, out var maxOffsetX, out var maxOffsetY);
      _statusHudPosition = new(minX, minY, maxX, maxY);
      _statusHudOffset = new(minOffsetX, minOffsetY, maxOffsetX, maxOffsetY);
#else
      _hudPayloadPrefix =
        $"[{{\"name\":\"{STATUS_HUD_NAME}\",\"parent\":\"Hud\",\"components\":[" +
        $"{{\"type\":\"UnityEngine.UI.Image\",\"color\":\"{STATUS_HUD_BACKGROUND_COLOR}\"}}," +
        $"{{\"type\":\"RectTransform\",\"anchormin\":\"{options.AnchorMin}\"," +
        $"\"anchormax\":\"{options.AnchorMax}\",\"offsetmin\":\"{options.OffsetMin}\"," +
        $"\"offsetmax\":\"{options.OffsetMax}\"}}]}},{{\"name\":\"{STATUS_HUD_TEXT_NAME}\"," +
        $"\"parent\":\"{STATUS_HUD_NAME}\",\"components\":[{{\"type\":\"UnityEngine.UI.Text\",\"text\":\"";
#endif

      var nowRealtime = UnityEngine.Time.realtimeSinceStartup;
      foreach (var player in BasePlayer.activePlayerList)
      {
        var playerID = player.userID.Get();
        if (!_hudStates.TryGetValue(playerID, out var hudState))
        {
          hudState = Facepunch.Pool.Get<HudPlayerState>();
          _hudStates[playerID] = hudState;
        }
        hudState.PrivilegeRefreshAt =
          nowRealtime + UnityEngine.Random.Range(
            0f, options.RefreshInterval);
        ScheduleStatusHudRefresh(playerID, hudState);
      }

      _statusHudScheduler = timer.Every(
        STATUS_HUD_SCHEDULER_INTERVAL,
        RefreshStatusHudScheduler);
    }

    private void UnloadStatusHud()
    {
      _statusHudScheduler?.Destroy();
      _statusHudScheduler = null;
      _statusHudRefreshQueued = false;
      _refreshQueuedStatusHudsAction = null;
      _queuedStatusHudPlayerIds.Clear();
      _statusHudDueQueue.Clear();
      _statusHudDueListPool.Clear();
      _statusHudScheduleGeneration = 0L;

      foreach (var player in BasePlayer.activePlayerList)
        HideStatusHud(player);

      foreach (var hudState in _hudStates.Values)
      {
        var state = hudState;
        Facepunch.Pool.Free(ref state);
      }

      _hudStates.Clear();

      foreach (var playerIds in _statusHudPlayerIdsByBuilding.Values)
      {
        var ids = playerIds;
        Facepunch.Pool.FreeUnmanaged(ref ids);
      }

      _statusHudPlayerIdsByBuilding.Clear();
      _hudBuilder.Clear();
    }

    private void RefreshPlayerStatusHud(
      BasePlayer player, HudPlayerState hudState = null)
    {
      if (!player || !player.IsConnected)
        return;

      var nowUtc = System.DateTime.UtcNow;
      var nowRealtime = UnityEngine.Time.realtimeSinceStartup;
      var playerID = player.userID.Get();

      if (hudState is null)
        _hudStates.TryGetValue(playerID, out hudState);

      BaseCombatEntity protectedEntity;
      var hasCommandPrivilege =
        hudState is not null &&
        nowRealtime < hudState.HudExpiresAt &&
        hudState.CommandPrivilege;

      if (hasCommandPrivilege)
      {
        protectedEntity = hudState.CommandPrivilege;
      }
      else
      {
        if (hudState is not null)
        {
          hudState.CommandPrivilege = null;
          hudState.HudExpiresAt = 0f;
        }
        protectedEntity = GetBoatStatusEntity(player) ??
          player.GetBuildingPrivilege();
      }

      if (!protectedEntity)
      {
        if (hudState is null)
          return;

        HideStatusHud(player, hudState);
        SetStatusHudBuilding(playerID, hudState, 0U);
        hudState.PrivilegeRefreshAt =
          nowRealtime + Configuration.StatusHud.RefreshInterval;
        ScheduleStatusHudRefresh(playerID, hudState);

        return;
      }

      if (hudState is null)
      {
        hudState = Facepunch.Pool.Get<HudPlayerState>();
        _hudStates[playerID] = hudState;
      }

      var isTrustedForProtectedEntity = false;
      if (Configuration.TaxProtection.Enabled ||
          (!hasCommandPrivilege &&
           !Configuration.StatusHud.DisplayInTrustedPrivilege))
      {
        isTrustedForProtectedEntity =
          IsTrustedForProtectedEntity(player, protectedEntity);
      }

      if (!hasCommandPrivilege &&
          !Configuration.StatusHud.DisplayInTrustedPrivilege &&
          isTrustedForProtectedEntity)
      {
        HideStatusHud(player, hudState);
        SetStatusHudBuilding(
          playerID, hudState,
          protectedEntity is BuildingPrivlidge privilege ?
            privilege.buildingID : 0U);
        hudState.PrivilegeRefreshAt =
          nowRealtime + Configuration.StatusHud.RefreshInterval;
        ScheduleStatusHudRefresh(playerID, hudState);
        return;
      }

      UpdateStatusHud(
        player, protectedEntity, hudState, nowUtc,
        isTrustedForProtectedEntity);
      hudState.PrivilegeRefreshAt =
        nowRealtime + Configuration.StatusHud.RefreshInterval;

      if (hasCommandPrivilege)
      {
        hudState.HudRefreshAt = System.Math.Min(
          nowRealtime + Configuration.StatusHud.RefreshInterval,
          hudState.HudExpiresAt);
      }
      ScheduleStatusHudRefresh(playerID, hudState);
    }

    private void ShowStatusCommandHud(
      BasePlayer player, in TcState tcState)
    {
      if (!Configuration.StatusHud.Enabled ||
          !Configuration.StatusHud.DisplayOnStatusCommand ||
          !player || !tcState.Privilege)
        return;

      var playerID = player.userID.Get();
      if (!_hudStates.TryGetValue(playerID, out var hudState))
      {
        hudState = Facepunch.Pool.Get<HudPlayerState>();
        _hudStates[playerID] = hudState;
      }

      hudState.CommandPrivilege = tcState.Privilege;
      hudState.HudExpiresAt =
        UnityEngine.Time.realtimeSinceStartup +
        Configuration.StatusHud.Duration;
      hudState.HudRefreshAt = 0f;
      RefreshPlayerStatusHud(player);
    }

    private void UpdateStatusHud(
      BasePlayer player, BaseCombatEntity protectedEntity,
      HudPlayerState hudState, System.DateTime nowUtc,
      bool isTrustedForProtectedEntity)
    {
      if (!player || !protectedEntity)
        return;

      var playerID = player.userID.Get();
      SetStatusHudBuilding(
        playerID, hudState,
        protectedEntity is BuildingPrivlidge privilege ?
          privilege.buildingID : 0U);
      var decision = EvaluateProtection(protectedEntity, null, nowUtc);
      var state = GetProtectionState(in decision);
      var hasTaxProtection = isTrustedForProtectedEntity &&
        HasTaxProtection(in decision, state);
      if (Configuration.StatusHud.DisplayOnlyWhenProtectionActive &&
          (decision.Kind is not DamageDecisionKind.ApplyScale ||
           decision.Scale >= 1f) &&
          !hasTaxProtection)
      {
        HideStatusHud(player, hudState);
        return;
      }

      var snapshot = CreateStatusHudSnapshot(
        GetNetworkID(protectedEntity), in decision, nowUtc,
        hasTaxProtection, out var penaltyEndTicks);
      if (hudState.IsVisible && hudState.HasSnapshot &&
          hudState.Snapshot.Matches(in snapshot))
        return;

#if CARBON
      RenderStatusHud(
        player, in decision, nowUtc, hasTaxProtection, penaltyEndTicks);
#else
      var payload = BuildStatusHudPayload(
        in decision, nowUtc, hasTaxProtection, penaltyEndTicks);
      CuiHelper.DestroyUi(player, STATUS_HUD_NAME);
      CuiHelper.AddUi(player, payload);
#endif
      hudState.Snapshot = snapshot;
      hudState.HasSnapshot = true;
      hudState.IsVisible = true;
    }

    private HudStateSnapshot CreateStatusHudSnapshot(
      ulong targetNetworkID, in DamageDecision decision, System.DateTime nowUtc,
      bool hasTaxProtection, out long penaltyEndTicks)
    {
      var options = Configuration.StatusHud;
      var state = GetProtectionState(in decision);
      var remainingMinutes = 0L;

      if (options.ShowRemainingTime &&
          state is HUDProtectionState.Protected or HUDProtectionState.Partial)
      {
        remainingMinutes =
          (decision.TargetScaleCache?.RemainingTime.Ticks ?? 0L) /
          System.TimeSpan.TicksPerMinute;
      }

      penaltyEndTicks = 0L;
      if (options.ShowPenaltyTimer &&
          state is not HUDProtectionState.Decaying and not HUDProtectionState.Grief &&
          _lastOnline.TryGetValue(decision.TargetID, out var lastOnline) &&
          lastOnline.PenaltyEndTicks > nowUtc.Ticks)
      {
        penaltyEndTicks = lastOnline.PenaltyEndTicks;
      }

      return new(
        targetNetworkID,
        state,
        decision.Scale,
        remainingMinutes,
        penaltyEndTicks > 0L ?
          (penaltyEndTicks - nowUtc.Ticks) / System.TimeSpan.TicksPerSecond :
          0L,
        hasTaxProtection,
        HasOnlineProtection(in decision));
    }

#if !CARBON
    private string BuildStatusHudPayload(
      in DamageDecision decision, System.DateTime nowUtc,
      bool hasTaxProtection, long penaltyEndTicks)
    {
      _hudBuilder.Clear();
      _hudBuilder.Append(_hudPayloadPrefix);
      AppendStatusHudText(
        in decision, nowUtc, hasTaxProtection, penaltyEndTicks);
      _hudBuilder.Append(STATUS_HUD_PAYLOAD_SUFFIX);
      return _hudBuilder.ToString();
    }
#endif

#if CARBON
    private void RenderStatusHud(
      BasePlayer player, in DamageDecision decision, System.DateTime nowUtc,
      bool hasTaxProtection, long penaltyEndTicks)
    {
      _hudBuilder.Clear();
      AppendStatusHudText(
        in decision, nowUtc, hasTaxProtection, penaltyEndTicks);
      CuiHandler.Destroy(STATUS_HUD_NAME, player);

      var lui = CreateCUI().v2;
      var panel = lui.CreatePanel(
        "Hud", _statusHudPosition, _statusHudOffset,
        STATUS_HUD_BACKGROUND_COLOR, STATUS_HUD_NAME);

      var text = lui.CreateText(
        panel, LuiPosition.Full, STATUS_HUD_TEXT_OFFSET,
        STATUS_HUD_HEADER_FONT_SIZE, STATUS_HUD_TEXT_COLOR, _hudBuilder.ToString(),
        TextAnchor.MiddleCenter,
        STATUS_HUD_TEXT_NAME);

      text.SetTextFont(CUI.Handler.FontTypes.RobotoCondensedBold)
        .SetTextOverflow(VerticalWrapMode.Overflow)
        .SetOutline(
          STATUS_HUD_OUTLINE_COLOR, new Vector2(1f, -1f));

      lui.SendUi(player);
    }
#endif

    private void AppendStatusHudText(
      in DamageDecision decision, System.DateTime nowUtc,
      bool hasTaxProtection, long penaltyEndTicks)
    {
      var options = Configuration.StatusHud;
      var state = GetProtectionState(in decision);
      switch (state)
      {
        case HUDProtectionState.Protected:
        case HUDProtectionState.Partial:
          var percent = decision.Scale is 0f ? 100f : decision.Scale.ToPercent();
          AppendStatusHudHeader(
            GetColor(percent), STATUS_HUD_PROTECTED_TEXT, hasTaxProtection);

          if (options.ShowProtectionPercentage)
          {
            var hasOnlineProtection = HasOnlineProtection(in decision);
            _hudBuilder.Append(STATUS_HUD_BODY_PREFIX);
            AppendPercentage(_hudBuilder, percent);
            _hudBuilder.Append('%');
            if (hasOnlineProtection)
              _hudBuilder.Append(" Online");
            _hudBuilder.Append(" Protection</color></size>");
          }
          break;

        case HUDProtectionState.Grief:
        case HUDProtectionState.Decaying:
          AppendStatusHudHeader(
            COLOR_YELLOW,
            state switch
            {
              HUDProtectionState.Grief => STATUS_HUD_GRIEF_TEXT,
              _ => STATUS_HUD_DECAYING_TEXT
            });
          break;

        case HUDProtectionState.IncreasedDamage:
          AppendStatusHudHeader(
            COLOR_GREEN,
            STATUS_HUD_INCREASED_DAMAGE_TEXT);

          if (options.ShowProtectionPercentage)
          {
            _hudBuilder.Append(STATUS_HUD_INCREASED_DAMAGE_PREFIX);
            AppendPercentage(
              _hudBuilder, -decision.Scale.ToPercent());
            _hudBuilder.Append("% Damage</color></size>");
          }
          break;

        case HUDProtectionState.Vulnerable:
        default:
          AppendStatusHudHeader(
            COLOR_GREEN,
            STATUS_HUD_VULNERABLE_TEXT,
            hasTaxProtection);
          break;
      }

      if (decision.TargetID is 0UL)
        return;

      if (options.ShowRemainingTime &&
          state is HUDProtectionState.Protected or
          HUDProtectionState.Partial)
      {
        var remainingTime = decision.TargetScaleCache?.RemainingTime ??
          System.TimeSpan.Zero;

        if (remainingTime != System.TimeSpan.Zero)
        {
          _hudBuilder.Append("<size=")
            .Append(STATUS_HUD_BODY_FONT_SIZE)
            .Append("> (");
          AppendFormattedDuration(
            _hudBuilder,
            remainingTime.Ticks / System.TimeSpan.TicksPerSecond,
            includeDays: true);
          _hudBuilder.Append(")</size>");
        }
      }

      if (!options.ShowPenaltyTimer ||
        state is HUDProtectionState.Decaying
          or HUDProtectionState.Grief ||
          penaltyEndTicks <= nowUtc.Ticks)
        return;

      _hudBuilder.Append(STATUS_HUD_PENALTY_PREFIX);
      AppendFormattedDuration(
        _hudBuilder,
        (penaltyEndTicks - nowUtc.Ticks) / System.TimeSpan.TicksPerSecond,
        includeDays: false);
      _hudBuilder.Append("</color></size>");
    }

    private void AppendStatusHudHeader(
      string color, string status, bool hasTaxProtection = false)
    {
      _hudBuilder
        .Append("<b>")
        .Append(ORP_PREFIX_COLORED)
        .Append("<color=")
        .Append(color)
        .Append('>')
        .Append(status);

      if (hasTaxProtection)
        _hudBuilder.Append(" (Tax)");

      _hudBuilder.Append("</color></b>");
    }

    private static void AppendTwoDigits(StringBuilder builder, int value)
    {
      builder.Append((char)('0' + value / 10));
      builder.Append((char)('0' + value % 10));
    }

    private static void AppendFormattedDuration(
      StringBuilder builder, long totalSeconds, bool includeDays)
    {
      if (includeDays)
      {
        builder.Append(totalSeconds / 86400L).Append("d:")
          .Append(totalSeconds / 3600L % 24L).Append("h:")
          .Append(totalSeconds / 60L % 60L).Append('m');
        return;
      }

      var totalHours = totalSeconds / 3600L;
      if (totalHours < 10L)
        builder.Append('0');

      builder.Append(totalHours).Append(':');
      AppendTwoDigits(builder, (int)(totalSeconds / 60L % 60L));
      builder.Append(':');
      AppendTwoDigits(builder, (int)(totalSeconds % 60L));
    }

    private static void AppendPercentage(
      StringBuilder builder, float percentage)
    {
      var rounded = System.Math.Round(percentage);
      if (System.Math.Abs(percentage - rounded) < 0.0001d)
      {
        builder.Append((int)rounded);
        return;
      }

      builder.Append(percentage.ToString("0.#", CultureInfo.InvariantCulture));
    }

    private static long GetDurationSecondsFromMinutes(long totalMinutes) =>
      totalMinutes > long.MaxValue / 60L ? long.MaxValue :
      totalMinutes < long.MinValue / 60L ? long.MinValue :
      totalMinutes * 60L;

    private void HideStatusHud(BasePlayer player)
    {
      if (player)
#if CARBON
        CuiHandler.Destroy(STATUS_HUD_NAME, player);
#else
        CuiHelper.DestroyUi(player, STATUS_HUD_NAME);
#endif
    }

    private void HideStatusHud(
      BasePlayer player, HudPlayerState hudState)
    {
      if (hudState.IsVisible)
        HideStatusHud(player);

      hudState.HasSnapshot = false;
      hudState.IsVisible = false;
    }

    private void RemoveStatusHud(
      BasePlayer player, ulong playerID, HudPlayerState hudState)
    {
      HideStatusHud(player, hudState);
      SetStatusHudBuilding(playerID, hudState, 0U);
      _hudStates.Remove(playerID);
      Facepunch.Pool.Free(ref hudState);
    }

    private void SetStatusHudBuilding(
      ulong playerID, HudPlayerState hudState, uint buildingID)
    {
      var previousBuildingID = hudState.BuildingID;
      if (previousBuildingID == buildingID)
        return;

      if (previousBuildingID is not 0U &&
          _statusHudPlayerIdsByBuilding.TryGetValue(
            previousBuildingID, out var previousPlayerIds))
      {
        previousPlayerIds.Remove(playerID);
        if (previousPlayerIds.Count is 0)
        {
          _statusHudPlayerIdsByBuilding.Remove(previousBuildingID);
          Facepunch.Pool.FreeUnmanaged(ref previousPlayerIds);
        }
      }

      hudState.BuildingID = buildingID;
      if (buildingID is 0U)
        return;

      if (!_statusHudPlayerIdsByBuilding.TryGetValue(
            buildingID, out var playerIds))
      {
        playerIds = Facepunch.Pool.Get<HashSet<ulong>>();
        _statusHudPlayerIdsByBuilding[buildingID] = playerIds;
      }
      playerIds.Add(playerID);
    }

    private void RefreshStatusHudScheduler()
    {
      if (!Configuration.StatusHud.Enabled || _statusHudDueQueue.Count is 0)
        return;

      var nowRealtime = UnityEngine.Time.realtimeSinceStartup;
      var dueTick = GetStatusHudScheduleTick(nowRealtime);
      while (_statusHudDueQueue.Count is not 0)
      {
        if (!TryGetFirstDueEntry(out var nextTick, out var dueEntries) ||
          nextTick > dueTick)
          return;

        _statusHudDueQueue.Remove(nextTick);
        for (var i = 0; i < dueEntries.Count; i++)
        {
          var entry = dueEntries[i];
          if (!_hudStates.TryGetValue(entry.PlayerID, out var hudState) ||
              hudState.ScheduleGeneration != entry.Generation)
            continue;

          RefreshScheduledStatusHudPlayer(entry.PlayerID, hudState);
        }
        dueEntries.Clear();
        _statusHudDueListPool.Push(dueEntries);
      }
    }

    private void RefreshScheduledStatusHudPlayer(
      ulong playerID, HudPlayerState hudState)
    {
      var player = _players.GetPlayer(playerID);
      if (!player || !player.IsConnected)
      {
        RemoveStatusHud(player, playerID, hudState);
        return;
      }

      RefreshPlayerStatusHud(player, hudState);
    }

    private static int GetStatusHudScheduleTick(float realtime) =>
      (int)System.Math.Ceiling(realtime / STATUS_HUD_SCHEDULER_INTERVAL);

    private bool TryGetFirstDueEntry(out int nextTick, out List<HudScheduleEntry> dueEntries)
    {
      foreach (var entry in _statusHudDueQueue)
      {
        nextTick = entry.Key;
        dueEntries = entry.Value;
        return true;
      }

      nextTick = int.MaxValue;
      dueEntries = null;
      return false;
    }

    private void ScheduleStatusHudRefresh(
      ulong playerID, HudPlayerState hudState)
    {
      if (hudState is null)
        return;

      var dueAt = hudState.PrivilegeRefreshAt;
      if (hudState.CommandPrivilege)
      {
        dueAt = System.Math.Min(dueAt, hudState.HudExpiresAt);
        if (hudState.HudRefreshAt > 0f)
          dueAt = System.Math.Min(dueAt, hudState.HudRefreshAt);
      }

      var dueTick = GetStatusHudScheduleTick(dueAt);
      hudState.ScheduleGeneration = ++_statusHudScheduleGeneration;
      if (!_statusHudDueQueue.TryGetValue(dueTick, out var dueEntries))
      {
        dueEntries = _statusHudDueListPool.Count > 0 ?
          _statusHudDueListPool.Pop() : new();
        _statusHudDueQueue[dueTick] = dueEntries;
      }
      dueEntries.Add(new(playerID, hudState.ScheduleGeneration));
    }

    private void QueueStatusHudRefresh(BasePlayer player)
    {
      if (!_serverInitialized || !Configuration.StatusHud.Enabled || !player ||
          !player.IsConnected)
        return;

      _queuedStatusHudPlayerIds.Add(player.userID.Get());
      if (_statusHudRefreshQueued)
        return;

      _statusHudRefreshQueued = true;
      NextFrame(_refreshQueuedStatusHudsAction);
    }

    private void QueueCupboardStatusHudRefresh(
      BuildingPrivlidge buildingPrivlidge)
    {
      if (!buildingPrivlidge)
        return;

      QueueBuildingStatusHudRefresh(buildingPrivlidge.buildingID);
    }

    private void QueueBuildingStatusHudRefresh(uint buildingID)
    {
      if (!_serverInitialized || buildingID is 0U ||
          !Configuration.StatusHud.Enabled)
        return;

      if (_statusHudPlayerIdsByBuilding.TryGetValue(
            buildingID, out var playerIds))
        _queuedStatusHudPlayerIds.UnionWith(playerIds);

      if (_queuedStatusHudPlayerIds.Count is 0 || _statusHudRefreshQueued)
        return;

      _statusHudRefreshQueued = true;
      NextFrame(_refreshQueuedStatusHudsAction);
    }

    private void RefreshQueuedStatusHuds()
    {
      _statusHudRefreshQueued = false;
      foreach (var playerID in _queuedStatusHudPlayerIds)
      {
        var player = _players.GetPlayer(playerID);
        if (!player || !player.IsConnected)
        {
          if (_hudStates.TryGetValue(playerID, out var hudState))
            RemoveStatusHud(player, playerID, hudState);
          continue;
        }
        RefreshPlayerStatusHud(player);
      }
      _queuedStatusHudPlayerIds.Clear();
    }

#endregion Methods

#region Helper Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HUDProtectionState GetProtectionState(
      in DamageDecision decision) =>
      decision switch
      {
        { IsGrief: true } => HUDProtectionState.Grief,
        { IsDecaying: true } => HUDProtectionState.Decaying,
        { Kind: not DamageDecisionKind.ApplyScale } or { Scale: 1f } => HUDProtectionState.Vulnerable,
        { Scale: <= 0f } => HUDProtectionState.Protected,
        { Scale: < 1f } => HUDProtectionState.Partial,
        _ => HUDProtectionState.IncreasedDamage
      };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasTaxProtection(
      in DamageDecision decision, HUDProtectionState state) =>
      decision.TaxProtectionGated ||
      decision.PurchasedProtectionEndTicks > 0L &&
      (state is HUDProtectionState.Protected or HUDProtectionState.Partial);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasOnlineProtection(in DamageDecision decision) =>
      decision.TargetScaleCache?.HasOnlineProtectPermission is true;

    private bool IsTrustedForCupboard(
      BasePlayer player, BuildingPrivlidge privilege)
    {
      if (!player || !privilege)
        return false;

      var authorizedPlayers = GetTotalAuthorizedPlayers(privilege);
      return IsTrustedForAuthorizedPlayers(player.userID.Get(), authorizedPlayers);
    }

    private static BaseCombatEntity GetBoatStatusEntity(BasePlayer player)
    {
      if (!Configuration.RaidProtection.ProtectBaseBoats || !player)
        return null;

      var vehiclePrivilege =
        player.GetVehicleBuildingPrivilege(false, 0f) as VehiclePrivilege;

      return vehiclePrivilege?.ParentVehicle switch
      {
        Tugboat boat => boat,
        PlayerBoat boat => boat,
        _ => null
      };
    }

    private bool IsTrustedForProtectedEntity(
      BasePlayer player, BaseCombatEntity protectedEntity)
    {
      if (protectedEntity is BuildingPrivlidge privilege)
        return IsTrustedForCupboard(player, privilege);

      var (tugboat, modularBoat, vehicle) = GetVehicle(protectedEntity);
      _tmpIdSetScratch.Clear();
      if (!GetAuthorizedPlayers(
            protectedEntity, tugboat, modularBoat, vehicle, null,
            _tmpIdSetScratch, out _) || _tmpIdSetScratch.Overflowed)
        return false;

      return IsTrustedForAuthorizedPlayers(
        player.userID.Get(), _tmpIdSetScratch);
    }

    private bool IsTrustedForAuthorizedPlayers(
      ulong playerID, PlayerIdSet authorizedPlayers)
    {
      // If nobody is authorized on TC or Code Locks, treat anyone in range as "trusted"
      if (authorizedPlayers is null || authorizedPlayers.Count is 0)
        return true;

      if (authorizedPlayers.Contains(playerID))
        return true;

      if (!Configuration.Team.TeamShare)
        return false;

      for (var i = 0; i < authorizedPlayers.Count; i++)
      {
        if (ArePlayersRelated(playerID, authorizedPlayers[i]))
          return true;
      }

      return false;
    }

    private bool ArePlayersRelated(ulong firstPlayerID, ulong secondPlayerID)
    {
      if (firstPlayerID == secondPlayerID)
        return true;

      var firstPlayer = _players.GetPlayer(firstPlayerID);
      var secondPlayer = _players.GetPlayer(secondPlayerID);

      if (firstPlayer is { currentTeam: not 0 } &&
          secondPlayer is { currentTeam: not 0 } &&
          firstPlayer.currentTeam == secondPlayer.currentTeam)
        return true;

      if (!firstPlayer || !secondPlayer)
      {
        var team = GetTeam(firstPlayerID);
        if (team?.members.Contains(secondPlayerID) is true)
          return true;
      }

      if (firstPlayer is { clanId: not 0 } &&
          secondPlayer is { clanId: not 0 } &&
          firstPlayer.clanId == secondPlayer.clanId)
        return true;

      if (!secondPlayer &&
          firstPlayer is { serverClan: not null, clanId: not 0 })
      {
        foreach (var clanMember in firstPlayer.serverClan.Members)
        {
          if (clanMember.SteamId == secondPlayerID)
            return true;
        }
      }

      if (Clans is null)
        return false;

      var firstTag = GetCachedClanTag(firstPlayerID);
      return !string.IsNullOrEmpty(firstTag) &&
        string.Equals(
          firstTag, GetCachedClanTag(secondPlayerID),
          System.StringComparison.Ordinal);
    }

    private void RefreshCupboardAuthorizationViews(
      BuildingPrivlidge buildingPrivlidge, BasePlayer player)
    {
      InvalidateMapMarkerAuthorizedPlayers();
      if (buildingPrivlidge)
      {
        QueueGriefTopologyRefresh(buildingPrivlidge.buildingID);
        QueueTaxProtectionSync(buildingPrivlidge.buildingID);
      }
      UpdateTcMarkerLabel(buildingPrivlidge);
      QueueCupboardStatusHudRefresh(buildingPrivlidge);
      QueueStatusHudRefresh(player);
    }

    private void RefreshProtectionViews(ulong userID)
    {
      QueueTaxProtectionSync();
      QueueStatusHudRefresh(_players.GetPlayer(userID));
      QueueMapMarkerRefresh();
    }

    private void RefreshAllProtectionViews()
    {
      QueueTaxProtectionSync();
      foreach (var player in BasePlayer.activePlayerList)
        QueueStatusHudRefresh(player);
      QueueMapMarkerRefresh();
    }

#endregion Helper Methods

#endregion Status HUD

#region ORP DDraw

#region Constants

    private const int DDRAW_MAX_RENDER = 32;
    private const int DDRAW_INITIAL_SESSION_CAPACITY = 4;
    private const int DDRAW_INITIAL_LABEL_CACHE_CAPACITY = 64;
    private const float DDRAW_REFRESH_INTERVAL = 3f;
    private const float DDRAW_DURATION = 3.15f;
    private const float DDRAW_MIN_RANGE = 10f;
    private const float DDRAW_MAX_RANGE = 1000f;
    private const float DDRAW_TEXT_SCALE = 1.15f;
    private const float DDRAW_TEXT_HEIGHT = 2.5f;
    private const string DDRAW_TEXT_COMMAND = "ddraw.text";
    private const string DDRAW_DISABLE_ARGUMENT = "off";

#endregion Constants

#region Types

    private readonly struct DdrawSession
    {
      public readonly float Range;
      public readonly float SqrRange;

      public DdrawSession(float range)
      {
        Range = range;
        SqrRange = range * range;
      }
    }

    private readonly struct DdrawCandidate
    {
      public readonly BaseCombatEntity ProtectedEntity;
      public readonly ulong NetworkID;
      public readonly float SqrDistance;
      public readonly Vector3 Position;
      public readonly bool IsBoat;

      public DdrawCandidate(
        BaseCombatEntity protectedEntity, ulong networkID,
        float sqrDistance, Vector3 position,
        bool isBoat)
      {
        ProtectedEntity = protectedEntity;
        NetworkID = networkID;
        SqrDistance = sqrDistance;
        Position = position;
        IsBoat = isBoat;
      }
    }

    private struct DdrawLabelCacheEntry
    {
      public long TaxMinutes;
      public long PenaltySeconds;
      public long RemainingMinutes;
      public string Label;
      public object BoxedColor;
      public object BoxedTextPosition;
      public Vector3 CachedPosition;
      public float Scale;
      public int AuthorizedCount;
      public uint LastEvaluatedGeneration;
      public HUDProtectionState ProtectionState;
      public bool HasRemainingTime;
      public bool IsBoat;

      public readonly bool NeedsLabelRefresh(
        HUDProtectionState protectionState, float scale,
        long taxMinutes, long penaltySeconds, long remainingMinutes,
        bool hasRemainingTime, bool isBoat, int authorizedCount)
      {
        return Label is null ||
          ProtectionState != protectionState ||
          Scale != scale ||
          TaxMinutes != taxMinutes ||
          PenaltySeconds != penaltySeconds ||
          RemainingMinutes != remainingMinutes ||
          HasRemainingTime != hasRemainingTime ||
          IsBoat != isBoat ||
          AuthorizedCount != authorizedCount;
      }
    }

#endregion Types

#region Fields

    private readonly Dictionary<ulong, DdrawSession>
      _ddrawSessions = new(DDRAW_INITIAL_SESSION_CAPACITY);
    private readonly Dictionary<ulong, DdrawLabelCacheEntry>
      _ddrawLabelCache = new(DDRAW_INITIAL_LABEL_CACHE_CAPACITY);
    private readonly List<ulong>
      _ddrawLabelCacheRemovalScratch =
        new(DDRAW_INITIAL_LABEL_CACHE_CAPACITY);
    private readonly List<ulong>
      _ddrawSessionRemovalScratch =
        new(DDRAW_INITIAL_SESSION_CAPACITY);
    private readonly DdrawCandidate[] _ddrawHeap =
      new DdrawCandidate[DDRAW_MAX_RENDER];
    private readonly HashSet<BaseVehicle> _ddrawBoats = new();
    private readonly StringBuilder _ddrawLabelBuilder = new(128);
    private readonly object[] _ddrawTextArgs = new object[7];

    private static readonly object
      BoxedDdrawDuration = DDRAW_DURATION,
      BoxedDdrawTextScale = DDRAW_TEXT_SCALE;

    private int _ddrawHeapCount;
    private uint _ddrawRefreshGeneration;
    private PluginTimer _ddrawTimer;
    private System.Action _ddrawTimerAction;
    private object _boxedDdrawProtectedColor;
    private object _boxedDdrawPartialColor;
    private object _boxedDdrawVulnerableColor;
    private object _boxedDdrawDecayingColor;
    private object _boxedDdrawGriefColor;

#endregion Fields

#region Lifecycle

    private void CacheDdrawColors()
    {
      _boxedDdrawProtectedColor = _markerProtectedColor;
      _boxedDdrawPartialColor = _markerPartialColor;
      _boxedDdrawVulnerableColor = _markerVulnerableColor;
      _boxedDdrawDecayingColor = _markerDecayingColor;
      _boxedDdrawGriefColor = _markerGriefColor;
    }

    private void AcquireDdrawHooks()
    {
      if (_ddrawSessions.Count is not 1)
        return;

      Subscribe(nameof(OnCupboardProtectionCalculated));
      Subscribe(nameof(OnEntitySpawned));
      Subscribe(nameof(OnEntityKill));
      Subscribe(nameof(OnBuildingSplit));
      Subscribe(nameof(OnBuildingMerge));

      if (!RequiresTcCacheWithoutDdraw)
        CacheAllCupboardsPreservingCodeLockWhitelist();

      CacheDdrawBoats();

      _ddrawTimerAction ??= ProcessDdrawTick;
      _ddrawTimer = timer.Every(
        DDRAW_REFRESH_INTERVAL, _ddrawTimerAction);
    }

    private void ReleaseDdrawHooks()
    {
      if (_ddrawSessions.Count is not 0)
        return;

      _ddrawTimer?.Destroy();
      _ddrawTimer = null;
      ClearDdrawWorldState();
      _ddrawSessionRemovalScratch.Clear();
      _ddrawLabelBuilder.Clear();
      System.Array.Clear(
        _ddrawTextArgs, 0, _ddrawTextArgs.Length);

      var needsTracking = NeedsTcTracking;
      var needsTaxProtectionRefunds =
        Configuration.TaxProtection.Enabled &&
        Configuration.TaxProtection.RefundOnDestruction;

      if (Configuration.RaidProtection.ProtectDecayingBase &&
          Configuration.RaidProtection.ProtectGriefTcs &&
          !needsTracking)
        Unsubscribe(nameof(OnCupboardProtectionCalculated));

      if (Configuration.RaidProtection.ProtectGriefTcs &&
          !needsTracking &&
          !Configuration.Team.IncludeWhitelistPlayers &&
          !needsTaxProtectionRefunds)
      {
        Unsubscribe(nameof(OnEntitySpawned));
        Unsubscribe(nameof(OnEntityKill));
        Unsubscribe(nameof(OnBuildingSplit));
        Unsubscribe(nameof(OnBuildingMerge));
      }

      if (RequiresTcCache)
        return;

      _tcCache.Clear();
      ClearGriefCupboardIndex();
      ClearQueuedTcCacheRefreshes();
    }

    private void UnloadDdraw()
    {
      _ddrawTimer?.Destroy();
      _ddrawTimer = null;
      _ddrawTimerAction = null;
      _ddrawSessions.Clear();
      ClearDdrawWorldState();
      _ddrawSessionRemovalScratch.Clear();
      _ddrawLabelBuilder.Clear();
      System.Array.Clear(
        _ddrawTextArgs, 0, _ddrawTextArgs.Length);
      _boxedDdrawProtectedColor = null;
      _boxedDdrawPartialColor = null;
      _boxedDdrawVulnerableColor = null;
      _boxedDdrawDecayingColor = null;
      _boxedDdrawGriefColor = null;
    }

    private void ClearDdrawWorldState()
    {
      _ddrawLabelCache.Clear();
      _ddrawLabelCacheRemovalScratch.Clear();
      _ddrawBoats.Clear();
      ClearDdrawSelection();
      _ddrawRefreshGeneration = 0U;
    }

    private void EndDdrawSession(ulong userID)
    {
      if (_ddrawSessions.Remove(userID))
        ReleaseDdrawHooks();
    }

    private void RemoveUnauthorizedDdrawSessions()
    {
      if (_ddrawSessions.Count is 0)
        return;

      _ddrawSessionRemovalScratch.Clear();
      foreach (var userID in _ddrawSessions.Keys)
      {
        if (!IsAuthorizedDdrawPlayer(_players.GetPlayer(userID)))
          _ddrawSessionRemovalScratch.Add(userID);
      }

      for (var i = 0; i < _ddrawSessionRemovalScratch.Count; i++)
        EndDdrawSession(_ddrawSessionRemovalScratch[i]);
    }

#endregion Lifecycle

#region Selection

    private void SelectNearestDdrawCupboards(
      Vector3 adminPosition, float maxSqrDistance)
    {
      _ddrawHeapCount = 0;
      foreach (var tcState in _tcCache.Values)
      {
        var privilege = tcState.Privilege;
        if (!privilege || tcState.CupboardNetworkID is 0UL)
          continue;

        var position = privilege.transform.position;
        var sqrDistance = (position - adminPosition).sqrMagnitude;
        if (sqrDistance > maxSqrDistance)
          continue;

        // Match protection evaluation: an NPC-first TC authorization
        // identifies a cupboard that ORP intentionally ignores
        var firstAuthorizedPlayerID = 0UL;
        if (privilege.authorizedPlayers is { } authorizedPlayers)
        {
          foreach (var playerID in authorizedPlayers)
          {
            if (playerID is 0UL)
              continue;

            firstAuthorizedPlayerID = playerID;
            break;
          }
        }
        if (firstAuthorizedPlayerID is not 0UL &&
            !firstAuthorizedPlayerID.IsSteamID())
          continue;

        AddDdrawCandidate(
          privilege, tcState.CupboardNetworkID, sqrDistance,
          position, false);
      }

      if (!Configuration.RaidProtection.ProtectBaseBoats)
        return;

      foreach (var boat in _ddrawBoats)
      {
        if (!boat)
          continue;

        var vehiclePrivilege = GetDdrawBoatPrivilege(boat);
        if (!vehiclePrivilege)
          continue;

        var position = vehiclePrivilege.transform.position;
        var sqrDistance = (position - adminPosition).sqrMagnitude;
        if (sqrDistance <= maxSqrDistance)
          AddDdrawCandidate(
            boat, GetNetworkID(boat), sqrDistance, position, true);
      }
    }

    private void AddDdrawCandidate(
      BaseCombatEntity protectedEntity, ulong networkID,
      float sqrDistance, Vector3 position, bool isBoat)
    {
      if (networkID is 0UL)
        return;

      var candidate = new DdrawCandidate(
        protectedEntity, networkID, sqrDistance, position, isBoat);
      if (_ddrawHeapCount < DDRAW_MAX_RENDER)
      {
        _ddrawHeap[_ddrawHeapCount] = candidate;
        SiftDdrawHeapUp(_ddrawHeapCount++);
      }
      else if (sqrDistance < _ddrawHeap[0].SqrDistance)
      {
        _ddrawHeap[0] = candidate;
        SiftDdrawHeapDown(0);
      }
    }

    private void SiftDdrawHeapUp(int index)
    {
      var candidate = _ddrawHeap[index];
      while (index > 0)
      {
        var parent = (index - 1) >> 1;
        if (_ddrawHeap[parent].SqrDistance >=
            candidate.SqrDistance)
          break;

        _ddrawHeap[index] = _ddrawHeap[parent];
        index = parent;
      }
      _ddrawHeap[index] = candidate;
    }

    private void SiftDdrawHeapDown(int index)
    {
      var candidate = _ddrawHeap[index];
      while (true)
      {
        var left = (index << 1) + 1;
        if (left >= _ddrawHeapCount)
          break;

        var right = left + 1;
        var largest = right < _ddrawHeapCount &&
                      _ddrawHeap[right].SqrDistance >
                      _ddrawHeap[left].SqrDistance ? right : left;
        if (_ddrawHeap[largest].SqrDistance <=
            candidate.SqrDistance)
          break;

        _ddrawHeap[index] = _ddrawHeap[largest];
        index = largest;
      }
      _ddrawHeap[index] = candidate;
    }

    private void ClearDdrawSelection()
    {
      if (_ddrawHeapCount is 0)
        return;

      System.Array.Clear(_ddrawHeap, 0, _ddrawHeapCount);
      _ddrawHeapCount = 0;
    }

#endregion Selection

#region Evaluation and Label Caching

    private void RenderDdrawSelection(
      BasePlayer player, System.DateTime nowUtc)
    {
      for (var i = 0; i < _ddrawHeapCount; i++)
      {
        var candidate = _ddrawHeap[i];
        var protectedEntity = candidate.ProtectedEntity;
        if (!protectedEntity)
          continue;

        var netID = candidate.NetworkID;
        _ddrawLabelCache.TryGetValue(netID, out var entry);
        var dirty = false;

        if (entry.LastEvaluatedGeneration !=
            _ddrawRefreshGeneration)
        {
          var decision = EvaluateProtection(
            protectedEntity, null, nowUtc,
            playerIdSetScratch: _tmpIdSetScratch);
          var state = GetProtectionState(in decision);
          var remainingTaxTicks =
            decision.PurchasedProtectionEndTicks - nowUtc.Ticks;
          var taxMinutes = remainingTaxTicks > 0L ?
            (remainingTaxTicks + System.TimeSpan.TicksPerMinute - 1L) /
            System.TimeSpan.TicksPerMinute : 0L;
          var penaltySeconds = 0L;
          if (state is not HUDProtectionState.Decaying and
              not HUDProtectionState.Grief &&
              decision.TargetID is not 0UL &&
              _lastOnline.TryGetValue(
                decision.TargetID, out var lastOnline) &&
              lastOnline.PenaltyEndTicks > nowUtc.Ticks)
          {
            var remainingPenaltyTicks =
              lastOnline.PenaltyEndTicks - nowUtc.Ticks;
            penaltySeconds =
              (remainingPenaltyTicks +
               System.TimeSpan.TicksPerSecond - 1L) /
              System.TimeSpan.TicksPerSecond;
          }
          var remainingTime = state is HUDProtectionState.Protected or
            HUDProtectionState.Partial ?
            decision.TargetScaleCache?.RemainingTime ??
            System.TimeSpan.Zero : System.TimeSpan.Zero;
          var hasRemainingTime = taxMinutes is 0L &&
            remainingTime != System.TimeSpan.Zero;
          var remainingMinutes = hasRemainingTime ?
            remainingTime.Ticks / System.TimeSpan.TicksPerMinute : 0L;
          var authorizedCount = decision.IsGrief ?
            GetTotalAuthorizedPlayers(
              protectedEntity as BuildingPrivlidge)?.Count ?? 0 :
            _tmpIdSetScratch.Count;

          if (entry.NeedsLabelRefresh(
                state, decision.Scale, taxMinutes, penaltySeconds,
                remainingMinutes,
                hasRemainingTime, candidate.IsBoat, authorizedCount))
          {
            entry.ProtectionState = state;
            entry.Scale = decision.Scale;
            entry.TaxMinutes = taxMinutes;
            entry.PenaltySeconds = penaltySeconds;
            entry.RemainingMinutes = remainingMinutes;
            entry.HasRemainingTime = hasRemainingTime;
            entry.IsBoat = candidate.IsBoat;
            entry.AuthorizedCount = authorizedCount;
            entry.BoxedColor = GetBoxedDdrawColor(state);
            entry.Label = BuildDdrawLabel(
              netID, candidate.IsBoat, state, decision.Scale,
              taxMinutes, penaltySeconds, hasRemainingTime,
              remainingMinutes, authorizedCount);
          }

          entry.LastEvaluatedGeneration =
            _ddrawRefreshGeneration;
          dirty = true;
        }

        if (candidate.Position != entry.CachedPosition)
        {
          entry.CachedPosition = candidate.Position;
          entry.BoxedTextPosition = null;
          dirty = true;
        }

        if (entry.BoxedTextPosition is null)
        {
          entry.BoxedTextPosition =
            candidate.Position + Vector3.up *
            DDRAW_TEXT_HEIGHT;
          dirty = true;
        }

        if (dirty)
          _ddrawLabelCache[netID] = entry;

        SendDdraw(player, in entry);
      }
    }

    private object GetBoxedDdrawColor(
      HUDProtectionState state) => state switch
      {
        HUDProtectionState.Protected =>
          _boxedDdrawProtectedColor,
        HUDProtectionState.Partial =>
          _boxedDdrawPartialColor,
        HUDProtectionState.Decaying =>
          _boxedDdrawDecayingColor,
        HUDProtectionState.Grief =>
          _boxedDdrawGriefColor,
        _ => _boxedDdrawVulnerableColor
      };

    private void PruneDdrawLabelCache()
    {
      _ddrawLabelCacheRemovalScratch.Clear();
      foreach (var (networkID, entry) in _ddrawLabelCache)
      {
        if (entry.LastEvaluatedGeneration !=
            _ddrawRefreshGeneration)
          _ddrawLabelCacheRemovalScratch.Add(networkID);
      }

      for (var i = 0; i < _ddrawLabelCacheRemovalScratch.Count; i++)
        _ddrawLabelCache.Remove(
          _ddrawLabelCacheRemovalScratch[i]);
    }

    private string BuildDdrawLabel(
      ulong netID, bool isBoat, HUDProtectionState state, float scale,
      long taxMinutes, long penaltySeconds, bool hasRemainingTime,
      long remainingMinutes, int authorizedCount)
    {
      _ddrawLabelBuilder.Clear();
      _ddrawLabelBuilder.Append(isBoat ? "BOAT " : "TC ")
        .Append(netID).Append('\n');

      switch (state)
      {
        case HUDProtectionState.Protected:
          _ddrawLabelBuilder.Append("PROTECTED 100%");
          break;
        case HUDProtectionState.Partial:
          _ddrawLabelBuilder.Append("PROTECTED ");
          AppendPercentage(
            _ddrawLabelBuilder,
            scale <= 0f ? 100f : scale.ToPercent());
          _ddrawLabelBuilder.Append('%');
          break;
        case HUDProtectionState.IncreasedDamage:
          _ddrawLabelBuilder.Append('+');
          AppendPercentage(
            _ddrawLabelBuilder, -scale.ToPercent());
          _ddrawLabelBuilder.Append("% DAMAGE");
          break;
        case HUDProtectionState.Decaying:
          _ddrawLabelBuilder.Append("DECAYING");
          break;
        case HUDProtectionState.Grief:
          _ddrawLabelBuilder.Append("GRIEF");
          break;
        default:
          _ddrawLabelBuilder.Append("VULNERABLE");
          break;
      }

      _ddrawLabelBuilder.Append('\n');
      if (penaltySeconds > 0L)
      {
        _ddrawLabelBuilder.Append("PENALTY: ");
        AppendFormattedDuration(
          _ddrawLabelBuilder, penaltySeconds, includeDays: false);
        _ddrawLabelBuilder.Append('\n');
      }
      if (hasRemainingTime)
      {
        _ddrawLabelBuilder.Append("REMAINING: ");
        AppendFormattedDuration(
          _ddrawLabelBuilder,
          GetDurationSecondsFromMinutes(remainingMinutes), includeDays: true);
        _ddrawLabelBuilder.Append('\n');
      }
      if (taxMinutes > 0L)
      {
        _ddrawLabelBuilder.Append("TAX: ");
        AppendFormattedDuration(
          _ddrawLabelBuilder,
          GetDurationSecondsFromMinutes(taxMinutes), includeDays: true);
        _ddrawLabelBuilder.Append('\n');
      }
      _ddrawLabelBuilder.Append("AUTH: ")
        .Append(authorizedCount);

      return _ddrawLabelBuilder.ToString();
    }

#endregion Evaluation and Label Caching

#region Rendering

    private void ProcessDdrawTick()
    {
      if (_ddrawSessions.Count is 0)
        return;

      unchecked
      {
        if (++_ddrawRefreshGeneration is 0U)
          _ddrawRefreshGeneration = 1U;
      }

      var nowUtc = System.DateTime.UtcNow;
      _ddrawSessionRemovalScratch.Clear();
      foreach (var (userID, session) in _ddrawSessions)
      {
        var player = _players.GetPlayer(userID);
        if (!IsAuthorizedDdrawPlayer(player))
        {
          _ddrawSessionRemovalScratch.Add(userID);
          continue;
        }

        try
        {
          SelectNearestDdrawCupboards(
            player.transform.position, session.SqrRange);
          RenderDdrawSelection(player, nowUtc);
        }
        finally
        {
          ClearDdrawSelection();
        }
      }

      PruneDdrawLabelCache();

      for (var i = 0; i < _ddrawSessionRemovalScratch.Count; i++)
        EndDdrawSession(_ddrawSessionRemovalScratch[i]);
    }

    private void SendDdraw(
      BasePlayer player, in DdrawLabelCacheEntry entry)
    {
      _ddrawTextArgs[0] = BoxedDdrawDuration;
      _ddrawTextArgs[1] = entry.BoxedColor;
      _ddrawTextArgs[2] = entry.BoxedTextPosition;
      _ddrawTextArgs[3] = entry.Label;
      _ddrawTextArgs[4] = BoxedTrue;
      _ddrawTextArgs[5] = BoxedFalse;
      _ddrawTextArgs[6] = BoxedDdrawTextScale;
      player.SendConsoleCommand(
        DDRAW_TEXT_COMMAND, _ddrawTextArgs);
    }

#endregion Rendering

#region Console Command

    private void ccOrpDdraw(ConsoleSystem.Arg arg)
    {
      if (arg is null)
      {
        PrintError("ccOrpDdraw(): arg is null");
        return;
      }

      if (arg.Connection?.player is not BasePlayer player)
      {
        Reply(arg,
          "This command must be called from the in-game F1 console.");
        return;
      }

      if (!IsAuthorizedDdrawPlayer(player))
      {
        Reply(arg, "Access denied.");
        return;
      }

      var argumentCount = arg.Args?.Length ?? 0;
      var userID = player.userID.Get();
      if (argumentCount is 0)
      {
        Reply(arg, _ddrawSessions.TryGetValue(userID, out var currentSession)
          ? $"DDraw is enabled [Range: {currentSession.Range:0}m].\n{GetDdrawUsage()}"
          : $"DDraw is disabled.\n{GetDdrawUsage()}");
        return;
      }

      if (argumentCount is not 1)
      {
        Reply(arg, GetDdrawUsage());
        return;
      }

      var rangeArgument = arg.GetString(0, string.Empty);
      if (string.Equals(
            rangeArgument, DDRAW_DISABLE_ARGUMENT,
            System.StringComparison.OrdinalIgnoreCase))
      {
        var wasEnabled = _ddrawSessions.ContainsKey(userID);
        EndDdrawSession(userID);
        Reply(arg, wasEnabled ?
          "DDraw disabled." : "DDraw is already disabled.");
        return;
      }

      var range = arg.GetFloat(0, float.NaN);
      if (!float.IsFinite(range) ||
          range is < DDRAW_MIN_RANGE or > DDRAW_MAX_RANGE)
      {
        Reply(arg,
          $"Range must be between {DDRAW_MIN_RANGE:0} and {DDRAW_MAX_RANGE:0} metres.");
        return;
      }

      var acquireCache = _ddrawSessions.Count is 0;
      _ddrawSessions[userID] =
        new DdrawSession(range);
      if (acquireCache)
        AcquireDdrawHooks();

      Reply(arg,
        $"DDraw enabled [Range: {range:0}m].");
    }

#endregion Console Command

#region Helpers

    private void CacheDdrawBoats()
    {
      _ddrawBoats.Clear();
      if (!Configuration.RaidProtection.ProtectBaseBoats)
        return;

      foreach (var entity in BaseNetworkable.serverEntities)
      {
        if (entity is Tugboat or PlayerBoat)
          _ddrawBoats.Add(entity as BaseVehicle);
      }
    }

    private void TrackDdrawBoat(BaseVehicle boat)
    {
      if (_ddrawSessions.Count is not 0 && boat)
        _ddrawBoats.Add(boat);
    }

    private void UntrackDdrawBoat(BaseVehicle boat)
    {
      if (boat)
        _ddrawBoats.Remove(boat);
    }

    private void RemoveDdrawLabelCacheEntry(
      BaseNetworkable entity)
    {
      var networkID = GetNetworkID(entity);
      if (networkID is not 0UL)
        _ddrawLabelCache.Remove(networkID);
    }

    private static VehiclePrivilege GetDdrawBoatPrivilege(
      BaseVehicle boat) => boat is Tugboat or PlayerBoat ?
        boat.GetChildPrivilege() : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAuthorizedDdrawPlayer(
      BasePlayer player) =>
      player && player.IsConnected &&
      player.Connection?.authLevel > 0 &&
      player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin) &&
      _adminIDCache.Contains(player.userID.Get());

    private static string GetDdrawUsage() =>
      $"Usage: {Configuration.Command.CommandOrpDdraw} [range] | {Configuration.Command.CommandOrpDdraw} off";

#endregion Helpers

#endregion ORP DDraw

#region Map Markers

#region Fields

    private readonly Dictionary<ulong, TcMapMarkerGroup> _mapMarkersByCupboard = new();
    private readonly Dictionary<BaseVehicle, ulong> _mapMarkerIdsByBoat = new();
    private readonly HashSet<ulong> _activeMarkerNetIds = new();
    private readonly HashSet<BaseNetworkable> _pendingMapMarkers = new();
    private readonly Queue<uint> _queuedBuildingMapMarkerSyncs = new(64);
    private readonly HashSet<uint> _queuedBuildingMapMarkerSyncIds = new(64);
    private readonly Queue<ulong> _queuedBoatMapMarkerSyncs = new(32);
    private readonly HashSet<ulong> _queuedBoatMapMarkerSyncIds = new(32);
    private readonly Queue<ulong> _queuedMapMarkerAuthorizationRefreshes = new(64);
    private readonly HashSet<ulong> _queuedMapMarkerAuthorizationRefreshIds = new(64);
    private readonly List<ulong> _activeMapMarkerCupboardIds = new(128);
    private readonly PlayerIdSet _mapMarkerIdsScratch =
      new(PlayerIdSet.AuthorizationInitialCapacity);
    private readonly HashSet<MapMarkerGenericRadius>
      _pendingMapMarkerRadiusReplays = new();
    private readonly List<MapMarkerGenericRadius>
      _mapMarkerRadiusReplayScratch = new(MAP_MARKER_SYNC_BATCH_SIZE);
    private int _mapMarkerRefreshIndex;
    private int _mapMarkerRefreshGeneration;
    private int _mapMarkerRefreshRemaining;
    private bool _mapMarkerRefreshActive;
    private bool _mapMarkerSyncQueued;
    private bool _mapMarkerRefreshQueued;
    private bool _mapMarkerAuthorizationRefreshQueued;
    private bool _mapMarkerRefreshRestartRequested;
    private bool _forceRadiusReplay;
    private bool _mapMarkerRefreshForceRadiusReplay;
    private int _mapMarkerAuthorizedPlayersVersion = 1;
    private System.Action _processQueuedMapMarkerSyncsAction;
    private System.Action _processQueuedMapMarkerRefreshAction;
    private System.Action _processQueuedMapMarkerAuthorizationRefreshesAction;
    private System.Action _processPendingMapMarkerRadiusReplaysAction;
    private readonly StringBuilder _mapMarkerBuilder = new(512);
    private Color _markerProtectedColor;
    private Color _markerPartialColor;
    private Color _markerVulnerableColor;
    private Color _markerDecayingColor;
    private Color _markerGriefColor;
    private Color _markerOutlineColor;
    private PluginTimer _mapMarkerTimer;
    private PluginTimer _boatMapMarkerTimer;
    private PluginTimer _mapMarkerRadiusReplayTimer;

#region Constants

    private const string MAP_MARKER_RADIUS_PREFAB =
      "assets/prefabs/tools/map/genericradiusmarker.prefab";

    private const string MAP_MARKER_VENDING_PREFAB =
      "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

    private const string MAP_MARKER_PROTECTED_TEXT = "PROTECTED ";
    private const string MAP_MARKER_ONLINE_PROTECTED_TEXT = "ONLINE PROTECTED ";
    private const string MAP_MARKER_DECAYING_TEXT = "DECAYING";
    private const string MAP_MARKER_GRIEF_TEXT = "GRIEF";
    private const string MAP_MARKER_INCREASED_DAMAGE_TEXT = "% DAMAGE";
    private const string MAP_MARKER_VULNERABLE_TEXT = "VULNERABLE";
    private const string MAP_MARKER_PENALTY_PREFIX = " \nPenalty | ";
    private const string MAP_MARKER_AUTHORIZED_PLAYERS_PREFIX = " \n";
    private const string MAP_MARKER_NO_AUTHORIZED_PLAYERS_TEXT = "None";
    private const char MAP_MARKER_AUTHORIZED_PLAYER_SEPARATOR = '\n';
    private const string MAP_MARKER_SHORT_TIME_FORMAT = "HH:mm";
    private const string MAP_MARKER_LONG_TIME_FORMAT = "dd.MM HH:mm";
    private const int MAP_MARKER_SYNC_BATCH_SIZE = 16;
    private const int MAP_MARKER_REFRESH_BATCH_SIZE = 16;
    private const float MAP_MARKER_SEND_UPDATE_DELAY = 1f;
    private const float BOAT_MAP_MARKER_RADIUS_REFRESH_INTERVAL = 1f;

#endregion Constants

#endregion Fields

#region Classes

    private sealed class TcMapMarkerGroup : Facepunch.Pool.IPooled
    {
      public BaseCombatEntity ProtectedEntity;
      public BaseEntity ParentEntity;
      public MapMarkerGenericRadius RadiusMarker;
      public VendingMachineMapMarker LabelMarker;
      public string LabelText;
      public TcState TcState;
      public ulong RadiusMarkerNetworkID;
      public ulong LabelMarkerNetworkID;
      public long LabelRemainingMinutes;
      public long LabelPenaltyEndTicks;
      public Vector3 LastPosition;
      public float LabelScale;
      public int AuthorizedPlayersVersion;
      public int RefreshListIndex;
      public int RefreshGeneration;
      public HUDProtectionState ProtectionState;
      public HUDProtectionState LabelProtectionState;
      public bool IsBoat;
      public bool LabelHasTaxProtection;
      public bool LabelHasOnlineProtection;
      public bool AuthorizedPlayersDirty;

      public void EnterPool()
      {
        ProtectedEntity = null;
        ParentEntity = null;
        RadiusMarker = null;
        LabelMarker = null;
        LabelText = null;
        TcState = default;
        RadiusMarkerNetworkID = 0UL;
        LabelMarkerNetworkID = 0UL;
        LabelRemainingMinutes = 0L;
        LabelPenaltyEndTicks = 0L;
        LastPosition = Vector3.zero;
        LabelScale = 0f;
        AuthorizedPlayersVersion = 0;
        RefreshListIndex = -1;
        RefreshGeneration = 0;
        ProtectionState = default;
        LabelProtectionState = default;
        IsBoat = false;
        LabelHasTaxProtection = false;
        LabelHasOnlineProtection = false;
        AuthorizedPlayersDirty = false;
      }

      public void LeavePool() { }
    }

#endregion Classes

#region Methods

    private void InitializeMapMarkers()
    {
      if (!Configuration.MapMarker.Enabled)
        return;

      _processQueuedMapMarkerSyncsAction = ProcessQueuedMapMarkerSyncs;
      _processQueuedMapMarkerRefreshAction = ProcessQueuedMapMarkerRefresh;
      _processQueuedMapMarkerAuthorizationRefreshesAction =
        ProcessQueuedMapMarkerAuthorizationRefreshes;
      _processPendingMapMarkerRadiusReplaysAction =
        ProcessPendingMapMarkerRadiusReplays;

      foreach (var buildingID in _tcCache.Keys)
        QueueBuildingMapMarkerSync(buildingID);

      foreach (var entity in BaseNetworkable.serverEntities)
      {
        if (entity is Tugboat or PlayerBoat)
          QueueBoatMapMarkerSync(entity as BaseVehicle);
      }

      _mapMarkerTimer = timer.Every(
        Configuration.MapMarker.RefreshInterval,
        QueueMapMarkerRefresh);

      if (Configuration.RaidProtection.ProtectBaseBoats)
      {
        _boatMapMarkerTimer = timer.Every(
          BOAT_MAP_MARKER_RADIUS_REFRESH_INTERVAL,
          RefreshBoatMapMarkerRadii);
      }
    }

    private void UnloadMapMarkers()
    {
      _mapMarkerTimer?.Destroy();
      _mapMarkerTimer = null;
      _boatMapMarkerTimer?.Destroy();
      _boatMapMarkerTimer = null;
      _queuedBuildingMapMarkerSyncs.Clear();
      _queuedBuildingMapMarkerSyncIds.Clear();
      _queuedBoatMapMarkerSyncs.Clear();
      _queuedBoatMapMarkerSyncIds.Clear();
      _queuedMapMarkerAuthorizationRefreshes.Clear();
      _queuedMapMarkerAuthorizationRefreshIds.Clear();
      _activeMapMarkerCupboardIds.Clear();
      _mapMarkerRefreshIndex = 0;
      _mapMarkerRefreshGeneration = 0;
      _mapMarkerRefreshRemaining = 0;
      _mapMarkerRefreshActive = false;
      _mapMarkerSyncQueued = false;
      _mapMarkerRefreshQueued = false;
      _mapMarkerAuthorizationRefreshQueued = false;
      _mapMarkerRefreshRestartRequested = false;
      _processQueuedMapMarkerSyncsAction = null;
      _processQueuedMapMarkerRefreshAction = null;
      _processQueuedMapMarkerAuthorizationRefreshesAction = null;
      _processPendingMapMarkerRadiusReplaysAction = null;
      _forceRadiusReplay = false;
      _mapMarkerRefreshForceRadiusReplay = false;
      _mapMarkerAuthorizedPlayersVersion = 1;
      RemoveAllMapMarkers();
      _mapMarkerBuilder.Clear();
      _mapMarkerIdsScratch.Clear();
    }

    private void CacheMapMarkerColors()
    {
      var marker = Configuration.MapMarker;
      ColorUtility.TryParseHtmlString(
        marker.ProtectedColor, out _markerProtectedColor);
      ColorUtility.TryParseHtmlString(
        marker.PartialColor, out _markerPartialColor);
      ColorUtility.TryParseHtmlString(
        marker.VulnerableColor, out _markerVulnerableColor);
      ColorUtility.TryParseHtmlString(
        marker.DecayingColor, out _markerDecayingColor);
      ColorUtility.TryParseHtmlString(
        marker.GriefColor, out _markerGriefColor);
      ColorUtility.TryParseHtmlString(
        marker.OutlineColor, out _markerOutlineColor);
    }

    private void SyncBuildingMapMarker(uint buildingID)
    {
      if (Configuration?.MapMarker?.Enabled is not true || buildingID is 0U)
        return;

      if (_tcCache.TryGetValue(buildingID, out var tcState) &&
          tcState.Privilege)
        SyncTcMapMarker(in tcState);
    }

    private void QueueBuildingMapMarkerSync(uint buildingID)
    {
      if (!_serverInitialized || !Configuration.MapMarker.Enabled ||
          buildingID is 0U)
        return;

      if (!_queuedBuildingMapMarkerSyncIds.Add(buildingID))
        return;

      _queuedBuildingMapMarkerSyncs.Enqueue(buildingID);
      if (_mapMarkerSyncQueued)
        return;

      QueueMapMarkerSyncProcessing();
    }

    private void InvalidateMapMarkerAuthorizedPlayers()
    {
      if (_mapMarkerAuthorizedPlayersVersion == int.MaxValue)
        _mapMarkerAuthorizedPlayersVersion = 1;
      else
        _mapMarkerAuthorizedPlayersVersion++;

      if (_mapMarkerRefreshActive)
        _mapMarkerRefreshRestartRequested = true;

      QueueMapMarkerRefresh();
    }

    private void QueueCodeLockMapMarkerAuthorizationRefresh(
      CodeLock codeLock, uint trackedBuildingID = 0U)
    {
      var queuedMarkerRefresh = false;
      var hasTrackedBuilding = trackedBuildingID is not 0U;
      if (!hasTrackedBuilding)
      {
        var lockNetworkID = GetNetworkID(codeLock);
        hasTrackedBuilding = lockNetworkID is not 0UL &&
          _codeLockBuildingIds.TryGetValue(lockNetworkID, out trackedBuildingID);
      }
      if (hasTrackedBuilding)
        queuedMarkerRefresh =
          QueueMapMarkerAuthorizationRefresh(trackedBuildingID);

      if (TryGetCodeLockBuildingID(codeLock, out var buildingID))
      {
        if (!hasTrackedBuilding || buildingID != trackedBuildingID)
          queuedMarkerRefresh |= QueueMapMarkerAuthorizationRefresh(buildingID);
        if (queuedMarkerRefresh)
          return;
      }

      QueueBoatMapMarkerAuthorizationRefresh(codeLock);
    }

    private bool QueueMapMarkerAuthorizationRefresh(uint buildingID)
    {
      if (buildingID is 0U ||
          !_tcCache.TryGetValue(buildingID, out var tcState) ||
          tcState.CupboardNetworkID is 0UL)
        return false;

      var modularBoat = GetParentModularBoat(tcState.Privilege);
      if (modularBoat)
      {
        if (!Configuration.RaidProtection.ProtectBaseBoats)
          QueueBuildingMapMarkerSync(buildingID);
        else if (_mapMarkerIdsByBoat.TryGetValue(modularBoat, out var markerID))
          QueueMapMarkerAuthorizationRefresh(markerID);
        else
          QueueBoatMapMarkerSync(modularBoat);
        return true;
      }

      if (!_mapMarkersByCupboard.ContainsKey(tcState.CupboardNetworkID))
      {
        QueueBuildingMapMarkerSync(buildingID);
        return true;
      }

      QueueMapMarkerAuthorizationRefresh(tcState.CupboardNetworkID);
      return true;
    }

    private void QueueBoatMapMarkerAuthorizationRefresh(CodeLock codeLock)
    {
      if (codeLock?.GetParentEntity() is not BaseCombatEntity lockedEntity ||
          lockedEntity.GetSlot(BaseEntity.Slot.Lock) != codeLock)
        return;

      var (tugboat, modularBoat, _) = GetVehicle(lockedEntity);
      BaseVehicle boat = tugboat ? tugboat : modularBoat;
      if (boat && _mapMarkerIdsByBoat.TryGetValue(boat, out var markerID))
        QueueMapMarkerAuthorizationRefresh(markerID);
    }

    private void QueueMapMarkerAuthorizationRefresh(ulong cupboardNetworkID)
    {
      if (!_serverInitialized || !Configuration.MapMarker.Enabled ||
          cupboardNetworkID is 0UL ||
          !_queuedMapMarkerAuthorizationRefreshIds.Add(cupboardNetworkID))
        return;

      _queuedMapMarkerAuthorizationRefreshes.Enqueue(cupboardNetworkID);
      if (_mapMarkerAuthorizationRefreshQueued)
        return;

      _mapMarkerAuthorizationRefreshQueued = true;
      NextFrame(_processQueuedMapMarkerAuthorizationRefreshesAction);
    }

    private void QueueBoatMapMarkerSync(BaseVehicle boat)
    {
      if (!_serverInitialized || !Configuration.MapMarker.Enabled ||
          !Configuration.RaidProtection.ProtectBaseBoats || !boat)
        return;

      var boatNetworkID = GetNetworkID(boat);
      if (boatNetworkID is 0UL || !_queuedBoatMapMarkerSyncIds.Add(boatNetworkID))
        return;

      _queuedBoatMapMarkerSyncs.Enqueue(boatNetworkID);
      if (!_mapMarkerSyncQueued)
        QueueMapMarkerSyncProcessing();
    }

    private void QueueMapMarkerSyncProcessing()
    {
      _mapMarkerSyncQueued = true;
      NextFrame(_processQueuedMapMarkerSyncsAction);
    }

    private void QueueMapMarkerRefresh() =>
      QueueMapMarkerRefresh(forceRadiusReplay: false);

    private void QueueMapMarkerRefresh(bool forceRadiusReplay)
    {
      if (!_serverInitialized || !Configuration.MapMarker.Enabled)
        return;

      if (forceRadiusReplay)
        _forceRadiusReplay = true;

      if (_mapMarkerRefreshQueued)
        return;

      QueueMapMarkerRefreshProcessing();
    }

    private void QueueMapMarkerRefreshProcessing()
    {
      _mapMarkerRefreshQueued = true;
      NextFrame(_processQueuedMapMarkerRefreshAction);
    }

    private void ProcessQueuedMapMarkerRefresh()
    {
      if (Configuration?.MapMarker?.Enabled is not true)
      {
        StopMapMarkerRefresh();
        _forceRadiusReplay = false;
        return;
      }

      if (!_mapMarkerRefreshActive)
      {
        if (_mapMarkersByCupboard.Count is 0)
        {
          _mapMarkerRefreshQueued = false;
          _forceRadiusReplay = false;
          return;
        }

        _mapMarkerRefreshActive = true;
        _mapMarkerRefreshForceRadiusReplay = _forceRadiusReplay;
        _forceRadiusReplay = false;
        _mapMarkerRefreshGeneration++;
        _mapMarkerRefreshRemaining = _activeMapMarkerCupboardIds.Count;
      }

      var processed = 0;
      var nowUtc = System.DateTime.UtcNow;

      while (processed < MAP_MARKER_REFRESH_BATCH_SIZE &&
             _mapMarkerRefreshRemaining is not 0 &&
             _activeMapMarkerCupboardIds.Count is not 0)
      {
        if (_mapMarkerRefreshIndex >= _activeMapMarkerCupboardIds.Count)
          _mapMarkerRefreshIndex = 0;

        var cupboardID =
          _activeMapMarkerCupboardIds[_mapMarkerRefreshIndex++];
        processed++;

        if (!_mapMarkersByCupboard.TryGetValue(
              cupboardID, out var markerState) ||
            markerState.RefreshGeneration == _mapMarkerRefreshGeneration)
          continue;

        markerState.RefreshGeneration = _mapMarkerRefreshGeneration;
        _mapMarkerRefreshRemaining--;
        if ((!markerState.RadiusMarker && !markerState.IsBoat) ||
            !markerState.LabelMarker || !markerState.ProtectedEntity)
          RemoveMapMarker(cupboardID);
        else
          UpdateMapMarkerState(
            markerState, nowUtc,
            _mapMarkerRefreshForceRadiusReplay);
      }

      if (_mapMarkerRefreshRemaining is 0 ||
          _activeMapMarkerCupboardIds.Count is 0)
      {
        FinishMapMarkerRefresh();
        return;
      }

      QueueMapMarkerRefreshProcessing();
    }

    private void ProcessQueuedMapMarkerAuthorizationRefreshes()
    {
      if (!_serverInitialized || Configuration?.MapMarker?.Enabled is not true)
      {
        _queuedMapMarkerAuthorizationRefreshes.Clear();
        _queuedMapMarkerAuthorizationRefreshIds.Clear();
        _mapMarkerAuthorizationRefreshQueued = false;
        return;
      }

      var processed = 0;
      var nowUtc = System.DateTime.UtcNow;

      while (processed < MAP_MARKER_REFRESH_BATCH_SIZE &&
             _queuedMapMarkerAuthorizationRefreshes.Count is not 0)
      {
        var cupboardNetworkID =
          _queuedMapMarkerAuthorizationRefreshes.Dequeue();
        _queuedMapMarkerAuthorizationRefreshIds.Remove(cupboardNetworkID);
        processed++;

        if (!_mapMarkersByCupboard.TryGetValue(
              cupboardNetworkID, out var markerState))
          continue;

        markerState.AuthorizedPlayersDirty = true;
        UpdateMapMarkerState(markerState, nowUtc);
      }

      if (_queuedMapMarkerAuthorizationRefreshes.Count is not 0)
      {
        NextFrame(_processQueuedMapMarkerAuthorizationRefreshesAction);
        return;
      }

      _mapMarkerAuthorizationRefreshQueued = false;
    }

    private void ProcessQueuedMapMarkerSyncs()
    {
      if (!_serverInitialized || Configuration?.MapMarker?.Enabled is not true)
      {
        _queuedBuildingMapMarkerSyncs.Clear();
        _queuedBuildingMapMarkerSyncIds.Clear();
        _queuedBoatMapMarkerSyncs.Clear();
        _queuedBoatMapMarkerSyncIds.Clear();
        _mapMarkerSyncQueued = false;
        return;
      }

      var processed = 0;
      while (processed < MAP_MARKER_SYNC_BATCH_SIZE &&
             _queuedBuildingMapMarkerSyncs.Count is not 0)
      {
        var buildingID = _queuedBuildingMapMarkerSyncs.Dequeue();
        _queuedBuildingMapMarkerSyncIds.Remove(buildingID);
        SyncBuildingMapMarker(buildingID);
        processed++;
      }

      while (processed < MAP_MARKER_SYNC_BATCH_SIZE &&
             _queuedBoatMapMarkerSyncs.Count is not 0)
      {
        var boatNetworkID = _queuedBoatMapMarkerSyncs.Dequeue();
        _queuedBoatMapMarkerSyncIds.Remove(boatNetworkID);
        if (BaseNetworkable.serverEntities.Find(
              new NetworkableId(boatNetworkID)) is BaseVehicle boat)
          SyncBoatMapMarker(boat);
        processed++;
      }

      if (_queuedBuildingMapMarkerSyncs.Count is not 0 ||
          _queuedBoatMapMarkerSyncs.Count is not 0)
      {
        QueueMapMarkerSyncProcessing();
        return;
      }

      _mapMarkerSyncQueued = false;
    }

    private void FinishMapMarkerRefresh()
    {
      _mapMarkerRefreshActive = false;
      _mapMarkerRefreshQueued = false;
      _mapMarkerRefreshRemaining = 0;

      if (_forceRadiusReplay || _mapMarkerRefreshRestartRequested)
      {
        _mapMarkerRefreshRestartRequested = false;
        QueueMapMarkerRefresh();
      }
    }

    private void StopMapMarkerRefresh()
    {
      _mapMarkerRefreshActive = false;
      _mapMarkerRefreshQueued = false;
      _mapMarkerRefreshForceRadiusReplay = false;
      _mapMarkerRefreshRestartRequested = false;
      _mapMarkerRefreshRemaining = 0;
    }

    private void SyncTcMapMarker(in TcState tcState)
    {
      if (!Configuration.MapMarker.Enabled ||
          !tcState.Privilege ||
          tcState.CupboardNetworkID is 0UL)
        return;

      var modularBoat = GetParentModularBoat(tcState.Privilege);
      if (modularBoat)
      {
        RemoveMapMarker(tcState.CupboardNetworkID);
        if (Configuration.RaidProtection.ProtectBaseBoats)
          QueueBoatMapMarkerSync(modularBoat);
        return;
      }

      if (_mapMarkersByCupboard.TryGetValue(
            tcState.CupboardNetworkID, out var existing))
      {
        existing.TcState = tcState;
        existing.ProtectedEntity = tcState.Privilege;
        existing.ParentEntity = tcState.Privilege;
        existing.IsBoat = false;
        UpdateMapMarkerState(existing, System.DateTime.UtcNow);
        return;
      }

      SpawnMapMarkerGroup(in tcState);
    }

    private void SyncBoatMapMarker(BaseVehicle boat)
    {
      if (!Configuration.MapMarker.Enabled ||
          !Configuration.RaidProtection.ProtectBaseBoats ||
          boat is not Tugboat and not PlayerBoat)
        return;

      var vehiclePrivilege = boat.GetChildPrivilege();
      var privilegeNetworkID = GetNetworkID(vehiclePrivilege);
      if (!vehiclePrivilege || privilegeNetworkID is 0UL)
      {
        RemoveBoatMapMarker(boat);
        return;
      }

      if (_mapMarkerIdsByBoat.TryGetValue(boat, out var previousMarkerID) &&
          previousMarkerID != privilegeNetworkID)
        RemoveMapMarker(previousMarkerID);

      if (_mapMarkersByCupboard.TryGetValue(
            privilegeNetworkID, out var existing))
      {
        if (existing.IsBoat &&
            existing.ProtectedEntity is BaseVehicle previousBoat &&
            previousBoat != boat &&
            _mapMarkerIdsByBoat.TryGetValue(
              previousBoat, out var previousBoatMarkerID) &&
            previousBoatMarkerID == privilegeNetworkID)
          _mapMarkerIdsByBoat.Remove(previousBoat);

        existing.ProtectedEntity = boat;
        existing.ParentEntity = vehiclePrivilege;
        existing.IsBoat = true;
        _mapMarkerIdsByBoat[boat] = privilegeNetworkID;
        UpdateMapMarkerState(existing, System.DateTime.UtcNow);
        return;
      }

      var tcState = default(TcState);
      SpawnMapMarkerGroup(
        boat, vehiclePrivilege, privilegeNetworkID, true, in tcState);
    }

    private void SpawnMapMarkerGroup(in TcState tcState) =>
      SpawnMapMarkerGroup(
        tcState.Privilege, tcState.Privilege, tcState.CupboardNetworkID,
        false, in tcState);

    private void SpawnMapMarkerGroup(
      BaseCombatEntity protectedEntity, BaseEntity parentEntity,
      ulong markerGroupID, bool isBoat, in TcState tcState)
    {
      var position = parentEntity.transform.position;
      var radiusMarker =
        GameManager.server.CreateEntity(
          MAP_MARKER_RADIUS_PREFAB, position) as
          MapMarkerGenericRadius;
      var labelMarker =
        GameManager.server.CreateEntity(
          MAP_MARKER_VENDING_PREFAB, position) as
          VendingMachineMapMarker;
      if (!radiusMarker || !labelMarker)
      {
        if (radiusMarker)
          radiusMarker.Kill();
        if (labelMarker)
          labelMarker.Kill();
        return;
      }

      var nowUtc = System.DateTime.UtcNow;
      var decision = EvaluateProtection(
        protectedEntity, null, nowUtc, false,
        _mapMarkerIdsScratch, out var authorizedPlayersCollected);
      var protectionState = GetProtectionState(in decision);
      var hasTaxProtection = HasTaxProtection(in decision, protectionState);
      var options = Configuration.MapMarker;
      radiusMarker.enableSaving = false;
      radiusMarker.limitNetworking = true;
      radiusMarker.radius = options.Radius;
      radiusMarker.alpha = options.Alpha;
      radiusMarker.color1 = GetMapMarkerColor(protectionState);
      radiusMarker.color2 = _markerOutlineColor;
      labelMarker.enableSaving = false;
      labelMarker.limitNetworking = true;
      var markerGroup = Facepunch.Pool.Get<TcMapMarkerGroup>();
      markerGroup.TcState = tcState;
      markerGroup.ProtectedEntity = protectedEntity;
      markerGroup.ParentEntity = parentEntity;
      markerGroup.LastPosition = position;
      markerGroup.IsBoat = isBoat;
      markerGroup.RadiusMarker = radiusMarker;
      markerGroup.LabelMarker = labelMarker;
      markerGroup.ProtectionState = protectionState;
      var authorizedPlayers = GetMapMarkerAuthorizedPlayers(
        markerGroup, authorizedPlayersCollected);
      CacheMapMarkerLabelState(
        markerGroup, protectionState, hasTaxProtection, in decision, nowUtc);
      markerGroup.LabelText = BuildMapMarkerLabel(
        protectionState, hasTaxProtection, in decision, nowUtc,
        markerGroup.LabelPenaltyEndTicks, authorizedPlayers);
      labelMarker.markerShopName = markerGroup.LabelText;
      SpawnMapMarkerEntity(radiusMarker);
      SpawnMapMarkerEntity(labelMarker);

      var radiusMarkerNetworkID = GetNetworkID(radiusMarker);
      var labelMarkerNetworkID = GetNetworkID(labelMarker);
      if (radiusMarkerNetworkID is 0UL || labelMarkerNetworkID is 0UL)
      {
        radiusMarker.Kill();
        labelMarker.Kill();
        Facepunch.Pool.Free(ref markerGroup);
        return;
      }

      markerGroup.RadiusMarkerNetworkID = radiusMarkerNetworkID;
      markerGroup.LabelMarkerNetworkID = labelMarkerNetworkID;
      markerGroup.RefreshListIndex = _activeMapMarkerCupboardIds.Count;
      markerGroup.RefreshGeneration = _mapMarkerRefreshActive ?
        _mapMarkerRefreshGeneration : 0;
      _activeMapMarkerCupboardIds.Add(markerGroupID);
      _mapMarkersByCupboard[markerGroupID] = markerGroup;
      if (isBoat && protectedEntity is BaseVehicle boat)
        _mapMarkerIdsByBoat[boat] = markerGroupID;
      _activeMarkerNetIds.Add(radiusMarkerNetworkID);
      _activeMarkerNetIds.Add(labelMarkerNetworkID);

      radiusMarker.limitNetworking = false;
      labelMarker.limitNetworking = false;

      radiusMarker.SendNetworkUpdate();
      labelMarker.SendNetworkUpdate();

      QueueMapMarkerRadiusReplay(radiusMarker);

      if (_forceRadiusReplay)
        QueueMapMarkerRefresh();

      if (isBoat)
        RefreshBoatMapMarkerRadius(markerGroup);
    }

    private void SpawnMapMarkerEntity(BaseNetworkable marker)
    {
      _pendingMapMarkers.Add(marker);
      marker.Spawn();
      _pendingMapMarkers.Remove(marker);
    }

    private void RefreshBoatMapMarkerRadii()
    {
      foreach (var markerState in _mapMarkersByCupboard.Values)
      {
        if (!markerState.IsBoat)
          continue;

        RefreshBoatMapMarkerRadius(markerState);
      }
    }

    private void RefreshBoatMapMarkerRadius(TcMapMarkerGroup markerState)
    {
      var boat = markerState.ProtectedEntity as BaseVehicle;
      if (!markerState.IsBoat || !boat || !markerState.ParentEntity)
        return;

      var position = markerState.ParentEntity.transform.position;
      var positionChanged = position != markerState.LastPosition;
      if (positionChanged)
      {
        markerState.LastPosition = position;
        if (markerState.LabelMarker)
        {
          markerState.LabelMarker.transform.position = position;
          markerState.LabelMarker.SendNetworkUpdate();
        }
      }

      if (boat.HasFlag(BaseEntity.Flags.On))
      {
        if (!Configuration.MapMarker.EnableBoatLiveCircle)
        {
          if (markerState.RadiusMarker)
          {
            RemoveMapMarkerEntity(
              markerState.RadiusMarker, markerState.RadiusMarkerNetworkID);
            markerState.RadiusMarker = null;
            markerState.RadiusMarkerNetworkID = 0UL;
          }
        }
        else if (markerState.RadiusMarker && positionChanged)
        {
          markerState.RadiusMarker.transform.position = position;
          markerState.RadiusMarker.SendNetworkUpdate();
          markerState.RadiusMarker.SendUpdate();
        }
        return;
      }

      if (markerState.RadiusMarker)
      {
        if (positionChanged)
        {
          markerState.RadiusMarker.transform.position = position;
          markerState.RadiusMarker.SendNetworkUpdate();
          markerState.RadiusMarker.SendUpdate();
        }
        return;
      }

      var radiusMarker = GameManager.server.CreateEntity(
        MAP_MARKER_RADIUS_PREFAB, position) as MapMarkerGenericRadius;
      if (!radiusMarker)
        return;

      var options = Configuration.MapMarker;
      radiusMarker.enableSaving = false;
      radiusMarker.limitNetworking = true;
      radiusMarker.radius = options.Radius;
      radiusMarker.alpha = options.Alpha;
      radiusMarker.color1 = GetMapMarkerColor(markerState.ProtectionState);
      radiusMarker.color2 = _markerOutlineColor;
      SpawnMapMarkerEntity(radiusMarker);

      var radiusMarkerNetworkID = GetNetworkID(radiusMarker);
      if (radiusMarkerNetworkID is 0UL)
      {
        radiusMarker.Kill();
        return;
      }

      markerState.RadiusMarker = radiusMarker;
      markerState.RadiusMarkerNetworkID = radiusMarkerNetworkID;
      _activeMarkerNetIds.Add(radiusMarkerNetworkID);
      radiusMarker.limitNetworking = false;
      radiusMarker.SendNetworkUpdate();

      QueueMapMarkerRadiusReplay(radiusMarker);
    }

    private void UpdateMapMarkerState(
      TcMapMarkerGroup markerState, System.DateTime nowUtc,
      bool forceRadiusReplay = false)
    {
      var protectedEntity = markerState.ProtectedEntity;
      if ((!markerState.RadiusMarker && !markerState.IsBoat) ||
          !markerState.LabelMarker || !protectedEntity)
        return;

      var decision = EvaluateProtection(
        protectedEntity, null, nowUtc, false,
        _mapMarkerIdsScratch, out var authorizedPlayersCollected);
      var protectionState = GetProtectionState(in decision);
      var hasTaxProtection = HasTaxProtection(in decision, protectionState);
      var protectionStateChanged =
        protectionState != markerState.ProtectionState;
      if (protectionStateChanged)
      {
        markerState.ProtectionState = protectionState;
        if (markerState.RadiusMarker)
          markerState.RadiusMarker.color1 = GetMapMarkerColor(protectionState);
      }

      if (markerState.RadiusMarker &&
          (protectionStateChanged || forceRadiusReplay))
      {
        markerState.RadiusMarker.SendNetworkUpdate();
        var radiusMarker = markerState.RadiusMarker;
        if (forceRadiusReplay)
          QueueMapMarkerRadiusReplay(radiusMarker);
        else
          radiusMarker.SendUpdate();
      }

      var shouldRefreshLabel = ShouldRefreshMapMarkerLabel(
        markerState, protectionState, hasTaxProtection, in decision, nowUtc,
        authorizedPlayersCollected, out var authorizedPlayers);
      if (!shouldRefreshLabel && !forceRadiusReplay)
        return;

      var label = shouldRefreshLabel ?
        BuildMapMarkerLabel(
          protectionState, hasTaxProtection, in decision, nowUtc,
          markerState.LabelPenaltyEndTicks, authorizedPlayers) :
        markerState.LabelText;
      markerState.LabelText = label;
      markerState.LabelMarker.markerShopName = label;
      markerState.LabelMarker.SendNetworkUpdate();
    }

    private bool ShouldRefreshMapMarkerLabel(
      TcMapMarkerGroup markerState,
      HUDProtectionState protectionState, bool hasTaxProtection,
      in DamageDecision decision,
      System.DateTime nowUtc, bool authorizedPlayersCollected,
      out PlayerIdSet authorizedPlayers)
    {
      authorizedPlayers = null;
      GetMapMarkerLabelState(
        protectionState, in decision, nowUtc,
        out var remainingMinutes, out var penaltyEndTicks);
      var hasOnlineProtection = HasOnlineProtection(in decision);

      if (markerState.LabelProtectionState == protectionState &&
          markerState.LabelHasTaxProtection == hasTaxProtection &&
          markerState.LabelHasOnlineProtection == hasOnlineProtection &&
          markerState.LabelScale == decision.Scale &&
          markerState.LabelRemainingMinutes == remainingMinutes &&
          markerState.LabelPenaltyEndTicks == penaltyEndTicks &&
          markerState.AuthorizedPlayersVersion ==
          _mapMarkerAuthorizedPlayersVersion &&
          !markerState.AuthorizedPlayersDirty)
        return false;

      authorizedPlayers = GetMapMarkerAuthorizedPlayers(
        markerState, authorizedPlayersCollected);

      markerState.LabelProtectionState = protectionState;
      markerState.LabelHasTaxProtection = hasTaxProtection;
      markerState.LabelHasOnlineProtection = hasOnlineProtection;
      markerState.LabelScale = decision.Scale;
      markerState.LabelRemainingMinutes = remainingMinutes;
      markerState.LabelPenaltyEndTicks = penaltyEndTicks;
      markerState.AuthorizedPlayersVersion = _mapMarkerAuthorizedPlayersVersion;
      markerState.AuthorizedPlayersDirty = false;
      return true;
    }

    private void CacheMapMarkerLabelState(
      TcMapMarkerGroup markerState,
      HUDProtectionState protectionState, bool hasTaxProtection,
      in DamageDecision decision, System.DateTime nowUtc)
    {
      GetMapMarkerLabelState(
        protectionState, in decision, nowUtc,
        out markerState.LabelRemainingMinutes,
        out markerState.LabelPenaltyEndTicks);
      markerState.LabelProtectionState = protectionState;
      markerState.LabelHasTaxProtection = hasTaxProtection;
      markerState.LabelHasOnlineProtection = HasOnlineProtection(in decision);
      markerState.LabelScale = decision.Scale;
      markerState.AuthorizedPlayersVersion = _mapMarkerAuthorizedPlayersVersion;
    }

    private void GetMapMarkerLabelState(
      HUDProtectionState protectionState, in DamageDecision decision,
      System.DateTime nowUtc, out long remainingMinutes,
      out long penaltyEndTicks)
    {
      remainingMinutes = protectionState is HUDProtectionState.Protected or
        HUDProtectionState.Partial ?
        (decision.TargetScaleCache?.RemainingTime.Ticks ?? 0L) /
        System.TimeSpan.TicksPerMinute : 0L;
      penaltyEndTicks = 0L;

      if (protectionState is not HUDProtectionState.Decaying and not HUDProtectionState.Grief &&
          decision.TargetID is not 0UL &&
          _lastOnline.TryGetValue(decision.TargetID, out var lastOnline) &&
          lastOnline.PenaltyEndTicks > nowUtc.Ticks)
      {
        penaltyEndTicks = lastOnline.PenaltyEndTicks;
      }

    }

    private Color GetMapMarkerColor(
      HUDProtectionState protectionState) => protectionState switch
      {
        HUDProtectionState.Protected => _markerProtectedColor,
        HUDProtectionState.Partial => _markerPartialColor,
        HUDProtectionState.Decaying => _markerDecayingColor,
        HUDProtectionState.Grief => _markerGriefColor,
        _ => _markerVulnerableColor
      };

    private string BuildMapMarkerLabel(
      HUDProtectionState protectionState, bool hasTaxProtection,
      in DamageDecision decision,
      System.DateTime nowUtc, long penaltyEndTicks,
      PlayerIdSet authorizedPlayers)
    {
      _mapMarkerBuilder.Clear();

      // --- Line 1: Protection Status & Percentage + Remaining Time ---
      switch (protectionState)
      {
        case HUDProtectionState.Protected:
        case HUDProtectionState.Partial:
          var percent = decision.Scale <= 0f ? 100f : decision.Scale.ToPercent();
          _mapMarkerBuilder.Append(ORP_PREFIX)
            .Append(HasOnlineProtection(in decision) ?
              MAP_MARKER_ONLINE_PROTECTED_TEXT : MAP_MARKER_PROTECTED_TEXT);
          AppendPercentage(_mapMarkerBuilder, percent);
          _mapMarkerBuilder.Append('%');

          var remainingTime =
            decision.TargetScaleCache?.RemainingTime ?? System.TimeSpan.Zero;
          if (remainingTime != System.TimeSpan.Zero)
          {
            _mapMarkerBuilder.Append(" (");
            AppendFormattedDuration(
              _mapMarkerBuilder,
              remainingTime.Ticks / System.TimeSpan.TicksPerSecond,
              includeDays: true);
            _mapMarkerBuilder.Append(')');
          }
          break;
        case HUDProtectionState.Decaying:
          _mapMarkerBuilder.Append(ORP_PREFIX)
            .Append(MAP_MARKER_DECAYING_TEXT);
          break;
        case HUDProtectionState.Grief:
          _mapMarkerBuilder.Append(ORP_PREFIX)
            .Append(MAP_MARKER_GRIEF_TEXT);
          break;
        case HUDProtectionState.IncreasedDamage:
          _mapMarkerBuilder.Append(ORP_PREFIX).Append('+');
          AppendPercentage(
            _mapMarkerBuilder, -decision.Scale.ToPercent());
          _mapMarkerBuilder.Append(MAP_MARKER_INCREASED_DAMAGE_TEXT);
          break;
        case HUDProtectionState.Vulnerable:
        default:
          _mapMarkerBuilder.Append(ORP_PREFIX)
            .Append(MAP_MARKER_VULNERABLE_TEXT);
          break;
      }

      if (hasTaxProtection)
        _mapMarkerBuilder.Append(" (Tax)");

      // --- Line 2: Penalty End Time (if active) ---
      if (penaltyEndTicks > nowUtc.Ticks)
      {
        var localEndTime = System.TimeZoneInfo.ConvertTimeFromUtc(
          new System.DateTime(penaltyEndTicks), _timeZone);
        var penaltyFormat = penaltyEndTicks - nowUtc.Ticks >
          System.TimeSpan.TicksPerDay ? MAP_MARKER_LONG_TIME_FORMAT :
          MAP_MARKER_SHORT_TIME_FORMAT;
        _mapMarkerBuilder.Append(MAP_MARKER_PENALTY_PREFIX)
          .Append(localEndTime.ToString(penaltyFormat));
      }

      // --- Line 3: Line-separated Name[steamID]  ---
      _mapMarkerBuilder.Append(MAP_MARKER_AUTHORIZED_PLAYERS_PREFIX);
      if (authorizedPlayers is null || authorizedPlayers.Count is 0)
        return _mapMarkerBuilder.Append(
          MAP_MARKER_NO_AUTHORIZED_PLAYERS_TEXT).ToString();

      var first = true;
      var playerLimit = System.Math.Min(
        authorizedPlayers.Count, Configuration.MapMarker.TooltipMaxPlayers);

      for (var index = 0; index < playerLimit; index++)
      {
        var userID = authorizedPlayers[index];
        if (!first)
          _mapMarkerBuilder.Append(MAP_MARKER_AUTHORIZED_PLAYER_SEPARATOR);

        var userName = GetPlayerName(userID);

        if (!string.IsNullOrEmpty(userName))
          _mapMarkerBuilder.Append(userName).Append('(').Append(userID).Append(')');
        else
          _mapMarkerBuilder.Append(userID);

        first = false;
      }

      return _mapMarkerBuilder.ToString();
    }

    private void QueueMapMarkerRadiusReplay(
      MapMarkerGenericRadius radiusMarker)
    {
      if (!radiusMarker ||
          !_pendingMapMarkerRadiusReplays.Add(radiusMarker) ||
          _mapMarkerRadiusReplayTimer is not null)
        return;

      _mapMarkerRadiusReplayTimer = timer.Once(
        MAP_MARKER_SEND_UPDATE_DELAY,
        _processPendingMapMarkerRadiusReplaysAction);
    }

    private void ProcessPendingMapMarkerRadiusReplays()
    {
      _mapMarkerRadiusReplayTimer = null;
      if (_pendingMapMarkerRadiusReplays.Count is 0)
        return;

      _mapMarkerRadiusReplayScratch.Clear();
      foreach (var radiusMarker in _pendingMapMarkerRadiusReplays)
        _mapMarkerRadiusReplayScratch.Add(radiusMarker);
      _pendingMapMarkerRadiusReplays.Clear();

      for (var i = 0; i < _mapMarkerRadiusReplayScratch.Count; i++)
      {
        var radiusMarker = _mapMarkerRadiusReplayScratch[i];
        if (radiusMarker)
          radiusMarker.SendUpdate();
      }
      _mapMarkerRadiusReplayScratch.Clear();
    }

    private void ClearMapMarkerRadiusReplays()
    {
      _mapMarkerRadiusReplayTimer?.Destroy();
      _mapMarkerRadiusReplayTimer = null;
      _pendingMapMarkerRadiusReplays.Clear();
      _mapMarkerRadiusReplayScratch.Clear();
    }

    private PlayerIdSet GetTotalAuthorizedPlayers(BuildingPrivlidge privilege)
    {
      if (!privilege)
        return null;

      _tmpIdSetScratch.Clear();
      PopulatePrivilegeAuthorizedPlayers(privilege, _tmpIdSetScratch);

      return _tmpIdSetScratch;
    }

    private void PopulatePrivilegeAuthorizedPlayers(
      BuildingPrivlidge privilege, PlayerIdSet authorizedPlayers)
    {
      if (privilege.authorizedPlayers is not null)
        authorizedPlayers.AddRange(privilege.authorizedPlayers);

      if (Configuration.Team.IncludeWhitelistPlayers)
        authorizedPlayers.AddRange(GetCodeLockWhitelistPlayers(privilege));
    }

    private PlayerIdSet GetMapMarkerAuthorizedPlayers(
      TcMapMarkerGroup markerState, bool authorizedPlayersCollected)
    {
      var authorizedPlayers = _mapMarkerIdsScratch;
      if (authorizedPlayersCollected)
        return authorizedPlayers;

      if (!markerState.IsBoat)
      {
        authorizedPlayers.Clear();
        var privilege = markerState.TcState.Privilege;
        if (!privilege)
          return null;

        PopulatePrivilegeAuthorizedPlayers(privilege, authorizedPlayers);

        return authorizedPlayers;
      }

      authorizedPlayers.Clear();
      var (tugboat, modularBoat, vehicle) =
        GetVehicle(markerState.ProtectedEntity);

      return GetAuthorizedPlayers(
        markerState.ProtectedEntity, tugboat, modularBoat, vehicle, null,
        authorizedPlayers, out _) && !authorizedPlayers.Overflowed ?
          authorizedPlayers : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetPlayerName(ulong userID)
    {
      if (_lastOnline.TryGetValue(userID, out var user) &&
          !string.IsNullOrEmpty(user.UserName))
        return user.UserName;

      return _players.GetPlayer(userID)?.displayName ?? string.Empty;
    }

    private void UpdateTcMarkerLabel(BuildingPrivlidge privilege)
    {
      if (!Configuration.MapMarker.Enabled || !privilege)
        return;

      QueueBuildingMapMarkerSync(privilege.buildingID);
      QueueModularBoatMapMarkerSync(privilege);
    }

    private void QueueModularBoatMapMarkerSync(
      BuildingPrivlidge privilege)
    {
      var modularBoat = GetParentModularBoat(privilege);
      if (modularBoat)
        QueueBoatMapMarkerSync(modularBoat);
    }

    private void RemoveMapMarker(ulong cupboardNetworkID)
    {
      if (!_mapMarkersByCupboard.Remove(
            cupboardNetworkID, out var markerState))
        return;

      RemoveActiveMapMarkerCupboardId(cupboardNetworkID, markerState);

      if (markerState.IsBoat &&
          markerState.ProtectedEntity is BaseVehicle boat &&
          _mapMarkerIdsByBoat.TryGetValue(boat, out var markerID) &&
          markerID == cupboardNetworkID)
        _mapMarkerIdsByBoat.Remove(boat);

      if (_mapMarkerRefreshActive &&
          markerState.RefreshGeneration != _mapMarkerRefreshGeneration)
        _mapMarkerRefreshRemaining--;

      RemoveMapMarkerEntity(
        markerState.RadiusMarker, markerState.RadiusMarkerNetworkID);
      RemoveMapMarkerEntity(
        markerState.LabelMarker, markerState.LabelMarkerNetworkID);

      Facepunch.Pool.Free(ref markerState);
    }

    private void RemoveBoatMapMarker(BaseVehicle boat)
    {
      if (!boat)
        return;

      if (_mapMarkerIdsByBoat.TryGetValue(boat, out var markerID))
        RemoveMapMarker(markerID);
    }

    private void RemoveAllMapMarkers()
    {
      foreach (var markerState in _mapMarkersByCupboard.Values)
      {
        _activeMarkerNetIds.Remove(markerState.RadiusMarkerNetworkID);
        _activeMarkerNetIds.Remove(markerState.LabelMarkerNetworkID);
        if (markerState.RadiusMarker)
          markerState.RadiusMarker.Kill();
        if (markerState.LabelMarker)
          markerState.LabelMarker.Kill();
        var markerGroup = markerState;
        Facepunch.Pool.Free(ref markerGroup);
      }

      _mapMarkersByCupboard.Clear();
      _mapMarkerIdsByBoat.Clear();
      _activeMapMarkerCupboardIds.Clear();
      _mapMarkerRefreshIndex = 0;
      _mapMarkerRefreshRemaining = 0;
      _activeMarkerNetIds.Clear();
      _pendingMapMarkers.Clear();
      ClearMapMarkerRadiusReplays();
    }

    private void RemoveActiveMapMarkerCupboardId(
      ulong cupboardNetworkID, TcMapMarkerGroup markerState)
    {
      var index = markerState.RefreshListIndex;
      var lastIndex = _activeMapMarkerCupboardIds.Count - 1;
      if (index < 0 || index > lastIndex ||
          _activeMapMarkerCupboardIds[index] != cupboardNetworkID)
        return;

      if (index != lastIndex)
      {
        var lastCupboardID = _activeMapMarkerCupboardIds[lastIndex];
        _activeMapMarkerCupboardIds[index] = lastCupboardID;
        if (_mapMarkersByCupboard.TryGetValue(
              lastCupboardID, out var lastMarkerState))
          lastMarkerState.RefreshListIndex = index;
      }

      _activeMapMarkerCupboardIds.RemoveAt(lastIndex);
      if (_mapMarkerRefreshIndex >= _activeMapMarkerCupboardIds.Count)
        _mapMarkerRefreshIndex = 0;
    }

    private void RemoveMapMarkerEntity(
      BaseNetworkable marker, ulong markerNetworkID)
    {
      _activeMarkerNetIds.Remove(markerNetworkID);

      if (marker is null)
        return;

      _pendingMapMarkers.Remove(marker);
      if (marker is MapMarkerGenericRadius radiusMarker &&
          _pendingMapMarkerRadiusReplays.Remove(radiusMarker) &&
          _pendingMapMarkerRadiusReplays.Count is 0)
      {
        _mapMarkerRadiusReplayTimer?.Destroy();
        _mapMarkerRadiusReplayTimer = null;
      }
      if (marker)
        marker.Kill();
    }

#endregion Methods

#endregion Map Markers

#region Tax Protection

#region Fields

    private readonly Dictionary<ulong, TaxProtectionState> _taxProtection = new();
    private readonly Dictionary<ulong, TaxProtectionRefund> _pendingTaxProtectionRefunds = new();
    private readonly Dictionary<ulong, TaxProtectionRefund> _taxProtectionRefundPouches = new();
    private readonly Queue<TaxProtectionRefund> _queuedTaxProtectionRefunds = new();
    private readonly PlayerIdSet _taxProtectionPlayerIdsScratch =
      new(PlayerIdSet.AuthorizationInitialCapacity);
    private readonly HashSet<uint> _modularBoatBuildingIdsScratch = new(16);
    private readonly Queue<uint> _queuedTaxProtectionSyncs = new(64);
    private readonly HashSet<uint> _queuedTaxProtectionSyncIds = new(64);
    private readonly HashSet<uint> _taxProtectionSyncBuildingIds = new(64);
    private bool _taxProtectionSyncQueued;
    private System.Action _processQueuedTaxProtectionSyncsAction;
    private bool _taxProtectionRefundsQueued;
    private System.Action _processQueuedTaxProtectionRefundsAction;
    private PluginTimer _taxProtectionTimer;
    private ItemDefinition _taxProtectionCurrencyDefinition;
    private string _taxProtectionCurrencyName =
      TAX_PROTECTION_CURRENCY_FALLBACK;
    private long _maxPurchasedProtectionTicks;
    private static readonly object BoxedCannotAccept =
      ItemContainer.CanAcceptResult.CannotAccept;

#region Constants

    private const string
      TAX_PROTECTION_REFUND_CONTAINER_PREFAB =
        "assets/prefabs/misc/item drop/item_drop_backpack.prefab",
      TAX_PROTECTION_CURRENCY_FALLBACK = "currency";
    private const float TAX_PROTECTION_REFUND_CONTAINER_LIFETIME = 300f;
    private const float TAX_PROTECTION_SYNC_INTERVAL = 60f;
    private const int TAX_PROTECTION_SYNC_BATCH_SIZE = 16;

#endregion Constants

#endregion Fields

#region Classes

    private sealed class TaxProtectionState
    {
      public long BankedTicks;
      public long ActiveSinceTicks;

      public long GetRemainingTicks(long nowTicks)
      {
        if (BankedTicks <= 0L || ActiveSinceTicks <= 0L ||
            nowTicks <= ActiveSinceTicks)
          return System.Math.Max(0L, BankedTicks);

        var elapsedTicks = nowTicks - ActiveSinceTicks;
        return elapsedTicks >= BankedTicks ? 0L :
          BankedTicks - elapsedTicks;
      }
    }

    private sealed class TaxProtectionRefund : Facepunch.Pool.IPooled
    {
      public readonly HashSet<ulong> AuthorizedPlayerIds = new();
      public ulong DestroyerID;
      public Vector3 Position;
      public int CurrencyAmount;

      public void EnterPool()
      {
        AuthorizedPlayerIds.Clear();
        CurrencyAmount = 0;
        DestroyerID = 0UL;
        Position = Vector3.zero;
      }

      public void LeavePool() { }
    }

#endregion Classes

#region Cache Methods

    private void CacheTaxProtectionLimits() =>
      _maxPurchasedProtectionTicks = Configuration.TaxProtection.MaxPurchaseHours *
        System.TimeSpan.TicksPerHour;

    private void CacheTaxProtectionCurrency()
    {
      var options = Configuration?.TaxProtection;
      _taxProtectionCurrencyDefinition =
        options?.CurrencyItemID is { } itemID and not 0 ?
          ItemManager.FindItemDefinition(itemID) : null;
      _taxProtectionCurrencyName =
        _taxProtectionCurrencyDefinition?.displayName?.english ??
        TAX_PROTECTION_CURRENCY_FALLBACK;
    }

    private int GetTaxProtectionCostPerHour(
      BuildingPrivlidge privilege)
      => GetTaxProtectionCostPerHour(
        privilege ? privilege.GetGroupAuthCount() : 0);

    private static int GetTaxProtectionCostPerHour(int groupAuthCount)
    {
      var options = Configuration.TaxProtection;
      var baseCost = options.CostPerHour;
      var scaling = options.GroupSizeCostScaling;
      if (baseCost <= 0 || !scaling.Enabled ||
          scaling.MaximumCostMultiplier <= 1m ||
          (scaling.SmallGroupIncreasePercent <= 0m &&
           scaling.LargeGroupIncreasePercent <= 0m))
        return baseCost;

      var remainingPlayers = System.Math.Max(
        0, groupAuthCount - scaling.BaseCostPlayerCount);
      if (remainingPlayers is 0)
        return baseCost;

      var smallGroupPlayers = System.Math.Min(
        remainingPlayers, scaling.SmallGroupPlayerCount);
      remainingPlayers -= smallGroupPlayers;

      var increasePercent =
        smallGroupPlayers * scaling.SmallGroupIncreasePercent +
        remainingPlayers * scaling.LargeGroupIncreasePercent;
      var multiplier = System.Math.Min(
        scaling.MaximumCostMultiplier, 1m + increasePercent / 100m);
      var scaledCost = decimal.Ceiling(baseCost * multiplier);

      return scaledCost >= int.MaxValue ? int.MaxValue : (int)scaledCost;
    }

    private void ClearTaxProtectionRefunds()
    {
      foreach (var refund in _pendingTaxProtectionRefunds.Values)
      {
        var cachedEntry = refund;
        Facepunch.Pool.Free(ref cachedEntry);
      }
      _pendingTaxProtectionRefunds.Clear();

      _tmpIdsScratch.Clear();
      foreach (var pouch in _taxProtectionRefundPouches)
      {
        _tmpIdsScratch.Add(pouch.Key);
        var cachedEntry = pouch.Value;
        Facepunch.Pool.Free(ref cachedEntry);
      }
      _taxProtectionRefundPouches.Clear();
      Unsubscribe(nameof(CanLootEntity));

      foreach (var pouchNetworkID in _tmpIdsScratch)
      {
        if (BaseNetworkable.serverEntities.Find(
              new NetworkableId(pouchNetworkID)) is DroppedItemContainer pouch)
          pouch.Kill();
      }

      foreach (var refund in _queuedTaxProtectionRefunds)
      {
        var cachedEntry = refund;
        Facepunch.Pool.Free(ref cachedEntry);
      }
      _queuedTaxProtectionRefunds.Clear();
      _taxProtectionRefundsQueued = false;
    }

#endregion Cache Methods

#region Hooks

    private void OnEntityDeath(BuildingPrivlidge entity, HitInfo hitInfo)
    {
      var options = Configuration.TaxProtection;
      if (!entity || !options.Enabled || !options.RefundOnDestruction ||
          hitInfo?.InitiatorPlayer is not { } destroyer)
        return;

      if (options.CurrencyItemID is 0 || options.CostPerHour <= 0)
        return;

      var cupboardNetworkID = GetNetworkID(entity);
      if (cupboardNetworkID is 0UL ||
          !_taxProtection.TryGetValue(
            cupboardNetworkID, out var state))
        return;

      var remainingTicks = state.GetRemainingTicks(System.DateTime.UtcNow.Ticks);
      if (remainingTicks <= 0L)
        return;

      var remainingHours = remainingTicks / System.TimeSpan.TicksPerHour;
      if (remainingHours <= 0L)
        return;

      // Group-size surcharges are purchase costs and are not refundable
      var currencyAmount = remainingHours * options.CostPerHour;
      if (currencyAmount > int.MaxValue)
        return;

      var authorizedPlayers = GetTotalAuthorizedPlayers(entity);
      if (authorizedPlayers?.Overflowed is true ||
          !IsTrustedForAuthorizedPlayers(
            destroyer.userID.Get(), authorizedPlayers))
        return;

      if (_pendingTaxProtectionRefunds.Remove(
            cupboardNetworkID, out var staleRefund))
        Facepunch.Pool.Free(ref staleRefund);

      var refund = Facepunch.Pool.Get<TaxProtectionRefund>();
      refund.CurrencyAmount = (int)currencyAmount;
      refund.DestroyerID = destroyer.userID.Get();
      refund.Position = destroyer.transform.position;
      if (authorizedPlayers is not null)
      {
        for (var i = 0; i < authorizedPlayers.Count; i++)
          refund.AuthorizedPlayerIds.Add(authorizedPlayers[i]);
      }
      refund.AuthorizedPlayerIds.Add(refund.DestroyerID);
      _pendingTaxProtectionRefunds[cupboardNetworkID] = refund;
    }

    private object CanLootEntity(
      BasePlayer player, DroppedItemContainer container)
    {
      if (!player || !container ||
          !_taxProtectionRefundPouches.TryGetValue(
            GetNetworkID(container), out var refund))
        return null;

      return refund.AuthorizedPlayerIds.Contains(player.userID.Get()) ? null : BoxedFalse;
    }

    private object CanAcceptItem(ItemContainer container, Item item, int targetPos)
    {
      var options = Configuration.TaxProtection;
      if (!item?.info ||
          item.info.itemid != options.CurrencyItemID || !options.Enabled ||
          !options.TaxCurrencyReservesEnabled ||
          options.MaxCurrencyReserves is 0 || container is null ||
          item.parent == container ||
          container.entityOwner is not BuildingPrivlidge privilege ||
          !IsTaxProtectionEnabledForPrivilege(privilege))
        return null;

      return (long)container.GetAmount(options.CurrencyItemID, false, false) +
        item.amount > options.MaxCurrencyReserves ?
        BoxedCannotAccept : null;
    }

#endregion Hooks

#region Methods

    private void InitializeTaxProtection()
    {
      if (!Configuration.TaxProtection.Enabled ||
          !Configuration.TaxProtection.EnableForModularBoats)
        PauseDisabledModularBoatTaxProtection(System.DateTime.UtcNow);

      if (!Configuration.TaxProtection.Enabled)
        return;

      _processQueuedTaxProtectionSyncsAction = ProcessQueuedTaxProtectionSyncs;
      RebuildTaxProtectionSyncIndex();
      if (ShouldTrackTaxProtectionReserveInventory)
      {
        Subscribe(nameof(OnItemAddedToContainer));
        Subscribe(nameof(OnItemRemovedFromContainer));
        Subscribe(nameof(OnItemStacked));
        Subscribe(nameof(OnItemUse));
      }

      InitializeTaxOverlay();
      QueueTaxProtectionSync();
      _taxProtectionTimer = timer.Every(
        TAX_PROTECTION_SYNC_INTERVAL, QueueTaxProtectionSync);
    }

    private void UnloadTaxProtection()
    {
      _taxProtectionTimer?.Destroy();
      _taxProtectionTimer = null;
      _queuedTaxProtectionSyncs.Clear();
      _queuedTaxProtectionSyncIds.Clear();
      _taxProtectionSyncBuildingIds.Clear();
      _taxProtectionSyncQueued = false;
      _processQueuedTaxProtectionSyncsAction = null;
      _processQueuedTaxProtectionRefundsAction = null;
      _taxProtectionPlayerIdsScratch.Clear();
      _modularBoatBuildingIdsScratch.Clear();
      Unsubscribe(nameof(OnItemAddedToContainer));
      Unsubscribe(nameof(OnItemRemovedFromContainer));
      Unsubscribe(nameof(OnItemStacked));
      Unsubscribe(nameof(OnItemUse));
      ClearTaxProtectionRefunds();
    }

    private void QueueTaxProtectionRefund(TaxProtectionRefund refund)
    {
      _queuedTaxProtectionRefunds.Enqueue(refund);
      if (_taxProtectionRefundsQueued)
        return;

      _taxProtectionRefundsQueued = true;
      _processQueuedTaxProtectionRefundsAction ??=
        ProcessQueuedTaxProtectionRefunds;
      NextFrame(_processQueuedTaxProtectionRefundsAction);
    }

    private void ProcessQueuedTaxProtectionRefunds()
    {
      _taxProtectionRefundsQueued = false;
      while (_queuedTaxProtectionRefunds.Count > 0)
        SpawnTaxProtectionRefund(_queuedTaxProtectionRefunds.Dequeue());
    }

    private void SpawnTaxProtectionRefund(TaxProtectionRefund refund)
    {
      var options = Configuration?.TaxProtection;
      if (options?.Enabled is not true || !options.RefundOnDestruction ||
          refund.CurrencyAmount <= 0 || refund.AuthorizedPlayerIds.Count is 0)
      {
        Facepunch.Pool.Free(ref refund);
        return;
      }

      var refundItem = ItemManager.CreateByItemID(
        Configuration.TaxProtection.CurrencyItemID, refund.CurrencyAmount);
      if (refundItem is null)
      {
        Facepunch.Pool.Free(ref refund);
        return;
      }

      var container = GameManager.server.CreateEntity(
        TAX_PROTECTION_REFUND_CONTAINER_PREFAB,
        refund.Position + Vector3.up * 0.25f) as DroppedItemContainer;
      if (!container)
      {
        refundItem.Remove();
        Facepunch.Pool.Free(ref refund);
        return;
      }

      container.enableSaving = false;
      container.OwnerID = refund.DestroyerID;
      container.inventory = new ItemContainer();
      container.inventory.ServerInitialize(null, 1);
      container.inventory.GiveItem(refundItem);
      container.Spawn();
      var containerNetworkID = GetNetworkID(container);
      if (containerNetworkID is 0UL)
      {
        container.Kill();
        Facepunch.Pool.Free(ref refund);
        return;
      }

      if (_taxProtectionRefundPouches.Count is 0)
        Subscribe(nameof(CanLootEntity));
      _taxProtectionRefundPouches[containerNetworkID] = refund;
      container.Invoke(
        () => container.Kill(), TAX_PROTECTION_REFUND_CONTAINER_LIFETIME);
    }

    private void QueueTaxProtectionSync()
    {
      if (!_serverInitialized || Configuration?.TaxProtection?.Enabled is not true)
        return;

      foreach (var buildingID in _taxProtectionSyncBuildingIds)
      {
        if (_queuedTaxProtectionSyncIds.Add(buildingID))
          _queuedTaxProtectionSyncs.Enqueue(buildingID);
      }

      if (_queuedTaxProtectionSyncs.Count is 0)
        return;

      if (_taxProtectionSyncQueued)
        return;

      _taxProtectionSyncQueued = true;
      QueueTaxProtectionSyncProcessing();
    }

    private void QueueTaxProtectionSync(uint buildingID)
    {
      if (!_serverInitialized || Configuration?.TaxProtection?.Enabled is not true ||
          buildingID is 0U || !_queuedTaxProtectionSyncIds.Add(buildingID))
        return;

      _queuedTaxProtectionSyncs.Enqueue(buildingID);
      if (_taxProtectionSyncQueued)
        return;

      _taxProtectionSyncQueued = true;
      QueueTaxProtectionSyncProcessing();
    }

    private void QueueTaxProtectionSyncProcessing() =>
      NextFrame(_processQueuedTaxProtectionSyncsAction);

    private void ProcessQueuedTaxProtectionSyncs()
    {
      if (!_serverInitialized || Configuration?.TaxProtection?.Enabled is not true)
      {
        _queuedTaxProtectionSyncs.Clear();
        _queuedTaxProtectionSyncIds.Clear();
        _taxProtectionSyncQueued = false;
        return;
      }

      var nowUtc = System.DateTime.UtcNow;
      var processed = 0;
      while (processed < TAX_PROTECTION_SYNC_BATCH_SIZE &&
             _queuedTaxProtectionSyncs.Count is not 0)
      {
        var buildingID = _queuedTaxProtectionSyncs.Dequeue();
        _queuedTaxProtectionSyncIds.Remove(buildingID);
        if (_tcCache.TryGetValue(buildingID, out var tcState) &&
            tcState.Privilege)
        {
          SyncPurchasedProtection(
            tcState.Privilege, tcState.CupboardNetworkID, nowUtc);
          MarkTaxOverlayDirty(tcState.CupboardNetworkID);
        }
        else
        {
          _taxProtectionSyncBuildingIds.Remove(buildingID);
        }
        processed++;
      }

      if (_queuedTaxProtectionSyncs.Count is not 0)
      {
        QueueTaxProtectionSyncProcessing();
        return;
      }

      _taxProtectionSyncQueued = false;
    }

    private void SyncPurchasedProtection(
      BuildingPrivlidge privilege, ulong cupboardNetworkID,
      System.DateTime nowUtc)
    {
      if (CanActivatePurchasedProtection(privilege, nowUtc))
      {
        if (_taxProtection.TryGetValue(cupboardNetworkID, out var state) &&
            state.GetRemainingTicks(nowUtc.Ticks) > 0L)
        {
          if (state.ActiveSinceTicks <= 0L)
            ResumePurchasedProtection(privilege, cupboardNetworkID, nowUtc);
          UpdateTaxProtectionSyncIndex(
            privilege, cupboardNetworkID, nowUtc.Ticks);
          return;
        }

        if (TryTopUpPurchasedProtection(privilege, cupboardNetworkID, nowUtc))
        {
          UpdateTaxProtectionSyncIndex(
            privilege, cupboardNetworkID, nowUtc.Ticks);
          return;
        }
      }

      if (PausePurchasedProtection(cupboardNetworkID, nowUtc))
      {
        QueueCupboardStatusHudRefresh(privilege);
        UpdateTcMarkerLabel(privilege);
      }
      MarkTaxOverlayDirty(cupboardNetworkID);
      UpdateTaxProtectionSyncIndex(
        privilege, cupboardNetworkID, nowUtc.Ticks);
    }

    private void PauseDisabledModularBoatTaxProtection(
      System.DateTime nowUtc)
    {
      foreach (var tcState in _tcCache.Values)
      {
        var privilege = tcState.Privilege;
        if (!privilege || !GetParentModularBoat(privilege) ||
            !PausePurchasedProtection(tcState.CupboardNetworkID, nowUtc))
          continue;

        QueueCupboardStatusHudRefresh(privilege);
        UpdateTcMarkerLabel(privilege);
        MarkTaxOverlayDirty(tcState.CupboardNetworkID);
      }
    }

    private bool ShouldTrackTaxProtectionReserveInventory =>
      Configuration?.TaxProtection is
      {
        Enabled: true,
        TaxCurrencyReservesEnabled: true,
        CurrencyItemID: not 0,
        CostPerHour: > 0,
        MaxPurchaseHours: > 0
      } && _taxProtectionCurrencyDefinition is not null;

    private void RebuildTaxProtectionSyncIndex()
    {
      _taxProtectionSyncBuildingIds.Clear();
      var nowTicks = System.DateTime.UtcNow.Ticks;
      foreach (var tcState in _tcCache.Values)
      {
        if (tcState.Privilege)
          UpdateTaxProtectionSyncIndex(
            tcState.Privilege, tcState.CupboardNetworkID, nowTicks);
      }
    }

    private void UpdateTaxProtectionSyncIndex(
      BuildingPrivlidge privilege, ulong cupboardNetworkID, long nowTicks)
    {
      if (!privilege || privilege.buildingID is 0U ||
          cupboardNetworkID is 0UL)
        return;

      if (!IsTaxProtectionEnabledForPrivilege(privilege))
      {
        _taxProtectionSyncBuildingIds.Remove(privilege.buildingID);
        return;
      }

      var hasPurchasedProtection =
        _taxProtection.TryGetValue(cupboardNetworkID, out var state) &&
        state.GetRemainingTicks(nowTicks) > 0L;
      var hasCurrencyReserves = ShouldTrackTaxProtectionReserveInventory &&
        privilege.inventory?.GetAmount(
          Configuration.TaxProtection.CurrencyItemID, false, false) > 0;

      if (hasPurchasedProtection || hasCurrencyReserves)
        _taxProtectionSyncBuildingIds.Add(privilege.buildingID);
      else
        _taxProtectionSyncBuildingIds.Remove(privilege.buildingID);
    }

    private bool PausePurchasedProtection(ulong cupboardNetworkID,
      System.DateTime nowUtc)
    {
      if (!_taxProtection.TryGetValue(cupboardNetworkID, out var state) ||
          state.ActiveSinceTicks <= 0L)
        return false;

      state.BankedTicks = state.GetRemainingTicks(nowUtc.Ticks);
      state.ActiveSinceTicks = 0L;
      MarkDataDirty();
      return true;
    }

    private void PauseActivePurchasedProtection(System.DateTime nowUtc)
    {
      var nowTicks = nowUtc.Ticks;
      var changed = false;
      foreach (var state in _taxProtection.Values)
      {
        if (state.ActiveSinceTicks <= 0L)
          continue;

        state.BankedTicks = state.GetRemainingTicks(nowTicks);
        state.ActiveSinceTicks = 0L;
        changed = true;
      }

      if (changed)
        MarkDataDirty();
    }

    private void ResumePurchasedProtection(BuildingPrivlidge privilege,
      ulong cupboardNetworkID, System.DateTime nowUtc)
    {
      if (!_taxProtection.TryGetValue(cupboardNetworkID, out var state) ||
          state.ActiveSinceTicks > 0L ||
          state.GetRemainingTicks(nowUtc.Ticks) <= 0L)
        return;

      state.ActiveSinceTicks = nowUtc.Ticks;
      MarkDataDirty();
      QueueCupboardStatusHudRefresh(privilege);
      UpdateTcMarkerLabel(privilege);
      MarkTaxOverlayDirty(cupboardNetworkID);
    }

#endregion Methods

#region Helper Methods

    private static PlayerBoat GetParentModularBoat(
      BuildingPrivlidge privilege) => privilege ?
        privilege.GetParentEntity() as PlayerBoat : null;

    private static BuildingPrivlidge GetModularBoatBuildingPrivilege(
      PlayerBoat modularBoat, VehiclePrivilege vehiclePrivilege)
    {
      if (!modularBoat || !vehiclePrivilege)
        return null;

      var privilege = vehiclePrivilege.GetBuildingPrivilege();
      return GetParentModularBoat(privilege) == modularBoat ? privilege : null;
    }

    private bool TryGetModularBoatTaxPrivilege(
      BasePlayer player, out BuildingPrivlidge privilege)
    {
      privilege = null;
      if (!Configuration.TaxProtection.EnableForModularBoats || !player ||
          player.GetVehicleBuildingPrivilege(false, 0f) is not
            VehiclePrivilege { ParentVehicle: PlayerBoat modularBoat })
        return false;

      var boatBuildingBlocks = modularBoat.BoatBuildingBlocks?.Cached;
      if (boatBuildingBlocks is null)
        return false;

      foreach (var boatBuildingBlock in boatBuildingBlocks)
      {
        if (boatBuildingBlock && TryGetModularBoatPrivilege(
              modularBoat, boatBuildingBlock.buildingID, out privilege))
          return true;
      }

      return false;
    }

    private bool IsTaxProtectionEnabledForPrivilege(
      BuildingPrivlidge privilege)
    {
      if (!privilege)
        return false;

      var modularBoat = GetParentModularBoat(privilege);
      if (!modularBoat)
        return true;

      return Configuration.TaxProtection.EnableForModularBoats &&
             TryGetModularBoatPrivilege(
               modularBoat, privilege.buildingID,
               out var physicalPrivilege) &&
             physicalPrivilege == privilege;
    }

    private bool TryGetModularBoatPrivilege(
      PlayerBoat modularBoat, uint buildingID,
      out BuildingPrivlidge privilege)
    {
      privilege = null;
      if (!modularBoat || buildingID is 0U ||
          !_tcCache.TryGetValue(buildingID, out var tcState) ||
          !tcState.Privilege ||
          GetParentModularBoat(tcState.Privilege) != modularBoat)
        return false;

      privilege = tcState.Privilege;
      return true;
    }

    private bool TryGetModularBoatTaxProtectionEndTicks(
      BaseCombatEntity entity, PlayerBoat modularBoat,
      System.DateTime nowUtc, out long endTicks)
    {
      endTicks = 0L;
      if (!Configuration.TaxProtection.EnableForModularBoats ||
          !modularBoat)
        return false;

      if (entity is DecayEntity component)
      {
        return TryGetModularBoatPrivilege(
                 modularBoat, component.buildingID, out var privilege) &&
               TryGetPurchasedProtectionEndTicks(
                 privilege, nowUtc, out endTicks);
      }

      var boatBuildingBlocks = modularBoat.BoatBuildingBlocks?.Cached;
      if (boatBuildingBlocks is null || boatBuildingBlocks.Count is 0)
        return false;

      var buildingIds = _modularBoatBuildingIdsScratch;
      buildingIds.Clear();
      foreach (var boatBuildingBlock in boatBuildingBlocks)
      {
        if (!boatBuildingBlock || boatBuildingBlock.buildingID is 0U)
        {
          buildingIds.Clear();
          return false;
        }

        buildingIds.Add(boatBuildingBlock.buildingID);
      }

      var earliestEndTicks = long.MaxValue;
      foreach (var buildingID in buildingIds)
      {
        if (!TryGetModularBoatPrivilege(
              modularBoat, buildingID, out var privilege) ||
            !TryGetPurchasedProtectionEndTicks(
              privilege, nowUtc, out var componentEndTicks))
        {
          buildingIds.Clear();
          return false;
        }

        if (componentEndTicks < earliestEndTicks)
          earliestEndTicks = componentEndTicks;
      }

      buildingIds.Clear();
      if (earliestEndTicks is long.MaxValue)
        return false;

      endTicks = earliestEndTicks;
      return true;
    }

    private bool TryGetPurchasedProtectionEndTicks(
      BuildingPrivlidge privilege, System.DateTime nowUtc, out long endTicks)
    {
      endTicks = 0L;
      if (!Configuration.TaxProtection.Enabled || !privilege)
        return false;

      var cupboardNetworkID = GetNetworkID(privilege);
      if (cupboardNetworkID is 0UL ||
          !_taxProtection.TryGetValue(cupboardNetworkID, out var state) ||
          state.ActiveSinceTicks <= 0L)
        return false;

      var remainingTicks = state.GetRemainingTicks(nowUtc.Ticks);
      if (remainingTicks <= 0L)
        return false;

      endTicks = nowUtc.Ticks > System.DateTime.MaxValue.Ticks - remainingTicks ?
        System.DateTime.MaxValue.Ticks : nowUtc.Ticks + remainingTicks;
      return true;
    }

    private bool CanActivatePurchasedProtection(
      BuildingPrivlidge privilege, System.DateTime nowUtc)
    {
      if (!Configuration.TaxProtection.Enabled ||
          !IsTaxProtectionEnabledForPrivilege(privilege))
        return false;

      var modularBoat = GetParentModularBoat(privilege);
      var cupboardNetworkID = GetNetworkID(privilege);
      if (!modularBoat && !Configuration.RaidProtection.ProtectGriefTcs &&
          _griefCupboardIds.Contains(cupboardNetworkID))
        return false;

      if (modularBoat)
      {
        if (!Configuration.RaidProtection.ProtectDecayingModularBoats &&
            IsCachedModularBoatDecaying(
              modularBoat, privilege.buildingID))
          return false;
      }
      else if (!Configuration.RaidProtection.ProtectDecayingBase &&
               _tcCache.TryGetValue(privilege.buildingID, out var tcState) &&
               tcState.IsDecaying)
      {
        return false;
      }

      var authorizedPlayers = _taxProtectionPlayerIdsScratch;
      authorizedPlayers.Clear();
      if (modularBoat)
      {
        if (!CollectBoatAuthorizedPlayers(
              null, modularBoat, authorizedPlayers))
          return false;
      }
      else
      {
        authorizedPlayers.AddRange(privilege.authorizedPlayers);
        if (Configuration.Team.IncludeWhitelistPlayers)
          authorizedPlayers.AddRange(GetCodeLockWhitelistPlayers(privilege));
      }

      if (authorizedPlayers.Count is 0 || authorizedPlayers.Overflowed ||
          !authorizedPlayers.First.IsSteamID())
        return false;

      var targetID = modularBoat ? modularBoat.OwnerID : privilege.OwnerID;
      if (targetID is 0UL || !authorizedPlayers.Contains(targetID))
        targetID = authorizedPlayers.First;

      targetID = GetRecentActiveMemberAll(
        targetID, authorizedPlayers, nowUtc);
      if (!_lastOnline.TryGetValue(targetID, out var targetLastOnline))
        return false;

      var targetScaleCache = GetOrCreateScaleCache(targetID);
      if (!targetScaleCache.HasTaxPermission ||
          !targetScaleCache.HasProtectPermission)
        return false;

      if (IsApiPenaltyActive(targetLastOnline, nowUtc))
        return false;

      if (!IsOnline(targetID))
        return IsOffline(targetID, targetLastOnline, nowUtc);

      if (!Configuration.RaidProtection.OnlineRaidProtection &&
          !targetScaleCache.HasOnlineProtectPermission)
        return false;

      var scale = GetDamageScale(
        targetID, targetLastOnline, targetScaleCache, nowUtc);
      return scale is not -1f && scale < 1f;
    }

    private bool TryTopUpPurchasedProtection(
      BuildingPrivlidge privilege, ulong cupboardNetworkID,
      System.DateTime nowUtc)
    {
      var options = Configuration.TaxProtection;
      if (!options.Enabled || !options.TaxCurrencyReservesEnabled ||
          !IsTaxProtectionEnabledForPrivilege(privilege) ||
          cupboardNetworkID is 0UL ||
          privilege.inventory is null || options.CurrencyItemID is 0 ||
          options.CostPerHour <= 0 ||
          _taxProtectionCurrencyDefinition is null)
        return false;

      var nowTicks = nowUtc.Ticks;
      var costPerHour = GetTaxProtectionCostPerHour(privilege);
      if (_taxProtection.TryGetValue(cupboardNetworkID, out var state) &&
          state.GetRemainingTicks(nowTicks) > 0L)
        return false;

      if (_maxPurchasedProtectionTicks < System.TimeSpan.TicksPerHour ||
          privilege.inventory.GetAmount(
            options.CurrencyItemID, false, false) < costPerHour)
        return false;

      privilege.inventory.Take(null, options.CurrencyItemID, costPerHour);
      state ??= new TaxProtectionState();
      state.BankedTicks = System.TimeSpan.TicksPerHour;
      state.ActiveSinceTicks = nowTicks;
      _taxProtection[cupboardNetworkID] = state;
      MarkDataDirty();
      QueueCupboardStatusHudRefresh(privilege);
      UpdateTcMarkerLabel(privilege);
      MarkTaxOverlayDirty(cupboardNetworkID);
      return true;
    }

#endregion Helper Methods

#region Commands

    private void cmdBuyTaxProtection(
      BasePlayer player, string _command, string[] args)
    {
      if (!player || !Configuration.TaxProtection.Enabled)
        return;

#if !CARBON
      if (!CheckChatCmdPerm(player, Configuration.Permission.TaxProtection))
        return;
#endif

      var argCount = args?.Length ?? 0;
      var requestedHours = 0;
      if (argCount > 1 ||
          argCount is 1 &&
          (!int.TryParse(args[0], out requestedHours) || requestedHours <= 0))
      {
        ChatMessage(player,
          $"Usage: /{Configuration.Command.CommandTaxProtection} <hours>");
        return;
      }

      _ray.origin = player.eyes.position;
      _ray.direction = player.eyes.HeadForward();
      BuildingPrivlidge privilege = null;
      if (Physics.RaycastNonAlloc(
            _ray, RaycastHits, 4f, Rust.Layers.Mask.Deployed) > 0)
      {
        var hitEntity = RaycastHits[0].GetEntity();
        privilege = hitEntity as BuildingPrivlidge ??
          hitEntity?.GetParentEntity() as BuildingPrivlidge;
      }

      privilege ??= player.GetBuildingPrivilege();
      if (!privilege)
        TryGetModularBoatTaxPrivilege(player, out privilege);
      if (!privilege)
      {
        ChatMessage(player,
          "You must be looking at or standing near a Tool Cupboard.");
        return;
      }

      if (!IsTrustedForCupboard(player, privilege))
      {
        ChatMessage(player, "You are not trusted at this building.");
        return;
      }

      if (!IsTaxProtectionEnabledForPrivilege(privilege))
      {
        ChatMessage(player,
          "Tax Protection is unavailable for this modular boat Tool Cupboard.");
        return;
      }

      var options = Configuration.TaxProtection;
      if (options.CurrencyItemID is 0 || options.CostPerHour <= 0 ||
          _taxProtectionCurrencyDefinition is null)
        return;

      if (argCount is 0)
      {
        SendPurchasedProtectionStatus(player, privilege);
        return;
      }

      var nowTicks = System.DateTime.UtcNow.Ticks;
      var cupboardNetworkID = GetNetworkID(privilege);
      if (cupboardNetworkID is 0UL)
        return;

      if (!TryBuyTaxProtectionHours(player, privilege, cupboardNetworkID,
            requestedHours, out var purchasedHours))
      {
        SendPurchasedProtectionStatus(player, privilege);
        return;
      }
      SyncPurchasedProtection(privilege, cupboardNetworkID,
        new System.DateTime(nowTicks));
      MarkTaxOverlayDirty(cupboardNetworkID);
      QueueCupboardStatusHudRefresh(privilege);
      UpdateTcMarkerLabel(privilege);
      SendPurchasedProtectionStatus(player, privilege, purchasedHours);
    }

    private void SendPurchasedProtectionStatus(
      BasePlayer player, BuildingPrivlidge privilege, int purchasedHours = 0)
    {
      var options = Configuration.TaxProtection;
      if (!options.Enabled || !player ||
          !IsTaxProtectionEnabledForPrivilege(privilege) ||
          privilege.inventory is null ||
          options.CurrencyItemID is 0 || options.CostPerHour <= 0)
        return;

      var nowUtc = System.DateTime.UtcNow;
      var cupboardNetworkID = GetNetworkID(privilege);
      var costPerHour = GetTaxProtectionCostPerHour(privilege);
      var bankedTicks = _taxProtection.TryGetValue(
        cupboardNetworkID, out var state) ?
          state.GetRemainingTicks(nowUtc.Ticks) : 0L;
      var reservesEnabled = options.TaxCurrencyReservesEnabled;
      var reserves = 0;
      var totalTicks = bankedTicks;
      if (reservesEnabled)
      {
        reserves = privilege.inventory.GetAmount(
          options.CurrencyItemID, false, false);
        var reserveHours = reserves / costPerHour;
        var maxTimeSpanTicks = System.TimeSpan.MaxValue.Ticks;
        var reserveTicks =
          reserveHours > maxTimeSpanTicks / System.TimeSpan.TicksPerHour ?
            maxTimeSpanTicks : reserveHours * System.TimeSpan.TicksPerHour;
        totalTicks = bankedTicks > maxTimeSpanTicks - reserveTicks ?
          maxTimeSpanTicks : bankedTicks + reserveTicks;
      }

      _sb.Clear();
      _sb.AppendLine("<color=" + COLOR_BLUE + ">Tax Protection Status</color>");
      if (purchasedHours > 0)
      {
        _sb.Append("<color=" + COLOR_AQUA + ">Tax Payment</color> <color=" + COLOR_GREEN + ">+")
          .Append(purchasedHours)
          .AppendLine(" hour(s)</color>");
      }

      _sb.Append("<color=" + COLOR_AQUA + ">Cost per Hour</color> <color=" + COLOR_ORANGE + ">")
        .Append(costPerHour)
        .Append(' ')
        .Append(_taxProtectionCurrencyName)
        .AppendLine("</color>");

      _sb.Append("<color=" + COLOR_YELLOW + ">Current Tax Protection</color> ");
      if (bankedTicks > 0L)
      {
        _sb.Append("<color=" + COLOR_GREEN + ">");
        AppendFormattedDuration(
          _sb,
          GetDurationSecondsFromMinutes(
            GetTaxOverlayCeilingMinutes(bankedTicks)), includeDays: true);
        _sb.AppendLine("</color>");
      }
      else
        _sb.AppendLine("<color=" + COLOR_RED + ">Inactive</color>");

      if (reservesEnabled)
      {
        _sb.Append("<color=" + COLOR_AQUA + ">Tax Reserves</color> <color=" + COLOR_ORANGE + ">")
          .Append(reserves)
          .Append('/');

        if (options.MaxCurrencyReserves is 0)
          _sb.Append(TAX_OVERLAY_UNLIMITED_TEXT);
        else
          _sb.Append(options.MaxCurrencyReserves);

        _sb.Append(' ')
          .Append(_taxProtectionCurrencyName)
          .AppendLine("</color>");

        _sb.Append("<color=" + COLOR_YELLOW + ">Total Tax Protection</color> ");
        if (totalTicks > 0L)
        {
          _sb.Append("<color=" + COLOR_GREEN + ">");
          AppendFormattedDuration(
            _sb,
            GetDurationSecondsFromMinutes(
              GetTaxOverlayCeilingMinutes(totalTicks)), includeDays: true);
          _sb.AppendLine("</color>");
        }
        else
          _sb.Append("<color=" + COLOR_RED + ">Inactive</color>");

        _sb.Append("<color=" + COLOR_ORANGE + ">/")
          .Append(options.MaxPurchaseHours)
          .AppendLine("h</color>");
      }

      ChatMessage(player, _sb.ToString());
    }

#endregion Commands

#region Tax Overlay

#region Fields

    private readonly Dictionary<ulong, TaxOverlayViewer> _taxOverlayViewersByPlayer = new();
    private readonly Dictionary<ulong, HashSet<ulong>> _taxOverlayViewersByTc = new();
    private readonly Dictionary<ulong, ulong> _pendingTaxOverlayTcByPlayer = new();
    private readonly HashSet<ulong> _dirtyTaxOverlayTcs = new();
    private readonly List<ulong> _taxOverlayViewerScratch = new();
    private readonly StringBuilder _taxOverlayBuilder = new(2048);
    private bool _isTaxOverlayEnabled;
    private bool _taxOverlayOpenQueued;
    private System.Action _openPendingTaxOverlaysAction;
    private bool _taxOverlayRefreshQueued;
    private System.Action _refreshDirtyTaxOverlaysAction;
    private System.Action _refreshTaxOverlayMinuteBoundaryAction;
    private bool _taxOverlayTrackingStopQueued;
    private System.Action _stopTaxOverlayViewerTrackingAction;
    private PluginTimer _taxOverlayMinuteRefreshTimer;
#if CARBON
    private LuiPosition _taxOverlayPosition;
    private LuiOffset _taxOverlayOffset;
#else
    private string _taxOverlayPayloadPrefix;
    private string _taxOverlayPayloadRootSuffix;
#endif

#region Constants

    private const string TAX_OVERLAY_ROOT = "ORP.TaxOverlay.";
    private const string TAX_OVERLAY_COMMAND = "orp.taxoverlay";
    private const string TAX_OVERLAY_TITLE = "TAX MANAGEMENT";
    private const string TAX_OVERLAY_CLOSE_TEXT = "X";
    private const string TAX_OVERLAY_BUTTON_BUY_TEXT = "BUY 1 HOUR";
    private const string TAX_OVERLAY_BUTTON_BUY_MAX_TEXT = "BUY MAX HOURS (";
    private const string TAX_OVERLAY_STATUS_LABEL = "Status: ";
    private const string TAX_OVERLAY_COSTS_LABEL = "Costs: ";
    private const string TAX_OVERLAY_RESERVES_LABEL = "Reserves: ";
    private const string TAX_OVERLAY_TOTAL_LABEL = "Total: ";
    private const string TAX_OVERLAY_SEPARATOR_TEXT = " / ";
    private const string TAX_OVERLAY_UNLIMITED_TEXT = "Unlimited";
    private const string TAX_OVERLAY_BACKGROUND_COLOR = "0.14 0.14 0.14 0.88";
    private const string TAX_OVERLAY_HEADER_BG_COLOR = "0.20 0.20 0.20 0.95";
    private const string TAX_OVERLAY_TEXT_COLOR = "1 1 1 1";
    private const string TAX_OVERLAY_TRANSPARENT_COLOR = "0 0 0 0";
    private const string TAX_OVERLAY_ENABLED_BUTTON_COLOR = "0.28 0.40 0.22 0.95";
    private const string TAX_OVERLAY_DISABLED_BUTTON_COLOR = "0.20 0.20 0.20 0.60";
    private const string TAX_OVERLAY_DISABLED_TEXT_COLOR = "0.55 0.55 0.55 0.60";
    private const string TAX_OVERLAY_LABEL_COLOR = "#8E9398";
    private const string TAX_OVERLAY_VALUE_COLOR = "#C5C5C5";
    private const string TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX = "<color=";
    private const char TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX = '>';
    private const string TAX_OVERLAY_RICH_TEXT_CLOSE_TAG = "</color>";
    private const string TAX_OVERLAY_ACTION_BUY = "buy";
    private const string TAX_OVERLAY_ACTION_BUY_MAX = "buymax";
    private const string TAX_OVERLAY_ACTION_CLOSE = "close";
    private const string TAX_OVERLAY_COMMAND_PREFIX = TAX_OVERLAY_COMMAND + " ";
    private const int TAX_OVERLAY_HEADER_FONT_SIZE = 13;
    private const int TAX_OVERLAY_TEXT_FONT_SIZE = 12;
    private const int TAX_OVERLAY_BUTTON_FONT_SIZE = 11;
    private const float TAX_OVERLAY_BUY_BUTTON_MIN_X = 8f;
    private const float TAX_OVERLAY_BUY_BUTTON_MIN_Y = 8f;
    private const float TAX_OVERLAY_BUY_BUTTON_MAX_X = 127f;
    private const float TAX_OVERLAY_BUY_BUTTON_MAX_Y = 31f;
    private const float TAX_OVERLAY_BUY_MAX_BUTTON_MIN_X = 133f;
    private const float TAX_OVERLAY_BUY_MAX_BUTTON_MIN_Y = 8f;
    private const float TAX_OVERLAY_BUY_MAX_BUTTON_MAX_X = 252f;
    private const float TAX_OVERLAY_BUY_MAX_BUTTON_MAX_Y = 31f;
    private const float TAX_OVERLAY_TEXT_HALF_WIDTH = 90f;
    private const float TAX_OVERLAY_MINUTE_BOUNDARY_EPSILON = 0.05f;
    private const byte
      TAX_OVERLAY_BUY = 1,
      TAX_OVERLAY_BUY_MAX = 2;
#if !CARBON
    private const string TAX_OVERLAY_HEADER_NAME_SUFFIX = ".Header";
    private const string TAX_OVERLAY_HEADER_TEXT_NAME_SUFFIX = ".Header.Text";
    private const string TAX_OVERLAY_BODY_NAME_SUFFIX = ".Body";
    private const string TAX_OVERLAY_CLOSE_NAME_SUFFIX = ".Close";
    private const string TAX_OVERLAY_CLOSE_TEXT_NAME_SUFFIX = ".Close.Text";
    private const string TAX_OVERLAY_BUY_NAME_SUFFIX = ".Buy";
    private const string TAX_OVERLAY_BUY_TEXT_NAME_SUFFIX = ".Buy.Text";
    private const string TAX_OVERLAY_BUY_MAX_NAME_SUFFIX = ".BuyMax";
    private const string TAX_OVERLAY_BUY_MAX_TEXT_NAME_SUFFIX = ".BuyMax.Text";
    private const string TAX_OVERLAY_PAYLOAD_ELEMENT_PREFIX = ",{\"name\":\"";
    private const string TAX_OVERLAY_PAYLOAD_PARENT_PREFIX = "\",\"parent\":\"";
    private const string TAX_OVERLAY_PAYLOAD_COMPONENTS_PREFIX = "\",\"components\":[";
    private static readonly string TAX_OVERLAY_PAYLOAD_HEADER_COMPONENTS =
      "{\"type\":\"UnityEngine.UI.Image\",\"color\":\"" + TAX_OVERLAY_HEADER_BG_COLOR +
      "\"},{\"type\":\"RectTransform\",\"anchormin\":\"0 1\",\"anchormax\":\"1 1\",\"offsetmin\":\"0 -24\",\"offsetmax\":\"0 0\"}]}";
    private static readonly string TAX_OVERLAY_PAYLOAD_HEADER_TEXT_COMPONENTS =
      "{\"type\":\"UnityEngine.UI.Text\",\"text\":\"" + TAX_OVERLAY_TITLE + "\",\"fontSize\":" + TAX_OVERLAY_HEADER_FONT_SIZE + ",\"align\":\"MiddleCenter\",\"font\":\"" + FONT_ROBOTO_CONDENSED_BOLD + "\",\"color\":\"" + TAX_OVERLAY_TEXT_COLOR + "\"},{\"type\":\"RectTransform\",\"anchormin\":\"0 0\",\"anchormax\":\"1 1\",\"offsetmin\":\"0 0\",\"offsetmax\":\"0 0\"}]}";
    private const string TAX_OVERLAY_PAYLOAD_BODY_TEXT_PREFIX =
      "{\"type\":\"UnityEngine.UI.Text\",\"text\":\"";
    private static readonly string TAX_OVERLAY_PAYLOAD_BODY_TEXT_SUFFIX =
      "\",\"fontSize\":" + TAX_OVERLAY_TEXT_FONT_SIZE + ",\"align\":\"MiddleLeft\",\"font\":\"" + FONT_ROBOTO_CONDENSED_BOLD + "\",\"color\":\"" + TAX_OVERLAY_TEXT_COLOR + "\"},{\"type\":\"RectTransform\",\"anchormin\":\"0.5 0\",\"anchormax\":\"0.5 1\",\"offsetmin\":\"-90 35\",\"offsetmax\":\"90 -28\"}]}";
    private const string TAX_OVERLAY_PAYLOAD_BUTTON_COLOR_PREFIX =
      "{\"type\":\"UnityEngine.UI.Button\",\"color\":\"";
    private const string TAX_OVERLAY_PAYLOAD_BUTTON_COMMAND_PREFIX =
      "\",\"command\":\"";
    private const string TAX_OVERLAY_PAYLOAD_CLOSE_BUTTON_SUFFIX =
      "\"},{\"type\":\"RectTransform\",\"anchormin\":\"1 0\",\"anchormax\":\"1 1\",\"offsetmin\":\"-28 0\",\"offsetmax\":\"0 0\"}]}";
    private const string TAX_OVERLAY_PAYLOAD_BUY_BUTTON_SUFFIX =
      "\"},{\"type\":\"RectTransform\",\"anchormin\":\"0 0\",\"anchormax\":\"0 0\",\"offsetmin\":\"8 8\",\"offsetmax\":\"127 31\"}]}";
    private const string TAX_OVERLAY_PAYLOAD_BUY_MAX_BUTTON_SUFFIX =
      "\"},{\"type\":\"RectTransform\",\"anchormin\":\"0 0\",\"anchormax\":\"0 0\",\"offsetmin\":\"133 8\",\"offsetmax\":\"252 31\"}]}";
    private const string TAX_OVERLAY_PAYLOAD_LABEL_TEXT_PREFIX =
      "{\"type\":\"UnityEngine.UI.Text\",\"text\":\"";
    private static readonly string TAX_OVERLAY_PAYLOAD_CLOSE_LABEL_SUFFIX =
      "\",\"fontSize\":" + TAX_OVERLAY_HEADER_FONT_SIZE + ",\"align\":\"MiddleCenter\",\"font\":\"" + FONT_ROBOTO_CONDENSED_BOLD + "\",\"color\":\"" + TAX_OVERLAY_TEXT_COLOR + "\"},{\"type\":\"RectTransform\",\"anchormin\":\"0 0\",\"anchormax\":\"1 1\",\"offsetmin\":\"0 0\",\"offsetmax\":\"0 0\"}]}";
    private static readonly string TAX_OVERLAY_PAYLOAD_BUTTON_LABEL_COLOR_PREFIX =
      "\",\"fontSize\":" + TAX_OVERLAY_BUTTON_FONT_SIZE + ",\"align\":\"MiddleCenter\",\"font\":\"" + FONT_ROBOTO_CONDENSED_BOLD + "\",\"color\":\"";
    private const string TAX_OVERLAY_PAYLOAD_BUTTON_LABEL_SUFFIX =
      "\"},{\"type\":\"RectTransform\",\"anchormin\":\"0 0\",\"anchormax\":\"1 1\",\"offsetmin\":\"0 0\",\"offsetmax\":\"0 0\"}]}";
#endif

#endregion Constants

#endregion Fields

#region Classes

    private enum TaxOverlayState : byte
    {
      Inactive,
      Active,
      Paused
    }

    private readonly struct TaxOverlaySnapshot
    {
      public readonly ulong TcID;
      public readonly long RemainingMinutes;
      public readonly long TotalMinutes;
      public readonly int Reserves;
      public readonly int ReserveCap;
      public readonly int Cost;
      public readonly int BuyMaxHours;
      public readonly bool HasCurrencyReserves;
      public readonly TaxOverlayState State;
      public readonly byte Actions;

      public TaxOverlaySnapshot(ulong tcId, bool hasCurrencyReserves,
        int reserves, int reserveCap,
        int cost, long remainingMinutes, long totalMinutes, int buyMaxHours,
        TaxOverlayState state, byte actions)
      {
        TcID = tcId;
        RemainingMinutes = remainingMinutes;
        TotalMinutes = totalMinutes;
        Reserves = reserves;
        ReserveCap = reserveCap;
        Cost = cost;
        BuyMaxHours = buyMaxHours;
        HasCurrencyReserves = hasCurrencyReserves;
        State = state;
        Actions = actions;
      }

      public bool Matches(in TaxOverlaySnapshot other) =>
        TcID == other.TcID &&
        RemainingMinutes == other.RemainingMinutes &&
        TotalMinutes == other.TotalMinutes &&
        Reserves == other.Reserves &&
        ReserveCap == other.ReserveCap &&
        Cost == other.Cost &&
        BuyMaxHours == other.BuyMaxHours &&
        HasCurrencyReserves == other.HasCurrencyReserves &&
        State == other.State &&
        Actions == other.Actions;
    }

    private sealed class TaxOverlayViewer : Facepunch.Pool.IPooled
    {
      public ulong TcID;
      public string PlayerID;
      public string RootName;
      public string BuyCommand;
      public string BuyMaxCommand;
      public string CloseCommand;
      public TaxOverlaySnapshot LastSnapshot;

      public void Initialize(ulong playerID, ulong tcId)
      {
        TcID = tcId;
        PlayerID = playerID.ToString();
        RootName = TAX_OVERLAY_ROOT + PlayerID;
        var id = tcId.ToString(CultureInfo.InvariantCulture);
        BuyCommand = TAX_OVERLAY_COMMAND_PREFIX + TAX_OVERLAY_ACTION_BUY + " " + id;
        BuyMaxCommand = TAX_OVERLAY_COMMAND_PREFIX + TAX_OVERLAY_ACTION_BUY_MAX + " " + id;
        CloseCommand = TAX_OVERLAY_COMMAND_PREFIX + TAX_OVERLAY_ACTION_CLOSE + " " + id;
      }

      public void EnterPool()
      {
        TcID = 0UL;
        PlayerID = null;
        RootName = null;
        BuyCommand = null;
        BuyMaxCommand = null;
        CloseCommand = null;
        LastSnapshot = default;
      }

      public void LeavePool() { }
    }

#endregion Classes

#region Hooks

    private void OnLootEntity(BasePlayer player, BuildingPrivlidge targetEntity)
    {
      if (!_serverInitialized || !player || !targetEntity ||
          !_isTaxOverlayEnabled ||
          !IsTaxProtectionEnabledForPrivilege(targetEntity) ||
          !HasTaxProtectionPermission(player))
        return;

      var playerID = player.userID.Get();
      var tcID = GetNetworkID(targetEntity);
      if (tcID is 0UL ||
          _taxOverlayViewersByPlayer.TryGetValue(playerID, out var viewer) &&
          viewer.TcID == tcID)
        return;

      _pendingTaxOverlayTcByPlayer[playerID] = tcID;
      if (_taxOverlayOpenQueued)
        return;

      _taxOverlayOpenQueued = true;

      NextFrame(_openPendingTaxOverlaysAction);
    }

    private void OnLootEntityEnd(
      BasePlayer player, BuildingPrivlidge targetEntity)
    {
      if (!player || !targetEntity)
        return;

      var playerID = player.userID.Get();
      if (!_taxOverlayViewersByPlayer.TryGetValue(playerID, out var viewer) ||
          viewer.TcID != GetNetworkID(targetEntity))
        return;

      CloseTaxOverlay(player, playerID);
    }

    private void OnItemAddedToContainer(ItemContainer container, Item item) =>
      HandleTaxProtectionInventoryChange(container, item);

    private void OnItemRemovedFromContainer(ItemContainer container, Item item) =>
      HandleTaxProtectionInventoryChange(container, item);

    private void OnItemStacked(
      Item target, Item source, ItemContainer destination, int _amount)
    {
      HandleTaxProtectionInventoryChange(destination, target);
      if (source?.parent != destination)
        HandleTaxProtectionInventoryChange(source?.parent, source);
    }

    private void OnItemUse(Item item, int _amountToConsume) =>
      HandleTaxProtectionInventoryChange(item?.parent, item);

#endregion Hooks

#region Lifecycle

    private void InitializeTaxOverlay()
    {
      if (!_isTaxOverlayEnabled)
        return;

      _openPendingTaxOverlaysAction = OpenPendingTaxOverlays;
      _refreshDirtyTaxOverlaysAction = RefreshDirtyTaxOverlays;
      _refreshTaxOverlayMinuteBoundaryAction = RefreshTaxOverlayMinuteBoundary;
      _stopTaxOverlayViewerTrackingAction = StopTaxOverlayViewerTrackingIfIdle;

      var overlay = Configuration.TaxProtection.TaxOverlay;
#if CARBON
      TryParseAnchor(overlay.AnchorMin, out var minX, out var minY);
      TryParseAnchor(overlay.AnchorMax, out var maxX, out var maxY);
      TryParseOffset(overlay.OffsetMin, out var minOffsetX, out var minOffsetY);
      TryParseOffset(overlay.OffsetMax, out var maxOffsetX, out var maxOffsetY);
      _taxOverlayPosition = new(minX, minY, maxX, maxY);
      _taxOverlayOffset = new(minOffsetX, minOffsetY, maxOffsetX, maxOffsetY);
#else
      _taxOverlayPayloadPrefix = "[{\"name\":\"";
      _taxOverlayPayloadRootSuffix =
        "\",\"parent\":\"Overlay\",\"components\":[{\"type\":\"UnityEngine.UI.Image\",\"color\":\"" +
        TAX_OVERLAY_BACKGROUND_COLOR + "\"},{\"type\":\"RectTransform\",\"anchormin\":\"" +
        overlay.AnchorMin + "\",\"anchormax\":\"" + overlay.AnchorMax +
        "\",\"offsetmin\":\"" + overlay.OffsetMin + "\",\"offsetmax\":\"" +
        overlay.OffsetMax + "\"},{\"type\":\"NeedsCursor\"},{\"type\":\"UnityEngine.UI.CanvasGroup\",\"blocksRaycasts\":true,\"interactable\":true}]}";
#endif
    }

    private void CacheTaxOverlayEnabled() =>
      _isTaxOverlayEnabled = Configuration?.TaxProtection is
        { Enabled: true, TaxOverlay.Enabled: true };


    private void OpenTaxOverlay(BasePlayer player, BuildingPrivlidge privilege)
    {
      if (!player || !privilege || !_isTaxOverlayEnabled ||
          !IsTaxProtectionEnabledForPrivilege(privilege) ||
          player.inventory.loot.entitySource != privilege ||
          !HasTaxProtectionPermission(player) || !IsTrustedForCupboard(player, privilege))
        return;

      var playerID = player.userID.Get();
      var tcID = GetNetworkID(privilege);
      if (tcID is 0UL)
        return;

      if (_taxOverlayViewersByPlayer.TryGetValue(playerID, out var existingViewer) &&
          existingViewer.TcID == tcID)
        return;

      CloseTaxOverlay(player, playerID);
      var viewer = Facepunch.Pool.Get<TaxOverlayViewer>();
      viewer.Initialize(playerID, tcID);
      _taxOverlayViewersByPlayer[playerID] = viewer;
      if (!_taxOverlayViewersByTc.TryGetValue(tcID, out var viewers))
      {
        viewers = Facepunch.Pool.Get<HashSet<ulong>>();
        _taxOverlayViewersByTc[tcID] = viewers;
      }
      viewers.Add(playerID);
      if (_taxOverlayViewersByPlayer.Count is 1)
        StartTaxOverlayViewerTracking();
      RenderTaxOverlay(player, privilege, viewer, force: true);
    }

    private void OpenPendingTaxOverlays()
    {
      _taxOverlayOpenQueued = false;
      _taxOverlayViewerScratch.Clear();
      foreach (var playerID in _pendingTaxOverlayTcByPlayer.Keys)
        _taxOverlayViewerScratch.Add(playerID);

      foreach (var playerID in _taxOverlayViewerScratch)
      {
        if (_pendingTaxOverlayTcByPlayer.Remove(playerID, out var tcId))
          OpenTaxOverlay(_players.GetPlayer(playerID), FindTaxCupboard(tcId));
      }
    }

    private bool HasTaxProtectionPermission(BasePlayer player) =>
      player && GetOrCreateScaleCache(player.userID.Get()).HasTaxPermission;

    private void CloseTaxOverlay(BasePlayer player, ulong playerID)
    {
      _pendingTaxOverlayTcByPlayer.Remove(playerID);
      var removedViewer =
        _taxOverlayViewersByPlayer.Remove(playerID, out var viewer);
      if (removedViewer &&
          _taxOverlayViewersByTc.TryGetValue(viewer.TcID, out var viewers))
      {
        viewers.Remove(playerID);
        if (viewers.Count is 0)
        {
          _taxOverlayViewersByTc.Remove(viewer.TcID);
          Facepunch.Pool.FreeUnmanaged(ref viewers);
        }
      }
      if (removedViewer && _taxOverlayViewersByPlayer.Count is 0)
        QueueTaxOverlayViewerTrackingStop();
      if (viewer is not null)
      {
        if (player)
          DestroyTaxOverlay(player, viewer.RootName);
        Facepunch.Pool.Free(ref viewer);
      }
    }

    private void StartTaxOverlayViewerTracking()
    {
      if (_taxOverlayTrackingStopQueued)
      {
        _taxOverlayTrackingStopQueued = false;
        if (_taxOverlayMinuteRefreshTimer is null)
          ScheduleTaxOverlayMinuteRefresh();
        return;
      }

      Subscribe(nameof(OnLootEntityEnd));
      Subscribe(nameof(OnItemAddedToContainer));
      Subscribe(nameof(OnItemRemovedFromContainer));
      Subscribe(nameof(OnItemStacked));
      Subscribe(nameof(OnItemUse));
      ScheduleTaxOverlayMinuteRefresh();
    }

    private void QueueTaxOverlayViewerTrackingStop()
    {
      if (_taxOverlayTrackingStopQueued)
        return;

      _taxOverlayTrackingStopQueued = true;
      NextFrame(_stopTaxOverlayViewerTrackingAction);
    }

    private void StopTaxOverlayViewerTrackingIfIdle()
    {
      if (!_taxOverlayTrackingStopQueued)
        return;

      _taxOverlayTrackingStopQueued = false;
      if (_taxOverlayViewersByPlayer.Count is 0)
        StopTaxOverlayViewerTracking();
    }

    private void StopTaxOverlayViewerTracking()
    {
      _taxOverlayTrackingStopQueued = false;
      Unsubscribe(nameof(OnLootEntityEnd));
      if (!ShouldTrackTaxProtectionReserveInventory)
      {
        Unsubscribe(nameof(OnItemAddedToContainer));
        Unsubscribe(nameof(OnItemRemovedFromContainer));
        Unsubscribe(nameof(OnItemStacked));
        Unsubscribe(nameof(OnItemUse));
      }
      _taxOverlayMinuteRefreshTimer?.Destroy();
      _taxOverlayMinuteRefreshTimer = null;
    }

    private void ScheduleTaxOverlayMinuteRefresh()
    {
      if (_taxOverlayViewersByPlayer.Count is 0)
        return;

      var nowTicks = System.DateTime.UtcNow.Ticks;
      var ticksToNextMinute = System.TimeSpan.TicksPerMinute -
        nowTicks % System.TimeSpan.TicksPerMinute;
      _taxOverlayMinuteRefreshTimer = timer.Once(
        (float)(ticksToNextMinute /
          (double)System.TimeSpan.TicksPerSecond) +
        TAX_OVERLAY_MINUTE_BOUNDARY_EPSILON,
        _refreshTaxOverlayMinuteBoundaryAction);
    }

    private void RefreshTaxOverlayMinuteBoundary()
    {
      _taxOverlayMinuteRefreshTimer = null;
      if (_taxOverlayViewersByPlayer.Count is 0)
        return;

      QueueTaxOverlayRefresh();
      ScheduleTaxOverlayMinuteRefresh();
    }

    private void CloseTaxOverlayViewers(ulong tcID)
    {
      if (tcID is 0UL || !_taxOverlayViewersByTc.TryGetValue(tcID, out var viewers))
        return;

      _taxOverlayViewerScratch.Clear();
      foreach (var playerID in viewers)
        _taxOverlayViewerScratch.Add(playerID);
      foreach (var playerID in _taxOverlayViewerScratch)
        CloseTaxOverlay(_players.GetPlayer(playerID), playerID);
    }

    private void CloseTaxOverlayViewersWithoutPermission()
    {
      _taxOverlayViewerScratch.Clear();
      foreach (var playerID in _taxOverlayViewersByPlayer.Keys)
        _taxOverlayViewerScratch.Add(playerID);
      foreach (var playerID in _taxOverlayViewerScratch)
      {
        var player = _players.GetPlayer(playerID);
        if (!player || !HasTaxProtectionPermission(player))
          CloseTaxOverlay(player, playerID);
      }
    }

    private void UnloadTaxOverlay()
    {
      foreach (var (playerID, viewer) in _taxOverlayViewersByPlayer)
      {
        DestroyTaxOverlay(_players.GetPlayer(playerID), viewer.RootName);
        var pooledViewer = viewer;
        Facepunch.Pool.Free(ref pooledViewer);
      }
      _taxOverlayViewersByPlayer.Clear();
      foreach (var viewers in _taxOverlayViewersByTc.Values)
      {
        var pooledViewers = viewers;
        Facepunch.Pool.FreeUnmanaged(ref pooledViewers);
      }
      _taxOverlayViewersByTc.Clear();
      _pendingTaxOverlayTcByPlayer.Clear();
      _taxOverlayOpenQueued = false;
      _openPendingTaxOverlaysAction = null;
      StopTaxOverlayViewerTracking();
      _dirtyTaxOverlayTcs.Clear();
      _taxOverlayViewerScratch.Clear();
      _taxOverlayBuilder.Clear();
      _taxOverlayRefreshQueued = false;
      _refreshDirtyTaxOverlaysAction = null;
      _refreshTaxOverlayMinuteBoundaryAction = null;
      _stopTaxOverlayViewerTrackingAction = null;
      _isTaxOverlayEnabled = false;
#if !CARBON
      _taxOverlayPayloadPrefix = null;
      _taxOverlayPayloadRootSuffix = null;
#endif
    }

#endregion Lifecycle

#region Refresh

    private void HandleTaxProtectionInventoryChange(
      ItemContainer container, Item item)
    {
      if (container is null || item?.info?.itemid !=
          Configuration.TaxProtection.CurrencyItemID)
        return;

      if (container.entityOwner is BuildingPrivlidge mountedPrivilege &&
          !IsTaxProtectionEnabledForPrivilege(mountedPrivilege))
        return;

      if (ShouldTrackTaxProtectionReserveInventory &&
          container.entityOwner is BuildingPrivlidge privilege &&
          privilege.buildingID is not 0U)
      {
        _taxProtectionSyncBuildingIds.Add(privilege.buildingID);
        QueueTaxProtectionSync(privilege.buildingID);
      }

      MarkTaxOverlayInventoryDirty(container);
    }

    private void MarkTaxOverlayInventoryDirty(ItemContainer container)
    {
      if (container.entityOwner is BuildingPrivlidge privilege)
      {
        if (!Configuration.TaxProtection.TaxCurrencyReservesEnabled)
          return;
        MarkTaxOverlayDirty(GetNetworkID(privilege));
        return;
      }

      var player = container.playerOwner;
      if (player && _taxOverlayViewersByPlayer.TryGetValue(
            player.userID.Get(), out var viewer))
        MarkTaxOverlayDirty(viewer.TcID);
    }

    private void MarkTaxOverlayDirty(ulong tcID)
    {
      if (tcID is 0UL || !_taxOverlayViewersByTc.ContainsKey(tcID))
        return;
      _dirtyTaxOverlayTcs.Add(tcID);
      if (_taxOverlayRefreshQueued)
        return;
      _taxOverlayRefreshQueued = true;
      NextFrame(_refreshDirtyTaxOverlaysAction);
    }

    private void QueueTaxOverlayRefresh()
    {
      foreach (var tcID in _taxOverlayViewersByTc.Keys)
        _dirtyTaxOverlayTcs.Add(tcID);
      if (_dirtyTaxOverlayTcs.Count is 0 || _taxOverlayRefreshQueued)
        return;
      _taxOverlayRefreshQueued = true;
      NextFrame(_refreshDirtyTaxOverlaysAction);
    }

    private void RefreshDirtyTaxOverlays()
    {
      _taxOverlayRefreshQueued = false;
      foreach (var tcID in _dirtyTaxOverlayTcs)
      {
        if (!_taxOverlayViewersByTc.TryGetValue(tcID, out var viewers))
          continue;

        var privilege = FindTaxCupboard(tcID);
        _taxOverlayViewerScratch.Clear();
        foreach (var playerID in viewers)
          _taxOverlayViewerScratch.Add(playerID);
        foreach (var playerID in _taxOverlayViewerScratch)
        {
          var player = _players.GetPlayer(playerID);
          if (!player || !privilege ||
              !IsTaxProtectionEnabledForPrivilege(privilege) ||
              player.inventory.loot.entitySource != privilege)
            CloseTaxOverlay(player, playerID);
          else
            RenderTaxOverlay(player, privilege, _taxOverlayViewersByPlayer[playerID], force: false);
        }
      }
      _dirtyTaxOverlayTcs.Clear();
    }

    private static BuildingPrivlidge FindTaxCupboard(ulong tcID) =>
      tcID is 0UL ? null : BaseNetworkable.serverEntities.Find(
        new NetworkableId(tcID)) as BuildingPrivlidge;

#endregion Refresh

#region Snapshot

    private TaxOverlaySnapshot CreateTaxOverlaySnapshot(
      BasePlayer player, BuildingPrivlidge privilege, ulong tcId)
    {
      var options = Configuration.TaxProtection;
      var costPerHour = GetTaxProtectionCostPerHour(privilege);
      var nowTicks = System.DateTime.UtcNow.Ticks;
      var remainingTicks = _taxProtection.TryGetValue(tcId, out var state) ?
        state.GetRemainingTicks(nowTicks) : 0L;
      var hasCurrencyReserves = options.TaxCurrencyReservesEnabled;
      var reserves = !hasCurrencyReserves || options.CurrencyItemID is 0 ||
        costPerHour <= 0 ? 0 :
        privilege.inventory?.GetAmount(options.CurrencyItemID, false, false) ?? 0;
      var reserveHours = costPerHour > 0 ? reserves / costPerHour : 0;
      var reserveTicks = reserveHours > System.TimeSpan.MaxValue.Ticks /
        System.TimeSpan.TicksPerHour ? System.TimeSpan.MaxValue.Ticks :
        reserveHours * System.TimeSpan.TicksPerHour;
      var totalTicks = remainingTicks > System.TimeSpan.MaxValue.Ticks - reserveTicks ?
        System.TimeSpan.MaxValue.Ticks : remainingTicks + reserveTicks;
      var buyMaxHours = GetMaxTaxProtectionPurchaseHours(
        player, remainingTicks, costPerHour);
      var actions = buyMaxHours > 0 ?
        (byte)(TAX_OVERLAY_BUY | TAX_OVERLAY_BUY_MAX) :
        (byte)0;
      return new(tcId, hasCurrencyReserves, reserves, options.MaxCurrencyReserves,
        costPerHour,
        GetTaxOverlayCeilingMinutes(remainingTicks),
        GetTaxOverlayCeilingMinutes(totalTicks),
        buyMaxHours,
        remainingTicks is 0L ?
          TaxOverlayState.Inactive :
        state?.ActiveSinceTicks > 0L ?
          TaxOverlayState.Active :
          TaxOverlayState.Paused,
        actions);
    }

    private static long GetTaxOverlayCeilingMinutes(long ticks) =>
      ticks <= 0L ? 0L : 1L + (ticks - 1L) /
        System.TimeSpan.TicksPerMinute;

    private int GetMaxTaxProtectionPurchaseHours(
      BasePlayer player, long bankedTicks, int costPerHour)
    {
      var options = Configuration.TaxProtection;
      if (!player || options.CurrencyItemID is 0 || costPerHour <= 0 ||
          _taxProtectionCurrencyDefinition is null)
        return 0;

      if (bankedTicks >= _maxPurchasedProtectionTicks)
        return 0;
      var availableHours = (int)((_maxPurchasedProtectionTicks - bankedTicks) /
        System.TimeSpan.TicksPerHour);
      if (availableHours <= 0)
        return 0;

      var affordableHours = player.inventory.GetAmount(
        options.CurrencyItemID, true, true) / costPerHour;
      return System.Math.Min(availableHours, affordableHours);
    }

#endregion Snapshot

#region Rendering

    private void RenderTaxOverlay(BasePlayer player,
      BuildingPrivlidge privilege, TaxOverlayViewer viewer, bool force)
    {
      var snapshot = CreateTaxOverlaySnapshot(player, privilege, viewer.TcID);
      if (!force && viewer.LastSnapshot.Matches(in snapshot))
        return;
      viewer.LastSnapshot = snapshot;
      DestroyTaxOverlay(player, viewer.RootName);
#if CARBON
      _taxOverlayBuilder.Clear();
      AppendTaxOverlayText(_taxOverlayBuilder, in snapshot);
      var lui = CreateCUI().v2;
      var root = lui.CreatePanel("Overlay", _taxOverlayPosition, _taxOverlayOffset,
        TAX_OVERLAY_BACKGROUND_COLOR, viewer.RootName)
        .AddCursor()
        .SetCanvasGroup(blocksRaycasts: true, interactable: true);
      var header = lui.CreatePanel(root, new LuiPosition(0f, 1f, 1f, 1f),
        new LuiOffset(0f, -24f, 0f, 0f), TAX_OVERLAY_HEADER_BG_COLOR);
      lui.CreateText(header, LuiPosition.Full, default,
        TAX_OVERLAY_HEADER_FONT_SIZE, TAX_OVERLAY_TEXT_COLOR, TAX_OVERLAY_TITLE,
        TextAnchor.MiddleCenter)
        .SetTextFont(CUI.Handler.FontTypes.RobotoCondensedBold);
      var closeButton = lui.CreateButton(header, new LuiPosition(1f, 0f, 1f, 1f),
        new LuiOffset(-28f, 0f, 0f, 0f), viewer.CloseCommand,
        TAX_OVERLAY_TRANSPARENT_COLOR, isProtected: true);
      lui.CreateText(closeButton, LuiPosition.Full, default, TAX_OVERLAY_HEADER_FONT_SIZE, TAX_OVERLAY_TEXT_COLOR,
        TAX_OVERLAY_CLOSE_TEXT, TextAnchor.MiddleCenter)
        .SetTextFont(CUI.Handler.FontTypes.RobotoCondensedBold);
      lui.CreateText(root, new LuiPosition(0.5f, 0f, 0.5f, 1f),
        new LuiOffset(-TAX_OVERLAY_TEXT_HALF_WIDTH, 35f,
          TAX_OVERLAY_TEXT_HALF_WIDTH, -28f),
        TAX_OVERLAY_TEXT_FONT_SIZE,
        TAX_OVERLAY_TEXT_COLOR, _taxOverlayBuilder.ToString(), TextAnchor.MiddleLeft)
        .SetTextFont(CUI.Handler.FontTypes.RobotoCondensedBold);
      RenderCarbonTaxOverlayButtons(lui, root, in snapshot, viewer);
      lui.SendUi(player);
#else
      CuiHelper.AddUi(player, BuildTaxOverlayPayload(in snapshot, viewer));
#endif
    }

    private void AppendTaxOverlayText(StringBuilder builder,
      in TaxOverlaySnapshot snapshot)
    {
      AppendTaxOverlayLabel(builder, TAX_OVERLAY_STATUS_LABEL);
      switch (snapshot.State)
      {
        case TaxOverlayState.Active:
          builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(COLOR_GREEN)
            .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX).Append("Protected for ");
          AppendFormattedDuration(
            builder,
            GetDurationSecondsFromMinutes(snapshot.RemainingMinutes),
            includeDays: true);
          builder.Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG);
          break;
        case TaxOverlayState.Paused:
          builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(COLOR_YELLOW)
            .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX).Append("Paused (");
          AppendFormattedDuration(
            builder,
            GetDurationSecondsFromMinutes(snapshot.RemainingMinutes),
            includeDays: true);
          builder.Append(')').Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG);
          break;
        default:
          builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(COLOR_RED)
            .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX)
            .Append("Inactive / Unprotected").Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG);
          break;
      }

      builder.Append('\n');
      AppendTaxOverlayLabel(builder, TAX_OVERLAY_COSTS_LABEL);
      builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(COLOR_ORANGE)
        .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX)
        .Append(snapshot.Cost)
        .Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG).Append(' ')
        .Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(TAX_OVERLAY_VALUE_COLOR)
        .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX).Append(_taxProtectionCurrencyName)
        .Append(" / hour").Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG).Append('\n');
      if (snapshot.HasCurrencyReserves)
      {
        builder.Append('\n');
        AppendTaxOverlayLabel(builder, TAX_OVERLAY_RESERVES_LABEL);
        builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(COLOR_ORANGE)
          .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX)
          .Append(snapshot.Reserves)
          .Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG);
        AppendTaxOverlayLabel(builder, TAX_OVERLAY_SEPARATOR_TEXT);
        builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(COLOR_ORANGE)
          .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX);
        if (snapshot.ReserveCap is 0)
          builder.Append(TAX_OVERLAY_UNLIMITED_TEXT);
        else
          builder.Append(snapshot.ReserveCap);
        builder.Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG).Append(' ')
          .Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(TAX_OVERLAY_VALUE_COLOR)
          .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX).Append(_taxProtectionCurrencyName)
          .Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG);
        if (snapshot.Reserves > 0)
        {
          builder.Append('\n');
          AppendTaxOverlayLabel(builder, TAX_OVERLAY_TOTAL_LABEL);
          builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(TAX_OVERLAY_VALUE_COLOR)
            .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX);
          var totalMinutes = System.Math.Min(snapshot.TotalMinutes,
            System.TimeSpan.MaxValue.Ticks / System.TimeSpan.TicksPerMinute);
          AppendFormattedDuration(
            builder, GetDurationSecondsFromMinutes(totalMinutes), includeDays: true);
          builder.Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG);
          AppendTaxOverlayLabel(builder, TAX_OVERLAY_SEPARATOR_TEXT);
          builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(TAX_OVERLAY_VALUE_COLOR)
            .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX)
            .Append(Configuration.TaxProtection.MaxPurchaseHours)
            .Append('h').Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG);
        }
      }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AppendTaxOverlayLabel(StringBuilder builder, string text)
    {
      builder.Append(TAX_OVERLAY_RICH_TEXT_COLOR_PREFIX).Append(TAX_OVERLAY_LABEL_COLOR)
        .Append(TAX_OVERLAY_RICH_TEXT_COLOR_SUFFIX).Append(text)
        .Append(TAX_OVERLAY_RICH_TEXT_CLOSE_TAG);
    }

#if !CARBON
    private string BuildTaxOverlayPayload(in TaxOverlaySnapshot snapshot,
      TaxOverlayViewer viewer)
    {
      var rootName = viewer.RootName;
      _taxOverlayBuilder.Clear();
      _taxOverlayBuilder.Append(_taxOverlayPayloadPrefix)
        .Append(rootName)
        .Append(_taxOverlayPayloadRootSuffix);
      AppendOxideTaxOverlayElementStart(_taxOverlayBuilder, rootName,
        TAX_OVERLAY_HEADER_NAME_SUFFIX, string.Empty);
      _taxOverlayBuilder.Append(TAX_OVERLAY_PAYLOAD_HEADER_COMPONENTS);
      AppendOxideTaxOverlayElementStart(_taxOverlayBuilder, rootName,
        TAX_OVERLAY_HEADER_TEXT_NAME_SUFFIX, TAX_OVERLAY_HEADER_NAME_SUFFIX);
      _taxOverlayBuilder.Append(TAX_OVERLAY_PAYLOAD_HEADER_TEXT_COMPONENTS);
      AppendOxideTaxOverlayCloseButton(_taxOverlayBuilder, rootName, viewer);
      AppendOxideTaxOverlayElementStart(_taxOverlayBuilder, rootName,
        TAX_OVERLAY_BODY_NAME_SUFFIX, string.Empty);
      _taxOverlayBuilder.Append(TAX_OVERLAY_PAYLOAD_BODY_TEXT_PREFIX);
      AppendTaxOverlayText(_taxOverlayBuilder, in snapshot);
      _taxOverlayBuilder.Append(TAX_OVERLAY_PAYLOAD_BODY_TEXT_SUFFIX);
      AppendOxideTaxOverlayButtons(_taxOverlayBuilder, rootName, in snapshot, viewer);
      _taxOverlayBuilder.Append(']');
      return _taxOverlayBuilder.ToString();
    }
#endif

    private void DestroyTaxOverlay(BasePlayer player, string rootName = null)
    {
      if (!player) return;
      rootName ??= TAX_OVERLAY_ROOT + player.userID.Get();
#if CARBON
      CuiHandler.Destroy(rootName, player);
#else
      CuiHelper.DestroyUi(player, rootName);
#endif
    }

#if CARBON

#region Carbon Rendering

    private void RenderCarbonTaxOverlayButtons(LUI lui, LUI.LuiContainer root,
      in TaxOverlaySnapshot snapshot, TaxOverlayViewer viewer)
    {
      AddCarbonTaxOverlayButton(lui, root, (snapshot.Actions & TAX_OVERLAY_BUY) != 0,
        TAX_OVERLAY_BUTTON_BUY_TEXT, viewer.BuyCommand,
        TAX_OVERLAY_BUY_BUTTON_MIN_X, TAX_OVERLAY_BUY_BUTTON_MIN_Y,
        TAX_OVERLAY_BUY_BUTTON_MAX_X, TAX_OVERLAY_BUY_BUTTON_MAX_Y);
      _taxOverlayBuilder.Clear();
      _taxOverlayBuilder.Append(TAX_OVERLAY_BUTTON_BUY_MAX_TEXT)
        .Append(snapshot.BuyMaxHours).Append(')');
      AddCarbonTaxOverlayButton(lui, root, (snapshot.Actions & TAX_OVERLAY_BUY_MAX) != 0,
        _taxOverlayBuilder.ToString(),
        viewer.BuyMaxCommand,
        TAX_OVERLAY_BUY_MAX_BUTTON_MIN_X, TAX_OVERLAY_BUY_MAX_BUTTON_MIN_Y,
        TAX_OVERLAY_BUY_MAX_BUTTON_MAX_X, TAX_OVERLAY_BUY_MAX_BUTTON_MAX_Y);
    }

    private static void AddCarbonTaxOverlayButton(LUI lui, LUI.LuiContainer root,
      bool enabled, string text, string command,
      float minX, float minY, float maxX, float maxY)
    {
      var button = lui.CreateButton(root, default, new LuiOffset(minX, minY, maxX, maxY),
        enabled ? command : string.Empty,
        enabled ? TAX_OVERLAY_ENABLED_BUTTON_COLOR : TAX_OVERLAY_DISABLED_BUTTON_COLOR,
        isProtected: true);
      lui.CreateText(button, LuiPosition.Full, default, TAX_OVERLAY_BUTTON_FONT_SIZE,
        enabled ? TAX_OVERLAY_TEXT_COLOR : TAX_OVERLAY_DISABLED_TEXT_COLOR, text,
        TextAnchor.MiddleCenter)
        .SetTextFont(CUI.Handler.FontTypes.RobotoCondensedBold);
    }

#endregion Carbon Rendering

#else

#region Oxide Rendering

    private static void AppendOxideTaxOverlayElementStart(StringBuilder builder,
      string rootName, string nameSuffix, string parentSuffix)
    {
      builder.Append(TAX_OVERLAY_PAYLOAD_ELEMENT_PREFIX)
        .Append(rootName).Append(nameSuffix)
        .Append(TAX_OVERLAY_PAYLOAD_PARENT_PREFIX)
        .Append(rootName).Append(parentSuffix)
        .Append(TAX_OVERLAY_PAYLOAD_COMPONENTS_PREFIX);
    }

    private static void AppendOxideTaxOverlayButtons(StringBuilder builder,
      string rootName, in TaxOverlaySnapshot snapshot, TaxOverlayViewer viewer)
    {
      AppendOxideTaxOverlayButton(builder, rootName,
        TAX_OVERLAY_BUY_NAME_SUFFIX, TAX_OVERLAY_BUY_TEXT_NAME_SUFFIX,
        (snapshot.Actions & TAX_OVERLAY_BUY) != 0, viewer.BuyCommand,
        TAX_OVERLAY_BUTTON_BUY_TEXT, TAX_OVERLAY_PAYLOAD_BUY_BUTTON_SUFFIX);
      AppendOxideTaxOverlayBuyMaxButton(builder, rootName, in snapshot, viewer);
    }

    private static void AppendOxideTaxOverlayCloseButton(StringBuilder builder,
      string rootName, TaxOverlayViewer viewer)
    {
      AppendOxideTaxOverlayElementStart(builder, rootName,
        TAX_OVERLAY_CLOSE_NAME_SUFFIX, TAX_OVERLAY_HEADER_NAME_SUFFIX);
      builder.Append(TAX_OVERLAY_PAYLOAD_BUTTON_COLOR_PREFIX)
        .Append(TAX_OVERLAY_TRANSPARENT_COLOR)
        .Append(TAX_OVERLAY_PAYLOAD_BUTTON_COMMAND_PREFIX)
        .Append(viewer.CloseCommand)
        .Append(TAX_OVERLAY_PAYLOAD_CLOSE_BUTTON_SUFFIX);
      AppendOxideTaxOverlayElementStart(builder, rootName,
        TAX_OVERLAY_CLOSE_TEXT_NAME_SUFFIX, TAX_OVERLAY_CLOSE_NAME_SUFFIX);
      builder.Append(TAX_OVERLAY_PAYLOAD_LABEL_TEXT_PREFIX)
        .Append(TAX_OVERLAY_CLOSE_TEXT)
        .Append(TAX_OVERLAY_PAYLOAD_CLOSE_LABEL_SUFFIX);
    }

    private static void AppendOxideTaxOverlayButton(StringBuilder builder,
      string rootName, string nameSuffix, string textNameSuffix,
      bool enabled, string command, string text, string buttonSuffix)
    {
      AppendOxideTaxOverlayElementStart(builder, rootName, nameSuffix, string.Empty);
      builder.Append(TAX_OVERLAY_PAYLOAD_BUTTON_COLOR_PREFIX)
        .Append(enabled ? TAX_OVERLAY_ENABLED_BUTTON_COLOR : TAX_OVERLAY_DISABLED_BUTTON_COLOR)
        .Append(TAX_OVERLAY_PAYLOAD_BUTTON_COMMAND_PREFIX);
      if (enabled)
        builder.Append(command);
      builder.Append(buttonSuffix);
      AppendOxideTaxOverlayElementStart(builder, rootName, textNameSuffix, nameSuffix);
      builder.Append(TAX_OVERLAY_PAYLOAD_LABEL_TEXT_PREFIX)
        .Append(text)
        .Append(TAX_OVERLAY_PAYLOAD_BUTTON_LABEL_COLOR_PREFIX)
        .Append(enabled ? TAX_OVERLAY_TEXT_COLOR : TAX_OVERLAY_DISABLED_TEXT_COLOR)
        .Append(TAX_OVERLAY_PAYLOAD_BUTTON_LABEL_SUFFIX);
    }

    private static void AppendOxideTaxOverlayBuyMaxButton(StringBuilder builder,
      string rootName, in TaxOverlaySnapshot snapshot, TaxOverlayViewer viewer)
    {
      var enabled = (snapshot.Actions & TAX_OVERLAY_BUY_MAX) != 0;
      AppendOxideTaxOverlayElementStart(builder, rootName,
        TAX_OVERLAY_BUY_MAX_NAME_SUFFIX, string.Empty);
      builder.Append(TAX_OVERLAY_PAYLOAD_BUTTON_COLOR_PREFIX)
        .Append(enabled ? TAX_OVERLAY_ENABLED_BUTTON_COLOR : TAX_OVERLAY_DISABLED_BUTTON_COLOR)
        .Append(TAX_OVERLAY_PAYLOAD_BUTTON_COMMAND_PREFIX);
      if (enabled)
        builder.Append(viewer.BuyMaxCommand);
      builder.Append(TAX_OVERLAY_PAYLOAD_BUY_MAX_BUTTON_SUFFIX);
      AppendOxideTaxOverlayElementStart(builder, rootName,
        TAX_OVERLAY_BUY_MAX_TEXT_NAME_SUFFIX, TAX_OVERLAY_BUY_MAX_NAME_SUFFIX);
      builder.Append(TAX_OVERLAY_PAYLOAD_LABEL_TEXT_PREFIX)
        .Append(TAX_OVERLAY_BUTTON_BUY_MAX_TEXT)
        .Append(snapshot.BuyMaxHours)
        .Append(')')
        .Append(TAX_OVERLAY_PAYLOAD_BUTTON_LABEL_COLOR_PREFIX)
        .Append(enabled ? TAX_OVERLAY_TEXT_COLOR : TAX_OVERLAY_DISABLED_TEXT_COLOR)
        .Append(TAX_OVERLAY_PAYLOAD_BUTTON_LABEL_SUFFIX);
    }

#endregion Oxide Rendering

#endif

#endregion Rendering

#region Commands

#if CARBON
    [ProtectedCommand(TAX_OVERLAY_COMMAND)]
#endif
    private void ccTaxOverlay(ConsoleSystem.Arg arg)
    {
      if (arg?.Connection?.player is not BasePlayer player ||
          !_isTaxOverlayEnabled || arg.Args?.Length is not 2)
        return;

      var action = arg.GetString(0);
      if (action is not (TAX_OVERLAY_ACTION_CLOSE or
                         TAX_OVERLAY_ACTION_BUY or
                         TAX_OVERLAY_ACTION_BUY_MAX))
        return;

      var playerID = player.userID.Get();
      var tcID = arg.GetULong(1);
      if (tcID is 0 ||
          !_taxOverlayViewersByPlayer.TryGetValue(playerID, out var viewer) ||
          viewer.TcID != tcID)
        return;
      if (action is TAX_OVERLAY_ACTION_CLOSE)
      {
        CloseTaxOverlay(player, playerID);
        return;
      }
#if !CARBON
      if (!CheckConCmdPerm(arg, Configuration.Permission.TaxProtection))
        return;
#endif
      if (!HasTaxProtectionPermission(player))
        return;

      var privilege = FindTaxCupboard(tcID);
      if (!privilege || player.inventory.loot.entitySource != privilege ||
          !IsTaxProtectionEnabledForPrivilege(privilege) ||
          !IsTrustedForCupboard(player, privilege))
        return;

      var snapshot = CreateTaxOverlaySnapshot(player, privilege, tcID);
      var success = action switch
      {
        TAX_OVERLAY_ACTION_BUY when (snapshot.Actions & TAX_OVERLAY_BUY) != 0 =>
          TryBuyOneTaxProtectionHour(player, privilege, tcID),
        TAX_OVERLAY_ACTION_BUY_MAX when (snapshot.Actions & TAX_OVERLAY_BUY_MAX) != 0 =>
          TryBuyMaxTaxProtectionHours(player, privilege, tcID),
        _ => false
      };
      if (!success)
        return;

      SyncPurchasedProtection(privilege, tcID, System.DateTime.UtcNow);
      MarkTaxOverlayDirty(tcID);
      QueueCupboardStatusHudRefresh(privilege);
      UpdateTcMarkerLabel(privilege);
    }

    private bool TryBuyOneTaxProtectionHour(BasePlayer player,
      BuildingPrivlidge privilege, ulong tcId)
      => TryBuyTaxProtectionHours(player, privilege, tcId, 1, out _);

    private bool TryBuyMaxTaxProtectionHours(BasePlayer player,
      BuildingPrivlidge privilege, ulong tcId)
    {
      var nowTicks = System.DateTime.UtcNow.Ticks;
      var bankedTicks = _taxProtection.TryGetValue(tcId, out var state) ?
        state.GetRemainingTicks(nowTicks) : 0L;
      var costPerHour = GetTaxProtectionCostPerHour(privilege);
      var targetHours = GetMaxTaxProtectionPurchaseHours(
        player, bankedTicks, costPerHour);
      return targetHours > 0 && TryBuyTaxProtectionHours(
        player, privilege, tcId, targetHours, out _);
    }

    private bool TryBuyTaxProtectionHours(BasePlayer player,
      BuildingPrivlidge privilege, ulong tcId,
      int requestedHours, out int purchasedHours)
    {
      purchasedHours = 0;
      var options = Configuration.TaxProtection;
      var costPerHour = GetTaxProtectionCostPerHour(privilege);
      if (requestedHours <= 0 ||
          !IsTaxProtectionEnabledForPrivilege(privilege) ||
          options.CurrencyItemID is 0 ||
          costPerHour <= 0 || _taxProtectionCurrencyDefinition is null)
        return false;

      var nowTicks = System.DateTime.UtcNow.Ticks;
      var remainingTicks = _taxProtection.TryGetValue(tcId, out var state) ?
        state.GetRemainingTicks(nowTicks) : 0L;
      if (remainingTicks >= _maxPurchasedProtectionTicks)
        return false;

      var availableHours = (int)((_maxPurchasedProtectionTicks - remainingTicks) /
        System.TimeSpan.TicksPerHour);
      if (availableHours <= 0)
        return false;
      purchasedHours = System.Math.Min(requestedHours, availableHours);
      var purchasedTicks = purchasedHours * System.TimeSpan.TicksPerHour;
      var cost = (long)purchasedHours * costPerHour;
      if (cost > int.MaxValue || player.inventory.GetAmount(
            options.CurrencyItemID, true, true) < cost)
        return false;

      player.inventory.Take(null, options.CurrencyItemID, (int)cost);
      state ??= new TaxProtectionState();
      state.BankedTicks = remainingTicks + purchasedTicks;
      if (state.ActiveSinceTicks > 0L)
        state.ActiveSinceTicks = nowTicks;
      _taxProtection[tcId] = state;
      MarkDataDirty();
      return true;
    }

#endregion Commands

#endregion Tax Overlay

#endregion Tax Protection

#region Clans/Teams Integration

#region Clans/Teams Methods

    private string GetClanTag(ulong userID)
    {
      if (_clanTagCache.TryGetValue(userID, out var tag))
        return tag;

      if (Clans is null)
        return null;

      tag = Clans?.Call<string>("GetClanOf", userID);
      _clanTagCache[userID] = tag;
      return tag;
    }

    private string GetCachedClanTag(ulong userID) =>
      _clanTagCache.GetValueOrDefault(userID);

    private List<ulong> GetClanMembers(string tag) =>
      string.IsNullOrEmpty(tag) ? null :
      _clanMemberCache.TryGetValue(tag, out var members) ? members :
      CacheClan(tag);

    private List<ulong> GetCachedClanMembers(string tag) =>
      string.IsNullOrEmpty(tag) ? null :
      _clanMemberCache.GetValueOrDefault(tag);

    private static RelationshipManager.PlayerTeam GetTeam(ulong userID) =>
      RelationshipManager.ServerInstance.FindPlayersTeam(userID);

    private static RelationshipManager.PlayerTeam GetTeam(BasePlayer player) =>
      player && player.currentTeam is not 0 ?
      RelationshipManager.ServerInstance.FindTeam(player.currentTeam) :
      null;

    private List<ulong> GetTeamMembers(ulong userID)
    {
      var player = _players.GetPlayer(userID);
      var team = GetTeam(player) ?? GetTeam(userID);

      var hasVanillaClan = player is { serverClan: not null, clanId: not 0 };
      if (!hasVanillaClan)
        return team?.members.Count > 0 ? team.members : null;

      _teamMembersScratch.Clear();

      if (team?.members.Count > 0)
        _teamMembersScratch.AddRange(team.members);

      foreach (var clanMember in player.serverClan.Members)
      {
        var memberID = clanMember.SteamId;
        if (memberID != userID)
          _teamMembersScratch.Add(memberID);
      }

      return _teamMembersScratch.Count > 0 ?
        _teamMembersScratch.GetList() : null;
    }

    private ulong GetOfflineMember(
      List<ulong> members, System.DateTime nowUtc)
    {
      if (members is null || members.Count is 0)
        return 0UL;

      var result = 0UL;
      if (Configuration.Team.TeamFirstOffline)
      {
        var maxMinutes = float.MinValue;
        for (var i = 0; i < members.Count; i++)
        {
          var memberID = members[i];
          if (!_lastOnline.TryGetValue(memberID, out var lastOnlineMember))
            continue;

          var memberMinutes = GetOfflineMinutes(lastOnlineMember, nowUtc);
          if (memberMinutes <= maxMinutes)
            continue;

          maxMinutes = memberMinutes;
          result = memberID;
        }
      }
      else
      {
        var minMinutes = float.MaxValue;
        for (var i = 0; i < members.Count; i++)
        {
          var memberID = members[i];
          if (!_lastOnline.TryGetValue(memberID, out var lastOnlineMember))
            continue;

          var memberMinutes = GetOfflineMinutes(lastOnlineMember, nowUtc);
          if (memberMinutes >= minMinutes)
            continue;

          minMinutes = memberMinutes;
          result = memberID;
        }
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
      if (!ulong.TryParse(userID, out var memberID))
        return;

      if (_clanMemberCache.TryGetValue(tag, out var clan))
        clan.Add(memberID);
      else
        CacheClan(tag);

      _clanTagCache[memberID] = tag;
    }

    private void OnClanMemberGone(string userID, string tag)
    {
      if (!ulong.TryParse(userID, out var memberID))
        return;

      if (_clanMemberCache.TryGetValue(tag, out var clan))
        clan.Remove(memberID);
      else
        CacheClan(tag);

      _clanTagCache.Remove(memberID);
    }

    private void OnClanDisbanded(string tag, List<string> _memberUserIDs) =>
      OnClanDestroy(tag);

    private void OnClanDestroy(string tag)
    {
      if (!_clanMemberCache.Remove(tag, out var list))
        return;

      for (var i = 0; i < list.Count; i++)
        _clanTagCache.Remove(list[i]);

      Facepunch.Pool.FreeUnmanaged(ref list);
    }

#endregion Clans Hooks

#region Team Hooks

    private object OnTeamDisband(RelationshipManager.PlayerTeam team) =>
      HandleTeamChange(team);

    private object OnTeamKick(
      RelationshipManager.PlayerTeam team, BasePlayer player, ulong _target) =>
      OnTeamLeave(team, player);

    private object OnTeamLeave(
      RelationshipManager.PlayerTeam team, BasePlayer _player) =>
      HandleTeamChange(team);

    private object HandleTeamChange(RelationshipManager.PlayerTeam team)
    {
      if (team is null || team.members.Count is 0)
        return null;

      if (Configuration.Team.TeamAvoidAbuse && AnyPlayersOffline(team.members))
        return BoxedTrue;

      if (!Configuration.Team.TeamEnablePenalty)
        return null;

      ApplyTeamPenalty(team.members);

      return null;
    }

    private void ApplyTeamPenalty(List<ulong> memberIDs)
    {
      var changedPlayers = 0;
      var durationHours = Configuration.Team.TeamPenaltyDuration;
      foreach (var memberID in memberIDs)
      {
        if (TryEnablePenalty(memberID, durationHours))
          changedPlayers++;
      }

      if (changedPlayers is not 0)
        MarkDataDirty();
    }

#endregion Team Hooks

#endregion Clans/Teams Integration

#region Commands

#region ChatCommands

    private readonly RaycastHit[] RaycastHits =
      new RaycastHit[1];

    private Ray _ray;

    private const int LayerMask =
      Rust.Layers.Mask.Construction  | // building blocks
      Rust.Layers.Mask.Deployed      | // deployable items
      Rust.Layers.Mask.Vehicle_World | // modular cars
      Rust.Layers.Mask.Vehicle_Large | // buildable boats
      Rust.Layers.Mask.World;          // rentable shops

#if !CARBON
    private static bool CheckChatCmdPerm(BasePlayer player, string perm)
    {
      if (player.HasPermission(perm))
        return true;

      ChatMessage(player, LANG_MESSAGE_NOPERMISSION);
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
        ChatMessage(player, GetStatusText(args));
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
      var hitCount = Physics.RaycastNonAlloc(
        _ray, RaycastHits, 50f, LayerMask);

      if (hitCount <= 0)
      {
        ChatMessage(player, "You are looking at nothing or you are too far away");
        return;
      }

      var baseEntity = RaycastHits[0].GetEntity();
      if (!baseEntity)
      {
        ChatMessage(player, "You are not looking at an entity");
        return;
      }

      // --- Custom Entity Checks (Apartments & Shops) ---
      switch (baseEntity)
      {
        case ApartmentDoor apartmentDoor:
          {
            var (protection, ownerID, _) = GetApartmentProtection(apartmentDoor);
            if (ownerID is 0UL)
            {
              ChatMessage(player, $"{apartmentDoor.GetType()}/{apartmentDoor} has no owner");
              return;
            }
            ChatMessage(player, $"{apartmentDoor.GetType()}/{apartmentDoor} report:\nApartment protection status: {protection}");
            ChatMessage(player, GetStatusText(ownerID, ownerOnly: true));
            return;
          }
        case RentableShop rentableShop:
          {
            var (protection, ownerID, _) = GetShopProtection(rentableShop);
            if (ownerID is 0UL)
            {
              ChatMessage(player, $"{rentableShop.GetType()}/{rentableShop} has no owner");
              return;
            }
            ChatMessage(player, $"{rentableShop.GetType()}/{rentableShop} report:\nShop protection status: {protection}");
            ChatMessage(player, GetStatusText(ownerID, ownerOnly: true));
            return;
          }
      }

      // --- Standard Combat Entity Check ---
      if (baseEntity is not BaseCombatEntity entity)
      {
        ChatMessage(player, $"{baseEntity.GetType()}/{baseEntity} is not a combat entity");
        return;
      }

      if (!IsProtected(entity))
      {
        ChatMessage(player, $"{entity.GetType()}/{entity} is not a protected player entity");
        return;
      }

      var decision =
        EvaluateProtection(entity, null, System.DateTime.UtcNow);
      if (decision.TargetID is 0UL || !decision.TargetID.IsSteamId())
      {
        ChatMessage(player, $"{entity.GetType()}/{entity} has no owner");
        return;
      }

      ChatMessage(player, $"{entity.GetType()}/{entity} report:");
      ChatMessage(player,
        GetStatusText(decision.TargetID, decision.IsDecaying,
          isGrief: decision.IsGrief, prefabID: entity.prefabID));

      if (TryGetTcState(entity, out var tcState))
      {
        SendPurchasedProtectionStatus(player, tcState.Privilege);
        ShowStatusCommandHud(player, in tcState);
      }
    }

    private void cmdHelp(BasePlayer player, string _command, string[] _args)
    {
      if (!player)
        return;
#if !CARBON
      if (!CheckChatCmdPerm(player, Configuration.Permission.Protect))
        return;
#endif

      ChatMessage(player, GetHelpText(player.userID.Get()));
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
      var msg =
        $"Updated the {nameof(StoredData)}.json file for {FillOnlineTimes()} players.";
      ChatMessage(player, msg);
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
        ChatMessage(player, MESSAGE_INVALID_SYNTAX);
        return;
      }

      var userID = player.userID.Get();
      if (args.Length is 2)
      {
        userID = _players.GetPlayer(args[0])?.userID.Get() ?? 0UL;
        if (userID is 0UL && !ulong.TryParse(args[0], out userID))
        {
          ChatMessage(player, MESSAGE_PLAYER_NOT_FOUND);
          return;
        }
      }

      if (!double.TryParse(args[^1], out var hours) ||
          double.IsNaN(hours) || double.IsInfinity(hours))
      {
        ChatMessage(player, MESSAGE_INVALID_SYNTAX);
        return;
      }

      if (_lastOnline.TryGetValue(userID, out var target))
      {
        target.LastOnlineDT = target.LastOnlineDT.AddHours(-hours);
        MarkDataDirty();
        ChatMessage(player, $"{target.UserName} | {System.TimeZoneInfo.ConvertTimeFromUtc(target.LastOnlineDT, _timeZone)}");
      }
      else
      {
        ChatMessage(player, MESSAGE_PLAYER_NOT_FOUND);
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
          ChatMessage(player, MESSAGE_INVALID_SYNTAX);
        return;
      }

      var userID = player.userID.Get();
      if (args.Length is 1)
      {
        userID = _players.GetPlayer(args[0])?.userID.Get() ?? 0UL;
        if (userID is 0UL && !ulong.TryParse(args[0], out userID))
        {
          ChatMessage(player, MESSAGE_PLAYER_NOT_FOUND);
          return;
        }
      }

      if (!_lastOnline.TryGetValue(userID, out var target))
      {
        ChatMessage(player, MESSAGE_PLAYER_NOT_FOUND);
        return;
      }
      target.LastOnlineDT = System.DateTime.UtcNow;
      MarkDataDirty();
      ChatMessage(player,
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
          ChatMessage(player, MESSAGE_INVALID_SYNTAX);

        return;
      }

      var userID = player.userID.Get();
      if (args.Length is 2)
      {
        userID = _players.GetPlayer(args[0])?.userID.Get() ?? 0UL;
        if (userID is 0UL && !ulong.TryParse(args[0], out userID))
        {
          ChatMessage(player, MESSAGE_PLAYER_NOT_FOUND);
          return;
        }
      }

      if (!float.TryParse(args[^1], out var duration))
      {
        ChatMessage(player, MESSAGE_INVALID_SYNTAX);
        return;
      }

      if (!_lastOnline.TryGetValue(userID, out var target))
      {
        ChatMessage(player, MESSAGE_PLAYER_NOT_FOUND);
        return;
      }

      if (duration > 0f)
      {
        if (!TryEnablePenalty(userID, target, duration))
        {
          ChatMessage(player, MESSAGE_INVALID_SYNTAX);
          return;
        }

        ChatMessage(player,
          $"{target.UserName} | Penalty until {System.TimeZoneInfo.ConvertTimeFromUtc(target.PenaltyEndDT, _timeZone)}");
      }
      else
      {
        TryDisablePenalty(userID, target);
        ChatMessage(player, $"{target.UserName} | Penalty disabled");
      }

      MarkDataDirty();
    }

    private void cmdTestGrief(BasePlayer player, string _command, string[] args)
    {
      if (!player)
        return;
#if !CARBON
      if (!CheckChatCmdPerm(player, Configuration.Permission.Admin))
        return;
#endif
      if (Configuration.RaidProtection.ProtectGriefTcs)
      {
        ChatMessage(player, "Protect grief TCs is enabled in the configuration!");
        return;
      }

      if (!TryGetGriefState(args?.Length is 0 ? "none" : args?[0], out var griefState))
      {
        ChatMessage(player, $"'{args?[0]}' is not a valid grief state. Use none, true, or false");
        return;
      }

      if (args?.Length is 0 or 1)
      {
        _ray.origin = player.eyes.position;
        _ray.direction = player.eyes.HeadForward();
        var hitCount = Physics.RaycastNonAlloc(
          _ray, RaycastHits, 50f, LayerMask);

        if (hitCount <= 0)
        {
          ChatMessage(player, "You are looking at nothing or you are too far away");
          return;
        }

        var baseEntity = RaycastHits[0].GetEntity();
        if (baseEntity is not DecayEntity decayEntity ||
            decayEntity.buildingID is 0U)
        {
          ChatMessage(player, "You are not looking at a building entity");
          return;
        }

        if (!_tcCache.TryGetValue(decayEntity.buildingID, out var tcState) ||
            !tcState.Privilege)
        {
          ChatMessage(player, "The looked-at building has no physical Tool Cupboard");
          return;
        }

        if (args.Length is 0)
        {
          GetGriefCupboardState(tcState.Privilege);
          return;
        }

        GetGriefCupboardState(tcState.Privilege, griefState);
        return;
      }

      for (var i = 1; i < args?.Length; i++)
      {
        var argument = args[i];
        if (!ulong.TryParse(argument, out var cupboardNetworkID) ||
            cupboardNetworkID is 0UL)
        {
          ChatMessage(player, $"'{argument}' is not a valid Tool Cupboard network ID");
          continue;
        }

        if (BaseNetworkable.serverEntities.Find(
              new NetworkableId(cupboardNetworkID)) is not BuildingPrivlidge toolCupboard)
        {
          ChatMessage(player, $"{cupboardNetworkID} is not a Tool Cupboard");
          continue;
        }

        GetGriefCupboardState(toolCupboard, griefState);
      }

      return;

      void GetGriefCupboardState(
        BuildingPrivlidge toolCupboard, TcGriefState? requestedState = null)
      {
        var cupboardNetworkID = GetNetworkID(toolCupboard);
        if (cupboardNetworkID is 0UL)
        {
          ChatMessage(player, "Tool Cupboard has no valid network ID");
          return;
        }

        var dataCreated = false;
        if (!_tcCreationData.TryGetValue(cupboardNetworkID, out var tcData))
        {
          tcData = new();
          _tcCreationData[cupboardNetworkID] = tcData;
          dataCreated = true;
        }

        var stateChanged = requestedState.HasValue &&
          tcData.GriefState != requestedState.Value;
        if (requestedState.HasValue)
          tcData.GriefState = requestedState.Value;

        if (dataCreated || stateChanged)
          MarkDataDirty();
        if (requestedState.HasValue)
          RefreshGriefCupboardState(cupboardNetworkID);

        _sb.Clear();
        _sb.AppendLine($"<color={COLOR_BLUE}>Grief Status</color> Tool Cupboard[{cupboardNetworkID}]");

        if (requestedState.HasValue)
          _sb.AppendLine($"<color={COLOR_YELLOW}>Forced Grief State</color> {GetGriefStateName(requestedState.Value)}");
        else if (tcData.GriefState is TcGriefState.None)
          _sb.AppendLine($"<color={COLOR_YELLOW}>Grief State</color> {GetGriefStateName(_griefCupboardIds.Contains(cupboardNetworkID) ? TcGriefState.ForceTrue : TcGriefState.ForceFalse)}");
        else
          _sb.AppendLine($"<color={COLOR_YELLOW}>Forced Grief State</color> {GetGriefStateName(tcData.GriefState)}");

        ChatMessage(player, _sb.ToString());
      }

      static bool TryGetGriefState(string value, out TcGriefState griefState)
      {
        switch (value)
        {
          case var _ when string.Equals(
            value, "none", System.StringComparison.OrdinalIgnoreCase):
            griefState = TcGriefState.None;
            return true;
          case var _ when string.Equals(
            value, "true", System.StringComparison.OrdinalIgnoreCase):
            griefState = TcGriefState.ForceTrue;
            return true;
          case var _ when string.Equals(
            value, "false", System.StringComparison.OrdinalIgnoreCase):
            griefState = TcGriefState.ForceFalse;
            return true;
          default:
            griefState = TcGriefState.None;
            return false;
        }
      }

      static string GetGriefStateName(TcGriefState griefState) =>
        griefState switch
        {
          TcGriefState.ForceTrue => "TRUE",
          TcGriefState.ForceFalse => "FALSE",
          _ => "NONE"
        };
    }

#endregion ChatCommands

#region ConsoleCommands

#if !CARBON
    private bool CheckConCmdPerm(ConsoleSystem.Arg arg, string perm)
    {
      if (arg.IsAdmin || arg.Connection?.userid.HasPermission(perm) is true)
        return true;

      Reply(arg, LANG_MESSAGE_NOPERMISSION);
      return false;
    }
#endif

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
      var msg =
        $"Updated the {nameof(StoredData)}.json file for {FillOnlineTimes()} players.";
      Reply(arg, msg);
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
      if (CacheAllPlayerPermissions())
        RefreshAllProtectionViews();
      Reply(arg, "Updated the permission status for all players.");
    }

    private void ccUpdatePrefabList(ConsoleSystem.Arg arg)
    {
      if (arg is null)
      {
        PrintError("ccUpdatePrefabList(): arg is null");
        return;
      }
#if !CARBON
      if (!CheckConCmdPerm(arg, Configuration.Permission.Admin))
        return;
#endif
      var count = Configuration.RaidProtection.Prefabs.Count;

      if (arg.Args?.Length is 1 && arg.GetBool(0))
        Configuration.RaidProtection.Prefabs = GetPrefabNames();
      else
        Configuration.RaidProtection.Prefabs.UnionWith(GetPrefabNames());

      count = Configuration.RaidProtection.Prefabs.Count - count;
      CachePrefabs();
      SaveConfig();

      Reply(arg, $"Updated the Prefabs to protect list in the configuration. {(count >= 0 ? $"Added {count}" : $"Removed {-count}")} Prefab(s)");
    }

    private void ccDumpPrefabList(ConsoleSystem.Arg arg)
    {
      if (arg is null)
      {
        PrintError("ccDumpPrefabList(): arg is null");
        return;
      }
#if !CARBON
      if (!CheckConCmdPerm(arg, Configuration.Permission.Admin))
        return;
#endif
      Configuration.RaidProtection.Prefabs.Clear();
      CachePrefabs();
      SaveConfig();

      Reply(arg, "Cleared the Prefabs to protect list in the configuration.");
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

    private string GetStatusText(string[] args)
    {
      if (args?.Length is not 1)
        return MESSAGE_INVALID_SYNTAX;

      var userID = _players.GetPlayer(args[0])?.userID.Get() ?? 0UL;
      if (userID is 0UL && !ulong.TryParse(args[0], out userID) ||
          !_lastOnline.ContainsKey(userID))
        return MESSAGE_PLAYER_NOT_FOUND;

      return GetStatusText(userID);
    }

    private string GetStatusText(
      ulong userID, bool isDecaying = false, bool ownerOnly = false,
      bool isGrief = false, uint prefabID = 0U)
    {
      if (!_lastOnline.TryGetValue(userID, out var lastOnline))
        return MESSAGE_PLAYER_NOT_FOUND;

      var nowUtc = System.DateTime.UtcNow;
      var isOnline = IsOnline(userID);
      var onlineColor = isOnline ? COLOR_GREEN : COLOR_RED;

      _sb.Clear();
      _sb.AppendLine($"<color={COLOR_BLUE}>Status</color> {lastOnline.UserName}");
      _sb.AppendLine($"<color={COLOR_YELLOW}>Player Status</color> <color={onlineColor}>{(isOnline ? "Online</color>" : $"Offline</color> {System.TimeZoneInfo.ConvertTimeFromUtc(lastOnline.LastOnlineDT, _timeZone)}")}");

      if (!ownerOnly)
        AppendTeamOrClanMembersStatus(userID);

      var penaltyEnabled =
        lastOnline.PenaltyEnd >= nowUtc.Ticks;
      if (penaltyEnabled || Configuration.Team.TeamEnablePenalty)
        _sb.AppendLine($"<color={COLOR_YELLOW}>Penalty Status</color> {(penaltyEnabled ? $"<color={COLOR_RED}>Enabled</color> {System.TimeZoneInfo.ConvertTimeFromUtc(lastOnline.PenaltyEndDT, _timeZone)}" : $"<color={COLOR_GREEN}>Disabled</color>")}");

      if (penaltyEnabled)
        return _sb.ToString();

      if (isDecaying && !isGrief)
      {
        _sb.AppendLine($"<color={COLOR_AQUA}>Scale</color> 0 (Decaying)");
      }
      else if (isGrief)
      {
        _sb.AppendLine($"<color={COLOR_AQUA}>Scale</color> 0 (Grief)");
      }
      else
      {
        var targetID = ownerOnly ? userID : GetRecentActiveMemberAll(userID);
        var scale =
          GetDamageScale(
            targetID, _scaleCache.GetValueOrDefault(targetID, null));
        if (scale is > -1f and < 1f &&
            _prefabProtectionMultipliers.TryGetValue(
              prefabID, out var prefabMultiplier))
        {
          scale = 1f - ((1f - scale) * prefabMultiplier);
          _sb.AppendLine(
            $"<color={COLOR_AQUA}>Prefab Multiplier</color>: {prefabMultiplier:0.###}");
        }

        var prot = scale.ToPercent();
        if (scale is not -1)
        {
          _sb.AppendLine(
            $"<color={COLOR_AQUA}>Scale</color> {scale:0.###} ({(prot >= 0f ? $"{prot:0.##}% Protection" : $"+{-prot:0.##}% Damage")})");
        }
      }

      return _sb.ToString();
    }

    private void AppendTeamOrClanMembersStatus(ulong userID)
    {
      if (!Configuration.Team.TeamShare)
        return;

      var tag = Clans is not null ? GetClanTag(userID) : null;
      var isClan = !string.IsNullOrEmpty(tag);
      var members = isClan ? GetClanMembers(tag) : GetTeamMembers(userID);

      if (!(members?.Count > 1))
        return;

      _sb.AppendLine($"<color={COLOR_DARK_GREEN}>{(isClan ?
        TEXT_CLAN_MEMBER : TEXT_TEAM_MEMBER)}</color>");

      foreach (var member in members)
      {
        if (userID == member)
          continue;

        if (!_lastOnline.TryGetValue(member, out var m))
          continue;

        var memberOnline = IsOnline(member);
        _sb.Append("■ ").Append(m.UserName).Append(" | ");
        if (memberOnline)
          _sb.AppendLine($"<color={COLOR_GREEN}>Online</color>");
        else
          _sb.AppendLine($"<color={COLOR_RED}>Offline</color> | " +
            System.TimeZoneInfo.ConvertTimeFromUtc(m.LastOnlineDT, _timeZone));
      }
    }

    private string GetHelpText(ulong userID)
    {
      var nowUtc = System.DateTime.UtcNow;
      var (absoluteTimeScale, absoluteTimeScaleKeys,
        damageScale, damageScaleKeys, _) =
        GetActiveTimeScales(nowUtc.Ticks);
      var hasAbsoluteKeys = absoluteTimeScaleKeys.Length > 0;
      var hasDamageKeys = damageScaleKeys.Length > 0;

      _sb.Clear();
      _sb.AppendLine($"<color={COLOR_BLUE}>Info</color> {System.TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _timeZone):HH:mm:ss} {_timeZone.DisplayName.Split(' ')[0]}");

      if (hasAbsoluteKeys)
      {
        foreach (var key in absoluteTimeScaleKeys)
        {
          var scalePercent = absoluteTimeScale[key].ToPercent();
          var hours = key.ToString();

          _sb.AppendLine($"<color={COLOR_ORANGE}>At {hours} o'clock</color>: {(scalePercent >= 0f ? $"{scalePercent:0.#}% Protection" : $"+{-scalePercent:0.#}% Damage")}");
        }
      }

      if (hasDamageKeys)
      {
        var interimDamageScalePercent =
          Configuration.RaidProtection.InterimDamage.ToPercent();
        if (Configuration.RaidProtection.CooldownMinutes > 0)
        {
          _sb.AppendLine($"<color={COLOR_ORANGE}>First {Configuration.RaidProtection.CooldownMinutes} minutes</color>: 0% Protection")
            .AppendLine($"<color={COLOR_ORANGE}>Between {Configuration.RaidProtection.CooldownMinutes} minutes and {damageScaleKeys[0]} hours</color>: {interimDamageScalePercent:0.#}% Protection");
        }
        else
          _sb.AppendLine($"<color={COLOR_ORANGE}>First {damageScaleKeys[0]} hour(s)</color>: {interimDamageScalePercent:0.#}% Protection");

        foreach (var key in damageScaleKeys)
        {
          var scalePercent = damageScale[key].ToPercent();
          _sb.AppendLine($"<color={COLOR_ORANGE}>After {key} hours</color>: {(scalePercent >= 0f ? $"{scalePercent:0.#}% Protection" : $"+{-scalePercent:0.#}% Damage")}");
        }
      }

      if (!_lastOnline.TryGetValue(userID, out var lastOnline))
        return _sb.ToString();

      var penaltyEnabled = lastOnline.PenaltyEnd >= nowUtc.Ticks;
      if (penaltyEnabled || Configuration.Team.TeamEnablePenalty)
        _sb.AppendLine($"<color={COLOR_YELLOW}>Penalty Status</color> {(penaltyEnabled ? $"<color={COLOR_RED}>Enabled</color> {System.TimeZoneInfo.ConvertTimeFromUtc(lastOnline.PenaltyEndDT, _timeZone):HH:mm:ss}" : $"<color={COLOR_GREEN}>Disabled</color>")}");

      return _sb.ToString();
    }

#endregion Texts

#region Helper Methods

    private static string PrefixMessage(string message, bool gameTip = false)
    {
      if (string.IsNullOrEmpty(message))
        return message;

      if (gameTip)
        return string.Concat(ORP_PREFIX, message);

      if (message.StartsWith(ORP_PREFIX_COLORED, System.StringComparison.Ordinal))
        return message;

      if (message.StartsWith(ORP_PREFIX, System.StringComparison.Ordinal))
        return string.Concat(
          ORP_PREFIX_COLORED, message.Substring(ORP_PREFIX.Length));

      return string.Concat(ORP_PREFIX_COLORED, message);
    }

    private static void ChatMessage(BasePlayer player, string message) =>
      player.ChatMessage(PrefixMessage(message));

    private void Reply(ConsoleSystem.Arg arg, string message) =>
      SendReply(arg, PrefixMessage(message));

    private string Msg(string key, string userID = null) =>
      lang.GetMessage(key, this, userID);

    private const int ItemIdWood  = -151838493;
    private const int ItemIdStone = -2099697608;
    private const int ItemIdMetal =  69511070;
    private const int ItemIdHqm   =  317398316;

    private bool IsCachedCupboardDecaying(
      BuildingPrivlidge toolCupboard, bool hasProtectedMinutes)
    {
      var raidProtection = Configuration.RaidProtection;
      if (raidProtection.ProtectDecayingBase &&
          raidProtection.ProtectDecayingModularBoats)
        return false;

      var modularBoat = GetParentModularBoat(toolCupboard);
      if (modularBoat)
        return !raidProtection.ProtectDecayingModularBoats &&
          IsModularBoatDecaying(modularBoat);

      if (raidProtection.ProtectDecayingBase)
        return false;

      return IsBuildingDecaying(toolCupboard, hasProtectedMinutes);
    }

    private static bool IsBuildingDecaying(
        BuildingPrivlidge toolCupboard, bool hasProtectedMinutes)
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
      const float damageThreshold = 0.5f; // e.g., below 50% health is "damaged"

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
      const byte
        ResourceStone = 1,
        ResourceMetal = 2,
        ResourceHqm = 4;
      byte neededResources = 0;
      if (hasBlockStone)
        neededResources |= ResourceStone;
      if (hasBlockMetal)
        neededResources |= ResourceMetal;
      if (hasBlockHqm)
        neededResources |= ResourceHqm;

      byte satisfiedResources = 0;

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
            satisfiedResources |= ResourceStone;
            break;

          case ItemIdMetal: // metal fragments
            satisfiedResources |= ResourceMetal;
            break;

          case ItemIdHqm: // high quality metal
            satisfiedResources |= ResourceHqm;
            break;
        }
      }

      // if something other than wood is missing, the building is not decaying
      //  solely due to twig, so we can immediately report that the building is
      //  decaying here
      if (satisfiedResources != neededResources)
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

        var buildCost = decayEntity.BuildCost().Items;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetNetworkID(BaseNetworkable networkable) =>
      networkable?.net?.ID.Value ?? 0UL;

    private bool NeedsTcTracking =>
      Configuration.TaxProtection.Enabled ||
      Configuration.StatusHud.Enabled ||
      Configuration.MapMarker.Enabled ||
      _ddrawSessions.Count > 0;

    private static bool RequiresTcCacheWithoutDdraw =>
      !Configuration.RaidProtection.ProtectDecayingBase ||
      !Configuration.RaidProtection.ProtectGriefTcs ||
      Configuration.TaxProtection.Enabled ||
      Configuration.StatusHud.Enabled ||
      Configuration.MapMarker.Enabled;

    private bool RequiresTcCache =>
      RequiresTcCacheWithoutDdraw ||
      _ddrawSessions.Count > 0;

    private bool ShouldMaintainCupboardCreationData =>
      _ddrawSessions.Count is 0 ||
      RequiresTcCacheWithoutDdraw;

#endregion Helper Methods

#region API

    public float API_GetEntityDamageScale(
      BaseCombatEntity entity, BasePlayer attacker) =>
        TryGetApiEntityDecision(entity, attacker, out var decision) &&
        decision.Kind is DamageDecisionKind.ApplyScale ? decision.Scale : -1f;

    public ulong API_GetEntityProtectionTargetID(
      BaseCombatEntity entity, BasePlayer attacker) =>
        TryGetApiEntityDecision(entity, attacker, out var decision) ?
          decision.TargetID : 0UL;

    public float API_GetPlayerDamageScale(ulong playerID) =>
      GetApiPlayerDamageScale(playerID, System.DateTime.UtcNow);

    public long API_GetRemainingTimeTicks(ulong playerID) =>
      GetApiPlayerRemainingTimeTicks(
        playerID, System.DateTime.UtcNow);

    public long API_GetPenaltyEndUtcTicks(ulong playerID)
    {
      var nowUtc = System.DateTime.UtcNow;
      return _lastOnline.TryGetValue(playerID, out var lastOnline) &&
             IsApiPenaltyActive(lastOnline, nowUtc) ?
               lastOnline.PenaltyEndTicks : 0L;
    }

    public bool API_EnablePenalty(ulong playerID, float durationHours)
    {
      if (!TryEnablePenalty(playerID, durationHours))
        return false;

      MarkDataDirty();
      return true;
    }

    public bool API_DisablePenalty(ulong playerID)
    {
      if (!TryDisablePenalty(playerID))
        return false;

      MarkDataDirty();
      return true;
    }

    public int API_EnablePenalties(
      ICollection<ulong> playerIDs, float durationHours)
    {
      if (!IsPenaltyDurationValid(durationHours) ||
          playerIDs is null || playerIDs.Count is 0)
        return 0;

      _tmpIdsScratch.Clear();
      foreach (var playerID in playerIDs)
        _tmpIdsScratch.Add(playerID);

      var changedPlayers = 0;
      foreach (var playerID in _tmpIdsScratch)
      {
        if (TryEnablePenalty(playerID, durationHours))
          changedPlayers++;
      }

      if (changedPlayers is not 0)
        MarkDataDirty();

      return changedPlayers;
    }

    public int API_DisablePenalties(ICollection<ulong> playerIDs)
    {
      if (playerIDs is null || playerIDs.Count is 0)
        return 0;

      _tmpIdsScratch.Clear();
      foreach (var playerID in playerIDs)
        _tmpIdsScratch.Add(playerID);

      var changedPlayers = 0;
      foreach (var playerID in _tmpIdsScratch)
      {
        if (TryDisablePenalty(playerID))
          changedPlayers++;
      }

      if (changedPlayers is not 0)
        MarkDataDirty();

      return changedPlayers;
    }

    private bool TryGetApiEntityDecision(
      BaseCombatEntity entity, BasePlayer attacker, out DamageDecision decision)
    {
      decision = default;
      if (!entity || !IsProtected(entity))
        return false;

      decision = EvaluateProtection(entity, attacker, System.DateTime.UtcNow);
      return true;
    }

    private float GetApiPlayerDamageScale(
      ulong playerID, System.DateTime nowUtc)
      => GetApiPlayerDamageScale(playerID, nowUtc, out _);

    private float GetApiPlayerDamageScale(
      ulong playerID, System.DateTime nowUtc, out float[] damageScaleKeys)
    {
      if (_lastOnline.TryGetValue(playerID, out var lastOnline))
        return GetApiPlayerDamageScale(
          playerID, lastOnline, nowUtc, out damageScaleKeys);

      damageScaleKeys = null;
      return -1f;
    }

    private float GetApiPlayerDamageScale(
      ulong playerID, LastOnlineData lastOnline,
      System.DateTime nowUtc, out float[] damageScaleKeys)
    {
      var scaleCache = GetOrCreateScaleCache(playerID);
      if (!IsApiPenaltyActive(lastOnline, nowUtc) &&
          scaleCache.HasProtectPermission)
        return GetDamageScale(
          playerID, lastOnline, scaleCache, nowUtc, out _, out damageScaleKeys);

      damageScaleKeys = null;
      return -1f;
    }

    private long GetApiPlayerRemainingTimeTicks(
      ulong playerID, System.DateTime nowUtc)
    {
      if (!_lastOnline.TryGetValue(playerID, out var lastOnline))
        return 0L;

      var scale = GetApiPlayerDamageScale(
        playerID, lastOnline, nowUtc, out var damageScaleKeys);
      if (scale is -1f ||
          damageScaleKeys is null || damageScaleKeys.Length is 0)
        return 0L;

      var remainingHours = damageScaleKeys[^1] -
        GetOfflineHours(lastOnline, nowUtc);
      return GetClampedTimeSpanFromHours(remainingHours).Ticks;
    }

    private static bool IsApiPenaltyActive(
      LastOnlineData lastOnline, System.DateTime nowUtc) =>
        lastOnline is not null && nowUtc.Ticks <= lastOnline.PenaltyEndTicks;

    private static bool IsPenaltyDurationValid(float durationHours)
    {
      if (durationHours <= 0f || float.IsNaN(durationHours) ||
          float.IsInfinity(durationHours))
        return false;

      return durationHours <=
        (System.DateTime.MaxValue.Ticks - System.DateTime.UtcNow.Ticks) /
        (double)System.TimeSpan.TicksPerHour;
    }

    private static System.TimeSpan GetClampedTimeSpanFromHours(float hours)
    {
      if (!(hours > 0f))
        return System.TimeSpan.Zero;

      var maxHours = System.TimeSpan.MaxValue.Ticks /
        (double)System.TimeSpan.TicksPerHour;
      return hours >= maxHours ? System.TimeSpan.MaxValue :
        System.TimeSpan.FromHours(hours);
    }

    private bool TryEnablePenalty(ulong playerID, float durationHours) =>
      _lastOnline.TryGetValue(playerID, out var lastOnline) &&
      TryEnablePenalty(playerID, lastOnline, durationHours);

    private bool TryEnablePenalty(
      ulong playerID, LastOnlineData lastOnline, float durationHours)
    {
      if (!IsPenaltyDurationValid(durationHours) || lastOnline is null)
        return false;

      var penaltyEndUtc = System.DateTime.UtcNow.AddHours(durationHours);
      lastOnline.EnablePenalty(penaltyEndUtc);
      CacheDamageScale(playerID, -1f);
      RefreshProtectionViews(playerID);
      return true;
    }

    private bool TryDisablePenalty(ulong playerID) =>
      _lastOnline.TryGetValue(playerID, out var lastOnline) &&
      TryDisablePenalty(playerID, lastOnline);

    private bool TryDisablePenalty(ulong playerID, LastOnlineData lastOnline)
    {
      if (lastOnline?.PenaltyEndTicks is 0L or null)
        return false;

      lastOnline.DisablePenalty();
      CacheDamageScale(playerID, -1f);
      RefreshProtectionViews(playerID);
      return true;
    }

#endregion API

#region Scheduled Timescales & Wipe Templates

#region Fields

    private readonly List<ScheduledTimescale> _scheduledTimescales = new();
    private readonly List<ScheduledTimescale> _scheduledTimescalesByStartTime = new();
    private readonly HashSet<System.Guid> _scheduledTimescaleIds = new();
    private readonly Dictionary<System.Guid, ScheduledTimescale> _scheduledTimescalesByRuntimeID = new();
    private TimeScaleSet _defaultTimeScales;
    private WipeTemplatesData _wipeTemplatesData = new();
#if CARBON && !MINIMAL
    private AdminModule _admin;
    private DatePickerModule _dp;
    private AdminModule.Tab _scheduledTimescaleTab;
    private readonly Dictionary<ulong, AdminModule.Tab> _scheduledTimescalePlayerTabs = new();
    private AdminModule.Tab _wipeTemplateTab;
    private readonly Dictionary<ulong, AdminModule.Tab> _wipeTemplatePlayerTabs = new();

    // Keep every transient view/editor state in one per-player object. New
    // schedule views can extend this state without another parallel map and
    // cleanup path
    private readonly Dictionary<ulong, ScheduledTimescaleUiState> _scheduledTimescaleUiStates = new();
    private readonly Dictionary<ulong, WipeTemplateUiState> _wipeTemplateUiStates = new();
#endif

#endregion Fields

#region Constants

    private const string SCHEDULES_DATA_FILE_NAME = nameof(ScheduledTimescale);
    private const string TEMPLATE_DATA_FILE_NAME = nameof(WipeTemplate);

    // Carbon reserves one native row for the pinned profile actions on the
    // profile column; the detail column has no pinned action.
    private const int
      MAX_HOUR = 23,
      FIRST_PAGE = 0,
      PROFILE_COLUMN = 0,
      DETAILS_COLUMN = 1,
      PINNED_PROFILE_COLUMN = -1,
      PROFILE_NAME_MAX_LENGTH = 128,
      TIME_MAX_LENGTH = 5,
      SCALE_VALUE_MAX_LENGTH = 16,
      // Profile column page size; the header and dynamic rows are counted here,
      // while pinned bottom actions are outside this page calculation
      PROFILE_ROWS_PER_PAGE = 18,
      // Details page size; fixed metadata/action rows precede dynamic entries
      DETAILS_ROWS_PER_PAGE = 19,
      DETAILS_FIXED_ROW_COUNT = 10,
      DETAILS_ENTRY_HEADER_ROW_COUNT = 1;
    private const float DATE_BUTTON_PRIORITY = 0.25f;
    private const string
      MISSING_PROFILE_MESSAGE = "A missing scheduled timescale profile is ignored.",
      DUPLICATE_ID_MESSAGE = "Profile ID is duplicated",
      NAME_REQUIRED_MESSAGE = "Name is required",
      DATE_RANGE_MESSAGE = "Start must be before end",
      OVERLAP_MESSAGE = "Scheduled profiles cannot overlap.",
      HOUR_MESSAGE = "Absolute time hour must be between 0 and 23.",
      DAMAGE_SCALE_MESSAGE = "Damage scale must be a finite number.",
      OFFLINE_HOURS_MESSAGE = "Offline hours must be a finite number.",
      ABSOLUTE_SCALE_MESSAGE = "Absolute time scale must be finite",
      ABSOLUTE_SCALE_RESERVED_MESSAGE = "Absolute time scale cannot be -1.",
      ENTRY_FINITE_MESSAGE = "Offline time scale entries must be finite",
      ENTRY_KEY_EXISTS_MESSAGE = "A scale entry already uses that key.",
      TIME_FORMAT_MESSAGE = "Time must use the 24-hour HH:mm format.",
      TIMEZONE_MESSAGE = "The selected local time is invalid or ambiguous in the configured timezone.",
      UI_SCHEDULES_TAB_NAME = "Schedules",
      UI_TEMPLATE_TAB_NAME = "Wipe Templates",
      UI_ACTIVE_TEMPLATE_FORMAT = "Active template: {0}",
      UI_ACTIVE_PHASE_FORMAT = "Active phase: {0}",
      UI_NO_ACTIVE_TEMPLATE = "No template phase is active",
      UI_ADD_TEMPLATE = "Add template",
      UI_SELECT_OR_ADD_TEMPLATE = "Select or add a template",
      UI_SET_DEFAULT_TEMPLATE = "Set default",
      UI_QUEUE_NEXT_WIPE = "Queue next wipe",
      UI_ADD_PHASE = "Add phase",
      UI_DELETE_TEMPLATE = "Delete template",
      UI_DELETE_TEMPLATE_FORMAT = "Delete '{0}'?",
      UI_EDIT_TEMPLATE_PHASE = "Edit template phase",
      UI_START_OFFSET_HOURS = "Start offset hours",
      UI_END_OFFSET_HOURS = "End offset hours",
      UI_TEMPLATE_SCALE_COUNT_FORMAT = "Current scales: {0} absolute, {1} offline",
      UI_CREATE_TEMPLATE_FROM_HERE = "Create wipe template from here",
      UI_ADD_AFTER_SELECTED = "Add after selected",
      UI_TEMPLATE_PHASE_FORMAT = "{0}: +{1:g} - +{2:g}",
      WIPE_TEMPLATE_PROFILE_NAME_FORMAT = "{0} - {1}",
      DEFAULT_WIPE_TEMPLATE_NAME = "New wipe template",
      DEFAULT_WIPE_TEMPLATE_PHASE_NAME = "Normal protection",
      DEFAULT_TEMPLATE_PHASE_NAME = "New phase",
      FALLBACK_WIPE_TEMPLATE_NAME = "Wipe template",
      IMPORTED_WIPE_TEMPLATE_NAME = "Imported wipe template",
      FALLBACK_TEMPLATE_PHASE_NAME = "Phase",
      WIPE_TEMPLATE_INVALID_PHASE_WARNING_FORMAT = "Wipe template '{0}' was not applied: an invalid phase was found.",
      WIPE_TEMPLATE_OVERLAP_WARNING_FORMAT = "Wipe template '{0}' was not applied: it overlaps an existing scheduled profile.",
      WIPE_TEMPLATE_PHASE_OVERLAP_WARNING_FORMAT = "Wipe template '{0}' was not applied: its phases overlap.",
      WIPE_TEMPLATE_APPLIED_MESSAGE_FORMAT = "Applied wipe template '{0}' with {1} phase(s).",
      TEMPLATE_OFFSET_FINITE_MESSAGE = "Offsets must be finite hours.",
      TEMPLATE_OFFSET_RANGE_MESSAGE = "Offsets are out of range.",
      TEMPLATE_OFFSET_ORDER_MESSAGE = "End offset must be after start offset.",
      TEMPLATE_PHASE_OVERLAP_MESSAGE = "Template phases cannot overlap.",
      UI_ACCESS_DENIED = "Access denied",
      UI_PROFILES = "Scheduled timescale profiles",
      UI_MISSING_PROFILE = "Missing profile",
      UI_ADD_PROFILE = "Add profile",
      UI_PROFILE = "Profile",
      UI_NEW_PROFILE = "New profile",
      UI_EDIT_PROFILE = "Edit profile",
      UI_NAME = "Name",
      UI_SCHEDULE = "Schedule",
      UI_START = "Start",
      UI_END = "End",
      UI_TIME_ZONE = "Time zone",
      UI_DAMAGE_SCALE = "Damage scale",
      UI_ABSOLUTE_TIME = "Absolute time",
      UI_OFFLINE_TIME = "Offline time",
      UI_ADD_SCALE = "Add scale",
      UI_REPLACE_STANDARD_VALUES = "Replace with standard values",
      UI_HOUR_LABEL = "Hour",
      UI_EDIT = "Edit",
      UI_COPY = "Copy",
      UI_DELETE = "Delete",
      UI_SAVE_SCALE_CHANGES = "Save scale changes",
      UI_DIRTY_CHANGES_MESSAGE = "Save scale changes before changing profiles.",
      UI_PREVIOUS = "<",
      UI_NEXT = ">",
      UI_MOVE_DAY = "Day",
      UI_MOVE_WEEK = "Week",
      UI_MOVE_MONTH = "Month",
      UI_DELETE_EXPIRED_COUNT_FORMAT = "Delete expired ({0})",
      UI_DELETE_PROFILE_FORMAT = "Delete '{0}' and all of its scale entries?",
      UI_DELETE_EXPIRED_FORMAT = "Delete {0} expired profile(s)?",
      UI_START_DATE = "Start date",
      UI_START_TIME = "Start time",
      UI_END_DATE = "End date",
      UI_END_TIME = "End time",
      UI_CHANGE = "Change",
      UI_CANCEL = "Cancel",
      UI_SAVE = "Save",
      UI_NEW_ABSOLUTE_SCALE = "New absolute scale",
      UI_EDIT_ABSOLUTE_SCALE = "Edit absolute scale",
      UI_NEW_OFFLINE_SCALE = "New offline scale",
      UI_EDIT_OFFLINE_SCALE = "Edit offline scale",
      UI_HOUR = "Hour (0-23)",
      UI_OFFLINE_HOURS = "Offline hours",
      UI_SCALE = "Scale",
      UI_INVALID_FIELD_PREFIX = "! ",
      UI_INVALID = "INVALID",
      UI_UPCOMING_FROM_FORMAT = "UPCOMING from {0}",
      UI_ACTIVE_UNTIL_FORMAT = "ACTIVE until {0}",
      UI_EXPIRED = "EXPIRED",
      DEFAULT_PROFILE_NAME = "New schedule",
      HOUR_TIME_FORMAT = "{0}:00",
      INVALID_DATE_TEXT = "invalid date",
      MIDNIGHT_TIME = "00:00",
      DATE_FORMAT = "dd-MM-yyyy",
      TIME_FORMAT = "HH:mm",
      DATE_TIME_FORMAT = "dd-MM-yyyy HH:mm";

    private const long
      DEFAULT_WIPE_TEMPLATE_DURATION_TICKS =
        System.TimeSpan.TicksPerDay * 14L,
      DEFAULT_TEMPLATE_PHASE_DURATION_TICKS =
        System.TimeSpan.TicksPerDay;

#endregion Constants

#region Classes

    private sealed class ScheduledTimescalesData
    {
      [JsonProperty(PropertyName = "Scheduled timescale profiles")]
      public List<ScheduledTimescale> Profiles { get; set; } = new();
    }

    private sealed class ScheduledTimescale
    {
      [JsonProperty(PropertyName = "ID")]
      public System.Guid ID { get; set; }

      // Keep UI identity separate so every loaded row remains addressable
      // without changing its JSON data
      [JsonIgnore]
      public System.Guid RuntimeID { get; set; }

      [JsonIgnore]
      public string IDText => ID.ToString("N");

      [JsonProperty(PropertyName = "Name")]
      public string Name { get; set; }

      [JsonProperty(PropertyName = "Start UTC ticks")]
      public long StartUtcTicks { get; set; }

      [JsonProperty(PropertyName = "End UTC ticks")]
      public long EndUtcTicks { get; set; }

      [JsonProperty(PropertyName = "Scale of damage depending on the current hour of the real day")]
      public Dictionary<int, float> AbsoluteTimeScale { get; set; } = new();

      [JsonProperty(PropertyName = "Scale of damage depending on the offline time in hours")]
      public Dictionary<float, float> OfflineTimeScale { get; set; } = new();

      [JsonIgnore]
      public string InvalidReason { get; set; }

      [JsonIgnore]
      public TimeScaleSet CachedTimeScales { get; set; }
    }

    private sealed class TimeScaleSet
    {
      public readonly Dictionary<int, float> AbsoluteTimeScale;
      public readonly int[] AbsoluteTimeScaleKeys;
      public readonly Dictionary<float, float> DamageScale;
      public readonly float[] DamageScaleKeys;

      public TimeScaleSet(
        Dictionary<int, float> absoluteTimeScale,
        Dictionary<float, float> damageScale)
      {
        AbsoluteTimeScale = absoluteTimeScale;
        DamageScale = damageScale;
        AbsoluteTimeScaleKeys = new int[absoluteTimeScale.Count];
        DamageScaleKeys = new float[damageScale.Count];
        absoluteTimeScale.Keys.CopyTo(AbsoluteTimeScaleKeys, 0);
        damageScale.Keys.CopyTo(DamageScaleKeys, 0);
        System.Array.Sort(AbsoluteTimeScaleKeys);
        System.Array.Sort(DamageScaleKeys);
      }
    }

    private sealed class WipeTemplatesData
    {
      [JsonProperty(PropertyName = "Templates")]
      public List<WipeTemplate> Templates { get; set; } = new();

      [JsonProperty(PropertyName = "Default template ID")]
      public System.Guid DefaultTemplateID { get; set; }

      [JsonProperty(PropertyName = "Queued next-wipe template ID")]
      public System.Guid QueuedNextWipeTemplateID { get; set; }

      [JsonProperty(PropertyName = "Last materialized wipe UTC ticks")]
      public long LastMaterializedWipeUtcTicks { get; set; }

      [JsonProperty(PropertyName = "Last materialized template ID")]
      public System.Guid LastMaterializedTemplateID { get; set; }

      [JsonProperty(PropertyName = "Generated profile IDs")]
      public List<System.Guid> GeneratedProfileIDs { get; set; } = new();
    }

    private sealed class WipeTemplate
    {
      [JsonProperty(PropertyName = "ID")]
      public System.Guid ID { get; set; } = System.Guid.NewGuid();

      [JsonProperty(PropertyName = "Name")]
      public string Name { get; set; }

      [JsonProperty(PropertyName = "Phases")]
      public List<WipeTemplatePhase> Phases { get; set; } = new();
    }

    private sealed class WipeTemplatePhase
    {
      [JsonProperty(PropertyName = "Name")]
      public string Name { get; set; }

      [JsonProperty(PropertyName = "Start offset ticks")]
      public long StartOffsetTicks { get; set; }

      [JsonProperty(PropertyName = "End offset ticks")]
      public long EndOffsetTicks { get; set; }

      [JsonProperty(PropertyName = "Scale of damage depending on the current hour of the real day")]
      public Dictionary<int, float> AbsoluteTimeScale { get; set; } = new();

      [JsonProperty(PropertyName = "Scale of damage depending on the offline time in hours")]
      public Dictionary<float, float> OfflineTimeScale { get; set; } = new();
    }

#if CARBON && !MINIMAL
    private sealed class ScheduledTimescaleUiState
    {
      public System.Guid SelectedProfileRuntimeID;
      public ScheduledTimescaleEditContext ProfileEditor;
      public ScheduledTimescaleEntryEditContext EntryEditor;
      public ScheduledTimescaleScaleEditContext ScaleEditor;
      public string Notice;
      public ScheduledTimescaleEntryKind EntryKind =
        ScheduledTimescaleEntryKind.Absolute;
    }

    private sealed class WipeTemplateUiState
    {
      public System.Guid SelectedTemplateID;
      public WipeTemplatePhaseEditContext PhaseEditor;
    }

    private sealed class WipeTemplatePhaseEditContext
    {
      public WipeTemplate Template;
      public WipeTemplatePhase StoredPhase;
      public WipeTemplatePhase Phase;
      public ScheduledTimescaleScaleDraft ScaleDraft;
      public ScheduledTimescaleEntryEditContext EntryEditor;
      public string Name;
      public string StartHours;
      public string EndHours;
      public string Error;
      public ScheduledTimescaleEntryKind EntryKind =
        ScheduledTimescaleEntryKind.Absolute;
      public bool IsDirty;
    }

    private sealed class ScheduledTimescaleEditContext
    {
      public ScheduledTimescale Profile;
      public ScheduledTimescale Draft;
      public System.DateTime StartDate;
      public System.DateTime EndDate;
      public string StartTime;
      public string EndTime;
      public string Error;
      public string InvalidField;
      public int ReturnPage;
    }

    private sealed class ScheduledTimescaleEntryEditContext
    {
      public ScheduledTimescaleScaleDraft Draft;
      public string Key;
      public string Scale;
      public string Error;
      public string InvalidField;
      public ScheduledTimescaleEntryKind Kind;
      public int ExistingAbsoluteHour;
      public float ExistingOfflineHours;
      public int ReturnPage;
      public bool HasExistingKey;
    }

    private sealed class ScheduledTimescaleScaleDraft
    {
      public readonly Dictionary<int, float> AbsoluteTimeScale;
      public readonly Dictionary<float, float> OfflineTimeScale;
      public int[] AbsoluteTimeScaleKeys;
      public float[] OfflineTimeKeys;

      public ScheduledTimescaleScaleDraft(ScheduledTimescale profile)
        : this(profile.AbsoluteTimeScale, profile.OfflineTimeScale, true) { }

      public ScheduledTimescaleScaleDraft(
        Dictionary<int, float> absoluteTimeScale,
        Dictionary<float, float> offlineTimeScale, bool copy)
      {
        AbsoluteTimeScale = copy ? new(absoluteTimeScale) : absoluteTimeScale;
        OfflineTimeScale = copy ? new(offlineTimeScale) : offlineTimeScale;
        RefreshKeys();
      }

      public void RefreshKeys()
      {
        AbsoluteTimeScaleKeys = new int[AbsoluteTimeScale.Count];
        OfflineTimeKeys = new float[OfflineTimeScale.Count];
        AbsoluteTimeScale.Keys.CopyTo(AbsoluteTimeScaleKeys, 0);
        OfflineTimeScale.Keys.CopyTo(OfflineTimeKeys, 0);
        System.Array.Sort(AbsoluteTimeScaleKeys);
        System.Array.Sort(OfflineTimeKeys);
      }
    }

    private sealed class ScheduledTimescaleScaleEditContext
    {
      public ScheduledTimescale Profile;
      public ScheduledTimescaleScaleDraft Draft;
      public bool IsDirty;
    }

    // Carbon uses Tab.Equals when it finds the selected top-bar tab. Instances
    // remain per-player so their native row callbacks cannot cross sessions
    private sealed class ScheduledTimescaleAdminTab(
      CarbonPlugin plugin,
      System.Action<AdminModule.PlayerSession, AdminModule.Tab> onChange =
        null)
      : AdminModule.Tab(ADMIN_TAB_ID, UI_SCHEDULES_TAB_NAME, plugin, onChange)
    {
      public override bool Equals(object obj) =>
        obj is ScheduledTimescaleAdminTab;

      public override int GetHashCode() =>
        System.StringComparer.Ordinal.GetHashCode(ADMIN_TAB_ID);
    }

    private sealed class WipeTemplateAdminTab(
      CarbonPlugin plugin,
      System.Action<AdminModule.PlayerSession, AdminModule.Tab> onChange = null)
      : AdminModule.Tab(WIPE_TEMPLATE_ADMIN_TAB_ID, UI_TEMPLATE_TAB_NAME,
          plugin, onChange)
    {
      public override bool Equals(object obj) =>
        obj is WipeTemplateAdminTab;

      public override int GetHashCode() =>
        System.StringComparer.Ordinal.GetHashCode(WIPE_TEMPLATE_ADMIN_TAB_ID);
    }
#endif

#endregion Classes

#region Persistence

    private void LoadScheduledTimescales()
    {
      try
      {
        _scheduledTimescales.Clear();
        var dataFileName = $"{Name}/{SCHEDULES_DATA_FILE_NAME}";
        if (Interface.Oxide.DataFileSystem.ExistsDatafile(dataFileName))
        {
          var data =
            Interface.Oxide.DataFileSystem.ReadObject<ScheduledTimescalesData>(dataFileName);
          if (data?.Profiles is not null)
            _scheduledTimescales.AddRange(data.Profiles);
        }

        if (NormalizeScheduledTimescales())
          SaveScheduledTimescales();
      }
      catch (System.Exception ex)
      {
        PrintError($"Failed to load scheduled timescales: {ex}");
        _scheduledTimescales.Clear();
        _scheduledTimescalesByStartTime.Clear();
        _scheduledTimescaleIds.Clear();
        _scheduledTimescalesByRuntimeID.Clear();
      }
    }

    private void SaveScheduledTimescales() =>
      Interface.Oxide.DataFileSystem.WriteObject(
        $"{Name}/{SCHEDULES_DATA_FILE_NAME}",
        new ScheduledTimescalesData { Profiles = _scheduledTimescales });

    private bool NormalizeScheduledTimescales()
    {
      _scheduledTimescalesByStartTime.Clear();
      _scheduledTimescaleIds.Clear();
      _scheduledTimescalesByRuntimeID.Clear();
      _scheduledTimescales.Sort(CompareScheduledTimescales);

      var previousEndTicks = long.MinValue;
      var changed = false;

      foreach (var profile in _scheduledTimescales)
      {
        if (profile is null)
        {
          PrintWarning(MISSING_PROFILE_MESSAGE);
          continue;
        }

        if (profile.ID == System.Guid.Empty)
        {
          profile.ID = System.Guid.NewGuid();
          changed = true;
        }

        if (profile.RuntimeID == System.Guid.Empty ||
            _scheduledTimescalesByRuntimeID.ContainsKey(profile.RuntimeID))
          profile.RuntimeID = System.Guid.NewGuid();

        _scheduledTimescalesByRuntimeID.Add(profile.RuntimeID, profile);

        if (profile.AbsoluteTimeScale is null)
        {
          profile.AbsoluteTimeScale = new();
          changed = true;
        }

        if (profile.OfflineTimeScale is null)
        {
          profile.OfflineTimeScale = new();
          changed = true;
        }
        profile.CachedTimeScales = new(profile.AbsoluteTimeScale, profile.OfflineTimeScale);
        profile.InvalidReason = GetScheduledTimescaleInvalidReason(profile);

        if (!_scheduledTimescaleIds.Add(profile.ID))
          profile.InvalidReason ??= DUPLICATE_ID_MESSAGE;

        if (profile.InvalidReason is null &&
            profile.StartUtcTicks < previousEndTicks)
          profile.InvalidReason = "Overlaps another scheduled profile";

        if (profile.InvalidReason is not null)
        {
          PrintWarning($"Scheduled timescale '{profile.Name ?? profile.IDText}' is ignored: {profile.InvalidReason}");
          continue;
        }

        previousEndTicks = profile.EndUtcTicks;
        _scheduledTimescalesByStartTime.Add(profile);
      }

      return changed;
    }

    private static int CompareScheduledTimescales(
      ScheduledTimescale left, ScheduledTimescale right)
    {
      return (left, right) switch
      {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        _ => left.StartUtcTicks.CompareTo(right.StartUtcTicks)
      };
    }

    private static string GetScheduledTimescaleInvalidReason(
      ScheduledTimescale profile)
    {
      if (string.IsNullOrWhiteSpace(profile.Name))
        return NAME_REQUIRED_MESSAGE;

      if (profile.StartUtcTicks <= 0L ||
          profile.StartUtcTicks > System.DateTime.MaxValue.Ticks ||
          profile.EndUtcTicks > System.DateTime.MaxValue.Ticks ||
          profile.EndUtcTicks <= profile.StartUtcTicks)
        return DATE_RANGE_MESSAGE;

      foreach (var (key, value) in profile.AbsoluteTimeScale)
      {
        if (key is < 0 or > MAX_HOUR)
          return HOUR_MESSAGE;

        if (!IsFinite(value))
          return ABSOLUTE_SCALE_MESSAGE;

        if (value is -1f)
          return ABSOLUTE_SCALE_RESERVED_MESSAGE;
      }

      foreach (var (key, value) in profile.OfflineTimeScale)
      {
        if (!IsFinite(key) || !IsFinite(value))
          return ENTRY_FINITE_MESSAGE;
      }

      return null;
    }

    private static bool IsFinite(float value) =>
      !float.IsNaN(value) && !float.IsInfinity(value);

#region Template

    private void LoadWipeTemplates()
    {
      try
      {
        var dataFileName = $"{Name}/{TEMPLATE_DATA_FILE_NAME}";
        _wipeTemplatesData =
          Interface.Oxide.DataFileSystem.ExistsDatafile(dataFileName) ?
            Interface.Oxide.DataFileSystem.ReadObject<WipeTemplatesData>(dataFileName) :
            new();
        if (NormalizeWipeTemplates())
          SaveWipeTemplates();
      }
      catch (System.Exception ex)
      {
        PrintError($"Failed to load wipe templates: {ex}");
        _wipeTemplatesData = new();
      }
    }

    private void SaveWipeTemplates() =>
      Interface.Oxide.DataFileSystem.WriteObject(
        $"{Name}/{TEMPLATE_DATA_FILE_NAME}", _wipeTemplatesData);

    private bool NormalizeWipeTemplates()
    {
      var changed = false;
      if (_wipeTemplatesData is null)
      {
        _wipeTemplatesData = new();
        changed = true;
      }
      if (_wipeTemplatesData.Templates is null)
      {
        _wipeTemplatesData.Templates = new();
        changed = true;
      }
      if (_wipeTemplatesData.GeneratedProfileIDs is null)
      {
        _wipeTemplatesData.GeneratedProfileIDs = new();
        changed = true;
      }

      var ids = new HashSet<System.Guid>();
      for (var i = _wipeTemplatesData.Templates.Count - 1; i >= 0; i--)
      {
        var template = _wipeTemplatesData.Templates[i];
        if (template is null)
        {
          _wipeTemplatesData.Templates.RemoveAt(i);
          changed = true;
          continue;
        }

        if (template.ID == System.Guid.Empty || !ids.Add(template.ID))
        {
          template.ID = System.Guid.NewGuid();
          changed = true;
        }

        var normalizedName = template.Name?.Trim();
        if (template.Name != normalizedName)
        {
          template.Name = normalizedName;
          changed = true;
        }
        if (template.Phases is null)
        {
          template.Phases = new();
          changed = true;
        }
        for (var phaseIndex = template.Phases.Count - 1;
             phaseIndex >= 0; phaseIndex--)
        {
          var phase = template.Phases[phaseIndex];
          if (!IsValidWipeTemplatePhase(phase))
          {
            template.Phases.RemoveAt(phaseIndex);
            changed = true;
          }
        }
        for (var phaseIndex = 1; phaseIndex < template.Phases.Count;
             phaseIndex++)
        {
          if (CompareWipeTemplatePhases(
                template.Phases[phaseIndex - 1], template.Phases[phaseIndex]) > 0)
          {
            template.Phases.Sort(CompareWipeTemplatePhases);
            changed = true;
            break;
          }
        }
      }

      if (_wipeTemplatesData.DefaultTemplateID != System.Guid.Empty &&
          GetWipeTemplate(_wipeTemplatesData.DefaultTemplateID) is null)
      {
        _wipeTemplatesData.DefaultTemplateID = System.Guid.Empty;
        changed = true;
      }
      if (_wipeTemplatesData.QueuedNextWipeTemplateID != System.Guid.Empty &&
          GetWipeTemplate(_wipeTemplatesData.QueuedNextWipeTemplateID) is null)
      {
        _wipeTemplatesData.QueuedNextWipeTemplateID = System.Guid.Empty;
        changed = true;
      }

      return changed;
    }

    private static bool IsValidWipeTemplatePhase(WipeTemplatePhase phase) =>
      phase is not null &&
      phase.StartOffsetTicks >= 0L &&
      phase.EndOffsetTicks > phase.StartOffsetTicks &&
      phase.EndOffsetTicks <= System.DateTime.MaxValue.Ticks &&
      phase.AbsoluteTimeScale is not null &&
      phase.OfflineTimeScale is not null;

    private static int CompareWipeTemplatePhases(
      WipeTemplatePhase left, WipeTemplatePhase right)
    {
      return (left, right) switch
      {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        _ => left.StartOffsetTicks != right.StartOffsetTicks ?
          left.StartOffsetTicks.CompareTo(right.StartOffsetTicks) :
          left.EndOffsetTicks.CompareTo(right.EndOffsetTicks)
      };
    }

#endregion Template

#endregion Persistence

#region Runtime

    private void InitializeScheduledTimescales()
    {
      CacheDefaultTimescales();
      LoadScheduledTimescales();
      LoadWipeTemplates();
#if CARBON && !MINIMAL
      if (!Configuration.RaidProtection.EnableScheduledTimescales)
        return;

      _admin = Base.BaseModule.GetModule<AdminModule>();
      _dp = Base.BaseModule.GetModule<DatePickerModule>();
      RegisterScheduledTimescaleAdminTab();
      RegisterWipeTemplateAdminTab();
#endif
    }

    private void UnloadScheduledTimescales()
    {
#if CARBON && !MINIMAL
      foreach (var player in BasePlayer.activePlayerList)
        CloseScheduledTimescaleEditor(player);

      foreach (var tab in _scheduledTimescalePlayerTabs.Values)
        tab.Dispose();

      _scheduledTimescalePlayerTabs.Clear();
      _admin?.UnregisterTab(ADMIN_TAB_ID);
      _scheduledTimescaleTab = null;
#endif
      _scheduledTimescales.Clear();
      _scheduledTimescalesByStartTime.Clear();
      _scheduledTimescaleIds.Clear();
      _scheduledTimescalesByRuntimeID.Clear();
      _defaultTimeScales = null;
      _wipeTemplatesData = null;
#if CARBON && !MINIMAL
      foreach (var tab in _wipeTemplatePlayerTabs.Values)
        tab.Dispose();

      _wipeTemplatePlayerTabs.Clear();
      _wipeTemplateUiStates.Clear();
      _admin?.UnregisterTab(WIPE_TEMPLATE_ADMIN_TAB_ID);
      _wipeTemplateTab = null;
      _scheduledTimescaleUiStates.Clear();
      _admin = null;
      _dp = null;
#endif
    }

    private WipeTemplate GetWipeTemplate(System.Guid id)
    {
      if (id == System.Guid.Empty)
        return null;

      foreach (var template in _wipeTemplatesData.Templates)
      {
        if (template?.ID == id)
          return template;
      }

      return null;
    }

    private void MaterializeQueuedWipeTemplate(long wipeUtcTicks)
    {
      if (!Configuration.RaidProtection.EnableScheduledTimescales ||
          wipeUtcTicks <= 0L ||
          _wipeTemplatesData.LastMaterializedWipeUtcTicks == wipeUtcTicks)
        return;

      var queuedID = _wipeTemplatesData.QueuedNextWipeTemplateID;
      var template = GetWipeTemplate(queuedID) ??
        GetWipeTemplate(_wipeTemplatesData.DefaultTemplateID);
      if (template is null)
        return;

      var generated = new List<ScheduledTimescale>(template.Phases.Count);
      foreach (var phase in template.Phases)
      {
        if (!IsValidWipeTemplatePhase(phase) ||
            !TryCreateMaterializedScheduledTimescale(
              template, phase, wipeUtcTicks, out var profile))
        {
          PrintWarning(string.Format(
            WIPE_TEMPLATE_INVALID_PHASE_WARNING_FORMAT, template.Name));
          return;
        }

        if (HasScheduledTimescaleOverlapExceptGenerated(profile, out _))
        {
          PrintWarning(string.Format(
            WIPE_TEMPLATE_OVERLAP_WARNING_FORMAT, template.Name));
          return;
        }

        foreach (var other in generated)
        {
          if (!(profile.StartUtcTicks >= other.EndUtcTicks ||
                other.StartUtcTicks >= profile.EndUtcTicks))
          {
            PrintWarning(string.Format(
              WIPE_TEMPLATE_PHASE_OVERLAP_WARNING_FORMAT, template.Name));
            return;
          }
        }

        generated.Add(profile);
      }

      RemoveGeneratedWipeTemplateProfiles();
      _scheduledTimescales.AddRange(generated);
      _wipeTemplatesData.LastMaterializedWipeUtcTicks = wipeUtcTicks;
      _wipeTemplatesData.LastMaterializedTemplateID = template.ID;
      _wipeTemplatesData.GeneratedProfileIDs.Clear();
      foreach (var profile in generated)
        _wipeTemplatesData.GeneratedProfileIDs.Add(profile.ID);
      if (queuedID != System.Guid.Empty)
        _wipeTemplatesData.QueuedNextWipeTemplateID = System.Guid.Empty;

      NormalizeScheduledTimescales();
      SaveScheduledTimescales();
      SaveWipeTemplates();
      CacheAllPlayerScale();
      Puts(string.Format(WIPE_TEMPLATE_APPLIED_MESSAGE_FORMAT,
        template.Name, generated.Count));
    }

    private bool HasScheduledTimescaleOverlapExceptGenerated(
      ScheduledTimescale candidate, out string error)
    {
      foreach (var profile in _scheduledTimescales)
      {
        if (profile is null ||
            _wipeTemplatesData.GeneratedProfileIDs.Contains(profile.ID) ||
            profile.ID == candidate.ID ||
            candidate.StartUtcTicks >= profile.EndUtcTicks ||
            profile.StartUtcTicks >= candidate.EndUtcTicks)
          continue;

        error = OVERLAP_MESSAGE;
        return true;
      }

      error = null;
      return false;
    }

    private void RemoveGeneratedWipeTemplateProfiles()
    {
      for (var i = _scheduledTimescales.Count - 1; i >= 0; i--)
      {
        if (_wipeTemplatesData.GeneratedProfileIDs.Contains(
              _scheduledTimescales[i]?.ID ?? System.Guid.Empty))
          _scheduledTimescales.RemoveAt(i);
      }
    }

    private bool TryCreateMaterializedScheduledTimescale(
      WipeTemplate template, WipeTemplatePhase phase, long wipeUtcTicks,
      out ScheduledTimescale profile)
    {
      profile = null;
      try
      {
        var startUtcTicks = checked(wipeUtcTicks + phase.StartOffsetTicks);
        var endUtcTicks = checked(wipeUtcTicks + phase.EndOffsetTicks);
        profile = new()
        {
          ID = System.Guid.NewGuid(),
          Name = string.IsNullOrWhiteSpace(phase.Name) ? template.Name :
            string.Format(WIPE_TEMPLATE_PROFILE_NAME_FORMAT,
              template.Name, phase.Name),
          StartUtcTicks = startUtcTicks,
          EndUtcTicks = endUtcTicks,
          AbsoluteTimeScale = new(phase.AbsoluteTimeScale),
          OfflineTimeScale = new(phase.OfflineTimeScale)
        };
        return GetScheduledTimescaleInvalidReason(profile) is null;
      }
      catch (System.OverflowException ex)
      {
        PrintError($"Failed to materialize wipe template scheduled timescale: {ex}");
        return false;
      }
    }

    private WipeTemplatePhase GetActiveWipeTemplatePhase(
      WipeTemplate template, long nowUtcTicks)
    {
      if (template is null ||
          _wipeTemplatesData.LastMaterializedTemplateID != template.ID ||
          _wipeTemplatesData.LastMaterializedWipeUtcTicks <= 0L)
        return null;

      foreach (var phase in template.Phases)
      {
        if (!IsValidWipeTemplatePhase(phase))
          continue;

        try
        {
          var startTicks = checked(
            _wipeTemplatesData.LastMaterializedWipeUtcTicks +
            phase.StartOffsetTicks);
          var endTicks = checked(
            _wipeTemplatesData.LastMaterializedWipeUtcTicks +
            phase.EndOffsetTicks);
          if (nowUtcTicks >= startTicks && nowUtcTicks < endTicks)
            return phase;
        }
        catch (System.OverflowException ex)
        {
          PrintError($"Failed to evaluate wipe template phase: {ex}");
        }
      }

      return null;
    }

    private TimeScaleSet ResolveTimeScaleSet(
      long nowUtcTicks, out long boundaryTicks)
    {
      var profileCount = _scheduledTimescalesByStartTime.Count;
      if (!Configuration.RaidProtection.EnableScheduledTimescales ||
          profileCount is 0)
      {
        boundaryTicks = 0L;
        return _defaultTimeScales;
      }

      var low = 0;
      var high = profileCount - 1;
      var latestStartedIndex = -1;

      while (low <= high)
      {
        var middle = low + ((high - low) >> 1);
        if (_scheduledTimescalesByStartTime[middle].StartUtcTicks <= nowUtcTicks)
        {
          latestStartedIndex = middle;
          low = middle + 1;
        }
        else
          high = middle - 1;
      }

      if (latestStartedIndex >= 0)
      {
        var active = _scheduledTimescalesByStartTime[latestStartedIndex];
        if (nowUtcTicks < active.EndUtcTicks)
        {
          boundaryTicks = active.EndUtcTicks;
          return active.CachedTimeScales;
        }
      }

      var next = latestStartedIndex + 1;
      boundaryTicks = next < profileCount ? _scheduledTimescalesByStartTime[next].StartUtcTicks : 0L;
      return _defaultTimeScales;
    }

#endregion Runtime

#region Commands

    private void cmdScheduledTimescales(BasePlayer player, string _command, string[] args)
    {
      if (!player)
        return;

#if !CARBON
        if (!CheckChatCmdPerm(player, Configuration.Permission.Admin))
          return;
#endif

      var argCount = args?.Length ?? 0;
      switch (argCount)
      {
        case > 1:
          ChatMessage(player,
            $"Usage: /{Configuration.Command.CommandScheduledTimescales} <true|false>");
          return;
        case 1:
          {
            if (!bool.TryParse(args?[0], out var enabled))
            {
              ChatMessage(player,
                $"Usage: /{Configuration.Command.CommandScheduledTimescales} <true|false>");
              return;
            }

            SetScheduledTimescalesState(player, enabled);
            return;
          }
        default:
          OpenScheduledTimescaleEditor(player);
          break;
      }
    }

    private void SetScheduledTimescalesState(BasePlayer player, bool enabled)
    {
      Configuration.RaidProtection.EnableScheduledTimescales = enabled;
      SaveConfig();

      if (enabled)
      {
        InitializeScheduledTimescales();
        ChatMessage(player, "Scheduled timescales have been enabled.");
      }
      else
      {
        UnloadScheduledTimescales();
        CacheDefaultTimescales();
        ChatMessage(player, "Scheduled timescales have been disabled.");
      }

      CacheAllPlayerScale();
      RefreshAllProtectionViews();
    }

    private void OpenScheduledTimescaleEditor(BasePlayer player)
    {
      if (!Configuration.RaidProtection.EnableScheduledTimescales)
      {
#if CARBON
        ChatMessage(player,
          $"Scheduled timescales are disabled in the configuration. Use /{Configuration.Command.CommandScheduledTimescales} true to enable them.");
#else
            SendScheduledTimescaleInfo(player);
#endif
        return;
      }

#if CARBON && !MINIMAL
      if (!HasScheduledTimescaleEditorModules())
      {
        ChatMessage(player, "The Carbon scheduled timescale editor modules are unavailable.");
        return;
      }

      OpenScheduledTimescaleAdminPanel(player);
#else
        SendScheduledTimescaleInfo(player);
#endif
    }

    private void SendScheduledTimescaleInfo(BasePlayer player)
    {
      var nowUtcTicks = System.DateTime.UtcNow.Ticks;
      var enabled = Configuration.RaidProtection.EnableScheduledTimescales;
      var active = enabled && !ReferenceEquals(
        ResolveTimeScaleSet(nowUtcTicks, out _), _defaultTimeScales);

      _sb.Clear();
      _sb.AppendLine($"<color={COLOR_BLUE}>Scheduled Timescales</color> {(enabled ? $"<color={COLOR_GREEN}>ENABLED</color>" : $"<color={COLOR_RED}>DISABLED</color>")}")
        .Append(active ? "A profile is active." : "No profile is active.")
        .Append(" Loaded profiles: ").Append(_scheduledTimescales.Count)
        .AppendLine();

      if (_scheduledTimescales.Count > 0)
      {
        _sb.AppendLine();
        foreach (var profile in _scheduledTimescales)
        {
          if (profile is null)
            continue;

          var status = GetScheduledTimescaleCommandStatus(profile, nowUtcTicks);
          _sb.AppendLine($"<color={COLOR_YELLOW}>■ {profile.Name ?? profile.IDText}</color>")
            .AppendLine($"   <color={COLOR_AQUA}>{FormatScheduledTimescaleUtc(profile.StartUtcTicks)}</color> to " +
              $"<color={COLOR_AQUA}>{FormatScheduledTimescaleUtc(profile.EndUtcTicks)}</color>")
            .AppendLine($"   Status: <color={GetScheduledTimescaleCommandStatusColor(profile, nowUtcTicks)}>{status}</color>");
        }
      }

      var materializedTemplate = GetWipeTemplate(
        _wipeTemplatesData.LastMaterializedTemplateID);
      var activePhase = GetActiveWipeTemplatePhase(materializedTemplate,
        nowUtcTicks);
      var queuedTemplate = GetWipeTemplate(
        _wipeTemplatesData.QueuedNextWipeTemplateID);
      var defaultTemplate = GetWipeTemplate(
        _wipeTemplatesData.DefaultTemplateID);

      if (materializedTemplate is not null || queuedTemplate is not null ||
          defaultTemplate is not null)
        _sb.AppendLine()
          .AppendLine("<color=" + COLOR_BLUE + ">" +
            UI_TEMPLATE_TAB_NAME + "</color>");

      if (materializedTemplate is not null)
      {
        _sb.AppendLine($"<color={COLOR_YELLOW}>■ {string.Format(
          UI_ACTIVE_TEMPLATE_FORMAT, materializedTemplate.Name ??
          materializedTemplate.ID.ToString("N"))}</color>");
        _sb.AppendLine(activePhase is null ?
          $"   MATERIALIZED {FormatScheduledTimescaleUtc(_wipeTemplatesData.LastMaterializedWipeUtcTicks)}" :
          $"   {string.Format(UI_ACTIVE_PHASE_FORMAT, activePhase.Name ?? FALLBACK_TEMPLATE_PHASE_NAME)}");
      }
      if (queuedTemplate is not null)
        _sb.AppendLine($"<color={COLOR_YELLOW}>■ Queued (Next Wipe):</color> " +
          (queuedTemplate.Name ?? queuedTemplate.ID.ToString("N")));
      if (defaultTemplate is not null)
        _sb.AppendLine($"<color={COLOR_YELLOW}>■ Default:</color> " +
          (defaultTemplate.Name ?? defaultTemplate.ID.ToString("N")));

      ChatMessage(player, _sb.ToString());
    }

    private static string FormatScheduledTimescaleUtc(long utcTicks) =>
      utcTicks <= 0L || utcTicks > System.DateTime.MaxValue.Ticks
        ? INVALID_DATE_TEXT
        : new System.DateTime(utcTicks, System.DateTimeKind.Utc).ToString(
          $"{DATE_TIME_FORMAT} 'UTC'", CultureInfo.InvariantCulture);

    private static string GetScheduledTimescaleCommandStatus(
      ScheduledTimescale profile, long nowUtcTicks)
    {
      if (!string.IsNullOrEmpty(profile.InvalidReason))
        return $"INVALID ({profile.InvalidReason})";

      return
        nowUtcTicks < profile.StartUtcTicks ?
          "UPCOMING" :
        nowUtcTicks < profile.EndUtcTicks ?
          "ACTIVE" :
          "EXPIRED";
    }

    private static string GetScheduledTimescaleCommandStatusColor(
      ScheduledTimescale profile, long nowUtcTicks)
    {
      if (!string.IsNullOrEmpty(profile.InvalidReason))
        return COLOR_RED;

      return
        nowUtcTicks < profile.StartUtcTicks ?
          COLOR_ORANGE :
        nowUtcTicks < profile.EndUtcTicks ?
          COLOR_GREEN :
          COLOR_WHITE;
    }

#endregion Commands

#region Editor
#if CARBON && !MINIMAL

#region Constants

    private const string
      ADMIN_TAB_ID = "orp-schedules",
      WIPE_TEMPLATE_ADMIN_TAB_ID = "orp-wipe-templates",
      FIELD_NAME = "name",
      FIELD_START_DATE = "start-date",
      FIELD_START_TIME = "start-time",
      FIELD_END_DATE = "end-date",
      FIELD_END_TIME = "end-time",
      FIELD_ENTRY_KEY = "key",
      FIELD_ENTRY_SCALE = "scale";

#endregion Constants

#region Types

    private enum ScheduledTimescaleEntryKind : byte
    {
      Absolute,
      Offline
    }

    private enum ScheduledTimescaleMoveOffset : byte
    {
      PreviousDay,
      NextDay,
      PreviousWeek,
      NextWeek,
      PreviousMonth,
      NextMonth
    }

#endregion Types

#region Admin Tabs

#region Schedules Tab

    private void RegisterScheduledTimescaleAdminTab()
    {
      if (_admin is null || _scheduledTimescaleTab is not null)
        return;

      _scheduledTimescaleTab = new ScheduledTimescaleAdminTab(
        this,
        (session, tab) =>
        {
          // COLUMN 0: Profile List (Left) - fixed two-column grid anchor
          tab.AddColumn(PROFILE_COLUMN, true);
          // COLUMN 1: Details View (Right) - fixed two-column grid anchor
          tab.AddColumn(DETAILS_COLUMN, true);

          if (!CanUseScheduledTimescaleEditor(session))
          {
            // COLUMN 0: Button; visible text UI_ACCESS_DENIED; action is disabled
            // because the current session lacks the scheduled-timescale permission
            tab.AddButton(PROFILE_COLUMN,
              UI_ACCESS_DENIED,
              null, static _ => AdminModule.Tab.OptionButton.Types.Important);
            return;
          }

          Community.Runtime.Core.NextFrame(() =>
          {
            if (session.Player &&
                HasScheduledTimescaleEditorModules())
              OpenScheduledTimescaleAdminPanel(session.Player);
          });
        });
      _admin.RegisterTab(_scheduledTimescaleTab);
    }

    private bool HasScheduledTimescaleEditorModules() =>
      _admin is not null &&
      _dp is not null &&
      _scheduledTimescaleTab is not null &&
      _admin.HasTab(ADMIN_TAB_ID);

    private bool CanUseScheduledTimescaleEditor(
      AdminModule.PlayerSession session) =>
      session is not null &&
      session.Player &&
      _adminIDCache.Contains(session.Player.userID.Get());

    private bool CanEditWipeTemplate(
      AdminModule.PlayerSession session, WipeTemplate template) =>
      CanUseScheduledTimescaleEditor(session) &&
      template is not null &&
      ReferenceEquals(GetWipeTemplate(template.ID), template);

    private void OpenScheduledTimescaleAdminPanel(BasePlayer player)
    {
      if (!HasScheduledTimescaleEditorModules())
        return;

      ResetScheduledTimescaleEditors(player);
      if (!_scheduledTimescalePlayerTabs.TryGetValue(
            player.userID, out var playerTab))
      {
        playerTab = new ScheduledTimescaleAdminTab(this);
        _scheduledTimescalePlayerTabs[player.userID] = playerTab;
      }

      var session = _admin.GetPlayerSession(player);
      DrawScheduledTimescaleAdminTab(playerTab, session);
      var previous = _admin.GetTab(player);
      _admin.SetTab(player, playerTab, false);
      if (ReferenceEquals(
            previous, playerTab))
        _admin.Draw(player);
    }

    private void DrawScheduledTimescaleAdminTab(
      AdminModule.Tab tab, AdminModule.PlayerSession session)
    {
      // Rebuild this player's native two-column tab before Carbon renders it
      tab.Dialog = null;
      // COLUMN 0: Profile List (Left); rebuilds the fixed grid column before rows
      tab.AddColumn(PROFILE_COLUMN, true);
      // COLUMN 1: Details View (Right); rebuilds the fixed grid column before rows
      tab.AddColumn(DETAILS_COLUMN, true);

      if (!CanUseScheduledTimescaleEditor(session))
      {
        // COLUMN 0: Button; visible text UI_ACCESS_DENIED; disabled permission notice
        tab.AddButton(PROFILE_COLUMN, UI_ACCESS_DENIED,
          null, static _ => AdminModule.Tab.OptionButton.Types.Important);

        return;
      }

      var uiState = GetScheduledTimescaleUiState(session.Player);
      var selectedProfile = GetSelectedScheduledTimescale(uiState);
      DrawScheduledTimescaleProfileColumn(tab, uiState, selectedProfile);

      switch (uiState.ProfileEditor, uiState.EntryEditor)
      {
        case (not null, _):
          DrawScheduledTimescaleProfileEditor(tab, session,
            uiState.ProfileEditor);
          return;
        case (_, not null):
          DrawScheduledTimescaleEntryEditor(tab, session,
            uiState.EntryEditor);
          return;
        default:
          DrawScheduledTimescaleDetails(
            tab, session, uiState, selectedProfile);
          return;
      }
    }

    private ScheduledTimescaleUiState GetScheduledTimescaleUiState(
      BasePlayer player)
    {
      if (!_scheduledTimescaleUiStates.TryGetValue(
            player.userID, out var state))
      {
        state = new();
        _scheduledTimescaleUiStates[player.userID] = state;
      }

      if (GetSelectedScheduledTimescale(state) is null)
        state.SelectedProfileRuntimeID = GetFirstScheduledTimescale()?.RuntimeID ??
          System.Guid.Empty;

      return state;
    }

    private ScheduledTimescale GetFirstScheduledTimescale()
    {
      foreach (var timescale in _scheduledTimescales)
      {
        if (timescale is not null)
          return timescale;
      }

      return null;
    }

    private ScheduledTimescale GetSelectedScheduledTimescale(
      ScheduledTimescaleUiState state)
    {
      if (state is null ||
          state.SelectedProfileRuntimeID == System.Guid.Empty)
        return null;

      return _scheduledTimescalesByRuntimeID.GetValueOrDefault(
        state.SelectedProfileRuntimeID);
    }

    private bool IsStoredScheduledTimescale(ScheduledTimescale profile) =>
      profile is not null &&
      _scheduledTimescalesByRuntimeID.TryGetValue(
        profile.RuntimeID, out var stored) &&
      ReferenceEquals(stored, profile);

    private void DrawScheduledTimescaleProfileColumn(
      AdminModule.Tab tab, ScheduledTimescaleUiState uiState,
      ScheduledTimescale selectedProfile)
    {
      // [VIEW: Main Overview] COLUMN 0: Profile List (Left). The profile header is
      // row 0; profile rows are dynamic and page at PROFILE_ROWS_PER_PAGE
      // PINNED COLUMN -1: Bottom Actions is reserved for create/cleanup actions
      // profile selection plus the pinned create & delete action
      // COLUMN 0: Header; visible text UI_PROFILES; labels the profile list
      var profileHeader = UI_PROFILES;
      if (Configuration.RaidProtection.EnableScheduledTimescales &&
          ReferenceEquals(
            ResolveTimeScaleSet(System.DateTime.UtcNow.Ticks, out _),
            _defaultTimeScales))
      {
        profileHeader += " - " +
          (Configuration.RaidProtection.AbsoluteTimeScale.Count > 0 ||
           Configuration.RaidProtection.DamageScale.Count > 0 ?
            FALLBACK_MESSAGE :
            EMPTY_FALLBACK_MESSAGE);
      }

      tab.AddName(PROFILE_COLUMN, profileHeader);

      foreach (var profile in _scheduledTimescales)
      {
        if (profile is null)
        {
          // COLUMN 0: Button; visible text UI_MISSING_PROFILE; reports a null
          // persisted row and has no callback
          tab.AddButton(PROFILE_COLUMN, UI_MISSING_PROFILE, null,
            static _ => AdminModule.Tab.OptionButton.Types.Important,
            TextAnchor.MiddleLeft);
          continue;
        }

        GetScheduledTimescaleStatus(
          profile, out var status, out var statusType);
        var selected = profile.RuntimeID == uiState.SelectedProfileRuntimeID;
        var profile1 = profile;
        System.Action<AdminModule.PlayerSession> select = current =>
        {
          if (!CanUseScheduledTimescaleEditor(current) ||
              !IsStoredScheduledTimescale(profile1))
            return;

          var currentState = GetScheduledTimescaleUiState(current.Player);
          if (HasUnsavedScheduledTimescaleScaleChanges(currentState) &&
              currentState.SelectedProfileRuntimeID != profile1.RuntimeID)
          {
            currentState.Notice = UI_DIRTY_CHANGES_MESSAGE;
            DrawScheduledTimescaleAdminTab(tab, current);
            return;
          }

          if (currentState.SelectedProfileRuntimeID == profile1.RuntimeID)
          {
            DrawScheduledTimescaleAdminTab(tab, current);
            return;
          }

          ResetScheduledTimescaleEditors(current.Player);
          currentState = GetScheduledTimescaleUiState(current.Player);
          currentState.SelectedProfileRuntimeID = profile1.RuntimeID;
          currentState.Notice = null;
          current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;
          DrawScheduledTimescaleAdminTab(tab, current);
        };

        // COLUMN 0: Action Group; visible text is status plus profile.Name;
        // clicking either button selects this profile and redraws the tab
        tab.AddButtonArray(PROFILE_COLUMN,
          new AdminModule.Tab.OptionButton(
            status, select, _ => statusType),
          new AdminModule.Tab.OptionButton(
            profile.Name, select,
            _ => selected ?
              AdminModule.Tab.OptionButton.Types.Warned :
              AdminModule.Tab.OptionButton.Types.None));
      }

      var addProfileButton = new AdminModule.Tab.OptionButton(UI_ADD_PROFILE,
        current =>
        {
          if (!CanUseScheduledTimescaleEditor(current))
            return;

          var state = GetScheduledTimescaleUiState(current.Player);
          if (HasUnsavedScheduledTimescaleScaleChanges(state))
          {
            state.Notice = UI_DIRTY_CHANGES_MESSAGE;
            DrawScheduledTimescaleAdminTab(tab, current);
            return;
          }

          ResetScheduledTimescaleEditors(current.Player);
          GetScheduledTimescaleUiState(current.Player).ProfileEditor =
            CreateScheduledTimescaleEditContext(null,
              current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);
          current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;
          DrawScheduledTimescaleAdminTab(tab, current);
        }, static _ => AdminModule.Tab.OptionButton.Types.Selected);

      var addAfterSelectedButton = selectedProfile is null ? null :
        new AdminModule.Tab.OptionButton(UI_ADD_AFTER_SELECTED, current =>
        {
          if (!CanUseScheduledTimescaleEditor(current) ||
              !IsStoredScheduledTimescale(selectedProfile))
            return;

          var state = GetScheduledTimescaleUiState(current.Player);
          if (HasUnsavedScheduledTimescaleScaleChanges(state))
          {
            state.Notice = UI_DIRTY_CHANGES_MESSAGE;
            DrawScheduledTimescaleAdminTab(tab, current);
            return;
          }

          ResetScheduledTimescaleEditors(current.Player);
          GetScheduledTimescaleUiState(current.Player).ProfileEditor =
            CreateScheduledTimescaleEditContext(selectedProfile.EndUtcTicks,
              current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);
          current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;
          DrawScheduledTimescaleAdminTab(tab, current);
        }, static _ => AdminModule.Tab.OptionButton.Types.Warned);

      var expiredCount = GetExpiredScheduledTimescaleCount();
      if (expiredCount > 0)
      {
        // PINNED COLUMN -1: Bottom Actions; Action Group; visible text is
        // UI_ADD_PROFILE plus UI_DELETE_EXPIRED_COUNT_FORMAT; add opens the
        // profile editor, while cleanup opens a confirmation modal dialog
        if (addAfterSelectedButton is null)
          tab.AddButtonArray(PINNED_PROFILE_COLUMN, addProfileButton,
            new AdminModule.Tab.OptionButton(
            string.Format(UI_DELETE_EXPIRED_COUNT_FORMAT, expiredCount),
            current =>
            {
              if (!CanUseScheduledTimescaleEditor(current))
                return;
              // PINNED COLUMN -1: Modal Dialog; visible text is the formatted
              // expired-profile confirmation; confirm executes cleanup
              tab.CreateDialog(
                string.Format(UI_DELETE_EXPIRED_FORMAT, expiredCount),
                confirm => DeleteExpiredScheduledTimescales(tab, confirm));
            }, static _ => AdminModule.Tab.OptionButton.Types.Important));
        else
          tab.AddButtonArray(PINNED_PROFILE_COLUMN, addProfileButton,
            addAfterSelectedButton,
            new AdminModule.Tab.OptionButton(
              string.Format(UI_DELETE_EXPIRED_COUNT_FORMAT, expiredCount),
              current =>
              {
                if (!CanUseScheduledTimescaleEditor(current))
                  return;
                tab.CreateDialog(
                  string.Format(UI_DELETE_EXPIRED_FORMAT, expiredCount),
                  confirm => DeleteExpiredScheduledTimescales(tab, confirm));
              }, static _ => AdminModule.Tab.OptionButton.Types.Important));
        return;
      }

      // PINNED COLUMN -1: Bottom Actions; Action Group; visible text UI_ADD_PROFILE;
      // clicking it creates a new profile editor after dirty-state checks
      if (addAfterSelectedButton is null)
        tab.AddButtonArray(PINNED_PROFILE_COLUMN, addProfileButton);
      else
        tab.AddButtonArray(PINNED_PROFILE_COLUMN, addProfileButton,
          addAfterSelectedButton);
    }

    private void DrawScheduledTimescaleDetails(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescaleUiState uiState, ScheduledTimescale profile)
    {
      // Detail column: selected profile metadata and its scale-entry list
      if (profile is null)
      {
        // COLUMN 1: Header; visible text UI_PROFILE; empty-state details view
        // when no profile is selected. No detail rows are emitted
        tab.AddName(DETAILS_COLUMN, UI_PROFILE);
        return;
      }

      var scaleDraft = GetScheduledTimescaleScaleDraft(uiState, profile);

      // COLUMN 1: Header; visible text profile.Name; identifies the selected profile.
      tab.AddName(DETAILS_COLUMN, profile.Name);

      // COLUMN 1: Action Group; visible text UI_EDIT and UI_DELETE; edit opens
      // the metadata editor, delete opens the profile confirmation modal dialog
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(
          UI_EDIT, current =>
          {
            if (!CanUseScheduledTimescaleEditor(current) ||
                !IsStoredScheduledTimescale(profile))
              return;

            var state = GetScheduledTimescaleUiState(current.Player);
            if (HasUnsavedScheduledTimescaleScaleChanges(state))
            {
              state.Notice = UI_DIRTY_CHANGES_MESSAGE;
              DrawScheduledTimescaleAdminTab(tab, current);
              return;
            }

            state.EntryEditor = null;
            state.ProfileEditor = CreateScheduledTimescaleEditContext(
              profile, current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);
            current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;
            DrawScheduledTimescaleAdminTab(tab, current);
          }, static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(
          UI_DELETE, current =>
          {
            if (!CanUseScheduledTimescaleEditor(current))
              return;

            var state = GetScheduledTimescaleUiState(current.Player);
            if (HasUnsavedScheduledTimescaleScaleChanges(state))
            {
              state.Notice = UI_DIRTY_CHANGES_MESSAGE;
              DrawScheduledTimescaleAdminTab(tab, current);
              return;
            }

            // COLUMN 1: Modal Dialog; visible text is the formatted profile
            // deletion confirmation; confirm executes DeleteScheduledTimescaleProfile
            tab.CreateDialog(
              string.Format(UI_DELETE_PROFILE_FORMAT, profile.Name),
              confirm => DeleteScheduledTimescaleProfile(
                tab, confirm, profile));
          }, static _ => AdminModule.Tab.OptionButton.Types.Important));

      tab.AddButton(DETAILS_COLUMN, UI_CREATE_TEMPLATE_FROM_HERE,
        current =>
        {
          if (!CanUseScheduledTimescaleEditor(current) ||
              !IsStoredScheduledTimescale(profile))
            return;

          var template = CreateWipeTemplateFromScheduledProfiles(
            profile.StartUtcTicks);
          if (template is null)
            return;

          GetWipeTemplateUiState(current.Player).SelectedTemplateID =
            template.ID;
          OpenWipeTemplateAdminPanel(current.Player);
        }, static _ => AdminModule.Tab.OptionButton.Types.Warned);


      // COLUMN 1: Action Group; each move has left/right previous/next actions.
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(UI_PREVIOUS,
          current => MoveScheduledTimescaleProfile(
            tab, current, profile, ScheduledTimescaleMoveOffset.PreviousDay),
          static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_MOVE_DAY,
          null, static _ => AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_NEXT,
          current => MoveScheduledTimescaleProfile(
            tab, current, profile, ScheduledTimescaleMoveOffset.NextDay),
          static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_PREVIOUS,
          current => MoveScheduledTimescaleProfile(
            tab, current, profile, ScheduledTimescaleMoveOffset.PreviousWeek),
          static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_MOVE_WEEK,
          null, static _ => AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_NEXT,
          current => MoveScheduledTimescaleProfile(
            tab, current, profile, ScheduledTimescaleMoveOffset.NextWeek),
          static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_PREVIOUS,
          current => MoveScheduledTimescaleProfile(
            tab, current, profile, ScheduledTimescaleMoveOffset.PreviousMonth),
          static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_MOVE_MONTH,
          null, static _ => AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_NEXT,
          current => MoveScheduledTimescaleProfile(
            tab, current, profile, ScheduledTimescaleMoveOffset.NextMonth),
          static _ => AdminModule.Tab.OptionButton.Types.Selected));


      // COLUMN 1: Header; visible text UI_SCHEDULE; fixed metadata section row
      tab.AddName(DETAILS_COLUMN, UI_SCHEDULE);

      // COLUMN 1: Input Field; visible text UI_START; read-only formatted start date
      tab.AddInput(DETAILS_COLUMN, UI_START,
        _ => FormatScheduledTimescaleDate(profile.StartUtcTicks));

      // COLUMN 1: Input Field; visible text UI_END; read-only formatted end date
      tab.AddInput(DETAILS_COLUMN, UI_END,
        _ => FormatScheduledTimescaleDate(profile.EndUtcTicks));

      // COLUMN 1: Input Field; visible text UI_TIME_ZONE; read-only configured zone
      tab.AddInput(DETAILS_COLUMN, UI_TIME_ZONE,
        _ => _timeZone.DisplayName);


      if (!string.IsNullOrEmpty(profile.InvalidReason))
      {
        // COLUMN 1: Button; visible text profile.InvalidReason; non-clickable
        // invalid-profile highlight emitted only for an invalid persisted profile
        tab.AddButton(DETAILS_COLUMN, profile.InvalidReason, null,
          static _ => AdminModule.Tab.OptionButton.Types.Important,
          TextAnchor.MiddleLeft);
      }

      if (!string.IsNullOrEmpty(uiState.Notice))
      {
        // COLUMN 1: Button; visible text uiState.Notice; non-clickable transient
        // notice, including the unsaved-changes warning
        tab.AddButton(DETAILS_COLUMN, uiState.Notice, null,
          static _ => AdminModule.Tab.OptionButton.Types.Important,
          TextAnchor.MiddleLeft);
      }

      // COLUMN 1: Header; visible text UI_DAMAGE_SCALE; scale-entry section row
      tab.AddName(DETAILS_COLUMN, UI_DAMAGE_SCALE);

      var addScaleButton = new AdminModule.Tab.OptionButton(UI_ADD_SCALE,
        current =>
        {
          if (!CanUseScheduledTimescaleEditor(current) ||
              !IsStoredScheduledTimescale(profile))
            return;

          var state = GetScheduledTimescaleUiState(current.Player);
          state.ProfileEditor = null;
          state.EntryEditor = CreateScheduledTimescaleEntryEditContext(
              GetOrCreateScheduledTimescaleScaleDraft(current.Player, profile),
              state.EntryKind, null,
              current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);

          current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;
          DrawScheduledTimescaleAdminTab(tab, current);
        }, static _ => AdminModule.Tab.OptionButton.Types.Selected);


      var replaceStandardValuesButton = new AdminModule.Tab.OptionButton(
        UI_REPLACE_STANDARD_VALUES,
        current => ReplaceScheduledTimescaleStandardValues(
          tab, current, profile, uiState.EntryKind),
        static _ => AdminModule.Tab.OptionButton.Types.Important);


      if (uiState.ScaleEditor?.IsDirty is true)
      {
        // COLUMN 1: Action Group; visible text UI_SAVE_SCALE_CHANGES, UI_ADD_SCALE,
        // and UI_REPLACE_STANDARD_VALUES; dynamic dirty state adds Save first
        tab.AddButtonArray(DETAILS_COLUMN,
          new AdminModule.Tab.OptionButton(UI_SAVE_SCALE_CHANGES,
            current => SaveScheduledTimescaleScaleChanges(tab, current),
            static _ => AdminModule.Tab.OptionButton.Types.Selected),
          addScaleButton, replaceStandardValuesButton);
      }
      else
      {
        // COLUMN 1: Action Group; visible text UI_ADD_SCALE and
        // UI_REPLACE_STANDARD_VALUES; fixed clean-state action row without Save
        tab.AddButtonArray(DETAILS_COLUMN,
          addScaleButton, replaceStandardValuesButton);
      }

      // COLUMN 1: Action Group; visible text UI_ABSOLUTE_TIME and UI_OFFLINE_TIME;
      // switches the dynamic entry table between absolute-hour and offline-duration rows
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(
          UI_ABSOLUTE_TIME, current =>
          {
            if (!CanUseScheduledTimescaleEditor(current))
              return;

            GetScheduledTimescaleUiState(current.Player).EntryKind =
              ScheduledTimescaleEntryKind.Absolute;

            current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;

            DrawScheduledTimescaleAdminTab(tab, current);
          }, _ => uiState.EntryKind is
            ScheduledTimescaleEntryKind.Absolute ?
              AdminModule.Tab.OptionButton.Types.Warned :
              AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(
          UI_OFFLINE_TIME, current =>
          {
            if (!CanUseScheduledTimescaleEditor(current))
              return;

            GetScheduledTimescaleUiState(current.Player).EntryKind =
              ScheduledTimescaleEntryKind.Offline;

            current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;

            DrawScheduledTimescaleAdminTab(tab, current);
          }, _ => uiState.EntryKind is
            ScheduledTimescaleEntryKind.Offline ?
              AdminModule.Tab.OptionButton.Types.Warned :
              AdminModule.Tab.OptionButton.Types.None));

      DrawScheduledTimescaleEntries(tab, profile, scaleDraft, uiState.EntryKind);
    }

    private void DrawScheduledTimescaleEntries(
      AdminModule.Tab tab, ScheduledTimescale profile,
      ScheduledTimescaleScaleDraft draft, ScheduledTimescaleEntryKind kind)
    {
      // COLUMN 1: Dynamic entry table. The header and rows follow the fixed
      // details layout; entry rows are data-dependent and page at
      // DETAILS_ROWS_PER_PAGE after DETAILS_FIXED_ROW_COUNT fixed rows
      var absolute = kind is ScheduledTimescaleEntryKind.Absolute;
      DrawScheduledTimescaleEntryHeader(tab,
        absolute ? UI_HOUR_LABEL : UI_OFFLINE_HOURS);

      if (absolute)
      {
        var values = draft?.AbsoluteTimeScale ?? profile.AbsoluteTimeScale;
        var keys = draft is not null ? draft.AbsoluteTimeScaleKeys :
          profile.CachedTimeScales.AbsoluteTimeScaleKeys;

        foreach (var key in keys)
        {
          var keyText = key.ToString(CultureInfo.InvariantCulture);

          AddScheduledTimescaleEntryRow(tab, profile, kind, keyText,
            string.Format(HOUR_TIME_FORMAT, keyText),
            FormatScheduledTimescaleFloat(values[key]));
        }
      }
      else
      {
        var values = draft?.OfflineTimeScale ?? profile.OfflineTimeScale;
        var keys = draft is not null ? draft.OfflineTimeKeys :
          profile.CachedTimeScales.DamageScaleKeys;

        foreach (var key in keys)
        {
          var keyText = FormatScheduledTimescaleFloat(key);

          AddScheduledTimescaleEntryRow(tab, profile, kind, keyText,
            keyText, FormatScheduledTimescaleFloat(values[key]));
        }
      }
    }

    private static void DrawScheduledTimescaleEntryHeader(
        AdminModule.Tab tab, string keyLabel)
    {
      // Since "HOUR" is shorter, it requires more padding than "OFFLINE HOURS"
      var padding = string.Equals(keyLabel, UI_HOUR_LABEL, System.StringComparison.OrdinalIgnoreCase) ? 37 : 29;

      // COLUMN 1: Header; visible text is keyLabel plus UI_SCALE; labels the
      // dynamic entry columns (absolute hour/offline duration and scale)
      tab.AddName(DETAILS_COLUMN, $"{keyLabel.PadRight(padding)}{UI_SCALE}");
    }

    private void AddScheduledTimescaleEntryRow(
      AdminModule.Tab tab, ScheduledTimescale profile,
      ScheduledTimescaleEntryKind kind,
      string key, string hourLabel, string scaleLabel)
    {
      // One native row per scale entry; both value cells open the same editor
      System.Action<AdminModule.PlayerSession> edit = current =>
      {
        if (!CanUseScheduledTimescaleEditor(current) ||
            !IsStoredScheduledTimescale(profile))
          return;

        var state = GetScheduledTimescaleUiState(current.Player);
        state.ProfileEditor = null;

        var draft = GetOrCreateScheduledTimescaleScaleDraft(
          current.Player, profile);

        state.EntryEditor = CreateScheduledTimescaleEntryEditContext(
            draft, kind, key,
            current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);

        current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;
        DrawScheduledTimescaleAdminTab(tab, current);
      };


      // COLUMN 1: Action Group; visible text is hourLabel, scaleLabel, UI_COPY,
      // and UI_DELETE; value cells open the entry editor, Copy duplicates the
      // entry in the draft, and Delete removes it from the draft
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(
        hourLabel,
        TextAnchor.MiddleLeft,
        edit),
        new AdminModule.Tab.OptionButton(
            scaleLabel,
            TextAnchor.MiddleLeft,
            edit),
        new AdminModule.Tab.OptionButton(
          UI_COPY, current =>
            CopyScheduledTimescaleEntry(tab, current, profile, kind, key),
          static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(
          UI_DELETE, current =>
          {
            if (!CanUseScheduledTimescaleEditor(current) ||
                !IsStoredScheduledTimescale(profile))
              return;

            var draft = GetOrCreateScheduledTimescaleScaleDraft(
              current.Player, profile);

            if (!RemoveScheduledTimescaleEntry(draft, kind, key))
              return;

            draft.RefreshKeys();

            var state = GetScheduledTimescaleUiState(current.Player);
            state.ScaleEditor.IsDirty = true;

            ClampScheduledTimescaleDetailsPage(
              current, draft, kind,
              !string.IsNullOrEmpty(profile.InvalidReason),
              state.Notice);

            DrawScheduledTimescaleAdminTab(tab, current);
          }, static _ => AdminModule.Tab.OptionButton.Types.Important));
    }

#endregion Shedules Tab

#region Templates Tab

    private void RegisterWipeTemplateAdminTab()
    {
      if (_admin is null || _wipeTemplateTab is not null)
        return;

      _wipeTemplateTab = new WipeTemplateAdminTab(this,
        (session, _) =>
        {
          if (!CanUseScheduledTimescaleEditor(session))
            return;

          Community.Runtime.Core.NextFrame(() =>
          {
            if (CanUseScheduledTimescaleEditor(session) &&
                HasWipeTemplateEditorModules())
              OpenWipeTemplateAdminPanel(session.Player);
          });
        });
      _admin.RegisterTab(_wipeTemplateTab);
    }

    private bool HasWipeTemplateEditorModules() =>
      _admin is not null &&
      _wipeTemplateTab is not null &&
      _admin.HasTab(WIPE_TEMPLATE_ADMIN_TAB_ID);

    private void OpenWipeTemplateAdminPanel(BasePlayer player)
    {
      if (!HasWipeTemplateEditorModules())
        return;

      var session = _admin.GetPlayerSession(player);
      if (!CanUseScheduledTimescaleEditor(session))
        return;

      if (!_wipeTemplatePlayerTabs.TryGetValue(player.userID, out var tab))
      {
        tab = new WipeTemplateAdminTab(this);
        _wipeTemplatePlayerTabs[player.userID] = tab;
      }

      DrawWipeTemplateAdminTab(tab, session);
      var previous = _admin.GetTab(player);
      _admin.SetTab(player, tab, false);
      if (ReferenceEquals(previous, tab))
        _admin.Draw(player);
    }

    private WipeTemplateUiState GetWipeTemplateUiState(BasePlayer player)
    {
      if (!_wipeTemplateUiStates.TryGetValue(player.userID, out var state))
      {
        state = new();
        _wipeTemplateUiStates[player.userID] = state;
      }

      if (GetWipeTemplate(state.SelectedTemplateID) is null)
      {
        state.SelectedTemplateID = _wipeTemplatesData.Templates.Count > 0 ?
          _wipeTemplatesData.Templates[0].ID : System.Guid.Empty;
      }

      return state;
    }

    private void DrawWipeTemplateAdminTab(
      AdminModule.Tab tab, AdminModule.PlayerSession session)
    {
      // [VIEW: Wipe Template Overview] COLUMN 0 lists reusable wipe-relative
      // templates; COLUMN 1 shows the selected template or its phase editor
      tab.Dialog = null;
      tab.AddColumn(PROFILE_COLUMN, true);
      tab.AddColumn(DETAILS_COLUMN, true);
      if (!CanUseScheduledTimescaleEditor(session))
      {
        tab.AddButton(PROFILE_COLUMN, UI_ACCESS_DENIED, null,
          static _ => AdminModule.Tab.OptionButton.Types.Important);
        return;
      }

      var state = GetWipeTemplateUiState(session.Player);
      var isTemplateEditing = state.PhaseEditor is not null;
      var nowUtcTicks = System.DateTime.UtcNow.Ticks;
      WipeTemplate activeTemplate = null;
      foreach (var template in _wipeTemplatesData.Templates)
      {
        if (GetActiveWipeTemplatePhase(template, nowUtcTicks) is null)
          continue;

        activeTemplate = template;
        break;
      }
      tab.AddName(PROFILE_COLUMN, activeTemplate is null ?
        UI_TEMPLATE_TAB_NAME : string.Format(UI_ACTIVE_TEMPLATE_FORMAT,
          activeTemplate.Name));
      foreach (var candidate in _wipeTemplatesData.Templates)
      {
        if (candidate is null)
          continue;

        var template = candidate;
        var activeTemplatePhase = GetActiveWipeTemplatePhase(template, nowUtcTicks);
        // COLUMN 0: Template row; selected templates are Warned, while a
        // template active at the current UTC time is highlighted as Selected
        System.Action<AdminModule.PlayerSession> select =
          isTemplateEditing ? null : current =>
          {
            if (!CanEditWipeTemplate(current, template))
              return;

            GetWipeTemplateUiState(current.Player).SelectedTemplateID = template.ID;
            DrawWipeTemplateAdminTab(tab, current);
          };
        tab.AddButton(PROFILE_COLUMN, template.Name, select, _ =>
          state.SelectedTemplateID == template.ID ?
            AdminModule.Tab.OptionButton.Types.Warned :
          activeTemplatePhase is not null ?
            AdminModule.Tab.OptionButton.Types.Selected :
            AdminModule.Tab.OptionButton.Types.None);
      }

      System.Action<AdminModule.PlayerSession> addTemplate =
        isTemplateEditing ? null : current =>
      {
        if (!CanUseScheduledTimescaleEditor(current))
          return;

        var template = new WipeTemplate
        {
          Name = DEFAULT_WIPE_TEMPLATE_NAME,
          Phases = new()
          {
            new()
            {
              Name = DEFAULT_WIPE_TEMPLATE_PHASE_NAME,
              StartOffsetTicks = 0L,
              EndOffsetTicks = DEFAULT_WIPE_TEMPLATE_DURATION_TICKS,
              AbsoluteTimeScale = new(Configuration.RaidProtection.AbsoluteTimeScale),
              OfflineTimeScale = new(Configuration.RaidProtection.DamageScale)
            }
          }
        };
        _wipeTemplatesData.Templates.Add(template);
        SaveWipeTemplates();
        GetWipeTemplateUiState(current.Player).SelectedTemplateID = template.ID;
        DrawWipeTemplateAdminTab(tab, current);
      };
      // PINNED COLUMN -1: Creates a new template initialized from current
      // standard scales and selects it after persistence
      tab.AddButton(PINNED_PROFILE_COLUMN, UI_ADD_TEMPLATE, addTemplate,
        static _ => AdminModule.Tab.OptionButton.Types.Selected);

      var selected = GetWipeTemplate(state.SelectedTemplateID);
      if (selected is null)
      {
        // COLUMN 1: Empty-state details view when no template is selected
        tab.AddName(DETAILS_COLUMN, UI_SELECT_OR_ADD_TEMPLATE);
        return;
      }

      if (state.PhaseEditor is not null)
      {
        // COLUMN 1: Phase editor replaces the template detail view while open
        DrawWipeTemplatePhaseEditor(tab, session, state.PhaseEditor);
        return;
      }

      // COLUMN 1: Header and editable template name
      tab.AddName(DETAILS_COLUMN, selected.Name);
      tab.AddInput(DETAILS_COLUMN, UI_NAME, _ => selected.Name,
        PROFILE_NAME_MAX_LENGTH, false, (current, args) =>
        {
          if (!CanEditWipeTemplate(current, selected))
            return;

          selected.Name = GetScheduledTimescaleInput(args)?.Trim();
          if (string.IsNullOrEmpty(selected.Name))
            selected.Name = FALLBACK_WIPE_TEMPLATE_NAME;
          SaveWipeTemplates();
          DrawWipeTemplateAdminTab(tab, current);
        });

      selected.Phases.Sort(CompareWipeTemplatePhases);
      var activePhase = GetActiveWipeTemplatePhase(selected, nowUtcTicks);

      foreach (var phase in selected.Phases)
      {
        if (phase is null)
          continue;

        var phase1 = phase;
        // COLUMN 1: Phase row; the active phase is highlighted and each row
        // opens the phase editor with its wipe-relative offsets
        tab.AddButton(DETAILS_COLUMN,
          string.Format(UI_TEMPLATE_PHASE_FORMAT,
            phase.Name ?? FALLBACK_TEMPLATE_PHASE_NAME,
            System.TimeSpan.FromTicks(phase.StartOffsetTicks),
            System.TimeSpan.FromTicks(phase.EndOffsetTicks)),
          current =>
          {
            if (!CanEditWipeTemplate(current, selected))
              return;

            GetWipeTemplateUiState(current.Player).PhaseEditor =
              CreateWipeTemplatePhaseEditContext(selected, phase1);
            DrawWipeTemplateAdminTab(tab, current);
          }, _ => ReferenceEquals(phase, activePhase) ?
            AdminModule.Tab.OptionButton.Types.Selected :
            AdminModule.Tab.OptionButton.Types.None);
      }

      var selectedID = selected.ID;
      // COLUMN 1: Independent persisted toggles for the default template and
      // the template consumed by the next wipe; selected styling reflects state
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(UI_SET_DEFAULT_TEMPLATE, current =>
          {
            if (!CanEditWipeTemplate(current, selected))
              return;

            _wipeTemplatesData.DefaultTemplateID =
              _wipeTemplatesData.DefaultTemplateID == selectedID ?
                System.Guid.Empty : selectedID;
            SaveWipeTemplates();
            DrawWipeTemplateAdminTab(tab, current);
          }, _ => _wipeTemplatesData.DefaultTemplateID == selectedID ?
            AdminModule.Tab.OptionButton.Types.Selected :
            AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_QUEUE_NEXT_WIPE, current =>
          {
            if (!CanEditWipeTemplate(current, selected))
              return;

            _wipeTemplatesData.QueuedNextWipeTemplateID =
              _wipeTemplatesData.QueuedNextWipeTemplateID == selectedID ?
                System.Guid.Empty : selectedID;
            SaveWipeTemplates();
            DrawWipeTemplateAdminTab(tab, current);
          }, _ => _wipeTemplatesData.QueuedNextWipeTemplateID == selectedID ?
            AdminModule.Tab.OptionButton.Types.Selected :
            AdminModule.Tab.OptionButton.Types.None));

      // COLUMN 1: Adds a phase after the current latest phase end offset
      tab.AddButton(DETAILS_COLUMN, UI_ADD_PHASE, current =>
      {
        if (!CanEditWipeTemplate(current, selected))
          return;

        var lastEndTicks = 0L;
        foreach (var phase in selected.Phases)
        {
          if (phase is not null && phase.EndOffsetTicks > lastEndTicks)
            lastEndTicks = phase.EndOffsetTicks;
        }

        if (lastEndTicks > System.DateTime.MaxValue.Ticks -
            DEFAULT_TEMPLATE_PHASE_DURATION_TICKS)
          return;
        var nextEndTicks = lastEndTicks +
          DEFAULT_TEMPLATE_PHASE_DURATION_TICKS;

        GetWipeTemplateUiState(current.Player).PhaseEditor =
          CreateWipeTemplatePhaseEditContext(selected, new()
          {
            Name = DEFAULT_TEMPLATE_PHASE_NAME,
            StartOffsetTicks = lastEndTicks,
            EndOffsetTicks = nextEndTicks,
            AbsoluteTimeScale = new(Configuration.RaidProtection.AbsoluteTimeScale),
            OfflineTimeScale = new(Configuration.RaidProtection.DamageScale)
          });
        DrawWipeTemplateAdminTab(tab, current);
      }, static _ => AdminModule.Tab.OptionButton.Types.Warned);

      // COLUMN 1: Deletes the selected template through a confirmation dialog
      // and clears either persisted selection that pointed to it
      tab.AddButton(DETAILS_COLUMN, UI_DELETE_TEMPLATE, current =>
      {
        if (!CanEditWipeTemplate(current, selected))
          return;

        tab.CreateDialog(string.Format(UI_DELETE_TEMPLATE_FORMAT,
          selected.Name), confirm =>
        {
          if (!CanEditWipeTemplate(confirm, selected))
            return;

          var template = GetWipeTemplate(selectedID);
          if (template is null)
            return;
          _wipeTemplatesData.Templates.Remove(template);
          if (_wipeTemplatesData.DefaultTemplateID == selectedID)
            _wipeTemplatesData.DefaultTemplateID = System.Guid.Empty;
          if (_wipeTemplatesData.QueuedNextWipeTemplateID == selectedID)
            _wipeTemplatesData.QueuedNextWipeTemplateID = System.Guid.Empty;
          SaveWipeTemplates();
          GetWipeTemplateUiState(confirm.Player).SelectedTemplateID =
            System.Guid.Empty;
          DrawWipeTemplateAdminTab(tab, confirm);
        });
      }, static _ => AdminModule.Tab.OptionButton.Types.Important);
    }

#endregion Templates Tab

#endregion Admin Tabs

#region Profile Editors

#region Schedules Editor

    // [VIEW: Profile Metadata Editor] COLUMN 1: Details View (Right) replaces
    // the overview while editing Name, Start/End dates
    // Detail-column replacement for creating or editing profile metadata
    private ScheduledTimescaleEditContext CreateScheduledTimescaleEditContext(
      ScheduledTimescale profile, int returnPage)
    {
      if (profile is null)
      {
        var localToday = System.TimeZoneInfo.ConvertTimeFromUtc(
          System.DateTime.UtcNow, _timeZone).Date;

        return new()
        {
          Draft = new()
          {
            ID = System.Guid.NewGuid(),
            Name = DEFAULT_PROFILE_NAME
          },
          StartDate = localToday,
          EndDate = localToday.AddDays(1),
          StartTime = MIDNIGHT_TIME,
          EndTime = MIDNIGHT_TIME,
          ReturnPage = returnPage
        };
      }

      var draft = CloneScheduledTimescale(profile);

      return new()
      {
        Profile = profile,
        Draft = draft,
        StartDate = GetScheduledTimescaleLocalDate(draft.StartUtcTicks),
        EndDate = GetScheduledTimescaleLocalDate(draft.EndUtcTicks),
        StartTime = GetScheduledTimescaleLocalTime(draft.StartUtcTicks),
        EndTime = GetScheduledTimescaleLocalTime(draft.EndUtcTicks),
        ReturnPage = returnPage
      };
    }

    private ScheduledTimescaleEditContext CreateScheduledTimescaleEditContext(
      long startUtcTicks, int returnPage)
    {
      var start = GetScheduledTimescaleLocalDateTime(startUtcTicks);
      var end = start.AddDays(1);
      return new()
      {
        Draft = new()
        {
          ID = System.Guid.NewGuid(),
          Name = DEFAULT_PROFILE_NAME
        },
        StartDate = start.Date,
        EndDate = end.Date,
        StartTime = start.ToString(TIME_FORMAT, CultureInfo.InvariantCulture),
        EndTime = end.ToString(TIME_FORMAT, CultureInfo.InvariantCulture),
        ReturnPage = returnPage
      };
    }

    private void DrawScheduledTimescaleProfileEditor(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescaleEditContext context)
    {
      if (context.Profile is not null &&
          !IsStoredScheduledTimescale(context.Profile))
      {
        GetScheduledTimescaleUiState(session.Player).ProfileEditor = null;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      // COLUMN 1: Header; visible text UI_NEW_PROFILE or UI_EDIT_PROFILE;
      // identifies whether metadata is being created or edited
      tab.AddName(DETAILS_COLUMN,
        context.Profile is null ? UI_NEW_PROFILE : UI_EDIT_PROFILE);


      // COLUMN 1: Input Field; visible text UI_NAME, or its invalid-field
      // prefixed form; input changes context.Draft.Name and redraw the editor
      tab.AddInput(DETAILS_COLUMN,
        context.InvalidField is FIELD_NAME ?
          UI_INVALID_FIELD_PREFIX + UI_NAME : UI_NAME,
        _ => context.Draft.Name, PROFILE_NAME_MAX_LENGTH, false,
        (current, args) =>
        {
          context.Draft.Name = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawScheduledTimescaleAdminTab(tab, current);
        });


      // COLUMN 1: DatePicker trigger; visible text UI_START_DATE plus UI_CHANGE;
      // the input displays context.StartDate and the button opens the start-date
      // modal date picker
      tab.AddInputButton(DETAILS_COLUMN,
        context.InvalidField is FIELD_START_DATE ?
          UI_INVALID_FIELD_PREFIX + UI_START_DATE :
          UI_START_DATE,
        DATE_BUTTON_PRIORITY,
        new AdminModule.Tab.OptionInput(
          null,
          _ => FormatScheduledTimescaleDateOnly(context.StartDate),
          0, true, null),
        new AdminModule.Tab.OptionButton(
          UI_CHANGE,
          current => OpenScheduledTimescaleDatePicker(
            tab, current, context, true),
          static _ => AdminModule.Tab.OptionButton.Types.Warned));


      // COLUMN 1: Input Field; visible text UI_START_TIME, or its invalid-field
      // prefixed form; input changes context.StartTime and redraws the editor
      tab.AddInput(DETAILS_COLUMN,
        context.InvalidField is FIELD_START_TIME ?
          UI_INVALID_FIELD_PREFIX + UI_START_TIME :
          UI_START_TIME,
        _ => context.StartTime, TIME_MAX_LENGTH, false,
        (current, args) =>
        {
          context.StartTime = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawScheduledTimescaleAdminTab(tab, current);
        });


      // COLUMN 1: DatePicker trigger; visible text UI_END_DATE plus UI_CHANGE;
      // the input displays context.EndDate and the button opens the end-date
      // modal date picker
      tab.AddInputButton(DETAILS_COLUMN,
        context.InvalidField is FIELD_END_DATE ?
          UI_INVALID_FIELD_PREFIX + UI_END_DATE :
          UI_END_DATE,
        DATE_BUTTON_PRIORITY,
        new AdminModule.Tab.OptionInput(
          null,
          _ => FormatScheduledTimescaleDateOnly(context.EndDate),
          0, true, null),
        new AdminModule.Tab.OptionButton(
          UI_CHANGE,
          current => OpenScheduledTimescaleDatePicker(
            tab, current, context, false),
          static _ => AdminModule.Tab.OptionButton.Types.Warned));


      // COLUMN 1: Input Field; visible text UI_END_TIME, or its invalid-field
      // prefixed form; input changes context.EndTime and redraws the editor
      tab.AddInput(DETAILS_COLUMN,
        context.InvalidField is FIELD_END_TIME ?
          UI_INVALID_FIELD_PREFIX + UI_END_TIME :
          UI_END_TIME,
        _ => context.EndTime, TIME_MAX_LENGTH, false,
        (current, args) =>
        {
          context.EndTime = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawScheduledTimescaleAdminTab(tab, current);
        });


      // COLUMN 1: Input Field; visible text UI_TIME_ZONE; read-only configured
      // TimeZone display used by the date conversion callbacks
      tab.AddInput(DETAILS_COLUMN, UI_TIME_ZONE,
        _ => _timeZone.DisplayName);


      if (!string.IsNullOrEmpty(context.Error))
      {
        // COLUMN 1: Button; visible text context.Error; non-clickable validation
        // highlight emitted only after a failed metadata save/validation attempt
        tab.AddButton(DETAILS_COLUMN, context.Error, null,
          static _ => AdminModule.Tab.OptionButton.Types.Important,
          TextAnchor.MiddleLeft);
      }


      // COLUMN 1: Action Group; visible text UI_CANCEL and UI_SAVE; Cancel returns
      // to the saved page, while Save executes ConfirmScheduledTimescaleProfile
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(
          UI_CANCEL, current =>
          {
            GetScheduledTimescaleUiState(current.Player).ProfileEditor = null;
            current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = context.ReturnPage;
            DrawScheduledTimescaleAdminTab(tab, current);
          }),
        new AdminModule.Tab.OptionButton(
          UI_SAVE, current =>
            ConfirmScheduledTimescaleProfile(tab, current, context),
          static _ => AdminModule.Tab.OptionButton.Types.Selected));
    }

    private void ConfirmScheduledTimescaleProfile(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescaleEditContext context)
    {
      if (!CanUseScheduledTimescaleEditor(session))
        return;

      ClearScheduledTimescaleValidation(context);
      if (!TryGetScheduledTimescaleUtc(
            context.StartDate, context.StartTime,
            out var startUtcTicks, out var error))
      {
        context.Error = error;
        context.InvalidField = FIELD_START_TIME;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }
      if (!TryGetScheduledTimescaleUtc(
            context.EndDate, context.EndTime,
            out var endUtcTicks, out error))
      {
        context.Error = error;
        context.InvalidField = FIELD_END_TIME;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      var draft = context.Draft;
      draft.Name = draft.Name?.Trim();
      draft.StartUtcTicks = startUtcTicks;
      draft.EndUtcTicks = endUtcTicks;

      var profile = context.Profile;
      if (profile is not null &&
          HasDuplicateScheduledTimescaleId(profile))
        draft.ID = System.Guid.NewGuid();

      var invalidReason = GetScheduledTimescaleInvalidReason(draft);
      if (invalidReason is not null ||
          HasScheduledTimescaleOverlap(draft, out invalidReason))
      {
        context.Error = invalidReason;
        context.InvalidField =
          invalidReason is NAME_REQUIRED_MESSAGE ?
            FIELD_NAME :
          invalidReason is DATE_RANGE_MESSAGE ?
            FIELD_END_DATE :
            null;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      if (profile is null)
      {
        profile = draft;
        _scheduledTimescales.Add(profile);
      }
      else
      {
        if (!IsStoredScheduledTimescale(profile))
        {
          GetScheduledTimescaleUiState(session.Player).ProfileEditor = null;
          DrawScheduledTimescaleAdminTab(tab, session);
          return;
        }
        profile.ID = draft.ID;
        profile.Name = draft.Name;
        profile.StartUtcTicks = draft.StartUtcTicks;
        profile.EndUtcTicks = draft.EndUtcTicks;
      }

      var state = GetScheduledTimescaleUiState(session.Player);
      state.ProfileEditor = null;
      SaveScheduledTimescaleChanges();
      state.SelectedProfileRuntimeID = profile.RuntimeID;
      session.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = context.ReturnPage;
      DrawScheduledTimescaleAdminTab(tab, session);
    }

    private void OpenScheduledTimescaleDatePicker(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescaleEditContext context, bool start)
    {
      if (!CanUseScheduledTimescaleEditor(session))
        return;

      var player = session.Player;
      if (!HasScheduledTimescaleEditorModules() ||
          !_scheduledTimescaleUiStates.TryGetValue(
            player.userID, out var state) ||
          !ReferenceEquals(state.ProfileEditor, context))
        return;

      // COLUMN 1: DatePicker trigger; the selected date updates StartDate or
      // EndDate, clears validation, and redraws the details view
      _dp.Open(player, date =>
      {
        if (!_scheduledTimescaleUiStates.TryGetValue(
              player.userID, out state) ||
            !ReferenceEquals(state.ProfileEditor, context))
          return;

        if (start)
          context.StartDate = date.Date;
        else
          context.EndDate = date.Date;

        ClearScheduledTimescaleValidation(context);
        DrawScheduledTimescaleAdminTab(tab, session);

        _admin.Draw(player);
      });
    }

    private static void ClearScheduledTimescaleValidation(
      ScheduledTimescaleEditContext context)
    {
      context.Error = null;
      context.InvalidField = null;
    }

#endregion Schedules Editor

#region Template Editor

    private static WipeTemplatePhaseEditContext
      CreateWipeTemplatePhaseEditContext(
        WipeTemplate template, WipeTemplatePhase phase)
    {
      var draft = CloneWipeTemplatePhase(phase);
      return new()
      {
        Template = template,
        StoredPhase = template.Phases.Contains(phase) ? phase : null,
        Phase = draft,
        ScaleDraft = new(draft.AbsoluteTimeScale, draft.OfflineTimeScale,
          false),
        IsDirty = !template.Phases.Contains(phase),
        Name = phase.Name,
        StartHours = (phase.StartOffsetTicks /
          (double)System.TimeSpan.TicksPerHour).ToString("R",
            CultureInfo.InvariantCulture),
        EndHours = (phase.EndOffsetTicks /
          (double)System.TimeSpan.TicksPerHour).ToString("R",
            CultureInfo.InvariantCulture)
      };
    }

    private void DrawWipeTemplatePhaseEditor(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      WipeTemplatePhaseEditContext context)
    {
      // COLUMN 1: Phase editor. Inputs hold a draft until Save validates and
      // persists it; an active entry editor takes over this column below
      if (context.EntryEditor is not null)
      {
        DrawWipeTemplatePhaseEntryEditor(tab, session, context);
        return;
      }

      tab.AddName(DETAILS_COLUMN, UI_EDIT_TEMPLATE_PHASE);
      tab.AddInput(DETAILS_COLUMN, UI_NAME, _ => context.Name,
        PROFILE_NAME_MAX_LENGTH, false, (current, args) =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          context.Name = GetScheduledTimescaleInput(args);
          context.IsDirty = true;
          context.Error = null;
          DrawWipeTemplateAdminTab(tab, current);
        });
      tab.AddInput(DETAILS_COLUMN, UI_START_OFFSET_HOURS, _ => context.StartHours,
        SCALE_VALUE_MAX_LENGTH, false, (current, args) =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          context.StartHours = GetScheduledTimescaleInput(args);
          context.IsDirty = true;
          context.Error = null;
          DrawWipeTemplateAdminTab(tab, current);
        });
      tab.AddInput(DETAILS_COLUMN, UI_END_OFFSET_HOURS, _ => context.EndHours,
        SCALE_VALUE_MAX_LENGTH, false, (current, args) =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          context.EndHours = GetScheduledTimescaleInput(args);
          context.IsDirty = true;
          context.Error = null;
          DrawWipeTemplateAdminTab(tab, current);
        });

      var cancelButton = new AdminModule.Tab.OptionButton(UI_CANCEL, current =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          GetWipeTemplateUiState(current.Player).PhaseEditor = null;
          DrawWipeTemplateAdminTab(tab, current);
        });
      var deleteButton = new AdminModule.Tab.OptionButton(UI_DELETE, current =>
          {
            if (!CanEditWipeTemplate(current, context.Template) ||
                context.StoredPhase is null ||
                !context.Template.Phases.Remove(context.StoredPhase))
              return;

            SaveWipeTemplates();
            GetWipeTemplateUiState(current.Player).PhaseEditor = null;
            DrawWipeTemplateAdminTab(tab, current);
          }, static _ => AdminModule.Tab.OptionButton.Types.Important);
      if (context.IsDirty)
        tab.AddButtonArray(DETAILS_COLUMN,
          new AdminModule.Tab.OptionButton(UI_SAVE,
            current => SaveWipeTemplatePhase(tab, current, context),
            static _ => AdminModule.Tab.OptionButton.Types.Selected),
          cancelButton, deleteButton);
      else
        tab.AddButtonArray(DETAILS_COLUMN, cancelButton, deleteButton);

      if (!string.IsNullOrEmpty(context.Error))
        tab.AddButton(DETAILS_COLUMN, context.Error, null,
          static _ => AdminModule.Tab.OptionButton.Types.Important);

      tab.AddName(DETAILS_COLUMN,
        string.Format(UI_TEMPLATE_SCALE_COUNT_FORMAT,
          context.Phase.AbsoluteTimeScale.Count,
          context.Phase.OfflineTimeScale.Count));

      // COLUMN 1: Scale actions; Add Scale edits the draft, while Replace
      // Standard Values copies the current configuration into that draft
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(UI_ADD_SCALE, current =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          context.EntryEditor = CreateScheduledTimescaleEntryEditContext(
            context.ScaleDraft, context.EntryKind, null,
            current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);
          DrawWipeTemplateAdminTab(tab, current);
        }, static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_REPLACE_STANDARD_VALUES, current =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          context.ScaleDraft.AbsoluteTimeScale.Clear();
          context.ScaleDraft.OfflineTimeScale.Clear();
          foreach (var (key, value) in Configuration.RaidProtection.AbsoluteTimeScale)
            context.ScaleDraft.AbsoluteTimeScale.Add(key, value);
          foreach (var (key, value) in Configuration.RaidProtection.DamageScale)
            context.ScaleDraft.OfflineTimeScale.Add(key, value);
          context.ScaleDraft.RefreshKeys();
          context.IsDirty = true;
          DrawWipeTemplateAdminTab(tab, current);
        }, static _ => AdminModule.Tab.OptionButton.Types.Important));

      DrawWipeTemplatePhaseScaleEditor(tab, session, context);
    }

    private void DrawWipeTemplatePhaseScaleEditor(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      WipeTemplatePhaseEditContext context)
    {
      // COLUMN 1: Scale kind selector and dynamic entry table for the phase
      // draft; absolute hours and offline durations remain separate views
      var kind = context.EntryKind;
      tab.AddName(DETAILS_COLUMN, string.Empty);
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(UI_ABSOLUTE_TIME, current =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          context.EntryKind = ScheduledTimescaleEntryKind.Absolute;
          DrawWipeTemplateAdminTab(tab, current);
        }, _ => kind is ScheduledTimescaleEntryKind.Absolute ?
          AdminModule.Tab.OptionButton.Types.Warned :
          AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_OFFLINE_TIME, current =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          context.EntryKind = ScheduledTimescaleEntryKind.Offline;
          DrawWipeTemplateAdminTab(tab, current);
        }, _ => kind is ScheduledTimescaleEntryKind.Offline ?
          AdminModule.Tab.OptionButton.Types.Warned :
          AdminModule.Tab.OptionButton.Types.None));

      var absolute = kind is ScheduledTimescaleEntryKind.Absolute;
      DrawScheduledTimescaleEntryHeader(tab,
        absolute ? UI_HOUR_LABEL : UI_OFFLINE_HOURS);
      if (absolute)
      {
        foreach (var key in context.ScaleDraft.AbsoluteTimeScaleKeys)
          AddWipeTemplatePhaseScaleRow(tab, session, context, key.ToString(
            CultureInfo.InvariantCulture),
            string.Format(HOUR_TIME_FORMAT, key),
            FormatScheduledTimescaleFloat(
              context.ScaleDraft.AbsoluteTimeScale[key]));
      }
      else
      {
        foreach (var key in context.ScaleDraft.OfflineTimeKeys)
        {
          var keyText = FormatScheduledTimescaleFloat(key);
          AddWipeTemplatePhaseScaleRow(tab, session, context, keyText,
            keyText, FormatScheduledTimescaleFloat(
              context.ScaleDraft.OfflineTimeScale[key]));
        }
      }
    }

    private void AddWipeTemplatePhaseScaleRow(AdminModule.Tab tab,
      AdminModule.PlayerSession session, WipeTemplatePhaseEditContext context,
      string key, string keyLabel, string scaleLabel)
    {
      // COLUMN 1: One row per draft scale entry; value cells edit, Copy
      // duplicates into the draft, and Delete removes from the draft
      var kind = context.EntryKind;
      System.Action<AdminModule.PlayerSession> edit = current =>
      {
        if (!CanEditWipeTemplate(current, context.Template))
          return;

        context.EntryEditor = CreateScheduledTimescaleEntryEditContext(
          context.ScaleDraft, kind, key,
          current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);
        DrawWipeTemplateAdminTab(tab, current);
      };
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(keyLabel,
          TextAnchor.MiddleLeft, edit),
        new AdminModule.Tab.OptionButton(scaleLabel,
          TextAnchor.MiddleLeft, edit),
        new AdminModule.Tab.OptionButton(UI_COPY, current =>
        {
          if (!CanEditWipeTemplate(current, context.Template) ||
              !TryCopyScheduledTimescaleEntry(
                context.ScaleDraft, kind, key))
            return;

          context.ScaleDraft.RefreshKeys();
          context.IsDirty = true;
          DrawWipeTemplateAdminTab(tab, current);
        }, static _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_DELETE, current =>
        {
          if (!CanEditWipeTemplate(current, context.Template))
            return;

          RemoveScheduledTimescaleEntry(context.ScaleDraft, kind, key);
          context.ScaleDraft.RefreshKeys();
          context.IsDirty = true;
          DrawWipeTemplateAdminTab(tab, current);
        }, static _ => AdminModule.Tab.OptionButton.Types.Important));
    }

    private static WipeTemplatePhase CloneWipeTemplatePhase(
      WipeTemplatePhase source) => new()
      {
        Name = source.Name,
        StartOffsetTicks = source.StartOffsetTicks,
        EndOffsetTicks = source.EndOffsetTicks,
        AbsoluteTimeScale = new(source.AbsoluteTimeScale),
        OfflineTimeScale = new(source.OfflineTimeScale)
      };

    private void SaveWipeTemplatePhase(AdminModule.Tab tab,
      AdminModule.PlayerSession session, WipeTemplatePhaseEditContext context)
    {
      if (!CanEditWipeTemplate(session, context.Template))
        return;

      if (!double.TryParse(context.StartHours, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var startHours) ||
          !double.TryParse(context.EndHours, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var endHours) ||
          double.IsNaN(startHours) || double.IsInfinity(startHours) ||
          double.IsNaN(endHours) || double.IsInfinity(endHours))
      {
        context.Error = TEMPLATE_OFFSET_FINITE_MESSAGE;
        DrawWipeTemplateAdminTab(tab, session);
        return;
      }

      try
      {
        context.Phase.Name = context.Name?.Trim();
        context.Phase.StartOffsetTicks = checked((long)(startHours *
          System.TimeSpan.TicksPerHour));
        context.Phase.EndOffsetTicks = checked((long)(endHours *
          System.TimeSpan.TicksPerHour));
      }
      catch (System.OverflowException)
      {
        context.Error = TEMPLATE_OFFSET_RANGE_MESSAGE;
        DrawWipeTemplateAdminTab(tab, session);
        return;
      }

      if (!IsValidWipeTemplatePhase(context.Phase))
      {
        context.Error = TEMPLATE_OFFSET_ORDER_MESSAGE;
        DrawWipeTemplateAdminTab(tab, session);
        return;
      }

      if (HasWipeTemplatePhaseOverlap(
            context.Template, context.Phase, context.StoredPhase))
      {
        context.Error = TEMPLATE_PHASE_OVERLAP_MESSAGE;
        DrawWipeTemplateAdminTab(tab, session);
        return;
      }

      if (context.StoredPhase is null)
        context.Template.Phases.Add(context.Phase);
      else
      {
        context.StoredPhase.Name = context.Phase.Name;
        context.StoredPhase.StartOffsetTicks = context.Phase.StartOffsetTicks;
        context.StoredPhase.EndOffsetTicks = context.Phase.EndOffsetTicks;
        context.StoredPhase.AbsoluteTimeScale = context.Phase.AbsoluteTimeScale;
        context.StoredPhase.OfflineTimeScale = context.Phase.OfflineTimeScale;
      }
      NormalizeWipeTemplates();
      SaveWipeTemplates();
      GetWipeTemplateUiState(session.Player).PhaseEditor = null;
      DrawWipeTemplateAdminTab(tab, session);
    }

    private static bool HasWipeTemplatePhaseOverlap(
      WipeTemplate template, WipeTemplatePhase candidate,
      WipeTemplatePhase excluded)
    {
      foreach (var phase in template.Phases)
      {
        if (phase is null || ReferenceEquals(phase, excluded) ||
            candidate.StartOffsetTicks >= phase.EndOffsetTicks ||
            phase.StartOffsetTicks >= candidate.EndOffsetTicks)
          continue;

        return true;
      }

      return false;
    }

#endregion Template Editor

#endregion Profile Editors

#region Entry Editor

#region Schedule Entry Editor

    // [VIEW: Scale Entry Editor] COLUMN 1: Details View (Right) replaces the
    // overview for Absolute hour or Offline duration input and validation
    // Detail-column replacement for one absolute-time or offline-time entry
    private ScheduledTimescaleEntryEditContext
      CreateScheduledTimescaleEntryEditContext(
        ScheduledTimescaleScaleDraft draft,
        ScheduledTimescaleEntryKind kind,
        string existingKey, int returnPage)
    {
      var absolute = kind is ScheduledTimescaleEntryKind.Absolute;
      var existingValue = 0f;
      var hasExistingKey = false;
      var existingHour = 0;
      var existingOfflineHours = 0f;

      switch (absolute)
      {
        case true when
          int.TryParse(existingKey, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var absoluteHour):

          hasExistingKey = draft.AbsoluteTimeScale.TryGetValue(
            absoluteHour, out existingValue);
          existingHour = absoluteHour;
          break;

        case false when
          TryParseFiniteFloat(existingKey, out var offlineHours):

          hasExistingKey = draft.OfflineTimeScale.TryGetValue(
            offlineHours, out existingValue);
          existingOfflineHours = offlineHours;
          break;
      }

      return new()
      {
        Draft = draft,
        Kind = kind,
        HasExistingKey = hasExistingKey,
        ExistingAbsoluteHour = existingHour,
        ExistingOfflineHours = existingOfflineHours,
        Key = hasExistingKey ? existingKey : string.Empty,
        Scale = FormatScheduledTimescaleFloat(existingValue),
        ReturnPage = returnPage
      };
    }

    private void DrawScheduledTimescaleEntryEditor(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescaleEntryEditContext context)
    {
      var uiState = GetScheduledTimescaleUiState(session.Player);
      if (!IsScheduledTimescaleScaleDraft(session.Player, context.Draft) ||
          !IsStoredScheduledTimescale(uiState.ScaleEditor?.Profile))
      {
        uiState.EntryEditor = null;
        uiState.ScaleEditor = null;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      var absolute =
        context.Kind is ScheduledTimescaleEntryKind.Absolute;

      // COLUMN 1: Header; visible text is the new/edit absolute-scale or
      // offline-scale constant selected by context.Kind and key existence
      var scaleText = (context.HasExistingKey, absolute) switch
      {
        (false, false) => UI_NEW_OFFLINE_SCALE,
        (false, true ) => UI_NEW_ABSOLUTE_SCALE,
        (true,  false) => UI_EDIT_OFFLINE_SCALE,
        (true,  true ) => UI_EDIT_ABSOLUTE_SCALE
      };
      tab.AddName(DETAILS_COLUMN, scaleText);


      // COLUMN 1: Input Field; visible text UI_HOUR or UI_OFFLINE_HOURS, with an
      // invalid-field prefix when needed; input changes context.Key and redraws
      var hourText = (context.InvalidField is FIELD_ENTRY_KEY, absolute) switch
      {
        (false, false) => UI_OFFLINE_HOURS,
        (false, true ) => UI_HOUR,
        (true,  false) => UI_INVALID_FIELD_PREFIX + UI_OFFLINE_HOURS,
        (true,  true ) => UI_INVALID_FIELD_PREFIX + UI_HOUR
      };
      tab.AddInput(DETAILS_COLUMN, hourText,
        _ => context.Key, SCALE_VALUE_MAX_LENGTH, false,
        (current, args) =>
        {
          context.Key = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawScheduledTimescaleAdminTab(tab, current);
        });


      // COLUMN 1: Input Field; visible text UI_SCALE, with an invalid-field
      // prefix when needed; input changes context.Scale and redraws
      tab.AddInput(DETAILS_COLUMN,
        context.InvalidField is FIELD_ENTRY_SCALE ?
          UI_INVALID_FIELD_PREFIX + UI_SCALE : UI_SCALE,
        _ => context.Scale, SCALE_VALUE_MAX_LENGTH, false,
        (current, args) =>
        {
          context.Scale = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawScheduledTimescaleAdminTab(tab, current);
        });


      if (!string.IsNullOrEmpty(context.Error))
      {
        // COLUMN 1: Button; visible text context.Error; non-clickable validation
        // highlight for invalid hour/duration or scale input
        tab.AddButton(DETAILS_COLUMN, context.Error, null,
          static _ => AdminModule.Tab.OptionButton.Types.Important,
          TextAnchor.MiddleLeft);
      }


      // COLUMN 1: Action Group; visible text UI_CANCEL and UI_SAVE; Cancel exits
      // the entry editor, while Save executes ConfirmScheduledTimescaleEntry
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(
          UI_CANCEL, current =>
          {
            var state = GetScheduledTimescaleUiState(current.Player);
            state.EntryEditor = null;

            if (state.ScaleEditor?.IsDirty is not true)
              state.ScaleEditor = null;

            current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = context.ReturnPage;
            DrawScheduledTimescaleAdminTab(tab, current);
          }),
        new AdminModule.Tab.OptionButton(
          UI_SAVE, current =>
            ConfirmScheduledTimescaleEntry(tab, current, context),
          static _ => AdminModule.Tab.OptionButton.Types.Selected));
    }

    private void ConfirmScheduledTimescaleEntry(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescaleEntryEditContext context)
    {
      if (!CanUseScheduledTimescaleEditor(session))
        return;

      var uiState = GetScheduledTimescaleUiState(session.Player);
      if (!IsScheduledTimescaleScaleDraft(session.Player, context.Draft) ||
          !IsStoredScheduledTimescale(uiState.ScaleEditor?.Profile))
      {
        uiState.EntryEditor = null;
        uiState.ScaleEditor = null;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      ClearScheduledTimescaleValidation(context);
      if (!TryParseFiniteFloat(context.Scale, out var scale))
      {
        context.Error = DAMAGE_SCALE_MESSAGE;
        context.InvalidField = FIELD_ENTRY_SCALE;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      if (context.Kind is ScheduledTimescaleEntryKind.Absolute)
      {
        if (!int.TryParse(context.Key, NumberStyles.Integer,
              CultureInfo.InvariantCulture, out var hour) ||
            hour is < 0 or > MAX_HOUR)
        {
          context.Error = HOUR_MESSAGE;
          context.InvalidField = FIELD_ENTRY_KEY;
          DrawScheduledTimescaleAdminTab(tab, session);
          return;
        }

        if (scale is -1f)
        {
          context.Error = ABSOLUTE_SCALE_RESERVED_MESSAGE;
          context.InvalidField = FIELD_ENTRY_SCALE;
          DrawScheduledTimescaleAdminTab(tab, session);
          return;
        }

        if ((!context.HasExistingKey || context.ExistingAbsoluteHour != hour) &&
            context.Draft.AbsoluteTimeScale.ContainsKey(hour))
        {
          context.Error = ENTRY_KEY_EXISTS_MESSAGE;
          context.InvalidField = FIELD_ENTRY_KEY;
          DrawScheduledTimescaleAdminTab(tab, session);
          return;
        }

        if (context.HasExistingKey)
          context.Draft.AbsoluteTimeScale.Remove(context.ExistingAbsoluteHour);
        context.Draft.AbsoluteTimeScale[hour] = scale;
      }
      else
      {
        if (!TryParseFiniteFloat(context.Key, out var hours))
        {
          context.Error = OFFLINE_HOURS_MESSAGE;
          context.InvalidField = FIELD_ENTRY_KEY;
          DrawScheduledTimescaleAdminTab(tab, session);
          return;
        }

        if ((!context.HasExistingKey ||
             context.ExistingOfflineHours != hours) &&
            context.Draft.OfflineTimeScale.ContainsKey(hours))
        {
          context.Error = ENTRY_KEY_EXISTS_MESSAGE;
          context.InvalidField = FIELD_ENTRY_KEY;
          DrawScheduledTimescaleAdminTab(tab, session);
          return;
        }

        if (context.HasExistingKey)
          context.Draft.OfflineTimeScale.Remove(context.ExistingOfflineHours);
        context.Draft.OfflineTimeScale[hours] = scale;
      }

      context.Draft.RefreshKeys();
      uiState.EntryEditor = null;
      uiState.ScaleEditor?.IsDirty = true;
      session.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = context.ReturnPage;

      ClampScheduledTimescaleDetailsPage(
        session, context.Draft, context.Kind,
        !string.IsNullOrEmpty(uiState.ScaleEditor?.Profile?.InvalidReason),
        uiState.Notice);

      DrawScheduledTimescaleAdminTab(tab, session);
    }

    private static void ClearScheduledTimescaleValidation(
      ScheduledTimescaleEntryEditContext context)
    {
      context.Error = null;
      context.InvalidField = null;
    }

    private static bool HasUnsavedScheduledTimescaleScaleChanges(
      ScheduledTimescaleUiState uiState) => uiState?.ScaleEditor?.IsDirty is true;

    private static ScheduledTimescaleScaleDraft GetScheduledTimescaleScaleDraft(
      ScheduledTimescaleUiState uiState, ScheduledTimescale profile) =>
      uiState?.ScaleEditor is not null &&
      ReferenceEquals(uiState.ScaleEditor.Profile, profile) ?
        uiState.ScaleEditor.Draft : null;

    private ScheduledTimescaleScaleDraft GetOrCreateScheduledTimescaleScaleDraft(
      BasePlayer player, ScheduledTimescale profile)
    {
      var uiState = GetScheduledTimescaleUiState(player);
      uiState.ScaleEditor ??= new()
      {
        Profile = profile,
        Draft = new(profile)
      };

      return uiState.ScaleEditor.Draft;
    }

    private bool IsScheduledTimescaleScaleDraft(
      BasePlayer player, ScheduledTimescaleScaleDraft draft)
    {
      var uiState = GetScheduledTimescaleUiState(player);
      return uiState.ScaleEditor is not null &&
        ReferenceEquals(uiState.ScaleEditor.Draft, draft);
    }

    private void SaveScheduledTimescaleScaleChanges(
      AdminModule.Tab tab, AdminModule.PlayerSession session)
    {
      if (!CanUseScheduledTimescaleEditor(session))
        return;

      var uiState = GetScheduledTimescaleUiState(session.Player);
      if (uiState.ScaleEditor is null)
        return;

      var scaleEditor = uiState.ScaleEditor;
      if (!scaleEditor.IsDirty)
      {
        uiState.ScaleEditor = null;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      if (!IsStoredScheduledTimescale(scaleEditor.Profile))
      {
        uiState.EntryEditor = null;
        uiState.ScaleEditor = null;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      scaleEditor.Profile.AbsoluteTimeScale = scaleEditor.Draft.AbsoluteTimeScale;
      scaleEditor.Profile.OfflineTimeScale = scaleEditor.Draft.OfflineTimeScale;
      uiState.ScaleEditor = null;
      uiState.Notice = null;

      SaveScheduledTimescaleChanges();
      DrawScheduledTimescaleAdminTab(tab, session);
    }

    private void CopyScheduledTimescaleEntry(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescale profile, ScheduledTimescaleEntryKind kind,
      string key)
    {
      if (!CanUseScheduledTimescaleEditor(session))
        return;

      var uiState = GetScheduledTimescaleUiState(session.Player);
      if (!IsStoredScheduledTimescale(profile))
        return;

      var draft = GetOrCreateScheduledTimescaleScaleDraft(
        session.Player, profile);
      if (!TryCopyScheduledTimescaleEntry(draft, kind, key))
        return;

      draft.RefreshKeys();
      uiState.Notice = null;
      uiState.ScaleEditor.IsDirty = true;

      ClampScheduledTimescaleDetailsPage(session, draft, kind,
        !string.IsNullOrEmpty(profile.InvalidReason), uiState.Notice);
      DrawScheduledTimescaleAdminTab(tab, session);
    }

    private static bool TryCopyScheduledTimescaleEntry(
      ScheduledTimescaleScaleDraft profile,
      ScheduledTimescaleEntryKind kind,
      string key)
    {
      switch (kind)
      {
        case ScheduledTimescaleEntryKind.Absolute when
          int.TryParse(key, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var hour) &&
          profile.AbsoluteTimeScale.TryGetValue(hour, out var absoluteScale):
          {
            if (hour >= MAX_HOUR)
              return false;

            var copyHour = hour + 1;
            while (copyHour <= MAX_HOUR &&
                   profile.AbsoluteTimeScale.ContainsKey(copyHour))
              copyHour++;

            if (copyHour > MAX_HOUR)
              return false;

            profile.AbsoluteTimeScale.Add(copyHour, absoluteScale);
            return true;
          }
        case ScheduledTimescaleEntryKind.Offline when
          TryParseFiniteFloat(key, out var hours) &&
          profile.OfflineTimeScale.TryGetValue(hours, out var damageScale):
          {
            var copyHours = hours;
            for (var attempts = 0; attempts <= profile.OfflineTimeScale.Count;
                 attempts++)
            {
              var nextHours = copyHours + 1f;
              if (!IsFinite(nextHours) || nextHours == copyHours)
                return false;

              copyHours = nextHours;

              if (profile.OfflineTimeScale.TryAdd(copyHours, damageScale))
                return true;
            }
            break;
          }
        default:
          return false;
      }
      return false;
    }

    private void ReplaceScheduledTimescaleStandardValues(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescale profile, ScheduledTimescaleEntryKind kind)
    {
      if (!CanUseScheduledTimescaleEditor(session) ||
          !IsStoredScheduledTimescale(profile))
        return;

      var uiState = GetScheduledTimescaleUiState(session.Player);
      var current = GetScheduledTimescaleScaleDraft(uiState, profile);

      if (kind is ScheduledTimescaleEntryKind.Absolute)
      {
        if (Configuration.RaidProtection.AbsoluteTimeScale.Count is 0)
          return;

        if (Configuration.RaidProtection.AbsoluteTimeScale.ContainsValue(-1f))
        {
          uiState.Notice = ABSOLUTE_SCALE_RESERVED_MESSAGE;
          DrawScheduledTimescaleAdminTab(tab, session);
          return;
        }

        if (HasSameScheduledTimescaleValues(
              (current?.AbsoluteTimeScale ?? profile.AbsoluteTimeScale),
              Configuration.RaidProtection.AbsoluteTimeScale))
          return;

        var draft = GetOrCreateScheduledTimescaleScaleDraft(
          session.Player, profile);
        draft.AbsoluteTimeScale.Clear();

        foreach (var (key, value) in Configuration.RaidProtection.AbsoluteTimeScale)
          draft.AbsoluteTimeScale.Add(key, value);
      }
      else
      {
        if (Configuration.RaidProtection.DamageScale.Count is 0)
          return;

        if (HasSameScheduledTimescaleValues(
              (current?.OfflineTimeScale ?? profile.OfflineTimeScale),
              Configuration.RaidProtection.DamageScale))
          return;

        var draft = GetOrCreateScheduledTimescaleScaleDraft(
          session.Player, profile);
        draft.OfflineTimeScale.Clear();

        foreach (var (key, value) in Configuration.RaidProtection.DamageScale)
          draft.OfflineTimeScale.Add(key, value);
      }

      uiState.ScaleEditor.Draft.RefreshKeys();
      uiState.Notice = null;
      uiState.ScaleEditor.IsDirty = true;

      ClampScheduledTimescaleDetailsPage(session,
        uiState.ScaleEditor.Draft, kind,
        !string.IsNullOrEmpty(profile.InvalidReason), uiState.Notice);

      DrawScheduledTimescaleAdminTab(tab, session);
    }

    private static bool HasSameScheduledTimescaleValues<TKey>(
      Dictionary<TKey, float> current, Dictionary<TKey, float> standard)
    {
      if (current.Count != standard.Count)
        return false;

      foreach (var (key, value) in standard)
      {
        if (!current.TryGetValue(key, out var currentValue) ||
            currentValue != value)
          return false;
      }
      return true;
    }

    private static bool RemoveScheduledTimescaleEntry(
      ScheduledTimescaleScaleDraft profile,
      ScheduledTimescaleEntryKind kind,
      string key)
    {
      return kind switch
      {
        ScheduledTimescaleEntryKind.Absolute when int.TryParse(key, NumberStyles.Integer,
          CultureInfo.InvariantCulture, out var absoluteHour) => profile.AbsoluteTimeScale.Remove(absoluteHour),

        ScheduledTimescaleEntryKind.Offline when TryParseFiniteFloat(key, out var offlineHours) => profile
          .OfflineTimeScale.Remove(offlineHours),
        _ => false
      };
    }

#endregion Schedule Entry Editor

#region Template Entry Editor

    private void DrawWipeTemplatePhaseEntryEditor(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      WipeTemplatePhaseEditContext phaseContext)
    {
      var context = phaseContext.EntryEditor;
      var absolute = context.Kind is ScheduledTimescaleEntryKind.Absolute;
      var scaleText = (context.HasExistingKey, absolute) switch
      {
        (false, false) => UI_NEW_OFFLINE_SCALE,
        (false, true ) => UI_NEW_ABSOLUTE_SCALE,
        (true,  false) => UI_EDIT_OFFLINE_SCALE,
        (true,  true ) => UI_EDIT_ABSOLUTE_SCALE
      };
      tab.AddName(DETAILS_COLUMN, scaleText);
      var hoursText = (context.InvalidField is FIELD_ENTRY_KEY, absolute) switch
      {
        (false, false) => UI_OFFLINE_HOURS,
        (false, true ) => UI_HOUR,
        (true,  false) => UI_INVALID_FIELD_PREFIX + UI_OFFLINE_HOURS,
        (true,  true ) => UI_INVALID_FIELD_PREFIX + UI_HOUR
      };
      tab.AddInput(DETAILS_COLUMN, hoursText,
        _ => context.Key, SCALE_VALUE_MAX_LENGTH, false, (current, args) =>
        {
          if (!CanEditWipeTemplate(current, phaseContext.Template))
            return;

          context.Key = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawWipeTemplateAdminTab(tab, current);
        });
      tab.AddInput(DETAILS_COLUMN,
        context.InvalidField is FIELD_ENTRY_SCALE ?
          UI_INVALID_FIELD_PREFIX + UI_SCALE : UI_SCALE,
        _ => context.Scale, SCALE_VALUE_MAX_LENGTH, false, (current, args) =>
        {
          if (!CanEditWipeTemplate(current, phaseContext.Template))
            return;

          context.Scale = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawWipeTemplateAdminTab(tab, current);
        });
      if (!string.IsNullOrEmpty(context.Error))
        tab.AddButton(DETAILS_COLUMN, context.Error, null,
          static _ => AdminModule.Tab.OptionButton.Types.Important);
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(UI_CANCEL, current =>
        {
          if (!CanEditWipeTemplate(current, phaseContext.Template))
            return;

          phaseContext.EntryEditor = null;
          DrawWipeTemplateAdminTab(tab, current);
        }),
        new AdminModule.Tab.OptionButton(UI_SAVE, current =>
          SaveWipeTemplatePhaseEntry(tab, current, phaseContext),
          static _ => AdminModule.Tab.OptionButton.Types.Selected));
    }

    private void SaveWipeTemplatePhaseEntry(AdminModule.Tab tab,
      AdminModule.PlayerSession session,
      WipeTemplatePhaseEditContext phaseContext)
    {
      if (!CanEditWipeTemplate(session, phaseContext.Template))
        return;

      var context = phaseContext.EntryEditor;
      ClearScheduledTimescaleValidation(context);
      if (!TryApplyScheduledTimescaleEntry(context, out var error,
            out var invalidField))
      {
        context.Error = error;
        context.InvalidField = invalidField;
        DrawWipeTemplateAdminTab(tab, session);
        return;
      }

      phaseContext.EntryEditor = null;
      phaseContext.IsDirty = true;
      DrawWipeTemplateAdminTab(tab, session);
    }

    private static bool TryApplyScheduledTimescaleEntry(
      ScheduledTimescaleEntryEditContext context, out string error,
      out string invalidField)
    {
      error = null;
      invalidField = null;
      if (!TryParseFiniteFloat(context.Scale, out var scale))
      {
        error = DAMAGE_SCALE_MESSAGE;
        invalidField = FIELD_ENTRY_SCALE;
        return false;
      }

      if (context.Kind is ScheduledTimescaleEntryKind.Absolute)
      {
        if (!int.TryParse(context.Key, NumberStyles.Integer,
              CultureInfo.InvariantCulture, out var hour) ||
            hour is < 0 or > MAX_HOUR)
        {
          error = HOUR_MESSAGE;
          invalidField = FIELD_ENTRY_KEY;
          return false;
        }
        if (scale is -1f)
        {
          error = ABSOLUTE_SCALE_RESERVED_MESSAGE;
          invalidField = FIELD_ENTRY_SCALE;
          return false;
        }
        if ((!context.HasExistingKey || context.ExistingAbsoluteHour != hour) &&
            context.Draft.AbsoluteTimeScale.ContainsKey(hour))
        {
          error = ENTRY_KEY_EXISTS_MESSAGE;
          invalidField = FIELD_ENTRY_KEY;
          return false;
        }
        if (context.HasExistingKey)
          context.Draft.AbsoluteTimeScale.Remove(context.ExistingAbsoluteHour);
        context.Draft.AbsoluteTimeScale[hour] = scale;
      }
      else
      {
        if (!TryParseFiniteFloat(context.Key, out var hours))
        {
          error = OFFLINE_HOURS_MESSAGE;
          invalidField = FIELD_ENTRY_KEY;
          return false;
        }
        if ((!context.HasExistingKey || context.ExistingOfflineHours != hours) &&
            context.Draft.OfflineTimeScale.ContainsKey(hours))
        {
          error = ENTRY_KEY_EXISTS_MESSAGE;
          invalidField = FIELD_ENTRY_KEY;
          return false;
        }
        if (context.HasExistingKey)
          context.Draft.OfflineTimeScale.Remove(context.ExistingOfflineHours);
        context.Draft.OfflineTimeScale[hours] = scale;
      }

      context.Draft.RefreshKeys();
      return true;
    }

#endregion Timescale Entry Editor

#endregion Entry Editors

#region Helpers

    private WipeTemplate CreateWipeTemplateFromScheduledProfiles(
      long wipeUtcTicks)
    {
      var template = new WipeTemplate
      {
        Name = IMPORTED_WIPE_TEMPLATE_NAME
      };

      foreach (var profile in _scheduledTimescalesByStartTime)
      {
        if (profile.StartUtcTicks < wipeUtcTicks)
          continue;

        template.Phases.Add(new()
        {
          Name = profile.Name,
          StartOffsetTicks = profile.StartUtcTicks - wipeUtcTicks,
          EndOffsetTicks = profile.EndUtcTicks - wipeUtcTicks,
          AbsoluteTimeScale = new(profile.AbsoluteTimeScale),
          OfflineTimeScale = new(profile.OfflineTimeScale)
        });
      }

      if (template.Phases.Count is 0)
        return null;

      _wipeTemplatesData.Templates.Add(template);
      SaveWipeTemplates();
      return template;
    }

    private int GetExpiredScheduledTimescaleCount()
    {
      var nowUtcTicks = System.DateTime.UtcNow.Ticks;
      var count = 0;
      foreach (var profile in _scheduledTimescales)
      {
        if (profile is not null && profile.EndUtcTicks <= nowUtcTicks)
          count++;
      }
      return count;
    }

    private void DeleteExpiredScheduledTimescales(
      AdminModule.Tab tab, AdminModule.PlayerSession session)
    {
      if (!CanUseScheduledTimescaleEditor(session))
        return;

      var uiState = GetScheduledTimescaleUiState(session.Player);
      if (HasUnsavedScheduledTimescaleScaleChanges(uiState))
      {
        uiState.Notice = UI_DIRTY_CHANGES_MESSAGE;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      var nowUtcTicks = System.DateTime.UtcNow.Ticks;
      var removed = false;

      for (var i = _scheduledTimescales.Count - 1; i >= 0; i--)
      {
        var profile = _scheduledTimescales[i];
        if (profile is null || profile.EndUtcTicks > nowUtcTicks)
          continue;

        _scheduledTimescales.RemoveAt(i);
        removed = true;
      }

      if (!removed)
      {
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      ResetScheduledTimescaleEditors(session.Player);

      uiState = GetScheduledTimescaleUiState(session.Player);
      uiState.SelectedProfileRuntimeID = System.Guid.Empty;
      uiState.Notice = null;

      SaveScheduledTimescaleChanges();
      session.GetOrCreatePage(PROFILE_COLUMN).CurrentPage = FIRST_PAGE;
      session.GetOrCreatePage(DETAILS_COLUMN).CurrentPage = FIRST_PAGE;
      DrawScheduledTimescaleAdminTab(tab, session);
    }

    private void MoveScheduledTimescaleProfile(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescale profile, ScheduledTimescaleMoveOffset offset)
    {
      if (!CanUseScheduledTimescaleEditor(session) ||
          !IsStoredScheduledTimescale(profile))
        return;

      var uiState = GetScheduledTimescaleUiState(session.Player);
      if (HasUnsavedScheduledTimescaleScaleChanges(uiState))
      {
        uiState.Notice = UI_DIRTY_CHANGES_MESSAGE;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      uiState.Notice = null;
      if (!TryMoveScheduledTimescale(profile, offset, out var error))
      {
        uiState.Notice = error;
        DrawScheduledTimescaleAdminTab(tab, session);
        return;
      }

      SaveScheduledTimescaleChanges();
      DrawScheduledTimescaleAdminTab(tab, session);
    }

    private bool TryMoveScheduledTimescale(
      ScheduledTimescale profile, ScheduledTimescaleMoveOffset offset,
      out string error)
    {
      var moved = CloneScheduledTimescale(profile);
      try
      {
        var start = OffsetScheduledTimescaleLocalDate(
          GetScheduledTimescaleLocalDateTime(profile.StartUtcTicks), offset);
        var end = OffsetScheduledTimescaleLocalDate(
          GetScheduledTimescaleLocalDateTime(profile.EndUtcTicks), offset);

        if (!TryGetScheduledTimescaleUtc(start, out var startUtcTicks, out error) ||
            !TryGetScheduledTimescaleUtc(end, out var endUtcTicks, out error))
          return false;

        moved.StartUtcTicks = startUtcTicks;
        moved.EndUtcTicks = endUtcTicks;
      }
      catch (System.ArgumentOutOfRangeException)
      {
        error = DATE_RANGE_MESSAGE;
        return false;
      }

      error = GetScheduledTimescaleInvalidReason(moved);
      if (error is not null || HasScheduledTimescaleOverlap(moved, out error))
        return false;

      profile.StartUtcTicks = moved.StartUtcTicks;
      profile.EndUtcTicks = moved.EndUtcTicks;

      return true;
    }

    private static System.DateTime OffsetScheduledTimescaleLocalDate(
      System.DateTime value, ScheduledTimescaleMoveOffset offset) => offset switch
      {
        ScheduledTimescaleMoveOffset.PreviousDay => value.AddDays(-1),
        ScheduledTimescaleMoveOffset.NextDay => value.AddDays(1),
        ScheduledTimescaleMoveOffset.PreviousWeek => value.AddDays(-7),
        ScheduledTimescaleMoveOffset.NextWeek => value.AddDays(7),
        ScheduledTimescaleMoveOffset.PreviousMonth => value.AddMonths(-1),
        _ => value.AddMonths(1)
      };

    private void DeleteScheduledTimescaleProfile(
      AdminModule.Tab tab, AdminModule.PlayerSession session,
      ScheduledTimescale profile)
    {
      if (!CanUseScheduledTimescaleEditor(session) ||
          !IsStoredScheduledTimescale(profile))
        return;

      var index = _scheduledTimescales.IndexOf(profile);
      if (!_scheduledTimescales.Remove(profile))
        return;

      ResetScheduledTimescaleEditors(session.Player);
      SaveScheduledTimescaleChanges();

      var uiState = GetScheduledTimescaleUiState(session.Player);
      uiState.SelectedProfileRuntimeID = System.Guid.Empty;

      if (_scheduledTimescales.Count > 0)
      {
        index = System.Math.Min(
          System.Math.Max(0, index), _scheduledTimescales.Count - 1);
        for (var i = index; i < _scheduledTimescales.Count; i++)
        {
          if (_scheduledTimescales[i] is null)
            continue;

          uiState.SelectedProfileRuntimeID = _scheduledTimescales[i].RuntimeID;
          break;
        }

        if (uiState.SelectedProfileRuntimeID == System.Guid.Empty)
          uiState.SelectedProfileRuntimeID =
            GetFirstScheduledTimescale()?.RuntimeID ?? System.Guid.Empty;
      }

      // COLUMN 0: page sizing counts the profile header plus dynamic profile
      // rows; the pinned -1 action row is outside this calculation
      var profileRows = 1 + _scheduledTimescales.Count;
      var maxProfilePage = System.Math.Max(0,
        (profileRows - 1) / PROFILE_ROWS_PER_PAGE);
      var profilePage = session.GetOrCreatePage(PROFILE_COLUMN);

      profilePage.CurrentPage = System.Math.Max(FIRST_PAGE,
        System.Math.Min(profilePage.CurrentPage, maxProfilePage));

      DrawScheduledTimescaleAdminTab(tab, session);
    }

    private static void ClampScheduledTimescaleDetailsPage(
      AdminModule.PlayerSession session, ScheduledTimescale profile,
      ScheduledTimescaleEntryKind kind, string notice = null) =>
      ClampScheduledTimescaleDetailsPage(session,
        kind is ScheduledTimescaleEntryKind.Absolute ?
          profile.AbsoluteTimeScale.Count : profile.OfflineTimeScale.Count,
        !string.IsNullOrEmpty(profile.InvalidReason),
        !string.IsNullOrEmpty(notice));

    private static void ClampScheduledTimescaleDetailsPage(
      AdminModule.PlayerSession session, ScheduledTimescaleScaleDraft draft,
      ScheduledTimescaleEntryKind kind, bool hasInvalidReason = false,
      string notice = null) =>
      ClampScheduledTimescaleDetailsPage(session,
        kind is ScheduledTimescaleEntryKind.Absolute ?
          draft.AbsoluteTimeScale.Count : draft.OfflineTimeScale.Count,
        hasInvalidReason, !string.IsNullOrEmpty(notice));

    private static void ClampScheduledTimescaleDetailsPage(
      AdminModule.PlayerSession session, int entryCount,
      bool hasInvalidReason, bool hasNotice = false)
    {
      // COLUMN 1: page sizing counts fixed detail rows, the dynamic-entry
      // header, optional feedback rows, and dynamic entry rows
      var rowCount = DETAILS_FIXED_ROW_COUNT +
        DETAILS_ENTRY_HEADER_ROW_COUNT +
        (hasInvalidReason ? 1 : 0) +
        (hasNotice ? 1 : 0) + entryCount;
      var maxPage = System.Math.Max(0,
        (rowCount - 1) / DETAILS_ROWS_PER_PAGE);
      var page = session.GetOrCreatePage(DETAILS_COLUMN);

      page.CurrentPage = System.Math.Max(FIRST_PAGE,
        System.Math.Min(page.CurrentPage, maxPage));
    }

    private void SaveScheduledTimescaleChanges()
    {
      NormalizeScheduledTimescales();
      SaveScheduledTimescales();
      CacheAllPlayerScale();
    }

    private bool HasScheduledTimescaleOverlap(
      ScheduledTimescale candidate, out string error)
    {
      foreach (var profile in _scheduledTimescales)
      {
        if (profile is null)
          continue;

        if (profile.ID == candidate.ID)
          continue;

        if (candidate.StartUtcTicks >= profile.EndUtcTicks ||
            profile.StartUtcTicks >= candidate.EndUtcTicks)
          continue;

        error = OVERLAP_MESSAGE;
        return true;
      }

      error = null;
      return false;
    }

    private bool HasDuplicateScheduledTimescaleId(ScheduledTimescale profile)
    {
      foreach (var candidate in _scheduledTimescales)
      {
        if (!ReferenceEquals(candidate, profile) &&
            candidate is not null && candidate.ID == profile.ID)
          return true;
      }

      return false;
    }

    private ScheduledTimescale CloneScheduledTimescale(
      ScheduledTimescale source)
    {
      if (source is null)
      {
        return new()
        {
          ID = System.Guid.NewGuid(),
          Name = DEFAULT_PROFILE_NAME
        };
      }

      return new()
      {
        ID = source.ID,
        Name = source.Name,
        StartUtcTicks = source.StartUtcTicks,
        EndUtcTicks = source.EndUtcTicks,
        AbsoluteTimeScale = new(source.AbsoluteTimeScale),
        OfflineTimeScale = new(source.OfflineTimeScale)
      };
    }

    private System.DateTime GetScheduledTimescaleLocalDate(long utcTicks) =>
      utcTicks > 0L &&
      utcTicks <= System.DateTime.MaxValue.Ticks ?
        System.TimeZoneInfo.ConvertTimeFromUtc(
          new System.DateTime(utcTicks, System.DateTimeKind.Utc),
          _timeZone).Date :
        System.TimeZoneInfo.ConvertTimeFromUtc(
          System.DateTime.UtcNow, _timeZone).Date;

    private System.DateTime GetScheduledTimescaleLocalDateTime(long utcTicks) =>
      System.TimeZoneInfo.ConvertTimeFromUtc(
        new System.DateTime(utcTicks, System.DateTimeKind.Utc), _timeZone);

    private string GetScheduledTimescaleLocalTime(long utcTicks) =>
      utcTicks > 0L &&
      utcTicks <= System.DateTime.MaxValue.Ticks ?
        System.TimeZoneInfo.ConvertTimeFromUtc(
          new System.DateTime(utcTicks, System.DateTimeKind.Utc),
          _timeZone).ToString(TIME_FORMAT) :
        MIDNIGHT_TIME;

    private bool TryGetScheduledTimescaleUtc(
      System.DateTime date, string time,
      out long utcTicks, out string error)
    {
      utcTicks = 0L;
      error = null;

      if (!System.DateTime.TryParseExact(
            time, TIME_FORMAT, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsedTime))
      {
        error = TIME_FORMAT_MESSAGE;
        return false;
      }

      var local = System.DateTime.SpecifyKind(
        date.Date.Add(parsedTime.TimeOfDay),
        System.DateTimeKind.Unspecified);

      return TryGetScheduledTimescaleUtc(local, out utcTicks, out error);
    }

    private bool TryGetScheduledTimescaleUtc(
      System.DateTime local, out long utcTicks, out string error)
    {
      utcTicks = 0L;
      error = null;
      local = System.DateTime.SpecifyKind(
        local, System.DateTimeKind.Unspecified);

      if (_timeZone.IsInvalidTime(local) ||
          _timeZone.IsAmbiguousTime(local))
      {
        error = TIMEZONE_MESSAGE;
        return false;
      }

      utcTicks = System.TimeZoneInfo.ConvertTimeToUtc(
        local, _timeZone).Ticks;

      return true;
    }

    private static bool TryParseFiniteFloat(
      string value, out float result) =>
      float.TryParse(
        value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out result) &&
      !float.IsNaN(result) &&
      !float.IsInfinity(result);

    private static string FormatScheduledTimescaleFloat(float value) =>
      value.ToString("R", CultureInfo.InvariantCulture);

    private string FormatScheduledTimescaleDate(long utcTicks) =>
      utcTicks <= 0L ||
      utcTicks > System.DateTime.MaxValue.Ticks ?
        INVALID_DATE_TEXT :
        FormatScheduledTimescaleDate(
          System.TimeZoneInfo.ConvertTimeFromUtc(
            new System.DateTime(utcTicks, System.DateTimeKind.Utc),
            _timeZone));

    private static string FormatScheduledTimescaleDate(
      System.DateTime value) =>
      IsCurrentCultureAvailable() ?
        value.ToString("g", CultureInfo.CurrentCulture) :
        value.ToString(DATE_TIME_FORMAT, CultureInfo.InvariantCulture);

    private static string FormatScheduledTimescaleDateOnly(
      System.DateTime value) =>
      IsCurrentCultureAvailable() ?
        value.ToString("d", CultureInfo.CurrentCulture) :
        value.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);

    private static bool IsCurrentCultureAvailable() =>
      !CultureInfo.CurrentCulture.Equals(CultureInfo.InvariantCulture);

    private void GetScheduledTimescaleStatus(
      ScheduledTimescale profile, out string status,
      out AdminModule.Tab.OptionButton.Types type)
    {
      var nowUtcTicks = System.DateTime.UtcNow.Ticks;
      (status, type) = profile.InvalidReason switch
      {
        { Length: > 0 } => (UI_INVALID,
          AdminModule.Tab.OptionButton.Types.Important),
        _ when nowUtcTicks < profile.StartUtcTicks => (
          string.Format(UI_UPCOMING_FROM_FORMAT,
            FormatScheduledTimescaleDate(profile.StartUtcTicks)),
          AdminModule.Tab.OptionButton.Types.None),
        _ when nowUtcTicks < profile.EndUtcTicks => (
          string.Format(UI_ACTIVE_UNTIL_FORMAT,
            FormatScheduledTimescaleDate(profile.EndUtcTicks)),
          AdminModule.Tab.OptionButton.Types.Selected),
        _ => (UI_EXPIRED, AdminModule.Tab.OptionButton.Types.None)
      };
    }

    private static string GetScheduledTimescaleInput(object[] args) =>
      args is null || args.Length is 0 ?
        string.Empty :
        string.Join(" ", args);

    private void ResetScheduledTimescaleEditors(BasePlayer player)
    {
      if (!player)
        return;

      if (_scheduledTimescaleUiStates.TryGetValue(
            player.userID, out var state))
      {
        state.ProfileEditor = null;
        state.EntryEditor = null;
        state.ScaleEditor = null;
        state.Notice = null;
      }

      ClearDatePickerCallback(player);
      _dp?.Close(player);
    }

    private void ClearDatePickerCallback(BasePlayer player)
    {
      if (_admin is null ||
          !_scheduledTimescalePlayerTabs.TryGetValue(
            player.userID, out var tab))
        return;

      _admin.GetPlayerSession(player).SetStorage<System.Action<System.DateTime>>(
        tab, DatePickerModule.OnDatePicked, null);
    }

    private void CloseScheduledTimescaleEditor(BasePlayer player)
    {
      if (!player)
        return;

      ResetScheduledTimescaleEditors(player);
      _scheduledTimescaleUiStates.Remove(player.userID);
      _wipeTemplateUiStates.Remove(player.userID);

      var activeTabID = _admin?.GetTab(player)?.Id;
      if (activeTabID is ADMIN_TAB_ID or WIPE_TEMPLATE_ADMIN_TAB_ID)
      {
        _admin.Close(player);
        _admin.GetPlayerSession(player).SelectedTab = null;
      }

      if (_scheduledTimescalePlayerTabs.Remove(
            player.userID, out var playerTab))
        playerTab.Dispose();

      if (_wipeTemplatePlayerTabs.Remove(
            player.userID, out var templateTab))
        templateTab.Dispose();
    }

#endregion Helpers
#endif
#endregion Editor

#endregion Scheduled Timescales & Wipe Templates

  }
}

#region Extension Methods

#if CARBON
namespace Carbon.Plugins.OfflineRaidProtectionEx
#else
namespace Oxide.Plugins.OfflineRaidProtectionEx
#endif
{
  file static class ExtensionMethods
  {
    private static readonly Permission P =
      Interface.Oxide.GetLibrary<Permission>();

    public static bool HasPermission(this string userID, string permission) =>
      !string.IsNullOrEmpty(userID) && P.UserHasPermission(userID, permission);

    public static bool HasPermission(
      this BasePlayer player, string permission) =>
      player.UserIDString.HasPermission(permission);

    public static bool HasPermission(this ulong userID, string permission) =>
      userID.ToString().HasPermission(permission);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ToPercent(this float value) => (1f - value) * 100f;

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

        foreach (var (key, value) in dictionary)
          first.TryAdd(key, value);
      }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSteamID(this ulong id) => id > 76561197960265728UL;
  }
}

#endregion Extension Methods
 
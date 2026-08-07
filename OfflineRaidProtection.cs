#if CARBON
using Carbon.Components;
using Carbon.Extensions;
using Carbon.Plugins.OfflineRaidProtectionEx;
using System.Runtime.InteropServices;

#if CARBON && !MINIMAL
using Carbon.Modules;
#endif

#else
using Facepunch.Extend;
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

namespace
#if CARBON
  Carbon.Plugins
#else
  Oxide.Plugins
#endif
{
  [Info("Offline Raid Protection", "realedwin/HunterZ", "1.6.2"), Description("Prevents/reduces offline raids by other players")]
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
    private readonly Dictionary<uint, TcState> _tcCache = new();
    private readonly Dictionary<uint, CodeLockWhitelistIndex> _codeLockWhitelistCache = new();
    private readonly Dictionary<ulong, uint> _codeLockBuildingIds = new();
    private readonly Dictionary<ulong, TcCreationData> _tcCreationData = new();
    private readonly HashSet<ulong> _griefCupboardIds = new();
    private readonly HashSet<ulong> _adminIDCache = new();
    private readonly PlayerRuntimeIndex _players = new();
    private readonly DamageScratchSlot _damageScratch = new();
    private readonly PlayerIdSet _relatedPlayersScratch = new();
    private readonly PlayerIdSet _teamMembersScratch = new();
    private bool _dataDirty;
    private bool _saveQueued;
    private bool _serverInitialized;

    // default to UTC
    private System.TimeZoneInfo _timeZone = System.TimeZoneInfo.Utc;

#region Temp

    private readonly StringBuilder _sb = new(2048);
    private readonly HashSet<ulong> _tmpIdsScratch = new();
    private readonly PlayerIdSet _tmpIdSetScratch = new();

#endregion Temp

#region Constants

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

        [JsonProperty(PropertyName = "Protect grief TCs")]
        public bool ProtectGriefTcs { get; set; }

        [JsonProperty(PropertyName = "Prefabs to protect")]
        public HashSet<string> Prefabs { get; set; }

        [JsonProperty(PropertyName = "Prefabs blacklist")]
        public HashSet<string> PrefabsBlacklist { get; set; }
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
        public string CommandTestGrief { get; set; } = "orp.test.grief";

        [JsonProperty(PropertyName = "Command to edit scheduled timescale profiles")]
        public string CommandScheduledTimescales { get; set; } = "orp.schedule";

        [JsonProperty(PropertyName = "Command to update the Prefabs to protect list")]
        public string CommandUpdatePrefabList { get; set; }

        [JsonProperty(PropertyName = "Command to dump the Prefabs to protect list")]
        public string CommandDumpPrefabList { get; set; }
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

          RegisterConsoleCommands(new[] {CommandFillOnlineTimes}, plugin, nameof(Instance.ccFillOnlineTimes), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandUpdatePermissions}, plugin, nameof(Instance.ccUpdatePermissions), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandUpdatePrefabList}, plugin, nameof(Instance.ccUpdatePrefabList), Configuration.Permission.Admin);
          RegisterConsoleCommands(new[] {CommandDumpPrefabList}, plugin, nameof(Instance.ccDumpPrefabList), Configuration.Permission.Admin);
        }

        private void RegisterChatCommands(string[] commands, Plugin plugin, System.Action<BasePlayer, string, string[]> callback, string permission)
        {
          foreach (var command in commands)
#if CARBON
            Community.Runtime.Core.cmd.AddChatCommand(command, plugin, callback, cooldown: CommandCooldown * 1000, permissions: [permission]);
#else
            Instance.cmd.AddChatCommand(command, plugin, callback);
#endif
        }

        private void RegisterConsoleCommands(string[] commands, Plugin plugin, string callback, string permission)
        {
          foreach (var command in commands)
#if CARBON
            Community.Runtime.Core.cmd.AddConsoleCommand(command, plugin, callback, cooldown: CommandCooldown * 1000, permissions: [permission]);
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
        LastConnectDT = connected ? currentTime : LastConnectDT;
      }

      public void EnablePenalty(float duration) => PenaltyEndDT = System.DateTime.UtcNow.AddHours(duration);

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
      public float Scale { get; set; }

      public long ExpiresTicks { get; private set; }

      public bool ActiveGameTipMessage { get; set; }

      public System.TimeSpan RemainingTime { get; set; }

      public bool HasPermission { get; set; }

      public System.Action HideGameTipAction { get; }

      public string ProtectionMessageBuilding { get; private set; }

      public string ProtectionMessageVehicle { get; private set; }

      public PlayerScaleCache(
        System.DateTime expires, float scale, bool hasPermission)
      {
        ExpiresDT = expires;
        Scale = scale;
        ActiveGameTipMessage = false;
        HasPermission = hasPermission;
        HideGameTipAction = HideGameTip;
      }

      public System.DateTime ExpiresDT
      {
        // get => new(Expires);
        set => ExpiresTicks = value.Ticks;
      }

      private void HideGameTip() => ActiveGameTipMessage = false;

      public void CacheMessages(
        OfflineRaidProtection plugin, string userID)
      {
        ProtectionMessageBuilding =
          PrefixMessage(plugin.Msg(LANG_PROTECTION_MESSAGE_BUILDING, userID), true);
        ProtectionMessageVehicle =
          PrefixMessage(plugin.Msg(LANG_PROTECTION_MESSAGE_VEHICLE, userID), true);
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

      private readonly HashSet<ulong> _lookup = new(Capacity);
      private readonly List<ulong> _items = new(Capacity);

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
      public readonly HashSet<ulong> PlayerIds = new(64);

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
      public readonly PlayerIdSet AuthorizedIds = new();

      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public void Clear() => AuthorizedIds.Clear();
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
      public readonly ulong CupboardNetworkId;
      public readonly bool IsDecaying;

      public TcState(BuildingPrivlidge privilege, ulong cupboardNetworkId, bool isDecaying)
      {
        Privilege = privilege;
        CupboardNetworkId = cupboardNetworkId;
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
      public readonly DamageDecisionKind Kind;
      public readonly ulong TargetID;
      public readonly float Scale;
      public readonly PlayerScaleCache TargetScaleCache;
      private readonly DamageDecisionFlags _flags;

      public bool IsVehicle => (_flags & DamageDecisionFlags.Vehicle) is not 0;

      public bool IsDecaying => (_flags & DamageDecisionFlags.Decaying) is not 0;

      public bool IsGrief => (_flags & DamageDecisionFlags.Grief) is not 0;

      public DamageDecision(
        DamageDecisionKind kind, ulong targetID = 0UL, float scale = -1f,
        bool isVehicle = false, bool isDecaying = false, bool isGrief = false,
        PlayerScaleCache targetScaleCache = null)
      {
        Kind = kind;
        TargetID = targetID;
        Scale = scale;
        TargetScaleCache = targetScaleCache;
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
        bool isGrief = false) =>
          new(DamageDecisionKind.Allow, targetID, -1f, isVehicle, isDecaying, isGrief);
    }

#endregion Classes

#region Data

    private sealed class StoredData
    {
      public Dictionary<ulong, LastOnlineData> LastOnline { get; init; } = new();
      public Dictionary<ulong, TcCreationData> TcCreation { get; init; } = new();
    }

    private void MarkDataDirty() => _dataDirty = true;

    private void SaveData()
    {
      Interface.Oxide.DataFileSystem.WriteObject(
        $"{Name}/{nameof(StoredData)}",
        new StoredData
        {
          LastOnline = _lastOnline,
          TcCreation = _tcCreationData
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
      foreach (var lastOnline in _lastOnline.Values)
        lastOnline?.RefreshRuntimeTicks();
    }

    private void RecordCupboardCreation(BuildingPrivlidge buildingPrivlidge)
    {
      var cupboardNetworkId = GetNetworkId(buildingPrivlidge);
      if (cupboardNetworkId is 0UL)
        return;

      if (!_tcCreationData.TryGetValue(cupboardNetworkId, out var creationData))
      {
        creationData = new();
        _tcCreationData[cupboardNetworkId] = creationData;
      }

      if (creationData.HasTrustedCreationTime)
        return;

      creationData.CreatedUtcTicks = System.DateTime.UtcNow.Ticks;
      creationData.HasTrustedCreationTime = true;
      MarkDataDirty();
    }

    private void EnsureCupboardCreationData(ulong cupboardNetworkId)
    {
      if (cupboardNetworkId is 0U ||
          _tcCreationData.ContainsKey(cupboardNetworkId))
        return;

      // A TC found after plugin loading has no trustworthy build timestamp
      // It can establish that a later TC is griefing it, but can never itself
      // lose protection solely because of this fallback observation
      _tcCreationData[cupboardNetworkId] = new();
      MarkDataDirty();
    }

    private void RemoveCupboardCreationData(ulong cupboardNetworkId)
    {
      if (cupboardNetworkId is not 0U &&
          _tcCreationData.Remove(cupboardNetworkId))
        MarkDataDirty();
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

        NormalizeHUDConfig(GetBaseConfig(Version));
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

      Configuration.Version = Version;

      SaveConfig();
      PrintWarning("Config update has been completed!");
    }
    private void SetTimeZone()
    {
      var id =
#if CARBON
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ?
          Configuration.TimeZone.WinTimeZone :
          Configuration.TimeZone.UnixTimeZone;
#else
        Configuration.TimeZone.TimeZone;
#endif
      if (!string.IsNullOrEmpty(id)) _timeZone = GetTimeZoneByID(id);
    }

    private static System.TimeZoneInfo GetTimeZoneByID(string id)
    {
      foreach (var tz in System.TimeZoneInfo.GetSystemTimeZones())
      {
        if (tz.Id == id) return tz;
      }
      return System.TimeZoneInfo.Utc;
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
        ProtectGriefTcs = true,
        Prefabs = GetPrefabNames(),
        PrefabsBlacklist = new()
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

    private void NormalizeHUDConfig(ConfigData baseConfig)
    {
      Configuration.StatusHud ??= baseConfig.StatusHud;
      Configuration.MapMarker ??= baseConfig.MapMarker;

      var hud = Configuration.StatusHud;
      hud.RefreshInterval = NormalizeFinite(
        hud.RefreshInterval, baseConfig.StatusHud.RefreshInterval, 0.5f, 60f);
      hud.Duration = NormalizeFinite(
        hud.Duration,
        baseConfig.StatusHud.Duration, 0.5f, 60f);
      if (!TryParseAnchor(hud.AnchorMin, out var minX, out var minY) ||
          !TryParseAnchor(hud.AnchorMax, out var maxX, out var maxY) ||
          minX > maxX || minY > maxY)
      {
        hud.AnchorMin = baseConfig.StatusHud.AnchorMin;
        hud.AnchorMax = baseConfig.StatusHud.AnchorMax;
      }

      if (!TryParseOffset(hud.OffsetMin, out _, out _) ||
          !TryParseOffset(hud.OffsetMax, out _, out _) ||
          !HasMinimumPointAnchoredHudBounds(hud))
      {
        hud.OffsetMin = baseConfig.StatusHud.OffsetMin;
        hud.OffsetMax = baseConfig.StatusHud.OffsetMax;
      }

      var marker = Configuration.MapMarker;
      marker.RefreshInterval = NormalizeFinite(
        marker.RefreshInterval,
        baseConfig.MapMarker.RefreshInterval, 1f, 600f);
      marker.Radius = NormalizeFinite(
        marker.Radius, baseConfig.MapMarker.Radius, 0.1f, 200f);
      marker.Alpha = NormalizeFinite(
        marker.Alpha, baseConfig.MapMarker.Alpha, 0f, 1f);

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

    private static bool HasMinimumPointAnchoredHudBounds(ConfigData.StatusHudOptions hud)
    {
      TryParseAnchor(hud.AnchorMin, out var minAnchorX, out var minAnchorY);
      TryParseAnchor(hud.AnchorMax, out var maxAnchorX, out var maxAnchorY);
      TryParseOffset(hud.OffsetMin, out var minOffsetX, out var minOffsetY);
      TryParseOffset(hud.OffsetMax, out var maxOffsetX, out var maxOffsetY);
      return (minAnchorX != maxAnchorX || maxOffsetX - minOffsetX > 10f) &&
             (minAnchorY != maxAnchorY || maxOffsetY - minOffsetY > 10f);
    }

    private static string NormalizeColor(string value, string fallback) =>
      UnityEngine.ColorUtility.TryParseHtmlString(value, out _) ? value : fallback;

#endregion Config

#region Hooks

    private void Loaded()
    {
      Instance = this;

      CacheMapMarkerColors();

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

      _serverInitialized = true;
      InitializeStatusHud();
      InitializeMapMarkers();
      InitializeScheduledTimescales();
    }

    private void OnNewSave(string _filename)
    {
      _lastOnline.Clear();
      _tcCreationData.Clear();
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

    private void OnServerShutdown() => Save();

    private void Unload()
    {
      _serverInitialized = false;
      Save();
      _saveQueued = false;

      UnloadScheduledTimescales();
      UnloadStatusHud();
      UnloadMapMarkers();

      Configuration = null;
      Instance = null;
      Clans = null;

      _prefabProtection.Clear();
      _scaleCache.Clear();
      _lastOnline.Clear();
      _tcCache.Clear();
      ClearCodeLockWhitelistCache();
      _tcCreationData.Clear();
      _griefCupboardIds.Clear();
      _tmpIdsScratch.Clear();
      _adminIDCache.Clear();

      _sb.Clear();
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

      if (!_scaleCache.TryGetValue(player.userID.Get(), out var scaleCache))
      {
        scaleCache = new(
          currentTime, -1f,
          player.userID.Get().HasPermission(Configuration.Permission.Protect));
        _scaleCache[player.userID.Get()] = scaleCache;
      }
      scaleCache.CacheMessages(this, player.UserIDString);

      if (Configuration.StatusHud.Enabled)
      {
        var playerID = player.userID.Get();
        if (!_hudStates.TryGetValue(playerID, out var hudState))
        {
          hudState = Facepunch.Pool.Get<HudPlayerState>();
          _hudStates[playerID] = hudState;
        }
        hudState.PrivilegeRefreshAt =
          UnityEngine.Time.realtimeSinceStartup +
          (playerID % 10UL) * 0.1f;

        QueueStatusHudRefresh(player);
      }

      QueueMapMarkerRefresh(
        _adminIDCache.Contains(player.userID.Get()));
    }

    private void OnPlayerDisconnected(BasePlayer player)
    {
      if (!player)
        return;

#if CARBON && !MINIMAL
      CloseScheduledTimescaleEditor(player);
#endif
      var playerID = player.userID.Get();
      _adminIDCache.Remove(playerID);

      if (_hudStates.TryGetValue(playerID, out var hudState))
        RemoveStatusHud(player, playerID, hudState);
      else
        HideStatusHud(player);

      var currentTime = System.DateTime.UtcNow;
      UpdateLastOnline(player, currentTime);

      _players.AddPlayer(player);

      QueueMapMarkerRefresh();
    }

    private void OnUserPermissionGranted(string id, string permissionName)
    {
      if (permissionName != Configuration.Permission.Admin) return;
      if (RefreshAdmin(id, out var becameAdmin))
        QueueMapMarkerRefresh(becameAdmin);
    }

    private void OnUserPermissionRevoked(string id, string permissionName)
    {
      if (permissionName != Configuration.Permission.Admin) return;
      if (RefreshAdmin(id, out var becameAdmin))
        QueueMapMarkerRefresh(becameAdmin);
    }

    private void OnUserGroupAdded(string id, string _groupName)
    {
      if (RefreshAdmin(id, out var becameAdmin))
        QueueMapMarkerRefresh(becameAdmin);
    }

    private void OnUserGroupRemoved(string id, string _groupName)
    {
      if (RefreshAdmin(id, out var becameAdmin))
        QueueMapMarkerRefresh(becameAdmin);
    }

    private void OnGroupPermissionGranted(
      string _groupName, string permissionName)
    {
      if (permissionName != Configuration.Permission.Admin) return;
      if (CacheAllAdmins(out var hasNewAdmin))
        QueueMapMarkerRefresh(hasNewAdmin);
    }

    private void OnGroupPermissionRevoked(
      string _groupName, string permissionName)
    {
      if (permissionName != Configuration.Permission.Admin) return;
      if (CacheAllAdmins(out var hasNewAdmin))
        QueueMapMarkerRefresh(hasNewAdmin);
    }

    private void OnUserNameUpdated(string id, string _oldName, string newName)
    {
      var userID = ulong.Parse(id);
      _players.UpdateName(userID, _oldName, newName);

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

      var markerRadiusNetworkID = GetNetworkId(entity);
      if (!_pendingMapMarkers.Contains(entity) &&
          (markerRadiusNetworkID is 0UL ||
           !_activeMarkerNetIds.Contains(markerRadiusNetworkID)))
        return null;

      return _adminIDCache.Contains(target.userID.Get()) ?
        null : false;
    }

    private object CanNetworkTo(
      VendingMachineMapMarker entity, BasePlayer target)
    {
      if (!Configuration.MapMarker.Enabled || !entity || !target)
        return null;

      var markerVendingNetworkID = GetNetworkId(entity);
      if (!_pendingMapMarkers.Contains(entity) &&
          (markerVendingNetworkID is 0UL ||
           !_activeMarkerNetIds.Contains(markerVendingNetworkID)))
        return null;

      return _adminIDCache.Contains(target.userID.Get()) ?
        null : false;
    }

    private void OnCupboardProtectionCalculated(
      BuildingPrivlidge buildingPrivlidge, float cachedProtectedMinutes)
    {
      if (!buildingPrivlidge || buildingPrivlidge.buildingID is 0U)
        return;

      var cupboardNetworkId = GetNetworkId(buildingPrivlidge);
      if (!Configuration.RaidProtection.ProtectGriefTcs)
        EnsureCupboardCreationData(cupboardNetworkId);

      _tcCache[buildingPrivlidge.buildingID] =
        new TcState(
          buildingPrivlidge,
          cupboardNetworkId,
          IsBuildingDecaying(buildingPrivlidge, cachedProtectedMinutes > 0));

      if (Configuration.MapMarker.Enabled)
        QueueBuildingMapMarkerSync(buildingPrivlidge.buildingID);
    }

    private void OnCupboardAuthorize(
      BuildingPrivlidge buildingPrivlidge, BasePlayer player)
    {
      UpdateTcMarkerLabel(buildingPrivlidge);
      QueueCupboardStatusHudRefresh(buildingPrivlidge);
      QueueStatusHudRefresh(player);
    }

    private void OnCupboardAssign(
      BuildingPrivlidge buildingPrivlidge, ulong _userID,
      BasePlayer _player) =>
      UpdateTcMarkerLabel(buildingPrivlidge);

    private void OnCupboardDeauthorize(
      BuildingPrivlidge buildingPrivlidge, BasePlayer player)
    {
      UpdateTcMarkerLabel(buildingPrivlidge);
      QueueCupboardStatusHudRefresh(buildingPrivlidge);
      QueueStatusHudRefresh(player);
    }

    private void OnCupboardClearList(
      BuildingPrivlidge buildingPrivlidge, BasePlayer player)
    {
      UpdateTcMarkerLabel(buildingPrivlidge);
      QueueCupboardStatusHudRefresh(buildingPrivlidge);
      QueueStatusHudRefresh(player);
    }

    private void OnEntitySpawned(Tugboat tugboat) =>
      QueueBoatMapMarkerSync(tugboat);

    private void OnEntitySpawned(PlayerBoat modularBoat) =>
      QueueBoatMapMarkerSync(modularBoat);

    private void OnEntitySpawned(BuildingPrivlidge buildingPrivlidge)
    {
      if (!buildingPrivlidge)
        return;

      RecordCupboardCreation(buildingPrivlidge);
      RefreshTcCache(buildingPrivlidge.buildingID);
    }

    private void OnEntitySpawned(CodeLock codeLock)
    {
      if (!Configuration.Team.IncludeWhitelistPlayers || !codeLock)
        return;

#if CARBON
      Community.Runtime.Core.NextFrame(() => TrackSpawnedCodeLock(codeLock));
#else
      NextFrame(() => TrackSpawnedCodeLock(codeLock));
#endif
    }

    private void OnEntityKill(BaseNetworkable baseNetworkable)
    {
      if (baseNetworkable is not BaseEntity entity)
        return;

      if (Configuration.Team.IncludeWhitelistPlayers)
      {
        if (entity is CodeLock codeLock)
          RemoveTrackedCodeLock(GetNetworkId(codeLock));
        else if (entity.GetSlot(BaseEntity.Slot.Lock) is CodeLock entityCodeLock)
          RemoveTrackedCodeLock(GetNetworkId(entityCodeLock));
      }

      if (entity is BuildingPrivlidge buildingPrivlidge)
      {
        var cupboardNetworkId = GetNetworkId(baseNetworkable);
        if (cupboardNetworkId is not 0U)
        {
          RemoveCupboardCreationData(cupboardNetworkId);
          RemoveMapMarker(cupboardNetworkId);
        }

        RefreshTcCache(buildingPrivlidge.buildingID);
        return;
      }

      switch (entity)
      {
        case Tugboat tugboat:
          RemoveBoatMapMarker(tugboat);
          break;
        case PlayerBoat modularBoat:
          RemoveBoatMapMarker(modularBoat);
          break;
        case VehiclePrivilege vehiclePrivilege when vehiclePrivilege.ParentVehicle is Tugboat or PlayerBoat:
          RemoveMapMarker(GetNetworkId(vehiclePrivilege));
          break;
      }
    }

    private void OnCodeEntered(CodeLock codeLock, BasePlayer _player, string _code) =>
      RefreshCodeLockWhitelistSnapshot(codeLock);

    private void OnCodeChanged(
      BasePlayer _player, CodeLock codeLock, string _code, bool _isGuestCode) =>
      RefreshCodeLockWhitelistSnapshot(codeLock);

    private void OnBuildingSplit(
      BuildingManager.Building oldBuilding, uint newBuildingId)
    {
      var oldBuildingID = oldBuilding?.ID ?? 0U;
      RemoveCodeLockWhitelistCache(oldBuildingID);
      RemoveCodeLockWhitelistCache(newBuildingId);
      RefreshTcCache(oldBuildingID);
      RefreshTcCache(newBuildingId);
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
      RefreshTcCache(toBuildingID);
      RefreshTcCache(fromBuildingID);
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

      // allow if unprotected
      if (ApartmentProtectionState.Protected != protection)
        return null;

      // allow if random chance enabled and roll succeeds
      if (Configuration.ApartmentProtection.DamageAsChance &&
          Random.Range(0f, 1f) <= damageScale)
      {
        return null;
      }

      // block and notify
      NotifyApartmentOrShop(player, ownerID, damageScale);
      return true;
    }

    private object OnRentableShopBreakInComplete(
      RentableShop shop, BasePlayer player)
    {
      if (!Configuration.ApartmentProtection.ProtectShops ||
          !shop || player?.userID.IsSteamId() is null or false)
        return null;

      var (protection, ownerID, damageScale) = GetShopProtection(shop);

      // allow if unprotected
      if (ApartmentProtectionState.Protected != protection)
        return null;

      // allow if random chance enabled and roll succeeds
      if (Configuration.ApartmentProtection.DamageAsChance &&
          Random.Range(0f, 1f) <= damageScale)
      {
        return null;
      }

      // block and notify
      NotifyApartmentOrShop(player, ownerID, damageScale);
      return true;
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
      if (Configuration.RaidProtection.ProtectGriefTcs &&
          !needsTcTracking &&
          !Configuration.Team.IncludeWhitelistPlayers)
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

      if (!Configuration.MapMarker.Enabled)
      {
        Unsubscribe(nameof(CanNetworkTo));
        Unsubscribe(nameof(OnCupboardAssign));
      }
      if (!Configuration.MapMarker.Enabled && !needsStatusHudCupboardTracking)
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
    }

#endregion Hook Subscribtion

#region Cache Methods

    private void CacheData()
    {
      CachePrefabs();
      CacheDefaultTimescales();
      CacheAllPlayerScale();
      CacheAllPlayers();

      if (!Configuration.RaidProtection.ProtectDecayingBase ||
          !Configuration.RaidProtection.ProtectGriefTcs ||
          NeedsTcTracking)
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

        CachePrefabProtection(prefabID, IsEntityProtected(shortName));
      }

      var manifest = GameManifest.Current;
      foreach (var entity in manifest.entities)
      {
        var prefab = GameManager.server.FindPrefab(entity.ToLowerInvariant());
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

        var prefabID = baseEntity.prefabID;
        var shortName = baseEntity.ShortPrefabName;
        var isAi = activeComponent is
          BaseNpc or NPCPlayer or BradleyAPC or AttackHelicopter or
          CH47Helicopter or BasePlayer;
        var isVehicle = activeComponent is BaseVehicle && !isAi;

        CachePrefabProtection(prefabID, IsEntityProtected(shortName, isVehicle, isAi));
      }
    }

    private void CachePrefabProtection(uint prefabID, bool isProtected)
    {
      if (isProtected || !_prefabProtection.ContainsKey(prefabID))
        _prefabProtection[prefabID] = isProtected;
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

    private void CacheDamageScale(ulong targetID, float scale)
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
        scaleCache.CacheMessages(this, targetID.ToString());
      }
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

    private void RefreshTcCache(uint buildingID)
    {
      if (buildingID is 0U)
        return;

      CachePhysicalCupboard(buildingID, BuildingManager.server.GetBuilding(buildingID));
      CacheGriefCupboards();
      QueueBuildingStatusHudRefresh(buildingID);

      if (Configuration.MapMarker.Enabled)
        SyncBuildingMapMarker(buildingID);
    }

    private void CacheAllCupboards()
    {
      _tcCache.Clear();
      ClearCodeLockWhitelistCache();
      _griefCupboardIds.Clear();
      var protectGriefTcs =
        Configuration.RaidProtection.ProtectGriefTcs;

      // scan all buildings
      foreach (var (buildingID, building)
               in BuildingManager.server.buildingDictionary)
      {
        if (building is null)
          continue;

        CachePhysicalCupboard(buildingID, building);
      }

      if (protectGriefTcs)
        return;

      CacheGriefCupboards();
      RemoveStaleCupboardCreationData();
      return;

      void RemoveStaleCupboardCreationData()
      {
        _tmpIdsScratch.Clear();
        foreach (var cupboardNetworkId in _tcCreationData.Keys)
        {
          if (BaseNetworkable.serverEntities.Find(
                new NetworkableId(cupboardNetworkId)) is BuildingPrivlidge)
            continue;

          _tmpIdsScratch.Add(cupboardNetworkId);
        }

        foreach (var cupboardNetworkId in _tmpIdsScratch)
        {
          _tcCreationData.Remove(cupboardNetworkId);
          MarkDataDirty();
        }
      }
    }

    private void CachePhysicalCupboard(
      uint buildingID, BuildingManager.Building building)
    {
      _tcCache.Remove(buildingID);
      RemoveCodeLockWhitelistCache(buildingID);

      var buildingPrivileges = building?.buildingPrivileges;
      if (buildingPrivileges is null)
        return;

      BuildingPrivlidge physicalCupboard = null;

      // scan all TCs whose build privileges overlap building
      // Only a matching building ID proves physical membership. Every other
      // entry merely has a privilege zone overlapping this building
      foreach (var buildingPrivlidge in buildingPrivileges)
      {
        if (!buildingPrivlidge)
          continue;

        if (!Configuration.RaidProtection.ProtectGriefTcs)
          EnsureCupboardCreationData(GetNetworkId(buildingPrivlidge));

        // Only one TC can be physically attached to a building ID
        if (!physicalCupboard && buildingPrivlidge.buildingID == buildingID)
          physicalCupboard = buildingPrivlidge;

        if (physicalCupboard)
          break;
      }

      if (!physicalCupboard)
        return;

      var cupboardNetworkId = GetNetworkId(physicalCupboard);
      var protectedMinutes = physicalCupboard.GetProtectedMinutes();
      _tcCache[buildingID] =
        new TcState(
          physicalCupboard,
          cupboardNetworkId,
          IsBuildingDecaying(physicalCupboard, protectedMinutes > 0));
    }

    private void CacheGriefCupboards()
    {
      _griefCupboardIds.Clear();

      foreach (var (id, data) in _tcCreationData)
      {
        if (data.GriefState is TcGriefState.ForceTrue)
          _griefCupboardIds.Add(id);
      }

      foreach (var (buildingID, building)
               in BuildingManager.server.buildingDictionary)
      {
        var overlappingPrivileges = building?.buildingPrivileges;
        if (overlappingPrivileges is null ||
            !_tcCache.TryGetValue(buildingID, out var buildingTc))
          continue;

        foreach (var overlappingTc in overlappingPrivileges)
        {
          if (!overlappingTc || overlappingTc.buildingID == buildingID)
            continue;

          if (!_tcCache.TryGetValue(
                overlappingTc.buildingID, out var overlappingTcState) ||
              overlappingTcState.CupboardNetworkId != GetNetworkId(overlappingTc))
            continue;

          // Privilege overlap is deliberately not ownership. Only the TC with
          // a trusted later creation time is a grief cupboard; ambiguous
          // legacy pairs keep their normal offline protection
          if (!IsCupboardNewer(overlappingTcState.CupboardNetworkId,
                               buildingTc.CupboardNetworkId))
            continue;

          if (_tcCreationData.TryGetValue(
                overlappingTcState.CupboardNetworkId,
                out var overlappingCreationData) &&
              overlappingCreationData.GriefState is TcGriefState.ForceFalse)
            continue;

          _griefCupboardIds.Add(overlappingTcState.CupboardNetworkId);
        }
      }

      return;

      bool IsCupboardNewer(ulong firstCupboardNetworkId,
        ulong otherCupboardNetworkId)
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
    }

    private void RemoveCodeLockWhitelistCache(uint buildingID)
    {
      if (buildingID is 0U ||
          !_codeLockWhitelistCache.Remove(buildingID, out var cacheEntry))
        return;

      foreach (var lockNetworkId in cacheEntry.Locks.Keys)
      {
        if (_codeLockBuildingIds.TryGetValue(lockNetworkId, out var trackedBuildingID) &&
            trackedBuildingID == buildingID)
          _codeLockBuildingIds.Remove(lockNetworkId);
      }

      Facepunch.Pool.Free(ref cacheEntry);
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

    private void TrackSpawnedCodeLock(CodeLock codeLock)
    {
      if (!codeLock || !TryGetCodeLockBuildingID(
          codeLock, out var buildingID))
        return;

      var lockNetworkId = GetNetworkId(codeLock);
      if (lockNetworkId is 0UL)
        return;

      if (_codeLockBuildingIds.TryGetValue(
            lockNetworkId, out var trackedBuildingID) &&
          trackedBuildingID != buildingID)
      {
        RemoveCodeLockWhitelistCache(trackedBuildingID);
        RemoveCodeLockWhitelistCache(buildingID);
        return;
      }

      if (_codeLockWhitelistCache.TryGetValue(buildingID, out var cacheEntry))
        RegisterCodeLockWhitelistSnapshot(buildingID, codeLock, cacheEntry);
    }

    private void RefreshCodeLockWhitelistSnapshot(CodeLock codeLock)
    {
      var lockNetworkId = GetNetworkId(codeLock);
      if (lockNetworkId is 0UL ||
          !_codeLockBuildingIds.TryGetValue(lockNetworkId, out var buildingID))
      {
        if (TryGetCodeLockBuildingID(codeLock, out buildingID))
          RemoveCodeLockWhitelistCache(buildingID);
        return;
      }

      if (!TryGetCodeLockBuildingID(codeLock, out var currentBuildingID) ||
          currentBuildingID != buildingID)
      {
        RemoveCodeLockWhitelistCache(buildingID);
        RemoveCodeLockWhitelistCache(currentBuildingID);
        return;
      }

      if (!_codeLockWhitelistCache.TryGetValue(buildingID, out var cacheEntry) ||
          !cacheEntry.Locks.TryGetValue(lockNetworkId, out var snapshot))
      {
        RemoveCodeLockWhitelistCache(buildingID);
        return;
      }

      var whitelistPlayers = codeLock.whitelistPlayers;
      foreach (var playerId in snapshot.PlayerIds)
      {
        if (whitelistPlayers?.Contains(playerId) is true)
          continue;

        RemoveCodeLockWhitelistPlayer(cacheEntry, playerId);
      }

      if (whitelistPlayers is not null)
      {
        foreach (var playerId in whitelistPlayers)
        {
          if (snapshot.PlayerIds.Contains(playerId))
            continue;

          AddCodeLockWhitelistPlayer(cacheEntry, playerId);
        }
      }

      snapshot.PlayerIds.Clear();
      if (whitelistPlayers is not null)
        snapshot.PlayerIds.UnionWith(whitelistPlayers);
    }

    private void RemoveTrackedCodeLock(ulong lockNetworkId)
    {
      if (lockNetworkId is 0UL ||
          !_codeLockBuildingIds.Remove(lockNetworkId, out var buildingID) ||
          !_codeLockWhitelistCache.TryGetValue(buildingID, out var cacheEntry) ||
          !cacheEntry.Locks.Remove(lockNetworkId, out var snapshot))
        return;

      foreach (var playerId in snapshot.PlayerIds)
        RemoveCodeLockWhitelistPlayer(cacheEntry, playerId);

      Facepunch.Pool.Free(ref snapshot);
    }

    private void RegisterCodeLockWhitelistSnapshot(
      uint buildingID, CodeLock codeLock, CodeLockWhitelistIndex cacheEntry)
    {
      var lockNetworkId = GetNetworkId(codeLock);
      if (lockNetworkId is 0UL)
        return;

      if (_codeLockBuildingIds.TryGetValue(lockNetworkId, out var trackedBuildingID) &&
          trackedBuildingID != buildingID)
        RemoveCodeLockWhitelistCache(trackedBuildingID);

      if (cacheEntry.Locks.ContainsKey(lockNetworkId))
      {
        RefreshCodeLockWhitelistSnapshot(codeLock);
        return;
      }

      var snapshot = Facepunch.Pool.Get<CodeLockWhitelistSnapshot>();
      var whitelistPlayers = codeLock.whitelistPlayers;
      if (whitelistPlayers is not null)
      {
        foreach (var playerId in whitelistPlayers)
        {
          snapshot.PlayerIds.Add(playerId);
          AddCodeLockWhitelistPlayer(cacheEntry, playerId);
        }
      }

      cacheEntry.Locks[lockNetworkId] = snapshot;
      _codeLockBuildingIds[lockNetworkId] = buildingID;
    }

    private static void AddCodeLockWhitelistPlayer(
      CodeLockWhitelistIndex cacheEntry, ulong playerId)
    {
      if (playerId is 0UL)
        return;

      if (!cacheEntry.PlayerReferences.TryGetValue(playerId, out var references))
      {
        cacheEntry.PlayerReferences[playerId] = 1;
        cacheEntry.AuthorizedPlayers.Add(playerId);
        return;
      }

      cacheEntry.PlayerReferences[playerId] = references + 1;
    }

    private static void RemoveCodeLockWhitelistPlayer(
      CodeLockWhitelistIndex cacheEntry, ulong playerId)
    {
      if (!cacheEntry.PlayerReferences.TryGetValue(playerId, out var references))
        return;

      if (references > 1)
      {
        cacheEntry.PlayerReferences[playerId] = references - 1;
        return;
      }

      cacheEntry.PlayerReferences.Remove(playerId);
      cacheEntry.AuthorizedPlayers.Remove(playerId);
    }

    private static bool TryGetCodeLockBuildingID(CodeLock codeLock,
      out uint buildingID)
    {
      buildingID = (codeLock?.GetParentEntity() as DecayEntity)?.buildingID ?? 0U;
      return buildingID is not 0U;
    }

#region TimeScale Caching & Resolution

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
      BaseCombatEntity entity) => entity switch
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
        MitigateDamage(ref hitInfo, in decision) :
        null;

      return result;
    }

    private DamageDecision EvaluateProtection(
      BaseCombatEntity entity, BasePlayer attacker,
      System.DateTime nowUtc)
    {
      var (tugboat, modularBoat, vehicle) = GetVehicle(entity);
      bool isVehicle = vehicle;
      BuildingPrivlidge physicalPrivilege = null;

      // Allow if it is recognised as a grief-building
      if (!Configuration.RaidProtection.ProtectGriefTcs &&
          !isVehicle && !tugboat && !modularBoat &&
          TryGetTcState(entity, out var physicalTc))
      {
        physicalPrivilege = physicalTc.Privilege;
        if (_griefCupboardIds.Contains(physicalTc.CupboardNetworkId))
          return DamageDecision.Allow(entity.OwnerID, isGrief: true);
      }

      _damageScratch.Clear();
      var authorizedPlayers = _damageScratch.AuthorizedIds;
      if (!GetAuthorizedPlayers(
            entity, tugboat, modularBoat, vehicle,
            physicalPrivilege, authorizedPlayers, out var privilege) || authorizedPlayers.Overflowed)
        return DamageDecision.Allow(isVehicle: isVehicle);

      // Allow if the TC has either no players authed, or an NPC authed
      // Note: Mixed auth is possible, but we still want to ignore it, because
      //  it probably indicates a Raidable Bases base or something
      var firstPlayer = authorizedPlayers.First;
      if (!firstPlayer.IsSteamID())
        return DamageDecision.Allow(firstPlayer, isVehicle);

      // Allow if damage is from is an authorized player
      if (attacker && authorizedPlayers.Contains(attacker.userID.Get()))
        return DamageDecision.Allow(firstPlayer, isVehicle);

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

      var isOnlineRaidProtectionEnabled = Configuration.RaidProtection.OnlineRaidProtection;
      if (!isOnlineRaidProtectionEnabled && AnyPlayersOnline(authorizedPlayers))
        return DamageDecision.Allow(targetID, isVehicle);

      // Get the most recent team member based on the configuration setting
      targetID = GetRecentActiveMemberAll(targetID, authorizedPlayers, nowUtc);
      if (!_lastOnline.TryGetValue(targetID, out var targetLastOnline) ||
          (!isOnlineRaidProtectionEnabled && IsOnline(targetID)) ||
          nowUtc.Ticks <= targetLastOnline.PenaltyEndTicks)
        return DamageDecision.Allow(targetID, isVehicle);

      var targetScaleCache = GetOrCreateScaleCache(targetID, nowUtc);
      var scale = GetCachedDamageScale(targetID, targetLastOnline, targetScaleCache, nowUtc);

      return scale is <= -1f or 1f ?
        DamageDecision.Allow(targetID, isVehicle) :
        new DamageDecision(
          DamageDecisionKind.ApplyScale,
          targetID,
          scale,
          isVehicle,
          targetScaleCache: targetScaleCache);
    }

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

        // Wheel authed players check
        var vehiclePrivilege =
          tugboat ? tugboat.GetChildPrivilege() :
          modularBoat ? modularBoat.GetChildPrivilege() :
          null;
        if (!vehiclePrivilege)
          return false;
        authorizedPlayers.AddRange(vehiclePrivilege.authorizedPlayers);

        // Abort if code lock checks disabled
        if (!Configuration.Team.IncludeWhitelistPlayers)
          return authorizedPlayers.Count is not 0;

        // Deployable code locks check
        if (tugboat && tugboat.children is not null)
        {
          foreach (var boatChild in tugboat.children)
            AddCodeLockWhitelistPlayers(boatChild, authorizedPlayers);
        }
        else if (modularBoat && modularBoat.Deployables.Cached is not null)
        {
          foreach (var boatChild in modularBoat.Deployables.Cached)
            AddCodeLockWhitelistPlayers(boatChild, authorizedPlayers);
        }

        // Don't fall through to TC check, because boats are their own base
        return authorizedPlayers.Count is not 0;
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

    private PlayerIdSet GetCodeLockWhitelistPlayers(BuildingPrivlidge privilege)
    {
      var buildingID = privilege.buildingID;
      if (_codeLockWhitelistCache.TryGetValue(buildingID, out var cacheEntry))
        return cacheEntry.AuthorizedPlayers;

      cacheEntry = Facepunch.Pool.Get<CodeLockWhitelistIndex>();
      _codeLockWhitelistCache[buildingID] = cacheEntry;
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

      foreach (var playerId in lockEntity.whitelistPlayers)
        targetSet.Add(playerId);
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

      if (!playersValid)
        return GetRecentActiveMember(targetID, nowUtc);

      _relatedPlayersScratch.Clear();

      if (Clans is not null)
      {
        for (var i = 0; i < players.Count; i++)
        {
          var playerID = players[i];
          if (_relatedPlayersScratch.Contains(playerID))
            continue;

          var tag = GetCachedClanTag(playerID);
          if (string.IsNullOrEmpty(tag))
          {
            _relatedPlayersScratch.Add(playerID);
            continue;
          }

          var clanMembers = GetCachedClanMembers(tag);
          if (clanMembers?.Count > 0)
          {
            _relatedPlayersScratch.AddRange(clanMembers);
            continue;
          }

          _relatedPlayersScratch.Add(playerID);
        }

        return _relatedPlayersScratch.Overflowed ? 0UL :
          GetOfflineMember(_relatedPlayersScratch.GetList(), nowUtc);
      }

      for (var i = 0; i < players.Count; i++)
      {
        var playerID = players[i];
        if (_relatedPlayersScratch.Contains(playerID))
          continue;

        var teamMembers = GetTeamMembers(playerID);
        if (teamMembers?.Count > 0)
        {
          _relatedPlayersScratch.AddRange(teamMembers);
          continue;
        }
        _relatedPlayersScratch.Add(playerID);
      }

      return _relatedPlayersScratch.Overflowed ? 0UL :
        GetOfflineMember(_relatedPlayersScratch.GetList(), nowUtc);

      ulong GetRecentActiveMember(ulong relatedTargetID, System.DateTime relatedNowUtc)
      {
        if (Clans is not null)
        {
          var tag = GetCachedClanTag(relatedTargetID);
          if (string.IsNullOrEmpty(tag))
            return relatedTargetID;

          var clanMembers = GetCachedClanMembers(tag);
          return clanMembers?.Count > 0 ?
            GetOfflineMember(clanMembers, relatedNowUtc) : relatedTargetID;
        }

        var teamMembers = GetTeamMembers(relatedTargetID);
        return teamMembers?.Count > 0 ?
          GetOfflineMember(teamMembers, relatedNowUtc) : relatedTargetID;
      }
    }

    private bool AnyPlayersOffline(List<ulong> playerIDs)
    {
      foreach (var player in playerIDs)
      {
        if (IsOffline(player))
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
      ulong targetID, System.DateTime nowUtc)
    {
      if (_scaleCache.TryGetValue(targetID, out var scaleCache))
        return scaleCache;

      scaleCache = new(
        nowUtc, -1f,
        targetID.HasPermission(Configuration.Permission.Protect));
      _scaleCache[targetID] = scaleCache;
      scaleCache.CacheMessages(this, targetID.ToString());
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
      if (targetScaleCache.HasPermission)
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
      targetScaleCache.ExpiresDT = new System.DateTime(expiresTicks);
      targetScaleCache.Scale = scale;
      return scale;
    }

    private float GetCachedDamageScale(ulong targetID)
    {
      var nowUtc = System.DateTime.UtcNow;
      if (!_lastOnline.TryGetValue(targetID, out var targetLastOnline))
        return -1f;

      var targetScaleCache = GetOrCreateScaleCache(targetID, nowUtc);
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
      if (targetLastOnline is null || !Configuration.RaidProtection.OnlineRaidProtection &&
          IsOnline(targetID))
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
           !Configuration.MapMarker.Enabled) ||
          scaleCache is null)
        return;

      if (offlineTimeScaleApplies && damageScaleKeys.Length > 0)
      {
        var remainingHours =
          damageScaleKeys[^1] - GetOfflineHours(targetLastOnline, nowUtc);
        scaleCache.RemainingTime =
          System.TimeSpan.FromHours(remainingHours > 0 ? remainingHours : 0d);
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
      System.DateTime nowUtc, out bool offlineTimeScaleApplies)
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

      if (damageScaleKeys.Length is 0 ||
          !IsOffline(targetID, targetLastOnline, nowUtc))
        return -1f;

      if (Configuration.RaidProtection.CooldownQualifyMinutes > 0)
      {
        var minutes =
          (targetLastOnline.LastOnlineTicks - targetLastOnline.LastConnectTicks) /
          (float)System.TimeSpan.TicksPerMinute;

        if (targetLastOnline.LastConnect <= 0L ||
            minutes < Configuration.RaidProtection.CooldownQualifyMinutes)
          return -1f;
      }

      var hours = GetOfflineHoursUnchecked(targetLastOnline, nowUtc);

      if (hours < damageScaleKeys[0])
      {
        offlineTimeScaleApplies = true;
        return Configuration.RaidProtection.InterimDamage;
      }

      for (var i = damageScaleKeys.Length - 1; i > 0; i--)
      {
        var key = damageScaleKeys[i];
        if (hours >= key)
        {
          offlineTimeScaleApplies = true;
          return damageScale[key];
        }
      }

      offlineTimeScaleApplies = true;
      return damageScale[damageScaleKeys[0]];
    }

    private object MitigateDamage(
      ref HitInfo hitInfo, in DamageDecision decision)
    {
      var scale = decision.Scale;
      if (scale >= 1f)
      {
        hitInfo.damageTypes.ScaleAll(scale);
        return null;
      }

      var initiator = hitInfo.InitiatorPlayer;
      var showMessages = Configuration.Other.ShowMessage;
      var playSound = Configuration.Other.PlaySound;

      if (!initiator || (!showMessages && !playSound))
      {
        if (scale is not 0f)
          hitInfo.damageTypes.ScaleAll(scale);

        return scale is 0f ? true : null;
      }

      var isFire = hitInfo.damageTypes.GetMajorityDamageType()
        is Rust.DamageType.Heat or Rust.DamageType.Fun_Water;
      var showMessage = showMessages &&
          (!isFire || hitInfo.WeaponPrefab is not null) &&
          Configuration.Other.GameTipWeaponCategories?.Contains(
            GetGameTipWeaponCategory(ref hitInfo)) is true;

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
          UnityEngine.Vector3.zero);
      }

      return scale is 0f ? true : null;
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
          UnityEngine.Vector3.zero);
      }
    }

    private ulong GetApartmentOwnerID(ApartmentRoom apartmentRoom)
    {
      using var e = apartmentRoom.Owners.GetEnumerator();
      return e.MoveNext() && e.Current.IsSteamId() ? e.Current : 0UL;
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

      // check if damage scale voids protection
      damageScale = GetCachedDamageScale(ownerID);
      if (damageScale < 0f)
        damageScale = GetDamageScale(ownerID);
      if (damageScale < 0f ||
          damageScale >= Configuration.ApartmentProtection.WhenDamageBelow ||
          damageScale >= 1f)
        return
          (ApartmentProtectionState.UnprotectedDamageScale, ownerID, damageScale);

      // must be at least partially protected
      return (ApartmentProtectionState.Protected, ownerID, damageScale);
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

      // check if damage scale voids protection
      damageScale = GetCachedDamageScale(ownerID);
      if (damageScale < 0f)
        damageScale = GetDamageScale(ownerID);
      if (damageScale < 0f ||
          damageScale >= Configuration.ApartmentProtection.WhenDamageBelow ||
          damageScale >= 1f)
        return
          (ApartmentProtectionState.UnprotectedDamageScale, ownerID, damageScale);

      // must be at least partially protected
      return (ApartmentProtectionState.Protected, ownerID, damageScale);
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

      var nowUtc = System.DateTime.UtcNow;
      var targetScaleCache = GetOrCreateScaleCache(targetID, nowUtc);
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
        .Append("<color=").Append(GetColor(amount)).Append(">").Append(amount).Append("%</color>");

      if (Configuration.Other.ShowRemainingTime)
      {
        var remainingTime =
          targetScaleCache?.RemainingTime ?? System.TimeSpan.Zero;
        if (remainingTime != System.TimeSpan.Zero)
        {
          _sb.Append(" (");
          AppendRemainingTime(_sb, remainingTime);
          _sb.Append(')');
        }
      }

      player.SendConsoleCommand(
        COMMAND_SHOWTOAST, GameTip.Styles.Blue_Short, _sb.ToString(),
        string.Empty, false);
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
      ref HitInfo hitInfo)
    {
      var damageTypes = hitInfo.damageTypes;

      if (damageTypes.Has(Rust.DamageType.Explosion))
        return GameTipWeaponCategory.Explosive;

      if (damageTypes.IsMeleeType())
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
    private readonly HashSet<ulong> _queuedStatusHudPlayerIds = new();
    private readonly List<ulong> _scheduledStatusHudPlayerIds = new();
    private readonly StringBuilder _hudBuilder = new(512);
    private bool _statusHudRefreshQueued;
#if CARBON
    private LuiPosition _statusHudPosition;
    private LuiOffset _statusHudOffset;
    private Oxide.Plugins.Timer _statusHudScheduler;
#else
    private string _hudPayloadPrefix;
    private Timer _statusHudScheduler;
#endif

#region Constants

    private const string STATUS_HUD_NAME = "ORP_HUD_STATUS_BANNER";
    private const string STATUS_HUD_TEXT_NAME = STATUS_HUD_NAME + ".Text";
    private const string STATUS_HUD_BACKGROUND_COLOR = "0.06 0.08 0.11 0.82";
    private const string STATUS_HUD_FONT_NAME = "robotocondensed-bold.ttf";
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
    private const float STATUS_HUD_SCHEDULER_INTERVAL = 0.5f;
#if !CARBON
    private const string STATUS_HUD_PAYLOAD_SUFFIX =
      "\",\"fontSize\":15,\"align\":\"MiddleCenter\"," +
      "\"verticalOverflow\":\"Overflow\",\"font\":\"robotocondensed-bold.ttf\"," +
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
      public uint BuildingID;
      public BuildingPrivlidge CommandPrivilege;
      public float HudExpiresAt;
      public float HudRefreshAt;
      public float PrivilegeRefreshAt;
      public HudStateSnapshot Snapshot;
      public bool HasSnapshot;
      public bool IsVisible;

      public void EnterPool()
      {
        BuildingID = 0U;
        CommandPrivilege = null;
        HudExpiresAt = 0f;
        HudRefreshAt = 0f;
        PrivilegeRefreshAt = 0f;
        Snapshot = default;
        HasSnapshot = false;
        IsVisible = false;
      }

      public void LeavePool() { }
    }

    private readonly struct HudStateSnapshot
    {
      private readonly ulong TargetNetworkID;
      private readonly HUDProtectionState State;
      private readonly float Scale;
      private readonly long RemainingMinutes;
      private readonly long PenaltySeconds;

      public HudStateSnapshot(
        ulong targetNetworkID, HUDProtectionState state, float scale,
        long remainingMinutes, long penaltySeconds)
      {
        TargetNetworkID = targetNetworkID;
        State = state;
        Scale = scale;
        RemainingMinutes = remainingMinutes;
        PenaltySeconds = penaltySeconds;
      }

      public bool Matches(in HudStateSnapshot other) =>
        TargetNetworkID == other.TargetNetworkID &&
        State == other.State &&
        Scale == other.Scale &&
        RemainingMinutes == other.RemainingMinutes &&
        PenaltySeconds == other.PenaltySeconds;
    }

#endregion Types & Classes

#region Methods

    private void InitializeStatusHud()
    {
      var options = Configuration.StatusHud;
      if (!options.Enabled)
        return;

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
          nowRealtime + (playerID % 10UL) * 0.1f;
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
      _queuedStatusHudPlayerIds.Clear();
      _scheduledStatusHudPlayerIds.Clear();

      foreach (var player in BasePlayer.activePlayerList)
        HideStatusHud(player);

      foreach (var hudState in _hudStates.Values)
      {
        var state = hudState;
        Facepunch.Pool.Free(ref state);
      }

      _hudStates.Clear();
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
        hudState.BuildingID = 0;
        hudState.PrivilegeRefreshAt =
          nowRealtime + Configuration.StatusHud.RefreshInterval;

        return;
      }

      if (hudState is null)
      {
        hudState = Facepunch.Pool.Get<HudPlayerState>();
        _hudStates[playerID] = hudState;
      }

      if (!hasCommandPrivilege &&
          !Configuration.StatusHud.DisplayInTrustedPrivilege &&
          IsTrustedForProtectedEntity(player, protectedEntity))
      {
        HideStatusHud(player, hudState);
        hudState.BuildingID = protectedEntity is BuildingPrivlidge privilege ?
          privilege.buildingID : 0U;
        hudState.PrivilegeRefreshAt =
          nowRealtime + Configuration.StatusHud.RefreshInterval;
        return;
      }

      UpdateStatusHud(player, protectedEntity, hudState, nowUtc);
      hudState.PrivilegeRefreshAt =
        nowRealtime + Configuration.StatusHud.RefreshInterval;

      if (hasCommandPrivilege)
      {
        hudState.HudRefreshAt = System.Math.Min(
          nowRealtime + Configuration.StatusHud.RefreshInterval,
          hudState.HudExpiresAt);
      }
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
      HudPlayerState hudState, System.DateTime nowUtc)
    {
      if (!player || !protectedEntity)
        return;

      hudState ??= Facepunch.Pool.Get<HudPlayerState>();
      hudState.BuildingID = protectedEntity is BuildingPrivlidge privilege ?
        privilege.buildingID : 0U;
      var decision = EvaluateProtection(protectedEntity, null, nowUtc);
      if (Configuration.StatusHud.DisplayOnlyWhenProtectionActive &&
          (decision.Kind is not DamageDecisionKind.ApplyScale ||
           decision.Scale >= 1f))
      {
        HideStatusHud(player, hudState);
        _hudStates[player.userID.Get()] = hudState;
        return;
      }

      var snapshot = CreateStatusHudSnapshot(
        GetNetworkId(protectedEntity), in decision, nowUtc);
      if (hudState.IsVisible && hudState.HasSnapshot &&
          hudState.Snapshot.Matches(in snapshot))
        return;

#if CARBON
      RenderStatusHud(player, in decision, nowUtc);
#else
      var payload = BuildStatusHudPayload(in decision, nowUtc);
      CuiHelper.DestroyUi(player, STATUS_HUD_NAME);
      CuiHelper.AddUi(player, payload);
#endif
      hudState.Snapshot = snapshot;
      hudState.HasSnapshot = true;
      hudState.IsVisible = true;
      _hudStates[player.userID.Get()] = hudState;
    }

    private HudStateSnapshot CreateStatusHudSnapshot(
      ulong targetNetworkID, in DamageDecision decision, System.DateTime nowUtc)
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

      var penaltySeconds = 0L;
      if (options.ShowPenaltyTimer &&
          state is not HUDProtectionState.Decaying and not HUDProtectionState.Grief &&
          _lastOnline.TryGetValue(decision.TargetID, out var lastOnline) &&
          lastOnline.PenaltyEndTicks > nowUtc.Ticks)
      {
        penaltySeconds =
          (lastOnline.PenaltyEndTicks - nowUtc.Ticks) /
          System.TimeSpan.TicksPerSecond;
      }

      return new(
        targetNetworkID,
        state,
        decision.Scale,
        remainingMinutes,
        penaltySeconds);
    }

#if !CARBON
    private string BuildStatusHudPayload(
      in DamageDecision decision, System.DateTime nowUtc)
    {
      _hudBuilder.Clear();
      _hudBuilder.Append(_hudPayloadPrefix);
      AppendStatusHudText(in decision, nowUtc);
      _hudBuilder.Append(STATUS_HUD_PAYLOAD_SUFFIX);
      return _hudBuilder.ToString();
    }
#endif

#if CARBON
    private void RenderStatusHud(
      BasePlayer player, in DamageDecision decision, System.DateTime nowUtc)
    {
      _hudBuilder.Clear();
      AppendStatusHudText(in decision, nowUtc);
      CuiHandler.Destroy(STATUS_HUD_NAME, player);

      var lui = CreateCUI().v2;
      var panel = lui.CreatePanel(
        "Hud", _statusHudPosition, _statusHudOffset,
        STATUS_HUD_BACKGROUND_COLOR, STATUS_HUD_NAME);

      var text = lui.CreateText(
        panel, LuiPosition.Full, STATUS_HUD_TEXT_OFFSET,
        STATUS_HUD_HEADER_FONT_SIZE, STATUS_HUD_TEXT_COLOR, _hudBuilder.ToString(),
        UnityEngine.TextAnchor.MiddleCenter,
        STATUS_HUD_TEXT_NAME);

      text.SetTextFont(CUI.Handler.FontTypes.RobotoCondensedBold)
        .SetTextOverflow(UnityEngine.VerticalWrapMode.Overflow)
        .SetOutline(
          STATUS_HUD_OUTLINE_COLOR, new UnityEngine.Vector2(1f, -1f));

      lui.SendUi(player);
    }
#endif

    private void AppendStatusHudText(
      in DamageDecision decision, System.DateTime nowUtc)
    {
      var options = Configuration.StatusHud;
      var state = GetProtectionState(in decision);
      switch (state)
      {
        case HUDProtectionState.Protected:
        case HUDProtectionState.Partial:
          var percent = decision.Scale is 0f ? 100f : decision.Scale.ToPercent();
          AppendStatusHudHeader(GetColor(percent), STATUS_HUD_PROTECTED_TEXT);

          if (options.ShowProtectionPercentage)
          {
            _hudBuilder.Append("\n<size=")
            .Append(STATUS_HUD_BODY_FONT_SIZE)
            .Append("><color=")
            .Append(STATUS_HUD_SUBTEXT_COLOR)
            .Append('>')
            .Append(percent)
            .Append("% Protection</color></size>");
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
            _hudBuilder.Append("\n<size=")
            .Append(STATUS_HUD_BODY_FONT_SIZE)
            .Append("><color=")
            .Append(STATUS_HUD_SUBTEXT_COLOR)
            .Append(">+")
            .Append(decision.Scale.ToPercent())
            .Append("% Damage</color></size>");
          }
          break;

        case HUDProtectionState.Vulnerable:
        default:
          AppendStatusHudHeader(
            COLOR_GREEN,
            STATUS_HUD_VULNERABLE_TEXT);
          break;
      }

      if (decision.TargetID is 0UL)
        return;

      if (options.ShowRemainingTime &&
          state is HUDProtectionState.Protected or
          HUDProtectionState.Partial)
      {
        var remainingTime =
          decision.TargetScaleCache?.RemainingTime ?? System.TimeSpan.Zero;

        if (remainingTime != System.TimeSpan.Zero)
        {
          _hudBuilder.Append("<size=")
            .Append(STATUS_HUD_BODY_FONT_SIZE)
            .Append("> (");
          AppendRemainingTime(_hudBuilder, remainingTime);
          _hudBuilder.Append(")</size>");
        }
      }

      if (!options.ShowPenaltyTimer ||
        state is HUDProtectionState.Decaying
          or HUDProtectionState.Grief ||
          !_lastOnline.TryGetValue(
            decision.TargetID, out var lastOnline) ||
          lastOnline.PenaltyEndTicks <= nowUtc.Ticks)
        return;

      _hudBuilder.Append("\n<size=")
        .Append(STATUS_HUD_BODY_FONT_SIZE)
        .Append("><color=")
        .Append(STATUS_HUD_PENALTY_COLOR)
        .Append('>')
        .Append(STATUS_HUD_PENALTY_TEXT);
      AppendHudDuration(lastOnline.PenaltyEndTicks - nowUtc.Ticks);
      _hudBuilder.Append("</color></size>");
    }

    private void AppendStatusHudHeader(
      string color, string status)
    {
      _hudBuilder
        .Append("<b>")
        .Append(ORP_PREFIX_COLORED)
        .Append("<color=")
        .Append(color)
        .Append('>')
        .Append(status)
        .Append("</color></b>");
    }

    private void AppendHudDuration(long ticks)
    {
      var duration = System.TimeSpan.FromTicks(ticks);
      var totalHours = (long)duration.TotalHours;
      if (totalHours < 10L)
        _hudBuilder.Append('0');

      _hudBuilder.Append(totalHours).Append(':');
      AppendTwoDigits(_hudBuilder, duration.Minutes);
      _hudBuilder.Append(':');
      AppendTwoDigits(_hudBuilder, duration.Seconds);
    }

    private static void AppendTwoDigits(StringBuilder builder, int value)
    {
      builder.Append((char)('0' + value / 10));
      builder.Append((char)('0' + value % 10));
    }

    private static void AppendRemainingTime(
      StringBuilder builder, System.TimeSpan remainingTime)
    {
      builder.Append(remainingTime.Days).Append("d:")
        .Append(remainingTime.Hours).Append("h:")
        .Append(remainingTime.Minutes).Append("m");
    }

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
      _hudStates.Remove(playerID);
      Facepunch.Pool.Free(ref hudState);
    }

    private void RefreshStatusHudScheduler()
    {
      if (!Configuration.StatusHud.Enabled)
        return;

      if (BasePlayer.activePlayerList.Count is 0 && _hudStates.Count is 0)
        return;

      var nowRealtime = UnityEngine.Time.realtimeSinceStartup;
      var players = BasePlayer.activePlayerList;
      for (var i = 0; i < players.Count; i++)
      {
        var player = players[i];
        if (!player || !player.IsConnected)
          continue;
        var playerID = player.userID.Get();
        if (!_hudStates.TryGetValue(playerID, out var hudState))
        {
          hudState = Facepunch.Pool.Get<HudPlayerState>();
          _hudStates[playerID] = hudState;
        }
        if (nowRealtime < hudState.PrivilegeRefreshAt)
          continue;
        RefreshPlayerStatusHud(player, hudState);
      }

      _scheduledStatusHudPlayerIds.Clear();
      foreach (var (key, hudState) in _hudStates)
      {
        if (hudState.HudExpiresAt <= 0f)
          continue;

        if (!hudState.CommandPrivilege ||
            nowRealtime >= hudState.HudExpiresAt ||
            nowRealtime >= hudState.HudRefreshAt)
        {
          _scheduledStatusHudPlayerIds.Add(key);
        }
      }

      for (var i = 0; i < _scheduledStatusHudPlayerIds.Count; i++)
        RefreshScheduledStatusHudPlayer(_scheduledStatusHudPlayerIds[i]);
    }

    private void RefreshScheduledStatusHudPlayer(ulong playerID)
    {
      var player = _players.GetPlayer(playerID);
      if (!player || !player.IsConnected)
      {
        if (_hudStates.TryGetValue(playerID, out var hudState))
          RemoveStatusHud(player, playerID, hudState);
        return;
      }

      RefreshPlayerStatusHud(player);
    }

    private void QueueStatusHudRefresh(BasePlayer player)
    {
      if (!Configuration.StatusHud.Enabled || !player || !player.IsConnected)
        return;

      _queuedStatusHudPlayerIds.Add(player.userID.Get());
      if (_statusHudRefreshQueued)
        return;

      _statusHudRefreshQueued = true;
#if CARBON
      Community.Runtime.Core.NextFrame(RefreshQueuedStatusHuds);
#else
      NextFrame(RefreshQueuedStatusHuds);
#endif
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
      if (buildingID is 0U || !Configuration.StatusHud.Enabled)
        return;

      foreach (var (key, hudStates) in _hudStates)
      {
        if (hudStates.BuildingID == buildingID)
          _queuedStatusHudPlayerIds.Add(key);
      }

      if (_queuedStatusHudPlayerIds.Count is 0 || _statusHudRefreshQueued)
        return;

      _statusHudRefreshQueued = true;
#if CARBON
      Community.Runtime.Core.NextFrame(RefreshQueuedStatusHuds);
#else
      NextFrame(RefreshQueuedStatusHuds);
#endif
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

    private static bool NeedsTcTracking =>
      Configuration.StatusHud.Enabled ||
      Configuration.MapMarker.Enabled;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HUDProtectionState GetProtectionState(in DamageDecision decision) =>
      decision switch
      {
        { IsGrief: true } => HUDProtectionState.Grief,
        { IsDecaying: true } => HUDProtectionState.Decaying,
        { Kind: not DamageDecisionKind.ApplyScale } or { Scale: 1f } => HUDProtectionState.Vulnerable,
        { Scale: <= 0f } => HUDProtectionState.Protected,
        { Scale: < 1f } => HUDProtectionState.Partial,
        _ => HUDProtectionState.IncreasedDamage
      };

    private bool IsTrustedForCupboard(
      BasePlayer player, BuildingPrivlidge privilege)
    {
      if (!player || !privilege)
        return false;

      var authorizedPlayers = GetTotalAuthorizedPlayers(privilege);
      return IsTrustedForAuthorizedPlayers(player.userID.Get(), authorizedPlayers);
    }

    private BaseCombatEntity GetBoatStatusEntity(BasePlayer player)
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

      for (var i = 0; i < authorizedPlayers.Count; i++)
      {
        if (Configuration.Team.TeamShare &&
            ArePlayersRelated(playerID, authorizedPlayers[i]))
          return true;
      }

      return false;
    }

    private bool ArePlayersRelated(ulong firstPlayerID, ulong secondPlayerID)
    {
      if (firstPlayerID == secondPlayerID)
        return true;

      var team = GetTeam(firstPlayerID);
      if (team?.members.Contains(secondPlayerID) is true)
        return true;

      var firstPlayer = _players.GetPlayer(firstPlayerID);
      var secondPlayer = _players.GetPlayer(secondPlayerID);
      if (firstPlayer is { clanId: not 0 } &&
          secondPlayer is { clanId: not 0 } &&
          firstPlayer.clanId == secondPlayer.clanId)
        return true;

      if (Clans is null)
        return false;

      var firstTag = GetCachedClanTag(firstPlayerID);
      return !string.IsNullOrEmpty(firstTag) &&
        string.Equals(
          firstTag, GetCachedClanTag(secondPlayerID),
          System.StringComparison.Ordinal);
    }

#endregion Helper Methods

#endregion Status HUD

#region Map Markers

#region Fields

    private readonly Dictionary<ulong, TcMapMarkerGroup> _mapMarkersByCupboard = new();
    private readonly HashSet<ulong> _activeMarkerNetIds = new();
    private readonly HashSet<BaseNetworkable> _pendingMapMarkers = new();
    private readonly Queue<uint> _queuedBuildingMapMarkerSyncs = new(64);
    private readonly HashSet<uint> _queuedBuildingMapMarkerSyncIds = new(64);
    private readonly Queue<ulong> _queuedBoatMapMarkerSyncs = new(32);
    private readonly HashSet<ulong> _queuedBoatMapMarkerSyncIds = new(32);
    private readonly List<ulong> _activeMapMarkerCupboardIds = new(128);
    private int _mapMarkerRefreshIndex;
    private int _mapMarkerRefreshGeneration;
    private int _mapMarkerRefreshRemaining;
    private bool _mapMarkerRefreshActive;
    private bool _mapMarkerSyncQueued;
    private bool _mapMarkerRefreshQueued;
    private bool _forceRadiusReplay;
    private bool _mapMarkerRefreshForceRadiusReplay;
    private readonly StringBuilder _mapMarkerBuilder = new(512);
    private UnityEngine.Color _markerProtectedColor;
    private UnityEngine.Color _markerPartialColor;
    private UnityEngine.Color _markerVulnerableColor;
    private UnityEngine.Color _markerDecayingColor;
    private UnityEngine.Color _markerGriefColor;
    private UnityEngine.Color _markerOutlineColor;
#if CARBON
    private Oxide.Plugins.Timer _mapMarkerTimer;
    private Oxide.Plugins.Timer _boatMapMarkerTimer;
#else
    private Timer _mapMarkerTimer;
    private Timer _boatMapMarkerTimer;
#endif

#region Constants

    private const string MAP_MARKER_RADIUS_PREFAB =
      "assets/prefabs/tools/map/genericradiusmarker.prefab";

    private const string MAP_MARKER_VENDING_PREFAB =
      "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

    private const string MAP_MARKER_PROTECTED_TEXT = "PROTECTED ";
    private const string MAP_MARKER_DECAYING_TEXT = "DECAYING";
    private const string MAP_MARKER_GRIEF_TEXT = "GRIEF";
    private const string MAP_MARKER_INCREASED_DAMAGE_TEXT = "% DAMAGE";
    private const string MAP_MARKER_VULNERABLE_TEXT = "VULNERABLE";
    private const string MAP_MARKER_PENALTY_PREFIX = " \nPenalty | ";
    private const string MAP_MARKER_AUTHORIZED_PLAYERS_PREFIX = " \n";
    private const string MAP_MARKER_NO_AUTHORIZED_PLAYERS_TEXT = "None";
    private const string MAP_MARKER_AUTHORIZED_PLAYER_SEPARATOR = "\n";
    private const string MAP_MARKER_SHORT_TIME_FORMAT = "HH:mm";
    private const string MAP_MARKER_LONG_TIME_FORMAT = "dd.MM HH:mm";
    private const int MAP_MARKER_HASH_SEED = 17;
    private const int MAP_MARKER_HASH_MULTIPLIER = 31;
    private const int MAP_MARKER_SYNC_BATCH_SIZE = 16;
    private const int MAP_MARKER_REFRESH_BATCH_SIZE = 16;
    private const float MAP_MARKER_SEND_UPDATE_DELAY = 1f;
    private const float BOAT_MAP_MARKER_RADIUS_REFRESH_INTERVAL = 1f;

#endregion Constants

#endregion Fields

#region Classes

    private sealed class TcMapMarkerGroup : Facepunch.Pool.IPooled
    {
      public TcState TcState;
      public BaseCombatEntity ProtectedEntity;
      public BaseEntity ParentEntity;
      public bool IsBoat;
      public MapMarkerGenericRadius RadiusMarker;
      public VendingMachineMapMarker LabelMarker;
      public ulong RadiusMarkerNetworkID;
      public ulong LabelMarkerNetworkID;
      public HUDProtectionState ProtectionState;
      public HUDProtectionState LabelProtectionState;
      public string LabelText;
      public float LabelScale;
      public long LabelRemainingMinutes;
      public long LabelPenaltyEndTicks;
      public int LabelAuthorizedPlayerCount;
      public int LabelAuthorizedPlayerHash;
      public int RefreshListIndex;
      public int RefreshGeneration;

      public void EnterPool()
      {
        TcState = default;
        ProtectedEntity = null;
        ParentEntity = null;
        IsBoat = false;
        RadiusMarker = null;
        LabelMarker = null;
        RadiusMarkerNetworkID = 0UL;
        LabelMarkerNetworkID = 0UL;
        ProtectionState = default;
        LabelProtectionState = default;
        LabelText = null;
        LabelScale = 0f;
        LabelRemainingMinutes = 0L;
        LabelPenaltyEndTicks = 0L;
        LabelAuthorizedPlayerCount = 0;
        LabelAuthorizedPlayerHash = 0;
        RefreshListIndex = -1;
        RefreshGeneration = 0;
      }

      public void LeavePool() { }
    }

#endregion Classes

#region Methods

    private void InitializeMapMarkers()
    {
      if (!Configuration.MapMarker.Enabled)
        return;

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
      _activeMapMarkerCupboardIds.Clear();
      _mapMarkerRefreshIndex = 0;
      _mapMarkerRefreshRemaining = 0;
      _mapMarkerRefreshActive = false;
      _mapMarkerSyncQueued = false;
      _mapMarkerRefreshQueued = false;
      _forceRadiusReplay = false;
      _mapMarkerRefreshForceRadiusReplay = false;
      RemoveAllMapMarkers();
      _mapMarkerBuilder.Clear();
    }

    private void CacheMapMarkerColors()
    {
      var marker = Configuration.MapMarker;
      UnityEngine.ColorUtility.TryParseHtmlString(
        marker.ProtectedColor, out _markerProtectedColor);
      UnityEngine.ColorUtility.TryParseHtmlString(
        marker.PartialColor, out _markerPartialColor);
      UnityEngine.ColorUtility.TryParseHtmlString(
        marker.VulnerableColor, out _markerVulnerableColor);
      UnityEngine.ColorUtility.TryParseHtmlString(
        marker.DecayingColor, out _markerDecayingColor);
      UnityEngine.ColorUtility.TryParseHtmlString(
        marker.GriefColor, out _markerGriefColor);
      UnityEngine.ColorUtility.TryParseHtmlString(
        marker.OutlineColor, out _markerOutlineColor);
    }

    private void SyncBuildingMapMarker(uint buildingID)
    {
      if (Configuration?.MapMarker?.Enabled is not true || buildingID is 0U)
        return;

      var currentCupboardID =
        _tcCache.TryGetValue(buildingID, out var tcState) &&
        tcState.Privilege ? tcState.CupboardNetworkId : 0UL;

      _tmpIdsScratch.Clear();
      foreach (var (key, markerState) in _mapMarkersByCupboard)
      {
        if (markerState.TcState.Privilege?.buildingID == buildingID &&
            key != currentCupboardID)
          _tmpIdsScratch.Add(key);
      }

      foreach (var cupboardID in _tmpIdsScratch)
        RemoveMapMarker(cupboardID);

      if (currentCupboardID is not 0UL)
        SyncTcMapMarker(in tcState);
    }

    private void QueueBuildingMapMarkerSync(uint buildingID)
    {
      if (!Configuration.MapMarker.Enabled || buildingID is 0U)
        return;

      if (!_queuedBuildingMapMarkerSyncIds.Add(buildingID))
        return;

      _queuedBuildingMapMarkerSyncs.Enqueue(buildingID);
      if (_mapMarkerSyncQueued)
        return;

      QueueMapMarkerSyncProcessing();
    }

    private void QueueBoatMapMarkerSync(BaseVehicle boat)
    {
      if (!_serverInitialized || !Configuration.MapMarker.Enabled ||
          !Configuration.RaidProtection.ProtectBaseBoats || !boat)
        return;

      var boatNetworkID = GetNetworkId(boat);
      if (boatNetworkID is 0UL || !_queuedBoatMapMarkerSyncIds.Add(boatNetworkID))
        return;

      _queuedBoatMapMarkerSyncs.Enqueue(boatNetworkID);
      if (!_mapMarkerSyncQueued)
        QueueMapMarkerSyncProcessing();
    }

    private void QueueMapMarkerSyncProcessing()
    {
      _mapMarkerSyncQueued = true;
#if CARBON
      Community.Runtime.Core.NextFrame(ProcessQueuedMapMarkerSyncs);
#else
      NextFrame(ProcessQueuedMapMarkerSyncs);
#endif
    }

    private void QueueMapMarkerRefresh() =>
      QueueMapMarkerRefresh(forceRadiusReplay: false);

    private void QueueMapMarkerRefresh(bool forceRadiusReplay)
    {
      if (!Configuration.MapMarker.Enabled)
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
#if CARBON
      Community.Runtime.Core.NextFrame(ProcessQueuedMapMarkerRefresh);
#else
      NextFrame(ProcessQueuedMapMarkerRefresh);
#endif
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
            markerState, System.DateTime.UtcNow,
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

      if (_forceRadiusReplay)
        QueueMapMarkerRefresh();
    }

    private void StopMapMarkerRefresh()
    {
      _mapMarkerRefreshActive = false;
      _mapMarkerRefreshQueued = false;
      _mapMarkerRefreshForceRadiusReplay = false;
      _mapMarkerRefreshRemaining = 0;
    }

    private void SyncTcMapMarker(in TcState tcState)
    {
      if (!Configuration.MapMarker.Enabled ||
          !tcState.Privilege ||
          tcState.CupboardNetworkId is 0UL)
        return;

      if (_mapMarkersByCupboard.TryGetValue(
            tcState.CupboardNetworkId, out var existing))
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

      var vehiclePrivilege = boat is Tugboat tugboat ?
        tugboat.GetChildPrivilege() :
        ((PlayerBoat)boat).GetChildPrivilege();
      var privilegeNetworkID = GetNetworkId(vehiclePrivilege);
      if (!vehiclePrivilege || privilegeNetworkID is 0UL)
        return;

      if (_mapMarkersByCupboard.TryGetValue(
            privilegeNetworkID, out var existing))
      {
        existing.ProtectedEntity = boat;
        existing.ParentEntity = vehiclePrivilege;
        existing.IsBoat = true;
        UpdateMapMarkerState(existing, System.DateTime.UtcNow);
        return;
      }

      var tcState = default(TcState);
      SpawnMapMarkerGroup(
        boat, vehiclePrivilege, privilegeNetworkID, true, in tcState);
    }

    private void SpawnMapMarkerGroup(in TcState tcState) =>
      SpawnMapMarkerGroup(
        tcState.Privilege, tcState.Privilege, tcState.CupboardNetworkId,
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
      var decision = EvaluateProtection(protectedEntity, null, nowUtc);
      var protectionState = GetProtectionState(in decision);
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
      markerGroup.IsBoat = isBoat;
      markerGroup.RadiusMarker = radiusMarker;
      markerGroup.LabelMarker = labelMarker;
      markerGroup.ProtectionState = protectionState;
      var authorizedPlayers = GetMapMarkerAuthorizedPlayers(markerGroup);
      markerGroup.LabelText = BuildMapMarkerLabel(
        protectionState, in decision, nowUtc, authorizedPlayers);
      CacheMapMarkerLabelState(
        markerGroup, protectionState, in decision, nowUtc, authorizedPlayers);
      labelMarker.markerShopName = markerGroup.LabelText;
      SpawnMapMarkerEntity(radiusMarker);
      SpawnMapMarkerEntity(labelMarker);

      var radiusMarkerNetworkID = GetNetworkId(radiusMarker);
      var labelMarkerNetworkID = GetNetworkId(labelMarker);
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
      _activeMarkerNetIds.Add(radiusMarkerNetworkID);
      _activeMarkerNetIds.Add(labelMarkerNetworkID);

      radiusMarker.limitNetworking = false;
      labelMarker.limitNetworking = false;

      radiusMarker.SendNetworkUpdate();
      labelMarker.SendNetworkUpdate();

      timer.Once(MAP_MARKER_SEND_UPDATE_DELAY, () =>
      {
        if (radiusMarker)
          radiusMarker.SendUpdate();
      });

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

      if (markerState.LabelMarker)
      {
        markerState.LabelMarker.transform.position =
          markerState.ParentEntity.transform.position;
        markerState.LabelMarker.SendNetworkUpdate();
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
        else if (markerState.RadiusMarker)
        {
          markerState.RadiusMarker.transform.position =
            markerState.ParentEntity.transform.position;
          markerState.RadiusMarker.SendNetworkUpdate();
          markerState.RadiusMarker.SendUpdate();
        }
        return;
      }

      if (markerState.RadiusMarker)
      {
        markerState.RadiusMarker.transform.position =
          markerState.ParentEntity.transform.position;
        markerState.RadiusMarker.SendNetworkUpdate();
        markerState.RadiusMarker.SendUpdate();
        return;
      }

      var radiusMarker = GameManager.server.CreateEntity(
        MAP_MARKER_RADIUS_PREFAB,
        markerState.ParentEntity.transform.position) as MapMarkerGenericRadius;
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

      var radiusMarkerNetworkID = GetNetworkId(radiusMarker);
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

      timer.Once(MAP_MARKER_SEND_UPDATE_DELAY, () =>
      {
        if (radiusMarker)
          radiusMarker.SendUpdate();
      });
    }

    private void UpdateMapMarkerState(
      TcMapMarkerGroup markerState, System.DateTime nowUtc,
      bool forceRadiusReplay = false)
    {
      var protectedEntity = markerState.ProtectedEntity;
      if ((!markerState.RadiusMarker && !markerState.IsBoat) ||
          !markerState.LabelMarker || !protectedEntity)
        return;

      var decision = EvaluateProtection(protectedEntity, null, nowUtc);
      var protectionState = GetProtectionState(in decision);
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
        {
          timer.Once(MAP_MARKER_SEND_UPDATE_DELAY, () =>
          {
            if (radiusMarker)
              radiusMarker.SendUpdate();
          });
        }
        else
          radiusMarker.SendUpdate();
      }

      var shouldRefreshLabel = ShouldRefreshMapMarkerLabel(
        markerState, protectionState, in decision, nowUtc,
        out var authorizedPlayers);
      if (!shouldRefreshLabel && !forceRadiusReplay)
        return;

      var label = BuildMapMarkerLabel(
        protectionState, in decision, nowUtc, authorizedPlayers);
      markerState.LabelText = label;
      markerState.LabelMarker.markerShopName = label;
      markerState.LabelMarker.SendNetworkUpdate();
    }

    private bool ShouldRefreshMapMarkerLabel(
      TcMapMarkerGroup markerState,
      HUDProtectionState protectionState, in DamageDecision decision,
      System.DateTime nowUtc, out PlayerIdSet authorizedPlayers)
    {
      authorizedPlayers = GetMapMarkerAuthorizedPlayers(markerState);
      GetMapMarkerLabelState(
        protectionState, in decision, nowUtc, authorizedPlayers,
        out var remainingMinutes, out var penaltyEndTicks,
        out var authorizedPlayerCount,
        out var authorizedPlayerHash);

      if (markerState.LabelProtectionState == protectionState &&
          markerState.LabelScale == decision.Scale &&
          markerState.LabelRemainingMinutes == remainingMinutes &&
          markerState.LabelPenaltyEndTicks == penaltyEndTicks &&
          markerState.LabelAuthorizedPlayerCount == authorizedPlayerCount &&
          markerState.LabelAuthorizedPlayerHash == authorizedPlayerHash)
        return false;

      markerState.LabelProtectionState = protectionState;
      markerState.LabelScale = decision.Scale;
      markerState.LabelRemainingMinutes = remainingMinutes;
      markerState.LabelPenaltyEndTicks = penaltyEndTicks;
      markerState.LabelAuthorizedPlayerCount = authorizedPlayerCount;
      markerState.LabelAuthorizedPlayerHash = authorizedPlayerHash;
      return true;
    }

    private void CacheMapMarkerLabelState(
      TcMapMarkerGroup markerState,
      HUDProtectionState protectionState, in DamageDecision decision,
      System.DateTime nowUtc, PlayerIdSet authorizedPlayers)
    {
      GetMapMarkerLabelState(
        protectionState, in decision, nowUtc, authorizedPlayers,
        out markerState.LabelRemainingMinutes,
        out markerState.LabelPenaltyEndTicks,
        out markerState.LabelAuthorizedPlayerCount,
        out markerState.LabelAuthorizedPlayerHash);
      markerState.LabelProtectionState = protectionState;
      markerState.LabelScale = decision.Scale;
    }

    private void GetMapMarkerLabelState(
      HUDProtectionState protectionState,
      in DamageDecision decision, System.DateTime nowUtc,
      PlayerIdSet authorizedPlayers,
      out long remainingMinutes, out long penaltyEndTicks,
      out int authorizedPlayerCount,
      out int authorizedPlayerHash)
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

      authorizedPlayerCount = authorizedPlayers?.Count ?? 0;
      authorizedPlayerHash = MAP_MARKER_HASH_SEED;

      if (authorizedPlayers is null)
        return;

      for (var i = 0; i < authorizedPlayers.Count; i++)
      {
        var userID = authorizedPlayers[i];
        // Fold the player's Steam ID into the hash
        authorizedPlayerHash = authorizedPlayerHash *
          MAP_MARKER_HASH_MULTIPLIER + userID.GetHashCode();

        // Fold the player's Display Name into the hash
        authorizedPlayerHash = authorizedPlayerHash *
          MAP_MARKER_HASH_MULTIPLIER +
          (GetPlayerName(userID)?.GetHashCode() ?? 0);
      }
    }

    private UnityEngine.Color GetMapMarkerColor(
      HUDProtectionState protectionState) => protectionState switch
      {
        HUDProtectionState.Protected => _markerProtectedColor,
        HUDProtectionState.Partial => _markerPartialColor,
        HUDProtectionState.Decaying => _markerDecayingColor,
        HUDProtectionState.Grief => _markerGriefColor,
        _ => _markerVulnerableColor
      };

    private string BuildMapMarkerLabel(
      HUDProtectionState protectionState, in DamageDecision decision,
      System.DateTime nowUtc, PlayerIdSet authorizedPlayers)
    {
      _mapMarkerBuilder.Clear();

      // --- Line 1: Protection Status & Percentage + Remaining Time ---
      switch (protectionState)
      {
        case HUDProtectionState.Protected:
        case HUDProtectionState.Partial:
          var percent = decision.Scale <= 0f ? 100f : decision.Scale.ToPercent();
          _mapMarkerBuilder.Append(ORP_PREFIX)
            .Append(MAP_MARKER_PROTECTED_TEXT)
            .Append(percent)
            .Append('%');

          var remainingTime =
            decision.TargetScaleCache?.RemainingTime ?? System.TimeSpan.Zero;
          if (remainingTime != System.TimeSpan.Zero)
          {
            _mapMarkerBuilder.Append(" (");
            AppendRemainingTime(_mapMarkerBuilder, remainingTime);
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
          _mapMarkerBuilder.Append(ORP_PREFIX).Append('+')
            .Append(decision.Scale.ToPercent())
            .Append(MAP_MARKER_INCREASED_DAMAGE_TEXT);
          break;
        case HUDProtectionState.Vulnerable:
        default:
          _mapMarkerBuilder.Append(ORP_PREFIX)
            .Append(MAP_MARKER_VULNERABLE_TEXT);
          break;
      }

      // --- Line 2: Penalty End Time (if active) ---
      if (protectionState is not HUDProtectionState.Decaying and not HUDProtectionState.Grief &&
          decision.TargetID is not 0UL &&
          _lastOnline.TryGetValue(decision.TargetID, out var lastOnline) &&
          lastOnline.PenaltyEndTicks > nowUtc.Ticks)
      {
        var localEndTime = System.TimeZoneInfo.ConvertTimeFromUtc(
          lastOnline.PenaltyEndDT, _timeZone);
        var penaltyFormat = lastOnline.PenaltyEndTicks - nowUtc.Ticks >
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

    private PlayerIdSet GetTotalAuthorizedPlayers(BuildingPrivlidge privilege)
    {
      if (!privilege)
        return null;

      _tmpIdSetScratch.Clear();
      if (privilege.authorizedPlayers is not null)
      {
        foreach (var userID in privilege.authorizedPlayers)
          _tmpIdSetScratch.Add(userID);
      }

      if (Configuration.Team.IncludeWhitelistPlayers)
        _tmpIdSetScratch.AddRange(GetCodeLockWhitelistPlayers(privilege));

      return _tmpIdSetScratch;
    }

    private PlayerIdSet GetMapMarkerAuthorizedPlayers(
      TcMapMarkerGroup markerState)
    {
      if (!markerState.IsBoat)
        return GetTotalAuthorizedPlayers(markerState.TcState.Privilege);

      _tmpIdSetScratch.Clear();
      var (tugboat, modularBoat, vehicle) =
        GetVehicle(markerState.ProtectedEntity);

      return GetAuthorizedPlayers(
        markerState.ProtectedEntity, tugboat, modularBoat, vehicle, null,
        _tmpIdSetScratch, out _) && !_tmpIdSetScratch.Overflowed ?
          _tmpIdSetScratch : null;
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
    }

    private void RemoveMapMarker(ulong cupboardNetworkID)
    {
      if (!_mapMarkersByCupboard.Remove(
            cupboardNetworkID, out var markerState))
        return;

      RemoveActiveMapMarkerCupboardId(cupboardNetworkID, markerState);

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

      _tmpIdsScratch.Clear();
      foreach (var (markerID, markerState) in _mapMarkersByCupboard)
      {
        if (markerState.IsBoat && markerState.ProtectedEntity == boat)
          _tmpIdsScratch.Add(markerID);
      }

      foreach (var markerID in _tmpIdsScratch)
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
      _activeMapMarkerCupboardIds.Clear();
      _mapMarkerRefreshIndex = 0;
      _mapMarkerRefreshRemaining = 0;
      _activeMarkerNetIds.Clear();
      _pendingMapMarkers.Clear();
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
      if (marker)
        marker.Kill();
    }

#endregion Methods

#endregion Map Markers

#region Clans/Teams Integration

#region Clans/Teams Methods

    private string GetClanTag(ulong userID)
    {
      if (_clanTagCache.TryGetValue(userID, out var tag))
        return tag;

      // TODO: probably shouldn't assume that teams and plugin Clans are
      //  mutually exclusive  -HZ
      var team = GetTeam(userID);
      if (!(team?.members.Count > 0))
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

    private List<ulong> GetTeamMembers(ulong userID)
    {
      var team = GetTeam(userID);
      var player = _players.GetPlayer(userID);

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
      var teamFirstOffline = Configuration.Team.TeamFirstOffline;
      var comparisonValue =
        teamFirstOffline ? float.MinValue : float.MaxValue;

      for (var i = 0; i < members.Count; i++)
      {
        var memberID = members[i];
        if (!_lastOnline.TryGetValue(memberID, out var lastOnlineMember))
          continue;

        var memberMinutes = GetOfflineMinutes(lastOnlineMember, nowUtc);

        if (teamFirstOffline ?
              memberMinutes <= comparisonValue :
              memberMinutes >= comparisonValue)
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
      var memberID = ulong.Parse(userID);
      if (_clanMemberCache.TryGetValue(tag, out var clan))
        clan.Add(memberID);
      else
        CacheClan(tag);

      _clanTagCache[memberID] = tag;
    }

    private void OnClanMemberGone(string userID, string tag)
    {
      var memberID = ulong.Parse(userID);
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
        if (!_lastOnline.TryGetValue(memberID, out var member))
          continue;

        member.EnablePenalty(Configuration.Team.TeamPenaltyDuration);
        MarkDataDirty();
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
        if (!_lastOnline.TryGetValue(memberID, out var member))
          continue;

        member.EnablePenalty(Configuration.Team.TeamPenaltyDuration);
        MarkDataDirty();
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
      var hitCount = UnityEngine.Physics.RaycastNonAlloc(
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
        GetStatusText(decision.TargetID, decision.IsDecaying, isGrief: decision.IsGrief));

      if (TryGetTcState(entity, out var tcState))
        ShowStatusCommandHud(player, in tcState);
    }

    private void cmdHelp(BasePlayer player, string _command, string[] _args)
    {
      if (!player)
        return;

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
        $"Updated the {nameof(LastOnlineData)}.json file for {FillOnlineTimes()} players.";
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

      if (!double.TryParse(args[^1], out var hours))
      {
        ChatMessage(player, MESSAGE_INVALID_SYNTAX);
        return;
      }

      if (_lastOnline.TryGetValue(userID, out var target))
      {
        target.LastOnlineDT =
          target.LastOnlineDT.Subtract(System.TimeSpan.FromHours(hours));
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
        target.EnablePenalty(duration);
        MarkDataDirty();
        ChatMessage(player,
          $"{target.UserName} | Penalty until {System.TimeZoneInfo.ConvertTimeFromUtc(target.PenaltyEndDT, _timeZone)}");
      }
      else
      {
        target.DisablePenalty();
        MarkDataDirty();
        ChatMessage(player, $"{target.UserName} | Penalty disabled");
      }

      CacheDamageScale(userID, -1f);
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

      var hasUpdates = false;
      if (args?.Length is 0 or 1)
      {
        _ray.origin = player.eyes.position;
        _ray.direction = player.eyes.HeadForward();
        var hitCount = UnityEngine.Physics.RaycastNonAlloc(
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

        hasUpdates = GetGriefCupboardState(tcState.Privilege, griefState);
        if (hasUpdates)
          CacheGriefCupboards();

        return;
      }

      for (var i = 1; i < args?.Length; i++)
      {
        var argument = args[i];
        if (!ulong.TryParse(argument, out var cupboardNetworkId) ||
            cupboardNetworkId is 0UL)
        {
          ChatMessage(player, $"'{argument}' is not a valid Tool Cupboard network ID");
          continue;
        }

        if (BaseNetworkable.serverEntities.Find(
              new NetworkableId(cupboardNetworkId)) is not BuildingPrivlidge toolCupboard)
        {
          ChatMessage(player, $"{cupboardNetworkId} is not a Tool Cupboard");
          continue;
        }

        hasUpdates |= GetGriefCupboardState(toolCupboard, griefState);
      }

      if (hasUpdates)
        CacheGriefCupboards();

      return;

      bool GetGriefCupboardState(
        BuildingPrivlidge toolCupboard, TcGriefState? requestedState = null)
      {
        var cupboardNetworkId = GetNetworkId(toolCupboard);
        if (cupboardNetworkId is 0UL)
        {
          ChatMessage(player, "Tool Cupboard has no valid network ID");
          return false;
        }

        var dataCreated = false;
        if (!_tcCreationData.TryGetValue(cupboardNetworkId, out var tcData))
        {
          tcData = new();
          _tcCreationData[cupboardNetworkId] = tcData;
          dataCreated = true;
        }

        var stateChanged = requestedState.HasValue &&
          tcData.GriefState != requestedState.Value;
        if (requestedState.HasValue)
          tcData.GriefState = requestedState.Value;

        if (dataCreated || stateChanged)
          MarkDataDirty();

        _sb.Clear();
        _sb.AppendLine($"<color={COLOR_BLUE}>Grief Status</color> Tool Cupboard[{cupboardNetworkId}]");

        if (requestedState.HasValue)
          _sb.AppendLine($"<color={COLOR_YELLOW}>Forced Grief State</color> {GetGriefStateName(requestedState.Value)}");
        else if (tcData.GriefState is TcGriefState.None)
          _sb.AppendLine($"<color={COLOR_YELLOW}>Grief State</color> {GetGriefStateName(_griefCupboardIds.Contains(cupboardNetworkId) ? TcGriefState.ForceTrue : TcGriefState.ForceFalse)}");
        else
          _sb.AppendLine($"<color={COLOR_YELLOW}>Forced Grief State</color> {GetGriefStateName(tcData.GriefState)}");

        ChatMessage(player, _sb.ToString());
        return true;
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
        $"Updated the {nameof(LastOnlineData)}.json file for {FillOnlineTimes()} players.";
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
      foreach (var (userID, playerData) in _scaleCache)
      {
        playerData.HasPermission =
          userID.HasPermission(Configuration.Permission.Protect);
      }
      Reply(arg, "Updated the permission status for all players.");
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
      ulong userID, bool isDecaying = false, bool ownerOnly = false, bool isGrief = false)
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
        var prot = scale.ToPercent();
        if (scale is not -1)
        {
          _sb.AppendLine(
            $"<color={COLOR_AQUA}>Scale</color> {scale} ({(prot >= 0f ? $"{prot}% Protection" : $"+{-prot}% Damage")})");
        }
      }

      return _sb.ToString();
    }

    private void AppendTeamOrClanMembersStatus(ulong userID)
    {
      if (!Configuration.Team.TeamShare)
        return;

      var tag = Clans is not null ? GetClanTag(userID) : null;
      var members = string.IsNullOrEmpty(tag) ?
        GetTeamMembers(userID) : GetClanMembers(tag);

      if (!(members?.Count > 1))
        return;

      _sb.AppendLine($"<color={COLOR_DARK_GREEN}>{(Clans is not null ?
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
          var scalePercent = $"{absoluteTimeScale[key].ToPercent()}";
          var hours = key.ToString();

          _sb.AppendLine($"<color={COLOR_ORANGE}>At {hours} o'clock</color>: {(scalePercent.ToFloat() >= 0f ? $"{scalePercent}% Protection" : $"+{-scalePercent.ToFloat()}% Damage")}");
        }
      }

      if (hasDamageKeys)
      {
        var interimDamageScalePercent =
          Configuration.RaidProtection.InterimDamage.ToPercent();
        if (Configuration.RaidProtection.CooldownMinutes > 0)
        {
          _sb.AppendLine($"<color={COLOR_ORANGE}>First {Configuration.RaidProtection.CooldownMinutes} minutes</color>: 0% Protection")
            .AppendLine($"<color={COLOR_ORANGE}>Between {Configuration.RaidProtection.CooldownMinutes} minutes and {damageScaleKeys[0]} hours</color>: {interimDamageScalePercent}% Protection");
        }
        else
          _sb.AppendLine($"<color={COLOR_ORANGE}>First {damageScaleKeys[0]} hour(s)</color>: {interimDamageScalePercent}% Protection");

        foreach (var key in damageScaleKeys)
        {
          var scalePercent = $"{damageScale[key].ToPercent()}";
          _sb.AppendLine($"<color={COLOR_ORANGE}>After {key} hours</color>: {(scalePercent.ToFloat() >= 0f ? $"{scalePercent}% Protection" : $"+{-scalePercent.ToFloat()}% Damage")}");
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

    private bool IsBuildingDecaying(
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
    private static ulong GetNetworkId(BaseNetworkable networkable) =>
      networkable?.net?.ID.Value ?? 0UL;

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
      if (!TryEnableApiPenalty(playerID, durationHours))
        return false;

      MarkDataDirty();
      return true;
    }

    public bool API_DisablePenalty(ulong playerID)
    {
      if (!TryDisableApiPenalty(playerID))
        return false;

      MarkDataDirty();
      return true;
    }

    public int API_EnablePenalties(
      ICollection<ulong> playerIDs, float durationHours)
    {
      if (!IsApiPenaltyDurationValid(durationHours) ||
          playerIDs is null || playerIDs.Count is 0)
        return 0;

      _tmpIdsScratch.Clear();
      foreach (var playerID in playerIDs)
        _tmpIdsScratch.Add(playerID);

      var changedPlayers = 0;
      foreach (var playerID in _tmpIdsScratch)
      {
        if (TryEnableApiPenalty(playerID, durationHours))
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
        if (TryDisableApiPenalty(playerID))
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
      if (!IsApiPenaltyActive(lastOnline, nowUtc) &&
          playerID.HasPermission(Configuration.Permission.Protect))
        return GetDamageScale(
          playerID, lastOnline, null, nowUtc, out _, out damageScaleKeys);

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
        GetOfflineHoursUnchecked(lastOnline, nowUtc);
      return remainingHours > 0f ?
        System.TimeSpan.FromHours(remainingHours).Ticks : 0L;
    }

    private static bool IsApiPenaltyActive(
      LastOnlineData lastOnline, System.DateTime nowUtc) =>
        lastOnline is not null && nowUtc.Ticks <= lastOnline.PenaltyEndTicks;

    private static bool IsApiPenaltyDurationValid(float durationHours) =>
      durationHours > 0f && !float.IsNaN(durationHours) &&
      !float.IsInfinity(durationHours);

    private bool TryEnableApiPenalty(ulong playerID, float durationHours)
    {
      if (!IsApiPenaltyDurationValid(durationHours) ||
          !_lastOnline.TryGetValue(playerID, out var lastOnline))
        return false;

      lastOnline.EnablePenalty(durationHours);
      CacheDamageScale(playerID, -1f);
      return true;
    }

    private bool TryDisableApiPenalty(ulong playerID)
    {
      if (!_lastOnline.TryGetValue(playerID, out var lastOnline) ||
          lastOnline.PenaltyEndTicks is 0L)
        return false;

      lastOnline.DisablePenalty();
      CacheDamageScale(playerID, -1f);
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
      public ScheduledTimescaleEntryKind EntryKind =
        ScheduledTimescaleEntryKind.Absolute;
      public ScheduledTimescaleEditContext ProfileEditor;
      public ScheduledTimescaleEntryEditContext EntryEditor;
      public ScheduledTimescaleScaleEditContext ScaleEditor;
      public string Notice;
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
      public ScheduledTimescaleEntryKind EntryKind =
        ScheduledTimescaleEntryKind.Absolute;
      public ScheduledTimescaleEntryEditContext EntryEditor;
      public bool IsDirty;
      public string Name;
      public string StartHours;
      public string EndHours;
      public string Error;
    }

    private sealed class ScheduledTimescaleEditContext
    {
      public ScheduledTimescale Profile;
      public ScheduledTimescale Draft;
      public System.DateTime StartDate;
      public System.DateTime EndDate;
      public string StartTime;
      public string EndTime;
      public int ReturnPage;
      public string Error;
      public string InvalidField;
    }

    private sealed class ScheduledTimescaleEntryEditContext
    {
      public ScheduledTimescaleScaleDraft Draft;
      public ScheduledTimescaleEntryKind Kind;
      public bool HasExistingKey;
      public int ExistingAbsoluteHour;
      public float ExistingOfflineHours;
      public string Key;
      public string Scale;
      public int ReturnPage;
      public string Error;
      public string InvalidField;
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
        NormalizeWipeTemplates();
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

    private void NormalizeWipeTemplates()
    {
      _wipeTemplatesData ??= new();
      _wipeTemplatesData.Templates ??= new();
      _wipeTemplatesData.GeneratedProfileIDs ??= new();

      var ids = new HashSet<System.Guid>();
      for (var i = _wipeTemplatesData.Templates.Count - 1; i >= 0; i--)
      {
        var template = _wipeTemplatesData.Templates[i];
        if (template is null)
        {
          _wipeTemplatesData.Templates.RemoveAt(i);
          continue;
        }

        if (template.ID == System.Guid.Empty || !ids.Add(template.ID))
          template.ID = System.Guid.NewGuid();

        template.Name = template.Name?.Trim();
        template.Phases ??= new();
        for (var phaseIndex = template.Phases.Count - 1;
             phaseIndex >= 0; phaseIndex--)
        {
          var phase = template.Phases[phaseIndex];
          if (!IsValidWipeTemplatePhase(phase))
            template.Phases.RemoveAt(phaseIndex);
          else
          {
            phase.AbsoluteTimeScale ??= new();
            phase.OfflineTimeScale ??= new();
          }
        }
        template.Phases.Sort(CompareWipeTemplatePhases);
      }

      if (GetWipeTemplate(_wipeTemplatesData.DefaultTemplateID) is null)
        _wipeTemplatesData.DefaultTemplateID = System.Guid.Empty;
      if (GetWipeTemplate(_wipeTemplatesData.QueuedNextWipeTemplateID) is null)
        _wipeTemplatesData.QueuedNextWipeTemplateID = System.Guid.Empty;
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

    private void CacheDefaultTimescales() =>
      _defaultTimeScales = new(
        Configuration.RaidProtection.AbsoluteTimeScale,
        Configuration.RaidProtection.DamageScale);

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

#region Wipe Template Runtime

#endregion Wipe Template Runtime

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
          ChatMessage(player, "Usage: /orp.schedule <true|false>");
          return;
        case 1:
          {
            if (!bool.TryParse(args?[0], out var enabled))
            {
              ChatMessage(player, "Usage: /orp.schedule <true|false>");
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
    }

    private void OpenScheduledTimescaleEditor(BasePlayer player)
    {
      if (!Configuration.RaidProtection.EnableScheduledTimescales)
      {
#if CARBON
        ChatMessage(player, "Scheduled timescales are disabled in the configuration. Use /orp.schedule true to enable them.");
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

      return nowUtcTicks < profile.StartUtcTicks ? "UPCOMING" :
        nowUtcTicks < profile.EndUtcTicks ? "ACTIVE" : "EXPIRED";
    }

    private static string GetScheduledTimescaleCommandStatusColor(
      ScheduledTimescale profile, long nowUtcTicks)
    {
      if (!string.IsNullOrEmpty(profile.InvalidReason))
        return COLOR_RED;

      return nowUtcTicks < profile.StartUtcTicks ? COLOR_ORANGE :
        nowUtcTicks < profile.EndUtcTicks ? COLOR_GREEN : COLOR_WHITE;
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
              null, _ => AdminModule.Tab.OptionButton.Types.Important);
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
          null, _ => AdminModule.Tab.OptionButton.Types.Important);

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
            _ => AdminModule.Tab.OptionButton.Types.Important,
            UnityEngine.TextAnchor.MiddleLeft);
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
        }, _ => AdminModule.Tab.OptionButton.Types.Selected);

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
        }, _ => AdminModule.Tab.OptionButton.Types.Warned);

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
            }, _ => AdminModule.Tab.OptionButton.Types.Important));
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
              }, _ => AdminModule.Tab.OptionButton.Types.Important));
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
          }, _ => AdminModule.Tab.OptionButton.Types.Selected),
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
          }, _ => AdminModule.Tab.OptionButton.Types.Important));

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
        }, _ => AdminModule.Tab.OptionButton.Types.Warned);


      // COLUMN 1: Action Group; each move has left/right previous/next actions.
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(UI_PREVIOUS,
          current => MoveScheduledTimescaleProfile(tab, current, profile,
            ScheduledTimescaleMoveOffset.PreviousDay),
          _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_MOVE_DAY, null,
          _ => AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_NEXT,
          current => MoveScheduledTimescaleProfile(tab, current, profile,
            ScheduledTimescaleMoveOffset.NextDay),
          _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_PREVIOUS,
          current => MoveScheduledTimescaleProfile(tab, current, profile,
            ScheduledTimescaleMoveOffset.PreviousWeek),
          _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_MOVE_WEEK, null,
          _ => AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_NEXT,
          current => MoveScheduledTimescaleProfile(tab, current, profile,
            ScheduledTimescaleMoveOffset.NextWeek),
          _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_PREVIOUS,
          current => MoveScheduledTimescaleProfile(tab, current, profile,
            ScheduledTimescaleMoveOffset.PreviousMonth),
          _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_MOVE_MONTH, null,
          _ => AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_NEXT,
          current => MoveScheduledTimescaleProfile(tab, current, profile,
            ScheduledTimescaleMoveOffset.NextMonth),
          _ => AdminModule.Tab.OptionButton.Types.Selected));


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
          _ => AdminModule.Tab.OptionButton.Types.Important,
          UnityEngine.TextAnchor.MiddleLeft);
      }

      if (!string.IsNullOrEmpty(uiState.Notice))
      {
        // COLUMN 1: Button; visible text uiState.Notice; non-clickable transient
        // notice, including the unsaved-changes warning
        tab.AddButton(DETAILS_COLUMN, uiState.Notice, null,
          _ => AdminModule.Tab.OptionButton.Types.Important,
          UnityEngine.TextAnchor.MiddleLeft);
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
        }, _ => AdminModule.Tab.OptionButton.Types.Selected);


      var replaceStandardValuesButton = new AdminModule.Tab.OptionButton(
        UI_REPLACE_STANDARD_VALUES,
        current => ReplaceScheduledTimescaleStandardValues(
          tab, current, profile, uiState.EntryKind),
        _ => AdminModule.Tab.OptionButton.Types.Important);


      if (uiState.ScaleEditor?.IsDirty is true)
      {
        // COLUMN 1: Action Group; visible text UI_SAVE_SCALE_CHANGES, UI_ADD_SCALE,
        // and UI_REPLACE_STANDARD_VALUES; dynamic dirty state adds Save first
        tab.AddButtonArray(DETAILS_COLUMN,
          new AdminModule.Tab.OptionButton(UI_SAVE_SCALE_CHANGES,
            current => SaveScheduledTimescaleScaleChanges(tab, current),
            _ => AdminModule.Tab.OptionButton.Types.Selected),
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
        UnityEngine.TextAnchor.MiddleLeft,
        edit),
        new AdminModule.Tab.OptionButton(
            scaleLabel,
            UnityEngine.TextAnchor.MiddleLeft,
            edit),
        new AdminModule.Tab.OptionButton(
          UI_COPY, current => CopyScheduledTimescaleEntry(
            tab, current, profile, kind, key),
          _ => AdminModule.Tab.OptionButton.Types.Selected),
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
          }, _ => AdminModule.Tab.OptionButton.Types.Important));
    }

#endregion Shedules Tab

#region Templates Tab

    private void RegisterWipeTemplateAdminTab()
    {
      if (_admin is null || _wipeTemplateTab is not null)
        return;

      _wipeTemplateTab = new WipeTemplateAdminTab(this,
        (session, tab) =>
        {
          if (!CanUseScheduledTimescaleEditor(session))
            return;

          Community.Runtime.Core.NextFrame(() =>
          {
            if (session.Player && HasWipeTemplateEditorModules())
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

      if (!_wipeTemplatePlayerTabs.TryGetValue(player.userID, out var tab))
      {
        tab = new WipeTemplateAdminTab(this);
        _wipeTemplatePlayerTabs[player.userID] = tab;
      }

      var session = _admin.GetPlayerSession(player);
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
          _ => AdminModule.Tab.OptionButton.Types.Important);
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
            GetWipeTemplateUiState(current.Player).SelectedTemplateID = template.ID;
            DrawWipeTemplateAdminTab(tab, current);
          };
        tab.AddButton(PROFILE_COLUMN, template.Name,
          select, _ => state.SelectedTemplateID == template.ID ?
            AdminModule.Tab.OptionButton.Types.Warned :
            activeTemplatePhase is not null ?
              AdminModule.Tab.OptionButton.Types.Selected :
              AdminModule.Tab.OptionButton.Types.None);
      }

      System.Action<AdminModule.PlayerSession> addTemplate =
        isTemplateEditing ? null : current =>
      {
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
        _ => AdminModule.Tab.OptionButton.Types.Selected);

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
        var lastEndTicks = 0L;
        foreach (var phase in selected.Phases)
        {
          if (phase is not null && phase.EndOffsetTicks > lastEndTicks)
            lastEndTicks = phase.EndOffsetTicks;
        }

        GetWipeTemplateUiState(current.Player).PhaseEditor =
          CreateWipeTemplatePhaseEditContext(selected, new()
          {
            Name = DEFAULT_TEMPLATE_PHASE_NAME,
            StartOffsetTicks = lastEndTicks,
            EndOffsetTicks = checked(lastEndTicks +
              DEFAULT_TEMPLATE_PHASE_DURATION_TICKS),
            AbsoluteTimeScale = new(Configuration.RaidProtection.AbsoluteTimeScale),
            OfflineTimeScale = new(Configuration.RaidProtection.DamageScale)
          });
        DrawWipeTemplateAdminTab(tab, current);
      }, _ => AdminModule.Tab.OptionButton.Types.Warned);

      // COLUMN 1: Deletes the selected template through a confirmation dialog
      // and clears either persisted selection that pointed to it
      tab.AddButton(DETAILS_COLUMN, UI_DELETE_TEMPLATE, current =>
      {
        tab.CreateDialog(string.Format(UI_DELETE_TEMPLATE_FORMAT,
          selected.Name), confirm =>
        {
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
      }, _ => AdminModule.Tab.OptionButton.Types.Important);
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
          _ => AdminModule.Tab.OptionButton.Types.Warned));


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
          _ => AdminModule.Tab.OptionButton.Types.Warned));


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
          _ => AdminModule.Tab.OptionButton.Types.Important,
          UnityEngine.TextAnchor.MiddleLeft);
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
          _ => AdminModule.Tab.OptionButton.Types.Selected));
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
          context.Name = GetScheduledTimescaleInput(args);
          context.IsDirty = true;
          context.Error = null;
          DrawWipeTemplateAdminTab(tab, current);
        });
      tab.AddInput(DETAILS_COLUMN, UI_START_OFFSET_HOURS, _ => context.StartHours,
        SCALE_VALUE_MAX_LENGTH, false, (current, args) =>
        {
          context.StartHours = GetScheduledTimescaleInput(args);
          context.IsDirty = true;
          context.Error = null;
          DrawWipeTemplateAdminTab(tab, current);
        });
      tab.AddInput(DETAILS_COLUMN, UI_END_OFFSET_HOURS, _ => context.EndHours,
        SCALE_VALUE_MAX_LENGTH, false, (current, args) =>
        {
          context.EndHours = GetScheduledTimescaleInput(args);
          context.IsDirty = true;
          context.Error = null;
          DrawWipeTemplateAdminTab(tab, current);
        });

      var cancelButton = new AdminModule.Tab.OptionButton(UI_CANCEL, current =>
        {
          GetWipeTemplateUiState(current.Player).PhaseEditor = null;
          DrawWipeTemplateAdminTab(tab, current);
        });
      var deleteButton = new AdminModule.Tab.OptionButton(UI_DELETE, current =>
          {
            if (context.StoredPhase is null ||
                !context.Template.Phases.Remove(context.StoredPhase))
              return;

            SaveWipeTemplates();
            GetWipeTemplateUiState(current.Player).PhaseEditor = null;
            DrawWipeTemplateAdminTab(tab, current);
          }, _ => AdminModule.Tab.OptionButton.Types.Important);
      if (context.IsDirty)
        tab.AddButtonArray(DETAILS_COLUMN,
          new AdminModule.Tab.OptionButton(UI_SAVE,
            current => SaveWipeTemplatePhase(tab, current, context),
            _ => AdminModule.Tab.OptionButton.Types.Selected),
          cancelButton, deleteButton);
      else
        tab.AddButtonArray(DETAILS_COLUMN, cancelButton, deleteButton);

      if (!string.IsNullOrEmpty(context.Error))
        tab.AddButton(DETAILS_COLUMN, context.Error, null,
          _ => AdminModule.Tab.OptionButton.Types.Important);

      tab.AddName(DETAILS_COLUMN,
        string.Format(UI_TEMPLATE_SCALE_COUNT_FORMAT,
          context.Phase.AbsoluteTimeScale.Count,
          context.Phase.OfflineTimeScale.Count));

      // COLUMN 1: Scale actions; Add Scale edits the draft, while Replace
      // Standard Values copies the current configuration into that draft
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(UI_ADD_SCALE, current =>
        {
          context.EntryEditor = CreateScheduledTimescaleEntryEditContext(
            context.ScaleDraft, context.EntryKind, null,
            current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);
          DrawWipeTemplateAdminTab(tab, current);
        }, _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_REPLACE_STANDARD_VALUES, current =>
        {
          context.ScaleDraft.AbsoluteTimeScale.Clear();
          context.ScaleDraft.OfflineTimeScale.Clear();
          foreach (var (key, value) in Configuration.RaidProtection.AbsoluteTimeScale)
            context.ScaleDraft.AbsoluteTimeScale.Add(key, value);
          foreach (var (key, value) in Configuration.RaidProtection.DamageScale)
            context.ScaleDraft.OfflineTimeScale.Add(key, value);
          context.ScaleDraft.RefreshKeys();
          context.IsDirty = true;
          DrawWipeTemplateAdminTab(tab, current);
        }, _ => AdminModule.Tab.OptionButton.Types.Important));

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
          context.EntryKind = ScheduledTimescaleEntryKind.Absolute;
          DrawWipeTemplateAdminTab(tab, current);
        }, _ => kind is ScheduledTimescaleEntryKind.Absolute ?
          AdminModule.Tab.OptionButton.Types.Warned :
          AdminModule.Tab.OptionButton.Types.None),
        new AdminModule.Tab.OptionButton(UI_OFFLINE_TIME, current =>
        {
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
        context.EntryEditor = CreateScheduledTimescaleEntryEditContext(
          context.ScaleDraft, kind, key,
          current.GetOrCreatePage(DETAILS_COLUMN).CurrentPage);
        DrawWipeTemplateAdminTab(tab, current);
      };
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(keyLabel,
          UnityEngine.TextAnchor.MiddleLeft, edit),
        new AdminModule.Tab.OptionButton(scaleLabel,
          UnityEngine.TextAnchor.MiddleLeft, edit),
        new AdminModule.Tab.OptionButton(UI_COPY, current =>
        {
          if (!TryCopyScheduledTimescaleEntry(
                context.ScaleDraft, kind, key))
            return;

          context.ScaleDraft.RefreshKeys();
          context.IsDirty = true;
          DrawWipeTemplateAdminTab(tab, current);
        }, _ => AdminModule.Tab.OptionButton.Types.Selected),
        new AdminModule.Tab.OptionButton(UI_DELETE, current =>
        {
          RemoveScheduledTimescaleEntry(context.ScaleDraft, kind, key);
          context.ScaleDraft.RefreshKeys();
          context.IsDirty = true;
          DrawWipeTemplateAdminTab(tab, current);
        }, _ => AdminModule.Tab.OptionButton.Types.Important));
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
      tab.AddName(DETAILS_COLUMN,
        !context.HasExistingKey ?
          (absolute ? UI_NEW_ABSOLUTE_SCALE : UI_NEW_OFFLINE_SCALE) :
          (absolute ? UI_EDIT_ABSOLUTE_SCALE : UI_EDIT_OFFLINE_SCALE));


      // COLUMN 1: Input Field; visible text UI_HOUR or UI_OFFLINE_HOURS, with an
      // invalid-field prefix when needed; input changes context.Key and redraws
      tab.AddInput(DETAILS_COLUMN,
        context.InvalidField is FIELD_ENTRY_KEY ?
          UI_INVALID_FIELD_PREFIX +
            (absolute ? UI_HOUR : UI_OFFLINE_HOURS) :
          absolute ? UI_HOUR : UI_OFFLINE_HOURS,
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
          _ => AdminModule.Tab.OptionButton.Types.Important,
          UnityEngine.TextAnchor.MiddleLeft);
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
          _ => AdminModule.Tab.OptionButton.Types.Selected));
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

    private bool TryCopyScheduledTimescaleEntry(
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

    private bool RemoveScheduledTimescaleEntry(
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
      tab.AddName(DETAILS_COLUMN, !context.HasExistingKey ?
        (absolute ? UI_NEW_ABSOLUTE_SCALE : UI_NEW_OFFLINE_SCALE) :
        (absolute ? UI_EDIT_ABSOLUTE_SCALE : UI_EDIT_OFFLINE_SCALE));
      tab.AddInput(DETAILS_COLUMN,
        context.InvalidField is FIELD_ENTRY_KEY ? UI_INVALID_FIELD_PREFIX +
          (absolute ? UI_HOUR : UI_OFFLINE_HOURS) :
          (absolute ? UI_HOUR : UI_OFFLINE_HOURS),
        _ => context.Key, SCALE_VALUE_MAX_LENGTH, false, (current, args) =>
        {
          context.Key = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawWipeTemplateAdminTab(tab, current);
        });
      tab.AddInput(DETAILS_COLUMN,
        context.InvalidField is FIELD_ENTRY_SCALE ?
          UI_INVALID_FIELD_PREFIX + UI_SCALE : UI_SCALE,
        _ => context.Scale, SCALE_VALUE_MAX_LENGTH, false, (current, args) =>
        {
          context.Scale = GetScheduledTimescaleInput(args);
          ClearScheduledTimescaleValidation(context);
          DrawWipeTemplateAdminTab(tab, current);
        });
      if (!string.IsNullOrEmpty(context.Error))
        tab.AddButton(DETAILS_COLUMN, context.Error, null,
          _ => AdminModule.Tab.OptionButton.Types.Important);
      tab.AddButtonArray(DETAILS_COLUMN,
        new AdminModule.Tab.OptionButton(UI_CANCEL, current =>
        {
          phaseContext.EntryEditor = null;
          DrawWipeTemplateAdminTab(tab, current);
        }),
        new AdminModule.Tab.OptionButton(UI_SAVE, current =>
          SaveWipeTemplatePhaseEntry(tab, current, phaseContext),
          _ => AdminModule.Tab.OptionButton.Types.Selected));
    }

    private void SaveWipeTemplatePhaseEntry(AdminModule.Tab tab,
      AdminModule.PlayerSession session,
      WipeTemplatePhaseEditContext phaseContext)
    {
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
  public static class ExtensionMethods
  {
    private static readonly Permission P;

    static ExtensionMethods() => P = Interface.Oxide.GetLibrary<Permission>();

    private static bool HasPermission(this string userID, string permission)
      => !string.IsNullOrEmpty(userID) &&
         P.UserHasPermission(userID, permission);

    public static bool HasPermission(
      this BasePlayer player, string permission) =>
      player.UserIDString.HasPermission(permission);

    public static bool HasPermission(this ulong userID, string permission)
      => userID.ToString().HasPermission(permission);

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
 
using Oxide.Game.Rust.Cui;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins;

[Info("CCTV GUI", "HunterZ", "1.0.0")]
public class CCTVGUI : RustPlugin
{
  // (full location, CCTV netIDs) by short location
  private SortedDictionary<string, (string, HashSet<ulong>)> _locationData =
    new();
  // CCTV (rcID, short location) by netID
  private Dictionary<ulong, (string, string)> _cameraData = new();
  // landmark names by object
  private Dictionary<LandmarkInfo, string> _landmarkData = new();

  // fixed CUI data strings
  private string _rootPanelName;
  private string _titleElementName;
  private string _locationClosePanelName;
  private string _locationCloseButtonName;

  // location selector UI JSON
  private string _locationUiJson;
  // whether to regenerate location UI JSON
  private bool _locationUiDirty;
  // CCTV codes UI JSON by short location
  private SortedDictionary<string, string> _codesUiJson = new();
  // set of short locations whose CCTV codes UI JSON should be regenerated
  private HashSet<string> _codesUiDirty = new();

  // CUI scratchpad container
  private CuiElementContainer _container = new();

  private const float Width = 544.0f;
  private const float Height = 544.0f;
  private const float OffsetLeft = -Width * 0.5f;
  private const float OffsetRight = Width * 0.5f;
  private const float OffsetBottom = -Height * 0.5f;
  private const float OffsetTop = Height * 0.5f;

  private void Init()
  {
    Unsubscribe(nameof(OnEntitySpawned));
    Unsubscribe(nameof(OnEntityKill));

    _locationUiDirty = true;
    _rootPanelName = $"{Name}.Root.Panel";
    _titleElementName = $"{Name}.Locations.Label.Help";
    _locationClosePanelName = $"{Name}.Locations.Panel.Close";
    _locationCloseButtonName = $"{Name}.Locations.Button.Close";
  }

  private void OnServerInitialized()
  {
    foreach (var entity in BaseNetworkable.serverEntities)
    {
      if (entity is CCTV_RC camera && camera) OnEntitySpawned(camera);
    }
    // this needs to be done in NextTick() because OnEntitySpawned() uses it
    NextTick(() =>
    {
      Puts($"Cached {_locationData.Count} locations, {_cameraData.Count} cameras, {_landmarkData.Count} landmarks");
      Subscribe(nameof(OnEntitySpawned));
      Subscribe(nameof(OnEntityKill));
    });
  }

  private void Unload()
  {
    foreach (var player in BasePlayer.activePlayerList)
    {
      OnPlayerDisconnected(player, Name);
    }

    foreach (var data in _locationData.Values)
    {
      var cameraSet = data.Item2;
      Facepunch.Pool.FreeUnmanaged(ref cameraSet);
    }

    _locationData.Clear();
    _cameraData.Clear();
    _landmarkData.Clear();
    _rootPanelName = null;
    _locationUiJson = null;
    _codesUiJson.Clear();
    _codesUiDirty.Clear();
    _container.Clear();
  }

  private void OnPlayerDisconnected(BasePlayer player, string reason)
  {
    if (!player) return;

    CuiHelper.DestroyUi(player, _rootPanelName);
  }

  private void OnEntitySpawned(CCTV_RC camera) => NextTick(() =>
  {
    if (null == camera?.net?.ID ||                 // ignore invalid
        camera.OwnerID.IsSteamId() ||              // ignore player owned
        string.IsNullOrEmpty(camera.rcIdentifier)) // ignore no RC ID
    {
      return;
    }

    var netID = camera.net.ID.Value;
    if (_cameraData.ContainsKey(netID)) return;

    var locationName = GetCameraLocationName(camera);
    var locationShortName = ToShortName(locationName);
    _cameraData[netID] = (camera.rcIdentifier, locationShortName);

    if (!_locationData.TryGetValue(locationShortName, out var locationData))
    {
      locationData = (locationName, Facepunch.Pool.Get<HashSet<ulong>>());
      _locationData[locationShortName] = locationData;
      _locationUiDirty = true;
    }
    var cameraSet = locationData.Item2;
    if (cameraSet.Add(netID)) _codesUiDirty.Add(locationShortName);
  });

  private void OnEntityKill(CCTV_RC camera)
  {
    if (null == camera?.net?.ID || camera.OwnerID.IsSteamId()) return;

    // remove camera from _cameraData
    var netID = camera.net.ID.Value;
    if (!_cameraData.Remove(netID, out var cameraData)) return;

    // remove camera from _locationData
    var locationShortName = cameraData.Item2;
    if (!_locationData.TryGetValue(locationShortName, out var locationData))
    {
      return;
    }
    var cameraSet = locationData.Item2;
    if (cameraSet.Remove(netID)) _codesUiDirty.Add(locationShortName);

    if (cameraSet.Count > 0) return;
    // no more cameras at location; remove entire location entry
    Facepunch.Pool.FreeUnmanaged(ref cameraSet);
    if (!_locationData.Remove(locationShortName)) return;
    _locationUiDirty = true;
    // ...also remove JSON and dirty state for defunct location
    if (_codesUiJson.Remove(locationShortName))
    {
      _codesUiDirty.Remove(locationShortName);
    }
  }

  private static string ToShortName(string locationName) =>
    string.Join("", locationName.Split(
      default(string[]), System.StringSplitOptions.RemoveEmptyEntries));

  private string GetCameraLocationName(CCTV_RC camera) =>
    true == camera?.transform.parent?.HasComponent<CargoShip>() ?
      "Cargo Ship" : GetMonument(camera);

  private string GetMonument(BaseEntity entity)
  {
    if (!entity) return null;
    var entityPos = entity.transform.position;
    SpawnGroup spawnGroup = null;
    if (entity is BaseCorpse baseCorpse) spawnGroup = baseCorpse.spawnGroup;
    if (!spawnGroup)
    {
      var component = entity.GetComponent<SpawnPointInstance>();
      if (component) spawnGroup = component.parentSpawnPointUser as SpawnGroup;
    }
    LandmarkInfo monumentInfo =
      spawnGroup?.Monument ??
      TerrainMeta.Path.FindMonumentWithBoundsOverlap(entityPos);
    if (!monumentInfo)
    {
      var minDist = -1f;
      foreach (var monument in TerrainMeta.Path.Monuments)
      {
        var dist = Vector3.Distance(entityPos, monument.transform.position);
        if (minDist > 0 && dist >= minDist) continue;
        minDist = dist;
        monumentInfo = monument;
      }
    }
    return monumentInfo ?
      GetLandmarkName(monumentInfo) : GetGrid(entity.transform.position);
  }

  private string GetLandmarkName(LandmarkInfo landmarkInfo)
  {
    if (_landmarkData.TryGetValue(landmarkInfo, out var cachedName))
    {
      return cachedName;
    }

    // vanilla monument
    if (!landmarkInfo.name.Contains("monument_marker.prefab"))
    {
      var vanillaName = landmarkInfo.displayPhrase?.english?.Trim();
      _landmarkData[landmarkInfo] = vanillaName;
      return vanillaName;
    }

    // custom monument

    // this sucks (results in scanning 5000+ prefabs during startup), but it
    //  seems to be how Facepunch decided to make us do it as of late 2025
    //  (stolen from their MonumentMarker class)
    var transformRoot = landmarkInfo.transform.root;
    var obj = transformRoot.gameObject;
    foreach (var (prefabName, objectSet) in World.SpawnedPrefabs)
    {
      if (!objectSet.Contains(obj)) continue;
      _landmarkData[landmarkInfo] = prefabName;
      return prefabName;
    }

    var rootName = transformRoot.name;
    if (!string.IsNullOrEmpty(rootName))
    {
      _landmarkData[landmarkInfo] = rootName;
      return rootName;
    }

    var gridName = GetGrid(landmarkInfo.transform.position);
    _landmarkData[landmarkInfo] = gridName;
    return gridName;
  }

  // Credit: Lorenzo - https://umod.org/community/rust/4861-calculate-current-coordinate-of-player?page=1#post-3
  private static string GetGrid(Vector3 pos) => MapHelper.PositionToString(pos);

  [ChatCommand("CCTV")]
  private void ChatCommandCctv(BasePlayer player, string command, string[] args)
  {
    if (!player) return;

    GenerateUI();

    CuiHelper.AddUi(player, _locationUiJson);

    // var argList = "";
    // foreach (var arg in args)
    // {
    //   if (!string.IsNullOrEmpty(argList)) argList += ", ";
    //   argList += arg;
    // }
    // SendReply(player, $"***** command={command}, args[0]={args[0]}, args: {argList}");
    // Puts($"***** command={command}, args[0]={args[0]}, args: {argList}");
  }

  // for some reason I can't get buttons to pass command parameters via chat, so
  //  instead it's done via a console command
  [ConsoleCommand("CCTV")]
  private void ConsoleCommandCctv(ConsoleSystem.Arg arg)
  {
    if (arg?.Connection?.connected is not true)
    {
      Puts("This is a client-only command");
      return;
    }

    if (arg.Connection.player is not BasePlayer player || !player)
    {
      Puts("Unable to resolve player");
      return;
    }

    if (arg.Args?.Length is not > 0)
    {
      ChatCommandCctv(player, null, null);
      return;
    }

    GenerateUI();

    var locationShortName = arg.GetString(0);
    if (!_codesUiJson.TryGetValue(locationShortName, out var json))
    {
      SendReply(player, $"Unknown location: {locationShortName}");
      return;
    }

    CuiHelper.AddUi(player, json);
  }

  private struct GridParams
  {
    public readonly int GridCols;
    public readonly float FullX;
    public readonly float FullY;
    public readonly float CellXY;
    public readonly float SpaceX;
    public readonly float SpaceY;

    public enum Bias
    {
      Cols, // bias towards more columns
      Rows  // bias towards more rows
    }

    public GridParams(
      int itemCount, Bias bias, float width, float height)
    {
      //  try to get a similar number of rows and columns
      var sqrt = Mathf.Sqrt(itemCount);
      GridCols =
        Bias.Cols == bias ? Mathf.CeilToInt(sqrt) : Mathf.FloorToInt(sqrt);
      var gridRows = Mathf.CeilToInt((float)itemCount / GridCols);
      // calculate CellSize + Spacing
      FullX = width / GridCols;
      FullY = height / gridRows;
      // calculate CellSize
      CellXY = Mathf.Min(FullX, FullY);
      // calculate Spacing
      SpaceX = FullX - CellXY;
      SpaceY = FullY - CellXY;

      // plugin.Puts($"GridCols={GridCols}, gridRows={gridRows}, FullX={FullX}, FullY={FullY}, CellXY={CellXY}, SpaceX={SpaceX}, SpaceY={SpaceY}");
    }
  }

  private void ResetContainer(
    string titleElementName, string titleTextText,
    string closePanelName, string closeButtonName)
  {
    if (_container.Count >= 4 &&
        _container[1] is { Components.Count: > 0 } titleElement &&
        titleElement.Components[0] is CuiTextComponent titleText &&
        _container[2] is { } closePanel &&
        _container[3] is { } closeButton)
    {
      // trash everything except for the title bar
      _container.RemoveRange(4, _container.Count - 4);

      // update existing stuff
      titleElement.Name = titleElement.DestroyUi = titleElementName;
      titleText.Text = titleTextText;
      closePanel.Name = closePanel.DestroyUi = closePanelName;
      closeButton.Name = closeButton.DestroyUi = closeButtonName;

      return;
    }

    // first generation, or (unlikely) beginning entries are unexpected format
    _container.Clear();

    // add root panel
    _container.Add(new CuiPanel
    {
      CursorEnabled = true,
      FadeOut = 0f,
      Image = { Color = "0 0 0 0.9" },
      KeyboardEnabled = true,
      // RawImage = null,
      RectTransform =
      {
        AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
        OffsetMin = $"{OffsetLeft} {OffsetBottom}",
        OffsetMax = $"{OffsetRight} {OffsetTop}"
      }
    }, "Overlay", _rootPanelName, _rootPanelName);

    // add title text
    _container.Add(new CuiLabel
    {
      FadeOut = 0f,
      RectTransform =
      {
        AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
        OffsetMin = $"{OffsetLeft + 32} {OffsetTop - 32}",
        OffsetMax = $"{OffsetRight - 32} {OffsetTop}"
      },
      Text =
      {
        Color = "1 1 1 1",
        Enabled = true,
        FadeIn = 0f,
        // PlaceholderParentId = "",
        Text = titleTextText,
        Align = TextAnchor.UpperCenter,
        // Font = "",
        FontSize = 18,
        VerticalOverflow = VerticalWrapMode.Overflow
      }
    }, _rootPanelName, titleElementName, titleElementName);

    // add close panel
    _container.Add(new CuiPanel
    {
      // CursorEnabled = true,
      FadeOut = 0f,
      Image = { Color = "1 0.5 0 1" },
      // KeyboardEnabled = false,
      // RawImage = null,
      RectTransform =
      {
        AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
        OffsetMin = $"{OffsetRight - 32} {OffsetTop - 32}",
        OffsetMax = $"{OffsetRight} {OffsetTop}"
      }
    }, _rootPanelName, closePanelName, closePanelName);

    // add close button
    _container.Add(new CuiButton
    {
      FadeOut = 0f,
      RectTransform =
      {
        AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
        OffsetMin = $"{OffsetRight - 28} {OffsetTop - 28}",
        OffsetMax = $"{OffsetRight -  4} {OffsetTop -  4}"
      },
      Button =
      {
        Color = "1 1 1 1",
        Close = _rootPanelName,
        // ColorMultiplier = 0f,
        // Command = "/CCTV X",
        // DisabledColor = "",
        Enabled = true,
        FadeDuration = 0f,
        // FadeIn = 0f,
        HighlightedColor = "1 0 0 1",
        // ImageType = Image.Type.Simple,
        // Material = "",
        // NormalColor = "0 0 1 1",
        // PlaceholderParentId = "",
        // PressedColor = "",
        // SelectedColor = "",
        Sprite = "assets/icons/close.png"
      }
      // Text =
      // {
      //   Color = "1 1 1 1",
      //   Enabled = true,
      //   FadeIn = 0f,
      //   PlaceholderParentId = "",
      //   Text = "X",
      //   Align = TextAnchor.MiddleCenter,
      //   Font = "",
      //   FontSize = 12,
      //   VerticalOverflow = VerticalWrapMode.Truncate
      // }
    }, _rootPanelName, closeButtonName, closeButtonName);
  }

  // generate location selector JSON
  private void GenerateLocationUI()
  {
    if (!_locationUiDirty) return;
    _locationUiDirty = false;
    Puts("Updating location selector UI cache");

    // Puts($"width={width}, height={height}, offsetLeft={offsetLeft}, offsetRight={offsetRight}");

    ResetContainer(
      _titleElementName,
      $"Click one of the {_locationData.Count} location(s) below to see CCTV RF IDs",
      _locationClosePanelName, _locationCloseButtonName);

    // add a grid for the location buttons
    var locGridName = $"{Name}.Locations.Grid";
    var locGridParams = new GridParams(
      _locationData.Count, GridParams.Bias.Cols, Width, Height - 32);
    _container.Add(new CuiElement
    {
      Name = locGridName,
      Parent = _rootPanelName,
      DestroyUi = locGridName,
      Components =
      {
        // new CuiRawImageComponent
        // {
        //   Sprite = "assets/content/effects/crossbreed/fx gradient skewed.png",
        //   Color = "0.1 0.1 0.1 1.0",
        // },
        new CuiRectTransformComponent
        {
          AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
          OffsetMin = $"{OffsetLeft} {OffsetBottom}",
          OffsetMax = $"{OffsetRight} {OffsetTop - 32}"
        },
        new CuiGridLayoutGroupComponent
        {
          CellSize = $"{locGridParams.CellXY} {locGridParams.CellXY}",
          ChildAlignment = TextAnchor.MiddleCenter,
          Constraint = GridLayoutGroup.Constraint.FixedColumnCount,
          ConstraintCount = locGridParams.GridCols,
          // Padding = "12", // "l t r b" or "x" for all sizes
          Spacing = $"{locGridParams.SpaceX} {locGridParams.SpaceY}",
          StartAxis = GridLayoutGroup.Axis.Horizontal,
          StartCorner = GridLayoutGroup.Corner.UpperLeft
        },
        new CuiContentSizeFitterComponent
        {
          VerticalFit = ContentSizeFitter.FitMode.PreferredSize,
          HorizontalFit = ContentSizeFitter.FitMode.PreferredSize
        }
      }
    });

    // add buttons to grid
    foreach (var (locationShortName, (locationName, _)) in _locationData)
    {
      // have to add individual components, because we don't use a transform
      var buttonNameI = $"{Name}.Locations.Button.{locationShortName}";

      _container.Add(new CuiElement
      {
        Name = buttonNameI,
        Parent = locGridName,
        DestroyUi = buttonNameI,
        Components =
        {
          new CuiButtonComponent
          {
            Color = "1 0.5 0.01 0.67",
            Command = $"CCTV {locationShortName}",
            Sprite = "assets/icons/folder.png",
            HighlightedColor = "1 1 0 1.5",
            PressedColor = "1 2 100 1.5"
            // Close = _rootPanelName
          }
        }
      });

      var textName = $"{Name}.Locations.Text.{locationShortName}";
      _container.Add(new CuiElement
      {
        Name = textName,
        Parent = buttonNameI,
        DestroyUi = textName,
        Components =
        {
          new CuiTextComponent
          {
            Text = locationName,
            Color = "0.95 0.95 0.95 1.0",
            FontSize = 18,
            Align = TextAnchor.MiddleCenter,
            VerticalOverflow = VerticalWrapMode.Overflow
          }
        }
      });
    }

    // cache as JSON string for reuse until it becomes dirty
    _locationUiJson = _container.ToJson();
  }

  private void GenerateCodesUI(
    string locationShortName, string locationName, HashSet<ulong> cameraSet)
  {
    if (!_codesUiDirty.Remove(locationShortName)) return;
    Puts($"Updating camera codes UI cache for location {locationShortName}");

    var prefix = $"{Name}.Codes_{locationShortName}.";

    ResetContainer(
      $"{prefix}Label.Help",
      $"{locationName} has {cameraSet.Count} CCTV RF ID(s)\nHighlight and hit Ctrl+C to copy",
      $"{prefix}Panel.Close", $"{prefix}Button.Close");

    // add a back panel + button
    var backPanelName = $"{prefix}Panel.Back";
    _container.Add(new CuiPanel
    {
      // CursorEnabled = true,
      FadeOut = 0f,
      Image = { Color = "1 0.5 0 1" },
      // KeyboardEnabled = false,
      // RawImage = null,
      RectTransform =
      {
        AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
        OffsetMin = $"{OffsetLeft} {OffsetTop - 32}",
        OffsetMax = $"{OffsetLeft + 32} {OffsetTop}"
      }
    }, _rootPanelName, backPanelName, backPanelName);

    var backButtonName = $"{prefix}Button.Back";
    _container.Add(new CuiButton
    {
      FadeOut = 0f,
      RectTransform =
      {
        AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
        OffsetMin = $"{OffsetLeft + 4} {OffsetTop - 28}",
        OffsetMax = $"{OffsetLeft + 28} {OffsetTop - 4}"
      },
      Button =
      {
        Color = "1 1 1 1",
        // Close = _rootPanelName,
        Command = "chat.say /CCTV",
        Enabled = true,
        FadeDuration = 0f,
        HighlightedColor = "1 0 0 1",
        Sprite = "assets/icons/folder_up.png"
      }
    }, _rootPanelName, backButtonName, backButtonName);

    // add a grid for the CCTV codes
    var codeGridName = $"{prefix}Grid";
    var codeGridParams = new GridParams(
      cameraSet.Count, GridParams.Bias.Rows, Width, Height - 32);
    _container.Add(new CuiElement
    {
      Name = codeGridName,
      Parent = _rootPanelName,
      DestroyUi = codeGridName,
      Components =
      {
        new CuiRectTransformComponent
        {
          AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
          OffsetMin = $"{OffsetLeft} {OffsetBottom}",
          OffsetMax = $"{OffsetRight} {OffsetTop - 32}"
        },
        new CuiGridLayoutGroupComponent
        {
          CellSize = $"{codeGridParams.FullX} {codeGridParams.FullY}",
          ChildAlignment = TextAnchor.MiddleCenter,
          Constraint = GridLayoutGroup.Constraint.FixedColumnCount,
          // try to get a similar number of rows and columns
          ConstraintCount = codeGridParams.GridCols,
          // Padding = "12", // "l t r b" or "x" for all sizes
          Spacing = "0 0",
          StartAxis = GridLayoutGroup.Axis.Horizontal,
          StartCorner = GridLayoutGroup.Corner.UpperLeft
        },
        new CuiContentSizeFitterComponent
        {
          VerticalFit = ContentSizeFitter.FitMode.PreferredSize,
          HorizontalFit = ContentSizeFitter.FitMode.PreferredSize
        }
      }
    });

    // add textInputs to grid
    foreach (var cameraNetID in cameraSet)
    {
      if (!_cameraData.TryGetValue(cameraNetID, out var cameraData)) continue;
      var cameraCode = cameraData.Item1;

      var imageNameI = $"{prefix}Image.{cameraCode}";
      _container.Add(new CuiElement
      {
        Name = imageNameI,
        Parent = codeGridName,
        DestroyUi = imageNameI,
        Components =
        {
          new CuiImageComponent
          {
            Color = "1 0.5 0.01 0.67",
            ItemId = 634478325
          }
        }
      });

      var textNameI = $"{prefix}TextInput.{cameraCode}";
      _container.Add(new CuiElement
      {
        Name = textNameI,
        Parent = imageNameI, //codeGridName,
        DestroyUi = textNameI,
        Components =
        {
          new CuiInputFieldComponent
          {
            Color = "1 1 1 1",
            Text = cameraCode,
            Align = TextAnchor.MiddleCenter,
            // Command = "",
            // Enabled = true,
            FadeIn = 0f,
            // PlaceholderParentId = "",
            // Font = "",
            FontSize = 18,
            // Autofocus = true,
            // CharsLimit = 0,
            // HudMenuInput = true,
            IsPassword = false,
            LineType = InputField.LineType.SingleLine,
            NeedsKeyboard = true,
            // PlaceholderId = "",
            ReadOnly = true
          }
        }
      });
    }

    // cache as JSON string for reuse until it becomes dirty
    _codesUiJson[locationShortName] = _container.ToJson();
  }

  private void GenerateUI()
  {
    // Puts($"width={width}, height={height}, offsetLeft={offsetLeft}, offsetRight={offsetRight}");

    GenerateLocationUI();

    foreach (var (locationShortName, (locationName, cameraSet))
             in _locationData)
    {
      GenerateCodesUI(locationShortName, locationName, cameraSet);
    }
  }
}

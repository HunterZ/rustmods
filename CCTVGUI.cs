using Oxide.Game.Rust.Cui;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins;

[Info("CCTV GUI", "HunterZ", "1.0.0")]
public class CCTVGUI : RustPlugin
{
  // whether CUI data needs to be updated due to underlying game state changes
  private bool _dirty;

  // (full location, CCTV netIDs) by short location
  private SortedDictionary<string, (string, HashSet<ulong>)> _locationData =
    new();
  // CCTV (rcID, short location) by netID
  private Dictionary<ulong, (string, string)> _cameraData = new();
  // landmark names by object
  private Dictionary<LandmarkInfo, string> _landmarkData = new();

  // UI root panel name
  private string _rootPanelName;
  // location selector UI JSON
  private string _locationUiJson;
  // CCTV codes UI JSON by short location
  private SortedDictionary<string, string> _codesUiJson = new();

  private void Init()
  {
    Unsubscribe(nameof(OnEntitySpawned));
    Unsubscribe(nameof(OnEntityKill));

    _rootPanelName = $"{Name}.Root.Panel";
  }

  private void OnServerInitialized()
  {
    foreach (var entity in BaseNetworkable.serverEntities)
    {
      if (entity && entity is CCTV_RC camera) OnEntitySpawned(camera);
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
    }
    locationData.Item2.Add(netID);
    _dirty = true;
  });

  private void OnEntityKill(CCTV_RC camera)
  {
    if (null == camera?.net?.ID || camera.OwnerID.IsSteamId()) return;

    // remove camera from _cameraData
    var netID = camera.net.ID.Value;
    if (!_cameraData.Remove(netID, out var cameraData)) return;
    _dirty = true;

    // remove camera from _locationData
    var locationShortName = cameraData.Item2;
    if (!_locationData.TryGetValue(locationShortName, out var locationData))
    {
      return;
    }
    var cameraSet = locationData.Item2;
    cameraSet.Remove(netID);

    if (cameraSet.Count > 0) return;
    // more cameras at location; remove entire location entry
    Facepunch.Pool.FreeUnmanaged(ref cameraSet);
    _locationData.Remove(locationShortName);
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
  private static string GetGrid(Vector3 pos) =>
    MapHelper.PositionToString(pos);

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

    var locationShortName = arg.Args[0];
    if (!_codesUiJson.TryGetValue(locationShortName, out var json))
    {
      SendReply(player, $"Unknown location: {locationShortName}");
      return;
    }

    GenerateUI();

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
      int itemCount, Bias bias, float width, float height, RustPlugin plugin)
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

  private void GenerateUI()
  {
    if (!_dirty) return;
    Puts("Updating GUI cache");
    _dirty = false;
    _codesUiJson.Clear();

    const float width = 544.0f;
    const float height = 544.0f;
    const float offsetLeft = -width * 0.5f;
    const float offsetRight = width * 0.5f;
    const float offsetBottom = -height * 0.5f;
    const float offsetTop = height * 0.5f;
    // Puts($"width={width}, height={height}, offsetLeft={offsetLeft}, offsetRight={offsetRight}");

    // generate location selector JSON
    var container = new CuiElementContainer
    {
      {
        new CuiPanel
        {
          CursorEnabled = true,
          FadeOut = 0f,
          Image = { Color = "0 0 0 0.9" },
          KeyboardEnabled = true,
          // RawImage = null,
          RectTransform =
          {
            AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
            OffsetMin = $"{offsetLeft} {offsetBottom}",
            OffsetMax = $"{offsetRight} {offsetTop}"
          }
        }, "Overlay", _rootPanelName, _rootPanelName
      },
      {
        new CuiLabel
        {
          FadeOut = 0f,
          RectTransform =
          {
            AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
            OffsetMin = $"{offsetLeft + 32} {offsetTop - 32}",
            OffsetMax = $"{offsetRight - 32} {offsetTop}"
          },
          Text =
          {
            Color = "1 1 1 1",
            Enabled = true,
            FadeIn = 0f,
            // PlaceholderParentId = "",
            Text = $"Click one of the {_locationData.Count} location(s) below to see CCTV RF IDs",
            Align = TextAnchor.UpperCenter,
            // Font = "",
            FontSize = 18,
            VerticalOverflow = VerticalWrapMode.Overflow
          }
        }, _rootPanelName, $"{Name}.Locations.Label.Help", $"{Name}.Locations.Label.Help"
      },
      {
        new CuiPanel
        {
          // CursorEnabled = true,
          FadeOut = 0f,
          Image = { Color = "1 0.5 0 1" },
          // KeyboardEnabled = false,
          // RawImage = null,
          RectTransform =
          {
            AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
            OffsetMin = $"{offsetRight - 32} {offsetTop - 32}",
            OffsetMax = $"{offsetRight} {offsetTop}"
          }
        }, _rootPanelName, $"{Name}.Locations.Panel.Close", $"{Name}.Locations.Panel.Close"
      },
      {
        new CuiButton
        {
          FadeOut = 0f,
          RectTransform =
          {
            AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
            OffsetMin = $"{offsetRight - 28} {offsetTop - 28}",
            OffsetMax = $"{offsetRight -  4} {offsetTop -  4}"
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
        }, _rootPanelName, $"{Name}.Locations.Button.Close", $"{Name}.Locations.Button.Close"
      }
    };

    // add a grid for the location buttons
    var locGridName = $"{Name}.Locations.Grid";
    var locGridParams = new GridParams(
      _locationData.Count, GridParams.Bias.Cols, width, height - 32, this);
    container.Add(new CuiElement
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
          OffsetMin = $"{offsetLeft} {offsetBottom}",
          OffsetMax = $"{offsetRight} {offsetTop - 32}"
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
          StartCorner = GridLayoutGroup.Corner.UpperLeft,
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

      container.Add(new CuiElement
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
      container.Add(new CuiElement
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
    _locationUiJson = container.ToJson();

    // now build the code panels
    foreach (var (locationShortName, (locationName, cameraSet))
             in _locationData)
    {
      // trash everything from the CUI builder except for the title bar
      container.RemoveRange(4, container.Count - 4);

      // change the title ID & text
      if (container.Count < 4 || container[1] is not { } titleElement ||
          titleElement.Components?.Count is not > 0 ||
          titleElement.Components[0] is not CuiTextComponent titleText)
      {
        continue;
      }

      var prefix = $"{Name}.Codes_{locationShortName}.";
      titleElement.Name = titleElement.DestroyUi = $"{prefix}Label.Help";
      titleText.Text = $"{locationName} has {cameraSet.Count} CCTV RF ID(s)\nHighlight and hit Ctrl+C to copy";

      // change close panel + button ID
      if (container[2] is not { } closePanel ||
          container[3] is not { } closeButton)
      {
        continue;
      }
      closePanel.Name = closePanel.DestroyUi = $"{prefix}Panel.Close";
      closeButton.Name = closeButton.DestroyUi = $"{prefix}Button.Close";

      // add a back panel + button
      var backPanelName = $"{prefix}Panel.Back";
      container.Add(new CuiPanel
      {
        // CursorEnabled = true,
        FadeOut = 0f,
        Image = { Color = "1 0.5 0 1" },
        // KeyboardEnabled = false,
        // RawImage = null,
        RectTransform =
        {
          AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
          OffsetMin = $"{offsetLeft} {offsetTop - 32}",
          OffsetMax = $"{offsetLeft + 32} {offsetTop}"
        }
      }, _rootPanelName, backPanelName, backPanelName);

      var backButtonName = $"{prefix}Button.Back";
      container.Add(new CuiButton
      {
        FadeOut = 0f,
        RectTransform =
        {
          AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
          OffsetMin = $"{offsetLeft + 4} {offsetTop - 28}",
          OffsetMax = $"{offsetLeft + 28} {offsetTop - 4}"
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
        cameraSet.Count, GridParams.Bias.Rows, width, height - 32, this);
      container.Add(new CuiElement
      {
        Name = codeGridName,
        Parent = _rootPanelName,
        DestroyUi = codeGridName,
        Components =
        {
          new CuiRectTransformComponent
          {
            AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5",
            OffsetMin = $"{offsetLeft} {offsetBottom}",
            OffsetMax = $"{offsetRight} {offsetTop - 32}"
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
            StartCorner = GridLayoutGroup.Corner.UpperLeft,
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
        container.Add(new CuiElement
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
        container.Add(new CuiElement
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
      _codesUiJson[locationShortName] = container.ToJson();
    }
  }
}

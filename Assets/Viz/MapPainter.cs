using System;
using System.Collections.Generic;
using System.IO;
using SimCore.Config;
using SimCore.Domain;
using UnityEngine;

namespace Viz
{
    /// <summary>
    /// D8 authoring tool. Edits a DRAFT ScenarioConfig (never the live one), renders it, and writes JSON.
    /// Controls (edit mode on): 1–5 pick brush (Road/Facility/House/Charger/Building), left-click paints,
    /// S sets start facility on a Facility, P then click Facility then House adds a package.
    /// </summary>
    public sealed class MapPainter : MonoBehaviour
    {
        public bool EditMode;
        public TileType Brush = TileType.Building;

        private SimulationView _view;
        private ScenarioConfig _draft;
        private GridMap _map;
        private string _saveName = "New-001";
        private string _seedText = "0";
        private string _gridText = "20";
        private string _message = "";
        private bool _packageMode;
        private GridCoord? _packageOrigin;
        private bool _wasEditMode;

        private static readonly (KeyCode key, TileType type)[] BrushKeys =
        {
            (KeyCode.Alpha1, TileType.Road), (KeyCode.Alpha2, TileType.Facility), (KeyCode.Alpha3, TileType.House),
            (KeyCode.Alpha4, TileType.ChargingStation), (KeyCode.Alpha5, TileType.Building)
        };

        public void Bind(SimulationView view) => _view = view;

        private void EnterEdit()
        {
            _draft = ScenarioSerializer.Clone(_view.Config);
            _map = BuildMap(_draft);
            _saveName = _draft.ScenarioId;
            _seedText = _draft.Seed.ToString();
            _gridText = _draft.GridSize.ToString();
            _message = "Editing a draft. Apply to run it; Save to write JSON.";
            _view.Grid.Build(_map);
        }

        private void ExitEdit()
        {
            _view.Grid.SetHighlight(null);
            _view.Grid.Build(_view.Runner.World.Map);
            _packageMode = false; _packageOrigin = null;
        }

        private static GridMap BuildMap(ScenarioConfig cfg)
        {
            var map = new GridMap(cfg.GridSize);
            foreach (var t in cfg.Tiles)
                if (Enum.TryParse<TileType>(t.Type, true, out var type) && map.InBounds(new GridCoord(t.X, t.Y)))
                    map.SetType(new GridCoord(t.X, t.Y), type);
            return map;
        }

        private void Update()
        {
            if (EditMode != _wasEditMode) { if (EditMode) EnterEdit(); else ExitEdit(); _wasEditMode = EditMode; }
            if (!EditMode || _draft == null) return;

            foreach (var (key, type) in BrushKeys) if (Input.GetKeyDown(key)) { Brush = type; _packageMode = false; }
            if (Input.GetKeyDown(KeyCode.P)) { _packageMode = !_packageMode; _packageOrigin = null; _message = _packageMode ? "Package mode: click a Facility, then a House." : "Package mode off."; }

            var tile = TileUnderMouse();
            _view.Grid.SetHighlight(tile);
            if (!tile.HasValue) return;
            var c = tile.Value;

            if (Input.GetKeyDown(KeyCode.S) && _map.TypeAt(c) == TileType.Facility)
            {
                _draft.Drone.StartFacility = new CoordSpec { X = c.X, Y = c.Y };
                _message = $"Start facility set to {c}.";
            }

            if (Input.GetMouseButtonDown(0) && !MouseOverGui())
            {
                if (_packageMode) ClickPackage(c);
                else Paint(c, Brush);
            }
        }

        private void Paint(GridCoord c, TileType type)
        {
            _map.SetType(c, type);
            _draft.Tiles.RemoveAll(t => t.X == c.X && t.Y == c.Y);
            if (type != TileType.Road) _draft.Tiles.Add(new TileSpec { X = c.X, Y = c.Y, Type = type.ToString() });

            int dropped = _draft.Packages.RemoveAll(p =>
                (p.Origin.X == c.X && p.Origin.Y == c.Y && type != TileType.Facility) ||
                (p.Destination.X == c.X && p.Destination.Y == c.Y && type != TileType.House));
            if (_draft.Drone.StartFacility != null && _draft.Drone.StartFacility.X == c.X && _draft.Drone.StartFacility.Y == c.Y && type != TileType.Facility)
                _draft.Drone.StartFacility = null;

            _message = dropped > 0 ? $"Painted {type} at {c}; dropped {dropped} package(s) that referenced it." : $"Painted {type} at {c}.";
            _view.Grid.Build(_map);
        }

        private void ClickPackage(GridCoord c)
        {
            var t = _map.TypeAt(c);
            if (!_packageOrigin.HasValue)
            {
                if (t != TileType.Facility) { _message = "Package origin must be a Facility."; return; }
                _packageOrigin = c; _message = $"Origin {c}. Now click a House.";
                return;
            }
            if (t != TileType.House) { _message = "Package destination must be a House."; return; }
            if (_draft.Packages.Exists(p => p.Destination.X == c.X && p.Destination.Y == c.Y)) { _message = $"House {c} already has a package (no shared destinations)."; return; }

            string id = $"P{_draft.Packages.Count + 1:D3}";
            while (_draft.Packages.Exists(p => p.Id == id)) id = "P" + (int.Parse(id.Substring(1)) + 1).ToString("D3");
            double dist = _packageOrigin.Value.DistanceTo(c);
            string diff = dist < 10 ? "Easy" : dist < 20 ? "Normal" : "Hard";
            _draft.Packages.Add(new PackageSpec
            {
                Id = id, Difficulty = diff,
                Origin = new CoordSpec { X = _packageOrigin.Value.X, Y = _packageOrigin.Value.Y },
                Destination = new CoordSpec { X = c.X, Y = c.Y }
            });
            _draft.PackageGeneration = null;
            _message = $"Added {id}: {_packageOrigin} → {c} ({dist:F1} tiles, {diff}).";
            _packageOrigin = null;
        }

        private GridCoord? TileUnderMouse()
        {
            var cam = Camera.main;
            if (cam == null || _map == null) return null;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
            if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter)) return null;
            var p = ray.GetPoint(enter);
            var c = new GridCoord(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.z));
            return _map.InBounds(c) ? c : null;
        }

        private static bool MouseOverGui()
        {
            var m = Input.mousePosition;
            return m.x <= 345 || m.x >= Screen.width - 300;
        }

        // ------------------------------------------------------------ GUI

        public void DrawGui()
        {
            if (!EditMode || _draft == null) return;
            GUILayout.BeginArea(new Rect(Screen.width - 290, 10, 280, 440), GUI.skin.box);
            GUILayout.Label("<b>Map painter</b>", RichLabel());
            GUILayout.Label("1 Road  2 Facility  3 House  4 Charger  5 Building\nS = start facility   P = package mode");
            GUILayout.Label($"Brush: <b>{Brush}</b>{(_packageMode ? "   <b>[PACKAGE MODE]</b>" : "")}", RichLabel());

            GUILayout.Space(6);
            GUILayout.BeginHorizontal(); GUILayout.Label("scenario_id", GUILayout.Width(90)); _saveName = GUILayout.TextField(_saveName); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("seed", GUILayout.Width(90)); _seedText = GUILayout.TextField(_seedText); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("grid_size", GUILayout.Width(90)); _gridText = GUILayout.TextField(_gridText);
            if (GUILayout.Button("New blank", GUILayout.Width(80))) NewBlank();
            GUILayout.EndHorizontal();
            _draft.Recovery.AutoRecoveryEnabled = GUILayout.Toggle(_draft.Recovery.AutoRecoveryEnabled, " auto_recovery_enabled");

            GUILayout.Label($"Tiles: {_draft.Tiles.Count}   Packages: {_draft.Packages.Count}   Start: {(_draft.Drone.StartFacility == null ? "first facility" : $"({_draft.Drone.StartFacility.X},{_draft.Drone.StartFacility.Y})")}");
            if (GUILayout.Button("Clear packages → seeded generation"))
            {
                _draft.Packages.Clear();
                _draft.PackageGeneration = new PackageGenerationConfig();
                _message = "Packages cleared; loader will generate from seed and distance bands.";
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply to sim")) Apply();
            if (GUILayout.Button("Save JSON")) Save();
            GUILayout.EndHorizontal();
            GUILayout.Label(_message, RichLabel());
            GUILayout.EndArea();
        }

        private void NewBlank()
        {
            if (!int.TryParse(_gridText, out int size) || size < 5 || size > 200) { _message = "grid_size must be 5..200."; return; }
            _draft = new ScenarioConfig { ScenarioId = _saveName, GridSize = size, Seed = int.TryParse(_seedText, out var s) ? s : 0 };
            _draft.PackageGeneration = new PackageGenerationConfig();
            _map = new GridMap(size);
            _view.Grid.Build(_map);
            GridRenderer.FitCamera(size);
            _message = "Blank map. Paint at least one Facility and some Houses.";
        }

        private ScenarioConfig Finalize()
        {
            _draft.ScenarioId = _saveName;
            _draft.Seed = int.TryParse(_seedText, out var s) ? s : _draft.Seed;
            if (_draft.Packages.Count > 0) _draft.PackageGeneration = null;
            else if (_draft.PackageGeneration == null) _draft.PackageGeneration = new PackageGenerationConfig();
            return _draft;
        }

        private void Apply()
        {
            try
            {
                var cfg = ScenarioSerializer.Clone(Finalize());
                ScenarioLoader.Build(cfg);                   // validate loudly before touching the live view
                _view.LoadFromConfig(cfg);
                EditMode = false;
                _message = "Applied.";
            }
            catch (Exception ex) { _message = $"<color=red>{ex.Message}</color>"; }
        }

        private void Save()
        {
            try
            {
                var cfg = Finalize();
                ScenarioLoader.Build(ScenarioSerializer.Clone(cfg));
                Directory.CreateDirectory(SimulationView.ScenariosDir);
                string path = Path.Combine(SimulationView.ScenariosDir, cfg.ScenarioId + ".json");
                File.WriteAllText(path, ScenarioSerializer.ToJson(cfg));
                _view.RefreshScenarioList();
                _message = $"Saved {Path.GetFileName(path)}";
            }
            catch (Exception ex) { _message = $"<color=red>{ex.Message}</color>"; }
        }

        private static GUIStyle _rich;
        private static GUIStyle RichLabel() => _rich ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
    }
}
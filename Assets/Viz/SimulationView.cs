using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SimCore.Config;
using SimCore.Domain;
using SimCore.Engine;
using SimCore.Eval;
using SimCore.Events;
using SimCore.Observation;
using SimCore.Planning;
using SimCore.Planning.Llm;
using SimCore.State;
using UnityEngine;

namespace Viz
{
    public enum PlannerChoice { Greedy, ClaudeLive, OpenAiLive, GeminiLive }

    /// <summary>
    /// The spectator. Builds the world from a scenario file, drives EpisodeRunner at a real-time multiple,
    /// and draws HUD + controls with IMGUI. Never mutates sim state directly.
    /// </summary>
    public sealed class SimulationView : MonoBehaviour
    {
        [Header("Scenario")]
        public string scenarioFile = "Easy-001.json";

        [Header("Experimental condition")]
        public PlannerChoice planner = PlannerChoice.Greedy;
        public ObservationMode observationMode = ObservationMode.Processed;
        public GuidanceTier guidance = GuidanceTier.Full;
        public string llmModel = "claude-sonnet-4-5";
        [Range(0f, 1f)] public float errorInjectionRate = 0f;
        public int errorInjectionSeed = 0;

        [Header("Playback")]
        [Range(0.25f, 60f)] public float simSpeed = 4f;
        public bool autoRunEpisode = false;

        public ScenarioConfig Config { get; private set; }
        public EpisodeRunner Runner { get; private set; }
        public EventLog Log { get; private set; }
        public GridRenderer Grid => _grid;
        public string Status { get; private set; } = "";

        private GridRenderer _grid;
        private DroneView _drone;
        private MapPainter _painter;
        private double _acc;
        private List<string> _scenarioFiles = new();
        private int _tripsSeen;
        private Vector2 _eventScroll;
        private static readonly Regex MoveRx = new(@"MOVE_TO\((\d+),(\d+)\)", RegexOptions.Compiled);

        public static string ScenariosDir => Path.Combine(Application.streamingAssetsPath, "Scenarios");

        private void Start()
        {
            _grid = new GameObject("Grid").AddComponent<GridRenderer>();
            _drone = new GameObject("Drone").AddComponent<DroneView>();
            _painter = GetComponent<MapPainter>() ?? gameObject.AddComponent<MapPainter>();
            _painter.Bind(this);
            RefreshScenarioList();
            LoadScenario(scenarioFile);
        }

        // ------------------------------------------------------------ setup

        public void RefreshScenarioList()
        {
            _scenarioFiles.Clear();
            if (Directory.Exists(ScenariosDir))
                foreach (var f in Directory.GetFiles(ScenariosDir, "*.json")) _scenarioFiles.Add(Path.GetFileName(f));
            _scenarioFiles.Sort(StringComparer.Ordinal);
        }

        public void LoadScenario(string file)
        {
            try
            {
                Config = ScenarioLoader.Parse(File.ReadAllText(Path.Combine(ScenariosDir, file)));
                scenarioFile = file;
                Rebuild();
            }
            catch (Exception ex) { Status = $"Load failed: {ex.Message}"; }
        }

        public void LoadFromConfig(ScenarioConfig cfg)
        {
            Config = cfg;
            Rebuild();
        }

        public void Rebuild()
        {
            var world = ScenarioLoader.Build(Config);
            Log = new EventLog();
            var engine = new SimulationEngine(world, Config, Log);
            var episode = new EpisodeConfig
            {
                ScenarioId = Config.ScenarioId, ObservationMode = observationMode, Guidance = guidance,
                PlannerId = planner.ToString()
            };
            Runner = new EpisodeRunner(engine, BuildPlanner(), new ObservationCompiler(), episode, Log);
            _grid.Build(world.Map);
            GridRenderer.FitCamera(world.Map.Size);
            _drone.Bind(Runner);
            _acc = 0;
            _tripsSeen = 0;
            Status = $"Loaded {Config.ScenarioId} v{Config.ScenarioVersion} — {world.Packages.Count} packages";
        }

        private IPlanner BuildPlanner()
        {
            IPlanner p = planner switch
            {
                PlannerChoice.ClaudeLive => new LlmPlanner(new ClaudeProvider(), new LlmPlannerConfig { Provider = "claude", Model = llmModel }),
                PlannerChoice.OpenAiLive => new LlmPlanner(new OpenAiProvider(), new LlmPlannerConfig { Provider = "openai", Model = llmModel }),
                PlannerChoice.GeminiLive => new LlmPlanner(new GeminiProvider(), new LlmPlannerConfig { Provider = "gemini", Model = llmModel }),
                _ => new GreedyPlanner()
            };
            if (errorInjectionRate > 0f)
                p = new ErrorInjectingPlanner(p, new ErrorInjectionConfig { Rate = errorInjectionRate, Seed = errorInjectionSeed });
            return p;
        }

        // ------------------------------------------------------------ playback

        private void Update()
        {
            if (Runner == null || _painter.EditMode) return;

            if (!Runner.IsExecuting)
            {
                if (autoRunEpisode && Runner.CanStartTrip) StartTrip();
                return;
            }

            _acc += Time.deltaTime * simSpeed;
            double tick = Config.TickSeconds;
            int guard = 20000;
            while (_acc >= tick && Runner.IsExecuting && guard-- > 0)
            {
                Runner.Tick();
                _acc -= tick;
            }
            _drone.Refresh();
            if (!Runner.IsExecuting) OnTripEnded();
        }

        public void StartTrip()
        {
            if (Runner == null || !Runner.CanStartTrip) return;
            Status = "Thinking…";
            try { Runner.StartTrip(); }
            catch (Exception ex) { Status = $"StartTrip failed: {ex.Message}"; return; }

            if (Runner.IsExecuting)
            {
                _drone.OnTripStarted(ExtractRoute());
                Status = $"Trip {Runner.CurrentTrip.TripNumber}: executing ({Runner.CurrentTrip.CommandsTotal} commands, {Runner.CurrentTrip.PlanAttempts} attempt(s))";
            }
            else OnTripEnded();
        }

        private List<GridCoord> ExtractRoute()
        {
            var pts = new List<GridCoord>();
            var accepted = Log.Last(EpisodeEventTypes.PlanAccepted);
            if (accepted == null || !accepted.Data.TryGetValue("plan", out var planObj)) return pts;
            foreach (Match m in MoveRx.Matches(planObj.ToString()))
                pts.Add(new GridCoord(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
            return pts;
        }

        private void OnTripEnded()
        {
            if (Runner.Trips.Count > _tripsSeen)
            {
                _tripsSeen = Runner.Trips.Count;
                var t = Runner.Trips[_tripsSeen - 1];
                Status = $"Trip {t.TripNumber}: {t.Classification} (sev {t.SeverityLevel}) — delivered {t.PackagesDelivered}, battery {t.BatteryEnd:F0}%"
                         + (t.EmergencyRecovery ? " — EMERGENCY RECOVERY" : "")
                         + (t.LowBatteryOccurred && !t.EmergencyRecovery ? " — low battery" : "");
            }
            if (!Runner.IsRunning)
                Status = $"EPISODE {Runner.World.Status} ({Runner.TerminationReason}) — {Runner.World.DeliveredCount()}/{Runner.World.Packages.Count} delivered in {Runner.Trips.Count} trips, {Runner.World.Clock.TimeSeconds:F1}s sim";
            _drone.Refresh();
        }

        public string SaveLog()
        {
            string dir = Path.Combine(Application.persistentDataPath, "runs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{Log.Header.RunId}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
            Log.WriteJsonl(path);
            return path;
        }

        // ------------------------------------------------------------ HUD

        private void OnGUI()
        {
            if (Runner == null) return;
            var w = Runner.World;
            var d = w.Drone;

            GUILayout.BeginArea(new Rect(10, 10, 330, Screen.height - 20), GUI.skin.box);

            GUILayout.Label($"<b>{Config.ScenarioId}</b>  v{Config.ScenarioVersion}  {w.Map.Size}×{w.Map.Size}", Rich());
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(30))) CycleScenario(-1);
            GUILayout.Label(scenarioFile, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("▶", GUILayout.Width(30))) CycleScenario(+1);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label($"Planner: <b>{Runner.Planner.Id}</b>  |  {observationMode}  |  {guidance}", Rich());
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Planner")) { planner = (PlannerChoice)(((int)planner + 1) % 4); Rebuild(); }
            if (GUILayout.Button("Obs")) { observationMode = observationMode == ObservationMode.Raw ? ObservationMode.Processed : ObservationMode.Raw; Rebuild(); }
            if (GUILayout.Button("Guide")) { guidance = (GuidanceTier)(((int)guidance + 1) % 3); Rebuild(); }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUI.enabled = Runner.CanStartTrip && !_painter.EditMode;
            if (GUILayout.Button(Runner.CanStartTrip ? "▶  Start Trip" : "Start Trip", GUILayout.Height(32))) StartTrip();
            GUI.enabled = true;
            GUILayout.BeginHorizontal();
            autoRunEpisode = GUILayout.Toggle(autoRunEpisode, " Run whole episode");
            if (GUILayout.Button("Reset")) Rebuild();
            GUILayout.EndHorizontal();
            GUILayout.Label($"Sim speed ×{simSpeed:F1}");
            simSpeed = GUILayout.HorizontalSlider(simSpeed, 0.25f, 60f);

            GUILayout.Space(8);
            GUILayout.Label($"Trip <b>{w.TripIndex + (Runner.IsExecuting ? 1 : 0)}</b> / {Config.Mission.MaxTrips}    Phase <b>{w.Phase}</b>    Episode <b>{w.Status}</b>", Rich());
            GUILayout.Label($"Sim time {w.Clock.TimeSeconds:F2}s   tick {w.Clock.Ticks}");
            GUILayout.Label($"Delivered <b>{w.DeliveredCount()}</b> / {w.Packages.Count}    On board: {string.Join(",", d.LoadedPackageIds)}", Rich());
            GUILayout.Label($"{d.Flight}  at {d.Position}  speed {d.Speed}" + (d.InLowBatteryState ? "  <color=orange><b>LOW BATTERY</b></color>" : "") + (d.Failure != FailureKind.None ? $"  <color=red><b>{d.Failure}</b></color>" : ""), Rich());

            GUILayout.Label("Battery");
            var r = GUILayoutUtility.GetRect(300, 18);
            GUI.Box(r, "");
            var fill = new Rect(r.x + 2, r.y + 2, (r.width - 4) * Mathf.Clamp01((float)(d.BatteryPct / Config.Drone.BatteryCapacityPct)), r.height - 4);
            var prev = GUI.color;
            GUI.color = d.BatteryPct > 40 ? Color.green : d.BatteryPct > 15 ? Color.yellow : Color.red;
            GUI.DrawTexture(fill, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(r, $"  {d.BatteryPct:F1}%" + (Runner.Engine.IsCharging ? $"  charging → {Runner.Engine.ChargeTargetPct:F0}%" : ""));

            GUILayout.Space(8);
            GUILayout.Label($"<b>Status:</b> {Status}", Rich());

            GUILayout.Space(6);
            if (Runner.Trips.Count > 0)
            {
                GUILayout.Label("<b>Trips</b>", Rich());
                foreach (var t in Runner.Trips)
                    GUILayout.Label($"  {t.TripNumber}: {t.Classification} (sev {t.SeverityLevel}), +{t.PackagesDelivered}, {t.BatteryStart:F0}→{t.BatteryEnd:F0}%");
            }

            GUILayout.Space(6);
            GUILayout.Label("<b>Recent events</b>", Rich());
            _eventScroll = GUILayout.BeginScrollView(_eventScroll, GUILayout.Height(160));
            int start = Math.Max(0, Log.Events.Count - 25);
            for (int i = start; i < Log.Events.Count; i++) GUILayout.Label(Log.Events[i].ToString());
            GUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save JSONL")) Status = "Saved " + SaveLog();
            _painter.EditMode = GUILayout.Toggle(_painter.EditMode, " Map painter", "Button");
            GUILayout.EndHorizontal();

            GUILayout.EndArea();

            _painter.DrawGui();
        }

        private void CycleScenario(int delta)
        {
            RefreshScenarioList();
            if (_scenarioFiles.Count == 0) return;
            int i = _scenarioFiles.IndexOf(scenarioFile);
            i = ((i < 0 ? 0 : i + delta) % _scenarioFiles.Count + _scenarioFiles.Count) % _scenarioFiles.Count;
            LoadScenario(_scenarioFiles[i]);
        }

        private static GUIStyle _rich;
        private static GUIStyle Rich() => _rich ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
    }
}
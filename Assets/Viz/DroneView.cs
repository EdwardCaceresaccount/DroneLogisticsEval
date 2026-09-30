using System.Collections.Generic;
using SimCore.Domain;
using SimCore.Engine;
using SimCore.State;
using UnityEngine;

namespace Viz
{
    /// <summary>Reads drone state each frame and draws it. Planned route (cyan) vs actual trail (white) — the gap is the story.</summary>
    public sealed class DroneView : MonoBehaviour
    {
        private const float FlightHeight = 1.4f;
        private const float LandedHeight = 0.5f;
        private const int TrailEveryTicks = 4;

        private EpisodeRunner _runner;
        private GameObject _body;
        private Material _bodyMat;
        private LineRenderer _route;
        private LineRenderer _trail;
        private readonly List<Vector3> _trailPoints = new();
        private long _lastTrailTick = -1;

        private static readonly Color Landed = new(0.55f, 0.55f, 0.60f);
        private static readonly Color Flying = new(0.15f, 0.90f, 0.35f);
        private static readonly Color LowBattery = new(1.00f, 0.55f, 0.10f);
        private static readonly Color Failed = new(0.95f, 0.15f, 0.15f);

        private void Awake()
        {
            _body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _body.name = "DroneBody";
            _body.transform.SetParent(transform, false);
            _body.transform.localScale = Vector3.one * 0.7f;
            _bodyMat = GridRenderer.MakeUnlit(Landed);
            _body.GetComponent<Renderer>().sharedMaterial = _bodyMat;
            Destroy(_body.GetComponent<Collider>());

            _route = MakeLine("PlannedRoute", new Color(0.2f, 0.9f, 1f, 0.9f), 0.12f);
            _trail = MakeLine("ActualTrail", new Color(1f, 1f, 1f, 0.8f), 0.08f);
        }

        private LineRenderer MakeLine(string name, Color c, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = lr.endColor = c;
            lr.startWidth = lr.endWidth = width;
            lr.positionCount = 0;
            lr.useWorldSpace = true;
            return lr;
        }

        public void Bind(EpisodeRunner runner)
        {
            _runner = runner;
            _route.positionCount = 0;
            _trailPoints.Clear();
            _trail.positionCount = 0;
            _lastTrailTick = -1;
            Refresh();
        }

        public void OnTripStarted(IReadOnlyList<GridCoord> waypoints)
        {
            _trailPoints.Clear();
            _trail.positionCount = 0;
            var pts = new List<Vector3> { GridRenderer.ToWorld(_runner.World.Drone.Position.X, _runner.World.Drone.Position.Y, FlightHeight) };
            foreach (var w in waypoints) pts.Add(GridRenderer.ToWorld(w, FlightHeight));
            _route.positionCount = pts.Count;
            _route.SetPositions(pts.ToArray());
        }

        public void Refresh()
        {
            if (_runner == null) return;
            var d = _runner.World.Drone;
            float h = d.Flight == FlightStatus.Flying ? FlightHeight : LandedHeight;
            _body.transform.position = GridRenderer.ToWorld(d.Position.X, d.Position.Y, h);

            Color c = d.Failure != FailureKind.None ? Failed
                    : d.InLowBatteryState ? LowBattery
                    : d.Flight == FlightStatus.Flying ? Flying
                    : Landed;
            _bodyMat.color = c;

            long tick = _runner.World.Clock.Ticks;
            if (d.Flight == FlightStatus.Flying && tick - _lastTrailTick >= TrailEveryTicks)
            {
                _trailPoints.Add(GridRenderer.ToWorld(d.Position.X, d.Position.Y, FlightHeight - 0.05f));
                _trail.positionCount = _trailPoints.Count;
                _trail.SetPositions(_trailPoints.ToArray());
                _lastTrailTick = tick;
            }
        }
    }
}
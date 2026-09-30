using System.Collections.Generic;
using SimCore.Domain;
using UnityEngine;

namespace Viz
{
    /// <summary>
    /// Draws a GridMap as flat colored quads plus low blocks for non-Road tiles.
    /// Sim (x,y) → Unity (x, 0, y): sim y is Unity z, so "up on the map" is +z and the camera looks straight down.
    /// Rebuilt whole on every map change; cheap at 40×40.
    /// </summary>
    public sealed class GridRenderer : MonoBehaviour
    {
        private readonly Dictionary<TileType, Material> _mats = new();
        private readonly List<GameObject> _spawned = new();
        private GameObject _highlight;
        private Material _highlightMat;

        public static Vector3 ToWorld(double x, double y, float height = 0f) => new((float)x, height, (float)y);
        public static Vector3 ToWorld(GridCoord c, float height = 0f) => ToWorld(c.X, c.Y, height);

        public static Color ColorOf(TileType t) => t switch
        {
            TileType.Road            => new Color(0.82f, 0.82f, 0.80f),
            TileType.Facility        => new Color(0.20f, 0.45f, 0.95f),
            TileType.House           => new Color(0.30f, 0.75f, 0.35f),
            TileType.ChargingStation => new Color(0.98f, 0.80f, 0.15f),
            TileType.Building        => new Color(0.25f, 0.25f, 0.28f),
            _ => Color.magenta
        };

        public static Material MakeUnlit(Color c)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            return new Material(shader) { color = c };
        }

        private Material Mat(TileType t)
        {
            if (!_mats.TryGetValue(t, out var m)) { m = MakeUnlit(ColorOf(t)); _mats[t] = m; }
            return m;
        }

        public void Build(GridMap map)
        {
            foreach (var go in _spawned) if (go) Destroy(go);
            _spawned.Clear();

            for (int x = 1; x <= map.Size; x++)
                for (int y = 1; y <= map.Size; y++)
                {
                    var c = new GridCoord(x, y);
                    var type = map.TypeAt(c);

                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.name = $"T{x}_{y}";
                    quad.transform.SetParent(transform, false);
                    quad.transform.position = ToWorld(c, 0f);
                    quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    quad.transform.localScale = new Vector3(0.94f, 0.94f, 1f);
                    quad.GetComponent<Renderer>().sharedMaterial = Mat(type);
                    Destroy(quad.GetComponent<Collider>());
                    _spawned.Add(quad);

                    if (type == TileType.Road) continue;

                    float h = type switch
                    {
                        TileType.Building => 1.0f,
                        TileType.Facility => 0.5f,
                        TileType.ChargingStation => 0.35f,
                        TileType.House => 0.4f,
                        _ => 0.2f
                    };
                    var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    block.name = $"B{x}_{y}";
                    block.transform.SetParent(transform, false);
                    block.transform.position = ToWorld(c, h / 2f);
                    block.transform.localScale = new Vector3(type == TileType.Building ? 1.0f : 0.6f, h, type == TileType.Building ? 1.0f : 0.6f);
                    block.GetComponent<Renderer>().sharedMaterial = Mat(type);
                    Destroy(block.GetComponent<Collider>());
                    _spawned.Add(block);
                }

            if (_highlight == null)
            {
                _highlightMat = MakeUnlit(new Color(1f, 1f, 1f, 0.9f));
                _highlight = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _highlight.name = "Highlight";
                _highlight.transform.SetParent(transform, false);
                _highlight.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                _highlight.transform.localScale = new Vector3(1.05f, 1.05f, 1f);
                _highlight.GetComponent<Renderer>().sharedMaterial = _highlightMat;
                Destroy(_highlight.GetComponent<Collider>());
                _highlight.SetActive(false);
            }
        }

        public void SetHighlight(GridCoord? c)
        {
            if (_highlight == null) return;
            if (!c.HasValue) { _highlight.SetActive(false); return; }
            _highlight.SetActive(true);
            _highlight.transform.position = ToWorld(c.Value, 0.02f);
        }

        public static void FitCamera(int size)
        {
            var cam = Camera.main;
            if (cam == null) return;
            cam.orthographic = true;
            cam.transform.position = new Vector3(size / 2f + 0.5f, 60f, size / 2f + 0.5f);
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            cam.orthographicSize = size / 2f + 1.5f;
            cam.nearClipPlane = 1f;
            cam.farClipPlane = 200f;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
            cam.clearFlags = CameraClearFlags.SolidColor;
        }
    }
}
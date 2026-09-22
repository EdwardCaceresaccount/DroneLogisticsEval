using System;

namespace SimCore.Domain
{
    /// <summary>
    /// Deterministic geometry for straight-line flight segments against the tile grid.
    /// Segment-vs-tile uses the slab method (segment vs axis-aligned bounding box).
    /// This is the math behind collisions (D5: legal-but-bad routes crash) and
    /// behind the PROCESSED observation's path-obstruction facts.
    /// </summary>
    public static class GridGeometry
    {
        private const double Eps = 1e-12;

        /// <summary>
        /// Does the segment p0→p1 intersect the AABB [minX,maxX]x[minY,maxY]?
        /// tEntry = parametric point (0..1 along the segment) where it first enters.
        /// </summary>
        public static bool SegmentIntersectsAabb(
            WorldPos p0, WorldPos p1,
            double minX, double minY, double maxX, double maxY,
            out double tEntry)
        {
            double tMin = 0.0, tMax = 1.0;
            tEntry = 0.0;

            // X slab
            double dx = p1.X - p0.X;
            if (Math.Abs(dx) < Eps)
            {
                if (p0.X < minX || p0.X > maxX) return false;
            }
            else
            {
                double inv = 1.0 / dx;
                double t1 = (minX - p0.X) * inv;
                double t2 = (maxX - p0.X) * inv;
                if (t1 > t2) { (t1, t2) = (t2, t1); }
                tMin = Math.Max(tMin, t1);
                tMax = Math.Min(tMax, t2);
                if (tMin > tMax) return false;
            }

            // Y slab
            double dy = p1.Y - p0.Y;
            if (Math.Abs(dy) < Eps)
            {
                if (p0.Y < minY || p0.Y > maxY) return false;
            }
            else
            {
                double inv = 1.0 / dy;
                double t1 = (minY - p0.Y) * inv;
                double t2 = (maxY - p0.Y) * inv;
                if (t1 > t2) { (t1, t2) = (t2, t1); }
                tMin = Math.Max(tMin, t1);
                tMax = Math.Min(tMax, t2);
                if (tMin > tMax) return false;
            }

            tEntry = tMin;
            return true;
        }

        public static bool SegmentIntersectsTile(WorldPos p0, WorldPos p1, GridCoord tile, out double tEntry)
            => SegmentIntersectsAabb(p0, p1,
                tile.X - 0.5, tile.Y - 0.5, tile.X + 0.5, tile.Y + 0.5,
                out tEntry);

        /// <summary>
        /// First Building tile hit along p0→p1, if any. Scans only tiles overlapped by the
        /// segment's bounding box; returns the hit with the smallest entry parameter so the
        /// crash position is the *first* wall struck, not an arbitrary one.
        /// </summary>
        public static bool TryGetFirstBuildingOnSegment(
            GridMap map, WorldPos p0, WorldPos p1,
            out GridCoord hitTile, out double tEntry)
        {
            hitTile = default;
            tEntry = double.MaxValue;
            bool found = false;

            // Tile x overlaps value v when x-0.5 <= v <= x+0.5  →  x in [v-0.5, v+0.5].
            int xStart = (int)Math.Ceiling(Math.Min(p0.X, p1.X) - 0.5);
            int xEnd   = (int)Math.Floor(Math.Max(p0.X, p1.X) + 0.5);
            int yStart = (int)Math.Ceiling(Math.Min(p0.Y, p1.Y) - 0.5);
            int yEnd   = (int)Math.Floor(Math.Max(p0.Y, p1.Y) + 0.5);

            xStart = Math.Max(1, xStart); xEnd = Math.Min(map.Size, xEnd);
            yStart = Math.Max(1, yStart); yEnd = Math.Min(map.Size, yEnd);

            for (int x = xStart; x <= xEnd; x++)
            {
                for (int y = yStart; y <= yEnd; y++)
                {
                    var c = new GridCoord(x, y);
                    if (map.TypeAt(c) != TileType.Building) continue;
                    if (SegmentIntersectsTile(p0, p1, c, out double t) && t < tEntry)
                    {
                        tEntry = t;
                        hitTile = c;
                        found = true;
                    }
                }
            }
            return found;
        }

        /// <summary>Convenience: is the direct path clear of Buildings?</summary>
        public static bool DirectPathClear(GridMap map, WorldPos p0, WorldPos p1)
            => !TryGetFirstBuildingOnSegment(map, p0, p1, out _, out _);
    }
}
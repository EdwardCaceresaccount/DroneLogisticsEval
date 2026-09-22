using System;

namespace SimCore.Domain
{
    /// <summary>
    /// Immutable integer grid coordinate. 1-indexed per spec: tile (1,1) is the
    /// first tile (first quadrant). Tile (x,y) has its center at WorldPos (x,y)
    /// and covers the square [x-0.5, x+0.5] x [y-0.5, y+0.5].
    /// </summary>
    public readonly struct GridCoord : IEquatable<GridCoord>
    {
        public readonly int X;
        public readonly int Y;

        public GridCoord(int x, int y) { X = x; Y = y; }

        public WorldPos Center => new WorldPos(X, Y);

        public double DistanceTo(GridCoord other)
        {
            double dx = X - other.X, dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public bool Equals(GridCoord other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is GridCoord g && Equals(g);
        public override int GetHashCode() => (X * 397) ^ Y;
        public override string ToString() => $"({X},{Y})";

        public static bool operator ==(GridCoord a, GridCoord b) => a.Equals(b);
        public static bool operator !=(GridCoord a, GridCoord b) => !a.Equals(b);
    }

    /// <summary>
    /// Continuous position in tile units (double precision). Used for the drone's
    /// in-flight position along route segments. Never a Unity Vector3 —
    /// the sim core has no engine types by design.
    /// </summary>
    public readonly struct WorldPos
    {
        public readonly double X;
        public readonly double Y;

        public WorldPos(double x, double y) { X = x; Y = y; }

        public double DistanceTo(WorldPos other)
        {
            double dx = X - other.X, dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Linear interpolation from this position toward 'target' by parameter t in [0,1].</summary>
        public WorldPos Lerp(WorldPos target, double t)
            => new WorldPos(X + (target.X - X) * t, Y + (target.Y - Y) * t);

        /// <summary>Nearest integer tile containing this position.</summary>
        public GridCoord NearestTile()
            => new GridCoord((int)Math.Round(X, MidpointRounding.AwayFromZero),
                             (int)Math.Round(Y, MidpointRounding.AwayFromZero));

        public override string ToString() => $"({X:F3},{Y:F3})";
    }
}
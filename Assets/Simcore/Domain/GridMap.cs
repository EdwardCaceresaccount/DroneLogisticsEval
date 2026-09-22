using System;
using System.Collections.Generic;

namespace SimCore.Domain
{
    /// <summary>
    /// Authoritative tile map. 1-indexed square grid; index 0 is unused padding
    /// so code reads naturally against the spec's (1,1)-origin coordinates.
    /// Pure data + queries. No rendering, no Unity.
    /// </summary>
    public sealed class GridMap
    {
        public int Size { get; }
        private readonly TileType[,] _tiles;

        public GridMap(int size)
        {
            if (size < 5 || size > 200)
                throw new ArgumentOutOfRangeException(nameof(size),
                    $"Grid size must be 5..200, got {size}.");
            Size = size;
            _tiles = new TileType[size + 1, size + 1]; // defaults to Road (enum value 0)
        }

        public bool InBounds(GridCoord c) => c.X >= 1 && c.X <= Size && c.Y >= 1 && c.Y <= Size;

        /// <summary>A continuous position is in bounds if it lies within the union of all tile squares.</summary>
        public bool InBounds(WorldPos p)
            => p.X >= 0.5 && p.X <= Size + 0.5 && p.Y >= 0.5 && p.Y <= Size + 0.5;

        public TileType TypeAt(GridCoord c)
        {
            if (!InBounds(c)) throw new ArgumentOutOfRangeException(nameof(c), $"{c} outside {Size}x{Size} grid.");
            return _tiles[c.X, c.Y];
        }

        public void SetType(GridCoord c, TileType type)
        {
            if (!InBounds(c)) throw new ArgumentOutOfRangeException(nameof(c), $"{c} outside {Size}x{Size} grid.");
            _tiles[c.X, c.Y] = type;
        }

        public bool IsChargeTile(GridCoord c)
        {
            var t = TypeAt(c);
            return t == TileType.Facility || t == TileType.ChargingStation;
        }

        /// <summary>Deterministic scan order (x-major then y) — every caller sees the same ordering.</summary>
        public IReadOnlyList<GridCoord> AllOfType(TileType type)
        {
            var result = new List<GridCoord>();
            for (int x = 1; x <= Size; x++)
                for (int y = 1; y <= Size; y++)
                    if (_tiles[x, y] == type)
                        result.Add(new GridCoord(x, y));
            return result;
        }
    }
}
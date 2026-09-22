namespace SimCore.Domain
{
    /// <summary>Tile taxonomy per spec §5. Road MUST stay = 0: new grids default to Road.</summary>
    public enum TileType
    {
        Road = 0,
        Facility = 1,
        House = 2,
        ChargingStation = 3,
        Building = 4
    }

    public enum SpeedMode { Slow, Normal, Fast }

    /// <summary>Distance-band difficulty of a package (D7: distinct from GuidanceTier).</summary>
    public enum PackageDifficulty { Easy, Normal, Hard }

    /// <summary>Instruction condition supplied to a planner (D7: distinct from PackageDifficulty).
    /// Full = explicit strategy guidance, Partial = task rules only, None = tools + objective only.</summary>
    public enum GuidanceTier { Full, Partial, None }

    public enum FlightStatus { Landed, Flying }

    public enum PackageStatus { AtFacility, Loaded, Delivered }
}
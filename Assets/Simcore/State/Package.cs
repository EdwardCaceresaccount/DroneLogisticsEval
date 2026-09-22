using SimCore.Domain;

namespace SimCore.State
{
    /// <summary>A deliverable. Origin is always a Facility tile; Destination always a House tile
    /// (the loader enforces this — bad scenarios fail loudly at load, never mid-episode).</summary>
    public sealed class Package
    {
        public string Id { get; }
        public GridCoord Origin { get; }
        public GridCoord Destination { get; }
        public PackageDifficulty Difficulty { get; }
        public PackageStatus Status { get; set; } = PackageStatus.AtFacility;

        public Package(string id, GridCoord origin, GridCoord destination, PackageDifficulty difficulty)
        {
            Id = id;
            Origin = origin;
            Destination = destination;
            Difficulty = difficulty;
        }
    }
}
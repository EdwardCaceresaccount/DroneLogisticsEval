using NUnit.Framework;
using SimCore.Config;
using SimCore.Domain;
using System.IO;

namespace SimCore.Tests
{
    public class FoundationTests
    {
        private static string ScenarioPath(string name)
            => Path.Combine(UnityEngine.Application.streamingAssetsPath, "Scenarios", name);

        [Test]
        public void Segment_Through_Building_Is_Detected()
        {
            var map = new GridMap(20);
            map.SetType(new GridCoord(10, 10), TileType.Building);

            bool hit = GridGeometry.TryGetFirstBuildingOnSegment(
                map, new WorldPos(5, 10), new WorldPos(15, 10), out var tile, out _);

            Assert.IsTrue(hit, "A horizontal segment through (10,10) must hit the building.");
            Assert.AreEqual(new GridCoord(10, 10), tile);
        }

        [Test]
        public void Segment_Beside_Building_Is_Clear()
        {
            var map = new GridMap(20);
            map.SetType(new GridCoord(10, 10), TileType.Building);

            // y = 12 passes a full tile above the building's top edge (10.5).
            Assert.IsTrue(GridGeometry.DirectPathClear(
                map, new WorldPos(5, 12), new WorldPos(15, 12)));
        }

        [Test]
        public void Easy001_Loads_And_Builds()
        {
            string json = File.ReadAllText(ScenarioPath("Easy-001.json"));
            var cfg = ScenarioLoader.Parse(json);
            var world = ScenarioLoader.Build(cfg);

            Assert.AreEqual(6, world.Packages.Count);
            Assert.AreEqual(100.0, world.Drone.BatteryPct, 1e-9);
            Assert.AreEqual(new GridCoord(4, 4), world.Drone.LandedTile);
            Assert.AreEqual(TileType.Facility, world.Map.TypeAt(new GridCoord(16, 15)));
        }

        [Test]
public void Seeded_Generation_Is_Reproducible()
{
    string json = File.ReadAllText(ScenarioPath("Easy-001.json"));
    var cfg = ScenarioLoader.Parse(json);

    var a = ScenarioLoader.Build(cfg);
    var b = ScenarioLoader.Build(cfg);

    Assert.AreEqual(a.Packages.Count, b.Packages.Count);
    foreach (var id in a.Packages.Keys)
        Assert.AreEqual(a.Packages[id].Destination, b.Packages[id].Destination,
            $"Same seed must produce identical destination for {id}.");
}
    }
}
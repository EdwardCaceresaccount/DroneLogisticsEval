using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using SimCore.Config;
using SimCore.Domain;
using SimCore.Observation;
using SimCore.Planning;

namespace SimCore.Tests
{
    public class ObservationEquivalenceTests
    {
        private static string Json(object o) => JsonConvert.SerializeObject(o, Observation.Observation.JsonSettings);

        [Test]
        public void Processed_Is_Fully_Derivable_From_Raw()
        {
            var (e, _) = TestFixtures.Engine40();
            var raw = ObservationCompiler.CompileRaw(e.World, e);
            var pro = ObservationCompiler.CompileProcessed(e.World, e);

            // Rebuild the world's geometry from RAW alone.
            var map = new GridMap(raw.GridSize);
            foreach (var t in raw.Tiles) map.SetType(new GridCoord(t.X, t.Y), t.Type);
            var dronePos = raw.Drone.Tile.ToCoord().Center;
            var physics = raw.Physics;

            // Shared sections must be identical.
            Assert.AreEqual(Json(raw.Drone), Json(pro.Drone));
            Assert.AreEqual(Json(raw.Mission), Json(pro.Mission));
            Assert.AreEqual(Json(raw.Physics), Json(pro.Physics));
            Assert.AreEqual(raw.GridSize, pro.GridSize);

            // Obstacles == raw Building tiles.
            var rawBuildings = raw.Tiles.Where(t => t.Type == TileType.Building).Select(t => $"{t.X},{t.Y}").OrderBy(s => s).ToList();
            CollectionAssert.AreEqual(rawBuildings, pro.Obstacles.Select(o => $"{o.X},{o.Y}").OrderBy(s => s).ToList());

            // Charge tiles == raw Facility/ChargingStation tiles, with distance/path/battery recomputed from RAW.
            var rawCharge = raw.Tiles.Where(t => t.Type == TileType.Facility || t.Type == TileType.ChargingStation)
                                     .Select(t => $"{t.X},{t.Y}").OrderBy(s => s).ToList();
            CollectionAssert.AreEqual(rawCharge, pro.ChargeTiles.Select(c => $"{c.Tile.X},{c.Tile.Y}").OrderBy(s => s).ToList());
            foreach (var c in pro.ChargeTiles)
            {
                var center = c.Tile.ToCoord().Center;
                Assert.AreEqual(dronePos.DistanceTo(center), c.Distance, 1e-9);
                Assert.AreEqual(GridGeometry.DirectPathClear(map, dronePos, center), c.DirectPathClear);
                Assert.AreEqual(physics.BatteryRequiredPct(c.Distance, SpeedMode.Normal), c.BatteryRequired.Normal, 1e-9);
                Assert.AreEqual(physics.BatteryRequiredPct(c.Distance, SpeedMode.Slow), c.BatteryRequired.Slow, 1e-9);
            }

            // Packages: every derived number reproduces from RAW packages + RAW geometry + RAW physics.
            var rawUndelivered = raw.Packages.Where(p => p.Status != PackageStatus.Delivered).Select(p => p.Id).OrderBy(s => s).ToList();
            CollectionAssert.AreEqual(rawUndelivered, pro.Packages.Select(p => p.Id).OrderBy(s => s).ToList());
            foreach (var pp in pro.Packages)
            {
                var rp = raw.Packages.First(p => p.Id == pp.Id);
                var dest = rp.Destination.ToCoord().Center;
                Assert.AreEqual(rp.Origin.ToCoord().DistanceTo(rp.Destination.ToCoord()), pp.DistanceOriginToDestination, 1e-9);
                Assert.AreEqual(dronePos.DistanceTo(dest), pp.DistanceFromDroneToDestination, 1e-9);
                Assert.AreEqual(GridGeometry.DirectPathClear(map, dronePos, dest), pp.DirectPathClearFromDrone);
                Assert.AreEqual(physics.BatteryRequiredPct(pp.DistanceFromDroneToDestination, SpeedMode.Normal), pp.BatteryRequiredFromDrone.Normal, 1e-9);
                Assert.AreEqual(rp.Status == PackageStatus.AtFacility && rp.Origin.X == raw.Drone.Tile.X && rp.Origin.Y == raw.Drone.Tile.Y, pp.AvailableHere);
                Assert.AreEqual(raw.Drone.Inventory.Contains(pp.Id), pp.OnBoard);
            }
        }

        [Test]
        public void Compiling_Does_Not_Mutate_The_World()
        {
            var (e, _) = TestFixtures.Engine40();
            string before = ObservationCompiler.CompileRaw(e.World, e).ToJson();
            double battery = e.World.Drone.BatteryPct;
            long ticks = e.World.Clock.Ticks;

            ObservationCompiler.CompileProcessed(e.World, e);
            ObservationCompiler.CompileProcessed(e.World, e);

            Assert.AreEqual(before, ObservationCompiler.CompileRaw(e.World, e).ToJson());
            Assert.AreEqual(battery, e.World.Drone.BatteryPct, 0.0);
            Assert.AreEqual(ticks, e.World.Clock.Ticks);
        }

        [Test]
        public void Observation_Is_A_Snapshot_Not_A_Live_View()
        {
            var (e, _) = TestFixtures.Engine40();
            var obs = ObservationCompiler.CompileProcessed(e.World, e);
            Assert.AreEqual(0, obs.Drone.Inventory.Count);

            e.BeginLoad("P001");
            while (e.IsBusy) e.Tick();

            Assert.AreEqual(0, obs.Drone.Inventory.Count, "A compiled observation must not change when the world does.");
            Assert.AreEqual(1, ObservationCompiler.CompileProcessed(e.World, e).Drone.Inventory.Count);
        }

        [Test]
        public void Same_State_Compiles_To_Identical_Json()
        {
            var (e, _) = TestFixtures.Engine40();
            Assert.AreEqual(ObservationCompiler.CompileProcessed(e.World, e).ToJson(), ObservationCompiler.CompileProcessed(e.World, e).ToJson());
            Assert.AreEqual(ObservationCompiler.CompileRaw(e.World, e).ToJson(), ObservationCompiler.CompileRaw(e.World, e).ToJson());
        }

        [Test]
        public void Observation_Sizes_Are_Measured_And_Bounded()
        {
            var (e, _) = TestFixtures.Engine40();
            string raw = ObservationCompiler.CompileRaw(e.World, e).ToJson();
            string pro = ObservationCompiler.CompileProcessed(e.World, e).ToJson();

            string easy = File.ReadAllText(Path.Combine(UnityEngine.Application.streamingAssetsPath, "Scenarios", "Easy-001.json"));
            var cfg = ScenarioLoader.Parse(easy);
            var world = ScenarioLoader.Build(cfg);
            var engine = new Engine.SimulationEngine(world, cfg, new Events.ListEventSink());
            string easyRaw = ObservationCompiler.CompileRaw(world, engine).ToJson();
            string easyPro = ObservationCompiler.CompileProcessed(world, engine).ToJson();

            TestContext.WriteLine($"Fixture40  RAW {raw.Length} chars ≈ {TokenEstimate.Of(raw)} tokens | PROCESSED {pro.Length} chars ≈ {TokenEstimate.Of(pro)} tokens");
            TestContext.WriteLine($"Easy-001   RAW {easyRaw.Length} chars ≈ {TokenEstimate.Of(easyRaw)} tokens | PROCESSED {easyPro.Length} chars ≈ {TokenEstimate.Of(easyPro)} tokens");

            Assert.Less(TokenEstimate.Of(easyPro), 6000, "PROCESSED for a 20x20 / 6-package scenario must stay well inside any provider's context.");
            Assert.Less(TokenEstimate.Of(easyRaw), 6000);
        }
    }
}
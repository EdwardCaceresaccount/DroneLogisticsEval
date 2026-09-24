using NUnit.Framework;
using SimCore.Config;
using SimCore.Domain;
using SimCore.Engine;
using SimCore.Events;
using SimCore.State;

namespace SimCore.Tests
{
    public class EngineTests
    {
        // ---- fixture: a 40x40 map built in code so tests never depend on a JSON file ----

        private static ScenarioConfig MakeConfig()
        {
            var cfg = new ScenarioConfig { ScenarioId = "EngineTest", GridSize = 40, TickSeconds = 0.05 };
            cfg.Tiles.Add(new TileSpec { X = 4,  Y = 4,  Type = "Facility" });
            cfg.Tiles.Add(new TileSpec { X = 20, Y = 4,  Type = "ChargingStation" });
            cfg.Tiles.Add(new TileSpec { X = 9,  Y = 7,  Type = "House" });
            cfg.Tiles.Add(new TileSpec { X = 10, Y = 20, Type = "Building" });
            cfg.Packages.Add(new PackageSpec
            {
                Id = "P001", Difficulty = "Easy",
                Origin = new CoordSpec { X = 4, Y = 4 },
                Destination = new CoordSpec { X = 9, Y = 7 }
            });
            return cfg;
        }

        private static (SimulationEngine engine, ListEventSink sink) MakeEngine(double startBattery = 100.0)
        {
            var cfg = MakeConfig();
            cfg.Drone.StartingBatteryPct = startBattery;
            var world = ScenarioLoader.Build(cfg);
            var sink = new ListEventSink();
            return (new SimulationEngine(world, cfg, sink), sink);
        }

        private static void RunUntil(SimulationEngine e, System.Func<bool> done, int maxTicks = 100000)
        {
            int n = 0;
            while (!done() && n++ < maxTicks) e.Tick();
            Assert.Less(n, maxTicks, "Condition never satisfied — runaway simulation.");
        }

        // ---- spec numbers ----

        [Test]
        public void Normal_Speed_Range_Is_36_Tiles()
        {
            var (e, _) = MakeEngine();
            Assert.AreEqual(36.0, e.RangeTilesAtSpeed(SpeedMode.Normal, 100.0), 1e-9,
                "3 tiles/s × 12 s of flight must equal 36 tiles.");
        }

        [Test]
        public void Hovering_Normal_Speed_Lasts_12_Seconds_Then_Forced_Landing()
        {
            var (e, sink) = MakeEngine();
            e.LiftOff();
            e.BeginWait(60);                                   // hover far longer than the battery lasts
            RunUntil(e, () => e.IsTerminal);

            Assert.AreEqual(FailureKind.ForcedLanding, e.World.Drone.Failure);
            Assert.AreEqual(0.0, e.World.Drone.BatteryPct, 1e-9);
            Assert.That(e.World.Clock.TimeSeconds, Is.InRange(12.0, 12.1),
                "100% at 8.333%/s must last 12.0 s (± one tick).");
            Assert.IsTrue(sink.Contains(SimEventTypes.ForcedLanding));
        }

        [Test]
        public void Empty_To_Full_Charge_Takes_6_Seconds()
        {
            var (e, sink) = MakeEngine(startBattery: 0.0);
            e.BeginCharge(100);
            Assert.IsTrue(e.IsCharging);
            RunUntil(e, () => !e.IsCharging);

            Assert.AreEqual(100.0, e.World.Drone.BatteryPct, 1e-9);
            Assert.That(e.World.Clock.TimeSeconds, Is.InRange(6.0, 6.1));
            Assert.IsTrue(sink.Contains(SimEventTypes.ChargeCompleted));
        }

        // ---- movement ----

        [Test]
        public void Move_Arrives_Exactly_On_Target_And_Drains_Expected_Battery()
        {
            var (e, sink) = MakeEngine();
            e.LiftOff();
            e.BeginMoveTo(new GridCoord(4, 34).Center);        // 30 tiles due north
            RunUntil(e, () => !e.IsBusy);

            Assert.AreEqual(4.0,  e.World.Drone.Position.X, 1e-12);
            Assert.AreEqual(34.0, e.World.Drone.Position.Y, 1e-12, "Arrival must snap exactly to the target.");
            Assert.That(e.World.Clock.TimeSeconds, Is.InRange(10.0, 10.1), "30 tiles at 3 tiles/s = 10 s.");
            Assert.AreEqual(100.0 - 30.0 / 36.0 * 100.0, e.World.Drone.BatteryPct, 0.5,
                "30 of 36 range tiles used → ~16.67% left.");
            Assert.IsTrue(sink.Contains(SimEventTypes.MoveArrived));
        }

        [Test]
        public void Fast_Speed_Is_Faster_But_Burns_More_Per_Tile()
        {
            var (fast, _) = MakeEngine();
            fast.SetSpeed(SpeedMode.Fast);
            fast.LiftOff();
            fast.BeginMoveTo(new GridCoord(4, 24).Center);     // 20 tiles
            RunUntil(fast, () => !fast.IsBusy);

            var (normal, _) = MakeEngine();
            normal.LiftOff();
            normal.BeginMoveTo(new GridCoord(4, 24).Center);
            RunUntil(normal, () => !normal.IsBusy);

            Assert.Less(fast.World.Clock.TimeSeconds, normal.World.Clock.TimeSeconds, "Fast must arrive sooner.");
            Assert.Less(fast.World.Drone.BatteryPct, normal.World.Drone.BatteryPct, "Fast must land with less battery.");
        }

        [Test]
        public void Flying_Through_Building_Crashes_Before_The_Wall()
        {
            var (e, sink) = MakeEngine();
            e.LiftOff();
            e.BeginMoveTo(new GridCoord(4, 20).Center);        // climb to row 20
            RunUntil(e, () => !e.IsBusy);
            e.BeginMoveTo(new GridCoord(16, 20).Center);       // straight through Building (10,20)
            RunUntil(e, () => e.IsTerminal);

            Assert.AreEqual(FailureKind.Crashed, e.World.Drone.Failure);
            Assert.LessOrEqual(e.World.Drone.Position.X, 9.5 + 1e-9, "Crash position is the wall's near face.");
            Assert.IsTrue(sink.Contains(SimEventTypes.Crash));
            Assert.Throws<SimInvariantException>(() => e.Tick(), "Terminal engine must refuse further ticks.");
        }

        // ---- packages ----

        [Test]
        public void Load_Takes_Configured_Seconds_Then_Package_Is_Onboard()
        {
            var (e, sink) = MakeEngine();
            e.BeginLoad("P001");
            Assert.AreEqual(PackageStatus.AtFacility, e.World.Packages["P001"].Status);
            RunUntil(e, () => !e.IsBusy);

            Assert.AreEqual(PackageStatus.Loaded, e.World.Packages["P001"].Status);
            CollectionAssert.Contains(e.World.Drone.LoadedPackageIds, "P001");
            Assert.That(e.World.Clock.TimeSeconds, Is.InRange(2.0, 2.1));
            Assert.IsTrue(sink.Contains(SimEventTypes.PackageLoaded));
        }

        [Test]
        public void Charge_Overlaps_Loading_And_Blocks_LiftOff_Until_Done()
        {
            var (e, _) = MakeEngine(startBattery: 0.0);
            e.BeginCharge(100);
            e.BeginLoad("P001");                               // D10: allowed while charging
            RunUntil(e, () => !e.IsBusy);                      // 2 s later: loaded, still charging

            Assert.IsTrue(e.IsCharging);
            Assert.Throws<SimInvariantException>(() => e.LiftOff(), "LiftOff must be refused mid-charge.");
            RunUntil(e, () => !e.IsCharging);
            Assert.That(e.World.Clock.TimeSeconds, Is.InRange(6.0, 6.1), "Total = 6 s, not 8 — the load overlapped.");
            e.LiftOff();                                       // now allowed
            Assert.AreEqual(FlightStatus.Flying, e.World.Drone.Flight);
        }

        [Test]
        public void Full_Delivery_Sequence_Marks_Package_Delivered()
        {
            var (e, sink) = MakeEngine();
            e.BeginLoad("P001");
            RunUntil(e, () => !e.IsBusy);
            e.LiftOff();
            e.BeginMoveTo(new GridCoord(9, 7).Center);
            RunUntil(e, () => !e.IsBusy);
            e.Deliver("P001");

            Assert.AreEqual(PackageStatus.Delivered, e.World.Packages["P001"].Status);
            Assert.AreEqual(0, e.World.Drone.LoadedPackageIds.Count);
            Assert.AreEqual(1, e.World.DeliveredCount());
            Assert.IsTrue(sink.Contains(SimEventTypes.PackageDelivered));
        }

        [Test]
        public void Deliver_Away_From_Destination_Is_An_Invariant_Violation()
        {
            var (e, _) = MakeEngine();
            e.BeginLoad("P001");
            RunUntil(e, () => !e.IsBusy);
            e.LiftOff();
            Assert.Throws<SimInvariantException>(() => e.Deliver("P001"));
        }

        // ---- determinism ----

        [Test]
        public void Identical_Inputs_Produce_Identical_State()
        {
            static SimulationEngine Script()
            {
                var (e, _) = MakeEngine();
                e.BeginLoad("P001");
                RunUntil(e, () => !e.IsBusy);
                e.LiftOff();
                e.SetSpeed(SpeedMode.Slow);
                e.BeginMoveTo(new GridCoord(9, 7).Center);
                RunUntil(e, () => !e.IsBusy);
                e.Deliver("P001");
                e.BeginMoveTo(new GridCoord(20, 4).Center);
                RunUntil(e, () => !e.IsBusy);
                e.Land();
                return e;
            }

            var a = Script();
            var b = Script();

            Assert.AreEqual(a.World.Clock.Ticks, b.World.Clock.Ticks);
            Assert.AreEqual(a.World.Drone.BatteryPct, b.World.Drone.BatteryPct, 0.0);
            Assert.AreEqual(a.World.Drone.Position.X, b.World.Drone.Position.X, 0.0);
            Assert.AreEqual(a.World.Drone.Position.Y, b.World.Drone.Position.Y, 0.0);
            Assert.AreEqual(a.World.Drone.LandedTile, b.World.Drone.LandedTile);
            Assert.AreEqual(TileType.ChargingStation, a.World.Map.TypeAt(a.World.Drone.LandedTile.Value));
        }
    }
}
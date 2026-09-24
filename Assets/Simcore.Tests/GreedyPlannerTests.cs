using System;
using System.Linq;
using NUnit.Framework;
using SimCore.Commands;
using SimCore.Config;
using SimCore.Domain;
using SimCore.Engine;
using SimCore.Eval;
using SimCore.Events;
using SimCore.Observation;
using SimCore.Planning;
using SimCore.State;

namespace SimCore.Tests
{
    public class GreedyPlannerTests
    {
        private static EpisodeRunner MakeRunner(Action<ScenarioConfig> tweak = null, ObservationMode mode = ObservationMode.Processed)
        {
            var cfg = TestFixtures.Config40();
            tweak?.Invoke(cfg);
            var world = ScenarioLoader.Build(cfg);
            var log = new EventLog();
            var engine = new SimulationEngine(world, cfg, log);
            var planner = new GreedyPlanner();
            var episode = new EpisodeConfig { ScenarioId = cfg.ScenarioId, PlannerId = planner.Id, ObservationMode = mode };
            return new EpisodeRunner(engine, planner, new ObservationCompiler(), episode, log);
        }

        private static Plan PlanFor(SimulationEngine e, ObservationMode mode)
        {
            var obs = new ObservationCompiler().Compile(e.World, e, mode);
            var resp = new GreedyPlanner().Plan(new PlanRequest(obs, e.World.TripIndex + 1, 1, null, null, GuidanceTier.None));
            Assert.IsNotNull(resp.Plan, resp.Errors.ToString());
            return resp.Plan;
        }

        [Test]
        public void First_Trip_Loads_Two_Nearest_Delivers_Both_And_Returns_To_Facility_With_Packages()
        {
            var (e, _) = TestFixtures.Engine40();
            var plan = PlanFor(e, ObservationMode.Processed);

            Assert.AreEqual(
                "LOAD(P001) → LOAD(P003) → LIFT_OFF → SET_SPEED(Normal) → MOVE_TO(9,7) → DELIVER(P001) → " +
                "MOVE_TO(15,15) → DELIVER(P003) → MOVE_TO(4,4) → LAND",
                plan.ToString());
        }

        [Test]
        public void Charges_First_When_Battery_Is_Below_Capacity()
        {
            var (e, _) = TestFixtures.Engine40(startBattery: 30.0);
            var plan = PlanFor(e, ObservationMode.Processed);
            Assert.AreEqual(CommandType.CHARGE, plan.Commands[0].Type);
            Assert.AreEqual(100.0, plan.Commands[0].Value.Value, 1e-9);
        }

        [Test]
        public void Full_Episode_Uses_Charger_As_Stepping_Stone_For_The_Far_Package()
        {
            var r = MakeRunner();
            r.RunEpisode();

            Assert.AreEqual(EpisodeStatus.CompletedSuccess, r.World.Status, r.TerminationReason);
            Assert.AreEqual(3, r.Trips.Count);
            Assert.AreEqual(TripClassification.StrongSuccess, r.Trips[0].Classification);
            Assert.AreEqual(2, r.Trips[0].PackagesDelivered);
            Assert.AreEqual(TripClassification.NoDeliverySafeReturn, r.Trips[1].Classification);
            Assert.AreEqual(new GridCoord(30, 20), r.Trips[1].EndTile, "Stepping stone closest to P002's destination (30,30).");
            Assert.AreEqual(1, r.Trips[1].PackagesLoaded, "Carries P002 to the stepping stone.");
            Assert.AreEqual(TripClassification.StrongSuccess, r.Trips[2].Classification);
            Assert.IsFalse(r.Trips.Any(t => t.LowBatteryOccurred), "Greedy plans inside the 2× margin; the simulator never rescues it.");
        }

        [Test]
        public void Produces_The_Same_Plan_From_Raw_And_Processed_Observations()
        {
            var (e, _) = TestFixtures.Engine40();
            Assert.AreEqual(PlanFor(e, ObservationMode.Processed).ToString(), PlanFor(e, ObservationMode.Raw).ToString(),
                "The baseline must not depend on derived information.");
        }

        [Test]
        public void Is_Deterministic_Across_Runs()
        {
            var a = MakeRunner(); a.RunEpisode();
            var b = MakeRunner(); b.RunEpisode();
            Assert.AreEqual(a.Trips.Count, b.Trips.Count);
            for (int i = 0; i < a.Trips.Count; i++)
            {
                Assert.AreEqual(a.Trips[i].Classification, b.Trips[i].Classification);
                Assert.AreEqual(a.Trips[i].BatteryEnd, b.Trips[i].BatteryEnd, 0.0);
                Assert.AreEqual(a.Trips[i].DistanceTraveled, b.Trips[i].DistanceTraveled, 0.0);
            }
            Assert.AreEqual(a.World.Clock.Ticks, b.World.Clock.Ticks);
        }

        [Test]
        public void Detours_Around_A_Building_On_The_First_Leg()
        {
            // Building (7,6) sits on the (4,4)→(9,7) line.
            var r = MakeRunner(cfg => cfg.Tiles.Add(new TileSpec { X = 7, Y = 6, Type = "Building" }));
            r.StartTrip();
            var accepted = (string)r.Log.Last(EpisodeEventTypes.PlanAccepted).Data["plan"];
            int firstDeliver = accepted.IndexOf("DELIVER(P001)", StringComparison.Ordinal);
            int moveCount = accepted.Substring(0, firstDeliver).Split("MOVE_TO").Length - 1;
            Assert.AreEqual(2, moveCount, "Exactly one detour waypoint before the destination.");

            r.RunTripToEnd();
            Assert.AreEqual(TripClassification.StrongSuccess, r.Trips[0].Classification);
            Assert.AreEqual(0, r.Log.Count(SimEventTypes.Crash));
        }

        [Test]
        public void At_A_Charger_With_Nothing_Carried_Repositions_To_A_Facility_With_Packages()
        {
            var (e, _) = TestFixtures.Engine40();
            e.LiftOff();
            e.BeginMoveTo(new GridCoord(20, 4).Center);
            while (e.IsBusy) e.Tick();
            e.Land();
            e.World.TripIndex = 1;

            var plan = PlanFor(e, ObservationMode.Processed);
            Assert.AreEqual(CommandType.CHARGE, plan.Commands[0].Type);
            Assert.IsFalse(plan.Commands.Any(c => c.Type == CommandType.LOAD || c.Type == CommandType.DELIVER));
            Assert.AreEqual(new GridCoord(4, 4), plan.Commands.Last(c => c.Type == CommandType.MOVE_TO).Target);
            Assert.AreEqual(CommandType.LAND, plan.Commands[plan.Commands.Count - 1].Type);
        }

        [Test]
        public void Processed_Observation_Facts_Are_Consistent_With_The_Simulator()
        {
            var (e, _) = TestFixtures.Engine40();
            var obs = ObservationCompiler.CompileProcessed(e.World, e);

            Assert.AreEqual(36.0, obs.RangeNowTiles.Normal, 1e-9);
            Assert.AreEqual(3, obs.ChargeTiles.Count);
            Assert.AreEqual(3, obs.Packages.Count);
            var p1 = obs.Packages.First(p => p.Id == "P001");
            Assert.IsTrue(p1.AvailableHere);
            Assert.IsTrue(p1.DirectPathClearFromDrone);
            Assert.AreEqual(Math.Sqrt(34), p1.DistanceFromDroneToDestination, 1e-9);
            Assert.IsTrue(p1.FeasibleNowNormal);
            Assert.AreEqual(1, obs.Obstacles.Count);
            Assert.IsTrue(obs.ChargeTiles.First(c => c.Tile.X == 4 && c.Tile.Y == 4).IsCurrentLocation);
            Assert.AreEqual(3, obs.ChargeTiles.First(c => c.IsCurrentLocation).AvailablePackages);

            var raw = ObservationCompiler.CompileRaw(e.World, e);
            Assert.AreEqual(7, raw.Tiles.Count, "Every non-Road tile.");
            StringAssert.Contains("\"observation_mode\":\"Raw\"", raw.ToJson());
            StringAssert.Contains("\"observation_mode\":\"Processed\"", obs.ToJson());
        }
    }
}
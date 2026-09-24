using System.Collections.Generic;
using NUnit.Framework;
using SimCore.Commands;
using SimCore.Domain;
using SimCore.Engine;
using SimCore.State;

namespace SimCore.Tests
{
    public class CommandTests
    {
        private const string GoodJson = @"{
          ""tool_calls"": [
            { ""tool"": ""CHARGE"",    ""args"": { ""target_pct"": 100 } },
            { ""tool"": ""LOAD"",      ""args"": { ""package_id"": ""P001"" } },
            { ""tool"": ""LIFT_OFF"" },
            { ""tool"": ""SET_SPEED"", ""args"": { ""mode"": ""fast"" } },
            { ""tool"": ""MOVE_TO"",   ""args"": { ""x"": 9, ""y"": 7 } },
            { ""tool"": ""DELIVER"",   ""args"": { ""package_id"": ""P001"" } },
            { ""tool"": ""WAIT"",      ""args"": { ""seconds"": 0.5 } },
            { ""tool"": ""MOVE_TO"",   ""args"": { ""x"": 20, ""y"": 4 } },
            { ""tool"": ""LAND"" }
          ]}";

        private static void RunToEnd(PlanExecutor ex, int maxTicks = 100000)
        {
            int n = 0;
            while (!ex.IsDone && n++ < maxTicks) ex.Tick();
            Assert.Less(n, maxTicks, "Executor never finished.");
        }

        // ---------------- Parser ----------------

        [Test]
        public void Parser_Accepts_Contract_Json()
        {
            Assert.IsTrue(ToolCallParser.TryParse(GoodJson, "test", out var plan, out var result), result.ToString());
            Assert.AreEqual(9, plan.Commands.Count);
            Assert.AreEqual(CommandType.SET_SPEED, plan.Commands[3].Type);
            Assert.AreEqual(SpeedMode.Fast, plan.Commands[3].Speed);
            Assert.AreEqual(new GridCoord(9, 7), plan.Commands[4].Target);
        }

        [Test]
        public void Parser_Malformed_Json_Is_Schema_Error()
        {
            Assert.IsFalse(ToolCallParser.TryParse("{ this is not json", "test", out _, out var r));
            Assert.IsTrue(r.HasCode(ErrorCodes.MalformedJson));
            Assert.AreEqual(ErrorCategory.Schema, r.Errors[0].Category);
        }

        [Test]
        public void Parser_Unknown_Tool_Is_Tool_Error_With_Index()
        {
            const string json = @"{""tool_calls"":[{""tool"":""LIFT_OFF""},{""tool"":""MVOE_TO"",""args"":{""x"":1,""y"":1}}]}";
            Assert.IsFalse(ToolCallParser.TryParse(json, "test", out _, out var r));
            Assert.AreEqual(1, r.Errors.Count);
            Assert.AreEqual(ErrorCategory.Tool, r.Errors[0].Category);
            Assert.AreEqual(ErrorCodes.UnknownTool, r.Errors[0].Code);
            Assert.AreEqual(1, r.Errors[0].CommandIndex);
        }

        [Test]
        public void Parser_Reports_Every_Schema_Problem_Not_Just_The_First()
        {
            const string json = @"{""tool_calls"":[
                {""tool"":""MOVE_TO"",""args"":{""x"":9}},
                {""tool"":""MOVE_TO"",""args"":{""x"":""nine"",""y"":7}},
                {""tool"":""WAIT"",""args"":{""seconds"":-1}},
                {""tool"":""LAND"",""args"":{""altitude"":0}}
            ]}";
            Assert.IsFalse(ToolCallParser.TryParse(json, "test", out _, out var r));
            Assert.IsTrue(r.HasCode(ErrorCodes.MissingArgument));
            Assert.IsTrue(r.HasCode(ErrorCodes.InvalidArgumentType));
            Assert.IsTrue(r.HasCode(ErrorCodes.InvalidArgumentValue));
            Assert.IsTrue(r.HasCode(ErrorCodes.UnknownArgument));
            Assert.AreEqual(4, r.Errors.Count);
        }

        // ---------------- Validator ----------------

        [Test]
        public void Validator_MoveTo_While_Landed_Is_State_Error()
        {
            var (e, _) = TestFixtures.Engine40();
            var v = new PlanValidator(e.World, e.Config);
            var r = v.ValidatePlan(new Plan(new[] { Command.MoveTo(9, 7) }), e.IsCharging);
            Assert.IsTrue(r.HasCode(ErrorCodes.NotAirborne));
        }

        [Test]
        public void Validator_OutOfBounds_Is_Physical_Error()
        {
            var (e, _) = TestFixtures.Engine40();
            var v = new PlanValidator(e.World, e.Config);
            var r = v.ValidatePlan(new Plan(new[] { Command.LiftOff(), Command.MoveTo(99, 99) }), e.IsCharging);
            Assert.IsTrue(r.HasCode(ErrorCodes.OutOfBounds));
            Assert.AreEqual(ErrorCategory.Physical, r.Errors[0].Category);
        }

        [Test]
        public void Validator_Projection_Catches_Deliver_Before_Arrival_And_Third_Load()
        {
            var (e, _) = TestFixtures.Engine40();
            var v = new PlanValidator(e.World, e.Config);
            // Load P001, P002 (fills inventory), then P003 triggers INVENTORY_FULL.
            // DELIVER before MOVE_TO triggers NOT_AT_DESTINATION.
            var plan = new Plan(new[]
            {
                Command.Load("P001"), Command.Load("P002"), Command.Load("P003"),  // #2: inventory full
                Command.LiftOff(),
                Command.Deliver("P001"),                                             // #4: not at destination yet
                Command.MoveTo(9, 7), Command.Deliver("P001"), Command.Land()
            });
            var r = v.ValidatePlan(plan, e.IsCharging);
            Assert.AreEqual(2, r.Errors.Count, r.ToString());
            Assert.AreEqual(ErrorCodes.InventoryFull, r.Errors[0].Code);
            Assert.AreEqual(2, r.Errors[0].CommandIndex);
            Assert.AreEqual(ErrorCodes.NotAtDestination, r.Errors[1].Code);
            Assert.AreEqual(4, r.Errors[1].CommandIndex);
        }

        [Test]
        public void Validator_Does_Not_Reject_A_Route_Through_A_Building_D5()
        {
            var (e, _) = TestFixtures.Engine40();
            var v = new PlanValidator(e.World, e.Config);
            var plan = new Plan(new[] { Command.LiftOff(), Command.MoveTo(4, 20), Command.MoveTo(16, 20) });
            Assert.IsTrue(v.ValidatePlan(plan, e.IsCharging).IsValid,
                "Legal-but-bad routes must pass validation so the environment can deliver the crash.");
        }

        // ---------------- Executor ----------------

        [Test]
        public void Executor_Runs_Contract_Plan_To_Completion()
        {
            var (e, sink) = TestFixtures.Engine40();
            Assert.IsTrue(ToolCallParser.TryParse(GoodJson, "test", out var plan, out var r), r.ToString());
            Assert.IsTrue(new PlanValidator(e.World, e.Config).ValidatePlan(plan, e.IsCharging).IsValid);

            var ex = new PlanExecutor(e, plan, sink);
            RunToEnd(ex);

            Assert.AreEqual(ExecutorStatus.LandedAtBase, ex.Status);
            Assert.AreEqual(PackageStatus.Delivered, e.World.Packages["P001"].Status);
            Assert.AreEqual(FlightStatus.Landed, e.World.Drone.Flight);
            Assert.AreEqual(new GridCoord(20, 4), e.World.Drone.LandedTile);
            Assert.AreEqual(SpeedMode.Fast, e.World.Drone.Speed);
            Assert.AreEqual(0, ex.CommandsTruncated, "LAND was the final command — nothing to truncate.");
        }

        [Test]
        public void Executor_Holds_LiftOff_Until_Charge_Target_Reached_D10()
        {
            var (e, sink) = TestFixtures.Engine40(startBattery: 0.0);
            var plan = new Plan(new[]
            {
                Command.Charge(100), Command.Load("P001"),
                Command.LiftOff(), Command.Wait(0.1), Command.Land()
            });
            var ex = new PlanExecutor(e, plan, sink);

            double liftOffTime = -1;
            double liftOffBattery = -1;
            while (!ex.IsDone)
            {
                ex.Tick();
                if (liftOffTime < 0 && e.World.Drone.Flight == FlightStatus.Flying)
                {
                    liftOffTime = e.World.Clock.TimeSeconds;
                    liftOffBattery = e.World.Drone.BatteryPct;
                }
            }

            Assert.AreEqual(ExecutorStatus.LandedAtBase, ex.Status);
            Assert.That(liftOffTime, Is.InRange(6.0, 6.15),
                "Lift-off must wait for the 6 s charge; the 2 s load overlaps it.");
            Assert.AreEqual(100.0, liftOffBattery, 0.5,
                "Battery must be at target when lift-off occurs, before airborne drain.");
        }

        [Test]
        public void Executor_Chains_Instantaneous_Commands_In_One_Tick()
        {
            var (e, _) = TestFixtures.Engine40();
            var plan = new Plan(new[] { Command.SetSpeed(SpeedMode.Slow), Command.LiftOff(), Command.SetSpeed(SpeedMode.Fast), Command.MoveTo(4, 10) });
            var ex = new PlanExecutor(e, plan, new SimCore.Events.ListEventSink());
            ex.Tick();
            Assert.AreEqual(4, ex.NextCommandIndex, "Three instantaneous commands and the first blocking one dispatch on tick 1.");
            Assert.IsTrue(e.IsBusy);
        }

        [Test]
        public void Executor_Halts_Terminal_When_Plan_Flies_Into_Building()
        {
            var (e, sink) = TestFixtures.Engine40();
            var plan = new Plan(new[] { Command.LiftOff(), Command.MoveTo(4, 20), Command.MoveTo(16, 20), Command.Land() });
            var ex = new PlanExecutor(e, plan, sink);
            RunToEnd(ex);

            Assert.AreEqual(ExecutorStatus.HaltedTerminal, ex.Status);
            Assert.AreEqual(FailureKind.Crashed, e.World.Drone.Failure);
            Assert.AreEqual(3, ex.NextCommandIndex, "LAND was never dispatched.");
            Assert.IsTrue(sink.Contains(ExecutorEventTypes.PlanHaltedTerminal));
        }

        [Test]
        public void Executor_Refuses_Ticks_After_Done()
        {
            var (e, _) = TestFixtures.Engine40();
            var ex = new PlanExecutor(e, new Plan(new[] { Command.SetSpeed(SpeedMode.Slow) }), new SimCore.Events.ListEventSink());
            RunToEnd(ex);
            Assert.AreEqual(ExecutorStatus.PlanComplete, ex.Status);
            Assert.Throws<SimInvariantException>(() => ex.Tick());
        }
    }
}
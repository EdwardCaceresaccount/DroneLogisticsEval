using SimCore.Config;
using SimCore.Engine;
using SimCore.Events;

namespace SimCore.Tests
{
    public static class TestFixtures
    {
        public static ScenarioConfig Config40()
        {
            var cfg = new ScenarioConfig { ScenarioId = "Fixture40", GridSize = 40, TickSeconds = 0.05 };
            cfg.Tiles.Add(new TileSpec { X = 4,  Y = 4,  Type = "Facility" });
            cfg.Tiles.Add(new TileSpec { X = 20, Y = 4,  Type = "ChargingStation" });
            cfg.Tiles.Add(new TileSpec { X = 30, Y = 20, Type = "ChargingStation" });
            cfg.Tiles.Add(new TileSpec { X = 9,  Y = 7,  Type = "House" });
            cfg.Tiles.Add(new TileSpec { X = 30, Y = 30, Type = "House" });
            cfg.Tiles.Add(new TileSpec { X = 15, Y = 15, Type = "House" });
            cfg.Tiles.Add(new TileSpec { X = 10, Y = 20, Type = "Building" });
            cfg.Packages.Add(new PackageSpec { Id = "P001", Difficulty = "Easy",
                Origin = new CoordSpec { X = 4, Y = 4 }, Destination = new CoordSpec { X = 9, Y = 7 } });
            cfg.Packages.Add(new PackageSpec { Id = "P002", Difficulty = "Hard",
                Origin = new CoordSpec { X = 4, Y = 4 }, Destination = new CoordSpec { X = 30, Y = 30 } });
            cfg.Packages.Add(new PackageSpec { Id = "P003", Difficulty = "Normal",
                Origin = new CoordSpec { X = 4, Y = 4 }, Destination = new CoordSpec { X = 15, Y = 15 } });
            return cfg;
        }

        public static (SimulationEngine engine, ListEventSink sink) Engine40(double startBattery = 100.0)
        {
            var cfg = Config40();
            cfg.Drone.StartingBatteryPct = startBattery;
            var world = ScenarioLoader.Build(cfg);
            var sink = new ListEventSink();
            return (new SimulationEngine(world, cfg, sink), sink);
        }
    }
}
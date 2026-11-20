using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldV2FactoryTests
{
    private static WorldV2Options ValidOptions() => new()
    {
        Shapes = new List<string> { "圆罐", "方罐" },
        Liquids = new List<string> { "酒", "蜜" },
        Producers = new List<WorldV2ProducerOptions>
        {
            new()
            {
                Name = "陶工",
                Rates = new Dictionary<string, double> { ["圆罐"] = 1.0 }
            }
        },
        LiquidProductionRates = new Dictionary<string, double>
        {
            ["酒"] = 0.5,
            ["蜜"] = 0
        },
        InitialLiquidStock = new Dictionary<string, int> { ["酒"] = 3 },
        JarFlowRatePerLogicalTime = 1,
        LiquidFlowRatePerLogicalTime = 1,
        Workshops = new List<WorldV2WorkshopOptions>
        {
            new()
            {
                Name = "主台",
                BaseMarks = new List<string> { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10
            },
            new()
            {
                Name = "备用台",
                BaseMarks = new List<string> { "圆罐" },
                MaxJars = 8,
                MaxLiquids = 8
            }
        },
        MasterWorkshopName = "主台",
        SlaveWorkshopName = "备用台",
        ExecutionLogicalTimePerUnit = 1000,
        PassiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.Continuous,
        ActiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot
    };

    [Fact]
    public void FactoryBuildsAConsistentWorldFromExplicitLogicalTimeConfiguration()
    {
        var options = ValidOptions();

        using var world = WorldV2Factory.Create(options);

        Assert.Equal(new[] { "圆罐", "方罐" }, world.Resources.Producers.Generators.Keys);
        Assert.Single(world.Resources.Producers.Producers);
        Assert.Equal(3, world.Resources.Inventory.LiquidCount("酒"));
        Assert.Equal(0, world.Resources.Inventory.LiquidCount("蜜"));
        Assert.Equal(1, world.Resources.JarFlowRate);
        Assert.Equal(1, world.Resources.LiquidFlowRate);
        Assert.Equal("主台", world.Roster.Master!.Name);
        Assert.Equal("备用台", world.Roster.Slave!.Name);
        Assert.Equal(DemandAreaFulfillmentPolicy.Continuous, world.DemandBoard.Passive.FulfillmentPolicy);
        Assert.Equal(DemandAreaFulfillmentPolicy.OneShot, world.DemandBoard.Active.FulfillmentPolicy);
        Assert.Same(world.Resources, world.Runtime.Resources);
        Assert.Same(world.Roster, world.Runtime.Roster);
        Assert.Same(world.DemandBoard, world.Runtime.DemandBoard);
    }

    [Fact]
    public void FactoryUsesPerLogicalTimeRatesAndDoesNotReuseLegacyTickFlowSettings()
    {
        var options = ValidOptions();
        using var world = WorldV2Factory.Create(options);

        var result = world.Resources.Advance(2_000, world.Roster.Master);

        Assert.Equal(2, result.ProducedJarCount);
        Assert.Equal(1, result.ProducedLiquids.Single().Amount);
        Assert.Equal(2, result.FlowedJarCount);
        Assert.Equal(2, result.FlowedLiquids["酒"]);
        Assert.Equal(0, world.Resources.Inventory.JarCount);
        Assert.Equal(2, world.Roster.Master!.Jars.Count);
        Assert.Equal(2, world.Roster.Master.LiquidStorage["酒"]);
    }

    [Fact]
    public void FactoryCopiesMutableConfigurationIntoWorldObjects()
    {
        var options = ValidOptions();
        using var world = WorldV2Factory.Create(options);

        options.Shapes[0] = "被篡改";
        options.Producers[0].Rates["圆罐"] = 99;
        options.Workshops[0].BaseMarks.Clear();
        options.InitialLiquidStock["酒"] = 99;

        Assert.Contains("圆罐", world.Resources.Producers.Generators.Keys);
        Assert.DoesNotContain("被篡改", world.Resources.Producers.Generators.Keys);
        Assert.Equal(1, world.Resources.Producers.Producers["陶工"].Rates["圆罐"]);
        Assert.Contains("圆罐", world.Roster.Master!.BaseMarks);
        Assert.Equal(3, world.Resources.Inventory.LiquidCount("酒"));
    }

    [Fact]
    public void ValidationRejectsMissingMasterAndUnknownResourceNames()
    {
        var options = ValidOptions();
        options.MasterWorkshopName = "不存在";
        options.LiquidProductionRates["不存在的液体"] = 1;
        options.Producers[0].Rates["不存在的形状"] = 1;

        var result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("masterWorkshopName"));
        Assert.Contains(result.Errors, error => error.Contains("不存在的液体"));
        Assert.Contains(result.Errors, error => error.Contains("不存在的形状"));
        Assert.Throws<System.ArgumentException>(() => WorldV2Factory.Create(options));
    }

    [Fact]
    public void ValidationRejectsDuplicateNamesInvalidRatesAndInvalidRolePair()
    {
        var options = ValidOptions();
        options.Workshops.Add(new WorldV2WorkshopOptions
        {
            Name = "主台",
            BaseMarks = new List<string> { "圆罐" }
        });
        options.Producers[0].Rates["圆罐"] = double.NaN;
        options.SlaveWorkshopName = "主台";

        var result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("工作台名重复"));
        Assert.Contains(result.Errors, error => error.Contains("有限非负数"));
        Assert.Contains(result.Errors, error => error.Contains("不能是同一台"));
    }
}

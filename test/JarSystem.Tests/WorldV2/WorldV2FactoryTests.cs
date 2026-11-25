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
        ShipLiquidCapacity = 4,
        JarFlowRatePerLogicalTime = 1,
        LiquidFlowRatePerLogicalTime = 1,
        Workshops = new List<WorldV2WorkshopOptions>
        {
            new()
            {
                Name = "主台",
                BaseMarks = new List<string> { "圆罐", "方罐" },
                MaxJars = 10,
                MaxLiquids = 10,
                LiquidStagingCapacity = 7
            },
            new()
            {
                Name = "备用台",
                BaseMarks = new List<string> { "圆罐" },
                MaxJars = 8,
                MaxLiquids = 8,
                LiquidStagingCapacity = 5
            }
        },
        MasterWorkshopName = "主台",
        SlaveWorkshopName = "备用台",
        ExecutionLogicalTimePerUnit = 1000,
        PassiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.Continuous,
        ActiveFulfillmentPolicy = DemandAreaFulfillmentPolicy.OneShot
    };

    [Fact]
    public void ExecutionCheckpointPreservesWorldOwnedLiquidPolarityAcrossJsonRestore()
    {
        var options = ValidOptions();
        options.Liquids = new List<string> { "fuel", "coolant" };
        options.LiquidPolarities = new Dictionary<string, LiquidPolarity>
        {
            ["fuel"] = LiquidPolarity.Anode,
            ["coolant"] = LiquidPolarity.Cathode
        };
        options.LiquidProductionRates = new Dictionary<string, double>
        {
            ["fuel"] = 0.5,
            ["coolant"] = 0
        };
        options.InitialLiquidStock = new Dictionary<string, int> { ["fuel"] = 3 };

        using var source = WorldV2Factory.Create(options);
        var checkpoint = source.CaptureExecutionResourceCheckpoint();
        var json = WorldV2CheckpointJsonCodec.EncodeExecutionResourceCheckpoint(checkpoint);
        var restoredCheckpoint = WorldV2CheckpointJsonCodec.DecodeExecutionResourceCheckpoint(json);

        Assert.Equal(LiquidPolarity.Anode, restoredCheckpoint.LiquidPolarities["fuel"]);
        Assert.Equal(LiquidPolarity.Cathode, restoredCheckpoint.LiquidPolarities["coolant"]);

        using var target = WorldV2Factory.Create(options);
        target.RestoreExecutionResourceCheckpoint(restoredCheckpoint);
        Assert.Equal(3, target.ShipLiquidInventory!.GetAmount("fuel"));
        Assert.Equal(0, target.ShipLiquidInventory.GetAmount("coolant"));
    }

    [Fact]
    public void FactoryAcceptsCustomLiquidIdentitiesOnlyWithExplicitPolarityAndFingerprintsTheMapping()
    {
        var options = ValidOptions();
        options.Liquids = new List<string> { "fuel", "coolant" };
        options.LiquidPolarities = new Dictionary<string, LiquidPolarity>
        {
            ["fuel"] = LiquidPolarity.Anode,
            ["coolant"] = LiquidPolarity.Cathode
        };
        options.LiquidProductionRates = new Dictionary<string, double>
        {
            ["fuel"] = 0.5,
            ["coolant"] = 0
        };
        options.InitialLiquidStock = new Dictionary<string, int> { ["fuel"] = 3 };

        Assert.True(options.Validate().IsValid, string.Join("; ", options.Validate().Errors));
        var fingerprint = WorldV2ConfigurationFingerprint.Compute(options);
        using var world = WorldV2Factory.Create(options);
        Assert.Equal(3, world.ShipLiquidInventory!.GetAmount("fuel"));
        Assert.Equal(0, world.ShipLiquidInventory.GetAmount("coolant"));

        var serialized = System.Text.Json.JsonSerializer.Serialize(options);
        var restored = System.Text.Json.JsonSerializer.Deserialize<WorldV2Options>(serialized)!;
        Assert.Equal(LiquidPolarity.Anode, restored.LiquidPolarities["fuel"]);
        Assert.Equal(LiquidPolarity.Cathode, restored.LiquidPolarities["coolant"]);

        restored.LiquidPolarities["fuel"] = LiquidPolarity.Cathode;
        restored.LiquidPolarities["coolant"] = LiquidPolarity.Anode;
        Assert.NotEqual(fingerprint, WorldV2ConfigurationFingerprint.Compute(restored));
    }

    [Fact]
    public void FactoryRejectsCustomLiquidIdentitiesWithoutExplicitPolarityMapping()
    {
        var options = ValidOptions();
        options.Liquids = new List<string> { "fuel", "coolant" };

        var validation = options.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("liquidPolarities"));
        Assert.Throws<System.ArgumentException>(() => WorldV2Factory.Create(options));
    }

    [Fact]
    public void FactoryBuildsAConsistentWorldFromExplicitLogicalTimeConfiguration()
    {
        var options = ValidOptions();

        using var world = WorldV2Factory.Create(options);

        Assert.Equal(new[] { "圆罐", "方罐" }, world.Resources.Producers.Generators.Keys);
        Assert.Single(world.Resources.Producers.Producers);
        Assert.Equal(3, world.ShipLiquidInventory!.GetAmount("酒"));
        Assert.Equal(0, world.ShipLiquidInventory.GetAmount("蜜"));
        Assert.Equal(0, world.Resources.Inventory.LiquidCount("酒"));
        Assert.Equal(1, world.Resources.JarFlowRate);
        Assert.Equal(1, world.Resources.LiquidFlowRate);
        Assert.Equal(4, world.ShipLiquidInventory!.Capacity);
        Assert.Same(world.ShipLiquidInventory, world.Resources.ShipLiquidInventory);
        Assert.Equal(7, world.Roster.Master!.LiquidStagingCapacity);
        Assert.Equal(5, world.Roster.Slave!.LiquidStagingCapacity);
        Assert.Equal("主台", world.Roster.Master!.Name);
        Assert.Equal("备用台", world.Roster.Slave!.Name);
        Assert.Equal(DemandAreaFulfillmentPolicy.Continuous, world.DemandBoard.Passive.FulfillmentPolicy);
        Assert.Equal(DemandAreaFulfillmentPolicy.OneShot, world.DemandBoard.Active.FulfillmentPolicy);
        Assert.Same(world.Resources, world.Runtime.Resources);
        Assert.Same(world.Roster, world.Runtime.Roster);
        Assert.Same(world.DemandBoard, world.Runtime.DemandBoard);
    }

    [Fact]
    public void FactoryAppliesBoardContributionsToExactMasterAndSlaveRuntimeSlots()
    {
        const string boardId = "board.test";
        var assignmentFingerprint = "";
        var assignment = new WorldV2BoardAssignmentSnapshotOptions
        {
            SchemaVersion = 1,
            BoardId = boardId,
            DefinitionVersion = 1,
            AssignmentFingerprint = assignmentFingerprint,
            BoardDefinition = new WorldV2BoardDefinitionSnapshotOptions
            {
                SchemaVersion = 1,
                BoardId = boardId,
                DefinitionVersion = 1,
                DisplayName = "Demo Board",
                Slots = new List<WorldV2BoardSlotSnapshotOptions>
                {
                    new() { SlotId = "slot.master", AreaId = "workshop", ComponentKind = "workshop", DisplayName = "Master", RoleId = "master", ControlArea = "none", Required = true },
                    new() { SlotId = "slot.slave", AreaId = "workshop", ComponentKind = "workshop", DisplayName = "Slave", RoleId = "slave", ControlArea = "none", Required = true },
                    new() { SlotId = "slot.parser", AreaId = "decomposition", ComponentKind = "ore_parser", DisplayName = "Parser", RoleId = "none", ControlArea = "none", Required = true }
                }
            },
            Assignments = new List<WorldV2BoardAssignmentEntryOptions>
            {
                new() { SlotId = "slot.master", ComponentKind = "workshop", RoleId = "master", OwnedInstanceId = "owned.master", DefinitionId = "workshop.master", DefinitionVersion = 1, SourceRevision = 1 },
                new() { SlotId = "slot.parser", ComponentKind = "ore_parser", OwnedInstanceId = "owned.parser", DefinitionId = "parser.basic", DefinitionVersion = 1, SourceRevision = 1 },
                new() { SlotId = "slot.slave", ComponentKind = "workshop", RoleId = "slave", OwnedInstanceId = "owned.slave", DefinitionId = "workshop.slave", DefinitionVersion = 1, SourceRevision = 1 }
            }
        };
        assignmentFingerprint = assignment.ComputeCanonicalFingerprint();
        assignment.AssignmentFingerprint = assignmentFingerprint;
        var profile = new WorldV2BoardRuleProfileOptions
        {
            SchemaVersion = 1,
            BoardId = boardId,
            BoardDefinitionVersion = 1,
            AssignmentFingerprint = assignmentFingerprint,
            Contributions = new List<WorldV2RuleContributionOptions>
            {
                Contribution("rule.master-base", "GrantBaseMark", "圆罐", "slot.master", "owned.master", "workshop.master"),
                Contribution("rule.master-ban", "BanLiquid", "酒", "slot.master", "owned.master", "workshop.master"),
                Contribution("rule.slave-base", "GrantBaseMark", "方罐", "slot.slave", "owned.slave", "workshop.slave"),
                Contribution("rule.slave-ban", "BanLiquid", "蜜", "slot.slave", "owned.slave", "workshop.slave")
            }
        };
        profile.Fingerprint = profile.ComputeCanonicalFingerprint();

        var options = ValidOptions();
        options.BoardRuleProfile = profile;
        options.BoardAssignmentSnapshot = assignment;
        options.Workshops[0].BoardSlotId = "slot.master";
        options.Workshops[1].BoardSlotId = "slot.slave";

        using var world = WorldV2Factory.Create(options);
        var master = world.Roster.Master!;
        var slave = world.Roster.Slave!;

        Assert.Equal("owned.master", master.OwnedInstanceId);
        Assert.Equal("owned.slave", slave.OwnedInstanceId);
        Assert.Equal(new[] { "圆罐" }, master.BaseMarks);
        Assert.Equal(new[] { "方罐" }, slave.BaseMarks);
        Assert.True(master.IsBanned("酒"));
        Assert.False(master.IsBanned("蜜"));
        Assert.True(slave.IsBanned("蜜"));
        Assert.False(slave.IsBanned("酒"));

        var snapshot = WorldSnapshotFactory.Capture(world.Roster, world.ShipLiquidInventory);
        var masterSnapshot = snapshot.Master!;
        var slaveSnapshot = snapshot.Slave!;
        Assert.Equal("slot.master", masterSnapshot.RuleSlotId);
        Assert.Equal("slot.slave", slaveSnapshot.RuleSlotId);
        Assert.Equal(assignmentFingerprint, masterSnapshot.EffectiveRuleProfile.AssignmentFingerprint);
        Assert.Equal(profile.Fingerprint, masterSnapshot.EffectiveRuleProfile.SourceProfileFingerprint);
        Assert.Contains(masterSnapshot.EffectiveRuleProfile.Contributions,
            x => x.Source.SourceInstanceId == "owned.master");
        Assert.Contains(slaveSnapshot.EffectiveRuleProfile.Contributions,
            x => x.Source.SourceInstanceId == "owned.slave");
        Assert.True(masterSnapshot.CreateWorkshopInfo().Rules!.IsBanned("酒"));
        Assert.False(slaveSnapshot.CreateWorkshopInfo().Rules!.IsBanned("酒"));
    }

    private static WorldV2RuleContributionOptions Contribution(
        string ruleId, string effect, string target, string slotId,
        string instanceId, string definitionId) => new()
    {
        RuleId = ruleId,
        RuleVersion = 1,
        Effect = effect,
        Target = target,
        Scope = "workshop",
        Priority = 0,
        Source = new WorldV2RuleContributionSourceOptions
        {
            SourceKind = "installed-component",
            DefinitionId = definitionId,
            DefinitionVersion = 1,
            SourceInstanceId = instanceId,
            SourceRevision = 1,
            SlotId = slotId
        },
        Parameters = new Dictionary<string, string>()
    };

    [Fact]
    public void FactoryUsesPerLogicalTimeRatesAndDoesNotReuseLegacyTickFlowSettings()
    {
        var options = ValidOptions();
        using var world = WorldV2Factory.Create(options);

        var result = world.Resources.Advance(2_000, world.Roster.Master);

        Assert.Equal(2, result.ProducedJarCount);
        Assert.Equal(1, result.ProducedLiquids.Single().Amount);
        Assert.Equal(2, result.FlowedJarCount);
        Assert.Empty(result.FlowedLiquids);
        Assert.Equal(0, world.Resources.Inventory.JarCount);
        Assert.Equal(2, world.Roster.Master!.Jars.Count);
        Assert.Equal(0, world.Roster.Master.LiquidStorage["酒"]);
        Assert.Equal(4, world.ShipLiquidInventory!.GetAmount("酒"));
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
        Assert.Equal(3, world.ShipLiquidInventory!.GetAmount("酒"));
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
    public void ValidationRejectsInitialShipInventoryAboveAggregateCapacity()
    {
        var options = ValidOptions();
        options.ShipLiquidCapacity = 2;

        var result = options.Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("initialLiquidStock 总量超过 shipLiquidCapacity"));
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

    [Fact]
    public void ConfigurationFingerprintIgnoresDictionaryInsertionOrderButTracksSemanticChanges()
    {
        var first = ValidOptions();
        var second = ValidOptions();
        second.LiquidProductionRates = new Dictionary<string, double>
        {
            ["蜜"] = 0,
            ["酒"] = 0.5
        };
        second.InitialLiquidStock = new Dictionary<string, int>
        {
            ["酒"] = 3
        };
        second.Producers[0].Rates = new Dictionary<string, double>
        {
            ["圆罐"] = 1.0
        };

        var firstFingerprint = WorldV2ConfigurationFingerprint.Compute(first);
        var reorderedFingerprint = WorldV2ConfigurationFingerprint.Compute(second);

        Assert.Equal(firstFingerprint, reorderedFingerprint);
        second.ShipLiquidCapacity++;
        Assert.NotEqual(firstFingerprint, WorldV2ConfigurationFingerprint.Compute(second));
        Assert.Equal(64, firstFingerprint.Length);
    }

    [Fact]
    public void ExecutionCheckpointRejectsDifferentFactoryConfigurationBeforeMutatingTarget()
    {
        var sourceOptions = ValidOptions();
        using var source = WorldV2Factory.Create(sourceOptions);
        var checkpoint = source.CaptureExecutionResourceCheckpoint();

        var targetOptions = ValidOptions();
        targetOptions.ShipLiquidCapacity++;
        using var target = WorldV2Factory.Create(targetOptions);
        var targetShip = target.ShipLiquidInventory!;

        var error = Assert.Throws<System.InvalidOperationException>(
            () => target.RestoreExecutionResourceCheckpoint(checkpoint));

        Assert.Contains("配置指纹", error.Message);
        Assert.Equal(3, targetShip.GetAmount("酒"));
        Assert.Empty(target.DemandBoard.AllDemands);
        Assert.Equal(0, target.Reservations.Count);
    }
}

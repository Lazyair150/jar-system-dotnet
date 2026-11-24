using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

/// <summary>
/// Deterministic resource-conservation samples. The seed range is fixed so a failure
/// can be reproduced exactly; each feasible concrete assembly is checked against
/// its ordered replay witness rather than only checking solver status.
/// </summary>
public sealed class WorldOrderFixedSeedResourceConservationTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    [Fact]
    public void FixedSeedAAndBPlansConserveAcceptedFreeLiquidAndPhysicalJarIdentity()
    {
        var sampleOrdinal = 0;
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Feasible"] = 0,
            ["Infeasible"] = 0,
            ["Undetermined"] = 0
        };

        foreach (var seed in new[] { 0x4A4152, 0x4A4153, 0x4A4154, 0x4A4155 })
        {
            var random = new Random(seed);
            for (var sample = 0; sample < 128; sample++)
            {
                var sampleId = sampleOrdinal++;
            var masterBan = RandomBan(random);
            var slaveBan = RandomBan(random);
            var masterCapacity = random.Next(1, 5);
            var slaveCapacity = random.Next(1, 5);
            var master = new Workshop(
                "seed-" + sampleId + "-master",
                new List<string> { "圆罐", "方罐" },
                ban: masterBan,
                maxJars: 5,
                maxLiquids: 5,
                liquidStagingCapacity: masterCapacity,
                vocabulary: Words);

            var jarCount = random.Next(1, 4);
            for (var index = 0; index < jarCount; index++)
            {
                var shape = random.Next(2) == 0 ? "圆罐" : "方罐";
                var liquid = random.Next(3) switch
                {
                    0 => null,
                    1 => "酒",
                    _ => "蜜"
                };
                Assert.True(master.AddJar(new Jar(
                    shape, liquid, id: 80_000 + sampleId * 10 + index, vocabulary: Words)));
            }

            master.AddLiquid("酒", random.Next(0, 4));
            master.AddLiquid("蜜", random.Next(0, 4));

            var requirementCount = random.Next(1, 3);
            var requirements = Enumerable.Range(0, requirementCount)
                .Select(_ => new Requirement
                {
                    Shape = random.Next(2) == 0 ? "圆罐" : "方罐",
                    Liquid = random.Next(3) switch
                    {
                        0 => null,
                        1 => "酒",
                        _ => "蜜"
                    }
                })
                // Domain contract: empty-Jar requirements precede full-Jar requirements.
                .OrderBy(requirement => requirement.Liquid is not null)
                .ToArray();
            var orderType = random.Next(2) == 0
                ? Constants.OrderType.A
                : Constants.OrderType.B;
            var specification = new JarOrderSpecification(requirements, orderType, Words);

            Workshop? slave = null;
            WorkshopRoster roster;
            if (orderType == Constants.OrderType.B)
            {
                slave = new Workshop(
                    "seed-" + sampleId + "-slave",
                    new List<string> { "圆罐", "方罐" },
                    ban: slaveBan,
                    maxJars: 5,
                    maxLiquids: 5,
                    liquidStagingCapacity: slaveCapacity,
                    vocabulary: Words);
                roster = new WorkshopRoster(new[] { master, slave }, master, slave);
            }
            else
            {
                roster = new WorkshopRoster(new[] { master }, master);
            }

            var world = WorldSnapshotFactory.Capture(roster);
            var masterProfile = WorldOrderRuleCandidateCatalog.Build(
                world, world.Master!, "Master", specification);
            var demandId = 90_000 + sampleId;

            if (orderType == Constants.OrderType.A)
            {
                var result = WorldOrderAClassBranchEnumerator.Enumerate(
                    demandId, masterProfile, world, maxBranches: 4096, maxNodes: 100_000);
                Count(statusCounts, result.Status.ToString());
                if (result.Status == WorldOrderAClassBranchEnumerationStatus.Feasible)
                    AssertConserves(result.Assembly, world, orderType);
                else
                    Assert.Null(result.Assembly);
            }
            else
            {
                var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
                    world, world.Slave!, "Slave", specification);
                var result = WorldOrderBClassBranchEnumerator.Enumerate(
                    demandId, masterProfile, slaveProfile, world);
                Count(statusCounts, result.Status.ToString());
                if (result.Status == WorldOrderBClassBranchEnumerationStatus.Feasible)
                    AssertConserves(result.Assembly, world, orderType);
                else
                    Assert.Null(result.Assembly);
            }
        }
        }

        Assert.Equal(512, sampleOrdinal);
        Assert.True(statusCounts["Feasible"] > 0, "Fixed seed produced no feasible samples.");
        Assert.True(statusCounts["Infeasible"] > 0, "Fixed seed produced no infeasible samples.");
    }

    private static List<string> RandomBan(Random random)
    {
        var mode = random.Next(4);
        return mode switch
        {
            0 => new List<string>(),
            1 => new List<string> { "酒" },
            2 => new List<string> { "蜜" },
            _ => new List<string> { "酒", "蜜" }
        };
    }

    private static void Count(Dictionary<string, int> counts, string status)
    {
        Assert.Contains(status, counts.Keys);
        counts[status]++;
    }

    private static int Total(IReadOnlyDictionary<string, int> balances) =>
        balances.Values.Aggregate(0, (sum, amount) => checked(sum + amount));

    private static void AssertConserves(
        WorldOrderConcretePlanAssemblyResult? assembly,
        WorldSnapshot world,
        string orderType)
    {
        Assert.NotNull(assembly);
        Assert.Equal(WorldOrderSolveStatus.Feasible, assembly!.Status);
        var plan = Assert.IsType<WorldOrderSolvePlan>(assembly.CandidatePlan);
        var manifest = Assert.IsType<WorldOrderResourceManifest>(assembly.Manifest);
        var replay = Assert.IsType<WorldOrderOrderedResourceReplayResult>(assembly.ResourceReplay);
        Assert.True(replay.IsComplete);
        Assert.Equal(replay.MasterInitialLiquids.OrderBy(item => item.Key),
            manifest.MasterInitialLiquidAmounts.OrderBy(item => item.Key));
        Assert.Equal(replay.MasterFinalLiquids.OrderBy(item => item.Key),
            manifest.MasterFinalLiquidBalances.OrderBy(item => item.Key));
        Assert.Equal(replay.TransferLiquids.OrderBy(item => item.Key),
            manifest.TransferLiquids.OrderBy(item => item.Key));
        Assert.Equal(replay.MasterStagingPeakCapacity, manifest.MasterStagingPeakCapacity);
        Assert.Equal(replay.SlaveStagingPeakCapacity, manifest.SlaveStagingPeakCapacity);

        Assert.Equal(plan.MasterSteps.Count, replay.MasterAcceptedProducedLiquids.Count);
        var masterProduced = replay.MasterAcceptedProducedLiquids.Count(item => item != null);
        var masterConsumed = plan.MasterSteps.Count(step =>
            step.Operation?.Type == "POUR_IN" ||
            (step.Operation?.Type == "RESHAPE" && step.Operation.FullReshape == false));
        var masterExpectedFinal = checked(
            Total(manifest.MasterInitialLiquidAmounts) + masterProduced - masterConsumed);
        Assert.Equal(masterExpectedFinal, Total(manifest.MasterFinalLiquidBalances));
        Assert.InRange(manifest.MasterStagingPeakCapacity, 0, world.Master!.LiquidStagingCapacity);
        Assert.All(manifest.MasterInitialLiquidAmounts,
            item => Assert.InRange(item.Value, 0, world.Master.LiquidStagingCapacity));

        var physicalMasterIds = world.Master.Jars.Select(item => item.Id!.Value).ToHashSet();
        Assert.All(manifest.MasterJarIds, id => Assert.Contains(id, physicalMasterIds));
        Assert.All(manifest.TransferJarIds, id => Assert.Contains(id, physicalMasterIds));
        Assert.Equal(manifest.MasterJarIds.Count, manifest.MasterJarIds.Distinct().Count());
        Assert.Equal(manifest.TransferJarIds.Count, manifest.TransferJarIds.Distinct().Count());

        if (orderType == Constants.OrderType.B)
        {
            Assert.True(manifest.IsPartitioned);
            Assert.NotNull(world.Slave);
            Assert.Equal(plan.TransferJarIds.OrderBy(id => id), manifest.TransferJarIds.OrderBy(id => id));
            Assert.Equal(plan.TransferLiquids.OrderBy(item => item.Key),
                manifest.TransferLiquids.OrderBy(item => item.Key));
            Assert.Equal(plan.SlaveSteps.Count, replay.SlaveAcceptedProducedLiquids.Count);

            var slaveProduced = replay.SlaveAcceptedProducedLiquids.Count(item => item != null);
            var slaveConsumed = plan.SlaveSteps.Count(step =>
                step.Operation?.Type == "POUR_IN" ||
                (step.Operation?.Type == "RESHAPE" && step.Operation.FullReshape == false));
            var slaveFinal = checked(
                Total(manifest.TransferLiquids) + slaveProduced - slaveConsumed);
            Assert.True(slaveFinal >= 0, "Slave free-liquid replay consumed more than its transferred and accepted supply.");
            Assert.Equal(slaveFinal, Total(replay.SlaveFinalLiquids));
            Assert.All(replay.SlaveFinalLiquids,
                item => Assert.InRange(item.Value, 0, world.Slave!.LiquidStagingCapacity));
            Assert.InRange(manifest.SlaveStagingPeakCapacity, 0, world.Slave!.LiquidStagingCapacity);
            Assert.InRange(Total(manifest.TransferLiquids), 0, world.Slave.LiquidStagingCapacity);
        }
        else
        {
            Assert.False(manifest.IsPartitioned);
            Assert.Empty(manifest.TransferJarIds);
            Assert.Empty(manifest.TransferLiquids);
            Assert.Equal(0, manifest.SlaveStagingPeakCapacity);
            Assert.Empty(plan.SlaveSteps);
        }
    }
}

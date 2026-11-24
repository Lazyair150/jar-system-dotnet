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

        var aSamples = 0;
        var bSamples = 0;
        var masterBanModes = new HashSet<string>(StringComparer.Ordinal);
        var slaveBanModes = new HashSet<string>(StringComparer.Ordinal);
        var masterCapacities = new HashSet<int>();
        var slaveCapacities = new HashSet<int>();
        var jarStates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in new[]
        {
            0x4A4152, 0x4A4153, 0x4A4154, 0x4A4155,
            0x4A4156, 0x4A4157, 0x4A4158, 0x4A4159,
            0x4A415A, 0x4A415B, 0x4A415C, 0x4A415D,
            0x4A415E, 0x4A415F, 0x4A4160, 0x4A4161
        })
        {
            var random = new Random(seed);
            for (var sample = 0; sample < 256; sample++)
            {
                var sampleId = sampleOrdinal++;
                var masterBan = RandomBan(random);
                var slaveBan = RandomBan(random);
                masterBanModes.Add(string.Join("|", masterBan.OrderBy(x => x, StringComparer.Ordinal)));
                slaveBanModes.Add(string.Join("|", slaveBan.OrderBy(x => x, StringComparer.Ordinal)));
                var masterCapacity = random.Next(1, 6);
                var slaveCapacity = random.Next(1, 6);
                masterCapacities.Add(masterCapacity);
                var master = new Workshop(
                    "seed-" + sampleId + "-master",
                    new List<string> { "圆罐", "方罐" },
                    ban: masterBan,
                    maxJars: 5,
                    maxLiquids: 5,
                    liquidStagingCapacity: masterCapacity,
                    vocabulary: Words);

                var jarCount = random.Next(1, 6);
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
                    jarStates.Add(shape + "|" + (liquid ?? "EMPTY"));
                }

                master.AddLiquid("酒", random.Next(0, 6));
                master.AddLiquid("蜜", random.Next(0, 6));

                var requirementCount = random.Next(1, 5);
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
                    bSamples++;
                    slaveCapacities.Add(slaveCapacity);
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
                    aSamples++;
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
                        AssertConserves(result.Assembly, world, orderType, seed, sample);
                    else
                        Assert.Null(result.Assembly);
                }
                else
                {
                    var slaveProfile = WorldOrderRuleCandidateCatalog.Build(
                        world, world.Slave!, "Slave", specification);
                    var result = WorldOrderBClassBranchEnumerator.Enumerate(
                        demandId, masterProfile, slaveProfile, world,
                        maxBranches: 4096, maxNodes: 100_000);
                    Count(statusCounts, result.Status.ToString());
                    if (result.Status == WorldOrderBClassBranchEnumerationStatus.Feasible)
                        AssertConserves(result.Assembly, world, orderType, seed, sample);
                    else
                        Assert.Null(result.Assembly);
                }
            }
        }

        Assert.Equal(4096, sampleOrdinal);
        Assert.True(statusCounts["Feasible"] > 0, "Fixed seed produced no feasible samples.");
        Assert.True(statusCounts["Infeasible"] > 0, "Fixed seed produced no infeasible samples.");
        Assert.True(aSamples > 0 && bSamples > 0, "Fixed seed must cover both A and B orders.");
        Assert.Equal(4096, aSamples + bSamples);
        Assert.Equal(0, statusCounts["Undetermined"]);
        Assert.Equal(4, masterBanModes.Count);
        Assert.Equal(4, slaveBanModes.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, masterCapacities.OrderBy(x => x));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, slaveCapacities.OrderBy(x => x));
        Assert.Equal(6, jarStates.Count);
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

    private static bool IsFreeLiquidConsumer(WorldPlanOperation? operation) =>
        operation?.Type == "POUR_IN" ||
        (operation?.Type == "RESHAPE" && operation.FullReshape == false);

    private static void AssertLiquidBalancesConserve(
        IReadOnlyDictionary<string, int> initial,
        IReadOnlyDictionary<string, int> final,
        IReadOnlyList<WorldPlanStep> steps,
        IReadOnlyList<string?> acceptedOutputs,
        string role,
        string sampleContext)
    {
        Assert.Equal(steps.Count, acceptedOutputs.Count);
        var expected = initial.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        Assert.All(expected.Values, amount => Assert.True(amount >= 0));

        for (var index = 0; index < steps.Count; index++)
        {
            var operation = steps[index].Operation;
            if (IsFreeLiquidConsumer(operation))
            {
                // POUR_IN consumes its target liquid; empty RESHAPE consumes its cost liquid.
                // These are distinct operation fields in the Core contract.
                var liquid = operation!.Type == "POUR_IN"
                    ? operation.TargetLiquid
                    : operation.CostLiquid;
                Assert.False(string.IsNullOrWhiteSpace(liquid),
                    role + " free-liquid consumer is missing its exact liquid identity.");
                var available = expected.GetValueOrDefault(liquid);
                Assert.True(available > 0,
                    role + " consumed unavailable free liquid: " + liquid);
                if (available == 1) expected.Remove(liquid);
                else expected[liquid] = available - 1;
            }

            if (acceptedOutputs[index] is { } produced)
                expected[produced] = checked(expected.GetValueOrDefault(produced) + 1);
        }

        var allLiquids = expected.Keys.Concat(final.Keys)
            .Distinct(StringComparer.Ordinal);
        foreach (var liquid in allLiquids)
        {
            var expectedAmount = expected.GetValueOrDefault(liquid);
            var actualAmount = final.GetValueOrDefault(liquid);
            Assert.True(actualAmount >= 0,
                role + " final balance is negative for liquid " + liquid);
            Assert.True(
                expectedAmount == actualAmount,
                $"{sampleContext} {role} liquid conservation failed for '{liquid}': expected={expectedAmount}, actual={actualAmount}; " +
                $"initial=[{string.Join(",", initial.Select(x => x.Key + ":" + x.Value))}]; " +
                $"final=[{string.Join(",", final.Select(x => x.Key + ":" + x.Value))}]; " +
                $"steps=[{string.Join(" | ", steps.Select((step, index) => index + ":" + step.Operation?.Type + ":" + step.Operation?.CostLiquid + ":accepted=" + (acceptedOutputs[index] ?? "-")))}]");
        }
    }

    private static int Total(IReadOnlyDictionary<string, int> balances) =>
        balances.Values.Aggregate(0, (sum, amount) => checked(sum + amount));

    private static void AssertConserves(
        WorldOrderConcretePlanAssemblyResult? assembly,
        WorldSnapshot world,
        string orderType,
        int seed,
        int sample)
    {
        var sampleContext = $"seed=0x{seed:X} sample={sample}";
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
        AssertLiquidBalancesConserve(
            manifest.MasterInitialLiquidAmounts,
            manifest.MasterFinalLiquidBalances,
            plan.MasterSteps,
            replay.MasterAcceptedProducedLiquids,
            "Master",
            sampleContext);
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

            AssertLiquidBalancesConserve(
                manifest.TransferLiquids,
                replay.SlaveFinalLiquids,
                plan.SlaveSteps,
                replay.SlaveAcceptedProducedLiquids,
                "Slave",
                sampleContext);
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

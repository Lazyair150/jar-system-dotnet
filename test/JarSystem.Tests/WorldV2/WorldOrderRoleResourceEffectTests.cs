using System;
using System.Collections.Generic;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderRoleResourceEffectTests
{
    private static WorldOrderRuleCandidate Candidate(
        int requirementIndex,
        string? consumedFree,
        string? producedFree,
        string? consumedJar) =>
        new(
            requirementIndex,
            "圆罐",
            null,
            "SYMBOLIC_TEST",
            "test.rule",
            1,
            "圆罐",
            null,
            null,
            consumedFree,
            producedFree,
            consumedJar);

    [Fact]
    public void MasterAndSlaveResourceEffectsRemainInSeparateLedgers()
    {
        var steps = new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(0, consumedFree: "酒", producedFree: "蜜", consumedJar: "酒")),
            new WorldOrderAssignedRuleCandidate(
                "Slave", Candidate(1, consumedFree: "蜜", producedFree: "酒", consumedJar: "蜜"))
        };

        var effects = WorldOrderRoleResourceEffect.AggregateByRole(steps);

        Assert.Equal(new[] { "Master", "Slave" }, new List<string>(effects.Keys));
        Assert.Equal(1, effects["Master"].GrossFreeLiquidConsumption["酒"]);
        Assert.Equal(1, effects["Master"].GrossFreeLiquidProduction["蜜"]);
        Assert.Equal(1, effects["Master"].GrossJarLiquidConsumption["酒"]);
        Assert.Equal(1, effects["Slave"].GrossFreeLiquidConsumption["蜜"]);
        Assert.Equal(1, effects["Slave"].GrossFreeLiquidProduction["酒"]);
        Assert.Equal(1, effects["Slave"].GrossJarLiquidConsumption["蜜"]);

        // Master 产出的蜜不能抵扣 Slave 的蜜需求；两边仍是独立账户。
        Assert.Equal(0, effects["Master"].GrossFreeLiquidConsumption.GetValueOrDefault("蜜"));
        Assert.Equal(0, effects["Slave"].GrossFreeLiquidProduction.GetValueOrDefault("蜜"));
    }

    [Fact]
    public void SymbolicSolutionExposesRoleLedgersWithoutRemovingLegacyAggregateTotals()
    {
        var solution = new WorldOrderSymbolicSolution(new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(0, consumedFree: "酒", producedFree: "蜜", consumedJar: null)),
            new WorldOrderAssignedRuleCandidate(
                "Slave", Candidate(1, consumedFree: "蜜", producedFree: "酒", consumedJar: null))
        });

        Assert.Equal(1, solution.ResourceEffectsByRole["Master"].GrossFreeLiquidConsumption["酒"]);
        Assert.Equal(1, solution.ResourceEffectsByRole["Slave"].GrossFreeLiquidConsumption["蜜"]);

        // Legacy totals remain available for diagnostics/backward compatibility only.
        // They intentionally do not encode account ownership or prove transfer.
        Assert.Equal(1, solution.GrossFreeLiquidConsumption["酒"]);
        Assert.Equal(1, solution.GrossFreeLiquidConsumption["蜜"]);
        Assert.Equal(1, solution.GrossFreeLiquidProduction["蜜"]);
        Assert.Equal(1, solution.GrossFreeLiquidProduction["酒"]);
    }

    [Fact]
    public void SingleRoleSolutionDoesNotInventSlaveLedger()
    {
        var effects = WorldOrderRoleResourceEffect.AggregateByRole(new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(0, consumedFree: "酒", producedFree: null, consumedJar: null))
        });

        Assert.Single(effects);
        Assert.Contains("Master", effects.Keys);
        Assert.DoesNotContain("Slave", effects.Keys);
    }

    [Fact]
    public void EffectsAccumulateWithinRoleAndKeepDeterministicLiquidOrder()
    {
        var effects = WorldOrderRoleResourceEffect.AggregateByRole(new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(0, consumedFree: "蜜", producedFree: null, consumedJar: null)),
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(1, consumedFree: "酒", producedFree: null, consumedJar: null)),
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(2, consumedFree: "蜜", producedFree: null, consumedJar: null))
        });

        Assert.Equal(3, effects["Master"].StepCount);
        Assert.Equal(2, effects["Master"].GrossFreeLiquidConsumption["蜜"]);
        Assert.Equal(1, effects["Master"].GrossFreeLiquidConsumption["酒"]);
        Assert.Equal(new[] { "蜜", "酒" },
            new List<string>(effects["Master"].GrossFreeLiquidConsumption.Keys));
    }

    [Fact]
    public void RoleLedgerIsReadOnlyAndRejectsMissingRole()
    {
        var effects = WorldOrderRoleResourceEffect.AggregateByRole(new[]
        {
            new WorldOrderAssignedRuleCandidate(
                "Master", Candidate(0, consumedFree: null, producedFree: null, consumedJar: null))
        });

        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, WorldOrderRoleResourceEffect>)effects)
                .Add("Slave", effects["Master"]));

        Assert.Throws<ArgumentException>(() =>
            WorldOrderRoleResourceEffect.AggregateByRole(new[]
            {
                new WorldOrderAssignedRuleCandidate(
                    " ", Candidate(0, consumedFree: null, producedFree: null, consumedJar: null))
            }));
    }
}

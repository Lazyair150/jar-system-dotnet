using System.Linq;
using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.Solver;
using JarSystem.Core.World;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Solving;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldOrderSolverTests
{
    private static readonly Vocabulary Words =
        new(new[] { "圆罐", "方罐" }, new[] { "酒", "蜜" });

    private static OrderDemand Demand(string type = Constants.OrderType.A, string shape = "圆罐") =>
        new(new JarOrderSpecification(
            new[] { new Requirement { Shape = shape } }, type, Words),
            0, new SequentialIdGenerator());

    private static WorldSnapshot Snapshot(Workshop master, Workshop? slave = null)
    {
        var workshops = new List<Workshop> { master };
        if (slave != null) workshops.Add(slave);
        return WorldSnapshotFactory.Capture(new WorkshopRoster(workshops, master, slave));
    }

    private static WorldOrderSolverRegistry Registry() =>
        new(new IWorldOrderSpecificationSolver[]
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        });

    [Fact]
    public void RegistryDispatchesJarSpecificationAndBindsResultToDemandAndSnapshotVersion()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var snapshot = Snapshot(master);
        var demand = Demand();

        var result = Registry().Solve(demand, snapshot);

        Assert.Equal(demand.Id, result.DemandId);
        Assert.Equal(snapshot.Version, result.WorldVersion);
        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Equal("9l_concrete_plan", result.CheckMethod);
        Assert.Equal(1, result.DirectCount);
        Assert.NotNull(result.Plan);
        Assert.False(result.Plan!.IsPartitioned);
        Assert.NotEmpty(result.Plan!.MasterSteps);
    }

    [Fact]
    public void AClassEntryPointMaterializesVersionedRuleIdentityAndPhysicalJar()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐", id: 8101));
        var snapshot = Snapshot(master);
        var result = Registry().Solve(Demand(), snapshot);

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        var step = Assert.Single(result.Plan!.MasterSteps);
        Assert.Equal(8101, step.Before![step.Operation!.JarIndex!.Value].Id);
        Assert.False(string.IsNullOrWhiteSpace(step.Operation.RuleId));
        Assert.True(step.Operation.RuleVersion > 0);
    }

    [Fact]
    public void AClassEntryPointDoesNotSelfConsumeJarLiquidBannedByMaster()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 8102));
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "方罐" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());

        var result = Registry().Solve(demand, Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Infeasible, result.Status);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void UnsupportedSpecificationIsNotSilentlyTreatedAsInfeasible()
    {
        var registry = Registry();
        var demand = new OrderDemand(new ForeignSpecification(), 0, new SequentialIdGenerator());
        var snapshot = Snapshot(new Workshop("主台"));

        var result = registry.Solve(demand, snapshot);

        Assert.Equal(WorldOrderSolveStatus.UnsupportedSpecification, result.Status);
    }

    [Fact]
    public void OpaqueSnapshotRuleProducesUndeterminedInsteadOfFalseInfeasibility()
    {
        var stack = new ModifierStack();
        var condition = true;
        stack.Push(new RequireModifier
        {
            Text = "外部条件",
            Violation = () => condition ? null : "不满足"
        });
        var master = new Workshop("主台", modifiers: stack);
        master.AddJar(new Jar("圆罐"));
        var snapshot = Snapshot(master);

        var result = Registry().Solve(Demand(), snapshot);

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
        Assert.Contains("无法静态投影", result.Reason ?? string.Empty);
    }

    [Fact]
    public void RuleCandidateProfileIsReusedAcrossResourceOnlyChanges()
    {
        var stack = new ModifierStack();
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        var roster = new WorkshopRoster(new[] { master }, master);
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.A, Words);
        var book = new WorldOrderRuleCandidateBook();

        var firstSnapshot = WorldSnapshotFactory.Capture(roster);
        var first = book.GetOrBuild(firstSnapshot, firstSnapshot.Master!, "Master", specification);
        var options = Assert.Single(first.Requirements).Candidates;
        Assert.Contains(options, candidate => candidate.Operation == "MATCH");
        Assert.Contains(options, candidate => candidate.Operation == "POUR_IN");
        Assert.True(first.HasDirectCandidateForEveryRequirement);

        master.AddLiquid("蜜", 1);
        var resourceChangedSnapshot = WorldSnapshotFactory.Capture(roster);
        Assert.NotEqual(firstSnapshot.ResourceVersion, resourceChangedSnapshot.ResourceVersion);
        var reused = book.GetOrBuild(
            resourceChangedSnapshot, resourceChangedSnapshot.Master!, "Master", specification);

        Assert.Same(first, reused);
        Assert.Equal(1, book.CacheMisses);
        Assert.Equal(1, book.CacheHits);

        stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "禁止灌酒"
        });
        var ruleChangedSnapshot = WorldSnapshotFactory.Capture(roster);
        var rebuilt = book.GetOrBuild(
            ruleChangedSnapshot, ruleChangedSnapshot.Master!, "Master", specification);

        Assert.NotSame(first, rebuilt);
        Assert.DoesNotContain(
            Assert.Single(rebuilt.Requirements).Candidates,
            candidate => candidate.Operation == "POUR_IN");
        Assert.Contains(
            Assert.Single(rebuilt.Requirements).Candidates,
            candidate => candidate.Operation == "MATCH");
        Assert.Equal(2, book.CacheMisses);
    }

    [Fact]
    public void NoDirectRuleCandidateIsNotReportedAsACompleteOrderInfeasibility()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Reason = "禁止直接匹配"
        });
        stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Shape = "圆罐",
            Liquid = "酒",
            Reason = "禁止灌酒"
        });
        var master = new Workshop(
            "主台", new List<string> { "圆罐" }, modifiers: stack);
        var snapshot = Snapshot(master);
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
            Constants.OrderType.A, Words);
        var profile = WorldOrderRuleCandidateCatalog.Build(
            snapshot, snapshot.Master!, "Master", specification);

        Assert.Empty(Assert.Single(profile.Requirements).Candidates);
        var candidateSet = WorldOrderSymbolicSolutionBuilder.Build(new[] { profile });
        Assert.Equal(
            WorldOrderSymbolicSolutionStatus.NoDirectCandidate,
            candidateSet.Status);
        Assert.False(candidateSet.IsEnumerationComplete);
        Assert.False(candidateSet.IsResourceBound);
    }

    [Fact]
    public void RuleCandidateProfileIncludesLiquidPreparationAndHonorsBanAndStagingCapacity()
    {
        var master = new Workshop(
            "主台",
            new List<string> { "圆罐" },
            ban: new List<string> { "酒" },
            liquidStagingCapacity: 1);
        var snapshot = Snapshot(master);
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } },
            Constants.OrderType.A, Words);

        var profile = WorldOrderRuleCandidateCatalog.Build(
            snapshot, snapshot.Master!, "Master", specification);

        Assert.Contains(profile.PreparatoryOperations, candidate =>
            candidate.SourceShape == "方罐" &&
            candidate.SourceLiquid == "蜜" &&
            candidate.Operation == "PREPARE_POUR_OUT");
        Assert.DoesNotContain(profile.PreparatoryOperations,
            candidate => candidate.SourceLiquid == "酒");

        var noStaging = new Workshop(
            "主台", new List<string> { "圆罐" },
            liquidStagingCapacity: 0);
        var noStagingSnapshot = Snapshot(noStaging);
        var blocked = WorldOrderRuleCandidateCatalog.Build(
            noStagingSnapshot, noStagingSnapshot.Master!, "Master", specification);
        Assert.Empty(blocked.PreparatoryOperations);
        Assert.DoesNotContain(
            Assert.Single(blocked.Requirements).Candidates,
            candidate => candidate.Operation == "POUR_OUT");
    }

    [Fact]
    public void OpaqueRequireProducesUndeterminedRuleCandidateProfile()
    {
        var stack = new ModifierStack();
        stack.Push(new RequireModifier
        {
            Text = "外部世界条件",
            Violation = () => null
        });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        var snapshot = Snapshot(master);
        var specification = new JarOrderSpecification(
            new[] { new Requirement { Shape = "圆罐" } },
            Constants.OrderType.A, Words);

        var profile = WorldOrderRuleCandidateCatalog.Build(
            snapshot, snapshot.Master!, "Master", specification);

        Assert.True(profile.IsUndetermined);
        Assert.False(profile.HasDirectCandidateForEveryRequirement);
        Assert.Contains("无法静态重建", profile.Reason ?? string.Empty);
    }

    [Fact]
    public void BSpecificationRequiresConcreteSlaveParticipation()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        master.AddJar(new Jar("圆罐"));
        var snapshot = Snapshot(master, slave);
        var demand = Demand(Constants.OrderType.B);

        var result = Registry().Solve(demand, snapshot);

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Equal("9l_concrete_plan", result.CheckMethod);
        Assert.Equal(1, result.DirectCount);
        Assert.NotNull(result.Plan);
        Assert.True(result.Plan.IsPartitioned);
        Assert.Null(result.PartitionReport);
        Assert.Empty(result.Plan.MasterSteps);
        Assert.Single(result.Plan.SlaveSteps);

        var repeated = Registry().Solve(demand, snapshot);
        Assert.Equal(result.Plan.SlaveSteps.Select(x => x.Operation!.RuleId),
            repeated.Plan!.SlaveSteps.Select(x => x.Operation!.RuleId));
        Assert.Equal(result.Plan.TransferJarIds, repeated.Plan.TransferJarIds);
    }

    [Fact]
    public void BClassCanTransferMasterBannedFullJarToPermissiveSlaveForSelfConsumingReshape()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        master.AddJar(new Jar("圆罐", "酒", id: 8201));
        var slave = new Workshop("允许用酒的从台",
            new List<string> { "圆罐", "方罐" });
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "方罐" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());

        var result = Registry().Solve(demand, Snapshot(master, slave));

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Equal("9l_concrete_plan", result.CheckMethod);
        Assert.Equal(new[] { 8201 }, result.Plan!.TransferJarIds);
        Assert.DoesNotContain(result.Plan.TransferLiquids, item => item.Value > 0);
        var step = Assert.Single(result.Plan.SlaveSteps);
        Assert.Equal("RESHAPE", step.Operation!.Type);
        Assert.True(step.Operation.FullReshape);
        Assert.Equal("酒", step.Operation.CostLiquid);
        Assert.Equal(8201, step.Before![step.Operation.JarIndex!.Value].Id);
        Assert.Equal("方罐", step.After![step.Operation.JarIndex!.Value].Shape);
        Assert.Null(step.After[step.Operation.JarIndex!.Value].Liquid);
    }

    [Fact]
    public void BClassEntryPointTransfersMasterPreparationOutputForSlavePourIn()
    {
        var masterRules = new ModifierStack();
        masterRules.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Reason = "主台不直接匹配圆罐"
        });
        masterRules.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Reason = "主台不执行倒入操作"
        });
        var slaveRules = new ModifierStack();
        slaveRules.Push(new SuppressModifier
        {
            Operation = "POUR_OUT",
            Reason = "从台不执行倒出操作"
        });
        slaveRules.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Liquid = "蜜",
            Reason = "从台不直接交付满蜜罐"
        });
        slaveRules.Push(new SuppressModifier
        {
            Operation = "RESHAPE",
            Shape = "圆罐",
            Liquid = "蜜",
            Reason = "从台不使用满蜜罐改形"
        });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: masterRules);
        var slave = new Workshop("从台", new List<string> { "圆罐" }, modifiers: slaveRules);
        master.AddJar(new Jar("圆罐", id: 8251));
        master.AddJar(new Jar("圆罐", "蜜", id: 8252));
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "蜜" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());

        var result = Registry().Solve(demand, Snapshot(master, slave));

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Equal("9l_concrete_plan", result.CheckMethod);
        Assert.Equal(new[] { 8251 }, result.Plan!.TransferJarIds);
        Assert.Equal(1, result.Plan.TransferLiquids.GetValueOrDefault("蜜"));
        Assert.Equal("POUR_OUT", Assert.Single(result.Plan.MasterSteps).Operation!.Type);
        Assert.Equal("POUR_IN", Assert.Single(result.Plan.SlaveSteps).Operation!.Type);
        Assert.Equal(8251, result.Plan.SlaveSteps[0]
            .Before![result.Plan.SlaveSteps[0].Operation!.JarIndex!.Value].Id);
        Assert.Equal("蜜", result.Plan.SlaveSteps[0]
            .After![result.Plan.SlaveSteps[0].Operation!.JarIndex!.Value].Liquid);
    }

    [Fact]
    public void BClassEntryPointBudgetExhaustionIsUndetermined()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        master.AddJar(new Jar("圆罐", id: 8261));
        var solver = new JarOrderSpecificationSolver(new StateSpaceSolver()) { NodeBudget = 1 };
        var registry = new WorldOrderSolverRegistry(new[] { solver });

        var result = registry.Solve(Demand(Constants.OrderType.B), Snapshot(master, slave));

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
        Assert.Equal("9l_concrete_b", result.CheckMethod);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void BClassEntryPointAllowsSlaveLocalPreparationOutputToFeedLaterSlaveStep()
    {
        var master = new Workshop("禁酒主台",
            new List<string> { "圆罐", "方罐" },
            ban: new List<string> { "酒" });
        var slaveRules = new ModifierStack();
        slaveRules.Push(new SuppressModifier
        {
            Operation = "RESHAPE",
            Shape = "圆罐",
            Liquid = "酒",
            Reason = "禁止满酒罐直接改形，但允许先倒出再使用游离酒"
        });
        var slave = new Workshop("从台",
            new List<string> { "圆罐", "方罐" }, modifiers: slaveRules);
        master.AddJar(new Jar("圆罐", id: 8281));
        master.AddJar(new Jar("圆罐", "酒", id: 8282));
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "方罐" } },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());

        var result = Registry().Solve(demand, Snapshot(master, slave));

        Assert.True(result.IsFeasible, result.Reason);
        var plan = result.Plan!;
        Assert.Empty(plan.MasterSteps);
        Assert.Equal(new[] { "POUR_OUT", "RESHAPE" },
            plan.SlaveSteps.Select(x => x.Operation!.Type));
        Assert.Equal(8282, plan.SlaveSteps[0]
            .Before![plan.SlaveSteps[0].Operation!.JarIndex!.Value].Id);
        Assert.Equal(8281, plan.SlaveSteps[1]
            .Before![plan.SlaveSteps[1].Operation!.JarIndex!.Value].Id);
        Assert.Equal(1, plan.SlaveSteps[0].AfterLiquids.GetValueOrDefault("酒"));
        Assert.Equal(0, plan.SlaveSteps[1].AfterLiquids.GetValueOrDefault("酒"));
        Assert.Empty(plan.TransferLiquids);
    }

    [Fact]
    public void BPartitionBacktracksWhenAllSlaveAssignmentViolatesSlaveRule()
    {
        var slaveRules = new ModifierStack();
        slaveRules.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Reason = "从台不能匹配圆罐"
        });
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台", new List<string> { "方罐" }, modifiers: slaveRules);
        master.AddJar(new Jar("圆罐"));
        master.AddJar(new Jar("方罐"));
        var snapshot = Snapshot(master, slave);
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[]
                {
                    new Requirement { Shape = "圆罐" },
                    new Requirement { Shape = "方罐" }
                },
                Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());

        var result = new JarOrderSpecificationSolver(new StateSpaceSolver())
            .SolvePartitionOracleForDifferentialTesting(demand, snapshot);

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.NotNull(result.PartitionReport);
        Assert.Contains(result.PartitionReport!.Events,
            item => item.Stage == "slave-infeasible" && item.Mask == 0);
        Assert.Contains(result.PartitionReport.Events,
            item => item.Stage == "partition-selected" && item.Mask == 1);
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Plan!.MasterSteps,
            step => step.Operation?.Type == "MATCH" &&
                    step.Before![step.Operation.JarIndex!.Value].Shape == "圆罐");
        Assert.Contains(result.Plan.SlaveSteps,
            step => step.Operation?.Type == "MATCH" &&
                    step.Before![step.Operation.JarIndex!.Value].Shape == "方罐");
    }

    [Fact]
    public void BPartitionDoesNotTreatFeasibleSolverWithoutPathAsInfeasible()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var slave = new Workshop("从台");
        var solver = new JarOrderSpecificationSolver(new FeasibleWithoutPathSolver());
        var result = solver.SolvePartitionOracleForDifferentialTesting(
            Demand(Constants.OrderType.B), Snapshot(master, slave));

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
        Assert.NotNull(result.PartitionReport);
        Assert.Contains(result.PartitionReport!.Events,
            item => item.Stage == "undetermined-master-missing-path");
    }

    [Fact]
    public void BPartitionRequiresJarAndLiquidTerminalStateFromSameProof()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var slave = new Workshop("从台");
        var solver = new JarOrderSpecificationSolver(new FeasibleWithoutFinalLiquidsSolver());
        var result = solver.SolvePartitionOracleForDifferentialTesting(
            Demand(Constants.OrderType.B), Snapshot(master, slave));

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
        Assert.NotNull(result.PartitionReport);
        Assert.Contains(result.PartitionReport!.Events,
            item => item.Stage == "undetermined-master-final-state");
    }

    [Fact]
    public void BPartitionBudgetExhaustionCarriesUndeterminedTrace()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var slave = new Workshop("从台");
        var solver = new JarOrderSpecificationSolver(new StateSpaceSolver()) { NodeBudget = 1 };
        var result = solver.SolvePartitionOracleForDifferentialTesting(
            Demand(Constants.OrderType.B), Snapshot(master, slave));

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
        Assert.NotNull(result.PartitionReport);
        Assert.False(result.PartitionReport!.OutcomeFound);
        Assert.NotNull(result.PartitionReport.InconclusiveReason);
        Assert.Contains(result.PartitionReport.Events,
            item => item.Stage == "undetermined-master-budget");
    }

    [Fact]
    public void BSpecificationRequiresBothWorkshopRoles()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));

        var result = Registry().Solve(Demand(Constants.OrderType.B), Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.InvalidWorld, result.Status);
    }

    [Fact]
    public void WholeShapeSuppressionIsProjectedFromTheSnapshot()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier { Shape = "圆罐" });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        master.AddJar(new Jar("圆罐"));

        var result = Registry().Solve(Demand(), Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Infeasible, result.Status);
    }

    [Fact]
    public void MatchSuppressionIsSharedByWorkshopExecutionAndSolver()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier
        {
            Operation = "MATCH",
            Shape = "圆罐",
            Reason = "禁止直接匹配"
        });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        var jar = new Jar("圆罐");
        master.AddJar(jar);

        var direct = master.MarkJar(jar);
        Assert.False(direct.Success);
        Assert.Equal("禁止直接匹配", direct.Reason);
        Assert.False(jar.IsMarked);

        var result = Registry().Solve(Demand(), Snapshot(master));
        Assert.Equal(WorldOrderSolveStatus.Infeasible, result.Status);
    }

    [Fact]
    public void OperationSpecificSuppressionDoesNotDeleteJarOrBlockMatch()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒",
            Reason = "禁止灌酒"
        });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        master.AddJar(new Jar("圆罐"));

        var result = Registry().Solve(Demand(), Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.NotNull(result.Plan);
        Assert.Equal("MATCH", Assert.Single(result.Plan!.MasterSteps).Operation!.Type);
    }

    [Fact]
    public void GrantModifierParticipatesInTheSameRuleContextUsedByTheSolver()
    {
        var stack = new ModifierStack();
        stack.Push(new GrantModifier { Shape = "方罐", Source = "test" });
        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        master.AddJar(new Jar("圆罐"));
        master.AddLiquid("酒", 1);
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "方罐" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());

        var result = Registry().Solve(demand, Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Feasible, result.Status);
        Assert.Contains(result.Plan!.MasterSteps, step => step.Operation?.Type == "RESHAPE");
    }

    [Fact]
    public void ExhaustedSolverBudgetIsUndeterminedNotInfeasible()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        var solver = new JarOrderSpecificationSolver(new StateSpaceSolver()) { NodeBudget = 1 };
        var registry = new WorldOrderSolverRegistry(new[] { solver });

        var result = registry.Solve(Demand(), Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.Undetermined, result.Status);
    }

    [Fact]
    public void RegistryDoesNotSolveDemandDuringItsCooldown()
    {
        var demand = new OrderDemand(
            new JarOrderSpecification(new[] { new Requirement { Shape = "圆罐" } }, Constants.OrderType.A, Words),
            1_000, new SequentialIdGenerator());
        demand.EnterCooldown();
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));

        var result = Registry().Solve(demand, Snapshot(master));

        Assert.Equal(WorldOrderSolveStatus.NotReady, result.Status);
    }

    [Fact]
    public void BSpecificationRejectsNonEmptySlaveSnapshot()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        master.AddJar(new Jar("圆罐"));
        slave.AddJar(new Jar("方罐"));

        var result = Registry().Solve(Demand(Constants.OrderType.B), Snapshot(master, slave));

        Assert.Equal(WorldOrderSolveStatus.InvalidWorld, result.Status);
    }

    [Fact]
    public void RegistryRejectsSolverResultBoundToAnotherDemand()
    {
        var demand = Demand();
        var snapshot = Snapshot(new Workshop("主台"));
        var registry = new WorldOrderSolverRegistry(new[]
        {
            new MismatchedResultSolver()
        });

        Assert.Throws<System.InvalidOperationException>(() => registry.Solve(demand, snapshot));
    }

    private sealed class MismatchedResultSolver : IWorldOrderSpecificationSolver
    {
        public System.Type SpecificationType => typeof(JarOrderSpecification);

        public WorldOrderSolveResult Solve(OrderDemand demand, WorldSnapshot snapshot) =>
            new(demand.Id + 1, snapshot.Version, WorldOrderSolveStatus.Infeasible);
    }

    private sealed class FeasibleWithoutPathSolver : ISolver
    {
        public SolverResult Solve(
            List<Requirement> requirements,
            WorkshopInfo workshopInfo,
            List<JarState> jars,
            Dictionary<string, int> liquids,
            int transferBudget = int.MaxValue) =>
            new()
            {
                Feasible = true,
                FinalJars = jars,
                FinalLiquids = new Dictionary<string, int>(liquids)
            };
    }

    private sealed class FeasibleWithoutFinalLiquidsSolver : ISolver
    {
        public SolverResult Solve(
            List<Requirement> requirements,
            WorkshopInfo workshopInfo,
            List<JarState> jars,
            Dictionary<string, int> liquids,
            int transferBudget = int.MaxValue) =>
            new()
            {
                Feasible = true,
                Path = new List<SolverStep>(),
                FinalJars = jars,
                FinalLiquids = null
            };
    }

    private sealed class ForeignSpecification : IOrderSpecification
    {
    }

    [Fact]
    public void ShipInventoryIsTheSolverSupplySourceWhenPresent()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddJar(new Jar("圆罐"));
        master.AddLiquid("蜜", 1); // 旧工作台池不再为新路径提供液体
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[] { new Requirement { Shape = "圆罐", Liquid = "酒" } },
                Constants.OrderType.A, Words),
            0, new SequentialIdGenerator());

        var suppliedByShip = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master }, master),
            new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 1 }));
        var first = Registry().Solve(demand, suppliedByShip);

        Assert.Equal(WorldOrderSolveStatus.Feasible, first.Status);

        master.AddLiquid("酒", 1);
        var shipWithoutWine = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { master }, master),
            new ShipLiquidInventory(4, new Dictionary<string, int> { ["蜜"] = 1 }));
        var second = Registry().Solve(demand, shipWithoutWine);

        Assert.Equal(WorldOrderSolveStatus.Infeasible, second.Status);
    }


    [Fact]
    public void BPartitionCountsRequiredSlaveTransferAgainstMasterStagingCapacity()
    {
        var master = new Workshop("主台", new List<string>(),
            maxJars: 4, maxLiquids: 1, vocabulary: Words);
        var slave = new Workshop("从台", new List<string> { "圆罐" },
            maxJars: 4, maxLiquids: 4, vocabulary: Words);
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        master.AddJar(new Jar("圆罐", vocabulary: Words));
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 2 });
        var snapshot = WorldSnapshotFactory.Capture(roster, ship);
        var demand = new OrderDemand(
            new JarOrderSpecification(
                new[]
                {
                    new Requirement { Shape = "圆罐", Liquid = "酒" },
                    new Requirement { Shape = "圆罐", Liquid = "酒" }
                }, Constants.OrderType.B, Words),
            0, new SequentialIdGenerator());

        var result = new JarOrderSpecificationSolver(new StateSpaceSolver())
            .SolvePartitionOracleForDifferentialTesting(demand, snapshot);

        Assert.Equal(WorldOrderSolveStatus.Infeasible, result.Status);
        Assert.Contains("主台订单液体路径及从台交接预留", result.Reason ?? string.Empty);
        Assert.NotNull(result.PartitionReport);
        Assert.True(result.PartitionReport!.IsInfeasible);
        Assert.Contains(result.PartitionReport.Events,
            item => item.Stage == "master-staging-capacity-rejected");
    }

}

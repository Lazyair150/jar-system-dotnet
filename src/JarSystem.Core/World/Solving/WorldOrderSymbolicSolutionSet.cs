using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 一个规则级订单配方组合。每项需求选择一个规则候选及其工作台角色；不包含实际 Jar ID。
/// 它是资源绑定的输入，不是已经预留资源或可直接提交执行的 BoundExecutionPlan。
/// </summary>
public sealed class WorldOrderSymbolicSolution
{
    public IReadOnlyList<WorldOrderAssignedRuleCandidate> Steps { get; }
    public IReadOnlyDictionary<string, int> GrossFreeLiquidConsumption { get; }
    public IReadOnlyDictionary<string, int> GrossFreeLiquidProduction { get; }
    public IReadOnlyDictionary<string, int> GrossJarLiquidConsumption { get; }

    internal WorldOrderSymbolicSolution(IEnumerable<WorldOrderAssignedRuleCandidate> steps)
    {
        var copied = steps.ToArray();
        Steps = Array.AsReadOnly(copied);
        GrossFreeLiquidConsumption = Sum(copied.Select(x => x.Candidate.ConsumedFreeLiquid));
        GrossFreeLiquidProduction = Sum(copied.Select(x => x.Candidate.ProducedFreeLiquid));
        GrossJarLiquidConsumption = Sum(copied.Select(x => x.Candidate.ConsumedJarLiquid));
    }

    private static IReadOnlyDictionary<string, int> Sum(IEnumerable<string?> liquids)
    {
        var amounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var liquid in liquids)
        {
            if (liquid == null) continue;
            amounts[liquid] = checked(amounts.GetValueOrDefault(liquid) + 1);
        }
        return new ReadOnlyDictionary<string, int>(amounts);
    }
}

public sealed record WorldOrderAssignedRuleCandidate(
    string WorkshopRole,
    WorldOrderRuleCandidate Candidate);

public enum WorldOrderSymbolicSolutionStatus
{
    EnumerationComplete,
    NoDirectCandidate,
    Undetermined
}

/// <summary>
/// 有限预算下枚举的规则级候选分配集合。
/// EnumerationComplete 只代表本层单步候选组合已穷举，不代表实际 Jar/液体资源可绑定。
/// NoDirectCandidate 只代表没有单步候选，不能据此宣告完整订单无解；
/// Undetermined 可能含部分结果，也不能用于宣告无解。
/// </summary>
public sealed class WorldOrderSymbolicSolutionSet
{
    public WorldOrderSymbolicSolutionStatus Status { get; }
    public string RuleSetVersion { get; }
    public string WorkshopProfileVersion { get; }
    public string SpecificationVersion { get; }
    public IReadOnlyList<WorldOrderSymbolicSolution> Solutions { get; }
    public int ExploredNodes { get; }
    public string? Reason { get; }

    public bool IsEnumerationComplete => Status == WorldOrderSymbolicSolutionStatus.EnumerationComplete;
    public bool IsResourceBound => false;
    public bool IsUndetermined => Status == WorldOrderSymbolicSolutionStatus.Undetermined;

    internal WorldOrderSymbolicSolutionSet(
        WorldOrderSymbolicSolutionStatus status,
        string ruleSetVersion,
        string workshopProfileVersion,
        string specificationVersion,
        IEnumerable<WorldOrderSymbolicSolution> solutions,
        int exploredNodes,
        string? reason)
    {
        Status = status;
        RuleSetVersion = ruleSetVersion;
        WorkshopProfileVersion = workshopProfileVersion;
        SpecificationVersion = specificationVersion;
        Solutions = Array.AsReadOnly(solutions.ToArray());
        ExploredNodes = exploredNodes;
        Reason = reason;
    }
}

/// <summary>
/// 将各岗位的单 Jar 规则候选组合成每需求一项的规则级配方集合。
/// 此层不检查 Jar 数量、Jar 身份、资源账户余额、容量峰值或 Master→Slave 实际转移。
/// </summary>
public static class WorldOrderSymbolicSolutionBuilder
{
    public static WorldOrderSymbolicSolutionSet Build(
        IReadOnlyList<WorldOrderRuleCandidateProfile> profiles,
        int maxSolutions = 256,
        int maxNodes = 100_000)
    {
        if (profiles == null || profiles.Count == 0)
            throw new ArgumentException("至少需要一个工作台规则候选 Profile", nameof(profiles));
        if (maxSolutions <= 0) throw new ArgumentOutOfRangeException(nameof(maxSolutions));
        if (maxNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maxNodes));

        if (profiles.Any(x => x == null))
            throw new ArgumentException("规则候选 Profile 不能为 null", nameof(profiles));
        var first = profiles[0];
        if (profiles.Select(x => x.Role).Distinct(StringComparer.Ordinal).Count() != profiles.Count)
            throw new ArgumentException("工作台角色不能重复", nameof(profiles));
        if (profiles.Any(x =>
            x.RuleSetVersion != first.RuleSetVersion ||
            x.WorkshopProfileVersion != first.WorkshopProfileVersion ||
            x.SpecificationVersion != first.SpecificationVersion ||
            x.Requirements.Count != first.Requirements.Count))
            throw new ArgumentException("参与组合的规则候选 Profile 必须来自同一求解输入", nameof(profiles));

        if (profiles.Any(x => x.IsUndetermined))
            return Result(WorldOrderSymbolicSolutionStatus.Undetermined,
                profiles, Array.Empty<WorldOrderSymbolicSolution>(), 0,
                profiles.First(x => x.IsUndetermined).Reason ?? "规则候选 Profile 未能确定");

        var choices = new List<IReadOnlyList<WorldOrderAssignedRuleCandidate>>(first.Requirements.Count);
        for (var requirementIndex = 0; requirementIndex < first.Requirements.Count; requirementIndex++)
        {
            var requirementChoices = new List<WorldOrderAssignedRuleCandidate>();
            foreach (var profile in profiles)
            {
                var requirement = profile.Requirements[requirementIndex];
                if (requirement.RequirementIndex != requirementIndex)
                    throw new InvalidOperationException("规则候选 Profile 的需求顺序不一致");
                foreach (var candidate in requirement.Candidates)
                    requirementChoices.Add(new WorldOrderAssignedRuleCandidate(profile.Role, candidate));
            }

            if (requirementChoices.Count == 0)
                return Result(WorldOrderSymbolicSolutionStatus.NoDirectCandidate,
                    profiles, Array.Empty<WorldOrderSymbolicSolution>(), 0,
                    "需求 " + (requirementIndex + 1) + " 在所有工作台规则下均无合法 Jar 算子候选");
            choices.Add(requirementChoices.AsReadOnly());
        }

        var solutions = new List<WorldOrderSymbolicSolution>();
        var selected = new WorldOrderAssignedRuleCandidate[choices.Count];
        var exploredNodes = 0;
        var nodeBudgetExceeded = false;
        var solutionBudgetExceeded = false;

        void Visit(int requirementIndex)
        {
            if (nodeBudgetExceeded || solutionBudgetExceeded) return;
            if (requirementIndex == choices.Count)
            {
                if (solutions.Count >= maxSolutions)
                {
                    solutionBudgetExceeded = true;
                    return;
                }
                solutions.Add(new WorldOrderSymbolicSolution(selected));
                return;
            }

            foreach (var choice in choices[requirementIndex])
            {
                if (exploredNodes >= maxNodes)
                {
                    nodeBudgetExceeded = true;
                    return;
                }
                exploredNodes++;
                selected[requirementIndex] = choice;
                Visit(requirementIndex + 1);
                if (nodeBudgetExceeded || solutionBudgetExceeded) return;
            }
        }

        Visit(0);
        if (nodeBudgetExceeded || solutionBudgetExceeded)
            return Result(WorldOrderSymbolicSolutionStatus.Undetermined,
                profiles, solutions, exploredNodes,
                nodeBudgetExceeded ? "规则级解集枚举超出节点预算" : "规则级解集超过结果数量上限");

        return Result(
            solutions.Count == 0
                ? WorldOrderSymbolicSolutionStatus.NoDirectCandidate
                : WorldOrderSymbolicSolutionStatus.EnumerationComplete,
            profiles, solutions, exploredNodes, null);
    }

    private static WorldOrderSymbolicSolutionSet Result(
        WorldOrderSymbolicSolutionStatus status,
        IReadOnlyList<WorldOrderRuleCandidateProfile> profiles,
        IEnumerable<WorldOrderSymbolicSolution> solutions,
        int exploredNodes,
        string? reason)
    {
        var first = profiles[0];
        return new WorldOrderSymbolicSolutionSet(
            status, first.RuleSetVersion, first.WorkshopProfileVersion,
            first.SpecificationVersion, solutions, exploredNodes, reason);
    }
}

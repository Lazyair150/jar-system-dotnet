using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Solver;

namespace JarSystem.Core.World.Solving;

/// <summary>
/// 将不可变求解计划解析为执行准入所需的精确资源占用清单。
/// 这里只解析/验证，不修改世界、不预留资源，也不启动执行。
/// </summary>
public sealed class WorldOrderResourceManifest
{
    public int DemandId { get; }
    public string WorldVersion { get; }
    public bool IsPartitioned { get; }
    public IReadOnlyList<int> MasterJarIds { get; }
    public IReadOnlyList<int> TransferJarIds { get; }
    public IReadOnlyList<int> LockedJarIds { get; }
    public IReadOnlyDictionary<string, int> MasterInitialLiquidAmounts { get; }
    public IReadOnlyDictionary<string, int> TransferLiquids { get; }
    /// <summary>主台路径完成后的实际全部剩余游离液体；仅用于核对主台终态，不代表泵送批次。</summary>
    public IReadOnlyDictionary<string, int> MasterTransferLiquidAmounts { get; }
    public int MasterStagingPeakCapacity { get; }
    public int SlaveStagingPeakCapacity { get; }

    private WorldOrderResourceManifest(
        int demandId, string worldVersion, bool isPartitioned,
        IEnumerable<int> masterJarIds, IEnumerable<int> transferJarIds,
        IDictionary<string, int> masterLiquids, IDictionary<string, int> transferLiquids,
        IDictionary<string, int> masterTransferLiquids,
        int masterStagingPeakCapacity, int slaveStagingPeakCapacity)
    {
        DemandId = demandId;
        WorldVersion = worldVersion;
        IsPartitioned = isPartitioned;
        MasterJarIds = Array.AsReadOnly(masterJarIds.Distinct().OrderBy(x => x).ToArray());
        TransferJarIds = Array.AsReadOnly(transferJarIds.Distinct().OrderBy(x => x).ToArray());
        LockedJarIds = Array.AsReadOnly(MasterJarIds.Concat(TransferJarIds).Distinct().OrderBy(x => x).ToArray());
        MasterInitialLiquidAmounts = Freeze(masterLiquids);
        TransferLiquids = Freeze(transferLiquids);
        MasterTransferLiquidAmounts = Freeze(masterTransferLiquids);
        MasterStagingPeakCapacity = masterStagingPeakCapacity;
        SlaveStagingPeakCapacity = slaveStagingPeakCapacity;
    }

    public static WorldOrderResourceManifest Create(WorldOrderSolveResult result, WorldSnapshot snapshot)
    {
        if (result == null) throw new ArgumentNullException(nameof(result));
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (!result.IsFeasible || result.Plan == null)
            throw new InvalidOperationException("只有携带计划的 Feasible 结果才能生成资源清单");
        if (!string.Equals(result.WorldVersion, snapshot.Version, StringComparison.Ordinal))
            throw new InvalidOperationException("求解结果与当前世界快照版本不一致");
        if (snapshot.Master == null)
            throw new InvalidOperationException("资源清单要求快照中存在主工作台");

        var plan = result.Plan;
        var masterIds = snapshot.Master.Jars.Select(x => RequireId(x.Id, "主台快照")).ToHashSet();
        var masterPathIds = ReadPathJarIds(plan.MasterSteps, masterIds, expectMasterOrigin: false, "主台路径");
        var transferIds = plan.TransferJarIds.ToArray();

        if (transferIds.Distinct().Count() != transferIds.Length)
            throw new InvalidOperationException("转移罐子清单包含重复 ID");
        if (transferIds.Any(id => !masterIds.Contains(id)))
            throw new InvalidOperationException("转移罐子不属于当前主台快照");

        Dictionary<string, int> transferLiquids;
        Dictionary<string, int> masterInitial;
        Dictionary<string, int> masterTransfer;
        int masterPeak;
        int slavePeak;
        if (!plan.IsPartitioned)
        {
            if (snapshot.Slave != null && plan.SlaveSteps.Count != 0)
                throw new InvalidOperationException("单阶段计划不能包含从台路径");
            if (transferIds.Length != 0 || plan.TransferLiquids.Any(x => x.Value != 0))
                throw new InvalidOperationException("单阶段计划不能包含资源转移");
            transferLiquids = new Dictionary<string, int>(StringComparer.Ordinal);
            masterInitial = RequiredInitialLiquids(plan.MasterSteps, null);
            var masterSimulation = SimulatePath(plan.MasterSteps, masterInitial);
            masterTransfer = masterSimulation.FinalBalances;
            masterPeak = masterSimulation.PeakCapacity;
            slavePeak = 0;
        }
        else
        {
            if (snapshot.Slave == null)
                throw new InvalidOperationException("B 类计划要求从台快照");
            if (snapshot.Slave.Jars.Count != 0 || snapshot.Slave.Liquids.Values.Any(x => x != 0))
                throw new InvalidOperationException("B 类计划对应的从台快照必须为空");

            var slavePathIds = ReadPathJarIds(
                plan.SlaveSteps, transferIds.ToHashSet(), expectMasterOrigin: true, "从台路径");
            if (slavePathIds.Any(id => !transferIds.Contains(id)))
                throw new InvalidOperationException("从台路径使用了未列入 TransferJarIds 的罐子");

            transferLiquids = RequiredInitialLiquids(plan.SlaveSteps, null);
            ValidateLiquidMap(plan.TransferLiquids, "TransferLiquids");
            if (!DictionaryEqual(transferLiquids, plan.TransferLiquids))
                throw new InvalidOperationException("TransferLiquids 与从台路径重新计算的最低需求不一致");

            masterInitial = RequiredInitialLiquids(plan.MasterSteps, transferLiquids);
            var masterSimulation = SimulatePath(plan.MasterSteps, masterInitial);
            masterTransfer = masterSimulation.FinalBalances;
            if (!DictionaryAtLeast(masterTransfer, transferLiquids))
                throw new InvalidOperationException("主台路径结束时的液体余额不足以满足从台路径");
            masterPeak = masterSimulation.PeakCapacity;
            // 从台只接收其执行路径所需的最低初始供给；主台额外剩余液体不进入从台容量计算。
            var slaveSimulation = SimulatePath(plan.SlaveSteps, transferLiquids);
            slavePeak = slaveSimulation.PeakCapacity;
        }

        return new WorldOrderResourceManifest(
            result.DemandId, result.WorldVersion, plan.IsPartitioned,
            masterPathIds, transferIds, masterInitial, transferLiquids,
            masterTransfer, masterPeak, slavePeak);
    }

    private static HashSet<int> ReadPathJarIds(
        IReadOnlyList<WorldPlanStep> steps, HashSet<int> allowedIds,
        bool expectMasterOrigin, string pathName)
    {
        var ids = new HashSet<int>();
        foreach (var step in steps)
        {
            ValidateStep(step, pathName);
            var index = step.Operation!.JarIndex!.Value;
            var jar = step.Before[index];
            var id = RequireId(jar.Id, pathName);
            if (!allowedIds.Contains(id))
                throw new InvalidOperationException(pathName + "引用了不属于对应工作台/转移集合的罐子");
            if (expectMasterOrigin && !jar.MasterOrigin)
                throw new InvalidOperationException("从台路径引用的罐子未标记为主台来源");
            if (!expectMasterOrigin && jar.MasterOrigin)
                throw new InvalidOperationException("主台路径不能引用主台来源转移投影");
            ids.Add(id);
        }
        return ids;
    }

    private static void ValidateStep(WorldPlanStep step, string pathName)
    {
        if (step == null || step.Operation == null || !step.Operation.JarIndex.HasValue)
            throw new InvalidOperationException(pathName + "存在缺少操作或 JarIndex 的步骤");
        var op = step.Operation;
        var before = step.Before;
        var after = step.After;
        var index = op.JarIndex.Value;
        if (before.Count == 0 || before.Count != after.Count || index < 0 || index >= before.Count)
            throw new InvalidOperationException(pathName + "的 JarIndex 或 Before/After 长度无效");

        for (var i = 0; i < before.Count; i++)
        {
            if (before[i].Id != after[i].Id || before[i].MasterOrigin != after[i].MasterOrigin)
                throw new InvalidOperationException(pathName + "的步骤改变了罐子身份或来源标记");
            if (i != index && (before[i].Shape != after[i].Shape ||
                before[i].Liquid != after[i].Liquid || before[i].IsMarked != after[i].IsMarked))
                throw new InvalidOperationException(pathName + "的步骤意外修改了非目标罐子");
        }

        var source = before[index];
        var target = after[index];
        var expected = new Dictionary<string, int>(StringComparer.Ordinal);
        switch (op.Type)
        {
            case "MATCH":
                if (source.Shape != target.Shape || source.Liquid != target.Liquid || !target.IsMarked)
                    throw new InvalidOperationException("MATCH 的状态变化不符合求解器语义");
                break;
            case "POUR_IN":
                if (source.Liquid != null || string.IsNullOrWhiteSpace(op.TargetLiquid) ||
                    target.Liquid != op.TargetLiquid || source.Shape != target.Shape || !target.IsMarked)
                    throw new InvalidOperationException("POUR_IN 的状态变化不符合求解器语义");
                expected[op.TargetLiquid] = 1;
                break;
            case "POUR_OUT":
                if (source.Liquid == null || target.Liquid != null ||
                    source.Shape != target.Shape || !target.IsMarked)
                    throw new InvalidOperationException("POUR_OUT 的状态变化不符合求解器语义");
                break;
            case "RESHAPE":
                if (string.IsNullOrWhiteSpace(op.TargetShape) || target.Shape != op.TargetShape ||
                    !target.IsMarked || target.Liquid != null)
                    throw new InvalidOperationException("RESHAPE 的状态变化不符合求解器语义");
                if (op.FullReshape)
                {
                    if (source.Liquid == null || op.CostLiquid != source.Liquid)
                        throw new InvalidOperationException("满罐改形必须消耗罐内原液体");
                }
                else
                {
                    if (source.Liquid != null || string.IsNullOrWhiteSpace(op.CostLiquid))
                        throw new InvalidOperationException("空罐改形必须消耗游离液体");
                    expected[op.CostLiquid] = 1;
                }
                break;
            default:
                throw new InvalidOperationException("未知求解操作类型: " + op.Type);
        }

        ValidateLiquidMap(op.Consumed, pathName + ".Consumed");
        if (!DictionaryEqual(expected, op.Consumed))
            throw new InvalidOperationException(pathName + "的 Consumed 与操作真实液体语义不一致");
    }

    private static (Dictionary<string, int> FinalBalances, int PeakCapacity) SimulatePath(
        IReadOnlyList<WorldPlanStep> steps, IReadOnlyDictionary<string, int> initial)
    {
        var balances = initial.Where(x => x.Value > 0)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var peak = ShipLiquidInventory.TotalAmount(balances);
        foreach (var step in steps)
        {
            ValidateStep(step, "液体路径");
            var op = step.Operation!;
            if (op.Type == "POUR_IN")
                Add(balances, op.TargetLiquid!, -1);
            else if (op.Type == "RESHAPE" && !op.FullReshape)
                Add(balances, op.CostLiquid!, -1);
            else if (op.Type == "POUR_OUT")
                Add(balances, step.Before[op.JarIndex!.Value].Liquid!, 1);

            if (balances.Values.Any(x => x < 0))
                throw new InvalidOperationException("路径模拟出现负液体余额");
            foreach (var key in balances.Where(x => x.Value == 0).Select(x => x.Key).ToArray())
                balances.Remove(key);
            peak = Math.Max(peak, ShipLiquidInventory.TotalAmount(balances));
        }
        return (balances, peak);
    }

    private static bool DictionaryAtLeast(
        IReadOnlyDictionary<string, int> actual, IReadOnlyDictionary<string, int> required) =>
        required.All(x => actual.GetValueOrDefault(x.Key) >= x.Value);

    // 计算路径任意前缀的最大净消耗；追加 transfer 量可确保主台执行完后仍能供给从台。
    private static Dictionary<string, int> RequiredInitialLiquids(
        IReadOnlyList<WorldPlanStep> steps, IReadOnlyDictionary<string, int>? requiredAtEnd)
    {
        var net = new Dictionary<string, int>(StringComparer.Ordinal);
        var peak = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            ValidateStep(step, "液体路径");
            var op = step.Operation!;
            if (op.Type == "POUR_IN")
                Add(net, op.TargetLiquid!, 1);
            else if (op.Type == "RESHAPE" && !op.FullReshape)
                Add(net, op.CostLiquid!, 1);
            else if (op.Type == "POUR_OUT")
            {
                var liquid = step.Before[op.JarIndex!.Value].Liquid!;
                Add(net, liquid, -1);
            }

            foreach (var item in net)
                peak[item.Key] = Math.Max(peak.GetValueOrDefault(item.Key), item.Value);
        }

        if (requiredAtEnd != null)
        {
            foreach (var item in requiredAtEnd)
                peak[item.Key] = Math.Max(peak.GetValueOrDefault(item.Key), net.GetValueOrDefault(item.Key) + item.Value);
        }

        foreach (var key in peak.Keys.ToArray())
            peak[key] = Math.Max(0, peak[key]);
        return peak;
    }

    private static void Add(Dictionary<string, int> values, string key, int amount) =>
        values[key] = checked(values.GetValueOrDefault(key) + amount);

    private static int RequireId(int? id, string source) =>
        id is > 0 ? id.Value : throw new InvalidOperationException(source + "包含没有有效 ID 的罐子");

    private static void ValidateLiquidMap(IReadOnlyDictionary<string, int> values, string name)
    {
        if (values.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value < 0))
            throw new InvalidOperationException(name + "包含无效液体名称或负数数量");
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, int> left, IReadOnlyDictionary<string, int> right)
    {
        var keys = left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal);
        return keys.All(key => left.GetValueOrDefault(key) == right.GetValueOrDefault(key));
    }

    private static IReadOnlyDictionary<string, int> Freeze(IDictionary<string, int> values) =>
        new ReadOnlyDictionary<string, int>(
            values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
}

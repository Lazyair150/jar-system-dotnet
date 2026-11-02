using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>两个工作台的禁令是否一致（逐液体比较，判断依据仅禁令，不含基础/附加标记）。</summary>
internal static class BanComparison
{
    public static bool Same(Workshop a, Workshop b)
    {
        foreach (var l in Constants.LIQUIDS)
            if (a.IsBanned(l) != b.IsBanned(l))
                return false;
        return true;
    }
}

/// <summary>
/// 切换事件的分支策略（原 <c>ExecuteCase1/ExecuteCase2</c>）。
/// 两个实现的差异只在「禁令相同 / 不同」这一维度，其余（转移阶段、回滚）由编排者负责。
/// </summary>
internal interface ISwitchCase
{
    /// <summary>对应的 <see cref="Constants.SwitchCaseType"/> 取值（对外契约）。</summary>
    string CaseType { get; }

    /// <summary>本策略是否适用于这对工作台。</summary>
    bool Applies(Workshop oldMaster, Workshop newMaster);

    /// <summary>执行本分支的资源重组（不负责转移与回滚）。</summary>
    void Execute(Workshop oldMaster, Workshop newMaster, SwitchOperationLog log);
}

/// <summary>
/// Case 1（禁令相同）：把游离液体尽量灌进旧台基础形状的空罐。
/// 优先用游离液体；没有游离液体时，从「非基础形状的满罐（液体未禁、存储有空间）」倒出再倒入。
/// 目的：新主台接收「已装满的罐子」作为额外液体容量。
/// </summary>
internal sealed class SameBansCase : ISwitchCase
{
    public string CaseType => Constants.SwitchCaseType.SAME_BANS;

    public bool Applies(Workshop oldMaster, Workshop newMaster) => BanComparison.Same(oldMaster, newMaster);

    public void Execute(Workshop oldMaster, Workshop newMaster, SwitchOperationLog log)
    {
        var baseShapes = oldMaster.BaseMarks;
        // 以「基础形状空罐」为驱动：先尽量用游离液体；没有游离液体时，
        // 从任意非基础形状的满罐（未禁液体）倒出补充，再倒入空罐。
        while (true)
        {
            var emptyJar = oldMaster.Jars
                .FirstOrDefault(j => j.IsEmpty() && !j.IsMarked &&
                    baseShapes.Contains(j.Shape));
            if (emptyJar == null) break;

            // 优先使用游离液体（未禁、有库存）
            var freeLiquid = Constants.LIQUIDS.FirstOrDefault(l =>
                !oldMaster.IsBanned(l) && oldMaster.LiquidStorage.GetValueOrDefault(l, 0) > 0);
            if (freeLiquid != null)
            {
                var r = oldMaster.PourIn(emptyJar, freeLiquid);
                if (r.Success) { log.Add("pour_in", $"向 {emptyJar.GetDescription()} 倒入 {freeLiquid}"); continue; }
                break;
            }

            // 无游离液体：从非基础形状满罐倒出（任意未禁液体，且存储有空间）
            var sourceFull = oldMaster.Jars
                .FirstOrDefault(j => j.Liquid != null && !j.IsMarked &&
                    !oldMaster.IsBanned(j.Liquid) &&
                    !baseShapes.Contains(j.Shape) &&
                    oldMaster.LiquidStorage.GetValueOrDefault(j.Liquid) < oldMaster.MaxLiquids);
            if (sourceFull == null) break;

            var pourResult = oldMaster.PourOut(sourceFull);
            if (!pourResult.Success) break;
            log.Add("pour_out", $"从 {sourceFull.GetDescription()} 倒出 {pourResult.Liquid}");
            var pourInResult = oldMaster.PourIn(emptyJar, pourResult.Liquid!);
            if (pourInResult.Success)
                log.Add("pour_in", $"向 {emptyJar.GetDescription()} 倒入 {pourResult.Liquid}");
            else
                break;
        }
    }
}

/// <summary>
/// Case 2（禁令不同）：旧台临时获得新台基础形状作为附加标记，把旧台罐子改形为新台形状。
/// 步骤：游离液体改形空罐 → 满罐用自身液体改形 → 仍缺液体时从满罐倒出补液再改形。
/// 剩余游离液体**不在此清空**，交由转移阶段按禁令/容量处理。
/// </summary>
internal sealed class DifferentBansCase : ISwitchCase
{
    public string CaseType => Constants.SwitchCaseType.DIFFERENT_BANS;

    public bool Applies(Workshop oldMaster, Workshop newMaster) => !BanComparison.Same(oldMaster, newMaster);

    public void Execute(Workshop oldMaster, Workshop newMaster, SwitchOperationLog log)
    {
        // 旧台在本作用域内临时获得新台的基础形状作为附加标记（作用域结束自动回收，异常时同样回收）
        using var temporaryMarks = oldMaster.WithTemporaryAdditionalMarks(newMaster.BaseMarks);
        {
            // The shapes we want to transform TO are new's base marks
            var targetShapes = newMaster.BaseMarks.ToList();

            // Step 1: Use free liquids to reshape old-base jars into new-base shapes
            var availableLiquids = oldMaster.LiquidStorage
                .Where(kv => kv.Value > 0 && !oldMaster.IsBanned(kv.Key))
                .Select(kv => kv.Key).ToList();

            foreach (var target in targetShapes)
            {
                foreach (var costLiquid in availableLiquids)
                {
                    while (oldMaster.LiquidStorage.GetValueOrDefault(costLiquid, 0) > 0)
                    {
                        var sourceJar = oldMaster.Jars
                            .FirstOrDefault(j => j.IsEmpty() && !j.IsMarked &&
                                oldMaster.CanTransform(j.Shape, target));
                        if (sourceJar == null) break;

                        var result = oldMaster.TryReshape(sourceJar, target, ReshapeMode.Empty, costLiquid);
                        if (result.Success)
                            log.Add("reshape_empty", $"{sourceJar.GetDescription()} → {target} (消耗{costLiquid})");
                        else break;
                    }
                }
            }

            // Step 2: ReshapeFull - old master's full jars use own liquid to reshape
            // into new master's base shapes (via temporarily added additional marks)
            var allTargets = newMaster.BaseMarks.ToList();
            foreach (var target in allTargets)
            {
                while (true)
                {
                    var fullJar = oldMaster.Jars
                        .FirstOrDefault(j => j.IsFull() && !j.IsMarked &&
                            !oldMaster.IsBanned(j.Liquid!) &&
                            oldMaster.CanTransform(j.Shape, target));
                    if (fullJar == null) break;

                    var liquid = fullJar.Liquid;
                    var result = oldMaster.TryReshape(fullJar, target, ReshapeMode.Full);
                    if (result.Success)
                        log.Add("reshape_full", $"{fullJar.GetDescription()} → {target} (自耗{liquid})");
                    else break;
                }
            }

            // Step 3: If still liquid < jars, pour out from non-matching full jars for more liquid
            foreach (var target in newMaster.BaseMarks)
            {
                while (true)
                {
                    var emptySource = oldMaster.Jars
                        .FirstOrDefault(j => j.IsEmpty() && !j.IsMarked &&
                            oldMaster.CanTransform(j.Shape, target));
                    if (emptySource == null) break;

                    // Need liquid - try pouring out from a full jar of non-banned liquid
                    var costLiquid = availableLiquids
                        .FirstOrDefault(l => oldMaster.LiquidStorage.GetValueOrDefault(l, 0) > 0);
                    if (costLiquid != null)
                    {
                        // Already tried free liquids, try pouring out
                        var pourSource = oldMaster.Jars
                            .FirstOrDefault(j => j.IsFull() && !j.IsMarked &&
                                !oldMaster.IsBanned(j.Liquid!) &&
                                oldMaster.LiquidStorage.GetValueOrDefault(j.Liquid!, 0) < oldMaster.MaxLiquids);
                        if (pourSource == null) break;

                        var pourResult = oldMaster.PourOut(pourSource);
                        if (pourResult.Success)
                        {
                            log.Add("pour_out", $"从 {pourSource.GetDescription()} 倒出 {pourResult.Liquid}");
                            var reshapeResult = oldMaster.TryReshape(emptySource, target, ReshapeMode.Empty, pourResult.Liquid);
                            if (reshapeResult.Success)
                                log.Add("reshape_empty", $"{emptySource.GetDescription()} → {target} (消耗{pourResult.Liquid})");
                        }
                        else break;
                    }
                    else break;
                }
            }
        }

        // 不再无条件清空游离液体：新主台可用的液体由 TransferAll 按禁令/容量转移，
        // 不可用的才在转移阶段丢弃并记录日志。
    }
}

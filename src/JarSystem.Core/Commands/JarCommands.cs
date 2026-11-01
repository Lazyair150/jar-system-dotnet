using JarSystem.Core.Abstractions;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Commands;

public class PourInCommand : Command
{
    public Jar Jar { get; }
    public string Liquid { get; }

    public PourInCommand(Jar jar, string liquid)
        : base(Config.Constants.OperationType.POUR_IN, new() { ["jarId"] = jar.Id, ["liquid"] = liquid })
    {
        Jar = jar;
        Liquid = liquid;
    }

    public override CommandResult Execute(Workshop workshop, ICommandJournal? journal)
    {
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (success, reason) = workshop.PourIn(Jar, Liquid);
        Executed = success;
        var result = new CommandResult { Success = success, Reason = reason };
        Result = result;
        return result;
    }

    public override CommandResult Undo(Workshop workshop, ICommandJournal? journal)
    {
        if (!Executed) return new CommandResult { Success = false };
        // 撤销倒入：罐内液体倒回工作台存储 + 清除标记，恢复到 Execute 之前。
        // 原实现（继承自 Node 版，见《审计报告》§2.10）有两处错误：
        //   1) 未清标记就调用 PourOut —— PourOut 以「罐子已被操作」直接拒绝，撤销必然失败；
        //   2) 成功分支里额外 AddLiquid 一次 —— 同一份液体被重复返还。
        Jar.Unmark();
        var (success, reason, _) = workshop.PourOut(Jar);
        if (success) Jar.Unmark();
        return new CommandResult { Success = success, Reason = reason };
    }
}

public class ReshapeEmptyCommand : Command
{
    public Jar Jar { get; }
    public string TargetShape { get; }
    public string CostLiquid { get; }
    public string OriginalShape { get; }

    public ReshapeEmptyCommand(Jar jar, string targetShape, string costLiquid)
        : base(Config.Constants.OperationType.RESHAPE_EMPTY, new()
        {
            ["jarId"] = jar.Id,
            ["targetShape"] = targetShape,
            ["costLiquid"] = costLiquid
        })
    {
        Jar = jar;
        TargetShape = targetShape;
        CostLiquid = costLiquid;
        OriginalShape = jar.Shape;
    }

    public override CommandResult Execute(Workshop workshop, ICommandJournal? journal)
    {
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (success, reason) = workshop.ReshapeEmpty(Jar, TargetShape, CostLiquid);
        Executed = success;
        var result = new CommandResult { Success = success, Reason = reason };
        Result = result;
        return result;
    }

    public override CommandResult Undo(Workshop workshop, ICommandJournal? journal)
    {
        if (!Executed) return new CommandResult { Success = false };
        // 撤销改形
        Jar.Reshape(OriginalShape);
        // 返还成本液体：**必须看返回值**（2026-09-16 修）。
        // AddLiquid 在"液体被禁"或"液体仓已满"时返回 false，原实现丢弃返回值却仍报 Success=true
        // ⇒ 这 1 单位液体**静默蒸发**，且当时 CommandExecutor.Rollback 也只看异常 ⇒ 连痕迹都没有。
        // 注：实际不可达 —— 能付出成本就说明该液体未被禁（HasUsableLiquid 含禁令判定），
        // 而改形刚刚释放了 1 格容量；这里只是把不变量写成代码，别让"沉默的失败"重新长回来。
        var refunded = workshop.AddLiquid(CostLiquid, 1);
        Jar.Unmark();
        return refunded
            ? new CommandResult { Success = true }
            : new CommandResult
            {
                Success = false,
                Reason = "返还成本液体 " + CostLiquid + " 被工作台拒绝（液体被禁或液体仓已满）"
            };
    }
}

public class TransferCommand : Command
{
    public Jar Jar { get; }
    public Workshop From { get; }
    public Workshop To { get; }

    public TransferCommand(Jar jar, Workshop fromWorkshop, Workshop toWorkshop)
        : base(Config.Constants.OperationType.TRANSFER, new()
        {
            ["jarId"] = jar.Id,
            ["from"] = fromWorkshop.Name,
            ["to"] = toWorkshop.Name
        })
    {
        Jar = jar;
        From = fromWorkshop;
        To = toWorkshop;
    }

    public override CommandResult Execute(Workshop workshop, ICommandJournal? journal)
    {
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var out_ = From.TransferOut(Jar);
        if (out_ != null)
        {
            var success = To.TransferIn(Jar);
            if (success)
            {
                journal?.RecordTransfer(Jar);
                Executed = true;
                var ok = new CommandResult { Success = true };
                Result = ok;
                return ok;
            }
            // 从台放不下：罐子必须放回原工作台，不能悬空丢失
            From.TransferIn(Jar);
        }
        var failed = new CommandResult { Success = false };
        Result = failed;
        return failed;
    }

    public override CommandResult Undo(Workshop workshop, ICommandJournal? journal)
    {
        if (!Executed) return new CommandResult { Success = false };
        var out_ = To.TransferOut(Jar);
        if (out_ != null)
        {
            From.TransferIn(Jar);
            return new CommandResult { Success = true };
        }
        return new CommandResult { Success = false };
    }
}

public class PourOutCommand : Command
{
    public Jar Jar { get; }
    public Workshop OwnerWorkshop { get; }
    public string OriginalLiquid { get; }

    public PourOutCommand(Jar jar, Workshop workshop)
        : base(Config.Constants.OperationType.POUR_OUT, new()
        {
            ["jarId"] = jar.Id,
            ["liquid"] = jar.Liquid
        })
    {
        Jar = jar;
        OwnerWorkshop = workshop;
        OriginalLiquid = jar.Liquid!;
    }

    public override CommandResult Execute(Workshop workshop, ICommandJournal? journal)
    {
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var (success, reason, liquid) = workshop.PourOut(Jar);
        Executed = success;
        var result = new CommandResult { Success = success, Reason = reason };
        Result = result;
        return result;
    }

    public override CommandResult Undo(Workshop workshop, ICommandJournal? journal)
    {
        if (!Executed) return new CommandResult { Success = false };
        // Reverse: remove liquid from storage, restore jar's liquid
        if (OwnerWorkshop.RemoveLiquid(OriginalLiquid))
        {
            Jar.RestoreLiquid(OriginalLiquid);
            Jar.Unmark();
            return new CommandResult { Success = true };
        }
        return new CommandResult { Success = false };
    }
}

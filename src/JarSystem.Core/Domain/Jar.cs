using System;
using JarSystem.Core.Config;

namespace JarSystem.Core.Domain;

/// <summary>
/// 陶罐实体 - 纯领域对象。
/// 不变量：<see cref="Shape"/> 恒为 <see cref="Constants.SHAPES"/> 之一；<see cref="Liquid"/> 为 null 表示空罐；
/// 「一罐一操作」由 <see cref="IsMarked"/> 表达。状态只能经本类意图方法变更（阶段 1：消除公共 setter）。
/// </summary>
public class Jar
{
    /// <summary>全局唯一 ID（跨工作台转移/回滚据此判定归属）；由工作台经 <c>IIdGenerator</c> 分配，入台前为 null。</summary>
    public int? Id { get; internal set; }

    /// <summary>罐子形状。</summary>
    public string Shape { get; private set; }

    /// <summary>罐内液体；null 表示空罐。</summary>
    public string? Liquid { get; private set; }

    /// <summary>本次执行周期内是否已被操作（一罐一操作）。</summary>
    public bool IsMarked { get; private set; }

    public Jar(string shape, string? liquid = null, int? id = null)
    {
        if (!IsValidShape(shape))
            throw new ArgumentException($"无效形状: {shape}");
        Id = id;
        Shape = shape;
        Liquid = liquid;
        IsMarked = false;
    }

    /// <summary>形状合法性校验（唯一实现，构造与改形共用）。</summary>
    public static bool IsValidShape(string shape) => Array.Exists(Constants.SHAPES, s => s == shape);

    public bool IsEmpty() => Liquid is null;
    public bool IsFull() => Liquid is not null;

    public string GetDescription()
    {
        var idStr = Id.HasValue ? $"#{Id.Value}" : "";
        return Liquid is not null
            ? $"满罐({Shape},{Liquid}){idStr}"
            : $"空罐({Shape}){idStr}";
    }

    // ============================================================
    // 状态变更（唯一入口）
    // ============================================================

    /// <summary>标记为本次执行周期内已操作。</summary>
    public void Mark() { IsMarked = true; }

    /// <summary>清除操作标记。</summary>
    public void Unmark() { IsMarked = false; }

    /// <summary>倒入液体（仅允许空罐）。</summary>
    public void PourIn(string liquid)
    {
        if (!IsEmpty()) throw new InvalidOperationException("罐子不为空");
        Liquid = liquid;
    }

    /// <summary>清空罐内液体并返回它（返回 null 表示本来就是空罐）。</summary>
    public string? ClearLiquid()
    {
        var l = Liquid;
        Liquid = null;
        return l;
    }

    /// <summary>恢复罐内液体（撤销倒入/倒出时使用；null 表示恢复为空罐）。</summary>
    public void RestoreLiquid(string? liquid) { Liquid = liquid; }

    /// <summary>改形（形状必须合法）。</summary>
    public void Reshape(string newShape)
    {
        if (!IsValidShape(newShape))
            throw new ArgumentException($"无效形状: {newShape}");
        Shape = newShape;
    }

    /// <summary>深拷贝（快照隔离用；显式命名替代含义模糊的 ICloneable）。</summary>
    public Jar DeepClone() => new(Shape, Liquid, Id) { IsMarked = IsMarked };

    public override string ToString() => GetDescription();
}

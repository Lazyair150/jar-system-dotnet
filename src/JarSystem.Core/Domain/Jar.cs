using System;
using System.Collections.Generic;
using JarSystem.Core.Config;

namespace JarSystem.Core.Domain;

/// <summary>
/// 陶罐实体 - 纯领域对象。
/// 不变量：<see cref="Shape"/> 恒为**词表**之一（S2：词表随实例携带，默认内置词表 ⇒ 行为与重构前一致）；
/// <see cref="Liquid"/> 为 null 表示空罐；「一罐一操作」由 <see cref="IsMarked"/> 表达。
/// 状态只能经本类意图方法变更（阶段 1：消除公共 setter）。
///
/// **对象池契约（2026 新增）**：罐子是持续产出、持续消耗的资源，因此允许经 <see cref="JarPool"/>
/// 回收复用。回收后本对象的**全部读写都会抛异常**（`IsRecycled`），
/// 因为"回收后仍被别处引用"等于让两处代码持有了同一只罐子 —— 这正是对象池唯一真正的危险。
/// 由此得到一条可以逐字照做的规矩：**要回收一只罐子，必须能说清"世界上再没有第二处引用它"**。
/// </summary>
public class Jar
{
    // S2：形状合法性由**词表**裁决。罐子携带创建时的那一份，改形仍校验同一份。
    private readonly Vocabulary _vocabulary;

    private int? _id;
    private string _shape;
    private string? _liquid;
    private bool _marked;

    /// <summary>全局唯一 ID（跨工作台转移/回滚据此判定归属）；由工作台经 <c>IIdGenerator</c> 分配，入台前为 null。</summary>
    public int? Id { get { ThrowIfRecycled(); return _id; } internal set { ThrowIfRecycled(); _id = value; } }

    /// <summary>罐子形状。</summary>
    public string Shape { get { ThrowIfRecycled(); return _shape; } private set { _shape = value; } }

    /// <summary>罐内液体；null 表示空罐。</summary>
    public string? Liquid { get { ThrowIfRecycled(); return _liquid; } private set { _liquid = value; } }

    /// <summary>本次执行周期内是否已被操作（一罐一操作）。</summary>
    public bool IsMarked { get { ThrowIfRecycled(); return _marked; } private set { _marked = value; } }

    /// <summary>
    /// 是否已被归还对象池。归还后再读写本对象一律抛 <see cref="InvalidOperationException"/>。
    /// 由 <see cref="JarPool.Return"/> / <see cref="JarPool.Rent"/> 维护，业务代码不应设置它。
    /// </summary>
    public bool IsRecycled { get; private set; }

    /// <param name="vocabulary">形状/液体词表；null 表示使用内置默认词表。</param>
    public Jar(string shape, string? liquid = null, int? id = null, Vocabulary? vocabulary = null)
    {
        _vocabulary = vocabulary ?? Vocabulary.Default;
        if (!_vocabulary.IsValidShape(shape))
            throw new ArgumentException($"无效形状: {shape}");
        _id = id;
        _shape = shape;
        _liquid = liquid;
        _marked = false;
    }

    /// <summary>
    /// 形状合法性校验（**静态版 = 内置默认词表**，保留以兼容旧调用）。
    /// 实例级校验用 <see cref="IsValidShapeIn"/>（它认创建该罐子时用的词表）。
    /// </summary>
    public static bool IsValidShape(string shape) => Vocabulary.Default.IsValidShape(shape);

    /// <summary>本罐子所属词表下的形状合法性。</summary>
    public bool IsValidShapeIn(string shape)
    {
        ThrowIfRecycled();
        return _vocabulary.IsValidShape(shape);
    }

    /// <summary>
    /// Derived polarity of the internal liquid. This is computed from the authoritative
    /// liquid identity and is never stored as a second mutable state.
    /// </summary>
    public JarPolarity Polarity { get { ThrowIfRecycled(); return JarPolarityResolver.Resolve(_liquid); } }

    public bool IsEmpty() { ThrowIfRecycled(); return _liquid is null; }
    public bool IsFull() { ThrowIfRecycled(); return _liquid is not null; }

    public string GetDescription()
    {
        ThrowIfRecycled();
        return Describe();
    }

    private string Describe()
    {
        var idStr = _id.HasValue ? $"#{_id.Value}" : "";
        return _liquid is not null
            ? $"满罐({_shape},{_liquid}){idStr}"
            : $"空罐({_shape}){idStr}";
    }

    // ============================================================
    // 状态变更（唯一入口）
    //
    // ⚠️ 这些方法一律 internal，**不是** public。
    // 理由：罐子状态只能由本程序集内的规则路径改动 ——
    //   · Workshop 的意图方法（PourIn / PourOut / ReshapeEmpty / ReshapeFull）
    //   · Commands 的撤销（Unmark / RestoreLiquid / Reshape 回原形状）
    //   · 执行器按已判定路径施加步骤（SolverStepApplier / GreedyPlanBuilder）
    //   · 切换事件重排资源（ResourceTransferrer）
    // 若把入口开放给外部，任何人都能 jar.Reshape("圆罐") / jar.PourIn("酒")，
    // 直接绕过 Workshop 的禁令、基础标记、一罐一操作等全部校验。
    // 收为 internal 后，Workshop.Jars / Warehouse.Jars / WorkshopSnapshot.Jars
    // 这三个"只读视图"在**元素层**才真正只读。
    // ============================================================

    /// <summary>标记为本次执行周期内已操作。</summary>
    internal void Mark() { ThrowIfRecycled(); _marked = true; }

    /// <summary>
    /// 应用规则引擎已经批准的纯值转换结果。
    /// 此入口只负责保持实体不变量，不重新解释 POUR_IN/POUR_OUT/RESHAPE 等算子语义。
    /// </summary>
    internal void ApplyRuleTransition(string shape, string? liquid, bool isMarked)
    {
        ThrowIfRecycled();
        if (!_vocabulary.IsValidShape(shape))
            throw new ArgumentException($"无效形状: {shape}", nameof(shape));
        _shape = shape;
        _liquid = liquid;
        _marked = isMarked;
    }

    /// <summary>清除操作标记。</summary>
    internal void Unmark() { ThrowIfRecycled(); _marked = false; }

    /// <summary>倒入液体（仅允许空罐）。</summary>
    internal void PourIn(string liquid)
    {
        ThrowIfRecycled();
        if (_liquid is not null) throw new InvalidOperationException("罐子不为空");
        _liquid = liquid;
    }

    /// <summary>清空罐内液体并返回它（返回 null 表示本来就是空罐）。</summary>
    internal string? ClearLiquid()
    {
        ThrowIfRecycled();
        var l = _liquid;
        _liquid = null;
        return l;
    }

    /// <summary>恢复罐内液体（撤销倒入/倒出时使用；null 表示恢复为空罐）。</summary>
    internal void RestoreLiquid(string? liquid) { ThrowIfRecycled(); _liquid = liquid; }

    /// <summary>改形（形状必须在**本罐子所属词表**内合法）。</summary>
    internal void Reshape(string newShape)
    {
        ThrowIfRecycled();
        if (!_vocabulary.IsValidShape(newShape))
            throw new ArgumentException($"无效形状: {newShape}");
        _shape = newShape;
    }

    /// <summary>深拷贝（快照隔离用；显式命名替代含义模糊的 ICloneable）。</summary>
    public Jar DeepClone()
    {
        ThrowIfRecycled();
        return new Jar(_shape, _liquid, _id, _vocabulary) { IsMarked = _marked };
    }

    /// <summary>从同词表的状态副本恢复本对象，保留当前对象身份。</summary>
    internal void ValidateRestoreStateFrom(Jar snapshot)
    {
        ThrowIfRecycled();
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        snapshot.ThrowIfRecycled();
        if (!ReferenceEquals(_vocabulary, snapshot._vocabulary))
            throw new InvalidOperationException("不能从不同词表的罐子快照恢复状态");
    }

    /// <summary>从同词表的状态副本恢复本对象；调用前可先批量验证，避免恢复到一半才发现无效引用。</summary>
    internal void RestoreStateFrom(Jar snapshot)
    {
        ValidateRestoreStateFrom(snapshot);
        _id = snapshot._id;
        _shape = snapshot._shape;
        _liquid = snapshot._liquid;
        _marked = snapshot._marked;
    }

    public override string ToString() => GetDescription();

    // ============================================================
    // 对象池（只有 JarPool 调用；业务代码不得触碰）
    // ============================================================

    /// <summary>本罐子是否用同一份词表创建（池只能复用词表一致的罐子）。</summary>
    internal bool HasVocabulary(Vocabulary vocabulary) => ReferenceEquals(_vocabulary, vocabulary);

    /// <summary>
    /// 尝试把这只罐子重新用作一个新罐子：清状态、置形状/液体/Id、解除回收标记。
    /// 返回 false 表示形状在该词表内非法（池会跳过它并继续找下一条）。
    /// </summary>
    internal bool TryRecycle(string shape, string? liquid, int? id, Vocabulary vocabulary)
    {
        if (!HasVocabulary(vocabulary) || !vocabulary.IsValidShape(shape))
            return false;
        _id = id;
        _shape = shape;
        _liquid = liquid;
        _marked = false;
        IsRecycled = false;
        return true;
    }

    /// <summary>置为已回收并清空状态。返回 false 表示它已经在池里（重复归还是个错误）。</summary>
    internal bool BeginRecycle()
    {
        if (IsRecycled)
            return false;
        IsRecycled = true;
        _id = null;
        _shape = "";
        _liquid = null;
        _marked = false;
        return true;
    }

    private void ThrowIfRecycled()
    {
        if (IsRecycled)
            throw new InvalidOperationException(
                "罐子已被归还对象池，不能再读写 —— 此时它可能已被别处复用成另一只罐子；"
                + "回到调用点检查是否在 Return 之后仍持有该引用。");
    }
}

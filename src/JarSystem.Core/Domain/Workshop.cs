using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Config;

namespace JarSystem.Core.Domain;

/// <summary>
/// 工作台：规则容器（基础/附加标记、禁令）+ 资源容器（罐子、游离液体）+ 资源操作入口。
/// 资源状态只能经本类的意图方法变更，对外只**以只读接口**暴露（阶段 1：消除 §2.3 的跨模块直接改写）。
///
/// **"只读"的精确含义（2026-09-16 收紧）**：`Jars` / `LiquidStorage` 不只是"属性类型是只读接口"，
/// 底层容器外面**包了真正的只读包装**（<see cref="ReadOnlyCollection{T}"/> / <see cref="ReadOnlyDictionary{TKey,TValue}"/>）
/// ⇒ 连向下转型（`((List&lt;Jar&gt;)ws.Jars).Clear()`）也改不动，与 <c>Vocabulary</c> /
/// <see cref="WorkshopCapability"/> 同一手法。（原注释只写了"只暴露只读视图"，而当时交出去的
/// 就是私有 `List`/`Dictionary` 本身 —— 那句话比实际保证更强。）
/// </summary>
public class Workshop
{
    private readonly List<Jar> _jars = new();
    private readonly Dictionary<string, int> _liquidStorage = new();
    private readonly IIdGenerator _ids;

    // 只读包装（构造时建立一次，是**活视图**：底层容器后续变化照样反映）。
    private readonly IReadOnlyList<Jar> _jarsView;
    private readonly IReadOnlyDictionary<string, int> _liquidStorageView;

    // 能力参数（基础/附加标记、禁令）——S1：抽成不可变值对象 <see cref="WorkshopCapability"/>，
    // 于是「配置 → 工作台 → 快照 → 求解器」共用同一份，不再三处各存一份标记表。
    private WorkshopCapability _capability;

    // 规则读取入口（基础能力 + 活跃修饰器）。P3b 起"临时附加标记"也走这里
    // —— 它就是 GrantModifier 的一个实例，于是"临时放宽"只有一套机制。
    private RuleContext _rules;

    public string Name { get; }

    /// <summary>能力参数（标记/禁令）。整体更换请用 <see cref="ReplaceCapability"/>。</summary>
    public WorkshopCapability Capability => _capability;

    /// <summary>规则读取入口（基础能力 + 活跃修饰器）。默认空栈 ⇒ 与改造前的判定逐项相同。</summary>
    internal RuleContext Rules => _rules;

    /// <summary>基础标记（只读视图；等价于 <c>Capability.BaseMarks</c>）。</summary>
    public IReadOnlyList<string> BaseMarks => _capability.BaseMarks;

    /// <summary>附加标记（只读视图；转换规则查询会一并考虑临时附加标记）。</summary>
    public IReadOnlyList<string> AdditionalMarks => _capability.AdditionalMarks;

    /// <summary>禁令（只读视图；构造时已规范化为裸液体名）。</summary>
    public IReadOnlyList<string> Ban => _capability.Ban;

    /// <summary>罐位上限（变更只能经 <see cref="ResizeJarCapacity"/>）。</summary>
    public int MaxJars { get; private set; }

    /// <summary>液体上限（变更只能经 <see cref="ResizeLiquidCapacity"/>）。</summary>
    public int MaxLiquids { get; private set; }

    /// <summary>罐子只读视图（真只读包装）。增删只能经 AddJar / RemoveJar / TransferOut / TransferIn / ResizeJarCapacity。</summary>
    public IReadOnlyList<Jar> Jars => _jarsView;

    /// <summary>游离液体只读视图（真只读包装）。变更只能经 AddLiquid / RemoveLiquid / DrainLiquid / TryReceiveLiquid / ResizeLiquidCapacity。</summary>
    public IReadOnlyDictionary<string, int> LiquidStorage => _liquidStorageView;

    /// <param name="vocabulary">词表（S2）：只用于初始化液体账目的键与顺序；null 表示内置默认词表。</param>
    /// <param name="modifiers">
    /// 世界的修饰器栈（P3b）：传同一个实例即可让"一处入栈，处处生效"；
    /// null 表示本工作台自带一个空栈（独立使用/测试场景，行为与改造前相同）。
    /// </param>
    public Workshop(string name, List<string>? baseMarks = null, List<string>? additionalMarks = null,
        List<string>? ban = null, int? maxJars = null, int? maxLiquids = null, IIdGenerator? ids = null,
        Vocabulary? vocabulary = null, ModifierStack? modifiers = null)
    {
        Name = name;
        _capability = new WorkshopCapability(baseMarks, additionalMarks, ban);
        _rules = new RuleContext(_capability, modifiers ?? new ModifierStack());
        MaxJars = maxJars ?? Constants.DEFAULT_MAX_JARS;
        MaxLiquids = maxLiquids ?? Constants.DEFAULT_MAX_LIQUIDS;
        _ids = ids ?? DefaultIdGenerators.Jars;
        // 液体账目按**词表**建键（顺序 = 词表顺序 ⇒ 契约快照里 liquids 的键序）
        foreach (var l in (vocabulary ?? Vocabulary.Default).Liquids) _liquidStorage[l] = 0;
        // 只读包装放在最后：包装是活视图，但"先建好账目再交出去"读起来更直白
        _jarsView = _jars.AsReadOnly();
        _liquidStorageView = new ReadOnlyDictionary<string, int>(_liquidStorage);
    }

    /// <summary>
    /// **更换工作台的能力参数**（标记/禁令）——"工作台可以更换"的模型入口。
    ///
    /// 语义边界（重要，别和"切换主台"混）：
    /// <list type="bullet">
    /// <item>只换**规则参数**，**不动**罐子与游离液体；资源搬迁是 <c>JarSystem.SetMaster</c> 切换事件的职责；</item>
    /// <item>换完允许存在"形状不在新标记里"的罐子：标记约束的是**操作合法性**（<c>PourIn</c>/改形），
    /// 不是存储合法性（这与重构前一致）；</item>
    /// <item>进行中的 Case2 临时附加标记作用域不受影响（临时标记叠在新能力之上，作用域结束照常回收）。</item>
    /// </list>
    /// </summary>
    public void ReplaceCapability(WorkshopCapability capability)
    {
        _capability = capability;
        // 换的是**能力参数**，修饰器栈照旧（栈属世界，不属这一次替换）
        _rules = new RuleContext(capability, _rules.Stack);
    }

    /// <summary>绑定到世界的修饰器栈（宿主把外部构造的工作台纳入世界时调用）。</summary>
    internal void AttachRuleStack(ModifierStack stack) => _rules = new RuleContext(_capability, stack);

    // ============================================================
    // 规则查询
    // ============================================================

    public bool IsBaseMarked(string s) => _capability.IsBaseMarked(s);

    /// <summary>是否属于附加标记（含 Case2 期间的临时附加标记）。</summary>
    public bool IsAdditionalMarked(string s) => _rules.IsAdditionalMarked(s);

    public bool IsBanned(string l) => _rules.IsBanned(l);

    public List<string> GetPossibleTransforms(string s) => _rules.GetPossibleTransforms(s);

    public bool CanTransform(string f, string t) => GetPossibleTransforms(f).Contains(t);

    /// <summary>
    /// 在作用域内临时获得若干附加标记（切换事件 Case2 用：旧台临时获得新台基础形状）。
    /// 作用域结束（含异常）自动回收本次新增的临时标记；已是基础/附加标记的形状不会重复添加。
    /// </summary>
    public IDisposable WithTemporaryAdditionalMarks(IEnumerable<string> shapes)
    {
        var scopes = new List<IDisposable>();
        foreach (var s in shapes)
        {
            // 已是基础/附加标记，或已被别的 Grant 授予 ⇒ 不重复入栈（与改造前的判重语义一致）
            if (_capability.IsBaseMarked(s) || _capability.IsAdditionalMarked(s)) continue;
            if (_rules.Stack.GrantedShapes().Contains(s)) continue;
            scopes.Add(_rules.Stack.Push(new GrantModifier { Shape = s, Source = "切换事件" }));
        }
        return new ScopeGroup(scopes);
    }

    /// <summary>把多个作用域当一个用（内部用；<see cref="ModifierScope.Dispose"/> 幂等）。</summary>
    private sealed class ScopeGroup : IDisposable
    {
        private readonly List<IDisposable> _scopes;

        public ScopeGroup(List<IDisposable> scopes) => _scopes = scopes;

        public void Dispose()
        {
            foreach (var s in _scopes) s.Dispose();
        }
    }

    // ============================================================
    // 容量与液体
    // ============================================================

    public bool HasJarCapacity() => _jars.Count < MaxJars;

    /// <summary>是否有可用（未被禁且有库存）的游离液体。</summary>
    public bool HasUsableLiquid(string l) => !IsBanned(l) && _liquidStorage.GetValueOrDefault(l) >= 1;

    /// <summary>支付 1 单位改形成本。</summary>
    public bool TryPayReshapeCost(string l)
    {
        if (!HasUsableLiquid(l)) return false;
        _liquidStorage[l]--;
        return true;
    }

    public bool AddLiquid(string l, int n = 1)
    {
        if (IsBanned(l)) return false;
        var c = _liquidStorage.GetValueOrDefault(l);
        if (c + n > MaxLiquids) return false;
        _liquidStorage[l] = c + n;
        return true;
    }

    public bool RemoveLiquid(string l, int n = 1)
    {
        var c = _liquidStorage.GetValueOrDefault(l);
        if (c < n) return false;
        _liquidStorage[l] = c - n;
        return true;
    }

    /// <summary>容量感知地接收液体：返回实际接收量（被禁液体或容量已满一律拒收，不做部分接收之外的补偿）。</summary>
    public int TryReceiveLiquid(string l, int amount)
    {
        if (amount <= 0 || IsBanned(l)) return 0;
        var cur = _liquidStorage.GetValueOrDefault(l);
        var accepted = Math.Min(amount, Math.Max(0, MaxLiquids - cur));
        if (accepted <= 0) return 0;
        _liquidStorage[l] = cur + accepted;
        return accepted;
    }

    /// <summary>清空某种液体的游离库存并返回原数量。</summary>
    public int DrainLiquid(string l)
    {
        var cur = _liquidStorage.GetValueOrDefault(l);
        _liquidStorage[l] = 0;
        return cur;
    }

    /// <summary>调整罐位上限：超出的罐子被移除，返回被移除（丢弃）的数量。</summary>
    public int ResizeJarCapacity(int maxJars)
    {
        var discarded = 0;
        if (maxJars < _jars.Count)
        {
            discarded = _jars.Count - maxJars;
            _jars.RemoveRange(maxJars, discarded);
        }
        MaxJars = maxJars;
        return discarded;
    }

    /// <summary>调整液体上限：超出的部分被裁掉，返回每种液体被裁掉（丢弃）的数量。</summary>
    public Dictionary<string, int> ResizeLiquidCapacity(int maxLiquids)
    {
        var discarded = new Dictionary<string, int>();
        foreach (var l in _liquidStorage.Keys.ToList())
        {
            var cur = _liquidStorage[l];
            if (cur > maxLiquids)
            {
                discarded[l] = cur - maxLiquids;
                _liquidStorage[l] = maxLiquids;
            }
        }
        MaxLiquids = maxLiquids;
        return discarded;
    }

    // ============================================================
    // 罐子
    // ============================================================

    public bool AddJar(Jar j)
    {
        if (!HasJarCapacity()) return false;
        if (j.Id == null) j.Id = _ids.Next();
        _jars.Add(j);
        return true;
    }

    public Jar? RemoveJar(Jar j)
    {
        var i = _jars.IndexOf(j);
        if (i < 0) return null;
        _jars.RemoveAt(i);
        return j;
    }

    /// <summary>移出罐子（转移到别的工作台的第一步）。</summary>
    public Jar? TransferOut(Jar j) => RemoveJar(j);

    /// <summary>接收罐子。</summary>
    public bool TransferIn(Jar? j) => j != null && AddJar(j);

    /// <summary>清除全部罐子的操作标记（订单开始/结束、阶段切换时使用）。</summary>
    public void ClearMarks()
    {
        foreach (var j in _jars) j.Unmark();
    }

    // ============================================================
    // 资源操作（原子步骤；规则校验 + 记账 + 打标记都在这里）
    // ============================================================

    public (bool Success, string? Reason) PourIn(Jar j, string l)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (!j.IsEmpty()) return (false, "罐子不为空");
        if (!_capability.IsBaseMarked(j.Shape)) return (false, "形状" + j.Shape + "非本工作台基础标记");
        if (IsBanned(l)) return (false, "工作台禁" + l);
        if (_rules.SuppressionReason("POUR_IN", j.Shape, l) is { } suppressed) return (false, suppressed);
        if (!RemoveLiquid(l)) return (false, "液体" + l + "不足");
        j.PourIn(l); j.Mark(); return (true, null);
    }

    public (bool Success, string? Reason, string? Liquid) PourOut(Jar j)
    {
        if (j.IsMarked) return (false, "罐子已被操作", null);
        if (j.IsEmpty()) return (false, "罐子为空", null);
        var l = j.Liquid!;
        if (IsBanned(l)) return (false, "工作台禁" + l, null);
        if (_rules.SuppressionReason("POUR_OUT", j.Shape, l) is { } suppressed) return (false, suppressed, null);
        if (_liquidStorage.GetValueOrDefault(l) >= MaxLiquids) return (false, "液体" + l + "存储已满", null);
        j.ClearLiquid(); AddLiquid(l); j.Mark(); return (true, null, l);
    }

    public (bool Success, string? Reason) ReshapeEmpty(Jar j, string to, string cost)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (!j.IsEmpty()) return (false, "必须为空罐");
        if (!CanTransform(j.Shape, to)) return (false, "无法从" + j.Shape + "转化为" + to);
        if (_rules.SuppressionReason("RESHAPE", j.Shape, cost) is { } suppressed) return (false, suppressed);
        if (!TryPayReshapeCost(cost)) return (false, "无法支付液体" + cost);
        j.Reshape(to); j.Mark(); return (true, null);
    }

    public (bool Success, string? Reason) ReshapeFull(Jar j, string to)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (j.IsEmpty()) return (false, "必须为满罐");
        if (!CanTransform(j.Shape, to)) return (false, "无法从" + j.Shape + "转化为" + to);
        if (_rules.SuppressionReason("RESHAPE", j.Shape, j.Liquid) is { } suppressed) return (false, suppressed);
        var l = j.ClearLiquid();
        if (IsBanned(l!)) { j.PourIn(l!); return (false, "工作台禁" + l); }
        j.Reshape(to); j.Mark(); return (true, null);
    }

    // ============================================================
    // 快照（备忘录）
    // ============================================================

    public WorkshopSnapshot CreateSnapshot() => new()
    {
        Name = Name,
        MaxJars = MaxJars,
        MaxLiquids = MaxLiquids,
        // S1：能力参数不可变 ⇒ 直接共享同一份，不再拷三份列表
        Capability = _capability,
        // 2026-09-16：快照的两个集合也包真只读（只给接口类型时，`((List<Jar>)snap.Jars).Clear()`
        // 能抹掉这份回滚依据）。仍是**深拷贝**出来的独立副本，只是对外不可变。
        Jars = _jars.Select(j => j.DeepClone()).ToList().AsReadOnly(),
        Liquids = new ReadOnlyDictionary<string, int>(
            _liquidStorage.ToDictionary(kv => kv.Key, kv => kv.Value))
    };

    /// <summary>
    /// 从快照还原**完整**状态：罐子、液体账目、容量、能力参数（标记/禁令）。
    ///
    /// ⚠️ **2026-09-16 补齐（原为只还原罐子与液体）**：<see cref="CreateSnapshot"/> 是把容量与能力
    /// 一起存进快照的，而还原只回罐子与液体 ⇒ "快照—还原"不对称。唯一的调用方
    /// （<c>WorkshopSwitchEvent</c>）的契约写的是"失败时两个工作台已回滚到**执行前状态**"，
    /// 一旦切换过程中改过容量或标记，回滚后就会留下一个与快照不一致的工作台（三态分歧）。
    ///
    /// **为什么这次敢改**（原注释说"未擅自改变行为"，其顾虑正是行为变化）：
    /// 本仓库里切换流程**不可能**改这两样 —— 各分支只用 <c>WithTemporaryAdditionalMarks</c>
    /// （作用域自动回收，且临时标记不属于能力参数），而 <c>ReplaceCapability</c> /
    /// <c>Resize*Capacity</c> 在 <c>Orders/</c> 里**零调用点**。因此补齐还原在所有可达路径上
    /// **行为完全不变**（`REPLAY` 字节不受影响），只是把注释里的承诺变成真的。
    ///
    /// 注：`Name` 只读且从不变化，无需还原。
    /// </summary>
    public void RestoreFromSnapshot(WorkshopSnapshot s)
    {
        _jars.Clear();
        _jars.AddRange(s.Jars.Select(j => j.DeepClone()));
        _liquidStorage.Clear();
        foreach (var kv in s.Liquids) _liquidStorage[kv.Key] = kv.Value;
        // 与 CreateSnapshot 对称：容量与能力参数一并还原（快照持有的能力对象是不可变的 ⇒ 直接共享引用）
        MaxJars = s.MaxJars;
        MaxLiquids = s.MaxLiquids;
        _capability = s.Capability;
    }
}

/// <summary>
/// 工作台状态快照（备忘录）。
///
/// **隔离性**：<see cref="Workshop.CreateSnapshot"/> 对罐子逐个 DeepClone、液体账目另建字典，
/// 因此快照与源工作台互不影响；`init` 也锁住了属性赋值。
/// **且两个集合现在是"真只读"**（2026-09-16）：深拷贝之后还包了 `ReadOnlyCollection`/`ReadOnlyDictionary`
/// ⇒ 连向下转型（`((List&lt;Jar&gt;)snap.Jars).Clear()`）也抹不掉这份回滚依据。
/// （原注释写的是"严格说它不是不可变对象，Jars/Liquids 仍是可变的 List/Dictionary"——那已过期。）
///
/// 注意：**原实现只恢复罐子与液体**，规则/容量字段只作诊断信息保留（审计 §2.10 记录该不一致）。
/// ✅ **2026-09-16 已补齐**：<see cref="Workshop.RestoreFromSnapshot"/> 现在把容量与能力参数一并还原
/// （与 <see cref="Workshop.CreateSnapshot"/> 对称）。该改动在全部可达路径上**行为中性**——切换流程
/// 不会改容量/标记，论证见该方法的注释；因此不再有"快照与还原不一致"这回事。
/// </summary>
public class WorkshopSnapshot
{
    public string Name { get; init; } = "";
    public int MaxJars { get; init; }
    public int MaxLiquids { get; init; }

    /// <summary>能力参数（不可变 ⇒ 可与 <see cref="Workshop"/> 共享同一份，无需拷贝）。</summary>
    public WorkshopCapability Capability { get; init; } = new();

    /// <summary>基础标记（只读视图，等价于 <c>Capability.BaseMarks</c>）。</summary>
    public IReadOnlyList<string> BaseMarks => Capability.BaseMarks;

    /// <summary>附加标记（只读视图）。</summary>
    public IReadOnlyList<string> AdditionalMarks => Capability.AdditionalMarks;

    /// <summary>禁令（只读视图；已规范化为裸液体名）。</summary>
    public IReadOnlyList<string> Ban => Capability.Ban;

    /// <summary>罐子（对外只读；由 <see cref="Workshop.CreateSnapshot"/> 深拷贝后包真只读）。</summary>
    public IReadOnlyList<Jar> Jars { get; init; } = System.Array.Empty<Jar>();

    /// <summary>液体账目（对外只读；同上）。</summary>
    public IReadOnlyDictionary<string, int> Liquids { get; init; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
}

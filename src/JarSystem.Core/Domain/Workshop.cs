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

    /// <summary>旧版游离液体池的单液种上限。</summary>
    public int MaxLiquids { get; private set; }

    /// <summary>新订单暂存区的全液种合计容量，与旧版单液种上限分离。</summary>
    public int LiquidStagingCapacity { get; }

    /// <summary>罐子只读视图（真只读包装）。增删只能经 AddJar / RemoveJar / TransferOut / TransferIn / ResizeJarCapacity。</summary>
    public IReadOnlyList<Jar> Jars => _jarsView;

    /// <summary>游离液体只读视图（真只读包装）。变更只能经 AddLiquid / RemoveLiquid / DrainLiquid / TryReceiveLiquid / ResizeLiquidCapacity。</summary>
    public IReadOnlyDictionary<string, int> LiquidStorage => _liquidStorageView;

    /// <summary>是否持有任何罐子或非零游离液体。主台与从台的位置职责据此区分。</summary>
    public bool HasResources => _jars.Count > 0 || _liquidStorage.Values.Any(amount => amount > 0);

    /// <summary>捕获工作台动态库存；配置/规则修饰器由世界配置快照单独负责。</summary>
    public WorkshopRuntimeStateSnapshot CaptureRuntimeState() =>
        new(Name, MaxJars, MaxLiquids, LiquidStagingCapacity,
            BaseMarks, AdditionalMarks, Ban, _jars, _liquidStorage);

    /// <summary>
    /// 恢复工作台罐子实体与旧版游离液体池。先完整验证身份、词表、容量和配置，
    /// 验证通过后才替换集合；不改变工作台能力或规则修饰器。
    /// </summary>
    public void ValidateRuntimeStateSnapshot(WorkshopRuntimeStateSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Name, Name, StringComparison.Ordinal) ||
            snapshot.MaxJars != MaxJars || snapshot.MaxLiquids != MaxLiquids ||
            snapshot.LiquidStagingCapacity != LiquidStagingCapacity ||
            !snapshot.BaseMarks.SequenceEqual(BaseMarks, StringComparer.Ordinal) ||
            !snapshot.AdditionalMarks.SequenceEqual(AdditionalMarks, StringComparer.Ordinal) ||
            !snapshot.BannedLiquids.SequenceEqual(Ban, StringComparer.Ordinal))
            throw new InvalidOperationException("工作台运行时快照与当前工作台配置不一致。");
        if (snapshot.Jars.Count > MaxJars)
            throw new DomainValidationException("工作台快照中的罐子数量超过罐位容量。");

        var ids = new HashSet<int>();
        foreach (var jar in snapshot.Jars)
        {
            if (jar.IsRecycled || jar.Id is not > 0 || !ids.Add(jar.Id.Value))
                throw new DomainValidationException("工作台快照包含已回收、无效或重复身份的罐子。");
            if (!jar.IsValidShapeIn(jar.Shape))
                throw new DomainValidationException("工作台快照包含无效罐子形状。");
            if (jar.Liquid != null && !_liquidStorage.ContainsKey(jar.Liquid))
                throw new DomainValidationException("工作台快照包含未知罐内液体：" + jar.Liquid);
        }

        if (snapshot.LiquidStorage.Count != _liquidStorage.Count ||
            snapshot.LiquidStorage.Keys.Any(key => !_liquidStorage.ContainsKey(key)) ||
            snapshot.LiquidStorage.Any(item => item.Value < 0 || item.Value > MaxLiquids))
            throw new DomainValidationException("工作台快照的游离液体键或数量不符合当前容量配置。");

    }

    public void RestoreRuntimeState(WorkshopRuntimeStateSnapshot snapshot)
    {
        ValidateRuntimeStateSnapshot(snapshot);
        var restoredJars = snapshot.Jars.Select(jar => jar.DeepClone()).ToArray();
        _jars.Clear();
        _jars.AddRange(restoredJars);
        foreach (var key in _liquidStorage.Keys.ToArray())
            _liquidStorage[key] = snapshot.LiquidStorage[key];
    }

    /// <param name="vocabulary">词表（S2）：只用于初始化液体账目的键与顺序；null 表示内置默认词表。</param>
    /// <param name="modifiers">
    /// 世界的修饰器栈（P3b）：传同一个实例即可让"一处入栈，处处生效"；
    /// null 表示本工作台自带一个空栈（独立使用/测试场景，行为与改造前相同）。
    /// </param>
    public Workshop(string name, List<string>? baseMarks = null, List<string>? additionalMarks = null,
        List<string>? ban = null, int? maxJars = null, int? maxLiquids = null, IIdGenerator? ids = null,
        Vocabulary? vocabulary = null, ModifierStack? modifiers = null,
        int? liquidStagingCapacity = null)
    {
        Name = name;
        _capability = new WorkshopCapability(baseMarks, additionalMarks, ban);
        _rules = new RuleContext(_capability, modifiers ?? new ModifierStack());
        MaxJars = maxJars ?? Constants.DEFAULT_MAX_JARS;
        MaxLiquids = maxLiquids ?? Constants.DEFAULT_MAX_LIQUIDS;
        LiquidStagingCapacity = liquidStagingCapacity ?? MaxLiquids;
        if (LiquidStagingCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(liquidStagingCapacity), "工作台订单液体总暂存容量不能为负数");
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

    // ============================================================
    // 「动用液体」的唯一说法（原则4：规则 = 概念体的能力）
    //
    // 在这之前，"能不能用这份液体"是**每个调用方自己拼**的：
    //   - Workshop 内部：IsBanned + GetValueOrDefault + MaxLiquids，四处各写一遍；
    //   - WorkshopRulebook / GreedyPlanBuilder / 求解器：各自再拼一遍。
    // 于是"禁令"这个规则有四五种说法，改一处漏三处（B1 的"先倒空再禁"守卫就是这么来的）。
    //
    // 现在它收敛成**向工作台发请求、拿回应**：can 什么、为什么不行、用哪句话解释。
    // 调用方不再需要知道实现是"字典 + 上限 + 禁令表"，只需要读 return。
    // ============================================================

    /// <summary>本台的液体规则纯值（禁令 + 每液容量）。规则在 <see cref="LiquidRules"/>，库存仍在本类里。</summary>
    public LiquidRules Liquid => new(_capability, MaxLiquids);

    /// <summary>能不能<strong>取用</strong>这种液体（禁令门）。**禁令不是"存储上限"：禁液能存进来、能随罐子转移，
    /// 但倒不出、付不了改形成本、满罐自耗也不行**。</summary>
    public LiquidGrant CheckLiquid(string liquid) => Liquid.Check(liquid);

    /// <summary>能不能从池子里取出 amount 份（禁令 + 库存）。倒液、付改形成本都走这里。</summary>
    public LiquidGrant CanUseLiquid(string liquid, int amount) =>
        Liquid.CanPay(liquid, amount, _liquidStorage.GetValueOrDefault(liquid));

    /// <summary>能不能往池子里放 amount 份（**只问容量**：存储不受禁令约束）。仓库流入、倒空回池都走这里。</summary>
    public LiquidGrant CanStoreLiquid(string liquid, int amount) =>
        Liquid.CanStore(liquid, amount, _liquidStorage.GetValueOrDefault(liquid));

    /// <summary>是否有可用（未被禁且有库存）的游离液体。</summary>
    public bool HasUsableLiquid(string l) => CanUseLiquid(l, 1).Granted;

    /// <summary>支付 1 单位改形成本。</summary>
    public bool TryPayReshapeCost(string l)
    {
        if (!HasUsableLiquid(l)) return false;
        _liquidStorage[l]--;
        return true;
    }

    public bool AddLiquid(string l, int n = 1)
    {
        if (!CanStoreLiquid(l, n).Granted) return false;   /* 容量：唯一说法在上面（禁令只约束取用） */
        _liquidStorage[l] = _liquidStorage.GetValueOrDefault(l) + n;
        return true;
    }

    public bool RemoveLiquid(string l, int n = 1)
    {
        var c = _liquidStorage.GetValueOrDefault(l);
        if (c < n) return false;
        _liquidStorage[l] = c - n;
        return true;
    }

    /// <summary>容量感知地接收液体：返回实际接收量（容量已满则部分接收，装不下的由调用方丢弃并计数）。</summary>
    public int TryReceiveLiquid(string l, int amount)
    {
        // **只问容量，不问禁令**：接收语义是"能装多少装多少"，装不下的由调用方丢弃并计数
        // （规则.txt「中央仓库单向流动」：溢出直接丢弃，不回收）。用 CanStoreLiquid 会变成"全有或全无"，
        // 那会把部分接收这件事从世界里抹掉 —— 既有测试当场抓到了。
        // 禁令不在这一层拦：被禁液体也能流进来（那一格仍然存在），只是之后取不出来（见 CheckLiquid）。
        if (amount <= 0) return 0;
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

    /// <summary>
    /// 标记一只属于本工作台的罐子，表示它已在当前执行周期内完成一次操作或需求匹配。
    /// MATCH 也必须经过工作台入口，避免执行器绕过工作台的资源所有权边界直接修改 Jar。
    /// </summary>
    public (bool Success, string? Reason) MarkJar(Jar jar)
    {
        if (jar == null) throw new ArgumentNullException(nameof(jar));
        if (!_jars.Contains(jar)) return (false, "罐子不属于本工作台");
        if (jar.IsMarked) return (false, "罐子已被操作");
        jar.Mark();
        return (true, null);
    }

    /// <summary>清除一只属于本工作台的罐子的操作标记，不影响同台其他罐子。</summary>
    public (bool Success, string? Reason) ClearMark(Jar jar)
    {
        if (jar == null) throw new ArgumentNullException(nameof(jar));
        if (!_jars.Any(existing => ReferenceEquals(existing, jar)))
            return (false, "罐子不属于本工作台");
        jar.Unmark();
        return (true, null);
    }

    /// <summary>清除全部罐子的操作标记（订单开始/结束、阶段切换时使用）。</summary>
    public void ClearMarks()
    {
        foreach (var j in _jars) j.Unmark();
    }

    // ============================================================
    // 资源操作（原子步骤；规则校验 + 记账 + 打标记都在这里）
    // ============================================================

    public (bool Success, string? Reason) PourIn(Jar j, string l, OrderLiquidScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (j.IsMarked) return (false, "罐子已被操作");
        if (!j.IsEmpty()) return (false, "罐子不为空");
        if (!_capability.IsBaseMarked(j.Shape)) return (false, "形状" + j.Shape + "非本工作台基础标记");
        var touchIn = CheckLiquid(l);
        if (!touchIn.Granted) return (false, touchIn.Reason);
        if (_rules.SuppressionReason("POUR_IN", j.Shape, l) is { } suppressed) return (false, suppressed);
        if (!scope.TryConsume(l, 1)) return (false, "液体" + l + "不足或不可用");
        j.PourIn(l);
        j.Mark();
        return (true, null);
    }

    public (bool Success, string? Reason) PourIn(Jar j, string l)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (!j.IsEmpty()) return (false, "罐子不为空");
        if (!_capability.IsBaseMarked(j.Shape)) return (false, "形状" + j.Shape + "非本工作台基础标记");
        var touchIn = CheckLiquid(l);
        if (!touchIn.Granted) return (false, touchIn.Reason);
        if (_rules.SuppressionReason("POUR_IN", j.Shape, l) is { } suppressed) return (false, suppressed);
        if (!RemoveLiquid(l)) return (false, "液体" + l + "不足");
        j.PourIn(l); j.Mark(); return (true, null);
    }

    public (bool Success, string? Reason, string? Liquid) PourOut(Jar j, OrderLiquidScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (j.IsMarked) return (false, "罐子已被操作", null);
        if (j.IsEmpty()) return (false, "罐子为空", null);
        var liquid = j.Liquid!;
        var touchOut = CheckLiquid(liquid);
        if (!touchOut.Granted) return (false, touchOut.Reason, null);
        if (_rules.SuppressionReason("POUR_OUT", j.Shape, liquid) is { } suppressed)
            return (false, suppressed, null);
        if (!scope.TryReceiveOperation(liquid, 1, out var receive))
            return (false, receive.Reason, null);

        j.ClearLiquid();
        j.Mark();
        return (true, null, liquid);
    }

    public (bool Success, string? Reason, string? Liquid) PourOut(Jar j)
    {
        if (j.IsMarked) return (false, "罐子已被操作", null);
        if (j.IsEmpty()) return (false, "罐子为空", null);
        var l = j.Liquid!;
        var touchOut = CheckLiquid(l);
        if (!touchOut.Granted) return (false, touchOut.Reason, null);
        if (_rules.SuppressionReason("POUR_OUT", j.Shape, l) is { } suppressed) return (false, suppressed, null);
        var storeOut = CanStoreLiquid(l, 1);
        if (!storeOut.Granted) return (false, storeOut.Reason, null);
        j.ClearLiquid(); AddLiquid(l); j.Mark(); return (true, null, l);
    }

    // ============================================================
    // 「这只罐子现在能改形成什么」的唯一说法（原则4 的第二笔欠账）
    //
    // 在这之前，这个问题的答案散在三处：
    //   - 方向：WorkshopCapability.CanTransform（只答"形状上通不通"）；
    //   - 成本：ReshapeEmpty 里 TryPayReshapeCost（空罐要付 1 份池中液体）；
    //   - 自耗：ReshapeFull 里 ClearLiquid + CheckLiquid（满罐吃罐内液体，读法 B：也算"使用"）。
    // 于是"能不能改形"没有单一入口：规则表、求解器、页面各自拼一遍，中文文案各写一份。
    //
    // 现在它是一次**请求-回应**：问工作台"这只罐子改成 to 行不行"，拿结构化回应。
    // 规则本身现在住在 ReshapeRules（纯值），活工作台与规则表问同一份实现。
    // 这里仍是**只读询问**：不改世界，只回答"行不行、为什么不行"；动手请走 TryReshape。
    // ============================================================

    /// <summary>
    /// 这只罐子此刻能不能改形成 <paramref name="to"/>。**不改世界**。
    ///
    /// 空罐：形状方向合法 + 池里至少有一份可取用的改形成本液体（具体付哪一种由执行者定，
    /// 所以"成本被抑制"要到执行时才判，这里只回答"有没有得付"）。
    /// 满罐：形状方向合法 + 罐内液体**可取用**（禁令挡自耗 —— 读法 B：自耗也算使用）。
    /// </summary>
    public ReshapeGrant CanReshapeTo(Jar jar, string to)
    {
        if (jar.IsMarked) return ReshapeGrant.No("marked", "罐子已被操作");
        if (!CanTransform(jar.Shape, to))
            return ReshapeGrant.No("direction", "无法从" + jar.Shape + "转化为" + to);

        // 规则都在 ReshapeRules 里（与规则表同一份实现），这里只负责"按执行者的顺序问一遍"。
        var reshape = new ReshapeRules(_capability, Liquid);

        if (jar.IsEmpty()) return reshape.PayCost(null, _liquidStorage);

        // 顺序与 ReshapeFull 一致：先看抑制，再看禁令（这样同一个"不行"给出同一句话）。
        if (_rules.SuppressionReason("RESHAPE", jar.Shape, jar.Liquid) is { } suppressed)
            return ReshapeGrant.No("suppressed", suppressed);
        return reshape.SelfConsume(jar.Liquid);
    }

    /// <summary>
    /// 这只罐子此刻能改形成的**全部形状**（方向 ∩ 标记 ∩ 成本 ∩ 禁令）。
    ///
    /// 候选来自 <see cref="WorkshopCapability.GetPossibleTransforms"/> —— 方向的唯一来源，
    /// 所以"能改形成什么"不会与规则表说的那两个方向打架。
    /// </summary>
    /// <summary>订单范围内的只读改形判定；成本库存来自指定订单子账。</summary>
    public ReshapeGrant CanReshapeTo(Jar jar, string to, OrderLiquidScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (jar.IsMarked) return ReshapeGrant.No("marked", "罐子已被操作");
        if (!CanTransform(jar.Shape, to))
            return ReshapeGrant.No("direction", "无法从" + jar.Shape + "转化为" + to);

        var reshape = new ReshapeRules(_capability, Liquid);
        if (jar.IsEmpty())
        {
            foreach (var pair in scope.Balance)
                if (!IsBanned(pair.Key) && scope.CanConsume(pair.Key, 1).Granted)
                    return ReshapeGrant.Yes();
            return ReshapeGrant.No("no-cost-liquid", "没有可支付的改形成本液体");
        }

        if (_rules.SuppressionReason("RESHAPE", jar.Shape, jar.Liquid) is { } suppressed)
            return ReshapeGrant.No("suppressed", suppressed);
        return reshape.SelfConsume(jar.Liquid);
    }

    public IReadOnlyList<string> ReshapeTargets(Jar jar)
    {
        var result = new List<string>();
        foreach (var to in GetPossibleTransforms(jar.Shape))
            if (CanReshapeTo(jar, to).Granted) result.Add(to);
        return result;
    }

    // ============================================================
    // 改形的**唯一入口**（判定 + 执行合为一处）
    //
    // 之前是两扇门：ReshapeEmpty(罐, 目标, 成本) 与 ReshapeFull(罐, 目标)。
    // 调用方必须自己先判断"这罐子空不空"，判错了就得到一个看起来像规则拒绝的错误。
    // 现在是一扇门：TryReshape(罐, 目标, mode, 成本) —— mode = Auto 时按罐子此刻状态自选，
    // 需要严格区分的调用方（命令、换台、执行器步骤）显式传 Empty / Full，语义与从前逐字相同。
    //
    // 与 CanReshapeTo 的分工：CanReshapeTo 是**只读询问**（不改世界、给结构化理由），
    // TryReshape 是**动手**。两者共用同一套规则（方向 / 标记 / 禁令 / 成本），
    // 由 WorkshopReshapeTests 的对拍逐例保证它们不会说法不一。
    // ============================================================

    /// <summary>
    /// 把 <paramref name="j"/> 改形成 <paramref name="to"/>。
    ///
    /// <para><b>mode</b>：<see cref="ReshapeMode.Auto"/> 按罐子此刻状态自选空/满两条路；
    /// 传 <see cref="ReshapeMode.Empty"/> / <see cref="ReshapeMode.Full"/> 则严格要求那一种
    /// （用于命令、换台等本来就已判定过状态的路径，语义与旧的 ReshapeEmpty/ReshapeFull 一致）。</para>
    ///
    /// <para><b>costLiquid</b>：空罐改形的成本液体。为 null 时由工作台自己挑一份**可取用**的
    /// （顺序按液池键序，确定性）；显式给了但那一种付不了 ⇒ **直接失败，不悄悄换一份**
    /// —— 换一份会让"我让你付蜜、它却吃了酒"这种事静默发生。</para>
    /// </summary>
    public (bool Success, string? Reason) TryReshape(
        Jar j, string to, ReshapeMode mode = ReshapeMode.Auto, string? costLiquid = null)
    {
        if (mode == ReshapeMode.Auto) mode = j.IsEmpty() ? ReshapeMode.Empty : ReshapeMode.Full;

        if (mode == ReshapeMode.Full) return ReshapeFullCore(j, to);

        var cost = costLiquid ?? FirstUsableCostLiquid();
        if (cost == null) return (false, "没有可支付的改形成本液体");
        return ReshapeEmptyCore(j, to, cost);
    }

    /// <summary>订单范围内执行改形；空罐成本只从当前订单子账扣除。</summary>
    public (bool Success, string? Reason) TryReshape(
        Jar j, string to, OrderLiquidScope scope,
        ReshapeMode mode = ReshapeMode.Auto, string? costLiquid = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (mode == ReshapeMode.Auto) mode = j.IsEmpty() ? ReshapeMode.Empty : ReshapeMode.Full;

        if (mode == ReshapeMode.Full)
        {
            if (j.IsMarked) return (false, "罐子已被操作");
            if (j.IsEmpty()) return (false, "必须为满罐");
            if (!CanTransform(j.Shape, to)) return (false, "无法从" + j.Shape + "转化为" + to);
            if (_rules.SuppressionReason("RESHAPE", j.Shape, j.Liquid) is { } suppressed)
                return (false, suppressed);
            var liquid = j.Liquid!;
            var touch = CheckLiquid(liquid);
            if (!touch.Granted) return (false, touch.Reason);
            j.ClearLiquid();
            j.Reshape(to);
            j.Mark();
            return (true, null);
        }

        if (j.IsMarked) return (false, "罐子已被操作");
        if (!j.IsEmpty()) return (false, "必须为空罐");
        if (!CanTransform(j.Shape, to)) return (false, "无法从" + j.Shape + "转化为" + to);

        var cost = costLiquid ?? scope.Balance
            .Where(pair => scope.CanConsume(pair.Key, 1).Granted && !IsBanned(pair.Key))
            .Select(pair => pair.Key)
            .FirstOrDefault();
        if (cost == null) return (false, "没有可支付的改形成本液体");
        if (_rules.SuppressionReason("RESHAPE", j.Shape, cost) is { } costSuppressed)
            return (false, costSuppressed);
        var costPermission = CheckLiquid(cost);
        if (!costPermission.Granted) return (false, costPermission.Reason);
        if (!scope.TryConsume(cost, 1)) return (false, "无法支付液体" + cost);
        j.Reshape(to);
        j.Mark();
        return (true, null);
    }

    /// <summary>按液池键序找第一份"取得到"的液体（空罐改形的成本）。</summary>
    private string? FirstUsableCostLiquid()
    {
        foreach (var l in _liquidStorage.Keys)
            if (HasUsableLiquid(l)) return l;
        return null;
    }

    private (bool Success, string? Reason) ReshapeEmptyCore(Jar j, string to, string cost)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (!j.IsEmpty()) return (false, "必须为空罐");
        if (!CanTransform(j.Shape, to)) return (false, "无法从" + j.Shape + "转化为" + to);
        if (_rules.SuppressionReason("RESHAPE", j.Shape, cost) is { } suppressed) return (false, suppressed);
        if (!TryPayReshapeCost(cost)) return (false, "无法支付液体" + cost);
        j.Reshape(to); j.Mark(); return (true, null);
    }

    private (bool Success, string? Reason) ReshapeFullCore(Jar j, string to)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (j.IsEmpty()) return (false, "必须为满罐");
        if (!CanTransform(j.Shape, to)) return (false, "无法从" + j.Shape + "转化为" + to);
        if (_rules.SuppressionReason("RESHAPE", j.Shape, j.Liquid) is { } suppressed) return (false, suppressed);
        var l = j.ClearLiquid();
        var touch = CheckLiquid(l!);
        if (!touch.Granted) { j.PourIn(l!); return (false, touch.Reason); }
        j.Reshape(to); j.Mark(); return (true, null);
    }

    // ============================================================
    // 快照（备忘录）
    // ============================================================

    public WorkshopSnapshot CreateSnapshot()
    {
        // 状态副本用于恢复；原始引用用于回滚时保持实体身份不变。
        var jarStates = _jars.Select(j => j.DeepClone()).ToList();
        return new WorkshopSnapshot
        {
            Name = Name,
            MaxJars = MaxJars,
            MaxLiquids = MaxLiquids,
            Capability = _capability,
            Jars = jarStates.AsReadOnly(),
            OriginalJars = _jars.ToList().AsReadOnly(),
            Liquids = new ReadOnlyDictionary<string, int>(
                _liquidStorage.ToDictionary(kv => kv.Key, kv => kv.Value))
        };
    }

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
        if (s == null) throw new ArgumentNullException(nameof(s));

        // 先完整准备恢复数据。任何快照结构错误、回收对象或词表不匹配都必须在改动工作台前失败。
        List<Jar> restoredJars;
        if (s.OriginalJars.Count == s.Jars.Count)
        {
            // 先验证所有原实体与状态副本；RestoreStateFrom 的赋值阶段随后不会再遇到可预期的验证错误。
            for (var i = 0; i < s.OriginalJars.Count; i++)
                s.OriginalJars[i].ValidateRestoreStateFrom(s.Jars[i]);

            restoredJars = s.OriginalJars.ToList();
        }
        else if (s.OriginalJars.Count == 0)
        {
            // 兼容外部手工构造的旧式快照：先完成所有克隆，再触碰当前工作台。
            restoredJars = s.Jars.Select(j => j.DeepClone()).ToList();
        }
        else
        {
            throw new InvalidOperationException("工作台快照中的罐子引用与状态数量不一致");
        }

        var restoredLiquids = s.Liquids.ToDictionary(kv => kv.Key, kv => kv.Value);

        // 所有可能失败的准备/验证已完成。以下只执行内存字段赋值与容器替换。
        if (s.OriginalJars.Count == s.Jars.Count)
        {
            for (var i = 0; i < s.OriginalJars.Count; i++)
                restoredJars[i].RestoreStateFrom(s.Jars[i]);
        }

        _jars.Clear();
        _jars.AddRange(restoredJars);
        _liquidStorage.Clear();
        foreach (var kv in restoredLiquids) _liquidStorage[kv.Key] = kv.Value;
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

    /// <summary>快照拍摄时工作台持有的原始实体引用，仅供工作台内部恢复身份使用。</summary>
    internal IReadOnlyList<Jar> OriginalJars { get; init; } = System.Array.Empty<Jar>();

    /// <summary>液体账目（对外只读；同上）。</summary>
    public IReadOnlyDictionary<string, int> Liquids { get; init; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
}


/// <summary>工作台动态库存的脱离式快照；不包含规则修饰器或岗位身份。</summary>
public sealed class WorkshopRuntimeStateSnapshot
{
    private readonly Jar[] _jars;

    internal WorkshopRuntimeStateSnapshot(
        string name,
        int maxJars,
        int maxLiquids,
        int liquidStagingCapacity,
        IEnumerable<string> baseMarks,
        IEnumerable<string> additionalMarks,
        IEnumerable<string> bannedLiquids,
        IEnumerable<Jar> jars,
        IReadOnlyDictionary<string, int> liquidStorage)
    {
        Name = name;
        MaxJars = maxJars;
        MaxLiquids = maxLiquids;
        LiquidStagingCapacity = liquidStagingCapacity;
        BaseMarks = Array.AsReadOnly(baseMarks.ToArray());
        AdditionalMarks = Array.AsReadOnly(additionalMarks.ToArray());
        BannedLiquids = Array.AsReadOnly(bannedLiquids.ToArray());
        _jars = jars.Select(jar => jar.DeepClone()).ToArray();
        LiquidStorage = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(liquidStorage, StringComparer.Ordinal));
    }

    public string Name { get; }
    public int MaxJars { get; }
    public int MaxLiquids { get; }
    public int LiquidStagingCapacity { get; }
    public IReadOnlyList<string> BaseMarks { get; }
    public IReadOnlyList<string> AdditionalMarks { get; }
    public IReadOnlyList<string> BannedLiquids { get; }
    /// <summary>每次返回独立罐子副本，防止调用方通过可变 Jar 修改检查点内部状态。</summary>
    public IReadOnlyList<Jar> Jars =>
        Array.AsReadOnly(_jars.Select(jar => jar.DeepClone()).ToArray());
    public IReadOnlyDictionary<string, int> LiquidStorage { get; }
}

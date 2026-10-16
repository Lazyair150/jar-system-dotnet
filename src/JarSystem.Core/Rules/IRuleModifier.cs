namespace JarSystem.Core.Rules;

/// <summary>修饰器的种类（取值不是契约：只在本程序集内使用，不进任何对外 JSON）。</summary>
public enum ModifierKind
{
    /// <summary>区间内禁用某个操作/形状/液体（"临时禁令"）。</summary>
    Suppress,

    /// <summary>区间内新增前置条件（P3b-2 接守卫条件）。</summary>
    Require,

    /// <summary>区间内放宽限制（既有 <c>WithTemporaryAdditionalMarks</c> 的一般化：额外可改形目标）。</summary>
    Grant,

    /// <summary>区间内改变步间隔（"这段日子重铸更慢"）。</summary>
    Retime,

    /// <summary>区间内改变强度参数（产出/丢弃/容量等；声明用，暂无消费者）。</summary>
    Intensity
}

/// <summary>
/// 规则修饰器：在时间区间内对基础法则做叠加或抑制（不改基础法则本身）。
///
/// 为什么必须是"叠加层"而不是"改法则"：基础法则是世界的不变量（设计文档 §2 的 L0），
/// 而"这段时间不许灌注"是事件的产物 —— 事件只应改世界与它的临时规则，不该改写世界是什么。
/// 于是所有修饰器都必须声明区间与来源，且必定回收。
/// </summary>
public interface IRuleModifier
{
    /// <summary>名字（诊断与日志用；不是契约）。</summary>
    string Name { get; }

    /// <summary>种类。</summary>
    ModifierKind Kind { get; }

    /// <summary>来源（事件 / 关卡 / 角色 / 器物）—— 冲突解析与审计要用。</summary>
    string Source { get; }

    /// <summary>优先级（越大越优先）；同优先级且语义冲突则显式报错（见 <see cref="ModifierStack"/>）。</summary>
    int Priority { get; }

    /// <summary>生效区间（tick，闭区间）；<c>To</c> 为 null 表示"直到被撤销"。</summary>
    (int From, int? To)? Window { get; }
}

/// <summary>抑制：区间内禁用某个操作 / 形状 / 液体（三者都可为 null 表示不限）。</summary>
public sealed class SuppressModifier : IRuleModifier
{
    public string Name { get; init; } = "suppress";
    public string Source { get; init; } = "事件";
    public int Priority { get; init; }
    public (int From, int? To)? Window { get; init; }
    public ModifierKind Kind => ModifierKind.Suppress;

    /// <summary>操作名（与执行日志同一套词汇：POUR_IN / POUR_OUT / RESHAPE / TRANSFER…）；null 表示不限。</summary>
    public string? Operation { get; init; }

    /// <summary>形状（null 表示不限）。</summary>
    public string? Shape { get; init; }

    /// <summary>液体（null 表示不限）。</summary>
    public string? Liquid { get; init; }

    /// <summary>世界语言的拒绝原因（会直接显示给玩家）。</summary>
    public string Reason { get; init; } = "此刻不得进行该操作";
}

/// <summary>改拍：区间内把某类过程的步间隔改成给定值。</summary>
public sealed class RetimeModifier : IRuleModifier
{
    public string Name { get; init; } = "retime";
    public string Source { get; init; } = "事件";
    public int Priority { get; init; }
    public (int From, int? To)? Window { get; init; }
    public ModifierKind Kind => ModifierKind.Retime;

    /// <summary>过程种类（null 表示所有过程）。</summary>
    public string? ProcessKind { get; init; }

    /// <summary>目标步间隔（刻/步；不大于 0 视为 1）。</summary>
    public int TicksPerStep { get; init; } = 1;
}

/// <summary>
/// 放宽：区间内把一个形状视作"附加标记"（于是它成为可改形目标）。
/// 这就是既有 <c>Workshop.WithTemporaryAdditionalMarks</c> 的一般化 —— 那条路径现在也走本机制。
/// </summary>
public sealed class GrantModifier : IRuleModifier
{
    public string Name { get; init; } = "grant";
    public string Source { get; init; } = "器物";
    public int Priority { get; init; }
    public (int From, int? To)? Window { get; init; }
    public ModifierKind Kind => ModifierKind.Grant;
    public string Shape { get; init; } = "";
}

/// <summary>
/// **新增前置条件**：内容侧给出一个条件（谓词），过程在**每个单位执行前**问它一次；
/// 返回世界语言的原因表示"此刻不成立"（⇒ 阻断等待），返回 null 表示成立。
/// </summary>
public sealed class RequireModifier : IRuleModifier
{
    public string Name { get; init; } = "require";
    public string Source { get; init; } = "关卡";
    public int Priority { get; init; }
    public (int From, int? To)? Window { get; init; }
    public ModifierKind Kind => ModifierKind.Require;

    /// <summary>条件的文字说明（诊断用）。</summary>
    public string Text { get; init; } = "";

    /// <summary>条件本身：返回"不成立的原因"（世界语言）或 null（成立）。</summary>
    public Func<string?>? Violation { get; init; }
}

/// <summary>
/// **强度参数**：区间内把某个目标的强度按**千分比**折算（整数运算 ⇒ 确定性）。
/// 目前接在"液体每 tick 产出速率"上；产生式/容量/丢弃等后续可逐个接。
/// </summary>
public sealed class IntensityModifier : IRuleModifier
{
    public string Name { get; init; } = "intensity";
    public string Source { get; init; } = "事件";
    public int Priority { get; init; }
    public (int From, int? To)? Window { get; init; }
    public ModifierKind Kind => ModifierKind.Intensity;

    /// <summary>目标（例：<c>"liquid:酒"</c>）。</summary>
    public string Target { get; init; } = "";

    /// <summary>折算比例（千分比；1000 = 不变，500 = 减半，0 = 停摆）。用整数是为了确定性。</summary>
    public int FactorPerMille { get; init; } = 1000;
}

namespace JarSystem.Core.Process;

/// <summary>
/// **步间隔分配器**：决定"过程的一步占用几个 tick"。
///
/// 默认值来自 2026-11 的回合语义修正：**一次手动推进就是一个完整回合**，
/// 该回合内所有到期的过程单位都完成。因此默认 <see cref="DefaultTicksPerStep"/> = 1。
/// 需要跨多回合的慢操作仍可显式配置，或由 <c>Retime</c> 修饰器临时覆盖。
///
/// P2 的现状（诚实声明）：间隔会被**计算并写进过程日志**，但宿主的同步执行路径（drain）
/// 仍在同一 tick 内把计划走完 —— 真正的"每 tick 至多一步"要等 P2b 把过程改为 tick 驱动
/// （那一步会改 <c>ExecuteOrder</c> 的对外语义，需单独裁决）。
/// </summary>
public sealed class StepAllocator
{
    /// <summary>默认步间隔（刻/步）。</summary>
    public const int DefaultTicksPerStep = 1;

    private readonly Dictionary<string, int> _perKind = new();

    /// <param name="ticksPerStep">全局默认间隔（≤0 视为 1，避免"零刻一步"这种无意义配置）。</param>
    public StepAllocator(int ticksPerStep = DefaultTicksPerStep) => TicksPerStep = Normalize(ticksPerStep);

    /// <summary>全局默认间隔（刻/步）。</summary>
    public int TicksPerStep { get; set; } = DefaultTicksPerStep;

    /// <summary>按过程种类覆盖（例：迁徙比执行更慢）。</summary>
    public void SetForKind(string kind, int ticksPerStep) => _perKind[kind] = Normalize(ticksPerStep);

    /// <summary>取某类过程的步间隔。</summary>
    public int For(string kind) => _perKind.TryGetValue(kind, out var v) ? v : Normalize(TicksPerStep);

    private static int Normalize(int v) => v <= 0 ? 1 : v;
}

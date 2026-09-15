using System.Collections.Generic;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Executor;
using JarSystem.Core.Orders;
using JarSystem.Core.Production;
using JarSystem.Core.Services;

namespace JarSystem.Core;

/// <summary>
/// 陶罐系统门面（唯一对外入口）。
///
/// 阶段 2 拆分后职责只剩「持有世界状态与协作者 + 转发用例」：
/// <see cref="WorkshopService"/>（工作台与主从切换）、<see cref="OrderService"/>（订单、可行性、执行）、
/// <see cref="ProductionService"/>（时间推进与产出）、<see cref="StateSerializer"/>（对外状态快照）、
/// <see cref="Bootstrap.SystemBootstrapper"/>（默认世界装配）。
///
/// **批 E 状态收口**：集合成员对外只读（`IReadOnlyList` / `IReadOnlyDictionary`），
/// 变更一律经**命名的意图方法**（`AddWorkshop`/`ClearWorkshops`/`SetMasterIndex`…）或服务层（程序集内 `internal` 可变视图）。
/// 这样"改世界"这件事在代码里可被 grep，外部也无法再用 `Workshops.Clear()` + 遗留索引把系统搞成非法状态。
/// </summary>
public class JarSystem
{
    private readonly WorkshopService _workshopService;
    private readonly OrderService _orderService;
    private readonly ProductionService _productionService;

    private readonly List<Workshop> _workshops = new();
    private readonly List<Order> _orders = new();
    private readonly List<Dictionary<string, object?>> _eventHistory = new();
    private readonly Dictionary<string, int> _liquidProductionRate;

    // ---- 对外只读视图（契约：内容与重构前逐项一致） ----
    public IReadOnlyList<Workshop> Workshops => _workshops;
    public IReadOnlyList<Order> Orders => _orders;
    public IReadOnlyList<Dictionary<string, object?>> EventHistory => _eventHistory;

    /// <summary>每 tick 的液体产出速率（酒/蜜）。**不参与 JSON 契约**；修改请用 <see cref="SetLiquidProductionRate"/>。</summary>
    public IReadOnlyDictionary<string, int> LiquidProductionRate => _liquidProductionRate;

    // ---- 程序集内可变视图（仅供 Services/Bootstrap 使用；外部看不到） ----
    internal List<Workshop> WorkshopList => _workshops;
    internal List<Order> OrderList => _orders;
    internal List<Dictionary<string, object?>> EventHistoryList => _eventHistory;

    /// <summary>产出模块。整体替换请用 <see cref="ResetProducerModule"/>。</summary>
    public ProducerModule ProducerModule { get; private set; }

    /// <summary>可行性检查器（注入点：构造后不可整体替换；其 <c>Solver</c> 可经构造注入替身）。</summary>
    public FeasibilityChecker Checker { get; } = new();

    /// <summary>订单执行器（注入点：构造后不可整体替换；<c>SelectionStrategy</c> 仍可设置）。</summary>
    public OrderExecutor Executor { get; }

    /// <summary>当前执行中的订单事件（由执行流程写入）。</summary>
    public OrderEvent? CurrentEvent { get; internal set; }

    /// <summary>主台下标。请用 <see cref="SetMasterIndex"/>（本属性不触发切换事件）。</summary>
    public int? CurrentMaster { get; internal set; }

    /// <summary>从台下标。请用 <see cref="SetSlaveIndex"/>。</summary>
    public int? CurrentSlave { get; internal set; }

    /// <summary>主台流入参数（每次流入的罐子数与液体量）。修改请用 <see cref="SetFlowSettings"/>。</summary>
    public (int JarCount, int LiquidAmount) FlowSettings { get; private set; }

    /// <summary>自动可行性轮询开关。修改请用 <see cref="SetAutoCheck"/>。</summary>
    public bool AutoCheckEnabled { get; private set; } = false;

    /// <summary>
    /// 可观测性出口（阶段 3-A 接线）：只用于记录"原本会被静默丢弃"的诊断信息，
    /// 不替代也不改变任何对外契约（详见 <see cref="ISystemLog"/>）。
    /// 宿主可在构造时注入自己的实现；默认写控制台。
    /// </summary>
    public ISystemLog Log { get; }

    /// <summary>
    /// 进程内统一的随机源（**T4**）：随机订单/随机工作台/随机罐子选择策略共用它，
    /// 因此给了 <c>randomSeed</c> 之后整条链路都可复现（此前三处各自 <c>new Random()</c>，不可复现）。
    /// </summary>
    internal Random Rng { get; }

    /// <summary>
    /// 世界词汇表（形状/液体目录）—— **S2 注入点**。
    /// 规则语义不在词表里（形状只是名字，能力由工作台标记决定），因此换一份词表即可加/减形状，
    /// 无需改规则引擎、求解器或执行器。默认 = 内置词表（8 形状 / 2 液体）。
    /// </summary>
    public Vocabulary Vocabulary { get; }

    /// <summary>
    /// 世界配置（**S3**）：生产者、初始库存、工作台清单、容量默认、流入与速率都来自它。
    /// 默认 = <see cref="WorldOptions.Default"/>（逐值等于重构前那套硬编码世界）。
    /// 宿主读到 <c>config/world.json</c> 时把它传进来即可 —— 核心实体不再"自知"某个具体世界。
    /// </summary>
    public WorldOptions World { get; }

    /// <param name="log">日志出口；null 时使用默认控制台日志（<see cref="ConsoleSystemLog"/>）。</param>
    /// <param name="randomSeed">随机种子；null 时使用不固定种子（保持"每次运行都不一样"的默认行为）。</param>
    /// <param name="vocabulary">世界词汇表；null 时由 <paramref name="world"/> 的词表字段构造。</param>
    /// <param name="world">世界配置；null 时使用内置默认世界（行为与重构前一致）。</param>
    public JarSystem(ISystemLog? log = null, int? randomSeed = null, Vocabulary? vocabulary = null,
        WorldOptions? world = null)
    {
        Log = log ?? ConsoleSystemLog.Instance;
        World = world ?? WorldOptions.Default;
        Vocabulary = vocabulary ?? World.BuildVocabulary();
        FlowSettings = (World.Flow.JarCount, World.Flow.LiquidAmount);
        _liquidProductionRate = DefaultLiquidRates();
        Rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        ProducerModule = new ProducerModule(Vocabulary);
        Executor = new OrderExecutor(Rng);
        _workshopService = new WorkshopService(this);
        _orderService = new OrderService(this);
        _productionService = new ProductionService(this);
        InitializeDefault();
    }

    /// <summary>装配世界（生产者 + 初始库存 + 工作台）。实现已迁至 <see cref="Bootstrap.SystemBootstrapper"/>。</summary>
    public void InitializeDefault() => Bootstrap.SystemBootstrapper.Apply(World, Vocabulary, ProducerModule, WorkshopList);

    // ============================================================
    // 世界装配 / 状态注入（批 E：把"改世界"收成命名方法）
    //
    // 用途：宿主与测试要把系统装成特定初始状态时，语义明确、可被 grep、且不会留下不一致索引。
    // 注意与 WS 命令的差别：下面的 SetMasterIndex/SetSlaveIndex 是**裸选择**（不执行切换事件），
    // 而 <see cref="SetMaster"/>/<see cref="SetSlave"/> 会按规则搬运资源。
    // ============================================================

    /// <summary>追加一个工作台（不做额外校验，与旧 `Workshops.Add` 等价）。</summary>
    public void AddWorkshop(Workshop workshop) => _workshops.Add(workshop);

    /// <summary>清空工作台，并同步解除主/从选择 —— 避免"清空后仍指向越界下标"的非法状态。</summary>
    public void ClearWorkshops()
    {
        _workshops.Clear();
        CurrentMaster = null;
        CurrentSlave = null;
    }

    /// <summary>追加一个订单（与旧 `Orders.Add` 等价）。</summary>
    public void AddOrder(Order order) => _orders.Add(order);

    /// <summary>清空订单。</summary>
    public void ClearOrders() => _orders.Clear();

    /// <summary>
    /// 直接指定主台下标，**不触发切换事件**（区别于 <see cref="SetMaster"/>）。
    /// 越界下标属编程错误，直接抛 <see cref="ArgumentOutOfRangeException"/>；传 null 表示不选主台。
    /// </summary>
    public void SetMasterIndex(int? index)
    {
        if (index.HasValue && (index.Value < 0 || index.Value >= _workshops.Count))
            throw new ArgumentOutOfRangeException(nameof(index), "主台下标越界: " + index.Value + "（工作台数 " + _workshops.Count + "）");
        CurrentMaster = index;
    }

    /// <summary>直接指定从台下标，**不触发切换事件**（区别于 <see cref="SetSlave"/>）；越界即抛异常。</summary>
    public void SetSlaveIndex(int? index)
    {
        if (index.HasValue && (index.Value < 0 || index.Value >= _workshops.Count))
            throw new ArgumentOutOfRangeException(nameof(index), "从台下标越界: " + index.Value + "（工作台数 " + _workshops.Count + "）");
        CurrentSlave = index;
    }

    /// <summary>替换整个产出模块（旧仓库内容随之丢弃，与旧 `ProducerModule = new()` 等价）。</summary>
    public void ResetProducerModule() => ProducerModule = new ProducerModule(Vocabulary);

    /// <summary>设置某液体的每 tick 产出速率（不参与 JSON 契约）。</summary>
    public void SetLiquidProductionRate(string liquid, int rate) => _liquidProductionRate[liquid] = rate;

    /// <summary>设置主台流入参数（每次流入的罐子数与液体量）。</summary>
    public void SetFlowSettings(int jarCount, int liquidAmount) => FlowSettings = (jarCount, liquidAmount);

    // ============================================================
    // 工作台
    // ============================================================

    /// <summary>设为主工作台（旧主台有资源时执行切换事件）。返回字典键：success/switched/caseType/operations/slaveCleared/reason。</summary>
    public Dictionary<string, object?> SetMaster(int index) => _workshopService.SetMaster(index);

    /// <summary>设为从工作台；拒绝把当前主台设为从台。</summary>
    public Dictionary<string, object?> SetSlave(int index) => _workshopService.SetSlave(index);

    public void CreateWorkshop(string name, List<string> baseMarks, List<string>? additionalMarks = null, List<string>? ban = null)
        => _workshopService.CreateWorkshop(name, baseMarks, additionalMarks, ban);

    public void CreateRandomWorkshop() => _workshopService.CreateRandomWorkshop();

    /// <summary>调整工作台容量；溢出的罐子/液体直接丢弃并计数（仓库单向流动，不回收）。</summary>
    public object? UpdateWorkshopCapacity(int index, int? maxJars, int? maxLiquids)
        => _workshopService.UpdateWorkshopCapacity(index, maxJars, maxLiquids);

    // ============================================================
    // 订单
    // ============================================================

    public (bool Success, string? Error) CreateOrder(List<Requirement> requirements, string orderType = Constants.OrderType.A)
        => _orderService.CreateOrder(requirements, orderType);

    public (bool Success, string? Error) CreateRandomOrder() => _orderService.CreateRandomOrder();

    public object? DeleteOrder(int index) => _orderService.DeleteOrder(index);

    /// <summary>可行性分析（A 类只需主台；B 类需主从）；结果同时写入订单的 FeasibilityResult。</summary>
    public FeasibilityResultData? CheckFeasibility(int orderIndex) => _orderService.CheckFeasibility(orderIndex);

    /// <summary>对所有「待执行」订单做可行性分析（元素见 <see cref="AutoCheckResultDto"/>：index / orderId / result）。</summary>
    public List<AutoCheckResultDto> CheckAllFeasibility() => _orderService.CheckAllFeasibility();

    /// <summary>执行订单：返回 成功 / 原因 / 事件报告 / 是否发生转移 / 操作日志。</summary>
    public (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred, List<ExecutionLogEntry>? Operations)
        ExecuteOrder(int orderIndex) => _orderService.ExecuteOrder(orderIndex);

    // ============================================================
    // 产出与推进
    // ============================================================

    /// <summary>推进 count 次（生产者产罐 → 液体产出 → 向主台流入）。返回值恒为 0。</summary>
    public int Tick(int count = 1) => _productionService.Tick(count);

    public void UpdateProducerRate(string name, string shape, double rate)
        => _productionService.UpdateProducerRate(name, shape, rate);

    public void UpdateLiquidProductionRate(string liquid, int rate)
        => _productionService.UpdateLiquidProductionRate(liquid, rate);

    public void SetAutoCheck(bool enabled) { AutoCheckEnabled = enabled; }

    /// <summary>重置世界并重新装配默认数据（ID 计数器按既有语义不清零）。</summary>
    public void Reset()
    {
        // 与重构前逐项一致：清空工作台/订单/事件历史 → 恢复速率与流入参数 → 清开关与主从 → 重建产出模块与默认世界
        ClearWorkshops(); ClearOrders(); CurrentEvent = null; _eventHistory.Clear();
        _liquidProductionRate.Clear();
        foreach (var kv in DefaultLiquidRates()) _liquidProductionRate[kv.Key] = kv.Value;  // S3：按配置恢复速率
        FlowSettings = (World.Flow.JarCount, World.Flow.LiquidAmount);                      // S3：按配置恢复流入
        AutoCheckEnabled = false;
        ResetProducerModule(); InitializeDefault();
    }

    /// <summary>
    /// 默认液体产出速率：按**词表**逐个取，速率来自世界配置（未列出者按 1）。
    /// 键的插入顺序 = 词表顺序 —— 契约快照里 `liquids` 的键序依赖它。
    /// </summary>
    private Dictionary<string, int> DefaultLiquidRates()
    {
        var rates = new Dictionary<string, int>();
        foreach (var liquid in Vocabulary.Liquids)
            rates[liquid] = World.Flow.LiquidRate.TryGetValue(liquid, out var r) ? r : 1;
        return rates;
    }

    /// <summary>对外状态快照（强类型 DTO；字段名由 <c>[JsonPropertyName]</c> 冻结，见 <see cref="StateDto"/>）。</summary>
    public StateDto GetState() => StateSerializer.Snapshot(this);

    /// <summary>
    /// 世界元数据（形状/液体目录）：**连接时一次性下发**给客户端（见 `world` 消息）。
    /// 刻意不放进 <see cref="GetState"/>：词表是静态信息，塞进每 2 秒推送的 state 只会让消息与契约膨胀。
    /// </summary>
    public WorldMetaDto WorldMeta() => new()
    {
        Shapes = new List<string>(Vocabulary.Shapes),
        Liquids = new List<string>(Vocabulary.Liquids)
    };
}

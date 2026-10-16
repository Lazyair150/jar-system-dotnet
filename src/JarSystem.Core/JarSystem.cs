using JarSystem.Core.Rules;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Executor;
using JarSystem.Core.Orders;
using JarSystem.Core.Process;
using JarSystem.Core.Production;
using JarSystem.Core.Sessions;
using JarSystem.Core.Services;
using JarSystem.Core.Solver;

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
public class JarSystem : Rules.IWorldFacts
{
    private readonly WorkshopService _workshopService;
    private readonly OrderService _orderService;
    private readonly ProductionService _productionService;
    private readonly MasterSwitchCoordinator _masterSwitch;

    private readonly List<Workshop> _workshops = new();
    private readonly List<Order> _orders = new();
    private readonly List<Dictionary<string, object?>> _eventHistory = new();
    private readonly Dictionary<string, int> _liquidProductionRate;

    // ---- 真只读包装（2026-09-16 全量只读化）----
    // 只把私有容器以 IReadOnly* 接口交出去是**不够**的：调用方可以 `((List<Workshop>)sys.Workshops).Clear()`
    // 就地改世界，绕过全部意图方法。这里给每个对外视图包一层 ReadOnly 包装（活视图，构造时建立一次），
    // 与 `Vocabulary` / `WorkshopCapability` / `Workshop.Jars` 同一手法。
    // 注：程序集内的 `WorkshopList`/`OrderList` 仍是**裸可变视图**（Services/Bootstrap 靠它们改世界）✓。
    private readonly IReadOnlyList<Workshop> _workshopsView;
    private readonly IReadOnlyList<Order> _ordersView;
    private readonly IReadOnlyList<Dictionary<string, object?>> _eventHistoryView;
    private readonly IReadOnlyList<WorldCommand> _commandLogView;
    private readonly IReadOnlyDictionary<string, int> _liquidProductionRateView;

    // ---- 会话日志（Event Sourcing 切片）：每次"改世界"的意图都追加一条 ----
    private readonly List<WorldCommand> _commandLog = new();
    private int _logSeq;

    /// <summary>
    /// **命令日志**（只读）：从构造（或 <see cref="Reset"/> 之后的默认世界）开始，
    /// 依次记录宿主下过的每一条"改世界"意图。配合 <see cref="RandomSeed"/> 与同一个世界配置，
    /// 可以用 <see cref="SessionReplay.Into"/> 重建出**同样的世界**。
    /// 读操作（GetState/可行性分析的结果写入除外）不记录。
    /// </summary>
    public IReadOnlyList<WorldCommand> CommandLog => _commandLogView;

    /// <summary>构造时使用的随机种子；null 表示不固定（那样**无法**保证回放一致）。</summary>
    public int? RandomSeed { get; }

    /// <summary>
    /// **本次意图的时间**：事件历史里的 `timestamp` 取自它，而不是直接取 `DateTimeOffset.UtcNow`。
    /// 于是它成为"可记录、可回放"的输入：回放时由日志里的时间驱动，读档后状态才能逐字节一致。
    /// </summary>
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>是否正在回放（由 <see cref="SessionReplay"/> 置位）：回放时**不刷新** <see cref="Now"/>。</summary>
    internal bool Replaying { get; set; }

    /// <summary>
    /// 罐子 ID 源（**注入点**）。回放/多世界场景必须注入各自独立的实例 ——
    /// 否则进程级计数器会让重建出的 ID 与原次不同（见 <see cref="Sessions.WorldCommand"/> 的边界说明）。
    /// </summary>
    public IIdGenerator JarIds { get; }

    /// <summary>订单 ID 源（注入点，同上）。</summary>
    public IIdGenerator OrderIds { get; }

    // ---- 对外只读视图（契约：内容与重构前逐项一致）----
    // 2026-09-16：**真只读包装**（连向下转型也改不动），不是"声明类型恰好是只读接口"。
    public IReadOnlyList<Workshop> Workshops => _workshopsView;
    public IReadOnlyList<Order> Orders => _ordersView;
    public IReadOnlyList<Dictionary<string, object?>> EventHistory => _eventHistoryView;

    /// <summary>每 tick 的液体产出速率（酒/蜜）。**不参与 JSON 契约**；修改请用 <see cref="SetLiquidProductionRate"/>。</summary>
    public IReadOnlyDictionary<string, int> LiquidProductionRate => _liquidProductionRateView;

    // ---- 程序集内可变视图（仅供 Services/Bootstrap 使用；外部看不到） ----
    internal List<Workshop> WorkshopList => _workshops;
    internal List<Order> OrderList => _orders;

    /// <summary>
    /// 事件历史保留条数（**内存边界**，2026-09）。对外只下发最近 10 条（见 <c>StateSerializer</c>），
    /// 但内部列表此前是无限增长的（实测 500 次主从切换后内部已有 500 条）—— 长跑宿主会持续泄漏。
    /// 保留窗口 = 下发窗口 ⇒ 对外 JSON 逐字节不变，只是不再无界。
    /// </summary>
    // 2026-09-16：由 `private` 改 `internal`，并把"两处 10"收敛成一处 —— 下发窗口由
    // StateSerializer **引用本常量**（原先它自己写死 `TakeLast(10)`）。两处各写一个 10 时，
    // 只改一处就会让"保留窗口 = 下发窗口 ⇒ 对外 JSON 逐字节不变"这条论据**静默失效**
    // （对外条数变了，而没人知道是漏改还是故意的）。
    internal const int MaxEventHistory = 10;

    /// <summary>追加一条事件历史并维持长度上限（写入事件历史的**唯一入口**）。</summary>
    internal void AddEvent(Dictionary<string, object?> entry)
    {
        _eventHistory.Add(entry);
        if (_eventHistory.Count > MaxEventHistory)
            _eventHistory.RemoveRange(0, _eventHistory.Count - MaxEventHistory);
    }

    /// <summary>产出模块。整体替换请用 <see cref="ResetProducerModule"/>。</summary>
    public ProducerModule ProducerModule { get; private set; }

    /// <summary>
    /// 可行性检查器（构造后不可整体替换，但**求解器可注入**：见构造参数 <c>solver</c>）。
    /// 预算可经 <see cref="FeasibilityChecker.NodeBudget"/> 调整。
    /// </summary>
    public FeasibilityChecker Checker { get; }

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

    /// <summary>
    /// **过程系统**（P1：过程骨架 + 过程日志）。
    ///
    /// 为什么由门面持有：门面是核心的组合根（它已经持有 <c>Checker</c>/`Executor`/`ProductionModule`），
    /// 而过程系统的职责是"观察并记账一次意图内部的步进"，它需要与门面同一生命周期。
    /// 依赖方向：门面可依赖 <c>Process</c>；但 <c>Domain</c>/<c>Orders</c>/<c>Executor</c>/<c>Services</c>
    /// **只依赖 <c>Abstractions.IProcessRecorder</c>**（依赖倒置），不认识 Process 包。
    /// </summary>
    public ProcessSystem Processes { get; }

    /// <summary>过程日志（只读视图；P1 不投影进 <c>GetState()</c>）。</summary>
    public ProcessJournal Journal => Processes.Journal;

    /// <summary>
    /// **世界的修饰器栈**（P3b）：区间性的临时规则（抑制 / 放宽 / 改拍 / 新增前置条件）。
    ///
    /// 用法（**必须用作用域**，异常路径也要回收）：
    /// <code>
    /// using var scope = sys.Modifiers.Push(new SuppressModifier { Operation = "POUR_IN", Reason = "忌律：此刻不得灌注" });
    /// </code>
    /// 它与工作台、过程系统**共用同一个实例** ⇒ 一处入栈，处处生效；区间到期的修饰器由每个 tick 自动摘掉。
    /// </summary>
    public ModifierStack Modifiers { get; }

    /// <summary>
    /// 世界修订号（= 宿主"改世界意图"的序号，与命令日志同一计数）。
    /// 过程日志用它标记"这一步观察到的是哪一版世界"；P1 里同一次 <c>ExecuteOrder</c> 的所有步骤共享它。
    /// </summary>
    public int WorldRevision => _logSeq;

    /// <param name="log">日志出口；null 时使用默认控制台日志（<see cref="ConsoleSystemLog"/>）。</param>
    /// <param name="randomSeed">随机种子；null 时使用不固定种子（保持"每次运行都不一样"的默认行为）。</param>
    /// <param name="vocabulary">世界词汇表；null 时由 <paramref name="world"/> 的词表字段构造。</param>
    /// <param name="world">世界配置；null 时使用内置默认世界（行为与重构前一致）。</param>
    /// <param name="solver">
    /// **求解器注入点**（策略模式真正可用的前提）：null 时用内置状态空间求解器。
    /// 有了它，宿主/测试可以把"判定算法"整体换掉，而不必绕开门面直接组装 <see cref="FeasibilityChecker"/>。
    /// 注意：替身必须满足现有对外契约（判定结论 + 计划字段），细节见 <see cref="ISolver"/>。
    /// </param>
    /// <param name="jarIds">罐子 ID 源；null 时用进程级共享计数器（保持既有行为）。回放/多世界场景请注入独立实例。</param>
    /// <param name="orderIds">订单 ID 源；null 时用进程级共享计数器（保持既有行为）。</param>
    public JarSystem(ISystemLog? log = null, int? randomSeed = null, Vocabulary? vocabulary = null,
        WorldOptions? world = null, ISolver? solver = null,
        IIdGenerator? jarIds = null, IIdGenerator? orderIds = null)
    {
        // 只读包装**最先建立**：构造期间任何一处（服务/装配器）若读公开视图，都不会拿到 null。
        _workshopsView = _workshops.AsReadOnly();
        _ordersView = _orders.AsReadOnly();
        _eventHistoryView = _eventHistory.AsReadOnly();
        _commandLogView = _commandLog.AsReadOnly();

        Log = log ?? ConsoleSystemLog.Instance;
        World = world ?? WorldOptions.Default;
        Vocabulary = vocabulary ?? World.BuildVocabulary();
        RandomSeed = randomSeed;
        JarIds = jarIds ?? DefaultIdGenerators.Jars;
        OrderIds = orderIds ?? DefaultIdGenerators.Orders;
        FlowSettings = (World.Flow.JarCount, World.Flow.LiquidAmount);
        _liquidProductionRate = DefaultLiquidRates();
        // 速率视图必须**在字段赋值之后**建（readonly 字段的确定性赋值；提前读它编译不过）
        _liquidProductionRateView = new ReadOnlyDictionary<string, int>(_liquidProductionRate);
        Rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        Modifiers = new ModifierStack();
        ProducerModule = new ProducerModule(Vocabulary, Modifiers);
        // P3b 设计裁决：求解器实现**一行不改**，把"修饰器带来的新规则"统一收在这一层装饰器里。
        // 无活跃修饰器时它是直通（逐字节等于原实现）；无法表达的限制如实回"未判定"，绝不误报"不可行"。
        Checker = new FeasibilityChecker(new ModifierAwareSolver(solver ?? new StateSpaceSolver(), Modifiers));
        Executor = new OrderExecutor(Rng);
        _workshopService = new WorkshopService(this);
        _orderService = new OrderService(this);
        _productionService = new ProductionService(this);
        _masterSwitch = new MasterSwitchCoordinator(this);
        Processes = new ProcessSystem(() => _logSeq, modifiers: Modifiers);
        InitializeDefault();
    }

    /// <summary>
    /// 装配世界（生产者 + 初始库存 + 工作台）。实现已迁至 <see cref="Bootstrap.SystemBootstrapper"/>。
    ///
    /// **刻意不是 public**（2026-09 收口）：装配是**追加**语义（不清理已有内容，与重构前一致），
    /// 因此宿主若在活世界上再调一次，会静默得到"工作台翻倍 + 每条生产线速率翻倍 + 初始库存翻倍"
    /// （实测：4→8 台、0.4→0.8、酒 20→40，而这些都会进 <c>producerStatus</c> 契约字段）。
    /// 装配只应由**门面自己的两个安全调用点**触发：构造（空世界）与 <see cref="Reset"/>（已先清空）。
    /// 宿主想换世界请用构造参数 <see cref="WorldOptions"/>，想重来请用 <see cref="Reset"/>。
    /// </summary>
    internal void InitializeDefault() =>
        Bootstrap.SystemBootstrapper.Apply(World, Vocabulary, ProducerModule, WorkshopList, JarIds, Modifiers);

    // ============================================================
    // 会话日志（唯一的写入口：本类所有"改世界"的意图方法都调它）
    // ============================================================

    private void Record(string op, string? name = null, string? orderType = null,
        IReadOnlyList<Requirement>? requirements = null, List<string>? baseMarks = null,
        List<string>? additionalMarks = null, List<string>? ban = null, WorkshopPayload? workshop = null,
        int? intA = null, int? intB = null, int? intC = null, double? doubleA = null, bool? flag = null)
    {
        _logSeq++;
        _commandLog.Add(new WorldCommand
        {
            Seq = _logSeq,
            Op = op,
            Time = Now.ToString("o"),
            Name = name,
            OrderType = orderType,
            // 深拷贝：日志必须与调用方后续的改动隔离（否则回放会拿到被改过的载荷）
            Requirements = requirements?.Select(r => new Requirement { Shape = r.Shape, Liquid = r.Liquid }).ToList(),
            BaseMarks = baseMarks == null ? null : new List<string>(baseMarks),
            AdditionalMarks = additionalMarks == null ? null : new List<string>(additionalMarks),
            Ban = ban == null ? null : new List<string>(ban),
            Workshop = workshop,
            IntA = intA,
            IntB = intB,
            IntC = intC,
            DoubleA = doubleA,
            Flag = flag
        });
    }

    // ============================================================
    // 世界装配 / 状态注入（批 E：把"改世界"收成命名方法）
    //
    // 用途：宿主与测试要把系统装成特定初始状态时，语义明确、可被 grep、且不会留下不一致索引。
    // 注意与 WS 命令的差别：下面的 SetMasterIndex/SetSlaveIndex 是**裸选择**（不执行切换事件），
    // 而 <see cref="SetMaster"/>/<see cref="SetSlave"/> 会按规则搬运资源。
    // ============================================================

    /// <summary>追加一个工作台（不做额外校验，与旧 `Workshops.Add` 等价）。</summary>
    public void AddWorkshop(Workshop workshop)
    {
        workshop.AttachRuleStack(Modifiers);   // 纳入世界 ⇒ 绑到世界的修饰器栈（P3b）
        _workshops.Add(workshop);
        Record(WorldOps.AddWorkshop, workshop: new WorkshopPayload
        {
            Name = workshop.Name,
            BaseMarks = new List<string>(workshop.BaseMarks),
            AdditionalMarks = new List<string>(workshop.AdditionalMarks),
            Ban = new List<string>(workshop.Ban),
            MaxJars = workshop.MaxJars,
            MaxLiquids = workshop.MaxLiquids
        });
    }

    /// <summary>清空工作台，并同步解除主/从选择 —— 避免"清空后仍指向越界下标"的非法状态。</summary>
    public void ClearWorkshops()
    {
        ClearWorkshopsCore();
        Record(WorldOps.ClearWorkshops);
    }

    /// <summary>只改状态、不写日志（内部转发用；理由见 <see cref="SetLiquidProductionRateCore"/>）。</summary>
    internal void ClearWorkshopsCore()
    {
        _workshops.Clear();
        CurrentMaster = null;
        CurrentSlave = null;
    }

    /// <summary>追加一个订单（与旧 `Orders.Add` 等价）。</summary>
    public void AddOrder(Order order)
    {
        _orders.Add(order);
        Record(WorldOps.AddOrder, orderType: order.Type, requirements: order.Requirements);
    }

    /// <summary>清空订单。</summary>
    public void ClearOrders()
    {
        ClearOrdersCore();
        Record(WorldOps.ClearOrders);
    }

    /// <summary>只改状态、不写日志（内部转发用）。</summary>
    internal void ClearOrdersCore()
    {
        _orders.Clear();
    }

    /// <summary>
    /// 直接指定主台下标，**不触发切换事件**（区别于 <see cref="SetMaster"/>）。
    /// 越界下标属编程错误，直接抛 <see cref="ArgumentOutOfRangeException"/>；传 null 表示不选主台。
    /// </summary>
    public void SetMasterIndex(int? index)
    {
        if (index.HasValue && (index.Value < 0 || index.Value >= _workshops.Count))
            throw new ArgumentOutOfRangeException(nameof(index), "主台下标越界: " + index.Value + "（工作台数 " + _workshops.Count + "）");
        CurrentMaster = index;
        Record(WorldOps.SetMasterIndex, intA: index);
    }

    /// <summary>直接指定从台下标，**不触发切换事件**（区别于 <see cref="SetSlave"/>）；越界即抛异常。</summary>
    public void SetSlaveIndex(int? index)
    {
        if (index.HasValue && (index.Value < 0 || index.Value >= _workshops.Count))
            throw new ArgumentOutOfRangeException(nameof(index), "从台下标越界: " + index.Value + "（工作台数 " + _workshops.Count + "）");
        CurrentSlave = index;
        Record(WorldOps.SetSlaveIndex, intA: index);
    }

    /// <summary>替换整个产出模块（旧仓库内容随之丢弃，与旧 `ProducerModule = new()` 等价）。</summary>
    public void ResetProducerModule()
    {
        ResetProducerModuleCore();
        Record(WorldOps.ResetProducerModule);
    }

    /// <summary>只改状态、不写日志（内部转发用）。</summary>
    internal void ResetProducerModuleCore()
    {
        ProducerModule = new ProducerModule(Vocabulary, Modifiers);
    }

    /// <summary>
    /// 设置某液体的每 tick 产出速率（不参与 JSON 契约）。
    /// **未知液体与负数会被拒绝** —— 校验住在唯一写入口，见 <see cref="SetLiquidProductionRateCore"/>。
    /// </summary>
    public void SetLiquidProductionRate(string liquid, int rate)
    {
        SetLiquidProductionRateCore(liquid, rate);
        Record(WorldOps.SetLiquidProductionRate, ban: new List<string> { liquid }, intA: rate);
    }

    /// <summary>
    /// 只改数据、**不写会话日志**的内部入口：给"服务内部转发"用。
    /// 会话日志必须**一次宿主意图只记一条** —— 否则存档里会出现重复条目、回放会把日志越滚越长
    /// （实测踩过：`UpdateLiquidProductionRate` 内部调 `SetLiquidProductionRate`，同一次意图被记两条）。
    ///
    /// **校验也住在这一层（2026-09-16 修）**：这是"液体速率"唯一的写入口 —— 公开的
    /// <see cref="SetLiquidProductionRate"/> 与服务的 <c>UpdateLiquidProductionRate</c> 都汇到这里，
    /// 规则只有一份才不会被绕过。原实现是 <c>_liquidProductionRate[liquid] = rate</c>（字典索引器），
    /// 未知液体名会被**插入成幻影液体**：它随后进入产出循环，而回放/存档的输入是**外部数据**
    /// ⇒ 能够造出一个手工不可达的世界（审查中 4 位专家独立指出）。
    /// </summary>
    internal void SetLiquidProductionRateCore(string liquid, int rate)
    {
        if (!_liquidProductionRate.ContainsKey(liquid))
            throw new DomainValidationException("没有这个液体: " + liquid);
        if (rate < 0)
            throw new DomainValidationException("液体产出速率不能为负: " + rate);
        _liquidProductionRate[liquid] = rate;
    }

    /// <summary>
    /// 设置主台流入参数（每次流入的罐子数与液体量）。
    /// **负数在这里被拒绝**：回放会把存档里的 <c>intA/intB</c> 原样送进来，
    /// 若放行负数，读档就能造出 UI 永远做不到的世界（判据与 <c>WorldOptions.Validate</c> 一致）。
    /// </summary>
    public void SetFlowSettings(int jarCount, int liquidAmount)
    {
        if (jarCount < 0) throw new DomainValidationException("流入罐子数不能为负: " + jarCount);
        if (liquidAmount < 0) throw new DomainValidationException("流入液体量不能为负: " + liquidAmount);
        FlowSettings = (jarCount, liquidAmount);
        Record(WorldOps.SetFlowSettings, intA: jarCount, intB: liquidAmount);
    }

    // ============================================================
    // 工作台
    // ============================================================

    /// <summary>
    /// 设为主工作台（旧主台有资源时执行切换事件）。
    /// 返回字典键：success/switched/caseType/operations/slaveCleared/reason。
    /// 编排已移至 <see cref="MasterSwitchCoordinator"/>（原 WorkshopService 的一部分）。
    /// </summary>
    public Dictionary<string, object?> SetMaster(int index)
    {
        // 切换事件会写事件历史（带时间戳）⇒ 先定好"本次意图的时间"（回放时不刷新，用日志里的时间）
        if (!Replaying) Now = DateTimeOffset.UtcNow;
        var result = _masterSwitch.SetMaster(index);
        Record(WorldOps.SetMaster, intA: index);
        return result;
    }

    /// <summary>设为从工作台；拒绝把当前主台设为从台。编排见 <see cref="MasterSwitchCoordinator"/>。</summary>
    public Dictionary<string, object?> SetSlave(int index)
    {
        var result = _masterSwitch.SetSlave(index);
        Record(WorldOps.SetSlave, intA: index);
        return result;
    }

    public void CreateWorkshop(string name, List<string> baseMarks, List<string>? additionalMarks = null, List<string>? ban = null)
    {
        _workshopService.CreateWorkshop(name, baseMarks, additionalMarks, ban);
        Record(WorldOps.CreateWorkshop, name: name, baseMarks: baseMarks, additionalMarks: additionalMarks, ban: ban);
    }

    public void CreateRandomWorkshop()
    {
        _workshopService.CreateRandomWorkshop();
        Record(WorldOps.CreateRandomWorkshop);
    }

    /// <summary>调整工作台容量；溢出的罐子/液体直接丢弃并计数（仓库单向流动，不回收）。</summary>
    public object? UpdateWorkshopCapacity(int index, int? maxJars, int? maxLiquids)
    {
        var result = _workshopService.UpdateWorkshopCapacity(index, maxJars, maxLiquids);
        Record(WorldOps.UpdateWorkshopCapacity, intA: index, intB: maxJars, intC: maxLiquids);
        return result;
    }

    // ============================================================
    // 订单
    // ============================================================

    public (bool Success, string? Error) CreateOrder(List<Requirement> requirements, string orderType = Constants.OrderType.A)
    {
        var result = _orderService.CreateOrder(requirements, orderType);
        // 只记**已生效**的意图：校验失败（形状/液体/顺序/条数）没有改变世界，不进日志
        if (result.Success) Record(WorldOps.CreateOrder, orderType: orderType, requirements: requirements);
        return result;
    }

    public (bool Success, string? Error) CreateRandomOrder()
    {
        var result = _orderService.CreateRandomOrder();
        if (result.Success) Record(WorldOps.CreateRandomOrder);
        return result;
    }

    public object? DeleteOrder(int index)
    {
        var result = _orderService.DeleteOrder(index);
        Record(WorldOps.DeleteOrder, intA: index);
        return result;
    }

    /// <summary>可行性分析（A 类只需主台；B 类需主从）；结果同时写入订单的 FeasibilityResult。</summary>
    public FeasibilityResultData? CheckFeasibility(int orderIndex)
    {
        var result = _orderService.CheckFeasibility(orderIndex);
        // 分析会把结果写进订单（属于状态变更）⇒ 记进日志，回放才能复现同一份分析结果
        Record(WorldOps.CheckFeasibility, intA: orderIndex);
        return result;
    }

    /// <summary>对所有「待执行」订单做可行性分析（元素见 <see cref="AutoCheckResultDto"/>：index / orderId / result）。</summary>
    public List<AutoCheckResultDto> CheckAllFeasibility()
    {
        var result = _orderService.CheckAllFeasibility();
        Record(WorldOps.CheckAllFeasibility);
        return result;
    }

    /// <summary>执行订单：返回 成功 / 原因 / 事件报告 / 是否发生转移 / 操作日志。</summary>
    public (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred, List<ExecutionLogEntry>? Operations)
        ExecuteOrder(int orderIndex)
    {
        // 订单执行同样会写事件历史（带时间戳）
        if (!Replaying) Now = DateTimeOffset.UtcNow;

        // ============================================================
        // P2b：订单执行 = 一个**过程**（同步轨：建立 → 在一个调用里把单位跑完 → 收尾）
        //
        // 语义边界（与既有行为逐条对齐）：
        //   · 只在订单下标合法时建过程 —— 与"前置校验拒绝的 5 条路径不写事件历史"同一口径；
        //   · 前置校验（未选主台 / B 类缺从台 / 终态）失败时，过程以失败结尾且**不改世界**；
        //   · 执行编排、交付、事件历史走与异步轨**同一个会话**（语义只有一份，不会漂移）。
        // ============================================================
        var order = orderIndex >= 0 && orderIndex < _orders.Count ? _orders[orderIndex] : null;
        var (run, session, error) = BeginOrderProcess(orderIndex, order);
        if (session == null)
        {
            run?.Finish(false, error);
            Record(WorldOps.ExecuteOrder, intA: orderIndex);
            return (false, error, null, false, null);
        }

        var advancer = new OrderProcessAdvancer(_orderService, session);
        Processes.AdvanceToCompletion(run!, advancer);   // 同步 drain（含结束过程与一次性收尾）

        // 失败也记：按设计"失败的尝试也留下代价"，它**改变过**世界
        Record(WorldOps.ExecuteOrder, intA: orderIndex);
        return advancer.Result ?? (advancer.Succeeded, advancer.FailureReason, null, false, null);
    }

    /// <summary>
    /// **异步轨（P2b，裁决选项 (c)）**：把订单执行起成一个**由 tick 驱动**的过程 ——
    /// 立即返回、**不推进世界**；随后每个世界 tick（每 <c>TicksPerStep</c> 刻）推进一个单位，
    /// 单位用尽时自动交付 / 落账 / 结束过程。
    ///
    /// 为什么两条轨并存（而不是改写同步轨）：本项目的回归资产（5000 用例 + 回放门 + 契约快照）
    /// 全部建立在"<c>executeOrder</c> 即时返回成败"之上；异步轨是**新增能力**，
    /// 让新宿主能拿到真实的时间感，而不必让旧宿主与全部回归一起改口。
    /// 两者共用同一个会话与工作体，因此语义只有一份（差别只在**谁驱动**）。
    /// </summary>
    /// <param name="deadlineTicks">相对时限（刻）：null = 无时限；超时未完成即失败（由 tick 管线判定）。</param>
    public (bool Started, string? Error, int? ProcessId) StartOrder(int orderIndex, int? deadlineTicks = null)
    {
        if (!Replaying) Now = DateTimeOffset.UtcNow;
        var order = orderIndex >= 0 && orderIndex < _orders.Count ? _orders[orderIndex] : null;
        var (run, session, error) = BeginOrderProcess(orderIndex, order, deadlineTicks);
        if (session == null)
        {
            run?.Finish(false, error);
            Record(WorldOps.StartOrder, intA: orderIndex);
            return (false, error, run?.Instance.Id);
        }

        // 异步轨挂上**守卫**：每个单位执行前检查"世界是否还容得下这个计划"。
        // 同步轨（ExecuteOrder）刻意不挂 —— 它在一个调用里跑完，世界不会中途变化。
        Processes.AttachAdvancer(run!, new OrderProcessAdvancer(_orderService, session, () => CheckOrderGuard(session)));
        Record(WorldOps.StartOrder, intA: orderIndex);
        return (true, null, run!.Instance.Id);
    }

    /// <summary>
    /// **订单过程的守卫**（异步轨每个单位执行前、以及被阻断后的每个 tick 调用）：
    /// 回答"世界是否还容得下这个计划"。三条判据都刻意做成**可解释、可恢复**的：
    /// <list type="number">
    /// <item>订单还在世界里（被删除 ⇒ 永久不成立）；</item>
    /// <item>计划涉及的工作台仍被选中（主台/从台被换掉或取消选择 ⇒ 暂时不成立，玩家可以选回来）；</item>
    /// <item>计划还要用到的器物仍在工作台里（改容量把罐子裁掉、切换主台把资源搬走 ⇒ 暂时不成立）。</item>
    /// </list>
    /// 只读检查，不改世界；原因用世界语言（可直接显示给玩家）。
    /// </summary>
    private GuardResult CheckOrderGuard(OrderExecutionSession session)
    {
        if (!_orders.Contains(session.Order)) return GuardResult.Fatal("订单已不在世界里");

        if (!CurrentMaster.HasValue || _workshops[CurrentMaster.Value] != session.Master)
            return GuardResult.Blocked("主台已变更");
        if (session.Order.Type == Constants.OrderType.B
            && (!CurrentSlave.HasValue || _workshops[CurrentSlave.Value] != session.Slave))
            return GuardResult.Blocked("从台已变更");

        // 修饰器（P3b）：即将执行的那一步是否被"临时禁令"抑制？
        // 抑制 ⇒ 阻断等待（区间过去即恢复），而不是执行到一半才失败。
        var (op, shape, liquid) = session.NextUnitTarget();
        if (Modifiers.SuppressionReason(op, shape, liquid) is { } suppressed)
            return GuardResult.Blocked(suppressed);

        // 修饰器（P3b）：内容侧给的"新增前置条件"是否此刻不成立？（成立则返回 null ⇒ 不影响）
        if (Modifiers.RequiredViolation() is { } required)
            return GuardResult.Blocked(required);

        foreach (var jarId in session.RemainingJarIds())
        {
            bool alive = session.Master.Jars.Any(j => j.Id == jarId) || session.Slave.Jars.Any(j => j.Id == jarId);
            if (!alive) return GuardResult.Blocked("计划所需的器物已不在工作台");
        }

        return GuardResult.Ok();
    }

    /// <summary>建过程（下标非法时**不建**）并准备执行会话；返回 (过程句柄, 会话, 错误)。</summary>
    private (ProcessRun? Run, OrderExecutionSession? Session, string? Error) BeginOrderProcess(
        int orderIndex, Order? order, int? deadlineTicks = null)
    {
        var run = order != null
            ? Processes.Begin(new ProcessDefinition(ProcessKinds.OrderExecution, "委托 #" + order.Id, order.Id,
                deadlineTicks: deadlineTicks, graceTicks: ProcessDefaults.GraceTicks))
            : null;
        var (session, error) = _orderService.BeginOrderExecution(orderIndex, run);
        return (run, session, error);
    }

    /// <summary>
    /// **取消一个进行中的过程**（P4）：已完成的部分不回滚；订单按失败收尾（原因会写进事件历史）。
    /// 返回 false 表示"没有这个进行中的过程"（可能已经结束）—— 宿主重试不会因此出错。
    /// </summary>
    public (bool Cancelled, string? Error) CancelProcess(int processId, string? reason = null)
    {
        var result = Processes.Cancel(processId, reason ?? "被取消");
        Record(WorldOps.CancelProcess, intA: processId);
        return result;
    }

    // ============================================================
    // 产出与推进
    // ============================================================

    /// <summary>
    /// 推进 count 个**世界步进**（生产者产罐 → 液体产出 → 向主台流入）。返回值恒为 0（保持对外签名与行为）。
    ///
    /// P2 起这里成了**时间与过程的唯一推进口**：每个步进先走 tick 管线
    /// （逻辑时钟 +1 → ① 事件 ② 修饰器 ③ 过程推进，见 <see cref="Process.TickPipeline"/>），
    /// 再做既有的世界产出。逐 tick 拆开**不改变状态**（论证见 <c>ProductionService</c> 的类注释）；
    /// 流入仍保持"每次宿主 Tick 调用一次"的批量语义。
    /// </summary>
    public int Tick(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            Processes.Pipeline.AdvanceOneTick();
            _productionService.TickStep();
        }
        _productionService.FlushFlow();
        Record(WorldOps.Tick, intA: count);
        return 0;
    }

    public void UpdateProducerRate(string name, string shape, double rate)
    {
        _productionService.UpdateProducerRate(name, shape, rate);
        Record(WorldOps.UpdateProducerRate, name: name, baseMarks: new List<string> { shape }, doubleA: rate);
    }

    public void UpdateLiquidProductionRate(string liquid, int rate)
    {
        _productionService.UpdateLiquidProductionRate(liquid, rate);
        Record(WorldOps.UpdateLiquidProductionRate, ban: new List<string> { liquid }, intA: rate);
    }

    public void SetAutoCheck(bool enabled)
    {
        AutoCheckEnabled = enabled;
        Record(WorldOps.SetAutoCheck, flag: enabled);
    }

    /// <summary>重置世界并重新装配默认数据（ID 计数器按既有语义不清零）。</summary>
    public void Reset()
    {
        // 与重构前逐项一致：清空工作台/订单/事件历史 → 恢复速率与流入参数 → 清开关与主从 → 重建产出模块与默认世界
        // 内部一律走 *Core（不写日志），本方法自己只记一条 `reset` —— 保证"一次宿主意图一条记录"
        ClearWorkshopsCore(); ClearOrdersCore(); CurrentEvent = null; _eventHistory.Clear();
        _liquidProductionRate.Clear();
        foreach (var kv in DefaultLiquidRates()) _liquidProductionRate[kv.Key] = kv.Value;  // S3：按配置恢复速率
        FlowSettings = (World.Flow.JarCount, World.Flow.LiquidAmount);                      // S3：按配置恢复流入
        AutoCheckEnabled = false;
        ResetProducerModuleCore(); InitializeDefault();
        Record(WorldOps.Reset);
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

    // ============================================================
    // 世界事实取值（Rules.IWorldFacts，P5）：给数据驱动的结构化查询（Rules.WorldQuery）用
    //
    // 口径：只读、不抛异常；取不到一律返回 0（于是"条件不成立"，规则表现为阻断而不是崩溃）。
    // workshop 接受 "master" / "slave" / 具体工作台名（解析不到视为不存在 ⇒ 0）。
    // ============================================================

    /// <inheritdoc />
    public int WorkshopJars(string workshop, string? shape, bool? empty)
    {
        var ws = ResolveWorkshop(workshop);
        if (ws == null) return 0;
        int n = 0;
        foreach (var jar in ws.Jars)
        {
            if (shape != null && jar.Shape != shape) continue;
            if (empty == true && !jar.IsEmpty()) continue;
            if (empty == false && !jar.IsFull()) continue;
            n++;
        }
        return n;
    }

    /// <inheritdoc />
    public int WorkshopLiquid(string workshop, string liquid)
    {
        var ws = ResolveWorkshop(workshop);
        return ws == null ? 0 : ws.LiquidStorage.GetValueOrDefault(liquid);
    }

    /// <inheritdoc />
    public int WarehouseJars() => ProducerModule.Warehouse.JarCount;

    /// <inheritdoc />
    public int WarehouseLiquid(string liquid) => ProducerModule.Warehouse.LiquidCount(liquid);

    /// <inheritdoc />
    public int DiscardedJars() => ProducerModule.Warehouse.DiscardedJars;

    /// <inheritdoc />
    public int DiscardedLiquid(string liquid) =>
        ProducerModule.Warehouse.DiscardedLiquids.GetValueOrDefault(liquid);

    /// <summary>解析查询里的工作台主体："master" / "slave" / 具体名字；找不到返回 null。</summary>
    private Workshop? ResolveWorkshop(string workshop)
    {
        if (string.Equals(workshop, "master", StringComparison.OrdinalIgnoreCase))
            return CurrentMaster.HasValue ? _workshops[CurrentMaster.Value] : null;
        if (string.Equals(workshop, "slave", StringComparison.OrdinalIgnoreCase))
            return CurrentSlave.HasValue ? _workshops[CurrentSlave.Value] : null;
        foreach (var w in _workshops)
            if (w.Name == workshop) return w;
        return null;
    }
}
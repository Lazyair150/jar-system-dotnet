using JarSystem.Core.Rules;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Executor;
using JarSystem.Core.Orders;
using JarSystem.Core.Process;
using JarSystem.Core.Presentation;
using JarSystem.Core.Production;
using JarSystem.Core.Sessions;
using JarSystem.Core.Services;
using JarSystem.Core.Solver;
using JarSystem.Core.World;

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
public partial class JarSystem : Rules.IWorldFacts
{
    private readonly WorkshopService _workshopService;
    private readonly OrderService _orderService;
    private readonly ProductionService _productionService;
    private readonly MasterSwitchCoordinator _masterSwitch;

    private readonly List<Workshop> _workshops = new();
    private readonly List<Order> _orders = new();
    private readonly List<Dictionary<string, object?>> _eventHistory = new();
    private readonly WorldOptions _world;
    // 液体产能是**概念体**（Production.LiquidProduction）：带累加器，因此能表达 0.5 份/tick。
    // 罐子的产能属于生产者（ShapeProductionLine），液体的产能属于世界自己 —— 概念不同，
    // 但"每 tick 产多少、能不能调、小数怎么攒"是同一套定点累加。
    private readonly Production.LiquidProduction _liquidProduction;

    // ---- 真只读包装（2026-09-16 全量只读化）----
    // 只把私有容器以 IReadOnly* 接口交出去是**不够**的：调用方可以 `((List<Workshop>)sys.Workshops).Clear()`
    // 就地改世界，绕过全部意图方法。这里给每个对外视图包一层 ReadOnly 包装（活视图，构造时建立一次），
    // 与 `Vocabulary` / `WorkshopCapability` / `Workshop.Jars` 同一手法。
    // 注：程序集内的 `WorkshopList`/`OrderList` 仍是**裸可变视图**（Services/Bootstrap 靠它们改世界）✓。
    private readonly IReadOnlyList<Workshop> _workshopsView;
    private readonly IReadOnlyList<Order> _ordersView;
    private readonly IReadOnlyList<Dictionary<string, object?>> _eventHistoryView;
    private readonly IReadOnlyList<WorldCommand> _commandLogView;
    private readonly IReadOnlyDictionary<string, double> _liquidProductionRateView;

    // ---- 会话日志（Event Sourcing 切片）：每次"改世界"的意图都追加一条 ----
    private readonly List<WorldCommand> _commandLog = new();
    private int _logSeq;
    private int _completedOrders;

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

    /// <summary>
    /// 本世界的**订单模板库**（来自 <see cref="WorldOptions.OrderTemplates"/>，装配后只读）。
    /// 空库表示这个世界不提供模板 —— 订单只能靠显式需求或随机生成。
    /// 用 <c>Orders.CreateOrderFromTemplate(name)</c> 按模板派单。
    /// </summary>
    public OrderTemplateLibrary OrderTemplates { get; }
    public IReadOnlyList<Dictionary<string, object?>> EventHistory => _eventHistoryView;

    /// <summary>
    /// 每 tick 的液体产出速率（键序 = 词表顺序）。**它是概念的只读投影**，修改请用
    /// <see cref="SetLiquidProductionRate"/> / <see cref="UpdateLiquidProductionRate"/>（规则住在
    /// <see cref="Production.LiquidProduction.SetRate"/>，只有一份）。
    /// </summary>
    public IReadOnlyDictionary<string, double> LiquidProductionRate => _liquidProductionRateView;

    /// <summary>液体产能这个概念体本身（速率、累加器、本 tick 会不会产出）。</summary>
    public Production.LiquidProduction LiquidProduction => _liquidProduction;

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

    /// <summary>
    /// 换台事件的**唯一发射点**。两条轨（同步 <c>setMaster</c> 经 <c>MasterSwitchCoordinator</c>、
    /// 异步 <c>beginSwitch</c> 经 <c>CommitSwitch</c>）都走这里。
    /// 之前是两处各拼一份同样的字典 —— 形状一旦分叉，消费方就得靠猜。
    /// </summary>
    internal void AddWorkshopSwitchEvent(
        string from, string to, string caseType, List<object> operations)
    {
        AddEvent(new Dictionary<string, object?>
        {
            ["type"] = "workshop_switch",
            ["from"] = from,
            ["to"] = to,
            ["caseType"] = caseType,
            ["operations"] = operations,
            ["timestamp"] = Now.ToString("o")
        });
    }

    /// <summary>
    /// 配置类世界事件（产线速率 / 液体速率 / 流入参数）的**唯一发射点**。
    /// 字段由调用方给：三种配置的字段本来就不同，硬塞成一个 <c>value</c> 只会让消费方猜。
    /// </summary>
    internal void AddConfigEvent(string kind, Dictionary<string, object?> fields)
    {
        var entry = new Dictionary<string, object?> { ["type"] = kind };
        foreach (var kv in fields) entry[kv.Key] = kv.Value;
        entry["timestamp"] = Now.ToString("o");
        AddEvent(entry);
    }

    /// <summary>产出模块。整体替换请用 <see cref="ResetProducerModule"/>。</summary>
    public ProducerModule ProducerModule { get; private set; }

    /// <summary>
    /// 资源账本（**唯一的资源变化事实来源**，原因词表见 <see cref="Production.ResourceCauses"/>）。
    ///
    /// 它回答"这个资源是从哪来的"：`production`（世界自产）/ `stock`（开局库存）/
    /// `flow`（仓库→主台）/ `transfer`（主台→从台）/ `return`（从台→主台）/ `discard`（溢出丢弃）。
    /// 表现层因此不必从前后状态差值反推"大概是自己长出来的"。
    ///
    /// **只读投影**：写入点只在资源真正变动的地方，读取可以有很多，读它不会改变世界。
    /// 有界（容量 <see cref="Production.ResourceLedger.DefaultCapacity"/>），丢弃的事实可读
    /// （<see cref="Production.ResourceLedger.Dropped"/>）。
    /// </summary>
    public Production.ResourceLedger ResourceLedger { get; }

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
    /// 对外访问返回深拷贝，修改返回值不会改变活世界。
    /// </summary>
    public WorldOptions World => _world.Clone();

    /// <summary>程序集内部读取的权威配置快照（只在构造时深拷贝一次）。</summary>
    internal WorldOptions WorldConfig => _world;

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
    /// **按主体回看过程步骤**（B2「步骤可观测」）：帧只带引用
    /// （<c>PresentationFrame.Subject</c> / <c>.SubjectId</c>），这是配上它的另一半 ——
    /// 消费方拿到一帧之后，用它把"发生了什么"查出来。
    ///
    /// <para>
    /// **它不是第二个真相源**：数据全部来自 <see cref="Journal"/>（过程日志本来就是步骤的唯一记录），
    /// 本方法只做两件事 —— 把主体映射成过程 id（<see cref="ProcessSystem.TryGetProcessId"/>），
    /// 再按"步骤下标升序、同下标按序号"排好。删掉它世界一步都不变，回放字节也不变。
    /// </para>
    ///
    /// <para>口径（消费方必须知道，刻意的）：</para>
    /// <list type="bullet">
    /// <item>覆盖**活动中的过程**：正在进行的过程同样可查，不必等它结束；</item>
    /// <item>返回该过程的**全部**日志条目（`plan_accepted` / `started` / `step_applied` / `phase` /
    /// `failed` …）；只想要"步"的消费方过滤 <see cref="JournalEntry.StepIndex"/> 即可；</item>
    /// <item>里程碑条目没有步骤下标，因此排在步骤之后 —— 这是
    /// <c>StepIndex ?? int.MaxValue</c> 排序规则的必然结果，不是遗漏；</item>
    /// <item>空结果有两种原因：该主体从未起过过程，或它的指针已被容量淘汰
    /// （<see cref="ProcessSystem.DroppedSubjectMappings"/>）。必须读成
    /// "没有**保留的**步骤"，而不是"没发生过"。</item>
    /// </list>
    /// </summary>
    /// <param name="subject">主体描述（与帧上的 <c>Subject</c> 同值，例 `"委托 #12"`）。</param>
    /// <param name="subjectId">结构化主体标识（与帧上的 <c>SubjectId</c> 同值，例订单 id）。</param>
    public IReadOnlyList<JournalEntry> StepsFor(string subject, int subjectId)
    {
        if (!Processes.TryGetProcessId(subject, subjectId, out var processId))
            return Array.Empty<JournalEntry>();

        var entries = new List<JournalEntry>();
        foreach (var entry in Journal.Entries)
            if (entry.ProcessId == processId) entries.Add(entry);

        entries.Sort((a, b) =>
        {
            int keyA = a.StepIndex ?? int.MaxValue;
            int keyB = b.StepIndex ?? int.MaxValue;
            return keyA != keyB ? keyA.CompareTo(keyB) : a.Seq.CompareTo(b.Seq);
        });
        return entries;
    }

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

    /// <summary>
    /// **回合预算**（关卡规则）：上限来自 <see cref="Config.WorldOptions.TurnLimit"/>（0 = 不限），
    /// 已用来自世界时钟。一回合 = 一个 tick —— 让"最多推几回合"和"推了多少"用同一把尺子，
    /// 否则上限与执行各算各的，界面和世界迟早对不上。
    /// </summary>
    public TurnBudget Turns => new(_world.TurnLimit, Processes.Clock.Tick);

    /// <summary>已经成功交付的订单数（判胜的分子；失败与进行中的都不算）。</summary>
    public int CompletedOrders => _completedOrders;

    /// <summary>
    /// 一次成功交付后推进胜利计数。它是**单调累计值**，不随后续删除/清理订单而回退：
    /// “完成过几单”是历史事实，不是“当前表里还留着几张成功单”。
    /// </summary>
    internal void NoteOrderCompleted() => _completedOrders++;

    /// <summary>
    /// **这一局的结局**（赢／输／还在打）。判据只有一处 —— <see cref="LevelGoal.Judge"/>：
    /// 目标单数来自 <see cref="Config.WorldOptions.GoalOrders"/>，已用回合来自 <see cref="Turns"/>。
    /// 页面只显示，不自己算"我赢了吗"。
    /// </summary>
    public LevelOutcome Outcome => LevelGoal.Judge(_world.GoalOrders, CompletedOrders, Turns);

    /// <summary>
    /// 世界此刻的阶段（内核判定，页面据此切换可用动作与措辞）。
    ///
    /// 判定只用世界自己的事实，顺序有讲究：
    /// <list type="number">
    /// <item>这一局已有结果（赢了或回合用尽）⇒ <see cref="global::JarSystem.Core.World.WorldPhase.Settled"/>：再推进改变不了结局；</item>
    /// <item>有订单过程正在跑 ⇒ <see cref="global::JarSystem.Core.World.WorldPhase.Running"/>：分析/新开单得等它收尾；</item>
    /// <item>否则 ⇒ <see cref="global::JarSystem.Core.World.WorldPhase.Setup"/>：布置产线、分析、挑一单开始。</item>
    /// </list>
    /// </summary>
    public global::JarSystem.Core.World.WorldPhase Phase
    {
        get
        {
            if (Outcome.IsOver) return global::JarSystem.Core.World.WorldPhase.Settled;
            var running = Processes.ActiveProcesses.Any(
                p => p.Kind == ProcessKinds.OrderExecution && p.State != ProcessState.Succeeded
                     && p.State != ProcessState.Failed && p.State != ProcessState.Aborted);
            return running ? global::JarSystem.Core.World.WorldPhase.Running : global::JarSystem.Core.World.WorldPhase.Setup;
        }
    }



    /// <summary>
    /// **世界信息总线**：世界每次变化在这里发一条信号，关心它的模块从同一个地方收到。
    ///
    /// 它同时是过程管线的**事件源**（`ProcessSystem.EventSource`）—— 于是反应器请求的
    /// "后续世界变化"会被管线在下一个 tick 的第 ① 步统一执行，级联因此可复现、有界、可审计。
    /// 默认没有订阅者也没有反应器，此时总线不改变世界任何一步的走向。
    /// </summary>
    public WorldBus Bus { get; }

    /// <summary>
    /// **表现层帧流**：核心对"世界如何被看见"唯一承诺的东西 —— 按发生顺序排好、帧号单调的一串引用。
    ///
    /// 它与总线的分工是刻意的：
    /// <list type="bullet">
    /// <item><see cref="Bus"/> 面向**世界内部**：订阅者可反应、可级联，会影响世界；</item>
    /// <item>本帧流面向**世界外部**：只读、只排顺序，删掉它世界一步都不变。</item>
    /// </list>
    /// 因此宿主（Web / Godot / 离线录制）应该只认这一条流，不要再各自造一套事件词汇
    /// —— 那正是 2026-11 审计发现的缺口（三个宿主三套语义，谁也说不清自己漏了什么）。
    ///
    /// 帧里**没有状态**：需要"那一刻的世界"请自己维护读模型、按帧增量应用；
    /// 想一次性对齐请调 <c>GetState()</c>。节奏（多久播一帧）是宿主的参数，不是内核的
    /// —— 和 <c>TickIntervalMs</c> 同类。
    /// </summary>
    public PresentationFeed Presentation { get; }

    /// <summary>罐子对象池（世界级；生产借出、丢弃归还）。见 <see cref="Domain.JarPool"/>。</summary>
    public JarPool JarPool { get; }

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
        _world = (world ?? WorldOptions.Default).Clone();
        Vocabulary = vocabulary ?? _world.BuildVocabulary();
        // 订单模板是**内容**：从世界配置装配一次，之后只读。校验在这里立即发生 ——
        // 配置写错要在装配时报，不要等第一次派单才炸。
        OrderTemplates = OrderTemplateLibrary.FromOptions(
            _world.OrderTemplates, Vocabulary.Shapes, Vocabulary.Liquids);
        RandomSeed = randomSeed;
        JarIds = jarIds ?? DefaultIdGenerators.Jars;
        OrderIds = orderIds ?? DefaultIdGenerators.Orders;
        FlowSettings = (_world.Flow.JarCount, _world.Flow.LiquidAmount);
        // 液体产能必须在 Vocabulary 建好之后（键序=词表顺序）；强度修饰器此时还没建，
        // 所以先用直通，等 Modifiers 建好后再把强度入口接上（见下方 AttachLiquidIntensity）。
        _liquidProduction = new Production.LiquidProduction(Vocabulary, null, DefaultLiquidRates());
        _liquidProductionRateView = _liquidProduction.Rates;
        Rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        Bus = new WorldBus();
        // 资源账本要在任何产出/流入之前就存在（Bootstrap 落初始库存时就要记账）。
        ResourceLedger = new Production.ResourceLedger();
        // 帧流只是总线的**只读投影**：它订阅总线、存帧，从不回写世界。
        // 删掉这一行与下一行，世界行为与回放字节完全不变（PresentationFeedTests 守着这条）。
        Presentation = new PresentationFeed();
        Bus.Subscribe(new BusPresentationBridge(Presentation));
        JarPool = new JarPool();
        // 第一个真实反应器：世界资源一变，待定订单的可行性缓存就不再成立。
        // 它排进下一个 tick 的批再清 —— 信号分发时世界还处在"改到一半"的状态，不能此刻读。
        Modifiers = new ModifierStack();
        _liquidProduction.AttachIntensity(Modifiers);
        ProducerModule = new ProducerModule(Vocabulary, Modifiers, JarPool, JarIds);
        ProducerModule.ProducerSlots = _world.ProducerSlots;   /* 0 = 不限：出厂沙盒；关卡文件显式给上限 */
        // P3b 设计裁决：求解器实现**一行不改**，把"修饰器带来的新规则"统一收在这一层装饰器里。
        // 无活跃修饰器时它是直通（逐字节等于原实现）；无法表达的限制如实回"未判定"，绝不误报"不可行"。
        Checker = new FeasibilityChecker(new ModifierAwareSolver(solver ?? new StateSpaceSolver(), Modifiers));
        Executor = new OrderExecutor(Rng);
        _workshopService = new WorkshopService(this);
        _orderService = new OrderService(this);
        Bus.AttachReactor(new World.FeasibilityInvalidationReactor(() => _ordersView, () => _orderService.WorldSignature()));
        _productionService = new ProductionService(this);
        _masterSwitch = new MasterSwitchCoordinator(this);
        Processes = new ProcessSystem(() => _logSeq, modifiers: Modifiers);
        // 世界时间 = 逻辑时钟之上的**唯一时间门面**（几点了 / 在不在走）。
        // 它不推进时钟：推进口仍然只有 Pipeline.AdvanceOneTick，本类只在推进后通知它。
        Time = new WorldTime(Processes.Clock);
        // 总线即事件源：反应器排的"后续世界变化"由管线在第 ① 步统一执行（见 ProcessSystem.DrainEvents）。
        // 无反应器时 DrainForTick 恒返回空表 —— 接入总线不改变世界的任何一步。
        Processes.EventSource = Bus;
        Processes.SignalSink = (inst, reason, tick) => Bus.Publish(new WorldSignal(
            WorldSignalKind.ProcessChanged, reason, inst.Subject, inst.SubjectId, tick, _logSeq));
        // ④ 世界产出与 ⑤ 流入也登记进 tick 表 —— 于是"一个 tick 里发生什么"全在
        // Processes.Pipeline.Scheduler 一张表上可见，而不是一半在管线、一半在本类。
        // 产出每 tick 一次；流入每次宿主 Tick 调用一次（既有语义，由 RunCallBoundary 承载）。
        Processes.Pipeline.Scheduler.Register(new DelegateTickHandler(
            TickPhase.Production, 0, "worldProduction", (in TickContext _) => _productionService.TickStep()));
        Processes.Pipeline.Scheduler.Register(new DelegateTickHandler(
            TickPhase.Flow, 0, "worldFlow", (in TickContext _) => _productionService.FlushFlow()));
        InitializeDefault();
        RecordInitialStock();
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

    /// <summary>
    /// 把开局库存记进资源账本（`stock` 原因）。装配与重置共用这一个入口 ——
    /// 于是"仓库里这些液/罐是哪来的"永远只有一个答案，而不是"宿主编的"。
    /// </summary>
    private void RecordInitialStock()
    {
        var ledger = ResourceLedger;
        foreach (var (liquid, amount) in World.InitialStock)
            ledger.Record(0, Production.ResourceCauses.Stock, Production.ResourceKinds.Liquid,
                liquid, amount, from: "关卡配置", to: "仓库", resourceIdentity: liquid,
                fromEndpoint: ResourceEndpoint.ExternalSource("level.initial-stock"),
                toEndpoint: ProducerModule.Warehouse.OwnerIdentity is { } warehouseOwner
                    ? ResourceEndpoint.ForAccount(new RuleResourceAccount(
                        warehouseOwner, "production.warehouse.liquid-storage").AccountId)
                    : ResourceEndpoint.LegacyUnknown);
    }

    // ============================================================
    // 会话日志（唯一的写入口：本类所有"改世界"的意图方法都调它）
    // ============================================================

    private void Record(string op, string? name = null, string? orderType = null,
        IReadOnlyList<Requirement>? requirements = null, List<string>? baseMarks = null,
        List<string>? additionalMarks = null, List<string>? ban = null, WorkshopPayload? workshop = null,
        int? intA = null, int? intB = null, int? intC = null, double? doubleA = null, bool? flag = null,
        string? reason = null, bool preserveTime = false)
    {
        // 命令日志记录的是这次意图发生的时间。会先写事件历史的动作已在入口盖章，
        // 那些记录必须复用同一个时间，否则实时状态与回放状态会出现微秒级差异。
        if (!Replaying && !preserveTime) Now = DateTimeOffset.UtcNow;
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
            Flag = flag,
            Reason = reason
        });

        // 唯一的写入口 ⇒ 一处发布就覆盖了全部"意图级世界变更"。
        // tick 取逻辑时钟（判定只认 tick，不读墙钟）；Processes 尚未装配时按 0 记。
        Bus.Publish(new WorldSignal(
            WorldSignalKind.WorldChanged, op, name, null, Processes?.Clock.Tick ?? 0, _logSeq));
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
        // 清空订单表前，先结束**服务于这些订单**的过程：否则过程持有 subjectId 却指向不存在的订单
        // （世界不变量 PROCESS-ORDER-MISSING，随机属性测试抓到的形状）。
        // 走 CancelProcess 而不是直接删过程，是为了让"取消"这件事照常进日志与过程日志。
        foreach (var p in Processes.ActiveProcesses.ToList())
            if (p.Kind == ProcessKinds.OrderExecution) CancelProcess(p.Id, "订单表被清空");

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
        if (index != CurrentMaster && HasActiveProcess(ProcessKinds.OrderExecution))
            throw new DomainValidationException("订单执行中不能更改主从位置");
        CurrentMaster = index;

        // 主台与从台不能是同一台。`SetMaster`（事件轨）早就是这么做的，直接改下标这条路径必须有同样的一致性，
        // 否则世界会停在一个"从台就是主台"的状态上（世界不变量 SLAVE-EQUALS-MASTER）。
        if (index.HasValue && CurrentSlave == index.Value) CurrentSlave = null;

        Record(WorldOps.SetMasterIndex, intA: index);
    }

    /// <summary>
    /// 选择 B 类订单的临时工具位。从台不接收仓库流入，也必须在订单开始前为空；
    /// 因此占用中的工作台不能成为从台。越界或违反位置职责时抛异常。
    /// </summary>
    public void SetSlaveIndex(int? index)
    {
        if (index.HasValue && (index.Value < 0 || index.Value >= _workshops.Count))
            throw new ArgumentOutOfRangeException(nameof(index), "从台下标越界: " + index.Value + "（工作台数 " + _workshops.Count + "）");
        if (index != CurrentSlave && HasActiveProcess(ProcessKinds.OrderExecution))
            throw new DomainValidationException("订单执行中不能更改主从位置");
        if (index.HasValue && index.Value == CurrentMaster)
            throw new DomainValidationException("从台不能与主台相同（索引 " + index.Value + "）");
        if (index.HasValue && _workshops[index.Value].HasResources)
            throw new DomainValidationException(
                "从台必须是空的临时工具位（工作台 " + index.Value + " 仍有资源）");

        CurrentSlave = index;
        Record(WorldOps.SetSlaveIndex, intA: index);
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
        if (result.TryGetValue("success", out var success) && success is true)
            Record(WorldOps.SetMaster, intA: index, preserveTime: true);
        return result;
    }

    /// <summary>设为从工作台；拒绝把当前主台或有资源的工作台设为临时工具位。</summary>
    public Dictionary<string, object?> SetSlave(int index)
    {
        var result = _masterSwitch.SetSlave(index);
        if (result.TryGetValue("success", out var success) && success is true)
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
    public (bool Success, string? Error) UpdateWorkshopCapacity(int index, int? maxJars, int? maxLiquids)
    {
        var result = _workshopService.UpdateWorkshopCapacity(index, maxJars, maxLiquids);
        if (index >= 0 && index < _workshops.Count)
            Record(WorldOps.UpdateWorkshopCapacity, intA: index, intB: maxJars, intC: maxLiquids);
        return result;
    }

    /// <summary>
    /// 更换工作台的能力参数（标记 / 禁令）。语义边界见
    /// <see cref="Workshop.ReplaceCapability"/>：只换规则参数，不动罐子与游离液体。
    ///
    /// 为什么这个入口必须存在：`createWorkshop` 只能在装配期决定标记与禁令，
    /// 之后玩家就没有任何办法"把这张卡换成另一种台子"。而"换台/换卡"是玩法的一部分
    /// （demo 的关卡就是靠换台把禁令绕开）。它同时也是表达器的 B1：
    /// 没有它，页面只能"复制现有卡片"，无法让玩家自由挑标记。
    /// </summary>
    public void SetWorkshopCapability(int index, List<string>? baseMarks = null,
        List<string>? additionalMarks = null, List<string>? ban = null)
    {
        _workshopService.SetWorkshopCapability(index, baseMarks, additionalMarks, ban);
        Record(WorldOps.SetWorkshopCapability, intA: index,
            baseMarks: baseMarks, additionalMarks: additionalMarks, ban: ban);
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

    /// <summary>
    /// 按**模板名**派一张订单（模板来自 <see cref="OrderTemplates"/>，即世界配置）。
    /// 与 <see cref="CreateOrder"/> 的唯一区别是需求从模板实例化，其余入口完全一样。
    /// 名字不存在时返回 (false, 原因)，不抛异常 —— 名字来自外部输入是常态。
    /// </summary>
    public (bool Success, string? Error) CreateOrderFromTemplate(string templateName)
    {
        // 与 CreateOrder 走同一个记录入口：模板只负责产生需求，派单本身必须进命令日志，
        // 否则关卡开局订单会在回放/读档时整批消失。
        if (!OrderTemplates.TryGet(templateName, out var template))
            return (false, "没有这个订单模板: " + templateName);
        return CreateOrder(template.Instantiate(), template.Type);
    }

    public (bool Success, string? Error) CreateRandomOrder()
    {
        var result = _orderService.CreateRandomOrder();
        if (result.Success) Record(WorldOps.CreateRandomOrder);
        return result;
    }

    /// <summary>
    /// 调整订单的执行顺序（上移 / 下移一格）。
    ///
    /// 执行顺序是世界状态而不是界面排版：它决定谁先拿走稀缺的罐子与液体。
    /// 用 orderId 而不是下标 —— 交换会让两个订单的下标都变，Id 才稳定。
    /// 越界不报错、直接返回 false（已经在最前面不是错误，是事实），也就不往日志里写空意图。
    /// </summary>
    public bool MoveOrder(int orderId, int dir)
    {
        if (!_orderService.MoveOrder(orderId, dir)) return false;
        Record(WorldOps.MoveOrder, intA: orderId, intB: dir);
        return true;
    }

    public (bool Success, string? Error) DeleteOrder(int index)
    {
        var before = _orders.Count;
        var result = _orderService.DeleteOrder(index);
        if (_orders.Count < before) Record(WorldOps.DeleteOrder, intA: index);
        return result;
    }

    /// <summary>可行性分析（A 类只需主台；B 类需主从）；结果同时写入订单的 FeasibilityResult。</summary>
    public FeasibilityResultData? CheckFeasibility(int orderIndex)
    {
        var order = orderIndex >= 0 && orderIndex < _orders.Count ? _orders[orderIndex] : null;
        var before = order?.FeasibilityResult;
        var result = _orderService.CheckFeasibility(orderIndex);
        // 分析会把结果写进订单（属于状态变更）⇒ 记进日志，回放才能复现同一份分析结果
        if (order != null && !ReferenceEquals(order.FeasibilityResult, before))
            Record(WorldOps.CheckFeasibility, intA: orderIndex);
        return result;
    }

    /// <summary>对所有「待执行」订单做可行性分析（元素见 <see cref="AutoCheckResultDto"/>：index / orderId / result）。</summary>
    public List<AutoCheckResultDto> CheckAllFeasibility()
    {
        var before = _orders.Select(o => o.FeasibilityResult).ToArray();
        var result = _orderService.CheckAllFeasibility();
        var changed = false;
        for (var i = 0; i < _orders.Count; i++)
            if (!ReferenceEquals(before[i], _orders[i].FeasibilityResult))
            {
                changed = true;
                break;
            }
        if (changed) Record(WorldOps.CheckAllFeasibility);
        return result;
    }

    /// <summary>执行订单：返回 成功 / 原因 / 事件报告 / 是否发生转移 / 操作日志。</summary>
    public (bool Success, string? Reason, Dictionary<string, object?>? Event, bool TransferOccurred, List<ExecutionLogEntry>? Operations)
        ExecuteOrder(int orderIndex)
    {
        if (Outcome.IsOver)
            return (false, "本关已经结束，无法执行订单", null, false, null);
        if (HasActiveProcess(ProcessKinds.OrderExecution))
            return (false, "已有订单正在执行，不能同时启动同步执行", null, false, null);

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
            if (run != null) Record(WorldOps.ExecuteOrder, intA: orderIndex, preserveTime: true);
            return (false, error, null, false, null);
        }

        var advancer = new OrderProcessAdvancer(_orderService, session);
        Processes.AdvanceToCompletion(run!, advancer);   // 同步 drain（含结束过程与一次性收尾）

        // 失败也记：按设计"失败的尝试也留下代价"，它**改变过**世界
        Record(WorldOps.ExecuteOrder, intA: orderIndex, preserveTime: true);
        return advancer.Result ?? (advancer.Succeeded, advancer.FailureReason, null, false, null);
    }

    /// <summary>
    /// **异步轨（P2b，裁决选项 (c)）**：把订单执行起成一个**由 tick 驱动**的过程 ——
    /// 立即返回、**不推进世界**；随后每个世界 tick（默认每个回合）推进一个单位，
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
        if (Outcome.IsOver)
            return (false, "本关已经结束，无法开始新订单", null);

        // **互斥的另一半**：工作台正在迁徙（配置在变）时，不能基于它起新的订单计划。
        // 同步轨（ExecuteOrder）不加这条：它在一个调用里跑完，不存在与迁徙交错的可能。
        if (HasActiveProcess(ProcessKinds.WorkshopMigration))
            return (false, "工作台正在迁徙，暂不能开始订单", null);

        var order = orderIndex >= 0 && orderIndex < _orders.Count ? _orders[orderIndex] : null;

        // **同一张订单不允许两个并行过程**（世界不变量 PROCESS-DUPLICATE-FOR-SUBJECT）：
        // 两个过程会各自按自己的计划改同一个工作台，且各自终局一次 —— 结果无法解释。
        // 随机属性测试抓到的形状：对同一张订单连按两次「开始」。
        if (order != null)
        {
            var running = Processes.ActiveProcesses
                .FirstOrDefault(p => p.Kind == ProcessKinds.OrderExecution && p.SubjectId == order.Id);
            if (running != null)
                return (false, "订单 #" + order.Id + " 已经在执行中（过程 #" + running.Id + "）", running.Id);
        }
        if (HasActiveProcess(ProcessKinds.OrderExecution))
            return (false, "已有订单正在执行；等它收尾后再开始下一张订单", null);

        var (run, session, error) = BeginOrderProcess(orderIndex, order, deadlineTicks);
        if (session == null)
        {
            run?.Finish(false, error);
            if (run != null) Record(WorldOps.StartOrder, intA: orderIndex, intB: deadlineTicks, preserveTime: true);
            return (false, error, run?.Instance.Id);
        }

        // 异步轨挂上**守卫**：每个单位执行前检查"世界是否还容得下这个计划"。
        // 同步轨（ExecuteOrder）刻意不挂 —— 它在一个调用里跑完，世界不会中途变化。
        var advancer = new OrderProcessAdvancer(_orderService, session, () => CheckOrderGuard(session));
        if (session.Stage == OrderExecutionStage.Done)
        {
            // 已经满足交付条件时没有实现步骤，不借一个 tick 来“完成交付”。
            // 过程仍立即写入 started/succeeded，并进入最近过程以便审计。
            Processes.AdvanceToCompletion(run!, advancer);
        }
        else
        {
            Processes.AttachAdvancer(run!, advancer);
        }
        Record(WorldOps.StartOrder, intA: orderIndex, intB: deadlineTicks, preserveTime: true);
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
    /// <summary>
    /// **把"换主台"起成一个过程**（`ProcessKinds.WorkshopMigration`）：一 tick 一段地重组并搬运资源。
    ///
    /// <para>与 <see cref="SetMaster"/> 的分工，是本步的**全部要点**：</para>
    /// <list type="bullet">
    /// <item><see cref="SetMaster"/> 保留为**同步瞬时**的原子切换（快照 + 全做 + 出错全回滚）。
    /// 命令、回放、测试都还走它，行为一行未变。</item>
    /// <item>本方法把同一份搬运逻辑**按 tick 拆开做**：先重组（按新台标记/禁令改形倒液），再转移（剩余资源搬过去），
    /// 两段各占一个过程单位。它走的是与订单执行**同一套**过程机制 —— 有 started/progressed/blocked/resumed/finished，
    /// 有 grace 宽限，可被 `cancelProcess` 取消，结果进过程日志。</item>
    /// </list>
    ///
    /// <para><b>它不是订单</b>：没有委托方、没有交付物、不产生 `orderEvent`。它是**使用者主动发起的资源重组**，
    /// 所以资源的变化是主动的（玩家决定换台，于是罐子被改形、被搬走），而不是某个订单执行的副作用。</para>
    ///
    /// <para>什么时候不走过程：旧主台**没有任何资源**（`Jars.Count == 0` 且液体全为 0）。那时没有可搬运的东西，
    /// 起一个过程只是让玩家白等两个 tick —— 直接改指针，与既有语义一致。</para>
    /// </summary>
    /// <param name="index">目标工作台下标。</param>
    /// <param name="deadlineTicks">可选的逻辑 tick 期限（超期由过程系统判定失败）。</param>
    public (bool Started, string? Error, int? ProcessId) BeginSwitch(int index, int? deadlineTicks = null)
    {
        if (!Replaying) Now = DateTimeOffset.UtcNow;
        if (index < 0 || index >= _workshops.Count) return (false, "无效工作台索引", null);
        if (CurrentMaster == index) return (false, "已经是当前主台", null);

        // **互斥**：迁徙改的是工作台配置，而订单执行以工作台为前提。
        // 有订单在跑时开始迁徙，等于当着使用者的面把底座换掉 —— 那份订单的计划是基于旧配置算的。
        // 刻意**不**做"阻断订单 → 迁徙 → 恢复订单"：恢复时手里的计划仍然基于旧配置，
        // 那正是可行性缓存失效要防的那类错误。所以这里直接拒绝，让玩家自己决定先后。
        if (HasActiveProcess(ProcessKinds.OrderExecution))
            return (false, "有订单正在执行，工作台配置不能此刻更新（先让订单结束或取消它）", null);
        if (HasActiveProcess(ProcessKinds.WorkshopMigration))
            return (false, "已有一次迁徙在进行中", null);

        Workshop? old = CurrentMaster.HasValue ? _workshops[CurrentMaster.Value] : null;
        var next = _workshops[index];
        bool hasResources = old != null && (old.Jars.Count > 0 || old.LiquidStorage.Values.Any(v => v > 0));

        if (old == null || !hasResources)
        {
            // 没有可搬运的东西：不起过程，直接切。
            var r = _masterSwitch.SetMaster(index);
            Record(WorldOps.SetMaster, intA: index, preserveTime: true);
            return (r.TryGetValue("success", out var ok) && ok is true, r.TryGetValue("reason", out var why) ? why as string : null, null);
        }

        var switchEvent = new WorkshopSwitchEvent(old, next, JarPool,
            ProducerModule.Warehouse.CountDiscardedJars,
            ProducerModule.Warehouse.CountDiscardedLiquids,
            ResourceLedger, () => Processes.Clock.Tick);
        switchEvent.Begin();

        var definition = new ProcessDefinition(ProcessKinds.WorkshopMigration,
            "迁徙 " + old.Name + " → " + next.Name, index,
            deadlineTicks: deadlineTicks, graceTicks: 1);
        var run = Processes.Begin(definition);
        Processes.AttachAdvancer(run,
            new SwitchProcessAdvancer(switchEvent, index, CommitSwitch));
        Record(WorldOps.StartSwitch, intA: index, intB: deadlineTicks, preserveTime: true);
        return (true, null, run.Instance.Id);
    }

    /// <summary>
    /// 是否有一类**仍在进行中**的过程。
    ///
    /// 用于「迁徙 ⟂ 订单」这条互斥规则。依据是**配置与使用者的关系**：
    /// 迁徙更新的是工作台配置，而任何订单执行都以工作台为前提 ——
    /// 配置在变的时候，正在跑的订单就是"基于旧配置的计划"。
    /// </summary>
    private bool HasActiveProcess(string kind)
    {
        foreach (var p in Processes.ActiveProcesses)
            if (string.Equals(p.Kind, kind, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>迁徙过程的**一次性收尾**：写 `workshop_switch` 事件历史并切换主台指针（幂等由调用方保证）。</summary>
    private void CommitSwitch(WorkshopSwitchEvent switchEvent, int index)
    {
        var ops = switchEvent.Operations
            .Select(o => (object)new Dictionary<string, object?>
            {
                ["action"] = o.Action,
                ["description"] = o.Description
            })
            .ToList();

        AddWorkshopSwitchEvent(switchEvent.OldMaster.Name, switchEvent.NewMaster.Name, switchEvent.CaseType, ops);

        CurrentMaster = index;
        if (CurrentSlave == index) CurrentSlave = null;
    }

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
        if (result.Cancelled) Record(WorldOps.CancelProcess, intA: processId, reason: reason);
        return result;
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

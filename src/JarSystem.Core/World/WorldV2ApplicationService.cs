using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JarSystem.Core.Production;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Execution;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 的外部应用入口。
///
/// Host 管世界实例的生命周期与互斥；ApplicationService 管外部命令/查询的语义，
/// 并把 Core 内部对象转换成脱离式 DTO。调用方不得直接接触 WorldV2Context。
/// 本服务不拥有 Host；Host 的创建与释放由组合根负责。
/// </summary>
public sealed class WorldV2ApplicationService
{
    private readonly WorldV2HostService _host;
    private readonly object _lifecycleSync = new();
    private WorldV2Options? _lastOptions;
    private readonly Dictionary<string, WorldV2Options> _worldOptionsByInstanceId =
        new(StringComparer.Ordinal);
    private readonly List<string> _worldOptionsOrder = new();
    private const int MaxRememberedWorldOptions = 8;

    public WorldV2ApplicationService(WorldV2HostService host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>创建或替换世界。普通新世界的实例身份由 Host 边界生成。</summary>
    public void CreateOrReplaceWorld(WorldV2Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lifecycleSync)
        {
            var snapshot = CloneOptions(options);
            _host.Replace(snapshot);
            _lastOptions = snapshot;
            RememberWorldOptions(GetWorldInstanceId(), snapshot);
        }
    }

    /// <summary>使用宿主已持久化的身份创建世界，供确定性重建与重放使用。</summary>
    public void CreateOrReplaceWorld(WorldV2Options options, WorldInstanceId worldInstanceId)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lifecycleSync)
        {
            var snapshot = CloneOptions(options);
            _host.Replace(snapshot, worldInstanceId);
            _lastOptions = snapshot;
            RememberWorldOptions(worldInstanceId, snapshot);
        }
    }

    /// <summary>使用完整 World/Owner 身份映射创建世界，供宿主保存和确定性重建。</summary>
    public void CreateOrReplaceWorld(
        WorldV2Options options,
        WorldInstanceId worldInstanceId,
        WorldV2OwnerIdentityMap ownerIdentities)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lifecycleSync)
        {
            var snapshot = CloneOptions(options);
            _host.Replace(snapshot, worldInstanceId, ownerIdentities);
            _lastOptions = snapshot;
            RememberWorldOptions(worldInstanceId, snapshot);
        }
    }

    /// <summary>读取当前世界已注入的稳定 Owner 身份映射。</summary>
    public WorldV2OwnerIdentityMap? GetOwnerIdentityMap()
    {
        WorldV2OwnerIdentityMap? identities = null;
        _host.ExecuteExclusive(context => identities = context.OwnerIdentities);
        return identities;
    }

    /// <summary>读取当前世界的稳定实例身份。</summary>
    public WorldInstanceId GetWorldInstanceId()
    {
        WorldInstanceId? identity = null;
        _host.ExecuteExclusive(context => identity = context.WorldInstanceId);
        return identity ?? throw new InvalidOperationException(
            "当前世界未配置 WorldInstanceId；该兼容工厂路径不能用于持久化身份。");
    }

    /// <summary>
    /// 捕获当前世界的完整配置快照，包括 Host 分配的 ProducerInstanceId / OwnedInstanceId。
    /// 必须与执行检查点一起持久化；仅保存定义配置不足以重建稳定 OwnerIdentity。
    /// </summary>
    public WorldV2Options CaptureWorldOptions()
    {
        lock (_lifecycleSync)
        {
            var options = _lastOptions
                ?? throw new InvalidOperationException("当前 ApplicationService 尚未装配 World 配置。");
            return CloneOptions(options);
        }
    }

    /// <summary>设置世界流速倍率；1000 表示 1 倍，0 表示逻辑时间停止增长。</summary>
    public void SetWorldSpeedRatio(double ratio)
    {
        _host.ExecuteExclusive(context => context.Runtime.Clock.SetSpeedRatio(ratio));
    }

    /// <summary>
    /// 将 Space 原石解析器已经完成的空罐批次导入 Jar 中央仓库。
    /// 这是离散产出入口，不经过时间型 Producer；批次 ID 通过资源账本操作收据持久去重。
    /// </summary>
    public WorldV2ProducedJarImportResultDto ImportProducedJars(
        string operationId,
        IReadOnlyDictionary<string, int> jarCounts)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128 ||
            operationId.Contains('|'))
            throw new ArgumentException("外部 Jar 批次 ID 必须是 1 到 128 个字符且不能包含 |", nameof(operationId));
        if (jarCounts == null)
            throw new ArgumentNullException(nameof(jarCounts));

        var requested = jarCounts.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (requested.Count == 0 ||
            requested.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0))
            throw new DomainValidationException("外部 Jar 批次必须包含有效形状和正整数数量");

        var submittedCount = requested.Values.Aggregate(0L, (sum, amount) => checked(sum + amount));
        if (submittedCount > int.MaxValue)
            throw new DomainValidationException("单个外部 Jar 批次数量超出可表示范围");

        var canonicalPayload = string.Join("\n", requested
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "\t" + pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var payloadFingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();
        var operationPrefix = "space-produced-jar-import|" + operationId + "|";
        var ledgerOperationId = operationPrefix + payloadFingerprint;

        WorldV2ProducedJarImportResultDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            foreach (var shape in requested.Keys)
                if (!context.Shapes.Contains(shape, StringComparer.Ordinal))
                    throw new DomainValidationException("外部 Jar 批次包含当前世界未知形状: " + shape);

            var priorOperations = context.ResourceLedger.Capture().OperationIds
                .Where(item => item.StartsWith(operationPrefix, StringComparison.Ordinal))
                .ToArray();
            if (priorOperations.Length > 0)
            {
                if (priorOperations.Length != 1 ||
                    !string.Equals(priorOperations[0], ledgerOperationId, StringComparison.Ordinal))
                    throw new DomainValidationException("外部 Jar 批次 ID 已用于不同的产出内容: " + operationId);
                result = new WorldV2ProducedJarImportResultDto(true, 0, 0, 0);
                return;
            }

            var remainingWarehouseSlots = Math.Max(
                0, Constants.WAREHOUSE_MAX_JARS - context.Resources.Inventory.JarCount);
            var storedByShape = new Dictionary<string, int>(StringComparer.Ordinal);
            var discardedByShape = new Dictionary<string, int>(StringComparer.Ordinal);
            var remainingSlots = remainingWarehouseSlots;
            foreach (var pair in requested.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var stored = Math.Min(pair.Value, remainingSlots);
                var discarded = pair.Value - stored;
                if (stored > 0) storedByShape.Add(pair.Key, stored);
                if (discarded > 0) discardedByShape.Add(pair.Key, discarded);
                remainingSlots -= stored;
            }

            var vocabulary = new Vocabulary(context.Shapes, context.Liquids);
            // JarId is assigned at entity creation, not deferred until the first Workshop admission.
            // Both stored and immediately discarded outputs are therefore individually auditable.
            var storedJars = storedByShape
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => Enumerable.Range(0, pair.Value)
                    .Select(_ => new Jar(pair.Key, id: context.Resources.Inventory.AllocateJarId(), vocabulary: vocabulary)))
                .ToArray();
            var discardedJars = discardedByShape
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => Enumerable.Range(0, pair.Value)
                    .Select(_ => new Jar(pair.Key, id: context.Roster.Master!.AllocateJarId(), vocabulary: vocabulary)))
                .ToArray();

            var mutations = storedJars.Select(jar => new ResourceLedgerMutation(
                    ResourceCauses.Production, ResourceKinds.Jar, jar.Shape, 1,
                    From: "Space 原石解析器", To: "Jar 中央仓库",
                    ResourceIdentity: jar.Id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Concat(discardedJars.Select(jar => new ResourceLedgerMutation(
                    ResourceCauses.Discard, ResourceKinds.Jar, jar.Shape, 1,
                    From: "Space 原石解析器", To: "容量溢出",
                    Reason: "中央仓库容量已满",
                    ResourceIdentity: jar.Id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                .ToArray();

            if (!context.ResourceLedger.RecordBatchOnce(
                ledgerOperationId, context.Runtime.LedgerTick, mutations))
            {
                result = new WorldV2ProducedJarImportResultDto(true, 0, 0, 0);
                return;
            }

            // 账本批次先提交，随后仅执行已预构造且不再需要校验的仓库写入。
            context.Resources.Inventory.StoreJars(storedJars);
            if (discardedByShape.Values.Sum() > 0)
                context.Resources.Inventory.CountDiscardedJars(discardedByShape.Values.Sum());

            var storedCount = storedByShape.Values.Sum();
            var discardedCount = discardedByShape.Values.Sum();
            result = new WorldV2ProducedJarImportResultDto(
                false, (int)submittedCount, storedCount, discardedCount);
        });
        return result!;
    }

    /// <summary>
    /// 原子更新一个已配置生产者的完整形状速率表。
    /// 形状拓扑保持不变，因此生产线小数余数与检查点配置指纹继续有效。
    /// 调用在 Host 独占区内执行，与世界时间推进串行。
    /// </summary>
    public void UpdateProducerRates(
        string producerName,
        IReadOnlyDictionary<string, double> rates)
    {
        if (string.IsNullOrWhiteSpace(producerName))
            throw new ArgumentException("生产者名称不能为空", nameof(producerName));
        if (rates == null)
            throw new ArgumentNullException(nameof(rates));

        // IReadOnlyDictionary 不保证底层不可变；在进入 Host 回调前复制调用方数据。
        var requested = rates.ToDictionary(
            pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (requested.Count == 0)
            throw new DomainValidationException("生产者速率更新不能为空");

        _host.ExecuteExclusive(context =>
        {
            var module = context.Resources.Producers;
            if (!module.Producers.TryGetValue(producerName, out var producer))
                throw new DomainValidationException("没有这个生产者: " + producerName);

            if (!producer.Rates.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(requested.Keys))
                throw new DomainValidationException(
                    "速率更新必须提供与生产者配置完全一致的形状集合: " + producerName);

            foreach (var pair in requested)
            {
                if (!module.Generators.ContainsKey(pair.Key))
                    throw new DomainValidationException("没有这个形状: " + pair.Key);
                if (double.IsNaN(pair.Value) || double.IsInfinity(pair.Value) ||
                    pair.Value < 0 || pair.Value > Constants.MAX_PRODUCER_RATE)
                    throw new DomainValidationException(
                        $"速率必须是 0 到 {Constants.MAX_PRODUCER_RATE} 之间的有限数，收到: {pair.Value}");
            }

            var currentActive = producer.Rates.Values.Count(value => value > 0);
            var requestedActive = requested.Values.Count(value => value > 0);
            var projectedSlots = module.UsedSlots - currentActive + requestedActive;
            if (module.ProducerSlots > 0 && projectedSlots > module.ProducerSlots)
                throw new DomainValidationException(
                    $"产线槽不足：当前更新需要 {projectedSlots} 个启用槽，容量为 {module.ProducerSlots}");

            // 先关闭不再需要的产线释放槽位，再启用新产线；前面已完整预检，
            // 因此下面的逐项更新不会因中途槽位不足而留下半套速率。
            foreach (var pair in requested.Where(pair =>
                producer.Rates[pair.Key] > 0 && pair.Value == 0))
                module.UpdateRate(producerName, pair.Key, pair.Value);

            foreach (var pair in requested.Where(pair =>
                !(producer.Rates[pair.Key] > 0 && pair.Value == 0)))
                module.UpdateRate(producerName, pair.Key, pair.Value);
        });
    }

    /// <summary>暂停世界时间。暂停期间 AdvanceWorld 不推进世界时钟、生产、冷却或执行。</summary>
    public void PauseWorld()
    {
        _host.ExecuteExclusive(context => context.Runtime.Clock.Pause());
    }

    /// <summary>恢复世界时间推进。</summary>
    public void ResumeWorld()
    {
        _host.ExecuteExclusive(context => context.Runtime.Clock.Resume());
    }

    /// <summary>
    /// 推进世界并运行一轮调度。deltaWorldTimeMilli 的 1000 表示 1 个世界时间单位。
    /// 调度顺序由 WorldOrderScheduler 决定；这里仅将结果复制成 DTO。
    /// </summary>
    public WorldV2AdvanceResultDto AdvanceWorld(long deltaWorldTimeMilli)
    {
        WorldV2AdvanceResultDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            var step = context.Scheduler.AdvanceWorldAndSchedule(deltaWorldTimeMilli);
            var resource = step.WorldStep.Resources;
            result = new WorldV2AdvanceResultDto(
                step.WorldStep.LogicalTimeDeltaMilli,
                resource.ProducedJarCount,
                CopyList(resource.ProducedLiquids.Select(x => new WorldV2LiquidAmountDto(x.Liquid, x.Amount))),
                CopyList(resource.LiquidProductionReceipts.Select(x => new WorldV2LiquidProductionReceiptDto(
                    x.Liquid, x.ProducedAmount, x.AcceptedAmount, x.DiscardedAmount))),
                resource.FlowedJarCount,
                CopyDictionary(resource.FlowedLiquids),
                CopyList(step.Scheduling.StartedDemandIds),
                step.Scheduling.VisitedDemandCount,
                context.Runtime.IsFaulted,
                context.Runtime.FaultReason);
        });
        return result!;
    }

    /// <summary>
    /// 读取资源账本的增量事实。Dropped/LastSeq 用于识别有界账本裁剪造成的事件缺口。
    /// </summary>
    public WorldV2ResourceLedgerDto GetResourceLedger(long afterSeq = 0)
    {
        if (afterSeq < 0) throw new ArgumentOutOfRangeException(nameof(afterSeq));
        WorldV2ResourceLedgerDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            var ledger = context.ResourceLedger;
            var events = ledger.Since(afterSeq).Select(item => new WorldV2ResourceEventDto(
                item.Seq, item.Tick, item.Cause, item.Kind, item.Name, item.Amount,
                item.From, item.To, item.Reason, item.ResourceIdentity, item.OperationId,
                item.FromAccountId, item.ToAccountId)).ToArray();
            result = new WorldV2ResourceLedgerDto(
                ledger.LastSeq, ledger.Dropped, ledger.Retained, CopyList(events));
        });
        return result!;
    }

    /// <summary>
    /// 读取尚未由 Space 确认的订单成功周期事件。读取不移除事件，重复读取安全。
    /// </summary>
    public IReadOnlyList<OrderCycleCompletedEvent> GetPendingOrderCycleEvents()
    {
        IReadOnlyList<OrderCycleCompletedEvent>? events = null;
        _host.ExecuteExclusive(context =>
            events = CopyList(context.DemandBoard.Outbox.Pending));
        return events!;
    }

    /// <summary>
    /// Space 在效果应用及处理身份持久化成功后确认事件。重复/未知确认返回 false。
    /// </summary>
    public bool AcknowledgeOrderCycleEvent(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            throw new ArgumentException("EventId 不能为空", nameof(eventId));
        var acknowledged = false;
        _host.ExecuteExclusive(context =>
            acknowledged = context.DemandBoard.Outbox.Acknowledge(eventId));
        return acknowledged;
    }

    /// <summary>
    /// 捕获当前活动执行的资源检查点。返回值为脱离 Context 的纯值快照，
    /// 调用方不能据此绕过 Host 直接修改世界。
    /// </summary>
    public WorldV2ExecutionResourceCheckpoint CaptureExecutionResourceCheckpoint()
    {
        WorldV2ExecutionResourceCheckpoint? checkpoint = null;
        _host.ExecuteExclusive(context => checkpoint = context.CaptureExecutionResourceCheckpoint());
        return checkpoint!;
    }

    /// <summary>
    /// 使用最近一次成功装配的配置重建独立 Context，并复用检查点的 WorldInstanceId。
    /// 恢复完成后才由 Host 原子替换当前世界；旧版无身份检查点必须使用显式迁移身份重载。
    /// </summary>
    public void RestoreExecutionResourceCheckpoint(WorldV2ExecutionResourceCheckpoint checkpoint)
    {
        if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
        lock (_lifecycleSync)
        {
            var worldInstanceId = checkpoint.WorldInstanceId
                ?? throw new InvalidOperationException(
                    "旧检查点缺少 WorldInstanceId；必须通过显式迁移身份重载恢复。");
            if (!_worldOptionsByInstanceId.TryGetValue(worldInstanceId.Value, out var options))
                throw new InvalidOperationException(
                    "当前 ApplicationService 没有该 WorldInstanceId 对应的配置快照；恢复时必须提供与检查点配套的 WorldOptions。");
            RestoreExecutionResourceCheckpointCore(options, checkpoint, null);
        }
    }

    /// <summary>
    /// 以新配置装配独立 Context，完整恢复检查点后原子替换 Host 当前世界。
    /// 失败时当前世界保持运行，不会暴露部分恢复状态。
    /// </summary>
    public void RestoreExecutionResourceCheckpoint(
        WorldV2Options options,
        WorldV2ExecutionResourceCheckpoint checkpoint,
        WorldInstanceId? legacyMigrationWorldInstanceId = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
        lock (_lifecycleSync)
            RestoreExecutionResourceCheckpointCore(options, checkpoint, legacyMigrationWorldInstanceId);
    }

    /// <summary>
    /// 从独立 JSON 文档恢复世界。解码与 DTO 校验先于 Host 发布操作；
    /// Host 仅在新 Context 完整恢复后原子替换当前实例。
    /// </summary>
    public void RestoreExecutionResourceCheckpointJson(
        WorldV2Options options,
        string json,
        WorldInstanceId? legacyMigrationWorldInstanceId = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        var checkpoint = WorldV2CheckpointJsonCodec.DecodeExecutionResourceCheckpoint(
            json, legacyMigrationWorldInstanceId);
        lock (_lifecycleSync)
            RestoreExecutionResourceCheckpointCore(options, checkpoint, legacyMigrationWorldInstanceId);
    }

    private void RestoreExecutionResourceCheckpointCore(
        WorldV2Options options,
        WorldV2ExecutionResourceCheckpoint checkpoint,
        WorldInstanceId? legacyMigrationWorldInstanceId)
    {
        var snapshot = CloneOptions(options);
        _host.RestoreExecutionResourceCheckpoint(
            snapshot, checkpoint, legacyMigrationWorldInstanceId);
        _lastOptions = snapshot;
        RememberWorldOptions(GetWorldInstanceId(), snapshot);
    }

    private void RememberWorldOptions(WorldInstanceId worldInstanceId, WorldV2Options options)
    {
        if (!worldInstanceId.IsValid)
            throw new ArgumentException("不能保存无效 WorldInstanceId 的配置快照。", nameof(worldInstanceId));
        var key = worldInstanceId.Value;
        _worldOptionsByInstanceId[key] = CloneOptions(options);
        _worldOptionsOrder.Remove(key);
        _worldOptionsOrder.Add(key);
        while (_worldOptionsOrder.Count > MaxRememberedWorldOptions)
        {
            var expired = _worldOptionsOrder[0];
            _worldOptionsOrder.RemoveAt(0);
            _worldOptionsByInstanceId.Remove(expired);
        }
    }

    private static WorldV2Options CloneOptions(WorldV2Options options)
    {
        var json = JsonSerializer.Serialize(options);
        return JsonSerializer.Deserialize<WorldV2Options>(json)
            ?? throw new InvalidOperationException("WorldV2Options snapshot serialization returned null.");
    }

    /// <summary>添加罐子订单。输入仅包含玩法数据，不接受外部构造的领域对象。</summary>
    public int AddJarDemand(WorldV2AddJarDemandRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Requirements == null) throw new ArgumentException("订单需求不能为空", nameof(request));
        if (request.OrderDefinitionKey != null && string.IsNullOrWhiteSpace(request.OrderDefinitionKey))
            throw new ArgumentException("订单定义 key 不能是空白字符串", nameof(request));

        // 在进入 Host 独占区之前复制调用方集合。IReadOnlyList 只限制接口，
        // 不保证底层集合不可变；不能让外部并发修改影响 Host 回调中的领域对象构造。
        var requirementInputs = request.Requirements.Select(item =>
        {
            if (item == null) throw new ArgumentException("订单需求项不能为 null", nameof(request));
            return new WorldV2RequirementInput(item.Shape, item.Liquid);
        }).ToArray();
        var type = request.Type;
        var area = request.Area;
        var cooldownDuration = request.CooldownDurationLogicalMilli;
        int demandId = 0;

        _host.ExecuteExclusive(context =>
        {
            var vocabulary = new Vocabulary(
                context.Resources.Producers.Generators.Keys,
                context.Resources.Inventory.LiquidWarehouse.Keys);
            var requirements = requirementInputs.Select(item =>
                new Requirement { Shape = item.Shape, Liquid = item.Liquid }).ToList();

            var specification = new JarOrderSpecification(
                requirements, type, vocabulary: vocabulary);
            var demand = new OrderDemand(
                specification, cooldownDuration, request.OrderDefinitionKey, context.DemandIdGenerator);
            context.DemandBoard.AddDemand(demand, area);
            demandId = demand.Id;
        });

        return demandId;
    }

    /// <summary>
    /// 从当前世界配置的不可变模板实例化一张订单。调用方只提交模板键，
    /// 需求列表和订单类型由 Core 的 OrderTemplateLibrary 唯一提供。
    /// </summary>
    public int AddJarDemandFromTemplate(
        string templateName,
        DemandAreaKind area,
        long cooldownDurationLogicalMilli,
        string? orderDefinitionKey = null)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            throw new ArgumentException("订单模板名不能为空", nameof(templateName));
        if (cooldownDurationLogicalMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(cooldownDurationLogicalMilli));
        if (!Enum.IsDefined(area))
            throw new ArgumentOutOfRangeException(nameof(area));
        if (orderDefinitionKey != null && string.IsNullOrWhiteSpace(orderDefinitionKey))
            throw new ArgumentException("订单定义 key 不能是空白字符串", nameof(orderDefinitionKey));

        int demandId = 0;
        _host.ExecuteExclusive(context =>
        {
            if (!context.OrderTemplates.TryGet(templateName, out var template))
                throw new KeyNotFoundException("当前 World v2 未配置订单模板: " + templateName);

            var vocabulary = new Vocabulary(context.Shapes, context.Liquids);
            var specification = new JarOrderSpecification(
                template.Instantiate(), template.Type, vocabulary: vocabulary);
            var demand = new OrderDemand(
                specification, cooldownDurationLogicalMilli, orderDefinitionKey, context.DemandIdGenerator);
            context.DemandBoard.AddDemand(demand, area);
            demandId = demand.Id;
        });

        return demandId;
    }

    /// <summary>将已登记订单移动到主动区或被动区；返回 false 表示订单不存在。</summary>
    public bool MoveDemand(int demandId, DemandAreaKind targetArea)
    {
        var moved = false;
        _host.ExecuteExclusive(context => moved = context.DemandBoard.MoveToArea(demandId, targetArea));
        return moved;
    }

    /// <summary>显式启动主动区域订单。被动订单不会通过此入口启动。</summary>
    public WorldV2StartDemandResultDto StartActiveDemand(int demandId)
    {
        WorldV2StartDemandResultDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            var start = context.Scheduler.TryStartActiveDemand(demandId);
            result = new WorldV2StartDemandResultDto(start.Success, start.Reason);
        });
        return result!;
    }

    /// <summary>
    /// 请求替换主/从工作台。订单中断由 World v2 Runtime 负责；当前应用服务没有外部订单系统，
    /// 因此传入无副作用适配器，Runtime 随后仍会中断它自己持有的所有执行会话。
    /// </summary>
    public WorldV2WorkshopReplacementResultDto BeginWorkshopReplacement(
        WorkshopRole role, string targetWorkshopName, long phaseDurationLogicalMilli)
    {
        if (!Enum.IsDefined(role))
            return new WorldV2WorkshopReplacementResultDto(false, "工作台岗位类型无效", null);
        if (string.IsNullOrWhiteSpace(targetWorkshopName))
            return new WorldV2WorkshopReplacementResultDto(false, "目标工作台名称不能为空", null);
        if (phaseDurationLogicalMilli <= 0)
            return new WorldV2WorkshopReplacementResultDto(false, "替换阶段时长必须大于零", null);

        WorldV2WorkshopReplacementResultDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            var target = context.Roster.Find(targetWorkshopName);
            if (target == null)
            {
                result = new WorldV2WorkshopReplacementResultDto(
                    false, "找不到目标工作台: " + targetWorkshopName, null);
                return;
            }

            try
            {
                var begin = context.Runtime.BeginReplacement(
                    role, target, NoExternalOrderInterruption.Instance, phaseDurationLogicalMilli);
                result = new WorldV2WorkshopReplacementResultDto(
                    begin.Success, begin.Error, ToReplacementDto(context.Runtime.ActiveReplacement));
            }
            catch (ArgumentException ex)
            {
                result = new WorldV2WorkshopReplacementResultDto(false, ex.Message, null);
            }
            catch (InvalidOperationException ex)
            {
                // 构造阶段的拒绝发生在替换流程启动之前；不能把 Runtime 中上一次替换的状态
                // 错误地作为本次请求的结果返回。
                result = new WorldV2WorkshopReplacementResultDto(false, ex.Message, null);
            }
        });
        return result!;
    }


    /// <summary>取消尚未完成的工作台替换；已提交的订单中断不会被撤销。</summary>
    public WorldV2WorkshopReplacementResultDto CancelWorkshopReplacement(
        string reason = "工作台替换已取消")
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new WorldV2WorkshopReplacementResultDto(false, "取消原因不能为空", null);

        WorldV2WorkshopReplacementResultDto? result = null;
        _host.ExecuteExclusive(context =>
        {
            var cancel = context.Runtime.CancelActiveReplacement(reason);
            result = new WorldV2WorkshopReplacementResultDto(
                cancel.Success,
                cancel.Error,
                ToReplacementDto(context.Runtime.ActiveReplacement));
        });
        return result!;
    }

    /// <summary>
    /// 捕获当前世界的展示快照。所有集合和元素均为复制值，不包含 Core 可变实体引用。
    /// 该 DTO 不是 Solver 使用的 WorldSnapshot。
    /// </summary>
    public WorldV2SnapshotDto GetWorldSnapshot()
    {
        WorldV2SnapshotDto? snapshot = null;
        _host.ExecuteExclusive(context =>
        {
            var clock = context.Runtime.Clock;
            var master = context.Roster.Master;
            var slave = context.Roster.Slave;
            var demands = context.DemandBoard.AllDemands.Select(demand =>
            {
                var area = context.DemandBoard.Passive.Demands.Any(x => x.Id == demand.Id)
                    ? DemandAreaKind.Passive
                    : DemandAreaKind.Active;
                var requirements = demand.Specification is JarOrderSpecification jarSpec
                    ? jarSpec.Requirements.Select(x => new WorldV2RequirementDto(x.Shape, x.Liquid)).ToArray()
                    : Array.Empty<WorldV2RequirementDto>();
                var type = demand.Specification is JarOrderSpecification typed
                    ? typed.Type
                    : demand.Specification.GetType().Name;
                return new WorldV2DemandDto(
                    demand.Id, area, type, CopyList(requirements),
                    demand.CooldownDurationLogicalMilli,
                    demand.CooldownRemainingLogicalMilli,
                    demand.IsCoolingDown,
                    demand.OrderDefinitionKey,
                    demand.CycleSequence);
            }).ToArray();

            var sessions = context.Runtime.ActiveExecutionSessions.Select(session =>
                new WorldV2ExecutionSessionDto(
                    session.DemandId, session.Stage.ToString(),
                    session.UnitDurationLogicalMilli, session.UnitElapsedLogicalMilli,
                    session.CompletedMasterSteps, session.CompletedSlaveSteps,
                    session.IsTerminal, session.Succeeded, session.Reason)).ToArray();

            snapshot = new WorldV2SnapshotDto(
                clock.WorldTimeMilli,
                clock.LogicalTimeMilli,
                clock.SpeedRatioMilli,
                clock.IsPaused,
                context.Runtime.IsFaulted,
                context.Runtime.FaultReason,
                CopyList(context.Resources.Inventory.Jars.Select(ToJarDto)),
                CopyDictionary(context.Resources.Inventory.LiquidWarehouse),
                ToWorkshopDto(master),
                ToWorkshopDto(slave),
                CopyList(demands),
                CopyList(sessions),
                context.Reservations.Count,
                ToReplacementDto(context.Runtime.ActiveReplacement),
                CopyList(context.Roster.Workshops
                    .Where(context.Roster.IsSpare)
                    .Select(workshop => new WorldV2ReplacementTargetDto(workshop.Name, workshop.HasResources))),
                ToShipLiquidInventoryDto(context.ShipLiquidInventory));
        });
        return snapshot!;
    }

    private static WorldV2WorkshopReplacementDto? ToReplacementDto(
        WorkshopReplacementProcess? process)
    {
        if (process == null) return null;
        return new WorldV2WorkshopReplacementDto(
            process.Role,
            process.OldWorkshopName,
            process.TargetWorkshopName,
            process.PhaseDurationLogicalMilli,
            process.PhaseElapsedLogicalMilli,
            process.IsFinished,
            process.Succeeded,
            process.InterruptionCommitted,
            process.InterruptionOutcomeUncertain,
            process.CompletedPhases,
            process.FailureReason,
            process.CommitWarning);
    }

    private sealed class NoExternalOrderInterruption : IOrderInterruptionPort
    {
        public static NoExternalOrderInterruption Instance { get; } = new();
        public (bool Success, string? Error) InterruptForWorkshopReplacement() => (true, null);
    }

    private static WorldV2ShipLiquidInventoryDto? ToShipLiquidInventoryDto(
        ShipLiquidInventory? inventory)
    {
        if (inventory == null) return null;
        return new WorldV2ShipLiquidInventoryDto(
            inventory.Capacity, inventory.UsedCapacity, inventory.AvailableCapacity,
            CopyDictionary(inventory.Balances));
    }

    private static WorldV2WorkshopDto? ToWorkshopDto(Workshop? workshop)
    {
        if (workshop == null) return null;
        return new WorldV2WorkshopDto(
            workshop.Name,
            workshop.MaxJars,
            workshop.MaxLiquids,
            CopyList(workshop.BaseMarks),
            CopyList(workshop.AdditionalMarks),
            CopyList(workshop.Ban),
            CopyList(workshop.Jars.Select(ToJarDto)),
            CopyDictionary(workshop.LiquidStorage));
    }

    private static WorldV2JarDto ToJarDto(Jar jar) =>
        new(jar.Id, jar.Shape, jar.Liquid, jar.IsMarked);

    private static IReadOnlyList<T> CopyList<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static IReadOnlyDictionary<string, int> CopyDictionary(
        IEnumerable<KeyValuePair<string, int>> values) =>
        new ReadOnlyDictionary<string, int>(
            values.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));

}

public sealed record WorldV2ProducedJarImportResultDto(
    bool WasDuplicate,
    int SubmittedJarCount,
    int StoredJarCount,
    int DiscardedJarCount);

public sealed record WorldV2RequirementInput(string Shape, string? Liquid);

public sealed record WorldV2AddJarDemandRequest(
    IReadOnlyList<WorldV2RequirementInput> Requirements,
    string Type,
    DemandAreaKind Area,
    long CooldownDurationLogicalMilli,
    string? OrderDefinitionKey = null);

public sealed record WorldV2RequirementDto(string Shape, string? Liquid);
public sealed record WorldV2JarDto(int? Id, string Shape, string? Liquid, bool IsMarked);
public sealed record WorldV2LiquidAmountDto(string Liquid, int Amount);
public sealed record WorldV2LiquidProductionReceiptDto(
    string Liquid, int ProducedAmount, int AcceptedAmount, int DiscardedAmount);
public sealed record WorldV2ShipLiquidInventoryDto(
    int Capacity, int UsedCapacity, int AvailableCapacity,
    IReadOnlyDictionary<string, int> Balances);
public sealed record WorldV2ResourceEventDto(
    long Seq, int Tick, string Cause, string Kind, string Name, int Amount,
    string? From, string? To, string? Reason,
    string? ResourceIdentity = null, string? OperationId = null,
    string? FromAccountId = null, string? ToAccountId = null);
public sealed record WorldV2ResourceLedgerDto(
    long LastSeq, int Dropped, int Retained,
    IReadOnlyList<WorldV2ResourceEventDto> Events);

public sealed record WorldV2WorkshopDto(
    string Name,
    int MaxJars,
    int MaxLiquids,
    IReadOnlyList<string> BaseMarks,
    IReadOnlyList<string> AdditionalMarks,
    IReadOnlyList<string> Ban,
    IReadOnlyList<WorldV2JarDto> Jars,
    IReadOnlyDictionary<string, int> Liquids);

public sealed record WorldV2DemandDto(
    int Id,
    DemandAreaKind Area,
    string SpecificationType,
    IReadOnlyList<WorldV2RequirementDto> Requirements,
    long CooldownDurationLogicalMilli,
    long CooldownRemainingLogicalMilli,
    bool IsCoolingDown,
    string? OrderDefinitionKey,
    long CycleSequence);

public sealed record WorldV2ExecutionSessionDto(
    int DemandId,
    string Stage,
    long UnitDurationLogicalMilli,
    long UnitElapsedLogicalMilli,
    int CompletedMasterSteps,
    int CompletedSlaveSteps,
    bool IsTerminal,
    bool Succeeded,
    string? Reason);

public sealed record WorldV2AdvanceResultDto(
    long LogicalTimeDeltaMilli,
    int ProducedJarCount,
    IReadOnlyList<WorldV2LiquidAmountDto> ProducedLiquids,
    IReadOnlyList<WorldV2LiquidProductionReceiptDto> LiquidProductionReceipts,
    int FlowedJarCount,
    IReadOnlyDictionary<string, int> FlowedLiquids,
    IReadOnlyList<int> StartedDemandIds,
    int VisitedDemandCount,
    bool RuntimeFaulted,
    string? FaultReason);

public sealed record WorldV2StartDemandResultDto(bool Success, string? Reason);

public sealed record WorldV2WorkshopReplacementResultDto(
    bool Success,
    string? Error,
    WorldV2WorkshopReplacementDto? Replacement);

public sealed record WorldV2WorkshopReplacementDto(
    WorkshopRole Role,
    string OldWorkshopName,
    string TargetWorkshopName,
    long PhaseDurationLogicalMilli,
    long PhaseElapsedLogicalMilli,
    bool IsFinished,
    bool Succeeded,
    bool InterruptionCommitted,
    bool InterruptionOutcomeUncertain,
    int CompletedPhases,
    string? FailureReason,
    string? CommitWarning);

/// <summary>当前不占据主/从岗位的工作台候选；只暴露 UI 选择所需的脱离式状态。</summary>
public sealed record WorldV2ReplacementTargetDto(string Name, bool HasResources);

public sealed record WorldV2SnapshotDto(
    long WorldTimeMilli,
    long LogicalTimeMilli,
    long SpeedRatioMilli,
    bool IsPaused,
    bool RuntimeFaulted,
    string? FaultReason,
    IReadOnlyList<WorldV2JarDto> ResourceJars,
    IReadOnlyDictionary<string, int> ResourceLiquids,
    WorldV2WorkshopDto? Master,
    WorldV2WorkshopDto? Slave,
    IReadOnlyList<WorldV2DemandDto> Demands,
    IReadOnlyList<WorldV2ExecutionSessionDto> ActiveSessions,
    int ReservationCount,
    WorldV2WorkshopReplacementDto? Replacement,
    IReadOnlyList<WorldV2ReplacementTargetDto> SpareWorkshops,
    WorldV2ShipLiquidInventoryDto? ShipLiquidInventory);

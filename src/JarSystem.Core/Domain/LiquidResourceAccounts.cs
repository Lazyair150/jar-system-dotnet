using System.Collections.ObjectModel;

namespace JarSystem.Core.Domain;

/// <summary>
/// 自机长期液体库存。两种及以上液体共享同一个总容量；数量始终为非负整数。
/// </summary>
public sealed class ShipLiquidInventory
{
    private readonly Dictionary<string, int> _balances = new(StringComparer.Ordinal);
    private int _reservedReturnCapacity;
    private readonly IReadOnlyDictionary<string, int> _view;

    public ShipLiquidInventory(int capacity, IReadOnlyDictionary<string, int>? initialBalances = null)
    {
        if (capacity < 0)
            throw new DomainValidationException("自机液库容量不能为负数。");

        Capacity = capacity;
        if (initialBalances is not null)
        {
            foreach (var pair in initialBalances)
            {
                ValidateLiquid(pair.Key);
                ValidateAmount(pair.Value, allowZero: true);
                if (pair.Value > 0) _balances[pair.Key] = pair.Value;
            }
        }

        if (TotalAmount(_balances) > Capacity)
            throw new DomainValidationException("自机液库初始库存超过总容量。");

        _view = new ReadOnlyDictionary<string, int>(_balances);
    }

    public int Capacity { get; }
    public int UsedCapacity => TotalAmount(_balances);
    public int ReservedReturnCapacity => _reservedReturnCapacity;
    public int AvailableCapacity => Capacity - UsedCapacity - _reservedReturnCapacity;
    public IReadOnlyDictionary<string, int> Balances => _view;
    public int GetAmount(string liquid) => _balances.GetValueOrDefault(ValidateLiquid(liquid));

    /// <summary>生产入库：允许部分接收，未接收的整数单位直接丢弃。</summary>
    public LiquidInventoryReceipt Produce(string liquid, int amount)
    {
        ValidateLiquid(liquid);
        ValidateAmount(amount, allowZero: true);
        var accepted = Math.Min(amount, AvailableCapacity);
        AddUnchecked(liquid, accepted);
        return new LiquidInventoryReceipt(amount, accepted, amount - accepted);
    }

    /// <summary>订单启动时一次性扣除：全有或全无，失败时库存完全不变。</summary>
    public bool TryWithdraw(IReadOnlyDictionary<string, int> amounts, out LiquidInventoryReceipt receipt)
    {
        ValidateAmounts(amounts);
        foreach (var pair in amounts)
        {
            if (GetAmount(pair.Key) < pair.Value)
            {
                receipt = new LiquidInventoryReceipt(TotalAmount(amounts), 0, 0, "自机液库库存不足：" + pair.Key);
                return false;
            }
        }

        foreach (var pair in amounts)
            AddUnchecked(pair.Key, -pair.Value);

        var total = TotalAmount(amounts);
        receipt = new LiquidInventoryReceipt(total, total, 0);
        return true;
    }

    /// <summary>
    /// 为可回滚的主台预置转移扣款，并同时预留等量回库容量。
    /// 替换期间生产仍可继续，但不能占用这部分回滚所需的容量。
    /// </summary>
    internal bool TryWithdrawForTransfer(
        IReadOnlyDictionary<string, int> amounts,
        out ShipLiquidTransferReservation? reservation)
    {
        ValidateAmounts(amounts);
        foreach (var pair in amounts)
        {
            if (GetAmount(pair.Key) < pair.Value)
            {
                reservation = null;
                return false;
            }
        }

        var total = TotalAmount(amounts);
        foreach (var pair in amounts) AddUnchecked(pair.Key, -pair.Value);
        _reservedReturnCapacity = checked(_reservedReturnCapacity + total);
        reservation = new ShipLiquidTransferReservation(this,
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(amounts, StringComparer.Ordinal)),
            total);
        return true;
    }

    internal void CommitTransferReservation(int amount)
    {
        if (amount < 0 || amount > _reservedReturnCapacity)
            throw new InvalidOperationException("自机液库回滚容量预留内部状态无效。");
        _reservedReturnCapacity -= amount;
    }

    internal void RollbackTransferReservation(IReadOnlyDictionary<string, int> amounts, int reservedAmount)
    {
        if (reservedAmount < 0 || reservedAmount > _reservedReturnCapacity ||
            TotalAmount(amounts) != reservedAmount)
            throw new InvalidOperationException("自机液库预置转移回滚预留不匹配。");
        foreach (var item in amounts) AddUnchecked(item.Key, item.Value);
        _reservedReturnCapacity -= reservedAmount;
    }

    /// <summary>订单结算返还：允许部分接收，超出自机总容量的部分丢弃。</summary>
    public LiquidBatchReceipt Return(IReadOnlyDictionary<string, int> amounts)
    {
        ValidateAmounts(amounts);
        var requested = new Dictionary<string, int>(StringComparer.Ordinal);
        var acceptedAmounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var discardedAmounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var remaining = AvailableCapacity;

        // 按液体名序分配有限的返还空间，结果可复现且逐种液体可审计。
        foreach (var pair in amounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var accepted = Math.Min(pair.Value, remaining);
            var discarded = pair.Value - accepted;
            if (pair.Value > 0) requested[pair.Key] = pair.Value;
            if (accepted > 0) acceptedAmounts[pair.Key] = accepted;
            if (discarded > 0) discardedAmounts[pair.Key] = discarded;
            AddUnchecked(pair.Key, accepted);
            remaining -= accepted;
        }

        return new LiquidBatchReceipt(requested, acceptedAmounts, discardedAmounts);
    }

    public ShipLiquidInventorySnapshot Capture()
    {
        if (_reservedReturnCapacity != 0)
            throw new InvalidOperationException("自机液库存在尚未提交的预置转移事务，不能单独捕获库存快照。");
        return new ShipLiquidInventorySnapshot(Capacity, new Dictionary<string, int>(_balances, StringComparer.Ordinal));
    }

    /// <summary>将已验证的库存快照装入同容量液库；不会执行生产、扣款或返还语义。</summary>
    public void RestoreFrom(ShipLiquidInventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_reservedReturnCapacity != 0)
            throw new InvalidOperationException("自机液库存在尚未结束的预置转移事务，不能恢复库存快照。");
        if (snapshot.Capacity != Capacity)
            throw new DomainValidationException("自机液库快照容量与当前实例不一致。");
        ValidateAmounts(snapshot.Balances);
        if (TotalAmount(snapshot.Balances) > Capacity)
            throw new DomainValidationException("自机液库快照总库存超过容量。");

        var next = snapshot.Balances.Where(item => item.Value > 0)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        _balances.Clear();
        foreach (var item in next) _balances.Add(item.Key, item.Value);
    }

    private void AddUnchecked(string liquid, int delta)
    {
        var next = checked(_balances.GetValueOrDefault(liquid) + delta);
        if (next < 0) throw new InvalidOperationException("液体库存内部不变量被破坏。");
        if (next == 0) _balances.Remove(liquid);
        else _balances[liquid] = next;
    }

    internal static string ValidateLiquid(string liquid)
    {
        if (string.IsNullOrWhiteSpace(liquid))
            throw new DomainValidationException("液体名称不能为空。");
        return liquid;
    }

    internal static void ValidateAmount(int amount, bool allowZero)
    {
        if (amount < 0 || (!allowZero && amount == 0))
            throw new DomainValidationException(allowZero ? "液体数量不能为负数。" : "液体数量必须为正整数。");
    }

    internal static void ValidateAmounts(IReadOnlyDictionary<string, int> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        foreach (var pair in amounts)
        {
            ValidateLiquid(pair.Key);
            ValidateAmount(pair.Value, allowZero: true);
        }
    }

    internal static int TotalAmount(IEnumerable<KeyValuePair<string, int>> amounts)
    {
        var total = 0;
        foreach (var pair in amounts)
            total = checked(total + pair.Value);
        return total;
    }
}

public sealed record LiquidInventoryReceipt(
    int RequestedAmount,
    int AcceptedAmount,
    int DiscardedAmount,
    string? RejectionReason = null);

/// <summary>批量返还的逐液体结果，避免只记录总量而丢失哪种液体被接收/丢弃。</summary>
public sealed class LiquidBatchReceipt
{
    public LiquidBatchReceipt(
        IReadOnlyDictionary<string, int> requested,
        IReadOnlyDictionary<string, int> accepted,
        IReadOnlyDictionary<string, int> discarded)
    {
        Requested = Freeze(requested);
        Accepted = Freeze(accepted);
        Discarded = Freeze(discarded);
    }

    public IReadOnlyDictionary<string, int> Requested { get; }
    public IReadOnlyDictionary<string, int> Accepted { get; }
    public IReadOnlyDictionary<string, int> Discarded { get; }
    public int RequestedTotal => ShipLiquidInventory.TotalAmount(Requested);
    public int AcceptedTotal => ShipLiquidInventory.TotalAmount(Accepted);
    public int DiscardedTotal => ShipLiquidInventory.TotalAmount(Discarded);

    private static IReadOnlyDictionary<string, int> Freeze(IReadOnlyDictionary<string, int> source) =>
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(source, StringComparer.Ordinal));
}

public sealed class ShipLiquidInventorySnapshot
{
    public ShipLiquidInventorySnapshot(int capacity, IReadOnlyDictionary<string, int> balances)
    {
        Capacity = capacity;
        Balances = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(balances, StringComparer.Ordinal));
    }

    public int Capacity { get; }
    public IReadOnlyDictionary<string, int> Balances { get; }
    public int UsedCapacity => ShipLiquidInventory.TotalAmount(Balances);
}


/// <summary>自机液库到工作台预置账户的可回滚资源转移；提交后不可回滚。</summary>
public sealed class WorkshopPrefillTransaction
{
    private readonly WorkshopLiquidStaging _staging;
    private readonly ShipLiquidTransferReservation? _shipReservation;
    private bool _completed;
    private bool _committed;

    internal WorkshopPrefillTransaction(
        WorkshopLiquidStaging staging,
        ShipLiquidTransferReservation? shipReservation,
        LiquidBatchReceipt receipt)
    {
        _staging = staging;
        _shipReservation = shipReservation;
        Receipt = receipt;
    }

    public LiquidBatchReceipt Receipt { get; }
    public bool IsCompleted => _completed;
    public bool IsCommitted => _committed;

    public void Commit()
    {
        if (_completed) return;
        _shipReservation?.Commit();
        if (_shipReservation != null) _staging.CompletePrefillTransaction();
        _completed = true;
        _committed = true;
    }

    public void Rollback()
    {
        if (_completed)
        {
            if (_committed) throw new InvalidOperationException("已提交的主台预置补液不能回滚。");
            return;
        }
        if (_shipReservation != null)
        {
            _staging.RemovePrefillForRollback(Receipt.Accepted);
            _shipReservation.Rollback();
            _staging.CompletePrefillTransaction();
        }
        _completed = true;
    }
}

internal sealed class ShipLiquidTransferReservation
{
    private readonly ShipLiquidInventory _inventory;
    private readonly IReadOnlyDictionary<string, int> _amounts;
    private readonly int _total;
    private bool _completed;

    public ShipLiquidTransferReservation(
        ShipLiquidInventory inventory, IReadOnlyDictionary<string, int> amounts, int total)
    {
        _inventory = inventory;
        _amounts = amounts;
        _total = total;
    }

    public void Commit()
    {
        if (_completed) return;
        _inventory.CommitTransferReservation(_total);
        _completed = true;
    }

    public void Rollback()
    {
        if (_completed) throw new InvalidOperationException("自机液库预置转移预留已经结束。");
        _inventory.RollbackTransferReservation(_amounts, _total);
        _completed = true;
    }
}

/// <summary>
/// 单个工作台的液体暂存区。容量由该工作台所有订单子账共同占用。
/// 本对象不执行 Jar 操作，只提供订单范围内的液体资源读写。
/// </summary>
public sealed class WorkshopLiquidStaging
{
    private readonly Dictionary<int, Dictionary<string, int>> _accounts = new();
    private readonly Dictionary<string, int> _prefillBalances = new(StringComparer.Ordinal);
    // 订单预留的是整条路径可能达到的总库存峰值，而不是额外可用量。
    private readonly Dictionary<int, int> _reservedCapacityByOrder = new();
    private bool _prefillTransactionPending;
    private readonly HashSet<string> _banned;
    private readonly IReadOnlyCollection<string> _bannedView;

    public WorkshopLiquidStaging(int capacity, IEnumerable<string>? bannedLiquids = null)
    {
        if (capacity < 0)
            throw new DomainValidationException("工作台液体暂存容量不能为负数。");

        Capacity = capacity;
        _banned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var liquid in bannedLiquids ?? [])
            _banned.Add(ShipLiquidInventory.ValidateLiquid(liquid));
        _bannedView = Array.AsReadOnly(_banned.ToArray());
    }

    public int Capacity { get; }
    public int UsedCapacity => checked(ShipLiquidInventory.TotalAmount(_prefillBalances) +
        ShipLiquidInventory.TotalAmount(_accounts.Values.SelectMany(account => account)));
    public IReadOnlyDictionary<string, int> PrefillBalances =>
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(_prefillBalances, StringComparer.Ordinal));
    public int ReservedCapacity => _reservedCapacityByOrder.Values.Sum();
    public int OccupiedCapacity
    {
        get
        {
            var occupied = ShipLiquidInventory.TotalAmount(_prefillBalances);
            foreach (var pair in _accounts)
                occupied = checked(occupied + Math.Max(
                    _reservedCapacityByOrder.GetValueOrDefault(pair.Key),
                    ShipLiquidInventory.TotalAmount(pair.Value)));
            foreach (var reservation in _reservedCapacityByOrder)
                if (!_accounts.ContainsKey(reservation.Key))
                    occupied = checked(occupied + reservation.Value);
            return occupied;
        }
    }
    public int AvailableCapacity => Capacity - OccupiedCapacity;

    /// <summary>
    /// 为订单预留整条执行路径的液体峰值容量。预留总和与其他订单的已用/预留容量共同受总容量约束。
    /// </summary>
    public bool TryReserveOrderCapacity(int orderId, int peakCapacity)
    {
        if (_prefillTransactionPending) return false;
        ValidateOrderId(orderId);
        if (peakCapacity < 0) throw new DomainValidationException("订单液体容量预留不能为负数。");
        if (_reservedCapacityByOrder.ContainsKey(orderId)) return false;
        if (peakCapacity > AvailableCapacity) return false;
        _reservedCapacityByOrder.Add(orderId, peakCapacity);
        return true;
    }

    /// <summary>
    /// 切换主工作台时，从自机液库为目标主台预置账户补液。
    /// 优先级列表必须由调用方提供确定顺序；剩余液种按名称排序，结果不依赖字典枚举顺序。
    /// 只补到工作台总暂存容量，库存不足时允许部分补入。
    /// </summary>
    internal WorkshopPrefillTransaction BeginPrefillFromShip(
        ShipLiquidInventory inventory, IEnumerable<string> preferredLiquids)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(preferredLiquids);
        EnsureNoPendingPrefillTransaction();
        var preferred = preferredLiquids.Select(ShipLiquidInventory.ValidateLiquid).Distinct(StringComparer.Ordinal).ToArray();
        var ordered = preferred.Concat(inventory.Balances.Keys.OrderBy(item => item, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal);
        var remaining = AvailableCapacity;
        var transfer = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var liquid in ordered)
        {
            if (remaining == 0) break;
            if (_banned.Contains(liquid)) continue;
            var amount = Math.Min(inventory.GetAmount(liquid), remaining);
            if (amount <= 0) continue;
            transfer.Add(liquid, amount);
            remaining -= amount;
        }

        if (transfer.Count == 0)
            return new WorkshopPrefillTransaction(this, null,
                new LiquidBatchReceipt(transfer, transfer, new Dictionary<string, int>()));
        if (!inventory.TryWithdrawForTransfer(transfer, out var reservation))
            throw new InvalidOperationException("自机液库在主台前置补液提交前发生变化。");
        try
        {
            RestorePrefill(transfer);
            _prefillTransactionPending = true;
            return new WorkshopPrefillTransaction(this, reservation,
                new LiquidBatchReceipt(transfer, transfer, new Dictionary<string, int>()));
        }
        catch
        {
            reservation!.Rollback();
            throw;
        }
    }

    internal LiquidBatchReceipt PrefillFromShip(ShipLiquidInventory inventory, IEnumerable<string> preferredLiquids)
    {
        var transaction = BeginPrefillFromShip(inventory, preferredLiquids);
        transaction.Commit();
        return transaction.Receipt;
    }

    /// <summary>从空闲预置余额中为新订单取液；不足部分由订单准入再从自机液库扣除。</summary>
    internal IReadOnlyDictionary<string, int> WithdrawPrefillUpTo(IReadOnlyDictionary<string, int> requested)
    {
        EnsureNoPendingPrefillTransaction();
        ShipLiquidInventory.ValidateAmounts(requested);
        var withdrawn = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in requested.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var amount = Math.Min(item.Value, _prefillBalances.GetValueOrDefault(item.Key));
            if (amount <= 0) continue;
            AddPrefillUnchecked(item.Key, -amount);
            withdrawn.Add(item.Key, amount);
        }
        return new ReadOnlyDictionary<string, int>(withdrawn);
    }

    /// <summary>准入回滚时把尚未提交给订单账户的预置液体放回原账户。</summary>
    internal void RestorePrefill(IReadOnlyDictionary<string, int> amounts)
    {
        ShipLiquidInventory.ValidateAmounts(amounts);
        foreach (var item in amounts)
        {
            if (item.Value == 0) continue;
            if (_banned.Contains(item.Key)) throw new InvalidOperationException("不能把已禁用液体恢复到工作台预置账户：" + item.Key);
            AddPrefillUnchecked(item.Key, item.Value);
        }
        if (OccupiedCapacity > Capacity) throw new InvalidOperationException("恢复工作台预置液体后超过暂存区总容量。");
    }
    public bool ReleaseOrderCapacity(int orderId)
    {
        EnsureNoPendingPrefillTransaction();
        ValidateOrderId(orderId);
        return _reservedCapacityByOrder.Remove(orderId);
    }
    public IReadOnlyCollection<string> BannedLiquids => _bannedView;

    public int GetAmount(int orderId, string liquid)
    {
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateLiquid(liquid);
        return _accounts.TryGetValue(orderId, out var account)
            ? account.GetValueOrDefault(liquid)
            : 0;
    }

    public IReadOnlyDictionary<string, int> GetBalance(int orderId)
    {
        ValidateOrderId(orderId);
        var copy = _accounts.TryGetValue(orderId, out var account)
            ? new Dictionary<string, int>(account, StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, int>(copy);
    }

    /// <summary>普通资源产出入口：ban 拒收；容量不足时部分接收，其余丢弃。</summary>
    public LiquidInventoryReceipt Receive(int orderId, string liquid, int amount)
    {
        EnsureNoPendingPrefillTransaction();
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);

        if (_banned.Contains(liquid))
            return new LiquidInventoryReceipt(amount, 0, amount, "工作台禁" + liquid);

        var accepted = Math.Min(amount, AvailableCapacityFor(orderId));
        AddUnchecked(orderId, liquid, accepted);
        return new LiquidInventoryReceipt(amount, accepted, amount - accepted);
    }

    public LiquidGrant CanConsume(int orderId, string liquid, int amount)
    {
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);
        if (_banned.Contains(liquid)) return LiquidGrant.Banned(liquid);
        var have = GetAmount(orderId, liquid);
        return have >= amount ? LiquidGrant.Yes() : LiquidGrant.Insufficient(liquid);
    }

    public bool TryConsume(int orderId, string liquid, int amount)
    {
        EnsureNoPendingPrefillTransaction();
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);
        if (_banned.Contains(liquid) || GetAmount(orderId, liquid) < amount)
            return false;

        AddUnchecked(orderId, liquid, -amount);
        return true;
    }

    /// <summary>
    /// 操作合法性专用检查：必须同时满足工作台 ban 与工作台所有订单共享的总容量。
    /// 与普通 Receive 的部分接收语义不同，Workshop 操作必须完整接收其固定产出量。
    /// </summary>
    public LiquidGrant CanAcceptOperation(int orderId, string liquid, int amount)
    {
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);
        if (_banned.Contains(liquid)) return LiquidGrant.Banned(liquid);
        return AvailableCapacityFor(orderId) >= amount
            ? LiquidGrant.Yes()
            : LiquidGrant.OverCapacity(liquid);
    }

    public LiquidGrant CanAcceptOperation(string liquid, int amount)
    {
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);
        if (_banned.Contains(liquid)) return LiquidGrant.Banned(liquid);
        return AvailableCapacity >= amount
            ? LiquidGrant.Yes()
            : LiquidGrant.OverCapacity(liquid);
    }

    /// <summary>操作执行专用的全量接收；失败时不修改库存。</summary>
    public bool TryReceiveExact(int orderId, string liquid, int amount, out LiquidGrant result)
    {
        EnsureNoPendingPrefillTransaction();
        ValidateOrderId(orderId);
        result = CanAcceptOperation(orderId, liquid, amount);
        if (!result.Granted) return false;
        AddUnchecked(orderId, liquid, amount);
        return true;
    }

    /// <summary>只允许清除指定订单子账；返回被清除的真实余额。</summary>
    public IReadOnlyDictionary<string, int> DrainOrder(int orderId)
    {
        EnsureNoPendingPrefillTransaction();
        ValidateOrderId(orderId);
        if (!_accounts.Remove(orderId, out var account))
            return new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
        return new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(account, StringComparer.Ordinal));
    }

    /// <summary>捕获订单子账与容量预留；快照不暴露内部可变字典。</summary>
    public WorkshopLiquidStagingSnapshot Capture()
    {
        EnsureNoPendingPrefillTransaction();
        return new WorkshopLiquidStagingSnapshot(
            Capacity, _banned, _accounts, _reservedCapacityByOrder, _prefillBalances);
    }

    internal void CompletePrefillTransaction()
    {
        if (!_prefillTransactionPending)
            throw new InvalidOperationException("主台预置事务状态不匹配。");
        _prefillTransactionPending = false;
    }

    internal void RemovePrefillForRollback(IReadOnlyDictionary<string, int> amounts)
    {
        if (!_prefillTransactionPending)
            throw new InvalidOperationException("没有待回滚的主台预置事务。");
        ShipLiquidInventory.ValidateAmounts(amounts);
        foreach (var item in amounts)
        {
            if (_prefillBalances.GetValueOrDefault(item.Key) < item.Value)
                throw new InvalidOperationException("主台预置账户在回滚前已发生变化：" + item.Key);
            AddPrefillUnchecked(item.Key, -item.Value);
        }
    }

    private void EnsureNoPendingPrefillTransaction()
    {
        if (_prefillTransactionPending)
            throw new InvalidOperationException("主台前置补液事务尚未提交，不能并发修改订单暂存账户。");
    }

    internal void EnsureCanTransferOrderLiquids() => EnsureNoPendingPrefillTransaction();

    /// <summary>
    /// 从纯值快照重建暂存区。恢复前完整验证 ban、订单账户、容量预留及总占用，
    /// 任一不变量不成立都拒绝恢复，不进行部分装载。
    /// </summary>
    public static WorkshopLiquidStaging Restore(WorkshopLiquidStagingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Capacity < 0)
            throw new DomainValidationException("工作台液体暂存快照容量不能为负数。");

        var staging = new WorkshopLiquidStaging(snapshot.Capacity, snapshot.BannedLiquids);
        foreach (var balance in snapshot.PrefillBalances)
        {
            ShipLiquidInventory.ValidateLiquid(balance.Key);
            ShipLiquidInventory.ValidateAmount(balance.Value, allowZero: false);
            if (staging._banned.Contains(balance.Key)) throw new DomainValidationException("暂存快照包含工作台禁用的预置液体：" + balance.Key);
            staging.AddPrefillUnchecked(balance.Key, balance.Value);
        }

        foreach (var pair in snapshot.Accounts)
        {
            ValidateOrderId(pair.Key);
            foreach (var balance in pair.Value)
            {
                ShipLiquidInventory.ValidateLiquid(balance.Key);
                ShipLiquidInventory.ValidateAmount(balance.Value, allowZero: false);
                if (staging._banned.Contains(balance.Key))
                    throw new DomainValidationException("暂存快照包含工作台禁用液体：" + balance.Key);
                staging.AddUnchecked(pair.Key, balance.Key, balance.Value);
            }
        }

        foreach (var pair in snapshot.ReservedCapacityByOrder)
        {
            ValidateOrderId(pair.Key);
            if (pair.Value < 0)
                throw new DomainValidationException("暂存快照包含负数订单容量预留。");
            staging._reservedCapacityByOrder.Add(pair.Key, pair.Value);
        }

        foreach (var pair in staging._accounts)
        {
            if (staging._reservedCapacityByOrder.TryGetValue(pair.Key, out var reserved) &&
                ShipLiquidInventory.TotalAmount(pair.Value) > reserved)
                throw new DomainValidationException("订单暂存余额超过其容量预留。");
        }
        if (staging.OccupiedCapacity > staging.Capacity)
            throw new DomainValidationException("订单暂存账户与容量预留总和超过工作台总容量。");
        return staging;
    }

    internal bool CanReceiveForTransfer(int orderId, string liquid, int amount)
    {
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);
        return !_banned.Contains(liquid) && AvailableCapacityFor(orderId) >= amount;
    }

    internal bool CanReceiveTransferBatch(int orderId, IReadOnlyDictionary<string, int> amounts)
    {
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateAmounts(amounts);
        if (amounts.Keys.Any(_banned.Contains)) return false;
        return ShipLiquidInventory.TotalAmount(amounts) <= AvailableCapacityFor(orderId);
    }

    internal bool TryTransferOut(int orderId, string liquid, int amount)
    {
        if (GetAmount(orderId, liquid) < amount) return false;
        AddUnchecked(orderId, liquid, -amount);
        return true;
    }

    internal void ReceiveTransferUnchecked(int orderId, string liquid, int amount) =>
        AddUnchecked(orderId, liquid, amount);

    private int AvailableCapacityFor(int orderId)
    {
        if (_reservedCapacityByOrder.TryGetValue(orderId, out var quota))
        {
            var used = _accounts.TryGetValue(orderId, out var account)
                ? ShipLiquidInventory.TotalAmount(account)
                : 0;
            return Math.Max(0, quota - used);
        }
        return AvailableCapacity;
    }

    internal void RemovePrefillForRollback(IReadOnlyDictionary<string, int> amounts)
    {
        ShipLiquidInventory.ValidateAmounts(amounts);
        foreach (var item in amounts)
        {
            if (_prefillBalances.GetValueOrDefault(item.Key) < item.Value)
                throw new InvalidOperationException("主台预置账户在回滚前已发生变化：" + item.Key);
            AddPrefillUnchecked(item.Key, -item.Value);
        }
    }

    private void AddPrefillUnchecked(string liquid, int delta)
    {
        var next = checked(_prefillBalances.GetValueOrDefault(liquid) + delta);
        if (next < 0) throw new InvalidOperationException("工作台预置液体余额内部不变量被破坏。");
        if (next == 0) _prefillBalances.Remove(liquid);
        else _prefillBalances[liquid] = next;
    }

    private void AddUnchecked(int orderId, string liquid, int delta)
    {
        if (delta == 0) return;
        if (!_accounts.TryGetValue(orderId, out var account))
        {
            account = new Dictionary<string, int>(StringComparer.Ordinal);
            _accounts.Add(orderId, account);
        }

        var next = checked(account.GetValueOrDefault(liquid) + delta);
        if (next < 0) throw new InvalidOperationException("订单暂存余额内部不变量被破坏。");
        if (next == 0) account.Remove(liquid);
        else account[liquid] = next;
        if (account.Count == 0) _accounts.Remove(orderId);
    }

    private static void ValidateOrderId(int orderId)
    {
        if (orderId <= 0)
            throw new DomainValidationException("订单 ID 必须是正整数。");
    }
}

public sealed class WorkshopLiquidStagingSnapshot
{
    public WorkshopLiquidStagingSnapshot(
        int capacity,
        IEnumerable<string> bannedLiquids,
        IReadOnlyDictionary<int, Dictionary<string, int>> accounts,
        IReadOnlyDictionary<int, int>? reservedCapacityByOrder = null,
        IReadOnlyDictionary<string, int>? prefillBalances = null)
    {
        Capacity = capacity;
        BannedLiquids = Array.AsReadOnly(bannedLiquids.OrderBy(liquid => liquid, StringComparer.Ordinal).ToArray());
        var copy = new Dictionary<int, IReadOnlyDictionary<string, int>>();
        foreach (var pair in accounts)
            copy.Add(pair.Key, new ReadOnlyDictionary<string, int>(
                new Dictionary<string, int>(pair.Value, StringComparer.Ordinal)));
        Accounts = new ReadOnlyDictionary<int, IReadOnlyDictionary<string, int>>(copy);
        ReservedCapacityByOrder = new ReadOnlyDictionary<int, int>(
            new Dictionary<int, int>(reservedCapacityByOrder ?? new Dictionary<int, int>()));
        PrefillBalances = new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(prefillBalances ?? new Dictionary<string, int>(), StringComparer.Ordinal));
    }

    public int Capacity { get; }
    public IReadOnlyCollection<string> BannedLiquids { get; }
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>> Accounts { get; }
    public IReadOnlyDictionary<int, int> ReservedCapacityByOrder { get; }
    public IReadOnlyDictionary<string, int> PrefillBalances { get; }
    public int ReservedCapacity => ReservedCapacityByOrder.Values.Sum();
    public int OccupiedCapacity
    {
        get
        {
            var occupied = ShipLiquidInventory.TotalAmount(PrefillBalances);
            foreach (var pair in Accounts)
                occupied = checked(occupied + Math.Max(
                    ReservedCapacityByOrder.GetValueOrDefault(pair.Key),
                    ShipLiquidInventory.TotalAmount(pair.Value)));
            foreach (var pair in ReservedCapacityByOrder)
                if (!Accounts.ContainsKey(pair.Key))
                    occupied = checked(occupied + pair.Value);
            return occupied;
        }
    }
    public int UsedCapacity => checked(ShipLiquidInventory.TotalAmount(PrefillBalances) +
        ShipLiquidInventory.TotalAmount(Accounts.Values.SelectMany(account => account)));
}

/// <summary>主台到从台的订单液体单向交接。整批原子提交，不允许部分交接。</summary>
public static class OrderLiquidTransfer
{
    public static bool TryExecute(
        int orderId,
        WorkshopLiquidStaging master,
        WorkshopLiquidStaging slave,
        IReadOnlyDictionary<string, int> amounts,
        out string? rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(slave);
        ShipLiquidInventory.ValidateAmounts(amounts);

        if (ReferenceEquals(master, slave))
            throw new DomainValidationException("主工作台和从工作台不能是同一个暂存区。");
        master.EnsureCanTransferOrderLiquids();
        slave.EnsureCanTransferOrderLiquids();

        if (orderId <= 0)
            throw new DomainValidationException("订单 ID 必须是正整数。");

        if (!slave.CanReceiveTransferBatch(orderId, amounts))
        {
            rejectionReason = "从工作台订单液体暂存总容量不足或包含禁用液体。";
            return false;
        }

        foreach (var pair in amounts)
        {
            if (master.GetAmount(orderId, pair.Key) < pair.Value)
            {
                rejectionReason = "主工作台订单液体余额不足：" + pair.Key;
                return false;
            }
            if (!slave.CanReceiveForTransfer(orderId, pair.Key, pair.Value))
            {
                rejectionReason = "从工作台禁" + pair.Key;
                return false;
            }
        }

        // 所有前置条件均通过后才提交；提交阶段没有可失败的业务分支。
        foreach (var pair in amounts)
            master.TryTransferOut(orderId, pair.Key, pair.Value);
        foreach (var pair in amounts)
            slave.ReceiveTransferUnchecked(orderId, pair.Key, pair.Value);

        rejectionReason = null;
        return true;
    }
}

/// <summary>
/// Workshop 一次订单操作所使用的液体范围。它只选择订单子账，不执行任何 Jar 操作。
/// Workshop 仍然是 PourIn / PourOut / Reshape 的唯一执行者。
/// </summary>
public sealed class OrderLiquidScope
{
    public OrderLiquidScope(int orderId, WorkshopLiquidStaging staging)
    {
        if (orderId <= 0)
            throw new DomainValidationException("订单 ID 必须是正整数。");
        Staging = staging ?? throw new ArgumentNullException(nameof(staging));
        OrderId = orderId;
    }

    public int OrderId { get; }
    public WorkshopLiquidStaging Staging { get; }
    public IReadOnlyDictionary<string, int> Balance => Staging.GetBalance(OrderId);
    public int GetAmount(string liquid) => Staging.GetAmount(OrderId, liquid);
    public LiquidGrant CanConsume(string liquid, int amount) => Staging.CanConsume(OrderId, liquid, amount);
    public bool TryConsume(string liquid, int amount) => Staging.TryConsume(OrderId, liquid, amount);
    public LiquidGrant CanReceiveOperation(string liquid, int amount) =>
        Staging.CanAcceptOperation(OrderId, liquid, amount);
    public bool TryReceiveOperation(string liquid, int amount, out LiquidGrant result) =>
        Staging.TryReceiveExact(OrderId, liquid, amount, out result);
}

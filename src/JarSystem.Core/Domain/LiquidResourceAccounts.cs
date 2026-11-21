using System.Collections.ObjectModel;

namespace JarSystem.Core.Domain;

/// <summary>
/// 自机长期液体库存。两种及以上液体共享同一个总容量；数量始终为非负整数。
/// </summary>
public sealed class ShipLiquidInventory
{
    private readonly Dictionary<string, int> _balances = new(StringComparer.Ordinal);
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
    public int AvailableCapacity => Capacity - UsedCapacity;
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

    public ShipLiquidInventorySnapshot Capture() =>
        new(Capacity, new Dictionary<string, int>(_balances, StringComparer.Ordinal));

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

/// <summary>
/// 单个工作台的液体暂存区。容量由该工作台所有订单子账共同占用。
/// 本对象不执行 Jar 操作，只提供订单范围内的液体资源读写。
/// </summary>
public sealed class WorkshopLiquidStaging
{
    private readonly Dictionary<int, Dictionary<string, int>> _accounts = new();
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
    public int UsedCapacity => ShipLiquidInventory.TotalAmount(
        _accounts.Values.SelectMany(account => account));
    public int AvailableCapacity => Capacity - UsedCapacity;
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
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);

        if (_banned.Contains(liquid))
            return new LiquidInventoryReceipt(amount, 0, amount, "工作台禁" + liquid);

        var accepted = Math.Min(amount, AvailableCapacity);
        AddUnchecked(orderId, liquid, accepted);
        return new LiquidInventoryReceipt(amount, accepted, amount - accepted);
    }

    public bool TryConsume(int orderId, string liquid, int amount)
    {
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
    public LiquidGrant CanAcceptOperation(string liquid, int amount)
    {
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);
        if (_banned.Contains(liquid))
            return LiquidGrant.Banned(liquid);
        return AvailableCapacity >= amount
            ? LiquidGrant.Yes()
            : LiquidGrant.OverCapacity(liquid);
    }

    /// <summary>操作执行专用的全量接收；失败时不修改库存。</summary>
    public bool TryReceiveExact(int orderId, string liquid, int amount, out LiquidGrant result)
    {
        ValidateOrderId(orderId);
        result = CanAcceptOperation(liquid, amount);
        if (!result.Granted) return false;
        AddUnchecked(orderId, liquid, amount);
        return true;
    }

    /// <summary>只允许清除指定订单子账；返回被清除的真实余额。</summary>
    public IReadOnlyDictionary<string, int> DrainOrder(int orderId)
    {
        ValidateOrderId(orderId);
        if (!_accounts.Remove(orderId, out var account))
            return new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
        return new ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(account, StringComparer.Ordinal));
    }

    public WorkshopLiquidStagingSnapshot Capture() =>
        new(Capacity, _banned, _accounts);

    internal bool CanReceiveForTransfer(int orderId, string liquid, int amount)
    {
        ValidateOrderId(orderId);
        ShipLiquidInventory.ValidateLiquid(liquid);
        ShipLiquidInventory.ValidateAmount(amount, allowZero: true);
        return !_banned.Contains(liquid) && AvailableCapacity >= amount;
    }

    internal bool TryTransferOut(int orderId, string liquid, int amount)
    {
        if (GetAmount(orderId, liquid) < amount) return false;
        AddUnchecked(orderId, liquid, -amount);
        return true;
    }

    internal void ReceiveTransferUnchecked(int orderId, string liquid, int amount) =>
        AddUnchecked(orderId, liquid, amount);

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
        IReadOnlyDictionary<int, Dictionary<string, int>> accounts)
    {
        Capacity = capacity;
        BannedLiquids = Array.AsReadOnly(bannedLiquids.OrderBy(liquid => liquid, StringComparer.Ordinal).ToArray());
        var copy = new Dictionary<int, IReadOnlyDictionary<string, int>>();
        foreach (var pair in accounts)
            copy.Add(pair.Key, new ReadOnlyDictionary<string, int>(
                new Dictionary<string, int>(pair.Value, StringComparer.Ordinal)));
        Accounts = new ReadOnlyDictionary<int, IReadOnlyDictionary<string, int>>(copy);
    }

    public int Capacity { get; }
    public IReadOnlyCollection<string> BannedLiquids { get; }
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, int>> Accounts { get; }
    public int UsedCapacity => ShipLiquidInventory.TotalAmount(
        Accounts.Values.SelectMany(account => account));
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

        if (orderId <= 0)
            throw new DomainValidationException("订单 ID 必须是正整数。");

        var transferTotal = ShipLiquidInventory.TotalAmount(amounts);
        if (transferTotal > slave.AvailableCapacity)
        {
            rejectionReason = "从工作台液体暂存总容量不足。";
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
    public bool TryConsume(string liquid, int amount) => Staging.TryConsume(OrderId, liquid, amount);
    public LiquidGrant CanReceiveOperation(string liquid, int amount) =>
        Staging.CanAcceptOperation(liquid, amount);
    public bool TryReceiveOperation(string liquid, int amount, out LiquidGrant result) =>
        Staging.TryReceiveExact(OrderId, liquid, amount, out result);
}

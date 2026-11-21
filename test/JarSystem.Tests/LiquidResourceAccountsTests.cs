using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class LiquidResourceAccountsTests
{
    [Fact]
    public void ShipInventoryUsesOneAggregateCapacityAcrossLiquids()
    {
        var ship = new ShipLiquidInventory(5);
        Assert.Equal(new LiquidInventoryReceipt(3, 3, 0), ship.Produce("酒", 3));
        Assert.Equal(new LiquidInventoryReceipt(4, 2, 2), ship.Produce("蜜", 4));
        Assert.Equal(5, ship.UsedCapacity);
        Assert.Equal(0, ship.AvailableCapacity);
    }

    [Fact]
    public void ShipWithdrawalIsAllOrNothing()
    {
        var ship = new ShipLiquidInventory(5,
            new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 });

        Assert.False(ship.TryWithdraw(
            new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 2 }, out var failed));
        Assert.Equal(0, failed.AcceptedAmount);
        Assert.Equal(2, ship.GetAmount("酒"));
        Assert.Equal(1, ship.GetAmount("蜜"));

        Assert.True(ship.TryWithdraw(
            new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 }, out var success));
        Assert.Equal(3, success.AcceptedAmount);
        Assert.Equal(0, ship.UsedCapacity);
    }

    [Fact]
    public void ShipReturnDiscardsOverflowAgainstAggregateCapacity()
    {
        var ship = new ShipLiquidInventory(3,
            new Dictionary<string, int> { ["酒"] = 2 });
        var result = ship.Return(
            new Dictionary<string, int> { ["蜜"] = 2, ["盐"] = 1 });

        Assert.Equal(3, result.RequestedTotal);
        Assert.Equal(1, result.AcceptedTotal);
        Assert.Equal(2, result.DiscardedTotal);
        Assert.Equal(1, result.Accepted["盐"]);
        Assert.Equal(2, result.Discarded["蜜"]);
        Assert.Equal(3, ship.UsedCapacity);
    }

    [Fact]
    public void NegativeAmountsAndOverCapacityInitialStateAreRejected()
    {
        Assert.Throws<DomainValidationException>(() => new ShipLiquidInventory(2,
            new Dictionary<string, int> { ["酒"] = 3 }));
        var ship = new ShipLiquidInventory(2);
        Assert.Throws<DomainValidationException>(() => ship.Produce("酒", -1));
        Assert.Throws<DomainValidationException>(() => ship.TryWithdraw(
            new Dictionary<string, int> { ["酒"] = -1 }, out _));
    }

    [Fact]
    public void WorkshopStagingSharesCapacityAndRejectsBannedLiquid()
    {
        var staging = new WorkshopLiquidStaging(3, new[] { "蜜" });
        Assert.Equal(new LiquidInventoryReceipt(2, 2, 0), staging.Receive(101, "酒", 2));
        Assert.Equal(new LiquidInventoryReceipt(2, 0, 2, "工作台禁蜜"),
            staging.Receive(101, "蜜", 2));
        Assert.Equal(new LiquidInventoryReceipt(2, 1, 1),
            staging.Receive(102, "盐", 2));
        Assert.Equal(3, staging.UsedCapacity);
        Assert.False(staging.TryConsume(101, "蜜", 1));
        Assert.True(staging.TryConsume(101, "酒", 1));
        Assert.Equal(2, staging.UsedCapacity);
    }

    [Fact]
    public void OrdersCannotReadOrConsumeEachOthersStagingBalances()
    {
        var staging = new WorkshopLiquidStaging(8);
        staging.Receive(101, "酒", 2);

        Assert.Equal(0, staging.GetAmount(102, "酒"));
        Assert.False(staging.TryConsume(102, "酒", 1));
        Assert.Equal(2, staging.GetAmount(101, "酒"));
    }

    [Fact]
    public void MasterToSlaveTransferIsAtomicAndPreservesOrderTotal()
    {
        var master = new WorkshopLiquidStaging(8);
        var slave = new WorkshopLiquidStaging(6);
        master.Receive(101, "酒", 3);
        master.Receive(101, "蜜", 2);

        Assert.True(OrderLiquidTransfer.TryExecute(101, master, slave,
            new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 }, out var reason));
        Assert.Null(reason);
        Assert.Equal(3, master.GetAmount(101, "酒") + slave.GetAmount(101, "酒"));
        Assert.Equal(2, master.GetAmount(101, "蜜") + slave.GetAmount(101, "蜜"));
        Assert.Equal(5, master.GetBalance(101).Values.Sum() + slave.GetBalance(101).Values.Sum());
        Assert.Equal(2, slave.GetAmount(101, "酒"));
        Assert.Equal(1, slave.GetAmount(101, "蜜"));
    }

    [Fact]
    public void MultiLiquidTransferChecksAggregateTargetCapacityBeforeChangingEitherSide()
    {
        var master = new WorkshopLiquidStaging(8);
        var slave = new WorkshopLiquidStaging(3);
        master.Receive(101, "酒", 2);
        master.Receive(101, "蜜", 2);
        slave.Receive(202, "盐", 2);

        Assert.False(OrderLiquidTransfer.TryExecute(101, master, slave,
            new Dictionary<string, int> { ["酒"] = 1, ["蜜"] = 1 }, out var reason));
        Assert.Equal("从工作台液体暂存总容量不足。", reason);
        Assert.Equal(2, master.GetAmount(101, "酒"));
        Assert.Equal(2, master.GetAmount(101, "蜜"));
        Assert.Equal(0, slave.GetAmount(101, "酒"));
        Assert.Equal(2, slave.GetAmount(202, "盐"));
    }

    [Fact]
    public void SlaveBanRejectsTransferWithoutMutatingEitherStaging()
    {
        var master = new WorkshopLiquidStaging(8);
        var slave = new WorkshopLiquidStaging(6, new[] { "酒" });
        master.Receive(101, "酒", 2);

        Assert.False(OrderLiquidTransfer.TryExecute(101, master, slave,
            new Dictionary<string, int> { ["酒"] = 1 }, out _));
        Assert.Equal(2, master.GetAmount(101, "酒"));
        Assert.Equal(0, slave.GetAmount(101, "酒"));
    }

    [Fact]
    public void SnapshotIsDetachedFromMutableStaging()
    {
        var staging = new WorkshopLiquidStaging(5);
        staging.Receive(101, "酒", 2);
        var snapshot = staging.Capture();
        staging.TryConsume(101, "酒", 1);

        Assert.Equal(2, snapshot.Accounts[101]["酒"]);
        Assert.Equal(1, staging.GetAmount(101, "酒"));
    }

    [Fact]
    public void DrainOrderReturnsOnlyThatOrdersActualBalance()
    {
        var staging = new WorkshopLiquidStaging(8);
        staging.Receive(101, "酒", 2);
        staging.Receive(202, "蜜", 3);

        var drained = staging.DrainOrder(101);

        Assert.Equal(2, drained["酒"]);
        Assert.Equal(0, staging.GetAmount(101, "酒"));
        Assert.Equal(3, staging.GetAmount(202, "蜜"));
        Assert.Equal(3, staging.UsedCapacity);
    }
}

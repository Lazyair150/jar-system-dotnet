using System;
using System.Collections.Generic;
using System.Linq;
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

    [Theory]
    [InlineData(20261124)]
    [InlineData(7919)]
    [InlineData(104729)]
    [InlineData(65537)]
    public void FixedSeedShipReturnsConserveRequestedAcceptedAndDiscardedAmounts(int seed)
    {
        var random = new Random(seed ^ 0x5A17);
        var liquids = new[] { "酒", "蜜", "盐" };
        for (var sample = 0; sample < 200; sample++)
        {
            var initial = liquids.ToDictionary(
                liquid => liquid, _ => random.Next(0, 5), StringComparer.Ordinal);
            var initialTotal = initial.Values.Sum();
            var requested = liquids.ToDictionary(
                liquid => liquid, _ => random.Next(0, 7), StringComparer.Ordinal);
            var requestedTotal = requested.Values.Sum();
            var capacity = checked(initialTotal + random.Next(0, requestedTotal + 1));
            var ship = new ShipLiquidInventory(capacity, initial);

            var receipt = ship.Return(requested);

            foreach (var liquid in liquids)
            {
                var amount = requested[liquid];
                var accepted = receipt.Accepted.GetValueOrDefault(liquid);
                var discarded = receipt.Discarded.GetValueOrDefault(liquid);
                Assert.Equal(amount, accepted + discarded);
                Assert.Equal(initial.GetValueOrDefault(liquid) + accepted, ship.GetAmount(liquid));
            }

            Assert.Equal(requestedTotal, receipt.RequestedTotal);
            Assert.Equal(requestedTotal, receipt.AcceptedTotal + receipt.DiscardedTotal);
            Assert.Equal(initialTotal + receipt.AcceptedTotal, ship.UsedCapacity);
            Assert.Equal(initialTotal + requestedTotal,
                ship.UsedCapacity + receipt.DiscardedTotal);
            Assert.InRange(ship.UsedCapacity, 0, ship.Capacity);
        }
    }

    [Theory]
    [InlineData(20261125)]
    [InlineData(7919)]
    [InlineData(104729)]
    [InlineData(65537)]
    public void FixedSeedStagingAdmissionConservesAcceptedAndDiscardedOutput(int seed)
    {
        var random = new Random(seed ^ 0x31C7);
        var liquids = new[] { "酒", "蜜", "盐" };

        for (var sample = 0; sample < 256; sample++)
        {
            var capacity = random.Next(1, 12);
            var banned = liquids.Where(_ => random.Next(2) == 0).ToArray();
            var staging = new WorkshopLiquidStaging(capacity, banned);
            var otherLiquid = liquids[random.Next(liquids.Length)];
            staging.Receive(20_000 + sample, otherLiquid, random.Next(1, 4));

            var orderId = 30_000 + sample;
            var liquid = liquids[random.Next(liquids.Length)];
            staging.Receive(orderId, liquid, random.Next(1, 4));
            var beforeOrderBalance = staging.GetAmount(orderId, liquid);
            var beforeUsedCapacity = staging.UsedCapacity;
            var requested = random.Next(1, 7);

            var settlement = staging.ReceiveWithTransition(
                "owned.workshop.master", orderId, liquid, requested);
            var receipt = settlement.Receipt;

            Assert.Equal(requested, receipt.AcceptedAmount + receipt.DiscardedAmount);
            Assert.Equal(beforeOrderBalance + receipt.AcceptedAmount,
                staging.GetAmount(orderId, liquid));
            Assert.Equal(beforeUsedCapacity + receipt.AcceptedAmount,
                staging.UsedCapacity);
            Assert.InRange(staging.UsedCapacity, 0, capacity);
            Assert.Equal(requested, receipt.RequestedTotal);
            Assert.Equal(receipt.AcceptedAmount + receipt.DiscardedAmount,
                settlement.Transition.ResourceEffects.Sum(effect => effect.Amount));
            Assert.Equal(receipt.AcceptedAmount,
                settlement.Transition.ResourceEffects
                    .Where(effect => effect.Kind == RuleResourceEffectKind.Transfer)
                    .Sum(effect => effect.Amount));
            Assert.Equal(receipt.DiscardedAmount,
                settlement.Transition.ResourceEffects
                    .Where(effect => effect.Kind == RuleResourceEffectKind.Discard)
                    .Sum(effect => effect.Amount));
            if (banned.Contains(liquid, StringComparer.Ordinal))
                Assert.Equal(requested, receipt.DiscardedAmount);
        }
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

    [Theory]
    [InlineData(20261124)]
    [InlineData(7919)]
    [InlineData(104729)]
    [InlineData(65537)]
    public void FixedSeedOrderLiquidTransfersPreserveEveryAccountAndResourceTotal(int seed)
    {
        var random = new Random(seed);
        var successCount = 0;
        var rejectionCount = 0;
        var liquids = new[] { "酒", "蜜", "盐" };

        for (var sample = 0; sample < 200; sample++)
        {
            var master = new WorkshopLiquidStaging(30);
            var source = liquids.ToDictionary(
                liquid => liquid, _ => random.Next(1, 7), StringComparer.Ordinal);
            foreach (var item in source)
                Assert.Equal(item.Value,
                    master.Receive(101, item.Key, item.Value).AcceptedAmount);

            var scenario = sample % 4;
            var otherOrderAmount = random.Next(1, 7);
            var slaveCapacity = scenario == 3 ? otherOrderAmount : 40;
            var banned = scenario == 2 ? new[] { "蜜" } : Array.Empty<string>();
            var slave = new WorkshopLiquidStaging(slaveCapacity, banned);
            Assert.Equal(otherOrderAmount,
                slave.Receive(202, "外来液", otherOrderAmount).AcceptedAmount);

            var requested = source.ToDictionary(
                item => item.Key,
                item => random.Next(1, item.Value + 1),
                StringComparer.Ordinal);
            if (scenario == 1)
                requested["酒"] = checked(source["酒"] + 1);
            if (scenario == 2)
                requested["蜜"] = Math.Max(1, requested["蜜"]);

            var masterBefore = master.GetBalance(101);
            var slaveBefore = slave.GetBalance(101);
            var unrelatedBefore = slave.GetBalance(202);
            var totalsBefore = liquids.ToDictionary(
                liquid => liquid,
                liquid => master.GetAmount(101, liquid) + slave.GetAmount(101, liquid),
                StringComparer.Ordinal);

            var moved = OrderLiquidTransfer.TryExecute(
                101, master, slave, requested, out _);

            if (moved)
            {
                successCount++;
                foreach (var liquid in liquids)
                {
                    Assert.Equal(totalsBefore[liquid],
                        master.GetAmount(101, liquid) + slave.GetAmount(101, liquid));
                    Assert.Equal(masterBefore.GetValueOrDefault(liquid) - requested.GetValueOrDefault(liquid),
                        master.GetAmount(101, liquid));
                    Assert.Equal(slaveBefore.GetValueOrDefault(liquid) + requested.GetValueOrDefault(liquid),
                        slave.GetAmount(101, liquid));
                }
            }
            else
            {
                rejectionCount++;
                Assert.Equal(masterBefore.OrderBy(item => item.Key), master.GetBalance(101).OrderBy(item => item.Key));
                Assert.Equal(slaveBefore.OrderBy(item => item.Key), slave.GetBalance(101).OrderBy(item => item.Key));
            }

            Assert.Equal(unrelatedBefore.OrderBy(item => item.Key),
                slave.GetBalance(202).OrderBy(item => item.Key));
            Assert.InRange(master.UsedCapacity, 0, master.Capacity);
            Assert.InRange(slave.UsedCapacity, 0, slave.Capacity);
        }

        Assert.True(successCount > 0, "固定样本必须覆盖成功交接");
        Assert.True(rejectionCount > 0, "固定样本必须覆盖拒绝交接");
        Assert.Equal(200, successCount + rejectionCount);
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
        Assert.Equal("从工作台订单液体暂存总容量不足或包含禁用液体。", reason);
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
    public void StagingSnapshotRestoresAccountsReservationsBansAndZeroQuota()
    {
        var staging = new WorkshopLiquidStaging(8, new[] { "蜜" });
        Assert.True(staging.TryReserveOrderCapacity(101, 5));
        Assert.True(staging.TryReserveOrderCapacity(202, 3));
        Assert.True(staging.TryReserveOrderCapacity(303, 0));
        Assert.Equal(2, staging.Receive(101, "酒", 2).AcceptedAmount);

        var snapshot = staging.Capture();
        var restored = WorkshopLiquidStaging.Restore(snapshot);

        Assert.Equal(8, restored.Capacity);
        Assert.Equal(2, restored.UsedCapacity);
        Assert.Equal(8, restored.ReservedCapacity);
        Assert.Equal(8, restored.OccupiedCapacity);
        Assert.Equal(0, restored.AvailableCapacity);
        Assert.Equal(2, restored.GetAmount(101, "酒"));
        Assert.Equal(new[] { "蜜" }, restored.BannedLiquids);
        Assert.True(restored.ReleaseOrderCapacity(303));
        Assert.True(restored.ReleaseOrderCapacity(202));
        Assert.Equal(5, restored.OccupiedCapacity);
        Assert.Equal(new LiquidInventoryReceipt(1, 0, 1, "工作台禁蜜"),
            restored.Receive(101, "蜜", 1));
    }

    [Fact]
    public void StagingRestoreRejectsAccountsThatExceedTheirReservedPeak()
    {
        var snapshot = new WorkshopLiquidStagingSnapshot(
            5,
            Array.Empty<string>(),
            new Dictionary<int, Dictionary<string, int>>
            {
                [101] = new() { ["酒"] = 3 }
            },
            new Dictionary<int, int> { [101] = 2 });

        Assert.Throws<DomainValidationException>(() => WorkshopLiquidStaging.Restore(snapshot));
    }

    [Fact]
    public void StagingRestoreRejectsAggregateOccupiedCapacityOverflow()
    {
        var snapshot = new WorkshopLiquidStagingSnapshot(
            5,
            Array.Empty<string>(),
            new Dictionary<int, Dictionary<string, int>>(),
            new Dictionary<int, int> { [101] = 3, [202] = 3 });

        Assert.Throws<DomainValidationException>(() => WorkshopLiquidStaging.Restore(snapshot));
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

    [Fact]
    public void ReservedOrderCapacityAllowsAtomicMultiLiquidPumpWithinAggregateQuotaOnly()
    {
        var master = new WorkshopLiquidStaging(8);
        var slave = new WorkshopLiquidStaging(8);
        Assert.True(master.TryReserveOrderCapacity(101, 6));
        Assert.True(slave.TryReserveOrderCapacity(101, 3));
        Assert.Equal(2, master.Receive(101, "酒", 2).AcceptedAmount);
        Assert.Equal(2, master.Receive(101, "蜜", 2).AcceptedAmount);

        var oversized = new Dictionary<string, int>
        {
            ["酒"] = 2,
            ["蜜"] = 2
        };
        Assert.False(OrderLiquidTransfer.TryExecute(
            101, master, slave, oversized, out _));
        Assert.Equal(2, master.GetAmount(101, "酒"));
        Assert.Equal(2, master.GetAmount(101, "蜜"));
        Assert.Equal(0, slave.UsedCapacity);

        var fitting = new Dictionary<string, int>
        {
            ["酒"] = 1,
            ["蜜"] = 2
        };
        Assert.True(OrderLiquidTransfer.TryExecute(
            101, master, slave, fitting, out var reason), reason);
        Assert.Equal(1, master.GetAmount(101, "酒"));
        Assert.Equal(0, master.GetAmount(101, "蜜"));
        Assert.Equal(1, slave.GetAmount(101, "酒"));
        Assert.Equal(2, slave.GetAmount(101, "蜜"));
        Assert.Equal(3, slave.UsedCapacity);
    }

    [Fact]
    public void MasterPrefillUsesDeterministicProductionPriorityAndRespectsSharedCapacity()
    {
        var ship = new ShipLiquidInventory(20, new Dictionary<string, int> { ["酒"] = 4, ["蜜"] = 4, ["水"] = 4 });
        var staging = new WorkshopLiquidStaging(6);
        var receipt = staging.PrefillFromShip(ship, new[] { "蜜", "酒" });
        Assert.Equal(6, receipt.AcceptedTotal);
        Assert.Equal(4, staging.PrefillBalances["蜜"]);
        Assert.Equal(2, staging.PrefillBalances["酒"]);
        Assert.False(staging.PrefillBalances.ContainsKey("水"));
        Assert.Equal(6, staging.UsedCapacity);
        Assert.Equal(0, staging.AvailableCapacity);
        Assert.Equal(0, ship.GetAmount("蜜"));
        Assert.Equal(2, ship.GetAmount("酒"));
        Assert.Equal(4, ship.GetAmount("水"));
        var restored = WorkshopLiquidStaging.Restore(staging.Capture());
        Assert.Equal(staging.PrefillBalances, restored.PrefillBalances);
        Assert.Equal(staging.UsedCapacity, restored.UsedCapacity);
    }

    [Fact]
    public void MasterPrefillAllowsPartialSupplyAndSkipsBannedLiquids()
    {
        var ship = new ShipLiquidInventory(5, new Dictionary<string, int> { ["酒"] = 2, ["蜜"] = 1 });
        var staging = new WorkshopLiquidStaging(8, new[] { "酒" });
        var receipt = staging.PrefillFromShip(ship, new[] { "酒", "蜜" });
        Assert.Equal(1, receipt.AcceptedTotal);
        Assert.Equal(1, staging.PrefillBalances["蜜"]);
        Assert.Equal(2, ship.GetAmount("酒"));
        Assert.Equal(0, ship.GetAmount("蜜"));
        Assert.Equal(7, staging.AvailableCapacity);
    }

    [Fact]
    public void PrefillCanBeWithdrawnIntoAnOrderWithoutDoubleCountingCapacity()
    {
        var staging = new WorkshopLiquidStaging(5);
        staging.RestorePrefill(new Dictionary<string, int> { ["酒"] = 3 });
        var allocated = staging.WithdrawPrefillUpTo(new Dictionary<string, int> { ["酒"] = 2 });
        Assert.Equal(2, allocated["酒"]);
        Assert.True(staging.TryReserveOrderCapacity(7, 4));
        Assert.True(staging.TryReceiveExact(7, "酒", 2, out _));
        Assert.Equal(1, staging.PrefillBalances["酒"]);
        Assert.Equal(3, staging.UsedCapacity);
        Assert.Equal(0, staging.AvailableCapacity);
    }

    [Fact]
    public void PendingPrefillLocksStagingMutationAndSnapshotUntilResolved()
    {
        var ship = new ShipLiquidInventory(5, new Dictionary<string, int> { ["酒"] = 3 });
        var staging = new WorkshopLiquidStaging(4);
        var transaction = staging.BeginPrefillFromShip(ship, new[] { "酒" });

        Assert.Throws<InvalidOperationException>(() => staging.Receive(7, "蜜", 1));
        Assert.Throws<InvalidOperationException>(() => staging.CanConsume(7, "酒", 1));
        Assert.Throws<InvalidOperationException>(() => staging.CanAcceptOperation(7, "蜜", 1));
        Assert.Throws<InvalidOperationException>(() => staging.TryConsume(7, "酒", 1));
        Assert.Throws<InvalidOperationException>(() => staging.Capture());
        Assert.Throws<InvalidOperationException>(() => OrderLiquidTransfer.TryExecute(
            7, staging, new WorkshopLiquidStaging(4),
            new Dictionary<string, int>(), out _));
        Assert.False(staging.TryReserveOrderCapacity(7, 1));
        Assert.Equal(3, ship.ReservedReturnCapacity);

        transaction.Commit();

        Assert.Equal(0, ship.ReservedReturnCapacity);
        Assert.Equal(1, staging.Receive(7, "蜜", 1).AcceptedAmount);
        Assert.Equal(4, staging.UsedCapacity);
        Assert.Equal(4, staging.Capture().PrefillBalances["酒"] +
            staging.Capture().Accounts.Values.SelectMany(x => x).Sum(x => x.Value));
    }

    [Fact]
    public void EmptyPrefillTransactionCanBeRolledBackAsNoOp()
    {
        var ship = new ShipLiquidInventory(2);
        var staging = new WorkshopLiquidStaging(2);
        var transaction = staging.BeginPrefillFromShip(ship, new[] { "酒" });

        Assert.Equal(0, transaction.Receipt.AcceptedTotal);
        transaction.Rollback();

        Assert.True(transaction.IsCompleted);
        Assert.Empty(staging.PrefillBalances);
        Assert.Equal(0, ship.ReservedReturnCapacity);
    }

    [Fact]
    public void ReversiblePrefillReservesShipReturnCapacityUntilCommitOrRollback()
    {
        var ship = new ShipLiquidInventory(4, new Dictionary<string, int> { ["酒"] = 3 });
        var staging = new WorkshopLiquidStaging(3);
        var transaction = staging.BeginPrefillFromShip(ship, new[] { "酒" });

        Assert.Equal(3, staging.PrefillBalances["酒"]);
        Assert.Equal(3, ship.ReservedReturnCapacity);
        Assert.Equal(1, ship.AvailableCapacity);
        var production = ship.Produce("蜜", 2);
        Assert.Equal(new LiquidInventoryReceipt(2, 1, 1), production);

        transaction.Rollback();

        Assert.Empty(staging.PrefillBalances);
        Assert.Equal(0, ship.ReservedReturnCapacity);
        Assert.Equal(3, ship.GetAmount("酒"));
        Assert.Equal(1, ship.GetAmount("蜜"));
        Assert.Equal(0, ship.AvailableCapacity);
    }

}

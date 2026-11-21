using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldSnapshotTests
{
    [Fact]
    public void IdenticalSolveInputsProduceTheSameVersion()
    {
        var firstMaster = new Workshop("主台", new List<string> { "圆罐" });
        var firstSlave = new Workshop("从台");
        var secondMaster = new Workshop("主台", new List<string> { "圆罐" });
        var secondSlave = new Workshop("从台");

        var first = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { firstMaster, firstSlave }, firstMaster, firstSlave));
        var second = WorldSnapshotFactory.Capture(
            new WorkshopRoster(new[] { secondMaster, secondSlave }, secondMaster, secondSlave));

        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public void SolveInputChangesProduceDifferentVersions()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var slave = new Workshop("从台");
        var roster = new WorkshopRoster(new[] { master, slave }, master, slave);
        var initial = WorldSnapshotFactory.Capture(roster);

        master.AddLiquid("酒", 1);
        var afterLiquid = WorldSnapshotFactory.Capture(roster);
        Assert.NotEqual(initial.Version, afterLiquid.Version);

        master.AddJar(new Jar("圆罐"));
        var afterJar = WorldSnapshotFactory.Capture(roster);
        Assert.NotEqual(afterLiquid.Version, afterJar.Version);

        master.ReplaceCapability(new WorkshopCapability(
            baseMarks: new[] { "圆罐" },
            additionalMarks: new[] { "方罐" }));
        var afterCapability = WorldSnapshotFactory.Capture(roster);
        Assert.NotEqual(afterJar.Version, afterCapability.Version);
    }

    [Fact]
    public void SnapshotDoesNotExposeLiveJarOrMutableSolverCollections()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        master.AddLiquid("酒", 1);
        var jar = new Jar("圆罐");
        master.AddJar(jar);
        var roster = new WorkshopRoster(new[] { master }, master);
        var snapshot = WorldSnapshotFactory.Capture(roster);
        var capturedVersion = snapshot.Version;
        var masterSnapshot = snapshot.Master!;

        // 求解器获得的是新建的可变输入，改写它不能污染共享快照。
        var solverJars = masterSnapshot.CreateSolverJars();
        solverJars[0].Shape = "被篡改";
        solverJars.Clear();
        var solverLiquids = masterSnapshot.CreateSolverLiquids();
        solverLiquids["酒"] = 999;

        // 活世界后续变化也不能回写已经拍摄的快照。
        master.RemoveJar(jar);
        master.RemoveLiquid("酒", 1);

        Assert.Equal(capturedVersion, snapshot.Version);
        Assert.Equal("圆罐", Assert.Single(masterSnapshot.Jars).Shape);
        Assert.Equal(1, masterSnapshot.Liquids["酒"]);
        Assert.Equal("圆罐", masterSnapshot.CreateSolverJars().Single().Shape);
        Assert.Equal(1, masterSnapshot.CreateSolverLiquids()["酒"]);
    }

    [Fact]
    public void RoleAssignmentIsPartOfTheSolveInputVersion()
    {
        var master = new Workshop("甲");
        var slave = new Workshop("乙");
        var spare = new Workshop("丙");
        var roster = new WorkshopRoster(new[] { master, slave, spare }, master, slave);
        var before = WorldSnapshotFactory.Capture(roster);

        // 角色不同，即使两个工作台本身的资源状态相同，也不是同一个主从求解输入。
        var changedRoster = new WorkshopRoster(new[] { master, slave, spare }, slave, master);
        var after = WorldSnapshotFactory.Capture(changedRoster);

        Assert.NotEqual(before.Version, after.Version);
    }

    [Fact]
    public void EffectiveGrantModifierIsCapturedButFutureModifierIsNot()
    {
        var stack = new ModifierStack();
        stack.Push(new GrantModifier
        {
            Shape = "方罐",
            Window = (5, null)
        });

        var master = new Workshop("主台", new List<string> { "圆罐" }, modifiers: stack);
        var roster = new WorkshopRoster(new[] { master }, master);

        var beforeStart = WorldSnapshotFactory.Capture(roster);
        Assert.Empty(beforeStart.Master!.SolverModifiers);

        stack.Expire(5);
        var afterStart = WorldSnapshotFactory.Capture(roster);

        Assert.NotEqual(beforeStart.Version, afterStart.Version);
        Assert.Single(afterStart.Master!.SolverModifiers);
        Assert.Equal("方罐", afterStart.Master.SolverModifiers[0].Shape);
        Assert.False(afterStart.Master.HasOpaqueSolverRules);
    }


    [Fact]
    public void ReturningToAnIdenticalSolveInputRestoresItsContentVersion()
    {
        var master = new Workshop("主台");
        var roster = new WorkshopRoster(new[] { master }, master);
        var original = WorldSnapshotFactory.Capture(roster);

        master.AddLiquid("酒", 1);
        var changed = WorldSnapshotFactory.Capture(roster);
        master.RemoveLiquid("酒", 1);
        var restored = WorldSnapshotFactory.Capture(roster);

        Assert.NotEqual(original.Version, changed.Version);
        Assert.Equal(original.Version, restored.Version);
    }

    [Fact]
    public void OperationSpecificSuppressionIsOpaqueToStaticSolving()
    {
        var stack = new ModifierStack();
        stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Liquid = "酒"
        });
        var master = new Workshop("主台", modifiers: stack);
        var roster = new WorkshopRoster(new[] { master }, master);

        var snapshot = WorldSnapshotFactory.Capture(roster);

        Assert.True(snapshot.Master!.HasOpaqueSolverRules);
        Assert.True(snapshot.Master.SolverModifiers.Single().IsOpaque);
    }

    [Fact]
    public void RequireModifierIsMarkedOpaqueInsteadOfCopyingItsMutableClosure()
    {
        var stack = new ModifierStack();
        var allowed = true;
        stack.Push(new RequireModifier
        {
            Text = "外部条件",
            Violation = () => allowed ? null : "条件不满足"
        });
        var master = new Workshop("主台", modifiers: stack);
        var roster = new WorkshopRoster(new[] { master }, master);

        var snapshot = WorldSnapshotFactory.Capture(roster);

        Assert.True(snapshot.Master!.HasOpaqueSolverRules);
        Assert.True(snapshot.Master.SolverModifiers.Single().IsOpaque);

        // 谓词闭包的变化不会被伪装成快照中的静态规则值。
        allowed = false;
        Assert.True(snapshot.Master.HasOpaqueSolverRules);
    }

    [Fact]
    public void ShipInventoryIsDetachedAndPartOfTheWorldVersion()
    {
        var master = new Workshop("主台", new List<string> { "圆罐" });
        var roster = new WorkshopRoster(new[] { master }, master);
        var ship = new ShipLiquidInventory(4,
            new Dictionary<string, int> { ["酒"] = 1 });

        var before = WorldSnapshotFactory.Capture(roster, ship);
        Assert.NotNull(before.ShipLiquidInventory);
        Assert.Equal(4, before.ShipLiquidInventory!.Capacity);
        Assert.Equal(1, before.ShipLiquidInventory.Balances["酒"]);

        ship.Produce("蜜", 1);
        var after = WorldSnapshotFactory.Capture(roster, ship);

        Assert.NotEqual(before.Version, after.Version);
        Assert.False(before.ShipLiquidInventory.Balances.ContainsKey("蜜"));
        Assert.Equal(2, after.ShipLiquidInventory!.UsedCapacity);
        Assert.Equal(1, after.ShipLiquidInventory.Balances["蜜"]);
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Domain;
using JarSystem.Core.Services;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 罐子对象池（<see cref="JarPool"/>）的契约测试。
///
/// 池化的**唯一危险**是"回收后仍被别处引用"：两处代码持有同一只罐子，互相覆盖且不报错。
/// 所以本组测试的重点不是"能不能复用"，而是**复用之后旧引用必须炸**、
/// 以及**复用不得让世界状态发生任何变化**。
/// </summary>
public class JarPoolTests
{
    [Fact]
    public void Rent_ReusesReturnedJar()
    {
        var pool = new JarPool();
        var a = pool.Rent("圆罐");
        Assert.Equal(1, pool.Created);
        Assert.True(pool.Return(a));
        var b = pool.Rent("方罐");
        Assert.Same(a, b);                 // 同一个对象被复用
        Assert.Equal(1, pool.Created);     // 没有新建第二只
        Assert.Equal(2, pool.Rented);
        Assert.Equal("方罐", b.Shape);
    }

    [Fact]
    public void ReturnedJarThrowsOnAnyUse()
    {
        var pool = new JarPool();
        var jar = pool.Rent("圆罐", "酒", 7);
        Assert.True(pool.Return(jar));

        Assert.True(jar.IsRecycled);
        Assert.Throws<InvalidOperationException>(() => jar.Shape);
        Assert.Throws<InvalidOperationException>(() => jar.Id);
        Assert.Throws<InvalidOperationException>(() => jar.GetDescription());
        Assert.Throws<InvalidOperationException>(() => jar.IsEmpty());
        Assert.Throws<InvalidOperationException>(() => jar.DeepClone());
    }

    [Fact]
    public void Rent_ResetsStateCompletely()
    {
        var pool = new JarPool();
        var dirty = pool.Rent("圆罐", "酒", 42);
        dirty.Mark();
        pool.Return(dirty);

        var fresh = pool.Rent("方罐");
        Assert.Null(fresh.Liquid);        // 液体不能带过来
        Assert.Null(fresh.Id);            // Id 由工作台分配，复用时不继承
        Assert.False(fresh.IsMarked);     // 一罐一操作标记必须清
        Assert.False(fresh.IsRecycled);
    }

    [Fact]
    public void DoubleReturnIsRejected()
    {
        var pool = new JarPool();
        var jar = pool.Rent("圆罐");
        Assert.True(pool.Return(jar));
        Assert.False(pool.Return(jar));   // 重复归还：说明两处都在还同一只
        Assert.Equal(1, pool.Returned);
    }

    [Fact]
    public void PoolIsBoundedByMaxIdle()
    {
        var pool = new JarPool(maxIdle: 1);
        var a = pool.Rent("圆罐");
        var b = pool.Rent("圆罐");
        Assert.True(pool.Return(a));
        Assert.False(pool.Return(b));     // 池满：直接交给 GC，不影响正确性
        Assert.Equal(1, pool.Idle);
    }

    [Fact]
    public void InvalidShapeIsNotRecycled()
    {
        var pool = new JarPool();
        pool.Return(pool.Rent("圆罐"));
        Assert.Throws<ArgumentException>(() => pool.Rent("不是形状"));
    }

    /// <summary>
    /// **复用对世界不可见**：两个同种子、同命令序列的世界，一个池容量为 0（等于不池化）、
    /// 一个正常池化，最终状态必须逐字相同。这是"池化不改变确定性"的直接证明。
    /// </summary>
    [Fact]
    public void PoolingDoesNotChangeWorldState()
    {
        var plain = Build(ticks: 30);
        var pooled = Build(ticks: 30);
        Assert.Equal(plain, pooled);
    }

    private static string Build(int ticks)
    {
        var sys = new JarSystem.Core.JarSystem(
            randomSeed: 20260101,
            jarIds: new SequentialIdGenerator(),
            orderIds: new SequentialIdGenerator());
        sys.Tick(ticks);
        // 用固定顺序的 JSON 投影比对：属性顺序由 DTO 定义决定，因此逐字可比。
        return System.Text.Json.JsonSerializer.Serialize(StateSerializer.Snapshot(sys));
    }
}

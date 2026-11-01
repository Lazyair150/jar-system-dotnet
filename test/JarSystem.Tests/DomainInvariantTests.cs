using System.Text.Json;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Orders;
using Xunit;

// 别名：本文件位于 JarSystem.* 命名空间下，用别名可避免 `JarSystem` 既是命名空间又是类型带来的歧义
using CoreSystem = JarSystem.Core.JarSystem;
using JarSystem.Core.Abstractions;

namespace JarSystem.Tests;

/// <summary>
/// 核心不变量测试（P2/P3 的第一步）。
///
/// 定位：**增量**，不替代现有基线。
/// <list type="bullet">
/// <item>旧 runner（<c>test/JarSystem.Test</c>：2000 随机 + 3000 容量 + 20 确定性）仍是"基线守护"，
/// 留在 <c>verify.bat</c> 第 2 步，输出字符串不变；</item>
/// <item>这里放的是**小、快、语义明确**的断言：世界装配、词表、配置等价、可行性与执行的双向一致。</item>
/// </list>
/// </summary>
public class DomainInvariantTests
{
    /// <summary>内置默认世界：4 个工作台 + 4 个生产者 + 每种液体 20 —— 与重构前逐值一致。</summary>
    [Fact]
    public void DefaultWorld_HasFourWorkshops_FourProducers_AndInitialStock()
    {
        var sys = new CoreSystem();

        Assert.Equal(4, sys.Workshops.Count);
        Assert.Equal(4, sys.ProducerModule.Producers.Count);
        Assert.Equal(20, sys.ProducerModule.LiquidWarehouse[Constants.LIQUIDS[0]]);
        Assert.Equal(20, sys.ProducerModule.LiquidWarehouse[Constants.LIQUIDS[1]]);
        Assert.Equal(3, sys.FlowSettings.JarCount);
        Assert.Equal(2, sys.FlowSettings.LiquidAmount);
    }

    /// <summary>词表（S2）：形状/液体目录是唯一权威，且默认等于内置常量。</summary>
    [Fact]
    public void Vocabulary_Default_MatchesBuiltInConstants()
    {
        var vocab = Vocabulary.Default;

        Assert.Equal(Constants.SHAPES.Count, vocab.Shapes.Count);
        Assert.Equal(Constants.LIQUIDS.Count, vocab.Liquids.Count);
        foreach (var s in Constants.SHAPES) Assert.True(vocab.IsValidShape(s));
        foreach (var l in Constants.LIQUIDS) Assert.True(vocab.IsValidLiquid(l));
        Assert.False(vocab.IsValidShape("不存在的形状"));
    }

    /// <summary>配置装配（S3）：用 <see cref="WorldOptions.Default"/> 装配出的世界，与内置世界逐字节一致。</summary>
    [Fact]
    public void WorldAssembledFromDefaultOptions_EqualsBuiltInWorld()
    {
        var builtIn = new CoreSystem();
        var fromConfig = new CoreSystem(world: WorldOptions.Default);

        var options = new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = false };
        Assert.Equal(
            JsonSerializer.Serialize(builtIn.GetState(), options),
            JsonSerializer.Serialize(fromConfig.GetState(), options));
    }

    /// <summary>
    /// 可行性与执行的双向一致（可行性分析有效性的最小断言）：
    /// 判定可行 ⇒ 执行必须成功；判定不可行 ⇒ 执行必须失败。
    /// </summary>
    [Fact]
    public void FeasibilityAndExecution_AreConsistentInBothDirections()
    {
        var master = new Workshop("M", new() { "圆罐" }, new(), new(), 20, 30);
        master.AddJar(new Jar("圆罐"));
        var slave = new Workshop("S", new() { "方罐" }, new(), new(), 20, 30);

        var sys = new CoreSystem();
        sys.ClearWorkshops();
        sys.AddWorkshop(master);
        sys.AddWorkshop(slave);
        sys.SetMasterIndex(0);
        sys.SetSlaveIndex(1);
        sys.CreateOrder(new() { new Requirement { Shape = "圆罐" } }, Constants.OrderType.A);

        var feasibility = sys.CheckFeasibility(0);
        var execution = sys.ExecuteOrder(0);

        Assert.NotNull(feasibility);
        Assert.Equal(feasibility!.Feasible, execution.Success);
    }

    // ============================================================
    // 下一步（装好包之后按这两个模式补）：
    //
    // ① 性质测试（FsCheck）：把旧 runner 里"跑 2000 个随机场景检查守恒"改写成一条性质 ——
    //    失败会自动**收缩**出最小反例并给出可重放的种子，比"跑很多次"强得多。
    //      [Property(MaxTest = 200, Replay = "12345,67890")]
    //      public void JarsAreConserved(Scenario s) => ...
    //
    // ② 快照测试（Verify）：把 <c>GetState()</c> 的 JSON 直接做审批基线 ——
    //    与现有 **223 行**文本快照互补（那份靠 fc，这份靠 *.verified.json + scrubber 处理 timestamp）。
    //      [UsesVerify]
    //      public Task State_MatchesApprovedSnapshot() => Verify(state);
    // ============================================================
}

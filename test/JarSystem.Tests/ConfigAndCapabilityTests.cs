using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 配置与能力测试：覆盖 S1（`WorkshopCapability`）、S2（词表注入）、S3（世界配置与校验）。
///
/// 这批逻辑此前只被 CLI（`--dump-contract --config`）与契约快照**间接**覆盖；
/// 这里用**纯 `[Fact]`** 直接断言，且不需要额外 NuGet 包。
/// </summary>
public class ConfigAndCapabilityTests
{
    /// <summary>深拷贝默认配置。WorldOptions.Default 现在每次访问都返回**新实例**（已不是共享静态单例），
    /// 但这里仍走一次 JSON 往返：既验证序列化往返无损，也让测试拿到的是一份与任何全局状态无关的配置。</summary>
    private static WorldOptions CloneDefault() =>
        WorldOptionsJson.FromJson(WorldOptionsJson.ToJson(WorldOptions.Default));

    [Fact]
    public void DefaultWorldOptions_IsValid()
    {
        var report = CloneDefault().Validate();

        Assert.True(report.IsValid);
        Assert.Empty(report.Errors);
        Assert.Empty(report.Warnings);   // 默认世界里每个形状都有生产者 ⇒ 连警告都没有
    }

    /// <summary>S2 的围栏：液体**数量恰好两个**、且**名字与顺序不可改**（求解/执行仍按内置名遍历）。</summary>
    [Fact]
    public void ChangingLiquidNamesOrCount_IsRejected()
    {
        var renamed = CloneDefault();
        renamed.Liquids = [Constants.LIQUIDS[1], Constants.LIQUIDS[0]];   // 只换顺序
        Assert.False(renamed.Validate().IsValid);

        var extra = CloneDefault();
        extra.Liquids = [Constants.LIQUIDS[0], Constants.LIQUIDS[1], "醋"];
        Assert.False(extra.Validate().IsValid);
    }

    [Fact]
    public void UnknownShapeOrDuplicateNames_AreRejected()
    {
        var unknownShape = CloneDefault();
        unknownShape.Workshops[0].BaseMarks.Add("不存在的形状");
        Assert.Contains(unknownShape.Validate().Errors, e => e.Contains("未知形状"));

        var duplicateProducer = CloneDefault();
        duplicateProducer.Producers[1].Name = duplicateProducer.Producers[0].Name;
        Assert.Contains(duplicateProducer.Validate().Errors, e => e.Contains("生产者名重复"));

        var duplicateWorkshop = CloneDefault();
        duplicateWorkshop.Workshops[1].Name = duplicateWorkshop.Workshops[0].Name;
        Assert.Contains(duplicateWorkshop.Validate().Errors, e => e.Contains("工作台名重复"));
    }

    /// <summary>"某形状没有任何生产者"只是**警告**（该形状永远产不出来），不阻断启动。</summary>
    [Fact]
    public void ShapeWithoutProducer_IsWarningNotError()
    {
        var noProducers = CloneDefault();
        noProducers.Producers.Clear();

        var report = noProducers.Validate();

        Assert.True(report.IsValid);
        Assert.NotEmpty(report.Warnings);
        Assert.Contains(report.Warnings, w => w.Contains("没有任何生产者"));
    }

    [Fact]
    public void NegativeProducerSlotsAreRejected()
    {
        var world = CloneDefault();
        world.ProducerSlots = -1;

        Assert.Contains(world.Validate().Errors, e => e.Contains("producerSlots"));
    }

    [Fact]
    public void NonFiniteProducerRateIsRejected()
    {
        var world = CloneDefault();
        world.Producers[0].Rates["圆罐"] = double.NaN;

        Assert.Contains(world.Validate().Errors, e => e.Contains("有限数"));
    }

    [Fact]
    public void LiveWorldConfigCannotBeMutatedThroughAliases()
    {
        var configured = CloneDefault();
        configured.GoalOrders = 1;
        configured.TurnLimit = 3;
        var sys = new JarSystem.Core.JarSystem(
            randomSeed: 1, world: configured,
            jarIds: new SequentialIdGenerator(), orderIds: new SequentialIdGenerator());

        configured.GoalOrders = 99;
        configured.Flow.LiquidRate[Constants.LIQUIDS[0]] = 99;
        var returned = sys.World;
        returned.GoalOrders = 88;
        returned.Workshops.Clear();

        Assert.Equal(1, sys.WorldConfig.GoalOrders);
        Assert.Equal(3, sys.WorldConfig.TurnLimit);
        Assert.NotEqual(99, sys.World.Flow.LiquidRate[Constants.LIQUIDS[0]]);
        Assert.NotEmpty(sys.Workshops);
    }

    /// <summary>S1：<see cref="Workshop.ReplaceCapability"/> 只换规则参数，不动资源；标记/禁令立即按新能力生效。</summary>
    [Fact]
    public void ReplaceCapability_SwapsRulesWithoutTouchingResources()
    {
        var ws = new Workshop("W", new() { "圆罐" }, new(), new(), 20, 30);
        ws.AddJar(new Jar("圆罐"));
        Assert.True(ws.IsBaseMarked("圆罐"));
        Assert.False(ws.IsBanned(Constants.LIQUIDS[0]));

        ws.ReplaceCapability(new WorkshopCapability(new[] { "方罐" }, null, new[] { Constants.LIQUIDS[0] }));

        Assert.False(ws.IsBaseMarked("圆罐"));            // 新能力里没有圆罐
        Assert.True(ws.IsBaseMarked("方罐"));
        Assert.True(ws.IsBanned(Constants.LIQUIDS[0]));   // 新能力禁第一种液体
        Assert.Single(ws.Jars);                          // 资源**不受影响**
    }

    /// <summary>S1/C：临时附加标记是**运行时状态**，作用域内可见、结束即回收。</summary>
    [Fact]
    public void TemporaryAdditionalMarks_AreScopedAndReleased()
    {
        var ws = new Workshop("W", new() { "圆罐" }, new(), new(), 20, 30);
        Assert.DoesNotContain("方罐", ws.GetPossibleTransforms("圆罐"));

        using (ws.WithTemporaryAdditionalMarks(new[] { "方罐" }))
        {
            Assert.Contains("方罐", ws.GetPossibleTransforms("圆罐"));
            Assert.True(ws.IsAdditionalMarked("方罐"));
        }

        Assert.DoesNotContain("方罐", ws.GetPossibleTransforms("圆罐"));
        Assert.False(ws.IsAdditionalMarked("方罐"));
    }

    /// <summary>S2：形状合法性由**创建该罐子时的词表**裁决 —— 自定义词表下新形状合法，默认词表下不合法。</summary>
    [Fact]
    public void ShapeValidation_FollowsTheJarOwnVocabulary()
    {
        var custom = new Vocabulary([.. Constants.SHAPES, "新形状"], Constants.LIQUIDS);

        var jar = new Jar("新形状", null, null, custom);   // 合法
        Assert.Equal("新形状", jar.Shape);
        Assert.True(jar.IsValidShapeIn("新形状"));

        Assert.Throws<ArgumentException>(() => new Jar("新形状"));   // 默认词表里没有它
    }

    /// <summary>S3b：随仓库提供的 `config/world.json` 必须能解析且校验通过（宿主启动会读它）。</summary>
    [Fact]
    public void ShippedWorldConfig_ParsesAndValidates()
    {
        // 注意：**不能假设当前工作目录是仓库根** —— 测试运行器（xUnit v3 的 in-process runner）的 cwd
        // 与 `dotnet run --project` 的 CLI 程序不同（这条测试第一次跑就因此失败）。
        // 这里采用与宿主 JarSystemService 相同的查找策略：当前目录 → 应用目录 → 向上逐级。
        var path = FindUpward(Path.Combine("config", "world.json"));
        Assert.NotNull(path);

        var world = WorldOptionsJson.FromJson(File.ReadAllText(path!));

        Assert.True(world.Validate().IsValid);
        Assert.Equal(Constants.SHAPES.Count, world.Shapes.Count);
        Assert.Equal(Constants.LIQUIDS.Count, world.Liquids.Count);
        Assert.Equal(4, world.Workshops.Count);
        Assert.Equal(4, world.Producers.Count);
    }

    /// <summary>
    /// 从当前目录与应用目录向上找文件（与宿主侧配置查找同一套策略）。
    /// 层数必须够：`test\JarSystem.Tests\bin\Debug\net10.0\` 到仓库根要向上 **6 层**。
    /// </summary>
    private static string? FindUpward(string relativePath)
    {
        var candidates = new List<string> { Path.Combine(Directory.GetCurrentDirectory(), relativePath) };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            candidates.Add(Path.Combine(dir.FullName, relativePath));
            dir = dir.Parent;
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

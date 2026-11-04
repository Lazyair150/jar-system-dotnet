using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// 关卡「订单实例表」的解析契约：两种写法逐字等价，坏条目一律开局前失败。
///
/// 为什么要有这些测试：订单表是**关卡内容与运行时的契约**——它决定了玩家开局看到几张单。
/// 解析器写错时页面不会报错，只会"少一张单"，而少一张单等于改掉了这一关的玩法。
/// </summary>
public class LevelOrderInstancesTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly string[] Templates = ["一只三角罐", "一只圆罐"];

    [Fact]
    public void PlainNamesAndTemplateObjectsProduceTheSameList()
    {
        var plain = LevelCatalog.ReadOrderInstances(Root("""{"orders":["一只三角罐","一只三角罐"]}"""),
            Templates, "t.json");
        var objects = LevelCatalog.ReadOrderInstances(
            Root("""{"orders":[{"template":"一只三角罐"},{"template":"一只三角罐"}]}"""),
            Templates, "t.json");

        Assert.Equal(new[] { "一只三角罐", "一只三角罐" }, plain);
        Assert.Equal(plain, objects);
    }

    [Fact]
    public void AMissingFieldKeepsTheOldRuleOfOneOrderPerTemplate()
    {
        var orders = LevelCatalog.ReadOrderInstances(Root("{}"), Templates, "t.json");

        Assert.Equal(Templates, orders);
    }

    [Fact]
    public void AnUnknownTemplateFailsBeforePlay()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LevelCatalog.ReadOrderInstances(Root("""{"orders":["不存在的模板"]}"""), Templates, "t.json"));

        Assert.Contains("不存在的模板", ex.Message);
        Assert.Contains("这一关的模板", ex.Message);
    }

    [Fact]
    public void AnEntryThatIsNeitherAStringNorATemplateObjectFails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LevelCatalog.ReadOrderInstances(Root("""{"orders":[{"template":42}]}"""), Templates, "t.json"));

        Assert.Contains("认不出的条目", ex.Message);
    }

    [Fact]
    public void AnEmptyArrayIsRejectedBecauseAnEmptyTableIsNotAGame()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LevelCatalog.ReadOrderInstances(Root("""{"orders":[]}"""), Templates, "t.json"));

        Assert.Contains("空数组", ex.Message);
    }

    [Fact]
    public void EveryShippedLevelAuthorsAtLeastOneOrderInstance()
    {
        var levels = LevelCatalog.Discover();

        Assert.NotEmpty(levels);
        Assert.All(levels, l => Assert.NotEmpty(l.Orders));
        // 第六关是唯一显式列两张同名单的关：它就是"抢资源"那一课的证据。
        var sixth = levels.Single(l => l.Index == 6);
        Assert.Equal(2, sixth.Orders.Count);
        Assert.Equal(sixth.Orders[0], sixth.Orders[1]);
    }
}

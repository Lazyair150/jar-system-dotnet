using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;

namespace JarSystem.Core.Bootstrap;

/// <summary>
/// 默认世界装配（从 <c>JarSystem.InitializeDefault</c> 迁出，让门面只做转发）。
/// 装配内容与调用顺序与重构前逐字一致：4 个生产者 → 酒/蜜各 20 → 模板工作台 + 青龙台 + 白虎台。
/// </summary>
public static class SystemBootstrapper
{
    /// <summary>向给定仓库与工作台列表装配默认世界（不清理已有内容，与重构前一致）。</summary>
    public static void ApplyDefault(ProducerModule producers, List<Workshop> workshops)
    {
        producers.RegisterProducer("陶工甲", new() { ["圆罐"] = 0.4, ["方罐"] = 0.3 });
        producers.RegisterProducer("陶工乙", new() { ["高罐"] = 0.5, ["长罐"] = 0.2 });
        producers.RegisterProducer("机器A", new() { ["三角罐"] = 0.6, ["扁罐"] = 0.3 });
        producers.RegisterProducer("机器B", new() { ["鼓罐"] = 0.4, ["尖罐"] = 0.3 });

        producers.ProduceLiquid("酒", 20);
        producers.ProduceLiquid("蜜", 20);

        foreach (var t in Rules.WORKSHOP_TEMPLATES)
            workshops.Add(new Workshop(
                t.Name,
                new List<string>(t.BaseMarks),
                new List<string>(t.AdditionalMarks),
                new List<string>(t.Ban)));

        workshops.Add(new Workshop("青龙台", new List<string> { "三角罐" }, new List<string> { "圆罐" }, new List<string> { "蜜" }));
        workshops.Add(new Workshop("白虎台", new List<string> { "长罐", "扁罐" }, new List<string> { "鼓罐" }, new List<string>()));
    }
}

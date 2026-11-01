using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// **订单模板**：一张订单的"形状"，不是订单本身。
///
/// <para>为什么需要独立这个概念。改前系统里只有<b>订单实例</b>（<see cref="Order"/>，带 Id、状态、
/// 可行性结论）和两种造它的办法：调用方直接给需求列表，或者 <c>OrderService.CreateRandomOrder</c>
/// 每次现掷骰子。于是"这个世界有哪些订单可派"没有落点 —— 它既不是配置（`WorldOptions` 只描述
/// 工作台/产线），也不是数据文件（`config/world.json` 里没有），只能活在调用方的代码里。</para>
///
/// <para>模板是**内容**（L1），实例是**运行时对象**。分开之后：
/// 模板可以在 JSON 里给出、可以被校验、可以按名字引用；而实例仍然只有一个创建入口
/// （<c>OrderService.CreateOrder</c>），ID 源与词表照旧由门面注入，回放语义不变。</para>
///
/// <para>模板**不可变**：它的需求列表在构造时复制成只读视图，<see cref="Instantiate"/> 每次
/// 都给出新的一份 —— 否则两张由同一模板生成的订单会共享同一个需求列表，改一张动两张。</para>
/// </summary>
public sealed class OrderTemplate
{
    private readonly IReadOnlyList<Requirement> _requirements;

    public OrderTemplate(string name, string type, IEnumerable<Requirement> requirements)
    {
        Name = name;
        Type = type;
        _requirements = requirements.ToList().AsReadOnly();
    }

    public string Name { get; }

    /// <summary><see cref="Constants.OrderType.A"/> 或 <see cref="Constants.OrderType.B"/>。</summary>
    public string Type { get; }

    public IReadOnlyList<Requirement> Requirements => _requirements;

    /// <summary>按本模板造一份**全新的**需求列表（每次调用都是新实例）。</summary>
    public List<Requirement> Instantiate()
        => _requirements.Select(r => new Requirement { Shape = r.Shape, Liquid = r.Liquid }).ToList();

    public override string ToString() => $"{Name}({Type}, {_requirements.Count} 需求)";
}

/// <summary>
/// **订单模板库**：一组按名字索引的 <see cref="OrderTemplate"/>。
///
/// <para>它是一个**值**而不是可变注册表：构造后不再改变，因此可以安全地被多个世界共享，
/// 也不会成为"某个世界改了库、别的世界跟着变"的隐秘耦合。世界的模板库来自
/// <see cref="WorldOptions.OrderTemplates"/>（见 <see cref="FromOptions"/>）。</para>
///
/// <para>校验放在这一层而不是 <see cref="WorldOptions"/> 的原因：Config 是第 0 层，
/// 不能引用 <see cref="Requirement"/>（第 2 层）与 <see cref="DomainValidationException"/>（第 1 层）。
/// 模板库在 Orders 层，两样都能用，于是"配置写错了"能在装配时**当场**报出可读的原因，
/// 而不是等第一次派单才炸。</para>
/// </summary>
public sealed class OrderTemplateLibrary
{
    /// <summary>空库：这个世界不提供模板（订单只能靠显式需求或随机生成）。</summary>
    public static OrderTemplateLibrary Empty { get; } = new(new List<OrderTemplate>(), null);

    private readonly List<OrderTemplate> _templates;
    private readonly Dictionary<string, int> _byName;
    private readonly IReadOnlyList<OrderTemplate> _view;

    private OrderTemplateLibrary(List<OrderTemplate> templates, Dictionary<string, int>? byName)
    {
        _templates = templates;
        _byName = byName ?? templates
            .Select((t, i) => (t.Name, i))
            .ToDictionary(p => p.Name, p => p.i, StringComparer.Ordinal);
        _view = _templates.AsReadOnly();
    }

    /// <summary>模板数。</summary>
    public int Count => _templates.Count;

    public bool IsEmpty => _templates.Count == 0;

    /// <summary>按配置顺序排列的模板（只读视图）。</summary>
    public IReadOnlyList<OrderTemplate> Templates => _view;

    /// <summary>全部模板名（按配置顺序）。</summary>
    public IReadOnlyList<string> Names => _templates.Select(t => t.Name).ToList().AsReadOnly();

    /// <summary>
    /// 从配置构造并校验。
    /// </summary>
    /// <param name="options">配置里的模板清单；null 或空返回 <see cref="Empty"/>。</param>
    /// <param name="allowedShapes">可选：允许的形状集合（通常传词表的 <c>Shapes</c>）。给了就校验。</param>
    /// <param name="allowedLiquids">可选：允许的液体集合（通常传词表的 <c>Liquids</c>）。给了就校验。</param>
    /// <exception cref="DomainValidationException">名字空/重复、类型不是 A/B、需求条数越界、形状或液体不在词表里。</exception>
    public static OrderTemplateLibrary FromOptions(
        IEnumerable<OrderTemplateOptions>? options,
        IReadOnlyCollection<string>? allowedShapes = null,
        IReadOnlyCollection<string>? allowedLiquids = null)
    {
        var list = options?.ToList() ?? new List<OrderTemplateOptions>();
        if (list.Count == 0) return Empty;

        var shapes = allowedShapes == null ? null : new HashSet<string>(allowedShapes, StringComparer.Ordinal);
        var liquids = allowedLiquids == null ? null : new HashSet<string>(allowedLiquids, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<OrderTemplate>(list.Count);

        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            var where = $"订单模板 #{i}";
            var name = (o.Name ?? "").Trim();
            if (name.Length == 0)
                throw new DomainValidationException($"{where}：名字不能为空");
            if (!seen.Add(name))
                throw new DomainValidationException($"{where}：名字重复 —— \"{name}\"");

            var type = string.IsNullOrEmpty(o.Type) ? Constants.OrderType.A : o.Type;
            if (type != Constants.OrderType.A && type != Constants.OrderType.B)
                throw new DomainValidationException(
                    $"{where}（{name}）：类型必须是 \"{Constants.OrderType.A}\" 或 \"{Constants.OrderType.B}\"，实际是 \"{type}\"");

            var reqs = o.Requirements ?? new List<RequirementOptions>();
            if (reqs.Count == 0)
                throw new DomainValidationException($"{where}（{name}）：至少要有一条需求");
            if (reqs.Count > Constants.MAX_ORDER_REQUIREMENTS)
                throw new DomainValidationException(
                    $"{where}（{name}）：需求 {reqs.Count} 条，超过上限 {Constants.MAX_ORDER_REQUIREMENTS}");

            var requirements = new List<Requirement>(reqs.Count);
            for (int k = 0; k < reqs.Count; k++)
            {
                var r = reqs[k];
                var shape = (r.Shape ?? "").Trim();
                if (shape.Length == 0)
                    throw new DomainValidationException($"{where}（{name}）：第 {k + 1} 条需求没有形状");
                if (shapes != null && !shapes.Contains(shape))
                    throw new DomainValidationException(
                        $"{where}（{name}）：第 {k + 1} 条需求的形状 \"{shape}\" 不在词表里");

                var liquid = string.IsNullOrEmpty(r.Liquid) ? null : r.Liquid;
                if (liquid != null && liquids != null && !liquids.Contains(liquid))
                    throw new DomainValidationException(
                        $"{where}（{name}）：第 {k + 1} 条需求的液体 \"{liquid}\" 不在词表里");

                requirements.Add(new Requirement { Shape = shape, Liquid = liquid });
            }

            // 与 Order 构造函数的顺序不变量对齐：空罐需求在前、满罐需求在后。
            // 这是**装配时**就该报的错 —— 让它漏到派单时才炸，等于把配置错误伪装成运行时故障。
            // （此条是补的：最初漏了，被 RandomOrderComesFromTheLibrary 抓到。）
            bool seenFull = false;
            for (int k = 0; k < requirements.Count; k++)
            {
                if (requirements[k].Liquid != null) { seenFull = true; continue; }
                if (seenFull)
                    throw new DomainValidationException(
                        $"{where}（{name}）：第 {k + 1} 条是空罐需求，却排在满罐需求之后 —— " +
                        "需求顺序约定是「空罐在前、满罐在后」");
            }

            result.Add(new OrderTemplate(name, type, requirements));
        }

        return new OrderTemplateLibrary(result, null);
    }

    /// <summary>按下标取模板（越界抛 <see cref="ArgumentOutOfRangeException"/>）。</summary>
    public OrderTemplate Get(int index)
    {
        if (index < 0 || index >= _templates.Count)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"模板下标 {index} 越界（共 {_templates.Count} 个）");
        return _templates[index];
    }

    /// <summary>按名字取模板；不存在返回 false（不抛异常 —— 名字来自外部输入是常态）。</summary>
    public bool TryGet(string? name, out OrderTemplate template)
    {
        if (name != null && _byName.TryGetValue(name.Trim(), out int i))
        {
            template = _templates[i];
            return true;
        }
        template = null!;
        return false;
    }

    /// <summary>按名字取下标；不存在返回 -1。</summary>
    public int IndexOf(string? name)
        => name != null && _byName.TryGetValue(name.Trim(), out int i) ? i : -1;
}

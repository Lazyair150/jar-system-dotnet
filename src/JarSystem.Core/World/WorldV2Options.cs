using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Production;
using JarSystem.Core.Solver;
using JarSystem.Core.World.Demands;
using JarSystem.Core.World.Solving;

namespace JarSystem.Core.World;

/// <summary>
/// World v2 的独立世界定义。
///
/// 时间契约：逻辑时间增量 1000 = 1 个逻辑时间单位；
/// 生产速率和资源流入速率均表示“每 1 个逻辑时间单位”的数量。
/// 这些字段不复用旧 WorldOptions 中以 tick 为单位的 Flow/Producer 语义。
/// </summary>
public sealed class WorldV2Options
{
    [JsonPropertyName("shapes")]
    public List<string> Shapes { get; set; } = new();
    [JsonPropertyName("liquids")]
    public List<string> Liquids { get; set; } = new() { "酒", "蜜" };

    [JsonPropertyName("producers")]
    public List<WorldV2ProducerOptions> Producers { get; set; } = new();
    [JsonPropertyName("liquidProductionRates")]
    public Dictionary<string, double> LiquidProductionRates { get; set; } = new();
    [JsonPropertyName("initialLiquidStock")]
    public Dictionary<string, int> InitialLiquidStock { get; set; } = new();

    /// <summary>自机液库跨液种共享的总容量。</summary>
    [JsonPropertyName("shipLiquidCapacity")]
    public int ShipLiquidCapacity { get; set; } = 200;

    [JsonPropertyName("jarFlowRatePerLogicalTime")]
    public double JarFlowRatePerLogicalTime { get; set; }
    [JsonPropertyName("liquidFlowRatePerLogicalTime")]
    public double LiquidFlowRatePerLogicalTime { get; set; }

    [JsonPropertyName("workshops")]
    public List<WorldV2WorkshopOptions> Workshops { get; set; } = new();
    [JsonPropertyName("masterWorkshopName")]
    public string MasterWorkshopName { get; set; } = "";
    [JsonPropertyName("slaveWorkshopName")]
    public string? SlaveWorkshopName { get; set; }

    /// <summary>执行计划中每个操作步消耗的逻辑时间毫秒数；1000 = 1 个逻辑时间单位。</summary>
    [JsonPropertyName("executionLogicalTimePerUnit")]
    public long ExecutionLogicalTimePerUnit { get; set; } = 1000;

    [JsonPropertyName("passiveFulfillmentPolicy")]
    public DemandAreaFulfillmentPolicy PassiveFulfillmentPolicy { get; set; } =
        DemandAreaFulfillmentPolicy.OneShot;
    [JsonPropertyName("activeFulfillmentPolicy")]
    public DemandAreaFulfillmentPolicy ActiveFulfillmentPolicy { get; set; } =
        DemandAreaFulfillmentPolicy.OneShot;

    public WorldV2ValidationResult Validate()
    {
        var result = new WorldV2ValidationResult();

        if (Shapes == null || Shapes.Count == 0)
            result.Errors.Add("shapes 不能为空");
        else
        {
            ValidateNames(Shapes, "shapes", result);
        }

        if (Liquids == null || Liquids.Count != 2)
            result.Errors.Add("liquids 必须恰好包含两种液体");
        else
            ValidateNames(Liquids, "liquids", result);

        var shapeSet = new HashSet<string>(Shapes ?? new List<string>(), StringComparer.Ordinal);
        var liquidSet = new HashSet<string>(Liquids ?? new List<string>(), StringComparer.Ordinal);

        if (Producers == null)
            result.Errors.Add("producers 不能为 null");
        else
        {
            var producerNames = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < Producers.Count; i++)
            {
                var producer = Producers[i];
                if (producer == null)
                {
                    result.Errors.Add("producers[" + i + "] 不能为 null");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(producer.Name))
                    result.Errors.Add("生产者缺少 name");
                else if (!producerNames.Add(producer.Name))
                    result.Errors.Add("生产者名重复: " + producer.Name);

                if (producer.Rates == null)
                {
                    result.Errors.Add("生产者 " + producer.Name + " 的 rates 不能为 null");
                    continue;
                }

                foreach (var pair in producer.Rates)
                {
                    if (!shapeSet.Contains(pair.Key))
                        result.Errors.Add("生产者 " + producer.Name + " 含未知形状: " + pair.Key);
                    ValidateRate(pair.Value, "生产者 " + producer.Name + " 的 " + pair.Key, result);
                }
            }
        }

        ValidateRateMap(LiquidProductionRates, liquidSet, "liquidProductionRates", result);
        ValidateStock(InitialLiquidStock, liquidSet, result);
        if (ShipLiquidCapacity < 0)
            result.Errors.Add("shipLiquidCapacity 不能为负数");
        if (InitialLiquidStock != null && InitialLiquidStock.Values.All(amount => amount >= 0))
        {
            try
            {
                var initialLiquidTotal = 0;
                foreach (var amount in InitialLiquidStock.Values)
                    initialLiquidTotal = checked(initialLiquidTotal + amount);
                if (initialLiquidTotal > ShipLiquidCapacity)
                    result.Errors.Add("initialLiquidStock 总量超过 shipLiquidCapacity");
            }
            catch (OverflowException)
            {
                result.Errors.Add("initialLiquidStock 总量超出可表示范围");
            }
        }
        ValidateRate(JarFlowRatePerLogicalTime, "jarFlowRatePerLogicalTime", result);
        ValidateRate(LiquidFlowRatePerLogicalTime, "liquidFlowRatePerLogicalTime", result);

        if (ExecutionLogicalTimePerUnit <= 0)
            result.Errors.Add("executionLogicalTimePerUnit 必须为正数");
        if (!Enum.IsDefined(PassiveFulfillmentPolicy))
            result.Errors.Add("passiveFulfillmentPolicy 无效");
        if (!Enum.IsDefined(ActiveFulfillmentPolicy))
            result.Errors.Add("activeFulfillmentPolicy 无效");

        if (Workshops == null || Workshops.Count == 0)
            result.Errors.Add("workshops 不能为空");
        else
        {
            var workshopNames = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < Workshops.Count; i++)
            {
                var workshop = Workshops[i];
                if (workshop == null)
                {
                    result.Errors.Add("workshops[" + i + "] 不能为 null");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(workshop.Name))
                    result.Errors.Add("工作台缺少 name");
                else if (!workshopNames.Add(workshop.Name))
                    result.Errors.Add("工作台名重复: " + workshop.Name);

                ValidateShapeList(workshop.BaseMarks, shapeSet,
                    "工作台 " + workshop.Name + " 的 baseMarks", result, requireNonEmpty: true);
                ValidateShapeList(workshop.AdditionalMarks, shapeSet,
                    "工作台 " + workshop.Name + " 的 additionalMarks", result, requireNonEmpty: false);

                if (workshop.BaseMarks != null && workshop.AdditionalMarks != null)
                {
                    foreach (var shape in workshop.BaseMarks.Intersect(workshop.AdditionalMarks))
                        result.Errors.Add("工作台 " + workshop.Name + " 的形状 " + shape +
                            " 同时是基础标记与附加标记");
                }

                if (workshop.Ban == null)
                    result.Errors.Add("工作台 " + workshop.Name + " 的 ban 不能为 null");
                else
                {
                    foreach (var ban in workshop.Ban)
                    {
                        var normalized = NormalizeBan(ban);
                        if (!liquidSet.Contains(normalized))
                            result.Errors.Add("工作台 " + workshop.Name + " 的禁令含未知液体: " + ban);
                    }
                }

                if (workshop.MaxJars <= 0)
                    result.Errors.Add("工作台 " + workshop.Name + " 的 maxJars 必须为正数");
                if (workshop.MaxLiquids <= 0)
                    result.Errors.Add("工作台 " + workshop.Name + " 的 maxLiquids 必须为正数");
                if (workshop.LiquidStagingCapacity is < 0)
                    result.Errors.Add("工作台 " + workshop.Name + " 的 liquidStagingCapacity 不能为负数");
            }

            if (string.IsNullOrWhiteSpace(MasterWorkshopName) ||
                !workshopNames.Contains(MasterWorkshopName))
                result.Errors.Add("masterWorkshopName 必须指向已配置的工作台");

            if (!string.IsNullOrWhiteSpace(SlaveWorkshopName))
            {
                if (!workshopNames.Contains(SlaveWorkshopName))
                    result.Errors.Add("slaveWorkshopName 必须指向已配置的工作台");
                if (string.Equals(MasterWorkshopName, SlaveWorkshopName, StringComparison.Ordinal))
                    result.Errors.Add("主工作台和从工作台不能是同一台");
            }
        }

        return result;
    }

    private static void ValidateNames(List<string> names, string field, WorldV2ValidationResult result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                result.Errors.Add(field + " 含非法名称: " + (name ?? "<null>"));
            else if (!seen.Add(name))
                result.Errors.Add(field + " 有重复项: " + name);
        }
    }

    private static void ValidateRateMap(
        Dictionary<string, double> rates,
        HashSet<string> allowedNames,
        string field,
        WorldV2ValidationResult result)
    {
        if (rates == null)
        {
            result.Errors.Add(field + " 不能为 null");
            return;
        }

        foreach (var pair in rates)
        {
            if (!allowedNames.Contains(pair.Key))
                result.Errors.Add(field + " 含未知项目: " + pair.Key);
            ValidateRate(pair.Value, field + "[" + pair.Key + "]", result);
        }
    }

    private static void ValidateStock(
        Dictionary<string, int> stock,
        HashSet<string> allowedNames,
        WorldV2ValidationResult result)
    {
        if (stock == null)
        {
            result.Errors.Add("initialLiquidStock 不能为 null");
            return;
        }

        foreach (var pair in stock)
        {
            if (!allowedNames.Contains(pair.Key))
                result.Errors.Add("initialLiquidStock 含未知液体: " + pair.Key);
            if (pair.Value < 0)
                result.Errors.Add("initialLiquidStock[" + pair.Key + "] 不能为负");
        }
    }

    private static void ValidateRate(double rate, string field, WorldV2ValidationResult result)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate < 0)
            result.Errors.Add(field + " 必须是有限非负数");
        else if (rate > Constants.MAX_PRODUCER_RATE)
            result.Errors.Add(field + " 超出核心允许上限 " + Constants.MAX_PRODUCER_RATE);
    }

    private static void ValidateShapeList(
        List<string> values,
        HashSet<string> shapes,
        string field,
        WorldV2ValidationResult result,
        bool requireNonEmpty)
    {
        if (values == null)
        {
            result.Errors.Add(field + " 不能为 null");
            return;
        }

        if (requireNonEmpty && values.Count == 0)
            result.Errors.Add(field + " 不能为空");
        foreach (var value in values)
            if (!shapes.Contains(value))
                result.Errors.Add(field + " 含未知形状: " + value);
    }

    private static string NormalizeBan(string ban)
    {
        var value = (ban ?? "").Trim();
        return value.StartsWith("禁", StringComparison.Ordinal) && value.Length > 1
            ? value[1..]
            : value;
    }
}

/// <summary>World v2 生产者定义：速率单位为“每逻辑时间单位”。</summary>
public sealed class WorldV2ProducerOptions
{
    public string Name { get; set; } = "";
    public Dictionary<string, double> Rates { get; set; } = new();
}

/// <summary>World v2 工作台定义。</summary>
public sealed class WorldV2WorkshopOptions
{
    public string Name { get; set; } = "";
    public List<string> BaseMarks { get; set; } = new();
    public List<string> AdditionalMarks { get; set; } = new();
    public List<string> Ban { get; set; } = new();
    public int MaxJars { get; set; } = Constants.DEFAULT_MAX_JARS;
    public int MaxLiquids { get; set; } = Constants.DEFAULT_MAX_LIQUIDS;
    /// <summary>新订单暂存区跨液种共享的总容量；与旧 MaxLiquids 单液种上限独立。</summary>
    public int? LiquidStagingCapacity { get; set; }
}

public sealed class WorldV2ValidationResult
{
    public List<string> Errors { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// 把已经校验的 WorldV2Options 装配成一个完整、彼此一致的 WorldV2Context。
/// 工厂不读取文件、不启动线程，也不把旧 WorldOptions 的 tick 参数换算为逻辑时间。
/// </summary>
public static class WorldV2Factory
{
    public static WorldV2Context Create(
        WorldV2Options options,
        WorldClock? clock = null,
        IEnumerable<IWorldOrderSpecificationSolver>? additionalSolvers = null)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        var validation = options.Validate();
        if (!validation.IsValid)
            throw new ArgumentException(
                "World v2 配置无效：" + string.Join("；", validation.Errors),
                nameof(options));

        var vocabulary = new Vocabulary(options.Shapes, options.Liquids);
        var producers = new ProducerModule(vocabulary);

        foreach (var producer in options.Producers)
            producers.RegisterProducer(producer.Name, new Dictionary<string, double>(producer.Rates));

        var liquidRates = vocabulary.Liquids.ToDictionary(
            liquid => liquid,
            liquid => options.LiquidProductionRates.TryGetValue(liquid, out var rate) ? rate : 0d,
            StringComparer.Ordinal);
        var liquidProduction = new LiquidProduction(vocabulary, defaults: liquidRates);

        var shipLiquidInventory = new ShipLiquidInventory(
            options.ShipLiquidCapacity, options.InitialLiquidStock);

        var resources = new ResourceArea(
            producers,
            liquidProduction,
            options.JarFlowRatePerLogicalTime,
            options.LiquidFlowRatePerLogicalTime,
            shipLiquidInventory);

        var workshops = options.Workshops.Select(workshop => new Workshop(
            workshop.Name,
            new List<string>(workshop.BaseMarks),
            new List<string>(workshop.AdditionalMarks),
            new List<string>(workshop.Ban),
            workshop.MaxJars,
            workshop.MaxLiquids,
            vocabulary: vocabulary,
            liquidStagingCapacity: workshop.LiquidStagingCapacity ?? workshop.MaxLiquids)).ToList();

        var master = workshops.Single(workshop =>
            string.Equals(workshop.Name, options.MasterWorkshopName, StringComparison.Ordinal));
        var slave = string.IsNullOrWhiteSpace(options.SlaveWorkshopName)
            ? null
            : workshops.Single(workshop =>
                string.Equals(workshop.Name, options.SlaveWorkshopName, StringComparison.Ordinal));

        var solvers = new List<IWorldOrderSpecificationSolver>
        {
            new JarOrderSpecificationSolver(new StateSpaceSolver())
        };
        if (additionalSolvers != null) solvers.AddRange(additionalSolvers);

        return WorldV2Context.Create(
            resources,
            workshops,
            master,
            slave,
            new WorldOrderSolverRegistry(solvers),
            options.ExecutionLogicalTimePerUnit,
            options.PassiveFulfillmentPolicy,
            options.ActiveFulfillmentPolicy,
            clock,
            shipLiquidInventory);
    }
}

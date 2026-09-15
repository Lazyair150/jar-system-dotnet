using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;

namespace JarSystem.Core.Production;

public class Producer
{
    public string Name { get; }
    public Dictionary<string, double> Rates { get; }

    public Producer(string name, Dictionary<string, double> rates)
    {
        Name = name;
        Rates = new Dictionary<string, double>(rates);
    }
}

public class ShapeProductionLine
{
    public string Shape { get; }
    public List<(string Name, double Rate)> Producers { get; } = new();
    public double Accumulated { get; set; }

    public ShapeProductionLine(string shape) { Shape = shape; }

    public void AddProducer(Producer p)
    {
        if (p.Rates.TryGetValue(Shape, out var rate))
            Producers.Add((p.Name, rate));
    }

    public void RemoveProducer(string name) =>
        Producers.RemoveAll(p => p.Name == name);

    public void UpdateRate(string name, double rate)
    {
        var idx = Producers.FindIndex(p => p.Name == name);
        if (idx >= 0)
            Producers[idx] = (name, rate);
    }

    public List<Jar> Tick()
    {
        var total = Producers.Sum(p => p.Rate);
        Accumulated += total;
        var n = (int)Math.Floor(Accumulated);
        Accumulated -= n;
        var jars = new List<Jar>();
        for (int i = 0; i < n; i++)
            jars.Add(new Jar(Shape));
        return jars;
    }

    public double GetTotalRate() => Producers.Sum(p => p.Rate);

    public Dictionary<string, object> GetStatus() => new()
    {
        ["shape"] = Shape,
        ["rate"] = GetTotalRate(),
        ["accumulated"] = Accumulated,
        ["producers"] = Producers.Count
    };
}

public class ProducerModule
{
    private readonly Dictionary<string, ShapeProductionLine> _generators = new();
    private readonly Dictionary<string, Producer> _producers = new();
    private readonly List<Jar> _warehouse = new();
    private readonly Dictionary<string, int> _liquidWarehouse = new() { ["酒"] = 0, ["蜜"] = 0 };
    private readonly Dictionary<string, int> _discardedLiquids = new() { ["酒"] = 0, ["蜜"] = 0 };

    /// <summary>各形状的生产线（只读视图）。</summary>
    public IReadOnlyDictionary<string, ShapeProductionLine> Generators => _generators;

    /// <summary>已注册的生产者（只读视图）。</summary>
    public IReadOnlyDictionary<string, Producer> Producers => _producers;

    /// <summary>中央仓库中的罐子（只读视图；增删只能经 Tick / FlowToMaster）。</summary>
    public IReadOnlyList<Jar> Warehouse => _warehouse;

    /// <summary>中央仓库液体库存（只读视图；变更只能经 ProduceLiquid / FlowToMaster）。</summary>
    public IReadOnlyDictionary<string, int> LiquidWarehouse => _liquidWarehouse;

    /// <summary>因仓库上限而丢弃的液体计数（只读视图）。</summary>
    public IReadOnlyDictionary<string, int> DiscardedLiquids => _discardedLiquids;

    /// <summary>仓库中的罐子数量（替代原先泄漏内部列表的 GetWarehouseJars）。</summary>
    public int WarehouseJarCount => _warehouse.Count;

    /// <summary>因仓库/工作台容量上限而丢弃的罐子计数。</summary>
    public int DiscardedJars { get; private set; }

    public void DiscardJars(int n) => DiscardedJars += n;
    public void DiscardLiquids(string liquid, int n) =>
        _discardedLiquids[liquid] = _discardedLiquids.GetValueOrDefault(liquid, 0) + n;

    public ProducerModule()
    {
        foreach (var s in Constants.SHAPES)
            _generators[s] = new ShapeProductionLine(s);
    }

    public Producer RegisterProducer(string name, Dictionary<string, double> rates)
    {
        var p = new Producer(name, rates);
        _producers[name] = p;
        foreach (var s in rates.Keys)
            if (_generators.ContainsKey(s))
                _generators[s].AddProducer(p);
        return p;
    }

    public void RemoveProducer(string name)
    {
        _producers.Remove(name);
        foreach (var g in _generators.Values)
            g.RemoveProducer(name);
    }

    public void UpdateRate(string name, string shape, double rate)
    {
        if (_producers.TryGetValue(name, out var p))
        {
            p.Rates[shape] = rate;
            if (_generators.ContainsKey(shape))
                _generators[shape].UpdateRate(name, rate);
        }
    }

    public List<Jar> Tick(int count = 1)
    {
        var newJars = new List<Jar>();
        for (int i = 0; i < count; i++)
        {
            foreach (var s in Constants.SHAPES)
                newJars.AddRange(_generators[s].Tick());
        }
        // 仓库上限：溢出丢弃并计数，避免长跑无限膨胀
        foreach (var j in newJars)
        {
            if (_warehouse.Count < Constants.WAREHOUSE_MAX_JARS) _warehouse.Add(j);
            else DiscardedJars++;
        }
        return newJars;
    }

    public void ProduceLiquid(string liquid, int amount)
    {
        var cur = _liquidWarehouse.GetValueOrDefault(liquid, 0);
        var accepted = Math.Min(amount, Math.Max(0, Constants.WAREHOUSE_MAX_LIQUIDS - cur));
        _liquidWarehouse[liquid] = cur + accepted;
        _discardedLiquids[liquid] = _discardedLiquids.GetValueOrDefault(liquid, 0) + (amount - accepted);
    }

    public (List<Jar> Jars, Dictionary<string, int> Liquids) FlowToMaster(
        Workshop master, int jarCount = 3, int liquidAmount = 2)
    {
        var transferLog = new Dictionary<string, object>();
        var transferredJars = new List<Jar>();

        // 1. 罐子转移（带容量检查）
        int taken = 0;
        while (taken < jarCount && _warehouse.Count > 0)
        {
            // 主台基础形状优先，减少与主台无关形状的滞留
            int pick = _warehouse.FindIndex(j => master.IsBaseMarked(j.Shape));
            if (pick < 0) pick = 0;
            var jar = _warehouse[pick];
            _warehouse.RemoveAt(pick);
            if (master.AddJar(jar))
            {
                transferredJars.Add(jar);
                taken++;
            }
            else
            {
                // 主台满了，返还仓库
                _warehouse.Insert(pick, jar);
                break;
            }
        }

        // 2. 液体转移（禁令与容量由工作台的意图方法统一裁决）
        var transferredLiquids = new Dictionary<string, int>();
        foreach (var kv in _liquidWarehouse.ToList())
        {
            var liquid = kv.Key;
            var amount = Math.Min(liquidAmount, kv.Value);
            if (amount <= 0) continue;

            // 容量感知的部分填充：主台能收多少收多少，被禁/满则全额留在仓库
            var accepted = master.TryReceiveLiquid(liquid, amount);
            if (accepted <= 0) continue;

            _liquidWarehouse[liquid] = kv.Value - accepted;
            transferredLiquids[liquid] = accepted;
        }

        return (transferredJars, transferredLiquids);
    }

    public int GetWarehouseLiquid(string l) => _liquidWarehouse.GetValueOrDefault(l, 0);

    public Dictionary<string, object> GetProducerStatus()
    {
        return new()
        {
            ["producers"] = _producers.Values.Select(p => new Dictionary<string, object>
            {
                ["name"] = p.Name,
                ["rates"] = new Dictionary<string, double>(p.Rates)
            }).ToList(),
            ["generators"] = _generators.Values.Select(g => g.GetStatus()).ToList(),
            ["warehouse"] = new Dictionary<string, object>
            {
                ["jars"] = _warehouse.Count,
                ["liquids"] = new Dictionary<string, int>(_liquidWarehouse)
            }
        };
    }
}

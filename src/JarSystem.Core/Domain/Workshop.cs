using System;
using System.Collections.Generic;
using System.Linq;
using JarSystem.Core.Abstractions;
using JarSystem.Core.Config;
using JarSystem.Core.Services;

namespace JarSystem.Core.Domain;

/// <summary>
/// 工作台：规则容器（基础/附加标记、禁令）+ 资源容器（罐子、游离液体）+ 资源操作入口。
/// 资源状态只能经本类的意图方法变更，对外只暴露只读视图（阶段 1：消除 §2.3 的跨模块直接改写）。
/// </summary>
public class Workshop
{
    private readonly List<Jar> _jars = new();
    private readonly Dictionary<string, int> _liquidStorage = new();
    private readonly IIdGenerator _ids;
    private readonly List<string> _baseMarks;
    private readonly List<string> _additionalMarks;
    private readonly List<string> _ban;
    // 「临时附加标记」：Case2 期间（旧台临时获得新台基础形状）使用，作用域结束即回收
    private readonly List<string> _temporaryAdditionalMarks = new();

    public string Name { get; }

    /// <summary>基础标记（只读）。</summary>
    public IReadOnlyList<string> BaseMarks => _baseMarks;

    /// <summary>附加标记（只读；转换规则查询会一并考虑临时附加标记）。</summary>
    public IReadOnlyList<string> AdditionalMarks => _additionalMarks;

    /// <summary>禁令（只读；构造时已规范化为裸液体名）。</summary>
    public IReadOnlyList<string> Ban => _ban;

    /// <summary>罐位上限（变更只能经 <see cref="ResizeJarCapacity"/>）。</summary>
    public int MaxJars { get; private set; }

    /// <summary>液体上限（变更只能经 <see cref="ResizeLiquidCapacity"/>）。</summary>
    public int MaxLiquids { get; private set; }

    /// <summary>罐子只读视图。增删只能经 AddJar / RemoveJar / TransferOut / TransferIn / ResizeJarCapacity。</summary>
    public IReadOnlyList<Jar> Jars => _jars;

    /// <summary>游离液体只读视图。变更只能经 AddLiquid / RemoveLiquid / DrainLiquid / TryReceiveLiquid / ResizeLiquidCapacity。</summary>
    public IReadOnlyDictionary<string, int> LiquidStorage => _liquidStorage;

    public Workshop(string name, List<string>? baseMarks = null, List<string>? additionalMarks = null,
        List<string>? ban = null, int? maxJars = null, int? maxLiquids = null, IIdGenerator? ids = null)
    {
        Name = name;
        _baseMarks = new(baseMarks ?? new());
        _additionalMarks = new(additionalMarks ?? new());
        _ban = NormalizeBan(ban);
        MaxJars = maxJars ?? Constants.DEFAULT_MAX_JARS;
        MaxLiquids = maxLiquids ?? Constants.DEFAULT_MAX_LIQUIDS;
        _ids = ids ?? DefaultIdGenerators.Jars;
        foreach (var l in Constants.LIQUIDS) _liquidStorage[l] = 0;
    }

    // 禁令统一存裸液体名（去掉「禁」前缀），避免 "酒" 与 "禁酒" 两种写法并存
    private static List<string> NormalizeBan(IEnumerable<string>? bans)
    {
        var r = new List<string>();
        if (bans == null) return r;
        foreach (var b in bans)
        {
            var v = b.Trim();
            if (v.StartsWith("禁") && v.Length > 1) v = v[1..];
            if (!r.Contains(v)) r.Add(v);
        }
        return r;
    }

    // ============================================================
    // 规则查询
    // ============================================================

    public bool IsBaseMarked(string s) => _baseMarks.Contains(s);

    /// <summary>是否属于附加标记（含 Case2 期间的临时附加标记）。</summary>
    public bool IsAdditionalMarked(string s) => _additionalMarks.Contains(s) || _temporaryAdditionalMarks.Contains(s);

    public bool IsBanned(string l) => _ban.Contains(l) || _ban.Contains("禁" + l);

    public List<string> GetPossibleTransforms(string s) =>
        ShapeRules.GetPossibleTransforms(_baseMarks, EffectiveAdditionalMarks(), s);
    public bool CanTransform(string f, string t) => GetPossibleTransforms(f).Contains(t);

    private List<string> EffectiveAdditionalMarks() =>
        _temporaryAdditionalMarks.Count == 0
            ? _additionalMarks
            : _additionalMarks.Concat(_temporaryAdditionalMarks).ToList();

    /// <summary>
    /// 在作用域内临时获得若干附加标记（切换事件 Case2 用：旧台临时获得新台基础形状）。
    /// 作用域结束（含异常）自动回收本次新增的临时标记；已是基础/附加标记的形状不会重复添加。
    /// </summary>
    public IDisposable WithTemporaryAdditionalMarks(IEnumerable<string> shapes)
    {
        var added = new List<string>();
        foreach (var s in shapes)
        {
            if (_baseMarks.Contains(s) || _additionalMarks.Contains(s) || _temporaryAdditionalMarks.Contains(s)) continue;
            _temporaryAdditionalMarks.Add(s);
            added.Add(s);
        }
        return new TemporaryMarksScope(this, added);
    }

    private sealed class TemporaryMarksScope : IDisposable
    {
        private readonly Workshop _workshop;
        private readonly List<string> _added;

        public TemporaryMarksScope(Workshop workshop, List<string> added)
        {
            _workshop = workshop;
            _added = added;
        }

        public void Dispose()
        {
            foreach (var s in _added) _workshop._temporaryAdditionalMarks.Remove(s);
        }
    }

    // ============================================================
    // 容量与液体
    // ============================================================

    public bool HasJarCapacity() => _jars.Count < MaxJars;

    /// <summary>是否有可用（未被禁且有库存）的游离液体。</summary>
    public bool HasUsableLiquid(string l) => !IsBanned(l) && _liquidStorage.GetValueOrDefault(l) >= 1;

    /// <summary>支付 1 单位改形成本。</summary>
    public bool TryPayReshapeCost(string l)
    {
        if (!HasUsableLiquid(l)) return false;
        _liquidStorage[l]--;
        return true;
    }

    public bool AddLiquid(string l, int n = 1)
    {
        if (IsBanned(l)) return false;
        var c = _liquidStorage.GetValueOrDefault(l);
        if (c + n > MaxLiquids) return false;
        _liquidStorage[l] = c + n;
        return true;
    }

    public bool RemoveLiquid(string l, int n = 1)
    {
        var c = _liquidStorage.GetValueOrDefault(l);
        if (c < n) return false;
        _liquidStorage[l] = c - n;
        return true;
    }

    /// <summary>容量感知地接收液体：返回实际接收量（被禁液体或容量已满一律拒收，不做部分接收之外的补偿）。</summary>
    public int TryReceiveLiquid(string l, int amount)
    {
        if (amount <= 0 || IsBanned(l)) return 0;
        var cur = _liquidStorage.GetValueOrDefault(l);
        var accepted = Math.Min(amount, Math.Max(0, MaxLiquids - cur));
        if (accepted <= 0) return 0;
        _liquidStorage[l] = cur + accepted;
        return accepted;
    }

    /// <summary>清空某种液体的游离库存并返回原数量。</summary>
    public int DrainLiquid(string l)
    {
        var cur = _liquidStorage.GetValueOrDefault(l);
        _liquidStorage[l] = 0;
        return cur;
    }

    /// <summary>调整罐位上限：超出的罐子被移除，返回被移除（丢弃）的数量。</summary>
    public int ResizeJarCapacity(int maxJars)
    {
        var discarded = 0;
        if (maxJars < _jars.Count)
        {
            discarded = _jars.Count - maxJars;
            _jars.RemoveRange(maxJars, discarded);
        }
        MaxJars = maxJars;
        return discarded;
    }

    /// <summary>调整液体上限：超出的部分被裁掉，返回每种液体被裁掉（丢弃）的数量。</summary>
    public Dictionary<string, int> ResizeLiquidCapacity(int maxLiquids)
    {
        var discarded = new Dictionary<string, int>();
        foreach (var l in _liquidStorage.Keys.ToList())
        {
            var cur = _liquidStorage[l];
            if (cur > maxLiquids)
            {
                discarded[l] = cur - maxLiquids;
                _liquidStorage[l] = maxLiquids;
            }
        }
        MaxLiquids = maxLiquids;
        return discarded;
    }

    // ============================================================
    // 罐子
    // ============================================================

    public bool AddJar(Jar j)
    {
        if (!HasJarCapacity()) return false;
        if (j.Id == null) j.Id = _ids.Next();
        _jars.Add(j);
        return true;
    }

    public Jar? RemoveJar(Jar j)
    {
        var i = _jars.IndexOf(j);
        if (i < 0) return null;
        _jars.RemoveAt(i);
        return j;
    }

    /// <summary>移出罐子（转移到别的工作台的第一步）。</summary>
    public Jar? TransferOut(Jar j) => RemoveJar(j);

    /// <summary>接收罐子。</summary>
    public bool TransferIn(Jar? j) => j != null && AddJar(j);

    /// <summary>清除全部罐子的操作标记（订单开始/结束、阶段切换时使用）。</summary>
    public void ClearMarks()
    {
        foreach (var j in _jars) j.Unmark();
    }

    // ============================================================
    // 资源操作（原子步骤；规则校验 + 记账 + 打标记都在这里）
    // ============================================================

    public (bool Success, string? Reason) PourIn(Jar j, string l)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (!j.IsEmpty()) return (false, "罐子不为空");
        if (!_baseMarks.Contains(j.Shape)) return (false, "形状" + j.Shape + "非本工作台基础标记");
        if (IsBanned(l)) return (false, "工作台禁" + l);
        if (!RemoveLiquid(l)) return (false, "液体" + l + "不足");
        j.PourIn(l); j.Mark(); return (true, null);
    }

    public (bool Success, string? Reason, string? Liquid) PourOut(Jar j)
    {
        if (j.IsMarked) return (false, "罐子已被操作", null);
        if (j.IsEmpty()) return (false, "罐子为空", null);
        var l = j.Liquid!;
        if (IsBanned(l)) return (false, "工作台禁" + l, null);
        if (_liquidStorage.GetValueOrDefault(l) >= MaxLiquids) return (false, "液体" + l + "存储已满", null);
        j.ClearLiquid(); AddLiquid(l); j.Mark(); return (true, null, l);
    }

    public (bool Success, string? Reason) ReshapeEmpty(Jar j, string to, string cost)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (!j.IsEmpty()) return (false, "必须为空罐");
        if (!CanTransform(j.Shape, to)) return (false, "无法从" + j.Shape + "转化为" + to);
        if (!TryPayReshapeCost(cost)) return (false, "无法支付液体" + cost);
        j.Reshape(to); j.Mark(); return (true, null);
    }

    public (bool Success, string? Reason) ReshapeFull(Jar j, string to)
    {
        if (j.IsMarked) return (false, "罐子已被操作");
        if (j.IsEmpty()) return (false, "必须为满罐");
        if (!CanTransform(j.Shape, to)) return (false, "无法从" + j.Shape + "转化为" + to);
        var l = j.ClearLiquid();
        if (IsBanned(l!)) { j.PourIn(l!); return (false, "工作台禁" + l); }
        j.Reshape(to); j.Mark(); return (true, null);
    }

    // ============================================================
    // 快照（备忘录）
    // ============================================================

    public WorkshopSnapshot CreateSnapshot() => new()
    {
        Name = Name,
        MaxJars = MaxJars,
        MaxLiquids = MaxLiquids,
        BaseMarks = new List<string>(_baseMarks),
        AdditionalMarks = new List<string>(_additionalMarks),
        Ban = new List<string>(_ban),
        Jars = _jars.Select(j => j.DeepClone()).ToList(),
        Liquids = _liquidStorage.ToDictionary(kv => kv.Key, kv => kv.Value)
    };

    public void RestoreFromSnapshot(WorkshopSnapshot s)
    {
        _jars.Clear();
        _jars.AddRange(s.Jars.Select(j => j.DeepClone()));
        _liquidStorage.Clear();
        foreach (var kv in s.Liquids) _liquidStorage[kv.Key] = kv.Value;
    }
}

/// <summary>
/// 工作台状态快照（不可变备忘录）。注意：按当前实现只恢复罐子与液体，
/// 规则/容量字段为诊断信息保留（审计 §2.10 记录该不一致，未擅自改变行为）。
/// </summary>
public class WorkshopSnapshot
{
    public string Name { get; init; } = "";
    public int MaxJars { get; init; }
    public int MaxLiquids { get; init; }
    public IReadOnlyList<string> BaseMarks { get; init; } = [];
    public IReadOnlyList<string> AdditionalMarks { get; init; } = [];
    public IReadOnlyList<string> Ban { get; init; } = [];
    public IReadOnlyList<Jar> Jars { get; init; } = [];
    public IReadOnlyDictionary<string, int> Liquids { get; init; } = new Dictionary<string, int>();
}

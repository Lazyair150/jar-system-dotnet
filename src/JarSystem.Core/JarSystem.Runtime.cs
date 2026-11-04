using System;
using System.Collections.Generic;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Sessions;
using JarSystem.Core.World;

namespace JarSystem.Core;

/// <summary>门面的世界时间、tick、生产速率与重置职责。</summary>
public partial class JarSystem
{
    /// <summary>世界时间（唯一门面：现在几点、在不在走）。</summary>
    public WorldTime Time { get; }

    /// <summary>时间是否被冻住。<see cref="WorldTimeState.Paused"/> 期间推进请求一律不生效。</summary>
    public bool IsTimePaused => Time.State == WorldTimeState.Paused;

    /// <summary>冻住时间；期间 Tick 不推进、不产出、不写命令日志。</summary>
    public bool PauseTime()
    {
        if (!Time.Pause())
            return false;
        Bus.Publish(new WorldSignal(WorldSignalKind.TimePaused, "pauseTime", null, null,
            Processes.Clock.Tick, _logSeq));
        return true;
    }

    /// <summary>恢复时间流动（幂等：本来就在流动时返回 false）。</summary>
    public bool ResumeTime()
    {
        if (!Time.Resume())
            return false;
        Bus.Publish(new WorldSignal(WorldSignalKind.TimeResumed, "resumeTime", null, null,
            Processes.Clock.Tick, _logSeq));
        return true;
    }

    /// <summary>
    /// 推进 count 个世界步进。每步先走 tick 管线，再按调用边界把仓库资源搬向主台；
    /// 一次批量请求若中途达成结局，会停在第一个结局成立的 tick。
    /// </summary>
    public int Tick(int count = 1)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "tick 数不能为负");
        if (count == 0)
            return 0;
        if (Time.State == WorldTimeState.Paused)
            return 0;

        var turns = Turns;
        if (!turns.Allows(count))
            throw new DomainValidationException(turns.ReasonIfBlocked(count));
        if (Outcome.IsOver)
            throw new DomainValidationException("本关已经结束，无法继续推进；需要继续游玩请重开本关");

        // 同一 tick 内的事件历史共享一个时间点；回放先写入同一日志时间。
        if (!Replaying) Now = DateTimeOffset.UtcNow;
        var applied = 0;
        for (int i = 0; i < count; i++)
        {
            Processes.Pipeline.AdvanceOneTick();
            Time.OnAdvanced();
            applied++;
            if (Outcome.IsOver) break;
        }
        Processes.Pipeline.RunCallBoundary();
        Record(WorldOps.Tick, intA: applied, preserveTime: true);
        return 0;
    }

    public void UpdateProducerRate(string name, string shape, double rate)
    {
        _productionService.UpdateProducerRate(name, shape, rate);
        Record(WorldOps.UpdateProducerRate, name: name,
            baseMarks: new List<string> { shape }, doubleA: rate);
        AddConfigEvent("producer_rate", new Dictionary<string, object?>
        {
            ["producer"] = name,
            ["shape"] = shape,
            ["rate"] = rate
        });
    }

    public void UpdateLiquidProductionRate(string liquid, double rate)
    {
        _productionService.UpdateLiquidProductionRate(liquid, rate);
        Record(WorldOps.UpdateLiquidProductionRate, ban: new List<string> { liquid }, doubleA: rate);
        AddConfigEvent("liquid_rate", new Dictionary<string, object?>
        {
            ["liquid"] = liquid,
            ["rate"] = rate
        });
    }

    public void SetAutoCheck(bool enabled)
    {
        AutoCheckEnabled = enabled;
        Record(WorldOps.SetAutoCheck, flag: enabled);
    }

    /// <summary>替换整个产出模块（旧仓库内容随之丢弃）。</summary>
    public void ResetProducerModule()
    {
        ResetProducerModuleCore();
        Record(WorldOps.ResetProducerModule);
    }

    internal void ResetProducerModuleCore()
    {
        ProducerModule = new Production.ProducerModule(Vocabulary, Modifiers, JarPool);
        ProducerModule.ProducerSlots = _world.ProducerSlots;
    }

    /// <summary>设置某液体的每 tick 产出速率；未知液体由唯一写入口拒绝。</summary>
    public void SetLiquidProductionRate(string liquid, double rate)
    {
        SetLiquidProductionRateCore(liquid, rate);
        Record(WorldOps.SetLiquidProductionRate, ban: new List<string> { liquid }, doubleA: rate);
    }

    internal void SetLiquidProductionRateCore(string liquid, double rate) =>
        _liquidProduction.SetRate(liquid, rate);

    /// <summary>设置主台流入参数；回放与脚本同样受非负校验。</summary>
    public void SetFlowSettings(int jarCount, int liquidAmount)
    {
        if (jarCount < 0) throw new DomainValidationException("流入罐子数不能为负: " + jarCount);
        if (liquidAmount < 0) throw new DomainValidationException("流入液体量不能为负: " + liquidAmount);
        FlowSettings = (jarCount, liquidAmount);
        Record(WorldOps.SetFlowSettings, intA: jarCount, intB: liquidAmount);
        AddConfigEvent("flow_settings", new Dictionary<string, object?>
        {
            ["jarCount"] = jarCount,
            ["liquidAmount"] = liquidAmount
        });
    }

    /// <summary>重置世界并重新装配默认数据（ID 计数器按既有语义不清零）。</summary>
    public void Reset()
    {
        Processes.Clear();
        _completedOrders = 0;
        Bus.ResetPending();
        Presentation.Clear();
        Time.Reset();
        ClearWorkshopsCore();
        ClearOrdersCore();
        CurrentEvent = null;
        _eventHistory.Clear();
        _liquidProduction.Reset(DefaultLiquidRates());
        FlowSettings = (_world.Flow.JarCount, _world.Flow.LiquidAmount);
        AutoCheckEnabled = false;
        ResetProducerModuleCore();
        ResourceLedger.Clear();   /* 重来一局：旧账不留（序号不回绕，所以"清过"仍可辨认） */
        InitializeDefault();
        RecordInitialStock();
        Record(WorldOps.Reset);
    }

    private Dictionary<string, double> DefaultLiquidRates()
    {
        var rates = new Dictionary<string, double>();
        foreach (var liquid in Vocabulary.Liquids)
            rates[liquid] = _world.Flow.LiquidRate.TryGetValue(liquid, out var rate) ? rate : 1;
        return rates;
    }
}

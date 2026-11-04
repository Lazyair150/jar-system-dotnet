using JarSystem.Core.Abstractions;
using JarSystem.Core.Commands;
using JarSystem.Core.Config;
using JarSystem.Core.Domain;
using JarSystem.Core.Executor;
using JarSystem.Core.Orders;
using JarSystem.Core.Process;
using JarSystem.Core.Rules;
using JarSystem.Core.Services;
using Xunit;

namespace JarSystem.Tests;

/// <summary>
/// Process-kernel regressions that cannot be expressed through the public facade
/// without first mutating the world into an invalid state.
/// </summary>
public class ProcessKernelTests
{
    [Fact]
    public void CompletionFailure_DrivesTheProcessOutcome()
    {
        var order = new Order(
            [new Requirement { Shape = "圆罐" }],
            Constants.OrderType.A,
            vocabulary: Vocabulary.Default);
        var master = new Workshop("主台", ["圆罐"], [], [], 20, 30, vocabulary: Vocabulary.Default);
        master.AddJar(new Jar("圆罐", vocabulary: Vocabulary.Default));
        var orderEvent = new OrderEvent(order, master, master);
        orderEvent.Start();

        var session = new OrderExecutionSession(
            order, master, master, orderEvent, new OrderExecutor(new Random(7)),
            new List<SolverStepDto>(), new List<SolverStepDto>(), solverBasedMethod: false,
            recorder: null);
        var advancer = new OrderProcessAdvancer(
            new RejectingCompletion("交付被拒绝"), session);
        var recorder = new NoopRecorder();

        while (advancer.AdvanceOneUnit(recorder)) { }
        advancer.OnFinished();

        Assert.False(advancer.Succeeded);
        Assert.Equal("交付被拒绝", advancer.FailureReason);
        Assert.NotNull(advancer.Result);
        Assert.False(advancer.Result!.Value.Success);
    }

    [Fact]
    public void Reset_ClearsProcessRuntimeState()
    {
        var system = new JarSystem.Core.JarSystem(
            randomSeed: 17,
            jarIds: new SequentialIdGenerator(),
            orderIds: new SequentialIdGenerator());
        system.SetMasterIndex(0);
        system.SetSlaveIndex(1);
        system.CreateOrder([new Requirement { Shape = "圆罐" }], Constants.OrderType.A);
        var started = system.StartOrder(0);
        Assert.True(started.Started);
        Assert.NotEmpty(system.Processes.ActiveProcesses);
        system.Tick();
        Assert.NotEmpty(system.Journal.Entries);

        system.Reset();

        Assert.Empty(system.Processes.ActiveProcesses);
        Assert.Empty(system.Processes.RecentProcesses);
        Assert.Empty(system.Journal.Entries);
        Assert.Equal(0, system.Processes.Clock.Tick);
        Assert.True(system.Processes.TotalCreated >= 1);

        var next = system.Processes.Begin(new ProcessDefinition(ProcessKinds.OrderExecution, "重置后自检"));
        Assert.True(started.ProcessId.HasValue);
        Assert.True(next.Instance.Id > started.ProcessId.Value);
        next.Abort("test cleanup");
    }

    [Fact]
    public void JournalClear_ResetsSequence()
    {
        var journal = new ProcessJournal();
        journal.Append(1, 1, JournalKinds.Started);
        journal.Append(1, 1, JournalKinds.Succeeded);

        journal.Clear();
        var first = journal.Append(1, 1, JournalKinds.Started);

        Assert.Equal(1, first.Seq);
        Assert.Equal(0, journal.DroppedEntries);
    }

    [Fact]
    public void ResetClock_ReanchorsModifierWindows()
    {
        var stack = new ModifierStack();
        stack.Expire(100);
        using var scope = stack.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Window = (50, 60),
            Reason = "旧时间线的禁令"
        });
        Assert.NotNull(stack.SuppressionReason("POUR_IN", null, null));

        stack.ResetClock();

        Assert.Null(stack.SuppressionReason("POUR_IN", null, null));
    }

    [Fact]
    public void WorldResetDoesNotResurrectTheOldTimelineModifier()
    {
        var system = new JarSystem.Core.JarSystem(randomSeed: 17);
        system.Modifiers.Expire(100);
        using var scope = system.Modifiers.Push(new SuppressModifier
        {
            Operation = "POUR_IN",
            Window = (50, 60),
            Reason = "旧时间线的禁令"
        });

        system.Reset();
        system.Modifiers.Expire(55);

        Assert.Null(system.Modifiers.SuppressionReason("POUR_IN", null, null));
        Assert.Empty(system.Modifiers.Active);
    }

    private sealed class RejectingCompletion(string reason) : IOrderExecutionCompletion
    {
        public (bool Success, string? Reason, Dictionary<string, object?>? Event,
            bool TransferOccurred, List<ExecutionLogEntry>? Operations) Complete(
            OrderExecutionSession session) =>
            (false, reason, null, false, session.Operations);
    }

    private sealed class NoopRecorder : IProcessRecorder
    {
        public void SetPlan(string source, int declaredSteps) { }
        public void StepApplied(string operation, int? jarId = null, string? summary = null) { }
        public void Milestone(string name, string? summary = null) { }
    }
}

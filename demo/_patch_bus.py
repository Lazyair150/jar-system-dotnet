import io, sys

p = r"D:\codex\jar-system-dotnet\src\JarSystem.Core\JarSystem.cs"
s = io.open(p, encoding="utf-8").read()
orig = s

def rep(old, new, n=1):
    global s
    assert old in s, "NOT FOUND: " + old[:80]
    s = s.replace(old, new, n)

# 1) using
rep("using JarSystem.Core.Solver;", "using JarSystem.Core.Solver;\nusing JarSystem.Core.World;")

# 2) 属性：WorldRevision 之后加 Bus
rep(
"""    public int WorldRevision => _logSeq;
""",
"""    public int WorldRevision => _logSeq;

    /// <summary>
    /// **世界信息总线**：世界每次变化在这里发一条信号，关心它的模块从同一个地方收到。
    ///
    /// 它同时是过程管线的**事件源**（`ProcessSystem.EventSource`）—— 于是反应器请求的
    /// "后续世界变化"会被管线在下一个 tick 的第 ① 步统一执行，级联因此可复现、有界、可审计。
    /// 默认没有订阅者也没有反应器，此时总线不改变世界任何一步的走向。
    /// </summary>
    public WorldBus Bus { get; }
""")

# 3) 构造函数里创建总线（必须在 Processes / InitializeDefault 之前）
rep(
"""        Rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();""",
"""        Rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        Bus = new WorldBus();""")

# 4) 接上过程系统：事件源 + 信号出口
rep(
"""        Processes = new ProcessSystem(() => _logSeq, modifiers: Modifiers);""",
"""        Processes = new ProcessSystem(() => _logSeq, modifiers: Modifiers);
        // 总线即事件源：反应器排的"后续世界变化"由管线在第 ① 步统一执行（见 ProcessSystem.DrainEvents）。
        // 无反应器时 DrainForTick 恒返回空表 —— 接入总线不改变世界的任何一步。
        Processes.EventSource = Bus;
        Processes.SignalSink = (inst, reason, tick) => Bus.Publish(new WorldSignal(
            WorldSignalKind.ProcessChanged, reason, inst.Definition.Kind, inst.Id, tick, _logSeq));""")

# 5) Record 里发信号（唯一的写入口 ⇒ 一处发布覆盖全部意图级变更）
rep(
"""            Flag = flag
        });
    }""",
"""            Flag = flag
        });

        // 唯一的写入口 ⇒ 一处发布就覆盖了全部"意图级世界变更"。
        // tick 取逻辑时钟（判定只认 tick，不读墙钟）；Processes 尚未装配时按 0 记。
        Bus.Publish(new WorldSignal(
            WorldSignalKind.WorldChanged, op, name, null, Processes?.Clock.Tick ?? 0, _logSeq));
    }""")

# 6) Reset 时丢弃未执行的队列（但保留订阅者）
rep(
"""        Processes.Clear();
        ClearWorkshopsCore();""",
"""        Processes.Clear();
        Bus.ResetPending();   // 已排队的闭包捕获的是旧世界，重来后不得再执行
        ClearWorkshopsCore();""")

io.open(p, "w", encoding="utf-8").write(s)
print("JarSystem.cs patched")

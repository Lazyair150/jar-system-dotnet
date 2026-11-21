namespace JarSystem.Core.Domain;

/// <summary>唯一 ID 源契约。</summary>
public interface IIdGenerator
{
    int Next();
}

/// <summary>
/// 可检查点化的单调 ID 源。Restore 只允许把计数器推进到不小于当前值的位置，
/// 避免同一进程内已发出的 ID 被重新分配。
/// </summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _value;

    public int Next() => Interlocked.Increment(ref _value);
    public int CurrentValue => Volatile.Read(ref _value);

    public void RestoreAtLeast(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        while (true)
        {
            var current = Volatile.Read(ref _value);
            if (value <= current) return;
            if (Interlocked.CompareExchange(ref _value, value, current) == current)
                return;
        }
    }
}

/// <summary>默认共享 ID 源。</summary>
public static class DefaultIdGenerators
{
    public static readonly IIdGenerator Jars = new SequentialIdGenerator();
    public static readonly IIdGenerator Orders = new SequentialIdGenerator();
    public static readonly IIdGenerator Demands = new SequentialIdGenerator();
}

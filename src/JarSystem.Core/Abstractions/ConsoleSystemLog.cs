using System;

namespace JarSystem.Core.Abstractions;

/// <summary>
/// <see cref="ISystemLog"/> 的默认实现：错误写 stderr、信息写 stdout。
///
/// **为什么默认就是"真实现"而不是空实现**：与 <c>DefaultIdGenerators</c>、<c>StateSpaceSolver</c> 的默认装配风格一致
/// —— 默认装配给出可用行为（这也避免了为"什么都不做"专门引入 Null Object 模式）。
/// 宿主（Web / Godot）要换去向时，在构造 <c>JarSystem</c> 时注入自己的实现即可，例如写文件或采集器。
///
/// **边界**：这里写出的内容**不属于任何对外契约**（不是 JSON、不进 <c>eventHistory</c>、不影响测试断言），
/// 只是把原先在移植过程中丢掉、或本就无处安放的诊断信息补回来。
/// </summary>
public sealed class ConsoleSystemLog : ISystemLog
{
    /// <summary>进程内共享实例（本类无状态、线程安全）。</summary>
    public static readonly ConsoleSystemLog Instance = new();

    /// <inheritdoc />
    public void Error(string message, Exception? ex = null)
        => Console.Error.WriteLine(
            ex == null
                ? "[error] " + message
                : "[error] " + message + " | " + ex.GetType().Name + ": " + ex.Message);

    /// <inheritdoc />
    public void Info(string message) => Console.Out.WriteLine("[info] " + message);
}

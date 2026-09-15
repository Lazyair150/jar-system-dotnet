using System;

namespace JarSystem.Core.Abstractions;

/// <summary>
/// 可观测性出口（阶段 2 定义、阶段 3-A 接线，见审计 §2.1）。
/// 实现方决定去向（控制台 / 文件 / 事件日志 / 采集器）；核心只负责如实上报，不改变控制流。
///
/// **接线范围（重要）**：只用于"原本会被静默丢弃"的诊断信息。凡已经进入对外契约的错误
/// （<c>OrderEvent.Errors</c>、<c>SwitchOperationLog</c>、返回给调用方的 <c>Reason</c>）
/// 一律**保持原样**，这里只是额外留痕，不替代、不改变它们。
///
/// **实现方约定**：
/// <list type="bullet">
/// <item>不得抛出异常 —— 日志失败不能影响领域流程（IO 异常由实现方自行吞掉）；</item>
/// <item>不得反向调用 <c>JarSystem</c>（避免日志触发状态变更或递归）。</item>
/// </list>
/// </summary>
public interface ISystemLog
{
    /// <summary>记录错误。调用方已按原语义处理完毕，此处仅为留痕。</summary>
    void Error(string message, Exception? ex = null);

    /// <summary>记录普通信息（供宿主记录里程碑事件用；核心自身目前不主动调用）。</summary>
    void Info(string message);
}

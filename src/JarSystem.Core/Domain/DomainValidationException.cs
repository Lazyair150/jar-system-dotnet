using System;

namespace JarSystem.Core.Domain;

/// <summary>
/// **领域规则拒绝**（玩法约束不满足），与"编程错误"（下标越界、空引用）刻意区分开。
///
/// 为什么需要它：规则必须住在核心，但"怎么把拒绝呈现给用户"属于宿主。有了这个类型，
/// 宿主只需在一处捕获并转成自己的错误反馈（Web 回 {type:"error"}、Godot 弹提示、命令行打印），
/// 而**所有宿主自动继承同一条规则** —— 这正是把"速率 0~100""容量必须为正"从
/// CommandDispatcher 搬进核心要解决的问题。
///
/// 它继承 <see cref="InvalidOperationException"/>：既有 catch (Exception) /
/// catch (InvalidOperationException) 调用点行为不变（例如 OrderService.CreateOrder
/// 把订单校验失败转成 (false, message) 的既有风格）。
/// </summary>
public sealed class DomainValidationException : InvalidOperationException
{
    /// <param name="message">给用户看的原因（会原样出现在 Web 的 error 消息里）。</param>
    public DomainValidationException(string message) : base(message) { }
}

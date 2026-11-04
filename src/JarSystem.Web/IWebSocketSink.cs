using System.Net.WebSockets;

/// <summary>
/// 向客户端推送消息的出口（依赖倒置：命令分派器与定时器只依赖本接口，
/// 便于替换实现与单元测试）。
/// </summary>
/// <summary>
/// 一条连接的两件事实：是不是本机、有没有出示管理令牌（原样保留，比对交给调用方）。
/// 分派器只拿这两件事问 <see cref="JarSystem.Core.Security.AccessPolicy"/>，自己不做推断。
/// </summary>
/// <param name="IsLoopback">连接来自回环地址。</param>
/// <param name="AdminToken">请求头 <c>X-JarSystem-Admin</c> 的原样值（没带就是 null）。</param>
public readonly record struct ConnectionAccess(bool IsLoopback, string? AdminToken)
{
    /// <summary>没有传输层信息时的保守默认：**不是**本机、没有令牌（测试替身用）。</summary>
    public static ConnectionAccess Unknown => new(false, null);

    /// <summary>本机、没带令牌。</summary>
    public static ConnectionAccess Local => new(true, null);
}

public interface IWebSocketSink
{
    /// <summary>向单个连接发送（连接已关闭时静默失败，与重构前一致）。</summary>
    Task SendAsync(WebSocket socket, string json);

    /// <summary>广播给所有存活连接，并清理已死连接。</summary>
    Task BroadcastAsync(string json);

    /// <summary>
    /// 这条连接的两件事实（回环 / 管理令牌）。默认实现返回 <see cref="ConnectionAccess.Unknown"/>：
    /// 非真实传输的替身（单测里的 RecordingSink）不会因此被放行任何管理操作。
    /// </summary>
    ConnectionAccess AccessOf(WebSocket socket) => ConnectionAccess.Unknown;
}

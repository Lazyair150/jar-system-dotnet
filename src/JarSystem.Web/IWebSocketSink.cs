using System.Net.WebSockets;

/// <summary>
/// 向客户端推送消息的出口（依赖倒置：命令分派器与定时器只依赖本接口，
/// 便于替换实现与单元测试）。
/// </summary>
public interface IWebSocketSink
{
    /// <summary>向单个连接发送（连接已关闭时静默失败，与重构前一致）。</summary>
    Task SendAsync(WebSocket socket, string json);

    /// <summary>广播给所有存活连接，并清理已死连接。</summary>
    Task BroadcastAsync(string json);
}

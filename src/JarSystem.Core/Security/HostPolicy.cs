using System;
using System.Collections.Generic;
using System.Net;

namespace JarSystem.Core.Security;

/// <summary>
/// 宿主姿态检查（G-06：以非 root / 低权限身份运行、只在本机可及）。
///
/// 这里只做**纯判定**：给它地址、给它"是否以提权身份运行"这个事实，它回答"能不能继续"和"要不要告警"。
/// 读环境变量、拿真实套接字地址、检测 Windows 令牌这些副作用都留在宿主（<c>Program.cs</c>），
/// 于是每条规则都能被单测直接钉住。
/// </summary>
public static class HostPolicy
{
    /// <summary>允许远程连接的开关环境变量名（默认关闭）。</summary>
    public const string AllowRemoteVariable = "JAR_ALLOW_REMOTE";

    /// <summary>管理令牌环境变量名。</summary>
    public const string AdminTokenVariable = "JAR_ADMIN_TOKEN";

    /// <summary>这个地址是不是回环（<c>127.0.0.0/8</c>、<c>::1</c>）。null（拿不到地址）按"不可信"处理。</summary>
    public static bool IsLoopback(IPAddress? address)
    {
        if (address == null) return false;
        if (IPAddress.IsLoopback(address)) return true;

        // IPv4-mapped IPv6（::ffff:127.0.0.1）也要认成回环，否则同一台机器上的某些栈会被误判成远程。
        return address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }

    /// <summary>
    /// 连接是否允许继续处理：回环直接放行；非回环只有显式打开"允许远程"才放行。
    /// </summary>
    /// <param name="remote">对端地址（<see cref="System.Net.Http.HttpContext.Connection"/> 的远端）。</param>
    /// <param name="allowRemote">宿主是否显式允许远程（<c>JAR_ALLOW_REMOTE=1</c>）。</param>
    public static bool IsAllowedConnection(IPAddress? remote, bool allowRemote)
        // 顺序有意义：**地址不明（null）一律拒**，哪怕宿主显式放开了远程 —— 不知道对端是谁时，
        // `allowRemote` 表达的是"允许别的机器连"，不是"允许来源不明的连接"。
        => remote != null && (allowRemote || IsLoopback(remote));

    /// <summary>
    /// 以提权身份运行时的告警文案；普通用户运行返回 null。
    /// 只告警不拒绝 —— 开发机上的普通 <c>dotnet run</c> 经常就在管理员终端里，
    /// 拒绝启动会让"以低权限运行"这条规则变成人人绕过的一道墙；告警则让事实永远可见。
    /// </summary>
    public static string? ElevationWarning(bool isElevated)
        => isElevated
            ? "当前进程以提权身份运行（管理员/SYSTEM）：本服务只应服务本机，建议用普通用户运行；"
              + "以高权限运行时，任何配置文件与场景数据的写入都会继承更高的文件权限。"
            : null;
}

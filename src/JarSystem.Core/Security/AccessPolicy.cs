using System;

namespace JarSystem.Core.Security;

/// <summary>
/// 这次连接/请求的**事实**（调用方量出来给它，不做任何推断）。
/// </summary>
/// <param name="IsLoopback">连接是不是来自本机回环地址。</param>
/// <param name="AdminTokenProvided">请求有没有出示管理令牌（头 <c>X-JarSystem-Admin</c>）。</param>
public readonly record struct AccessFacts(bool IsLoopback, bool AdminTokenProvided);

/// <summary>
/// 鉴权结论：允许或拒绝，**拒绝一定带稳定码与一句话理由**（前端与日志都按码判断，不解析中文）。
/// </summary>
public readonly record struct AccessDecision(bool Allowed, string Code, string Reason)
{
    public static AccessDecision Allow(string reason) => new(true, "ok", reason);

    public static AccessDecision Deny(string code, string reason) => new(false, code, reason);
}

/// <summary>
/// 访问策略：**只回答"这次操作允不允许"**，不解析请求、不读环境变量、不碰世界。
///
/// 两类策略源刻意分开（C-06：管理接口与业务接口分离鉴权策略）：
/// <list type="bullet">
/// <item>业务类（<see cref="AccessCategory.BusinessRead"/> / <see cref="AccessCategory.BusinessWrite"/>）
/// 只问一件事：**是不是本机**。这台服务是单用户本机玩具，业务操作的安全边界就是操作系统用户边界。</item>
/// <item>管理类（<see cref="AccessCategory.Admin"/>）问两件事：是不是本机，**以及**（配了管理令牌时）
/// 有没有出示令牌。管理操作会作废整局事实（reset / restartLevel / loadLevel / 写作者数据），
/// 所以它比业务操作多一道独立条件 —— 这一道不是给远程用户的（远程根本进不来），而是给
/// "同一个用户身份下的其它进程/脚本"设的门槛：要动作者的关卡数据，必须显式带上令牌。</item>
/// </list>
/// 令牌未配置（默认）时管理操作只剩"本机"这一道，等于开发期零摩擦 —— 这是有意的：
/// 门槛可配、行为可预测，比默认强制一个没人设置的令牌导致编辑器不可用要好。
/// </summary>
public static class AccessPolicy
{
    /// <summary>拒绝码：不是本机连接。</summary>
    public const string CodeRemoteDenied = "remote-denied";

    /// <summary>拒绝码：管理操作缺少管理令牌。</summary>
    public const string CodeAdminTokenRequired = "admin-token-required";

    /// <summary>
    /// 判定一个权限点在这组事实下是否允许。
    /// </summary>
    /// <param name="point">已登记的权限点（调用方必须先查到它，未登记不得调用）。</param>
    /// <param name="facts">这次连接/请求的事实。</param>
    /// <param name="adminTokenConfigured">宿主是否配了管理令牌（配了才要求出示）。</param>
    public static AccessDecision Decide(PermissionPoint point, AccessFacts facts, bool adminTokenConfigured)
    {
        if (!facts.IsLoopback)
        {
            return AccessDecision.Deny(
                CodeRemoteDenied,
                "这台服务只服务本机：来自非回环地址的 " + point.Name + " 一律拒绝");
        }

        if (point.Category == AccessCategory.Admin && adminTokenConfigured && !facts.AdminTokenProvided)
        {
            return AccessDecision.Deny(
                CodeAdminTokenRequired,
                "管理操作（" + point.Name + "）需要管理令牌：请带上请求头 X-JarSystem-Admin");
        }

        return AccessDecision.Allow(point.Category == AccessCategory.Admin
            ? "本机 + 管理令牌（或未配置令牌）放行管理操作 " + point.Name
            : "本机放行业务操作 " + point.Name);
    }
}

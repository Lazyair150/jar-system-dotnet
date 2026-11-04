using System.Collections.Generic;

namespace JarSystem.Core.Security;

/// <summary>
/// 一次操作的鉴权类别。**类别决定策略**：业务类只问"是不是本机"，
/// 管理类额外问"这次请求有没有出示管理令牌"（见 <see cref="AccessPolicy"/>）。
/// </summary>
public enum AccessCategory
{
    /// <summary>读：不改世界。</summary>
    BusinessRead = 0,

    /// <summary>写：改世界，但仍属玩法内的普通操作。</summary>
    BusinessWrite = 1,

    /// <summary>管理：重置世界、换关卡、改写作者数据 —— 一旦被误用，整局事实都会变。</summary>
    Admin = 2
}

/// <summary>
/// 一个**权限点**：某个资源（resource）上的某个动作（action），属于某个类别，并写明为什么需要它。
///
/// 为什么要有这张表（C-05）：新增一种资源或一个动作时，最容易发生的事是"顺手多开一条路径"，
/// 而默认拒绝只写在 <c>switch</c> 的 <c>default</c> 分支里 —— 那句话保护不了新增的 case。
/// 于是把"对外暴露的每一个动作"变成**必须先登记**的显式数据：分派器与 HTTP 端点都从这张表取类别，
/// 未登记的名字一律当成未知操作拒绝。登记表本身有测试盯着（数量、命名、类别分布、资源与动作非空）。
/// </summary>
/// <param name="Name">命令名或路由名（与对外协议里出现的字符串逐字一致）。</param>
/// <param name="Category">鉴权类别。</param>
/// <param name="Resource">被操作的资源种类：世界、订单、过程、关卡、作者数据…</param>
/// <param name="Action">在这类资源上做的动作：读、创建、修改、删除、推进、切换。</param>
/// <param name="Why">为什么要把它暴露出来（一句话，供审阅者判断它该不该存在）。</param>
public sealed record PermissionPoint(
    string Name,
    AccessCategory Category,
    string Resource,
    string Action,
    string Why);

/// <summary>对外暴露的权限点登记表：27 条 WebSocket 命令 + 6 个 HTTP 端点。</summary>
public static class PermissionPoints
{
    /// <summary>WebSocket 命令（与 <c>CommandDispatcher</c> 的 case 一一对应）。</summary>
    public static IReadOnlyList<PermissionPoint> Commands { get; } = new[]
    {
        new PermissionPoint("getState", AccessCategory.BusinessRead, "世界", "读", "页面要渲染世界快照"),
        new PermissionPoint("getSteps", AccessCategory.BusinessRead, "过程", "读", "页面要看某个主体走过的步骤"),
        new PermissionPoint("checkFeasibility", AccessCategory.BusinessRead, "订单", "分析", "只读求解，不改世界"),
        new PermissionPoint("checkAllFeasibility", AccessCategory.BusinessRead, "订单", "分析", "批量只读求解，供进度条使用"),

        new PermissionPoint("createOrder", AccessCategory.BusinessWrite, "订单", "创建", "玩家手写需求派单"),
        new PermissionPoint("createRandomOrder", AccessCategory.BusinessWrite, "订单", "创建", "随机派单，省去手写"),
        new PermissionPoint("createWorkshop", AccessCategory.BusinessWrite, "工作台", "创建", "布置阶段加一台"),
        new PermissionPoint("createRandomWorkshop", AccessCategory.BusinessWrite, "工作台", "创建", "随机加一台"),
        new PermissionPoint("updateWorkshopCapacity", AccessCategory.BusinessWrite, "工作台", "修改", "调整罐位与液位上限"),
        new PermissionPoint("setWorkshopCapability", AccessCategory.BusinessWrite, "工作台", "修改", "改基础/附加标记与禁令"),
        new PermissionPoint("moveOrder", AccessCategory.BusinessWrite, "订单", "排序", "调整订单执行顺序"),
        new PermissionPoint("setMaster", AccessCategory.BusinessWrite, "工作台", "切换", "指定主台"),
        new PermissionPoint("setSlave", AccessCategory.BusinessWrite, "工作台", "切换", "指定从台"),
        new PermissionPoint("executeOrder", AccessCategory.BusinessWrite, "订单", "执行", "旧的一步到位入口"),
        new PermissionPoint("deleteOrder", AccessCategory.BusinessWrite, "订单", "删除", "删掉不要的订单"),
        new PermissionPoint("startOrder", AccessCategory.BusinessWrite, "订单", "执行", "开始一单（起过程）"),
        new PermissionPoint("cancelProcess", AccessCategory.BusinessWrite, "过程", "取消", "主动放弃一单"),
        new PermissionPoint("setTickInterval", AccessCategory.BusinessWrite, "时间", "修改", "调整自动走时节奏"),
        new PermissionPoint("manualTick", AccessCategory.BusinessWrite, "时间", "推进", "手动推进一回合"),
        new PermissionPoint("updateLiquidRate", AccessCategory.BusinessWrite, "生产", "修改", "改某种液体的自产速率"),
        new PermissionPoint("updateProducerRate", AccessCategory.BusinessWrite, "生产", "修改", "改某个形状的产出速率"),
        new PermissionPoint("startTick", AccessCategory.BusinessWrite, "时间", "启动", "开始自动走时"),
        new PermissionPoint("stopTick", AccessCategory.BusinessWrite, "时间", "停止", "停止自动走时"),
        new PermissionPoint("setAutoCheck", AccessCategory.BusinessWrite, "订单", "修改", "开关后台可行性自检"),

        new PermissionPoint("reset", AccessCategory.Admin, "世界", "重置", "整局重来：所有事实作废"),
        new PermissionPoint("restartLevel", AccessCategory.Admin, "世界", "重开", "按当前关卡重新装配世界"),
        new PermissionPoint("loadLevel", AccessCategory.Admin, "世界", "切换", "换一个关卡世界，等于换一局")
    };

    /// <summary>HTTP 端点（与 <c>Program.cs</c> 的 <c>Map</c> 一一对应）。</summary>
    public static IReadOnlyList<PermissionPoint> Endpoints { get; } = new[]
    {
        new PermissionPoint("GET /api/state", AccessCategory.BusinessRead, "世界", "读", "调试与冒烟用只读快照"),
        new PermissionPoint("GET /api/levels", AccessCategory.BusinessRead, "关卡", "读", "页面要列出可玩的关卡"),
        new PermissionPoint("GET /api/invariants", AccessCategory.BusinessRead, "世界", "读", "自检面板要不变量结论"),
        new PermissionPoint("GET /api/provenance", AccessCategory.BusinessRead, "资源账本", "读", "页面按因果展示每份资源的来去"),
        new PermissionPoint("GET /api/scenario", AccessCategory.BusinessRead, "作者数据", "读", "编辑器读当前场景"),
        new PermissionPoint("POST /api/scenario/validate", AccessCategory.BusinessRead, "作者数据", "校验", "只解析不落盘"),
        new PermissionPoint("POST /api/scenario", AccessCategory.Admin, "作者数据", "写", "把场景写进 config/scenario.json")
    };

    /// <summary>按命令名查找权限点；未登记返回 false（调用方必须默认拒绝）。</summary>
    public static bool TryFindCommand(string name, out PermissionPoint point) => TryFind(Commands, name, out point);

    /// <summary>按 "METHOD 路由" 查找权限点。</summary>
    public static bool TryFindEndpoint(string endpoint, out PermissionPoint point) => TryFind(Endpoints, endpoint, out point);

    private static bool TryFind(IReadOnlyList<PermissionPoint> points, string name, out PermissionPoint point)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (string.Equals(points[i].Name, name, System.StringComparison.Ordinal))
            {
                point = points[i];
                return true;
            }
        }

        point = null!;
        return false;
    }
}

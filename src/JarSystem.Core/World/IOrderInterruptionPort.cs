namespace JarSystem.Core.World;

/// <summary>
/// 工作台替换对订单系统的窄接口。
/// 成功返回时，活动订单必须已经停止、进入其 CD，且从工作台临时资源已按订单规则结算。
/// 没有活动订单时应返回成功且不产生副作用。
/// 该接口不规定普通订单完成/失败后的 CD 行为。
/// </summary>
public interface IOrderInterruptionPort
{
    (bool Success, string? Error) InterruptForWorkshopReplacement();
}

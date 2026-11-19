namespace JarSystem.Core.Domain;

/// <summary>
/// 订单委托持有的领域规格契约。
/// 该契约属于领域模型的最小词汇，不依赖技术抽象层；具体玩法在 Domain 内提供实现。
/// 通用订单区域只保存并传递规格，不解释其业务内容。
/// </summary>
public interface IOrderSpecification
{
}

namespace JarSystem.Core.Domain;

/// <summary>
/// 订单委托持有的领域规格契约。
/// 该契约属于领域模型的最小词汇，不依赖技术抽象层；具体玩法在 Domain 内提供实现。
/// 通用订单区域只保存并传递规格，不解释其业务内容。
/// 实现必须在绑定到 OrderDemand 后保持不可变；若规格内容需要变化，应创建新的规格/订单，
/// 不能原地修改，否则基于旧规格生成的求解结果无法仅靠世界版本识别失效。
/// </summary>
public interface IOrderSpecification
{
}

namespace JarSystem.Core.Abstractions;

/// <summary>
/// **强度折算出口**（P4c）：把"某目标的强度按千分比折算"这件事收成接口，
/// 让产出层（`Production`）与产出服务（`Services`）**不必认识 Rules 里的修饰器栈**。
///
/// 实现方：`Rules.ModifierStack` —— 语义按它的注释：**无匹配修饰器时恒等返回**、
/// 全程整数运算（不引入浮点，保住确定性）。
/// 目标名约定：`liquid:酒`（每 tick 液体产出）、`producer:圆罐`（某形状的生产者速率）。
/// </summary>
public interface IIntensitySource
{
    /// <summary>把 <paramref name="value"/> 按当前生效的强度修饰器折算（无匹配 ⇒ 原样返回）。</summary>
    int Apply(string target, int value);
}

namespace JarSystem.Core.Domain;

/// <summary>
/// 改形走哪条路：空罐（付池里 1 份液体）还是满罐（自耗罐内液体）。
///
/// 绝大多数调用方**不该关心**这件事 —— 罐子此刻是空是满，工作台自己看得见，
/// 所以默认值是 <see cref="Auto"/>。只有那些"本来就规定必须是空罐改形"的路径
/// （玩家命令 `ReshapeEmptyCommand`、换台重组）才显式传 <see cref="Empty"/>，
/// 这样它拿到的是"必须为空罐"的拒绝，而不是"我顺手帮你按满罐改了"。
/// </summary>
public enum ReshapeMode
{
    /// <summary>按罐子此刻状态自选。</summary>
    Auto = 0,

    /// <summary>必须是空罐改形（付池里的成本液体）。</summary>
    Empty = 1,

    /// <summary>必须是满罐改形（自耗罐内液体，读法 B：也算"使用"）。</summary>
    Full = 2
}

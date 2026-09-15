using JarSystem.Core.Domain;

namespace JarSystem.Core.Orders;

/// <summary>
/// 罐子对**新主台**的价值排序（越小越优先保留，容量不足时优先丢弃大值）。
/// 原为 <c>WorkshopSwitchEvent.JarValue</c> 内联规则，现独立成策略对象以便单独演进与测试。
///
/// 五级（不可改动）：
/// 0 同液满罐（罐内液体新主台可用，液体+罐子双重价值）
/// 1 同形空罐（形状属于新主台基础标记，可作倒液/改形目标）
/// 2 基础+附加之外的异形空罐（新主台无法通过任何方式获得）
/// 3 附加标记空罐（可通过改形基础标记罐子获得，价值低于「无法获得」的空罐）
/// 4 异液满罐（罐内液体被新主台禁用，含同形与异形，价值最低）
/// </summary>
internal sealed class JarValuePolicy
{
    private readonly Workshop _newMaster;

    public JarValuePolicy(Workshop newMaster)
    {
        _newMaster = newMaster;
    }

    public int ValueOf(Jar jar)
    {
        if (jar.Liquid != null)
        {
            if (!_newMaster.IsBanned(jar.Liquid)) return 0; // 同液满罐
            return 4;                                      // 异液满罐（液体被禁，含同形/异形）
        }
        if (_newMaster.BaseMarks.Contains(jar.Shape)) return 1;        // 同形空罐（基础标记）
        if (!_newMaster.IsAdditionalMarked(jar.Shape)) return 2;       // 基础+附加之外的异形空罐（新台无法获得）
        return 3;                                                      // 附加标记空罐（可通过改形基础罐获得）
    }
}

namespace JarSystem.Core.Domain;

/// <summary>
/// 改形请求的回应（方向／标记／成本／禁令各有一个稳定码）。
///
/// <para>稳定码：<c>marked</c>（已被操作）／<c>direction</c>（形状方向不通）／
/// <c>no-cost-liquid</c>（付不出改形成本）／<c>banned</c>（禁液，读法 B：满罐自耗也算使用）／
/// <c>suppressed</c>（被生效中的法令抑制）／<c>not-full</c>（声明满罐但罐子是空的）。</para>
///
/// <para>码是给程序看的，<see cref="Reason"/> 是给人看的，且**与执行者返回的文案逐字一致**
/// —— 同一个"不行"不该有五种说法。</para>
/// </summary>
public readonly struct ReshapeGrant
{
    private ReshapeGrant(bool granted, string? code, string? reason)
    {
        Granted = granted;
        Code = code;
        Reason = reason;
    }

    /// <summary>能不能改。</summary>
    public bool Granted { get; }

    /// <summary>稳定码（程序判定用）。</summary>
    public string? Code { get; }

    /// <summary>人读理由（与执行者的错误文案一致）。</summary>
    public string? Reason { get; }

    internal static ReshapeGrant Yes() => new(true, null, null);
    internal static ReshapeGrant No(string code, string? reason) => new(false, code, reason);

    public override string ToString() => Granted ? "ok" : "[" + Code + "] " + Reason;
}

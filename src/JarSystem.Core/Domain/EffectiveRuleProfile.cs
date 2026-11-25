using System.Security.Cryptography;
using System.Text;

namespace JarSystem.Core.Domain;

/// <summary>
/// Serializable rule contribution emitted by one installed component instance.
/// It is a declaration, not executable code: the Core owns interpretation.
/// </summary>
public sealed record RuleContribution(
    string RuleId,
    int RuleVersion,
    RuleContributionEffect Effect,
    string Target,
    string Scope,
    int Priority,
    RuleContributionSource Source,
    IReadOnlyList<RuleContributionParameter> Parameters)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RuleId) || RuleVersion <= 0)
            throw new ArgumentException("Rule contribution requires an exact rule identity/version.");
        if (string.IsNullOrWhiteSpace(Target) || string.IsNullOrWhiteSpace(Scope))
            throw new ArgumentException("Rule contribution requires an explicit target and scope.");
        if (Source is null)
            throw new ArgumentException("Rule contribution source is required.");
        Source.Validate();
        if (Parameters is null)
            throw new ArgumentException("Rule contribution parameters are required.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in Parameters)
        {
            parameter.Validate();
            if (!names.Add(parameter.Name))
                throw new ArgumentException($"Duplicate rule contribution parameter: {parameter.Name}");
        }
        RuleContributionSchemaRegistry.Validate(this);
    }
}

public enum RuleContributionEffect
{
    GrantBaseMark,
    GrantAdditionalMark,
    BanLiquid,
    SuppressOperation,
    RequireCondition,
    ModifyCapacity,
    RetimeProcess,
    ModifyIntensity
}

/// <summary>Exact provenance of the installed instance that emitted a contribution.</summary>
public sealed record RuleContributionSource(
    string SourceKind,
    string DefinitionId,
    int DefinitionVersion,
    string SourceInstanceId,
    int SourceRevision,
    string SlotId)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceKind) ||
            string.IsNullOrWhiteSpace(DefinitionId) || DefinitionVersion <= 0 ||
            string.IsNullOrWhiteSpace(SourceInstanceId) || SourceRevision <= 0 ||
            string.IsNullOrWhiteSpace(SlotId))
            throw new ArgumentException("Rule contribution source must retain exact Definition, Owned Instance, progression and slot identity.");
    }
}

/// <summary>
/// String-valued canonical parameter. Component catalogs serialize values before Core
/// interpretation; this keeps the contribution profile deterministic and transport-safe.
/// </summary>
public sealed record RuleContributionParameter(string Name, string Value)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Value is null)
            throw new ArgumentException("Rule contribution parameter requires a name and canonical value.");
    }
}

/// <summary>
/// Immutable, canonicalized output of resolving all installed component contributions.
/// Resource balances are intentionally excluded: they belong to Readiness/ResourceBinding.
/// </summary>
public sealed class EffectiveRuleProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; }
    public string BoardId { get; }
    public int BoardDefinitionVersion { get; }
    public long AssignmentRevision { get; }
    /// <summary>Exact Assignment fingerprint supplied by the Space Board ingress; null for legacy profiles.</summary>
    public string? AssignmentFingerprint { get; }
    /// <summary>Exact source Profile fingerprint supplied by Space; null for legacy profiles.</summary>
    public string? SourceProfileFingerprint { get; }
    public IReadOnlyList<RuleContribution> Contributions { get; }
    public string Fingerprint { get; }

    private EffectiveRuleProfile(
        string boardId,
        int boardDefinitionVersion,
        long assignmentRevision,
        string? assignmentFingerprint,
        string? sourceProfileFingerprint,
        IReadOnlyList<RuleContribution> contributions,
        string fingerprint)
    {
        SchemaVersion = CurrentSchemaVersion;
        BoardId = boardId;
        BoardDefinitionVersion = boardDefinitionVersion;
        AssignmentRevision = assignmentRevision;
        AssignmentFingerprint = assignmentFingerprint;
        SourceProfileFingerprint = sourceProfileFingerprint;
        Contributions = contributions;
        Fingerprint = fingerprint;
    }

    public static EffectiveRuleProfile Create(
        string boardId,
        int boardDefinitionVersion,
        long assignmentRevision,
        IEnumerable<RuleContribution> contributions,
        string? assignmentFingerprint = null,
        string? sourceProfileFingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(boardId) || boardDefinitionVersion <= 0 || assignmentRevision <= 0)
            throw new ArgumentException("Effective rule profile requires exact Board identity and positive assignment revision.");
        if ((assignmentFingerprint is null) != (sourceProfileFingerprint is null) ||
            (assignmentFingerprint != null && (!IsSha256(assignmentFingerprint) || !IsSha256(sourceProfileFingerprint!))))
            throw new ArgumentException("Ingress Effective Rule Profile must retain both exact SHA-256 source fingerprints.");
        ArgumentNullException.ThrowIfNull(contributions);

        var canonical = contributions.ToArray();
        foreach (var contribution in canonical)
        {
            ArgumentNullException.ThrowIfNull(contribution);
            contribution.Validate();
        }

        canonical = canonical
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Source.SlotId, StringComparer.Ordinal)
            .ThenBy(x => x.RuleId, StringComparer.Ordinal)
            .ThenBy(x => x.Source.SourceInstanceId, StringComparer.Ordinal)
            .ToArray();

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in canonical)
        {
            var identity = string.Join("\u001f",
                contribution.Source.SlotId,
                contribution.Source.SourceInstanceId,
                contribution.RuleId,
                contribution.RuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contribution.Target);
            if (!identities.Add(identity))
                throw new ArgumentException($"Duplicate contribution identity: {contribution.RuleId} from {contribution.Source.SourceInstanceId}.");
        }

        var readOnly = Array.AsReadOnly(canonical);
        var fingerprint = ComputeFingerprint(
            boardId, boardDefinitionVersion, assignmentRevision,
            assignmentFingerprint, sourceProfileFingerprint, canonical);
        return new EffectiveRuleProfile(
            boardId, boardDefinitionVersion, assignmentRevision,
            assignmentFingerprint, sourceProfileFingerprint, readOnly, fingerprint);
    }

    public IReadOnlyList<RuleContribution> Find(RuleContributionEffect effect) =>
        Array.AsReadOnly(Contributions.Where(x => x.Effect == effect).ToArray());

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string ComputeFingerprint(
        string boardId,
        int boardDefinitionVersion,
        long assignmentRevision,
        string? assignmentFingerprint,
        string? sourceProfileFingerprint,
        IReadOnlyList<RuleContribution> contributions)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("JarSystem.EffectiveRuleProfile.v" + CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(RuleContributionSchemaRegistry.CurrentVersion);
            writer.Write(boardId);
            writer.Write(boardDefinitionVersion);
            writer.Write(assignmentRevision);
            if (assignmentFingerprint != null && sourceProfileFingerprint != null)
            {
                writer.Write("SpaceBoardIngress.v1");
                writer.Write(assignmentFingerprint);
                writer.Write(sourceProfileFingerprint);
            }
            writer.Write(contributions.Count);
            foreach (var item in contributions)
            {
                writer.Write(item.RuleId);
                writer.Write(item.RuleVersion);
                writer.Write((int)item.Effect);
                writer.Write(item.Target);
                writer.Write(item.Scope);
                writer.Write(item.Priority);
                writer.Write(item.Source.SourceKind);
                writer.Write(item.Source.DefinitionId);
                writer.Write(item.Source.DefinitionVersion);
                writer.Write(item.Source.SourceInstanceId);
                writer.Write(item.Source.SourceRevision);
                writer.Write(item.Source.SlotId);
                var parameters = item.Parameters.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                writer.Write(parameters.Length);
                foreach (var parameter in parameters)
                {
                    writer.Write(parameter.Name);
                    writer.Write(parameter.Value);
                }
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

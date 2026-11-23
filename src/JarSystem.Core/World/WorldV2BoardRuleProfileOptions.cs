using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using JarSystem.Core.Domain;

namespace JarSystem.Core.World;

/// <summary>
/// Versioned ingress contract for the Space-owned frozen Board rule profile.
/// The assignment snapshot is carried beside the profile so Core can verify every
/// contribution against the exact installed instance and Board Slot before execution.
/// </summary>
public sealed class WorldV2BoardRuleProfileOptions
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }
    [JsonPropertyName("boardId")]
    public string BoardId { get; set; } = "";
    [JsonPropertyName("boardDefinitionVersion")]
    public int BoardDefinitionVersion { get; set; }
    [JsonPropertyName("assignmentFingerprint")]
    public string AssignmentFingerprint { get; set; } = "";
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";
    [JsonPropertyName("contributions")]
    public List<WorldV2RuleContributionOptions> Contributions { get; set; } = new();
    public IReadOnlyList<string> Validate(WorldV2BoardAssignmentSnapshotOptions? assignmentSnapshot)
    {
        var errors = new List<string>();
        if (SchemaVersion != 1 || string.IsNullOrWhiteSpace(BoardId) || BoardDefinitionVersion <= 0)
            errors.Add("boardRuleProfile schema or Board identity is invalid");
        if (!IsSha256(AssignmentFingerprint) || !IsSha256(Fingerprint))
            errors.Add("boardRuleProfile requires SHA-256 assignment and profile fingerprints");
        if (Contributions == null || assignmentSnapshot == null)
        {
            errors.Add("boardRuleProfile contributions and exact boardAssignmentSnapshot are required");
            return errors;
        }

        errors.AddRange(assignmentSnapshot.Validate(BoardId, BoardDefinitionVersion, AssignmentFingerprint));
        var assignmentsBySlot = new Dictionary<string, WorldV2BoardAssignmentEntryOptions>(StringComparer.Ordinal);
        if (assignmentSnapshot.Assignments != null)
            foreach (var assignment in assignmentSnapshot.Assignments.Where(x => x != null))
                assignmentsBySlot.TryAdd(assignment.SlotId, assignment);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in Contributions)
        {
            if (contribution == null)
            {
                errors.Add("boardRuleProfile contains a null contribution");
                continue;
            }
            errors.AddRange(contribution.Validate());
            if (contribution.Source == null) continue;
            if (!assignmentsBySlot.TryGetValue(contribution.Source.SlotId, out var assignment))
            {
                errors.Add("rule contribution references an unassigned Board Slot: " + contribution.Source.SlotId);
                continue;
            }
            if (assignment.ComponentKind != "workshop")
                errors.Add("workshop-scoped contribution must originate from a workshop Board Slot: " + contribution.Source.SlotId);
            if (assignment.OwnedInstanceId != contribution.Source.SourceInstanceId ||
                assignment.DefinitionId != contribution.Source.DefinitionId ||
                assignment.DefinitionVersion != contribution.Source.DefinitionVersion ||
                assignment.SourceRevision != contribution.Source.SourceRevision)
            {
                errors.Add("rule contribution provenance differs from its exact Board Assignment: " + contribution.Source.SlotId);
            }
            var identity = string.Join("\u001f", contribution.Source.SlotId, contribution.Source.SourceInstanceId,
                contribution.RuleId, contribution.RuleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contribution.Target);
            if (!identities.Add(identity))
                errors.Add("duplicate Board rule contribution identity: " + contribution.RuleId);
        }

        if (errors.Count == 0 && !string.Equals(ComputeCanonicalFingerprint(), Fingerprint, StringComparison.OrdinalIgnoreCase))
            errors.Add("boardRuleProfile fingerprint does not match its canonical content");
        return errors;
    }

    public EffectiveRuleProfile BuildEffectiveRuleProfile(WorldV2BoardAssignmentSnapshotOptions? assignmentSnapshot)
    {
        var errors = Validate(assignmentSnapshot);
        if (errors.Count != 0)
            throw new ArgumentException("Invalid boardRuleProfile: " + string.Join("; ", errors));
        var contributions = Contributions.Select(x => x.ToDomain()).ToArray();
        return EffectiveRuleProfile.Create(BoardId, BoardDefinitionVersion, 1, contributions);
    }

    public string ComputeCanonicalFingerprint()
    {
        var payload = new ProfileFingerprintPayload
        {
            SchemaVersion = SchemaVersion,
            BoardId = BoardId,
            BoardDefinitionVersion = BoardDefinitionVersion,
            AssignmentFingerprint = AssignmentFingerprint,
            Contributions = Contributions
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, JsonOptions));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            WriteCanonical(document.RootElement, writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static bool IsSha256(string value) =>
        value != null && value.Length == 64 && value.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }

    private sealed class ProfileFingerprintPayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("boardId")] public string BoardId { get; set; } = "";
        [JsonPropertyName("boardDefinitionVersion")] public int BoardDefinitionVersion { get; set; }
        [JsonPropertyName("assignmentFingerprint")] public string AssignmentFingerprint { get; set; } = "";
        [JsonPropertyName("contributions")] public List<WorldV2RuleContributionOptions> Contributions { get; set; } = new();
    }
}

public sealed class WorldV2RuleContributionOptions
{
    [JsonPropertyName("ruleId")] public string RuleId { get; set; } = "";
    [JsonPropertyName("ruleVersion")] public int RuleVersion { get; set; }
    [JsonPropertyName("effect")] public string Effect { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("source")] public WorldV2RuleContributionSourceOptions? Source { get; set; }
    [JsonPropertyName("parameters")] public Dictionary<string, string> Parameters { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(RuleId) || RuleVersion != 1 ||
            string.IsNullOrWhiteSpace(Target) || string.IsNullOrWhiteSpace(Scope))
            errors.Add("rule contribution identity, supported version 1, target or scope is invalid");
        // This ingress revision has a real consumer only for workshop-scoped rules.
        // Other component scopes remain authored data, but cannot enter a runtime that would ignore them.
        if (!string.Equals(Scope, "workshop", StringComparison.Ordinal))
            errors.Add("rule contribution scope has no World V2 Core runtime consumer yet: " + Scope);
        var parameters = Parameters ?? new Dictionary<string, string>();
        if (!Enum.TryParse<RuleContributionEffect>(Effect, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed))
            errors.Add("rule contribution effect is unsupported: " + Effect);
        else if (parsed == RuleContributionEffect.RetimeProcess &&
            (!parameters.TryGetValue("ticksPerStep", out var ticksText) ||
             !int.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0))
            errors.Add("RetimeProcess requires a positive invariant ticksPerStep integer");
        else if (parsed == RuleContributionEffect.ModifyIntensity &&
            (!parameters.TryGetValue("factorPerMille", out var factorText) ||
             !int.TryParse(factorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var factor) || factor < 0))
            errors.Add("ModifyIntensity requires a non-negative invariant factorPerMille integer");
        if (Source == null) errors.Add("rule contribution source is required");
        else
        {
            errors.AddRange(Source.Validate());
            if (Source.SourceKind != "installed-component")
                errors.Add("Board rule contribution must originate from an installed component instance");
        }
        if (Parameters == null || Parameters.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value == null))
            errors.Add("rule contribution parameters must be canonical scalar strings");
        return errors;
    }

    public RuleContribution ToDomain()
    {
        if (!Enum.TryParse<RuleContributionEffect>(Effect, ignoreCase: false, out var parsed) ||
            !Enum.IsDefined(parsed) || Source == null)
            throw new ArgumentException("Unsupported rule contribution.");
        return new RuleContribution(
            RuleId, RuleVersion, parsed, Target, Scope, Priority, Source.ToDomain(),
            Array.AsReadOnly(Parameters.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new RuleContributionParameter(x.Key, x.Value)).ToArray()));
    }
}

public sealed class WorldV2RuleContributionSourceOptions
{
    [JsonPropertyName("sourceKind")] public string SourceKind { get; set; } = "";
    [JsonPropertyName("definitionId")] public string DefinitionId { get; set; } = "";
    [JsonPropertyName("definitionVersion")] public int DefinitionVersion { get; set; }
    [JsonPropertyName("sourceInstanceId")] public string SourceInstanceId { get; set; } = "";
    [JsonPropertyName("sourceRevision")] public int SourceRevision { get; set; }
    [JsonPropertyName("slotId")] public string SlotId { get; set; } = "";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(SourceKind) || string.IsNullOrWhiteSpace(DefinitionId) ||
            DefinitionVersion <= 0 || string.IsNullOrWhiteSpace(SourceInstanceId) ||
            SourceRevision <= 0 || string.IsNullOrWhiteSpace(SlotId))
            errors.Add("rule contribution source provenance is incomplete");
        return errors;
    }

    public RuleContributionSource ToDomain() =>
        new(SourceKind, DefinitionId, DefinitionVersion, SourceInstanceId, SourceRevision, SlotId);
}

public sealed class WorldV2BoardAssignmentSnapshotOptions
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("boardId")] public string BoardId { get; set; } = "";
    [JsonPropertyName("definitionVersion")] public int DefinitionVersion { get; set; }
    [JsonPropertyName("assignmentFingerprint")] public string AssignmentFingerprint { get; set; } = "";
    [JsonPropertyName("boardDefinition")] public WorldV2BoardDefinitionSnapshotOptions? BoardDefinition { get; set; }
    [JsonPropertyName("assignments")] public List<WorldV2BoardAssignmentEntryOptions> Assignments { get; set; } = new();

    public IReadOnlyList<string> Validate(string boardId, int boardVersion, string assignmentFingerprint)
    {
        var errors = new List<string>();
        if (SchemaVersion != 1 || BoardId != boardId || DefinitionVersion != boardVersion ||
            AssignmentFingerprint != assignmentFingerprint || BoardDefinition == null ||
            BoardDefinition.SchemaVersion != 1 || BoardDefinition.BoardId != BoardId ||
            BoardDefinition.DefinitionVersion != DefinitionVersion || string.IsNullOrWhiteSpace(BoardDefinition.DisplayName))
            errors.Add("boardRuleProfile assignment snapshot identity does not match its frozen Board");
        if (Assignments == null || BoardDefinition?.Slots == null)
        {
            errors.Add("board assignment snapshot requires slots and assignment entries");
            return errors;
        }
        var slots = new Dictionary<string, WorldV2BoardSlotSnapshotOptions>(StringComparer.Ordinal);
        foreach (var slot in BoardDefinition.Slots)
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.SlotId) ||
                string.IsNullOrWhiteSpace(slot.AreaId) || string.IsNullOrWhiteSpace(slot.ComponentKind) ||
                string.IsNullOrWhiteSpace(slot.DisplayName) || string.IsNullOrWhiteSpace(slot.RoleId) ||
                string.IsNullOrWhiteSpace(slot.ControlArea) || !slots.TryAdd(slot.SlotId, slot))
                errors.Add("Board Definition contains an invalid or duplicate slot identity");
            else if (!IsValidSlotShape(slot))
                errors.Add("Board Definition slot has an unsupported area/component/role/control-area combination: " + slot.SlotId);
        }
        var seenInstances = new HashSet<string>(StringComparer.Ordinal);
        var seenSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Assignments)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.SlotId) ||
                string.IsNullOrWhiteSpace(entry.OwnedInstanceId) ||
                string.IsNullOrWhiteSpace(entry.DefinitionId) || entry.DefinitionVersion <= 0 ||
                entry.SourceRevision <= 0 ||
                !seenSlots.Add(entry.SlotId) || !seenInstances.Add(entry.OwnedInstanceId))
            {
                errors.Add("board assignment contains invalid or duplicate slot/instance identity");
                continue;
            }
            if (!slots.TryGetValue(entry.SlotId, out var slot) || slot.ComponentKind != entry.ComponentKind)
                errors.Add("board assignment does not match its frozen slot definition: " + entry.SlotId);
            else if (entry.ComponentKind == "workshop" && (slot.RoleId ?? "") != (entry.RoleId ?? ""))
                errors.Add("workshop assignment role does not match its frozen Board Slot: " + entry.SlotId);
            else if (entry.ComponentKind == "skill" && (slot.ControlArea ?? "") != (entry.ControlArea ?? ""))
                errors.Add("skill assignment control area does not match its frozen Board Slot: " + entry.SlotId);
        }
        foreach (var slot in BoardDefinition.Slots)
            if (slot.Required && !seenSlots.Contains(slot.SlotId))
                errors.Add("required Board Slot is not assigned: " + slot.SlotId);
        if (Assignments.Select(x => x.SlotId).SequenceEqual(
            Assignments.Select(x => x.SlotId).OrderBy(x => x, StringComparer.Ordinal)) == false)
            errors.Add("boardAssignmentSnapshot assignments must be ordered by stable slotId");
        if (errors.Count == 0 && !string.Equals(
            ComputeCanonicalFingerprint(), AssignmentFingerprint, StringComparison.OrdinalIgnoreCase))
            errors.Add("boardAssignmentSnapshot fingerprint does not match its exact frozen content");
        return errors;
    }

    private static bool IsValidSlotShape(WorldV2BoardSlotSnapshotOptions slot) =>
        (slot.AreaId == "workshop" && slot.ComponentKind == "workshop" &&
            (slot.RoleId == "master" || slot.RoleId == "slave") && slot.ControlArea == "none") ||
        (slot.AreaId == "decomposition" && slot.ComponentKind == "ore_parser" &&
            slot.RoleId == "none" && slot.ControlArea == "none") ||
        (slot.AreaId == "orders" && slot.ComponentKind == "skill" &&
            slot.RoleId == "none" && (slot.ControlArea == "Active" || slot.ControlArea == "Passive")) ||
        (slot.AreaId == "production" && slot.ComponentKind == "production_device" &&
            slot.RoleId == "none" && slot.ControlArea == "none");

    public string ComputeCanonicalFingerprint()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("boardId", BoardId);
            writer.WriteNumber("definitionVersion", DefinitionVersion);
            writer.WritePropertyName("boardDefinition");
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", BoardDefinition!.SchemaVersion);
            writer.WriteString("boardId", BoardDefinition.BoardId);
            writer.WriteNumber("definitionVersion", BoardDefinition.DefinitionVersion);
            writer.WriteString("displayName", BoardDefinition.DisplayName);
            writer.WritePropertyName("slots");
            writer.WriteStartArray();
            foreach (var slot in BoardDefinition.Slots)
            {
                writer.WriteStartObject();
                writer.WriteString("slotId", slot.SlotId);
                writer.WriteString("areaId", slot.AreaId);
                writer.WriteString("componentKind", slot.ComponentKind);
                writer.WriteString("displayName", slot.DisplayName);
                writer.WriteString("roleId", slot.RoleId);
                writer.WriteString("controlArea", slot.ControlArea);
                writer.WriteBoolean("required", slot.Required);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WritePropertyName("assignments");
            writer.WriteStartArray();
            foreach (var entry in Assignments)
            {
                writer.WriteStartObject();
                writer.WriteString("slotId", entry.SlotId);
                writer.WriteString("componentKind", entry.ComponentKind);
                writer.WriteString("ownedInstanceId", entry.OwnedInstanceId);
                writer.WriteString("definitionId", entry.DefinitionId);
                writer.WriteNumber("definitionVersion", entry.DefinitionVersion);
                writer.WriteNumber("sourceRevision", entry.SourceRevision);
                if (entry.ComponentKind == "workshop")
                    writer.WriteString("roleId", entry.RoleId);
                if (entry.ComponentKind == "skill")
                    writer.WriteString("controlArea", entry.ControlArea);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

public sealed class WorldV2BoardDefinitionSnapshotOptions
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("boardId")] public string BoardId { get; set; } = "";
    [JsonPropertyName("definitionVersion")] public int DefinitionVersion { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("slots")] public List<WorldV2BoardSlotSnapshotOptions> Slots { get; set; } = new();
}

public sealed class WorldV2BoardSlotSnapshotOptions
{
    [JsonPropertyName("slotId")] public string SlotId { get; set; } = "";
    [JsonPropertyName("areaId")] public string AreaId { get; set; } = "";
    [JsonPropertyName("componentKind")] public string ComponentKind { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("roleId")] public string RoleId { get; set; } = "";
    [JsonPropertyName("controlArea")] public string ControlArea { get; set; } = "";
    [JsonPropertyName("required")] public bool Required { get; set; }
}

public sealed class WorldV2BoardAssignmentEntryOptions
{
    [JsonPropertyName("slotId")] public string SlotId { get; set; } = "";
    [JsonPropertyName("componentKind")] public string ComponentKind { get; set; } = "";
    [JsonPropertyName("ownedInstanceId")] public string OwnedInstanceId { get; set; } = "";
    [JsonPropertyName("definitionId")] public string DefinitionId { get; set; } = "";
    [JsonPropertyName("definitionVersion")] public int DefinitionVersion { get; set; }
    [JsonPropertyName("sourceRevision")] public int SourceRevision { get; set; }
    [JsonPropertyName("roleId")] public string? RoleId { get; set; }
    [JsonPropertyName("controlArea")] public string? ControlArea { get; set; }
}

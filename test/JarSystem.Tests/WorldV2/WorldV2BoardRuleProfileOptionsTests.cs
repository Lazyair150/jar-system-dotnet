using System.Collections.Generic;
using JarSystem.Core.World;
using Xunit;

namespace JarSystem.Tests.WorldV2;

public sealed class WorldV2BoardRuleProfileOptionsTests
{
    [Fact]
    public void ValidateAcceptsParserInDecompositionSubAreaOfProduction()
    {
        var assignment = new WorldV2BoardAssignmentSnapshotOptions
        {
            SchemaVersion = 1,
            BoardId = "demo.circuit_board",
            DefinitionVersion = 1,
            BoardDefinition = new WorldV2BoardDefinitionSnapshotOptions
            {
                SchemaVersion = 1,
                BoardId = "demo.circuit_board",
                DefinitionVersion = 1,
                DisplayName = "Demo circuit board",
                Slots = new List<WorldV2BoardSlotSnapshotOptions>
                {
                    new()
                    {
                        SlotId = "workshop.master",
                        AreaId = "workshop",
                        SubAreaId = "none",
                        ComponentKind = "workshop",
                        DisplayName = "Master workshop",
                        RoleId = "master",
                        ControlArea = "none",
                        Required = true
                    },
                    new()
                    {
                        SlotId = "workshop.slave",
                        AreaId = "workshop",
                        SubAreaId = "none",
                        ComponentKind = "workshop",
                        DisplayName = "Slave workshop",
                        RoleId = "slave",
                        ControlArea = "none",
                        Required = true
                    },
                    new()
                    {
                        SlotId = "decomposition.parser.primary",
                        AreaId = "production",
                        SubAreaId = "decomposition",
                        ComponentKind = "ore_parser",
                        DisplayName = "Ore parser",
                        RoleId = "none",
                        ControlArea = "none",
                        Required = true
                    }
                }
            },
            Assignments = new List<WorldV2BoardAssignmentEntryOptions>
            {
                new()
                {
                    SlotId = "workshop.master",
                    ComponentKind = "workshop",
                    OwnedInstanceId = "instance.master",
                    DefinitionId = "workshop.master",
                    DefinitionVersion = 1,
                    SourceRevision = 1,
                    RoleId = "master"
                },
                new()
                {
                    SlotId = "workshop.slave",
                    ComponentKind = "workshop",
                    OwnedInstanceId = "instance.slave",
                    DefinitionId = "workshop.slave",
                    DefinitionVersion = 1,
                    SourceRevision = 1,
                    RoleId = "slave"
                },
                new()
                {
                    SlotId = "decomposition.parser.primary",
                    ComponentKind = "ore_parser",
                    OwnedInstanceId = "instance.parser",
                    DefinitionId = "parser.universal",
                    DefinitionVersion = 1,
                    SourceRevision = 1
                }
            }
        };
        assignment.Assignments.Sort((left, right) => string.CompareOrdinal(left.SlotId, right.SlotId));
        assignment.AssignmentFingerprint = assignment.ComputeCanonicalFingerprint();

        var profile = new WorldV2BoardRuleProfileOptions
        {
            SchemaVersion = 1,
            BoardId = assignment.BoardId,
            BoardDefinitionVersion = assignment.DefinitionVersion,
            AssignmentFingerprint = assignment.AssignmentFingerprint,
            Contributions = new List<WorldV2RuleContributionOptions>()
        };
        profile.Fingerprint = profile.ComputeCanonicalFingerprint();

        Assert.Empty(profile.Validate(assignment));
    }

    [Fact]
    public void ValidateRejectsParserPlacedDirectlyInProductionWithoutDecompositionSubArea()
    {
        var assignment = CreateParserAssignment(subAreaId: "none");
        var profile = CreateProfile(assignment);

        var errors = profile.Validate(assignment);

        Assert.Contains(errors, error =>
            error.Contains("unsupported area/component/role/control-area combination", System.StringComparison.Ordinal));
    }

    private static WorldV2BoardAssignmentSnapshotOptions CreateParserAssignment(string subAreaId)
    {
        var assignment = new WorldV2BoardAssignmentSnapshotOptions
        {
            SchemaVersion = 1,
            BoardId = "board",
            DefinitionVersion = 1,
            BoardDefinition = new WorldV2BoardDefinitionSnapshotOptions
            {
                SchemaVersion = 1,
                BoardId = "board",
                DefinitionVersion = 1,
                DisplayName = "Board",
                Slots = new List<WorldV2BoardSlotSnapshotOptions>
                {
                    new()
                    {
                        SlotId = "workshop.master", AreaId = "workshop", SubAreaId = "none",
                        ComponentKind = "workshop", DisplayName = "Master", RoleId = "master",
                        ControlArea = "none", Required = true
                    },
                    new()
                    {
                        SlotId = "workshop.slave", AreaId = "workshop", SubAreaId = "none",
                        ComponentKind = "workshop", DisplayName = "Slave", RoleId = "slave",
                        ControlArea = "none", Required = true
                    },
                    new()
                    {
                        SlotId = "parser", AreaId = "production", SubAreaId = subAreaId,
                        ComponentKind = "ore_parser", DisplayName = "Parser", RoleId = "none",
                        ControlArea = "none", Required = true
                    }
                }
            },
            Assignments = new List<WorldV2BoardAssignmentEntryOptions>
            {
                new() { SlotId = "workshop.master", ComponentKind = "workshop", OwnedInstanceId = "m", DefinitionId = "m", DefinitionVersion = 1, SourceRevision = 1, RoleId = "master" },
                new() { SlotId = "workshop.slave", ComponentKind = "workshop", OwnedInstanceId = "s", DefinitionId = "s", DefinitionVersion = 1, SourceRevision = 1, RoleId = "slave" },
                new() { SlotId = "parser", ComponentKind = "ore_parser", OwnedInstanceId = "p", DefinitionId = "p", DefinitionVersion = 1, SourceRevision = 1 }
            }
        };
        assignment.AssignmentFingerprint = assignment.ComputeCanonicalFingerprint();
        return assignment;
    }

    private static WorldV2BoardRuleProfileOptions CreateProfile(WorldV2BoardAssignmentSnapshotOptions assignment)
    {
        var profile = new WorldV2BoardRuleProfileOptions
        {
            SchemaVersion = 1,
            BoardId = assignment.BoardId,
            BoardDefinitionVersion = assignment.DefinitionVersion,
            AssignmentFingerprint = assignment.AssignmentFingerprint,
            Contributions = new List<WorldV2RuleContributionOptions>()
        };
        profile.Fingerprint = profile.ComputeCanonicalFingerprint();
        return profile;
    }
}

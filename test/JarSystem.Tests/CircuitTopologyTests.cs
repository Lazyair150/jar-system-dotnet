using System;
using JarSystem.Core.Domain;
using Xunit;

namespace JarSystem.Tests;

public sealed class CircuitTopologyTests
{
    [Fact]
    public void ProductionCanContainADecompositionSubAreaWithoutCreatingAnotherBoardArea()
    {
        var topology = new CircuitTopology(
        [
            new CircuitAreaDefinition("ship.production", "ship.01", "production", false),
            new CircuitAreaDefinition("ship.production.decomposition", "ship.01", "decomposition", false, "ship.production"),
            new CircuitAreaDefinition("workshop.01.jar-storage", "workshop.01", "jar-storage", true),
            new CircuitAreaDefinition("ship.orders", "ship.01", "orders", false)
        ],
        [
            new CircuitPortDefinition("parser.output", "ship.production.decomposition", ResourcePortDirection.Output,
                RuleResourceKind.Jar, ["圆罐", "方罐"], "parser.output-policy", 1),
            new CircuitPortDefinition("workshop.jar-input", "workshop.01.jar-storage", ResourcePortDirection.Input,
                RuleResourceKind.Jar, ["圆罐", "方罐"], "workshop.jar-intake", 1)
        ],
        [
            new CircuitConnectionDefinition("parser-to-workshop", "parser.output", "workshop.jar-input")
        ]);

        Assert.Equal("ship.production", topology.Areas["ship.production.decomposition"].ParentAreaId);
        Assert.Equal("ship.01", topology.Areas["ship.production.decomposition"].OwnerIndividualId);
        Assert.Single(topology.Connections);
    }

    [Fact]
    public void NestedAreaMustHaveSameOwnerAsItsParent()
    {
        Assert.Throws<ArgumentException>(() => new CircuitTopology(
            [
                new CircuitAreaDefinition("ship.production", "ship.01", "production", false),
                new CircuitAreaDefinition("workshop.decomposition", "workshop.01", "decomposition", false, "ship.production")
            ],
            [],
            []));
    }

    [Fact]
    public void ParentHierarchyCannotContainCycles()
    {
        Assert.Throws<ArgumentException>(() => new CircuitTopology(
            [
                new CircuitAreaDefinition("area.a", "owner", "a", false, "area.b"),
                new CircuitAreaDefinition("area.b", "owner", "b", false, "area.a")
            ],
            [],
            []));
    }

    [Fact]
    public void ConnectionMustBeOutputToInputAndResourceKindMustMatch()
    {
        var areas = new[] { new CircuitAreaDefinition("a", "owner.a", "a", false), new CircuitAreaDefinition("b", "owner.b", "b", true) };
        var output = new CircuitPortDefinition("a.out", "a", ResourcePortDirection.Output, RuleResourceKind.Jar, null, "policy", 1);
        var input = new CircuitPortDefinition("b.in", "b", ResourcePortDirection.Input, RuleResourceKind.Liquid, null, "policy", 1);
        Assert.Throws<ArgumentException>(() => new CircuitTopology(areas, [output, input],
            [new CircuitConnectionDefinition("bad", "a.out", "b.in")]));
    }

    [Fact]
    public void IdentityConstraintsMustOverlapWhenBothEndsAreConstrained()
    {
        var areas = new[] { new CircuitAreaDefinition("a", "owner.a", "a", false), new CircuitAreaDefinition("b", "owner.b", "b", true) };
        var output = new CircuitPortDefinition("a.out", "a", ResourcePortDirection.Output, RuleResourceKind.Jar, ["圆罐"], "policy", 1);
        var input = new CircuitPortDefinition("b.in", "b", ResourcePortDirection.Input, RuleResourceKind.Jar, ["方罐"], "policy", 1);
        Assert.Throws<ArgumentException>(() => new CircuitTopology(areas, [output, input],
            [new CircuitConnectionDefinition("bad", "a.out", "b.in")]));
    }
    [Fact]
    public void PortCarriesOptionalTransferAndLogicalThroughputLimits()
    {
        var port = new CircuitPortDefinition(
            "production.out", "production", ResourcePortDirection.Output,
            RuleResourceKind.Jar, ["圆罐"], "production.policy", 2,
            maxAmountPerTransfer: 4, maxAmountPerLogicalTimeMilli: 2500);

        Assert.Equal(4, port.MaxAmountPerTransfer);
        Assert.Equal(2500, port.MaxAmountPerLogicalTimeMilli);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitPortDefinition(
            "bad.out", "production", ResourcePortDirection.Output,
            RuleResourceKind.Jar, null, "production.policy", 2,
            maxAmountPerTransfer: -1));
    }

}

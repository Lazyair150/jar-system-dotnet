using JarSystem.Core.Domain;
using JarSystem.Core.Rules;
using Xunit;

namespace JarSystem.Tests;

public class ScenarioValidationTests
{
    [Fact]
    public void NegativeWindowsAndIntensityFactors_AreRejected()
    {
        var scenario = new ScenarioOptions
        {
            InitialModifiers =
            [
                new ModifierData
                {
                    Kind = "intensity",
                    Target = "flow:jars",
                    FactorPerMille = -1,
                    From = -2,
                    To = 4
                }
            ]
        };

        var report = scenario.Validate();

        Assert.Contains(report.Errors, e => e.Contains("from/to"));
        Assert.Contains(report.Errors, e => e.Contains("factorPerMille"));
    }

    [Fact]
    public void ExtremeIntensityFactor_IsRejectedAtTheDataBoundary()
    {
        var scenario = new ScenarioOptions
        {
            InitialModifiers =
            [
                new ModifierData
                {
                    Kind = "intensity",
                    Target = "flow:jars",
                    FactorPerMille = 100_001,
                    To = 4
                }
            ]
        };

        Assert.Contains(scenario.Validate().Errors, e => e.Contains("factorPerMille"));
    }

    [Fact]
    public void IntensityApplication_SaturatesInsteadOfOverflowing()
    {
        var stack = new ModifierStack();
        using var scope = stack.Push(new IntensityModifier
        {
            Target = "flow:jars",
            FactorPerMille = int.MaxValue
        });

        Assert.Equal(int.MaxValue, stack.ApplyIntensity("flow:jars", int.MaxValue));
    }
}

using System.Collections.Generic;
using JarSystem.Core.Rules;
using Xunit;

namespace JarSystem.Tests;

public class RuleVocabularyTests
{
    [Fact]
    public void WorldQueryVocabularies_CannotBeMutatedByCallers()
    {
        var what = Assert.IsAssignableFrom<IList<string>>(WorldQuery.WhatValues);
        var ops = Assert.IsAssignableFrom<IList<string>>(WorldQuery.OpValues);

        Assert.Throws<NotSupportedException>(() => what[0] = "tampered");
        Assert.Throws<NotSupportedException>(() => ops[0] = "tampered");
    }
}

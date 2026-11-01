# Agent Rules

Read [CONSTRAINTS.md](CONSTRAINTS.md) before writing code. Do not weaken it to make a change pass.

The only full verification loop is:

```bat
set CI=1
重构报告\verify.bat
```

Focused test entry (xUnit v3 executable runner):

```bat
dotnet run --project test\JarSystem.Tests\JarSystem.Tests.csproj -c Release
```

`VERDICT=OK` and `QUALITY-OK` are both required. Contract artifacts, replay bytes, and process
determinism are part of the public surface; do not regenerate or re-baseline them silently.

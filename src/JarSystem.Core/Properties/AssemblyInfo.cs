using System.Runtime.CompilerServices;

// The xUnit project owns focused regression tests for internal process ports
// (for example the one-shot completion handshake). Keep production visibility
// unchanged; only the test assembly can see internals.
[assembly: InternalsVisibleTo("JarSystem.Tests")]

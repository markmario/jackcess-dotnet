using System;

namespace JackcessDotNet.AccessTests.Support
{
    /// <summary>
    /// Whether the referee tests whose sweeps take minutes run them whole or run a sample of them.
    /// <c>JACKCESS_FULL_SWEEPS=1</c> runs every sweep whole; without it each of those tests runs its sample, chosen so
    /// that a change to what the sweep proves still fails it. CI runs the samples on a pull request and the full sweeps
    /// on a push to <c>main</c>, every night and when asked (<c>.github/workflows/ci.yml</c>).
    /// </summary>
    internal static class Sweeps
    {
        /// <summary>The name of the environment variable that asks for the full sweeps.</summary>
        public const string Variable = "JACKCESS_FULL_SWEEPS";

        /// <summary>True when the full sweeps are asked for.</summary>
        public static bool Full { get; } = Environment.GetEnvironmentVariable(Variable) == "1";
    }
}

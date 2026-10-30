using System;
using System.Collections.Generic;

namespace PioneerFramework.Tests;

/// <summary>Only the explicitly expected restore diagnostic is captured; every unrelated Log.Error still fails the fixture.</summary>
internal sealed class CelestialExpectedRestoreErrorScope : IDisposable
{
    private readonly Func<string, bool> previous = CelestialXmlTestScope.ExpectedError;
    internal List<string> Messages { get; } = new();
    internal CelestialExpectedRestoreErrorScope()
    {
        CelestialXmlTestScope.ExpectedError = message => {
            if (!message.StartsWith("[PFL Celestial] Restore suspended;", StringComparison.Ordinal)) return previous?.Invoke(message) == true;
            Messages.Add(message);
            return true;
        };
    }
    public void Dispose() => CelestialXmlTestScope.ExpectedError = previous;
}

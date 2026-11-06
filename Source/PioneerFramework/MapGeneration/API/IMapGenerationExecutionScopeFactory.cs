using System;

namespace PioneerFramework.MapGeneration
{
    /// <summary>Optional host boundary around each factory/Execute/validation and PostInitialize callback.
    /// Dispose must release host state even when it reports a callback failure.</summary>
    public interface IMapGenerationExecutionScopeFactory
    {
        IDisposable Enter(MapGenerationContext context, bool postInitialize);
    }
}

using System.Collections.Generic;

namespace PioneerFramework.Megastructures
{
    public interface IMegastructureService
    {
        FrameworkResult Create(CreateStructureRequest request);
        FrameworkResult Execute(StructureCommand command);
        StructureState? GetSnapshot(string instanceId);
        IReadOnlyList<StructureState> Snapshots { get; }
    }
}

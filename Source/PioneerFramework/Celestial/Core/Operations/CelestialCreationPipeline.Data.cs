using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed partial class CelestialCreationPipeline
    {
        internal CelestialOperation CreateData(CelestialCreateBatchRequest request)
        {
            var scope = request.Scope;
            var epochError = operations.Epochs.Check(scope.Epoch);
            if (epochError != null) return Reject(scope.OperationId, epochError);
            scope = scope with { OperationId = scope.QualifiedOperationId };
            if (scope.WorldSessionId != WorldSessionId) return Reject(scope.OperationId, "StaleSession");
            if (request.Items.Count == 0 || request.Items.Any(i => string.IsNullOrWhiteSpace(i.Key)) ||
                request.Items.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count() != request.Items.Count)
                return Reject(scope.OperationId, "InvalidCreationRequest");
            try
            {
                var items = new List<CelestialSpawnItem>();
                foreach (var input in request.Items)
                {
                    var template = resolveTemplate(input.DefinitionId);
                    if (!template.Success) return Rejected(scope.OperationId, template.Errors);
                    string path = "operation/" + Uri.EscapeDataString(scope.OperationId) + "/" + Uri.EscapeDataString(input.Key) + "[0]";
                    ulong seed = input.Seed ?? CelestialSeed.Derive(worldSeed, path, "$create", "seed");
                    CelestialGenerationContext generation;
                    try { generation = new(seed, path, parameterOverrides: input.ParameterOverrides); }
                    catch (ArgumentException exception) { return Rejected(scope.OperationId, new[] { new CelestialError("InvalidParameterOverride", Detail: exception.Message) }); }
                    var resolved = template.Value.Resolve(generation);
                    if (!resolved.Success) return Rejected(scope.OperationId, resolved.Errors);
                    items.Add(new(CelestialInstanceId.Parse("dynamic:" + path), input.Key, 0, path, template.Value, resolved.Value, input.ParentId, input.InitializationReferences));
                }
                return CreateBatch(new(new(scope.OperationId, scope.WorldSessionId, scope.ExpectedRevision), new("dynamic", worldSeed, null, items)));
            }
            catch (Exception exception) { return Rejected(scope.OperationId, new[] { new CelestialError("CreationResolutionFailed", Detail: exception.Message) }); }
        }
    }
}

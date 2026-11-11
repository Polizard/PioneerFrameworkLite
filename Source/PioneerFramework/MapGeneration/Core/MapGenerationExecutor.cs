using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PioneerFramework.MapGeneration
{
    public sealed class MapGenerationExecutor
    {
        private readonly MapGenerationPlan plan;
        private readonly MapSourceContext source;
        private readonly IServiceProvider? services;
        private readonly IReadOnlyDictionary<string, IMapGenerationModule>? preparedModules;
        private readonly Dictionary<string, object> data = new(StringComparer.Ordinal);
        private readonly List<(IMapGenerationModule Module, MapGenerationContext Context)> completed = new();
        private readonly List<MapGenerationStepReport> reports = new();
        private MapGenerationState state;
        private bool executionStarted;
        private bool postInitializationStarted;
        public MapGenerationReport Report => new(state, source, plan.Purpose, reports, plan.Diagnostics);
        public MapGenerationExecutor(MapGenerationPlan plan, MapSourceContext source, IServiceProvider? services = null,
            IReadOnlyDictionary<string, object>? externalInputs = null, IReadOnlyDictionary<string, IMapGenerationModule>? preparedModules = null)
        {
            this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.services = services;
            this.preparedModules = preparedModules == null ? null : new Dictionary<string, IMapGenerationModule>(preparedModules, StringComparer.Ordinal);
            var supplied = externalInputs ?? new Dictionary<string, object>();
            if (supplied.Count != plan.ExternalInputs.Count) throw new ArgumentException("External input schema does not match supplied data.", nameof(externalInputs));
            foreach (var port in plan.ExternalInputs)
            {
                if (!supplied.TryGetValue(port.Name, out object? value) || value == null || !port.RuntimeType.IsInstanceOfType(value))
                    throw new ArgumentException("Missing or incompatible external input: " + port.Name, nameof(externalInputs));
                data.Add(port.Name, value);
            }
        }
        public MapGenerationReport Execute()
        {
            if (executionStarted) throw new InvalidOperationException("Generation execution is single-use.");
            executionStarted = true;
            foreach (var compiled in plan.Steps)
            {
                var step = compiled.Spec;
                ulong seed = MapGenerationSeeds.ForStep(source, plan.Purpose, step);
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                try
                {
                    var context = new MapGenerationContext(source, plan.Purpose, compiled, seed, data, services);
                    IMapGenerationModule module;
                    using (context.GetService<IMapGenerationExecutionScopeFactory>()?.Enter(context, false))
                    {
                        module = preparedModules != null && preparedModules.TryGetValue(step.Key, out var prepared) ? prepared : compiled.Registration.Create(step.Configuration);
                        module.Execute(context);
                        context.ValidateOutputs();
                    }
                    completed.Add((module, context));
                    reports.Add(new(step.Key, step.Stage, step.HandlerId, step.ConfigurationVersion, step.AlgorithmVersion,
                        seed, step.IsOpaque, true, false, Stopwatch.GetElapsedTime(started), GC.GetAllocatedBytesForCurrentThread() - allocated));
                }
                catch (Exception exception)
                {
                    reports.Add(new(step.Key, step.Stage, step.HandlerId, step.ConfigurationVersion, step.AlgorithmVersion,
                        seed, step.IsOpaque, false, false, Stopwatch.GetElapsedTime(started), GC.GetAllocatedBytesForCurrentThread() - allocated,
                        exception.ToString(), exception));
                    state = MapGenerationState.Failed;
                    return Report;
                }
            }
            state = MapGenerationState.Generated;
            return Report;
        }
        public MapGenerationReport PostInitialize()
        {
            if (state != MapGenerationState.Generated || postInitializationStarted)
                throw new InvalidOperationException("Post initialization requires complete generation and is single-use.");
            postInitializationStarted = true;
            for (int i = 0; i < completed.Count; i++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                var previous = reports[i];
                try
                {
                    using (completed[i].Context.GetService<IMapGenerationExecutionScopeFactory>()?.Enter(completed[i].Context, true))
                    {
                        if (completed[i].Module is IMapGenerationPostInitializer callback) callback.PostInitialize(completed[i].Context);
                    }
                    reports[i] = previous with { PostInitialized = true, Elapsed = previous.Elapsed + Stopwatch.GetElapsedTime(started),
                        AllocatedBytes = previous.AllocatedBytes + GC.GetAllocatedBytesForCurrentThread() - allocated };
                }
                catch (Exception exception)
                {
                    reports[i] = previous with { Error = exception.ToString(), Exception = exception,
                        Elapsed = previous.Elapsed + Stopwatch.GetElapsedTime(started),
                        AllocatedBytes = previous.AllocatedBytes + GC.GetAllocatedBytesForCurrentThread() - allocated };
                    state = MapGenerationState.Failed;
                    return Report;
                }
            }
            state = MapGenerationState.Ready;
            return Report;
        }
    }
}

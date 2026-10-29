using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>提交后分阶段调度：短锁选择与记录，组件回调在发布锁外执行；移除实例的游标随目录修订清理。</summary>
    public sealed class CelestialCompScheduler
    {
        private readonly CelestialCatalog catalog;
        private readonly CelestialCreationPipeline creation;
        private readonly Dictionary<(CelestialInstanceId Id, string Key), double> cursors = new();
        private readonly Queue<CelestialPendingEvent> events = new();
        private PhysicsSampleStamp? lastStamp;
        private long indexedRevision = -1;
        private readonly SortedSet<Scheduled> due = new(Comparer<Scheduled>.Create((a, b) => {
            int result = a.Seconds.CompareTo(b.Seconds);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Instance.Id.Value, b.Instance.Id.Value);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Component.Descriptor.Key, b.Component.Descriptor.Key);
        }));
        private sealed record Scheduled(double Seconds, CelestialBodyInstance Instance, CelestialBodyComp Component);
        private readonly record struct ScheduledWork(Scheduled Item, CelestialUpdateContext Context);
        private sealed record EventWork(CelestialBodyInstance Instance, CelestialPendingEvent Event, CelestialBodyComp[] Components);
        public CelestialCompScheduler(CelestialCatalog catalog, CelestialCreationPipeline creation) { this.catalog = catalog; this.creation = creation; }
        public CelestialResult<bool> Enqueue(CelestialInstanceId id, CelestialEvent message, string? compKey = null)
        {
            if (!catalog.TryGetInstance(id, out var instance) || instance.Lifecycle != CelestialLifecycle.Ready)
                return CelestialResult.Reject<bool>(new CelestialError("BodyNotReady", id));
            if (compKey != null && !creation.GetComponents(id).Any(component => component.Descriptor.Key == compKey && component.IsAttached))
                return CelestialResult.Reject<bool>(new CelestialError("CompMissing", id, compKey));
            events.Enqueue(new CelestialPendingEvent(id, compKey, message));
            return CelestialResult.Ok(true);
        }
        internal void OnCommitted(PhysicsSampleStamp stamp, CelestialRuntime runtime)
        {
            if (!runtime.WithPublication(() => BeginCommitted(stamp))) return;
            while (true)
            {
                var selected = runtime.WithPublication<ScheduledWork?>(() => {
                    while (due.Count > 0 && due.Min!.Seconds <= stamp.PhysicalSeconds)
                    {
                        var item = due.Min!; due.Remove(item);
                        if (item.Instance.Lifecycle != CelestialLifecycle.Ready || item.Component.IsReleased) continue;
                        double start = cursors[(item.Instance.Id, item.Component.Descriptor.Key)];
                        return new ScheduledWork(item, new CelestialUpdateContext(start, stamp.PhysicalSeconds, stamp.StateRevision));
                    }
                    return null;
                });
                if (!selected.HasValue) break;
                var work = selected.Value;
                Exception? failure = null;
                try { work.Item.Component.Advance(work.Context); }
                catch (Exception exception) { failure = exception; }
                runtime.WithPublication(() => {
                    var instance = work.Item.Instance; var component = work.Item.Component;
                    if (failure != null) Block(instance, component, "ComponentAdvanceFailed", failure);
                    else
                    {
                        cursors[(instance.Id, component.Descriptor.Key)] = stamp.PhysicalSeconds;
                        double next = stamp.PhysicalSeconds + component.Descriptor.UpdatePolicy.IntervalSeconds;
                        if (next <= stamp.PhysicalSeconds) next = Math.BitIncrement(stamp.PhysicalSeconds);
                        due.Add(new(next, instance, component));
                    }
                    return true;
                });
            }

            // Advance 可以投递本批事件；Handle 中追加的事件保持留待下一次提交。
            int eventCount = runtime.WithPublication(() => events.Count);
            for (int index = 0; index < eventCount; index++)
            {
                var work = runtime.WithPublication<EventWork?>(() => {
                    var message = events.Dequeue();
                    if (!catalog.TryGetInstance(message.InstanceId, out var instance) || instance.Lifecycle != CelestialLifecycle.Ready) return null;
                    return new EventWork(instance, message, creation.GetComponents(instance.Id)
                        .Where(component => component.IsAttached && component.Descriptor.UpdatePolicy.Mode != CelestialUpdateMode.None &&
                            (message.CompKey == null || message.CompKey == component.Descriptor.Key)).ToArray());
                });
                if (work == null) continue;
                foreach (var component in work.Components)
                {
                    bool active = runtime.WithPublication(() => component.IsAttached);
                    if (!active) continue;
                    try { component.Handle(work.Event.Message); }
                    catch (Exception exception)
                    {
                        runtime.WithPublication(() => { Block(work.Instance, component, "ComponentEventFailed", exception); return true; });
                        break;
                    }
                }
            }
        }

        private bool BeginCommitted(PhysicsSampleStamp stamp)
        {
            if (lastStamp.HasValue && lastStamp.Value.WorldSessionId == stamp.WorldSessionId && lastStamp.Value.StateRevision >= stamp.StateRevision) return false;
            lastStamp = stamp;
            if (indexedRevision != catalog.Revision)
            {
                PruneRetiredCursors();
                due.Clear();
                foreach (var instance in catalog.CaptureInstances().Where(instance => instance.Lifecycle == CelestialLifecycle.Ready))
                {
                    foreach (var component in creation.GetComponents(instance.Id).Where(component => component.IsAttached))
                    {
                        var key = (instance.Id, component.Descriptor.Key);
                        if (!cursors.TryGetValue(key, out double start)) cursors.Add(key, start = stamp.PhysicalSeconds);
                        if (component.Descriptor.UpdatePolicy.Mode == CelestialUpdateMode.Scheduled)
                            due.Add(new(start + component.Descriptor.UpdatePolicy.IntervalSeconds, instance, component));
                    }
                }
                indexedRevision = catalog.Revision;
            }
            return true;
        }

        private void PruneRetiredCursors(CelestialInstanceId? onlyInstance = null)
        {
            foreach (var key in cursors.Keys)
            {
                if (onlyInstance.HasValue && key.Id != onlyInstance.Value) continue;
                if (!catalog.TryGetInstance(key.Id, out var instance) || instance.Lifecycle == CelestialLifecycle.Removed)
                { cursors.Remove(key); continue; }
                // 缺失扩展/提供者时恢复可能暂挂；这些游标仍是可恢复状态。
                if (instance.Lifecycle == CelestialLifecycle.Blocked || instance.RestoreSuspension != null) continue;
                var component = creation.GetComponents(key.Id).FirstOrDefault(component => component.Descriptor.Key == key.Key);
                if (component != null ? component.IsReleased : !instance.CompDescriptors.ContainsKey(key.Key)) cursors.Remove(key);
            }
        }

        private void Block(CelestialBodyInstance instance, CelestialBodyComp component, string code, Exception exception)
        {
            creation.HandleComponentFault(instance, component, code, exception);
            PruneRetiredCursors(instance.Id);
        }
        public IReadOnlyList<CelestialSchedulerCursor> CaptureCursors() => Array.AsReadOnly(cursors.OrderBy(pair => pair.Key.Id.Value, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Key, StringComparer.Ordinal).Select(pair => new CelestialSchedulerCursor(pair.Key.Id, pair.Key.Key, pair.Value)).ToArray());
        public IReadOnlyList<CelestialPendingEvent> CaptureEvents() => Array.AsReadOnly(events.ToArray());
        internal void Restore(IEnumerable<CelestialSchedulerCursor> savedCursors, IEnumerable<CelestialPendingEvent> savedEvents)
        {
            foreach (var cursor in savedCursors) cursors.Add((cursor.InstanceId, cursor.CompKey), cursor.LastSeconds);
            foreach (var message in savedEvents) events.Enqueue(message);
        }
    }
}

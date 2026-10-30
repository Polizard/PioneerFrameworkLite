using System;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public abstract class CelestialBodyComp
    {
        private ICelestialCompContext? context;
        private bool released;
        private ICelestialResolvedProperties? initializedProperties;
        public ICelestialResolvedProperties? ResolvedProperties => initializedProperties;
        public CelestialAttachReason AttachReason { get; private set; }
        public bool IsDisabled { get; private set; }
        public CelestialError? Failure { get; private set; }
        private bool restoredNotified;
        private bool restoredState;
        internal void MarkRestored() => restoredState = true;
        public CelestialCompDescriptor Descriptor { get; private set; }
        protected ICelestialCompContext Context => context ?? throw new InvalidOperationException("Component is not attached.");
        public bool IsAttached => context != null && !released && !IsDisabled;
        internal bool IsReleased => released;

        protected CelestialBodyComp(CelestialCompDescriptor descriptor)
            => Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));

        internal void Initialize(ICelestialResolvedProperties properties)
        {
            if (initializedProperties != null && !ReferenceEquals(initializedProperties, properties))
                throw new InvalidOperationException("Component configuration is already bound.");
            if (properties.Key != Descriptor.Key || properties.HandlerId != Descriptor.HandlerId)
                throw new ArgumentException("Resolved properties do not match component.");
            initializedProperties = properties;
        }

        internal void BindDescriptor(CelestialCompDescriptor descriptor)
        {
            // Factories declare intrinsic dependencies; XML and restored descriptors may add further requirements.
            // They must retain every intrinsic requirement, while additional ordering dependencies remain valid.
            if (context != null || released) throw new InvalidOperationException("Component metadata is already bound.");
            if (Descriptor.Key != descriptor.Key || Descriptor.HandlerId != descriptor.HandlerId || Descriptor.SchemaVersion != descriptor.SchemaVersion ||
                Descriptor.ExclusiveSlot != descriptor.ExclusiveSlot ||
                !Descriptor.Provides.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(descriptor.Provides.OrderBy(value => value, StringComparer.Ordinal)) ||
                Descriptor.Requires.Except(descriptor.Requires, StringComparer.Ordinal).Any())
                throw new ArgumentException("Component factory metadata does not match resolved configuration.", nameof(descriptor));
            Descriptor = descriptor;
        }

        public void Attach(ICelestialCompContext context, CelestialAttachReason reason = CelestialAttachReason.Created)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (this.context != null || released) throw new InvalidOperationException("Component cannot be attached again.");
            if (!context.InstanceId.IsValid || context.Properties.Key != Descriptor.Key || context.Properties.HandlerId != Descriptor.HandlerId)
                throw new ArgumentException("Component context does not match its descriptor.", nameof(context));
            this.context = context;
            Initialize(context.Properties);
            AttachReason = restoredState ? CelestialAttachReason.Restored : reason;
            if (AttachReason == CelestialAttachReason.Created) OnInitialized();
            OnAttached();
        }

        protected virtual void OnAttached() { }
        protected virtual void OnInitialized() { }
        public virtual void OnRestored() { }
        internal void NotifyRestored()
        { if (restoredNotified || !IsAttached || AttachReason != CelestialAttachReason.Restored) return; restoredNotified = true; OnRestored(); }
        internal void Disable(CelestialError failure)
        { IsDisabled = true; Failure = failure; Release(); }
        internal void RecordFailure(CelestialError failure) => Failure = failure;
        public virtual void OnReady() { }
        public virtual void Advance(CelestialUpdateContext update) { }
        public virtual void Handle(CelestialEvent message) { }
        public virtual ReadOnlyMemory<byte> CaptureState() => ReadOnlyMemory<byte>.Empty;
        public virtual CelestialRemovalCheck CanRemove() => CelestialRemovalCheck.Permit();
        public void Release()
        {
            if (released) return;
            released = true;
            try { OnRelease(); }
            finally { context = null; }
        }
        protected virtual void OnRelease() { }
    }
}

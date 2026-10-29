using System.Buffers.Binary;
using System.IO;
using PioneerFramework.Celestial;
using Xunit;

namespace PioneerFramework.Pure.Tests;

public class CelestialCompStateTests
{
    [Fact]
    public void FactoryInstancesAndRestoredStateRemainIndependent()
    {
        var registry = Registry();
        var properties = new CounterProperties("counter", 3);
        var factory = new CounterFactory();
        var first = (CounterComp)factory.Create(properties);
        var second = (CounterComp)factory.Create(properties);
        first.Handle(new CelestialEvent("increment"));
        var saved = CelestialCompStateCodec.Encode(first, properties);
        var restored = CelestialCompStateCodec.Decode(saved, registry);

        Assert.Equal(4, first.Value);
        Assert.Equal(3, second.Value);
        Assert.True(restored.Success);
        Assert.True(restored.Value.IsReady);
        Assert.Equal(4, Assert.IsType<CounterComp>(restored.Value.Comp).Value);
        Assert.Equal(3, Assert.IsType<CounterProperties>(restored.Value.Properties).Initial);
    }

    [Fact]
    public void UnknownHandlerRetainsExactConfigurationAndState()
    {
        var raw = new CelestialCompSaveRecord("unknown", "missing.extension", 7, 9,
            new byte[] { 0, 13, 10, 32, 255 }, new byte[] { 8, 9, 10 });
        var result = CelestialCompStateCodec.Decode(raw, new CelestialCompRegistry());
        Assert.True(result.Success);
        Assert.False(result.Value.IsReady);
        Assert.Equal("HandlerMissing", Assert.Single(result.Value.Errors).Code);
        Assert.Equal(raw.ConfigurationPayload.ToArray(), result.Value.OriginalRecord.ConfigurationPayload.ToArray());
        Assert.Equal(raw.StatePayload.ToArray(), result.Value.OriginalRecord.StatePayload.ToArray());
        Assert.Equal(7, result.Value.OriginalRecord.ConfigurationVersion);
        Assert.Equal(9, result.Value.OriginalRecord.SchemaVersion);
    }

    [Fact]
    public void KnownOlderStateMigratesAndReencodesAtCurrentSchema()
    {
        var raw = new CelestialCompSaveRecord("counter", "sample.counter", 1, 1, IntBytes(3), new byte[] { 9 });
        var result = CelestialCompStateCodec.Decode(raw, Registry());
        Assert.True(result.Value.IsReady);
        Assert.Equal(9, Assert.IsType<CounterComp>(result.Value.Comp).Value);
        var encoded = CelestialCompStateCodec.Encode(result.Value.Comp!, result.Value.Properties!);
        Assert.Equal(2, encoded.SchemaVersion);
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(encoded.StatePayload.Span));
    }

    [Fact]
    public void UnsupportedStateVersionStaysBlockedAndKeepsOriginalPayload()
    {
        var raw = new CelestialCompSaveRecord("counter", "sample.counter", 1, 99, IntBytes(3), new byte[] { 5, 6 });
        var factory = new CounterFactory();
        var registry = new CelestialCompRegistry();
        registry.Register(new CelestialCompRegistration("sample.counter", 1, 2, factory, new CounterCodec()));
        var result = CelestialCompStateCodec.Decode(raw, registry);
        Assert.False(result.Value.IsReady);
        Assert.Equal("UnsupportedStateVersion", Assert.Single(result.Value.Errors).Code);
        Assert.Equal(new byte[] { 5, 6 }, result.Value.OriginalRecord.StatePayload.ToArray());
        Assert.False(factory.LastCreated!.OwnedBuffer.CanRead);
    }

    [Fact]
    public void ExceptionDuringRestoreReleasesNewComponentAndRetainsSavedBytes()
    {
        var factory = new CounterFactory();
        var registry = new CelestialCompRegistry();
        registry.Register(new CelestialCompRegistration("sample.counter", 1, 2, factory, new ThrowingCodec()));
        var raw = new CelestialCompSaveRecord("counter", "sample.counter", 1, 2, IntBytes(3), IntBytes(9));
        var result = CelestialCompStateCodec.Decode(raw, registry);
        Assert.False(result.Value.IsReady);
        Assert.Equal("ComponentRestoreFailed", Assert.Single(result.Value.Errors).Code);
        Assert.Equal(IntBytes(9), result.Value.OriginalRecord.StatePayload.ToArray());
        Assert.False(factory.LastCreated!.OwnedBuffer.CanRead);
    }

    [Fact]
    public void FalseRestoreAcknowledgementCannotPublishDefaultState()
    {
        var factory = new CounterFactory();
        var registry = new CelestialCompRegistry();
        registry.Register(new CelestialCompRegistration("sample.counter", 1, 2, factory, new FalseCodec()));
        var raw = new CelestialCompSaveRecord("counter", "sample.counter", 1, 2, IntBytes(3), IntBytes(9));
        var result = CelestialCompStateCodec.Decode(raw, registry);
        Assert.False(result.Value.IsReady);
        Assert.Equal("HandlerContractViolation", Assert.Single(result.Value.Errors).Code);
        Assert.False(factory.LastCreated!.OwnedBuffer.CanRead);
    }

    [Fact]
    public void DuplicateHandlersAndLateRegistrationAreRejected()
    {
        var registry = Registry();
        Assert.Equal("DuplicateHandler", Assert.Single(registry.Register(Registration()).Errors).Code);
        registry.Seal();
        Assert.Equal("RegistrySealed", Assert.Single(registry.Register(Registration()).Errors).Code);
    }

    private static CelestialCompRegistry Registry()
    {
        var result = new CelestialCompRegistry();
        Assert.True(result.Register(Registration()).Success);
        return result;
    }

    private static CelestialCompRegistration Registration()
        => new("sample.counter", 1, 2, new CounterFactory(), new CounterCodec());

    private static byte[] IntBytes(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private sealed record CounterProperties(string Key, int Initial) : ICelestialResolvedProperties
    {
        public string HandlerId => "sample.counter";
        public int Version => 1;
        public ReadOnlyMemory<byte> Serialize() => IntBytes(Initial);
    }

    private sealed class CounterFactory : ICelestialCompFactory
    {
        public CounterComp? LastCreated { get; private set; }
        public CelestialBodyComp Create(ICelestialResolvedProperties properties)
        {
            LastCreated = new CounterComp((CounterProperties)properties);
            return LastCreated;
        }
    }

    private sealed class CounterComp : CelestialBodyComp
    {
        public int Value { get; set; }
        public MemoryStream OwnedBuffer { get; } = new();
        public CounterComp(CounterProperties properties)
            : base(new CelestialCompDescriptor(properties.Key, properties.HandlerId, 2, provides: new[] { "counter" }))
            => Value = properties.Initial;
        public override void Handle(CelestialEvent message)
        {
            if (message.Kind == "increment") Value++;
        }
        public override ReadOnlyMemory<byte> CaptureState() => IntBytes(Value);
        protected override void OnRelease() => OwnedBuffer.Dispose();
    }

    private class CounterCodec : ICelestialCompStateHandler
    {
        public CelestialResult<ICelestialResolvedProperties> DecodeProperties(string key, int version, ReadOnlyMemory<byte> payload)
            => version == 1 && payload.Length == 4
                ? CelestialResult.Ok<ICelestialResolvedProperties>(new CounterProperties(key, BinaryPrimitives.ReadInt32LittleEndian(payload.Span)))
                : CelestialResult.Reject<ICelestialResolvedProperties>(new CelestialError("UnsupportedConfigVersion"));

        public virtual CelestialResult<bool> RestoreState(CelestialBodyComp component, int version, ReadOnlyMemory<byte> payload)
        {
            var counter = (CounterComp)component;
            if (version == 1 && payload.Length == 1) counter.Value = payload.Span[0];
            else if (version == 2 && payload.Length == 4) counter.Value = BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
            else return CelestialResult.Reject<bool>(new CelestialError("UnsupportedStateVersion"));
            return CelestialResult.Ok(true);
        }
    }

    private sealed class ThrowingCodec : CounterCodec
    {
        public override CelestialResult<bool> RestoreState(CelestialBodyComp component, int version, ReadOnlyMemory<byte> payload)
            => throw new InvalidDataException("Extension failed to read its payload.");
    }

    private sealed class FalseCodec : CounterCodec
    {
        public override CelestialResult<bool> RestoreState(CelestialBodyComp component, int version, ReadOnlyMemory<byte> payload)
            => CelestialResult.Ok(false);
    }
}

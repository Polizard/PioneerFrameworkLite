using System;

namespace PioneerFramework.Celestial
{
    /// <summary>配置与状态各自保存版本和原始字节；未知处理器的记录也可完整往返。</summary>
    public sealed class CelestialCompSaveRecord
    {
        private readonly byte[] configurationPayload;
        private readonly byte[] statePayload;
        public string Key { get; }
        public string HandlerId { get; }
        public int ConfigurationVersion { get; }
        public int SchemaVersion { get; }
        public ReadOnlyMemory<byte> ConfigurationPayload => configurationPayload;
        public ReadOnlyMemory<byte> StatePayload => statePayload;

        public CelestialCompSaveRecord(string key, string handlerId, int configurationVersion,
            int schemaVersion, ReadOnlySpan<byte> configurationPayload, ReadOnlySpan<byte> statePayload)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Missing component key.", nameof(key));
            if (string.IsNullOrWhiteSpace(handlerId)) throw new ArgumentException("Missing handler identity.", nameof(handlerId));
            ArgumentOutOfRangeException.ThrowIfLessThan(configurationVersion, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
            Key = key;
            HandlerId = handlerId;
            ConfigurationVersion = configurationVersion;
            SchemaVersion = schemaVersion;
            this.configurationPayload = configurationPayload.ToArray();
            this.statePayload = statePayload.ToArray();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed class PhysicsCheckpoint
    {
        private readonly byte[] payload;
        public string ProviderId { get; }
        public PhysicsContractVersion ContractVersion { get; }
        public int PayloadVersion { get; }
        public string CheckpointId { get; }
        public PhysicsSampleStamp Stamp { get; }
        public ReadOnlyMemory<byte> Payload => payload;
        public PhysicsCheckpoint(string providerId, PhysicsContractVersion contractVersion, int payloadVersion, string checkpointId,
            PhysicsSampleStamp stamp, ReadOnlySpan<byte> payload)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
            ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
            ArgumentOutOfRangeException.ThrowIfLessThan(payloadVersion, 1);
            ProviderId = providerId;
            ContractVersion = contractVersion;
            PayloadVersion = payloadVersion;
            CheckpointId = checkpointId;
            Stamp = stamp;
            this.payload = payload.ToArray();
        }
    }

    public sealed class PhysicsRestoreResult
    {
        public bool Success { get; }
        public PhysicsSampleStamp? Stamp { get; }
        public IReadOnlyList<CelestialError> Errors { get; }
        private PhysicsRestoreResult(bool success, PhysicsSampleStamp? stamp, IEnumerable<CelestialError> errors)
        {
            Success = success;
            Stamp = stamp;
            Errors = Array.AsReadOnly(errors.ToArray());
        }
        public static PhysicsRestoreResult Ok(PhysicsSampleStamp stamp) => new(true, stamp, Array.Empty<CelestialError>());
        public static PhysicsRestoreResult Reject(params CelestialError[] errors) => new(false, null, errors);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    public sealed record CelestialError(string Code, CelestialInstanceId? InstanceId = null,
        string? CompKey = null, string Detail = "")
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public CelestialRetryDisposition? RetryOverride { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public CelestialErrorCategory? CategoryOverride { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string LocalizationKey => Category switch {
            CelestialErrorCategory.Conflict => "PF_CelestialError_Conflict",
            CelestialErrorCategory.Availability => "PF_CelestialError_Availability",
            CelestialErrorCategory.Component => "PF_CelestialError_Component",
            CelestialErrorCategory.Consistency => "PF_CelestialError_Consistency",
            CelestialErrorCategory.Expired => "PF_CelestialError_Expired",
            _ => "PF_CelestialError_Validation" };
        [System.Text.Json.Serialization.JsonIgnore]
        public CelestialRetryDisposition RetryDisposition => RetryOverride ?? (Code switch {
            "ProviderUnavailable" or "RestorePending" or "ProviderRestoring" => CelestialRetryDisposition.WhenProviderReady,
            "AdvanceInProgress" or "RevisionConflict" or "PublicationConflict" or "HostWorkPending" or "EpochPendingLimitReached" or "EpochHasPendingOperations" => CelestialRetryDisposition.NextSafeBoundary,
            "EpochReceiptLimitReached" or "EpochLimitReached" => CelestialRetryDisposition.Manual,
            "ComponentAdvanceFailed" or "ComponentEventFailed" => CelestialRetryDisposition.Manual,
            _ => CelestialRetryDisposition.Never });
        [System.Text.Json.Serialization.JsonIgnore]
        public CelestialErrorCategory Category => CategoryOverride ?? (Code switch {
            "OperationExpired" or "SurfaceRetired" => CelestialErrorCategory.Expired,
            "RevisionConflict" or "OperationConflict" or "PublicationConflict" => CelestialErrorCategory.Conflict,
            "ProviderUnavailable" or "RestorePending" or "ProviderRestoring" => CelestialErrorCategory.Availability,
            "ConsistencyFault" or "ProviderFaulted" => CelestialErrorCategory.Consistency,
            _ when Code.StartsWith("Component", StringComparison.Ordinal) => CelestialErrorCategory.Component,
            _ => CelestialErrorCategory.Validation });
    }

    public sealed class CelestialResult<T>
    {
        private readonly T? value;
        public bool Success { get; }
        public IReadOnlyList<CelestialError> Errors { get; }
        public T Value => Success ? value! : throw new InvalidOperationException("The celestial operation was rejected.");

        internal CelestialResult(bool success, T? value, IReadOnlyList<CelestialError> errors)
        {
            Success = success;
            this.value = value;
            Errors = errors;
        }

    }

    public static class CelestialResult
    {
        public static CelestialResult<T> Ok<T>(T value)
            => new(true, value, Array.Empty<CelestialError>());

        public static CelestialResult<T> Reject<T>(params CelestialError[] errors)
        {
            if (errors == null || errors.Length == 0 || errors.Any(error => error == null || string.IsNullOrWhiteSpace(error.Code)))
                throw new ArgumentException("A rejection requires a structured error.", nameof(errors));
            return new(false, default, Array.AsReadOnly(errors.ToArray()));
        }
    }
}

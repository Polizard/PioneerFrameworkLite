using System;
using System.Collections.Generic;
using System.Linq;

namespace PioneerFramework.Celestial
{
    /// <summary>已解析的天体事实。发布后只读，显示尺度不参与物理半径赋值。</summary>
    public sealed class CelestialBodyDescription
    {
        public CelestialInstanceId Id { get; }
        public string DefinitionId { get; }
        public int Handle { get; }
        public long DescriptorRevision { get; }
        public string Category { get; }
        public double RadiusMeters { get; }
        public double? GravitationalParameter { get; }
        public IReadOnlyList<string> Tags { get; }

        public CelestialBodyDescription(CelestialInstanceId id, string definitionId, int handle,
            long descriptorRevision, string category, double radiusMeters,
            double? gravitationalParameter = null, IEnumerable<string>? tags = null)
        {
            if (!id.IsValid) throw new ArgumentException("Missing instance identity.", nameof(id));
            if (string.IsNullOrWhiteSpace(definitionId)) throw new ArgumentException("Missing definition identity.", nameof(definitionId));
            ArgumentOutOfRangeException.ThrowIfNegative(handle);
            ArgumentOutOfRangeException.ThrowIfLessThan(descriptorRevision, 1);
            if (!double.IsFinite(radiusMeters) || radiusMeters <= 0) throw new ArgumentOutOfRangeException(nameof(radiusMeters));
            if (gravitationalParameter.HasValue && (!double.IsFinite(gravitationalParameter.Value) || gravitationalParameter.Value < 0))
                throw new ArgumentOutOfRangeException(nameof(gravitationalParameter));
            Id = id;
            DefinitionId = definitionId;
            Handle = handle;
            DescriptorRevision = descriptorRevision;
            Category = category ?? string.Empty;
            RadiusMeters = radiusMeters;
            GravitationalParameter = gravitationalParameter;
            Tags = Array.AsReadOnly((tags ?? Array.Empty<string>()).ToArray());
            if (Tags.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Tags must be nonempty.", nameof(tags));
        }
    }
}

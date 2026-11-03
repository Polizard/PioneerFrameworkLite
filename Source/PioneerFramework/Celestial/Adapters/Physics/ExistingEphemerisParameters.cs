using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Celestial
{
    /// <summary>既有解析模型 v1 的冻结初始化数据。类别仅作为显示元数据，轨道由显式半径决定。</summary>
    public sealed partial class ExistingEphemerisParameters
    {
        public double OrbitRadius { get; }
        public double OrbitPeriod { get; }
        public double InitialPhase { get; }
        public double DayLengthTicks { get; }
        public DVector3 OrbitNormal { get; }
        public string BodyType { get; }
        public string FrameOriginReference { get; }
        public bool HasOrbit => OrbitRadius > 0;
        private readonly DVector3 refX;
        private readonly DVector3 refY;
        private ExistingEphemerisParameters(Data data)
        {
            OrbitRadius = data.orbitRadius; OrbitPeriod = data.orbitPeriod; InitialPhase = data.initialPhase;
            DayLengthTicks = data.dayLengthTicks; BodyType = data.bodyType; FrameOriginReference = data.frameOriginReference;
            OrbitNormal = new(data.orbitNormal[0], data.orbitNormal[1], data.orbitNormal[2]);
            // 原 CelestialBodyDef.EnsureOrbitFrame 的运算和次序。
            var normal = OrbitNormal;
            if (normal.sqrMagnitude > 0.0) normal = normal.normalized;
            var up = Math.Abs(DVector3.Dot(normal, DVector3.up)) > 0.99 ? DVector3.forward : DVector3.up;
            refX = DVector3.Cross(normal, up).normalized;
            refY = DVector3.Cross(normal, refX).normalized;
        }
        public void GetOrbitFrame(out DVector3 x, out DVector3 y) { x = refX; y = refY; }
        public (DVector3 Position, DVector3 Velocity) SampleParent(double seconds)
        {
            if (!HasOrbit) return (DVector3.zero, DVector3.zero);
            // 原 CelestialBodyManager 的父系位置、速度核心；保持乘加顺序。
            double omega = 2.0 * Math.PI / OrbitPeriod;
            double phase = InitialPhase + omega * seconds;
            double cos = Math.Cos(phase), sin = Math.Sin(phase);
            return (OrbitRadius * (cos * refX + sin * refY), (OrbitRadius * omega) * (-sin * refX + cos * refY));
        }
        public static CelestialResult<ExistingEphemerisParameters> Parse(PhysicsInitialization initialization)
        {
            if (initialization.ModeId == "existing.circular.v2") return ParseV2(initialization);
            if (initialization.ProviderId != ExistingCelestialEphemerisBridge.Id || initialization.ModeId != "existing.circular.v1" || initialization.Version != 1)
                return CelestialResult.Reject<ExistingEphemerisParameters>(new CelestialError("UnsupportedInitialization"));
            try
            {
                var data = JsonSerializer.Deserialize<Data>(initialization.Payload.Span, JsonOptions)!;
                if (data == null || !double.IsFinite(data.orbitRadius) || data.orbitRadius < 0 || !double.IsFinite(data.orbitPeriod) ||
                    data.orbitPeriod < 0 || data.orbitRadius > 0 && data.orbitPeriod <= 0 || !double.IsFinite(data.initialPhase) ||
                    !double.IsFinite(data.dayLengthTicks) || data.dayLengthTicks <= 0 || data.orbitNormal == null || data.orbitNormal.Length != 3 ||
                    data.bodyType == null || data.frameOriginReference == null)
                    throw new ArgumentException("Invalid existing ephemeris parameters.");
                var normal = new DVector3(data.orbitNormal[0], data.orbitNormal[1], data.orbitNormal[2]);
                if (!normal.IsFinite() || !double.IsFinite(normal.sqrMagnitude) || normal.sqrMagnitude <= 0)
                    throw new ArgumentException("Invalid orbital normal.");
                return CelestialResult.Ok(new ExistingEphemerisParameters(data));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { return CelestialResult.Reject<ExistingEphemerisParameters>(new CelestialError("InvalidInitialization", Detail: exception.Message)); }
        }
        private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        private sealed class Data
        {
            public double orbitRadius { get; set; } = double.NaN;
            public double orbitPeriod { get; set; } = double.NaN;
            public double initialPhase { get; set; } = double.NaN;
            public double[] orbitNormal { get; set; } = Array.Empty<double>();
            public double dayLengthTicks { get; set; } = double.NaN;
            public string bodyType { get; set; } = "Unclassified";
            public string frameOriginReference { get; set; } = "";
        }
    }
}

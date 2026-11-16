using System;
using System.Text.Json;

namespace PioneerFramework.Celestial
{
    public sealed partial class ExistingEphemerisParameters
    {
        internal const string EllipticalMode = "existing.elliptical.v1";
        private EllipticalOrbit? ellipse;
        internal bool HasEllipticalElements => ellipse != null;
        public double Eccentricity => ellipse?.Eccentricity ?? 0;
        public double PeriapsisRadius => OrbitRadius * (1 - Eccentricity);
        public double ApoapsisRadius => OrbitRadius * (1 + Eccentricity);

        internal static PhysicsInitialization CreateEllipticalInitialization(CelestialMotionXml motion, double eccentricity,
            double epochSeconds, DVector3 periapsis, bool orbitRetrograde)
            => new(ExistingCelestialEphemerisBridge.Id, EllipticalMode, 1,
                JsonSerializer.SerializeToUtf8Bytes(new EllipticalData(ToMotionData(motion), eccentricity, epochSeconds,
                    new[] { periapsis.x, periapsis.y, periapsis.z }, orbitRetrograde)));

        private static CelestialResult<ExistingEphemerisParameters> ParseEllipse(PhysicsInitialization initialization)
        {
            if (initialization.ProviderId != ExistingCelestialEphemerisBridge.Id || initialization.Version != 1)
                return CelestialResult.Reject<ExistingEphemerisParameters>(new CelestialError("UnsupportedInitialization"));
            try
            {
                CelestialJsonFields.RejectDuplicates(initialization.Payload.Span);
                var data = JsonSerializer.Deserialize<EllipticalData>(initialization.Payload.Span, MotionOptions)
                    ?? throw new ArgumentException("Missing elliptical orbit.");
                if (data.Motion == null || data.Periapsis == null || data.Periapsis.Length != 3 ||
                    !double.IsFinite(data.Eccentricity) || data.Eccentricity < 0 || data.Eccentricity >= 1 ||
                    !double.IsFinite(data.EpochSeconds) || data.EpochSeconds < 0)
                    throw new ArgumentException("Invalid elliptical elements.");

                var parameters = FromMotionData(data.Motion);
                var normal = parameters.OrbitNormal.normalized;
                var axis = new DVector3(data.Periapsis[0], data.Periapsis[1], data.Periapsis[2]);
                var unit = axis.normalized;
                double alongNormal = DVector3.Dot(normal, unit);
                if (!parameters.HasOrbit || !axis.IsFinite() || unit.sqrMagnitude <= 0 ||
                    Math.Abs(alongNormal) > 1e-10 || !double.IsFinite(parameters.OrbitRadius * (1 + data.Eccentricity)))
                    throw new ArgumentException("Invalid periapsis frame.");
                unit = (unit - normal * alongNormal).normalized;
                parameters.ellipse = new(data.Eccentricity, data.EpochSeconds, unit, DVector3.Cross(normal, unit).normalized,
                    data.OrbitRetrograde);
                return CelestialResult.Ok(parameters);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                return CelestialResult.Reject<ExistingEphemerisParameters>(new CelestialError("InvalidInitialization", Detail: exception.Message));
            }
        }

        internal DVector3 OffsetAtEccentricAnomaly(double angle)
        {
            GetOrbitFrame(out var x, out var y);
            double e = Eccentricity;
            return OrbitRadius * ((Math.Cos(angle) - e) * x + Math.Sqrt((1 - e) * (1 + e)) * Math.Sin(angle) * y);
        }

        private (DVector3 Position, DVector3 Velocity) SampleEllipse(EllipticalOrbit orbit, double seconds)
        {
            double rate = (orbit.Retrograde ? -2 : 2) * Math.PI / OrbitPeriod;
            double elapsed = Math.IEEERemainder(seconds - orbit.EpochSeconds, OrbitPeriod);
            double mean = Math.IEEERemainder(Math.IEEERemainder(InitialPhase, 2 * Math.PI) + rate * elapsed, 2 * Math.PI);
            double anomaly = SolveEccentricAnomaly(mean, orbit.Eccentricity);
            var (sine, cosine) = Math.SinCos(anomaly);
            double e = orbit.Eccentricity;
            double transverse = Math.Sqrt((1 - e) * (1 + e));
            double anomalyRate = rate / (1 - e * cosine);
            return (OrbitRadius * ((cosine - e) * orbit.Periapsis + transverse * sine * orbit.Transverse),
                (OrbitRadius * anomalyRate) * (-sine * orbit.Periapsis + transverse * cosine * orbit.Transverse));
        }

        private static double SolveEccentricAnomaly(double mean, double eccentricity)
        {
            double lower = -Math.PI, upper = Math.PI, current = mean;
            for (int i = 0; i < 64; i++)
            {
                var (sine, cosine) = Math.SinCos(current);
                double residual = current - eccentricity * sine - mean;
                if (residual == 0) return current;
                if (residual > 0) upper = current;
                else lower = current;
                double next = current - residual / (1 - eccentricity * cosine);
                if (next == current) return current;
                current = next > lower && next < upper ? next : (lower + upper) * .5;
            }
            return current;
        }

        private sealed record EllipticalOrbit(double Eccentricity, double EpochSeconds, DVector3 Periapsis, DVector3 Transverse, bool Retrograde);
        private sealed record EllipticalData(MotionData Motion, double Eccentricity, double EpochSeconds, double[] Periapsis, bool OrbitRetrograde);
    }
}

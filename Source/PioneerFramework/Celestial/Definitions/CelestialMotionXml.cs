using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace PioneerFramework.Celestial
{
    /// <summary>Readable opt-in initialization for the existing provider. Values use meters, seconds and radians.</summary>
    public sealed class CelestialMotionXml
    {
        public double orbitRadiusMeters;
        public double orbitPeriodSeconds;
        public double orbitPhaseRadians;
        public double orbitNormalX;
        public double orbitNormalY = 1;
        public double orbitNormalZ;
        public string bodyType = "Unclassified";
        public string frameOriginReference = "";
        public CelestialRotationXml rotation = new();
        public List<CelestialEnvironmentParameterXml> parameters = new();
        private readonly List<string> xmlErrors = new();
        public void LoadDataFromXmlCustom(XmlNode node) => CelestialMotionXmlFields.Read(this, node, xmlErrors);
        private IEnumerable<string> ScalarErrors()
        {
            foreach (string error in xmlErrors) yield return error;
            if (new[] { orbitRadiusMeters, orbitPeriodSeconds, orbitPhaseRadians }.Any(value => !double.IsFinite(value)) ||
                orbitRadiusMeters < 0 || orbitPeriodSeconds < 0 || orbitRadiusMeters > 0 && orbitPeriodSeconds <= 0)
                yield return "InvalidMotionOrbit";
            if (!CelestialMotionXmlFields.ValidAxis(orbitNormalX, orbitNormalY, orbitNormalZ)) yield return "InvalidMotionOrbitNormal";
            if (bodyType == null || frameOriginReference == null) yield return "InvalidMotionMetadata";
            if (rotation == null) yield return "MissingRotation";
            else foreach (string error in rotation.ConfigErrors()) yield return error;
        }
        public PhysicsInitialization Compile()
            => Compile(null);
        public IEnumerable<string> ConfigErrors()
        {
            string? error = null;
            try { _ = Compile(null); } catch (ArgumentException exception) { error = exception.Message; }
            if (error != null) yield return error;
        }
        /// <summary>Resolve once under the owning physics component. Numeric override keys start with motion.</summary>
        public PhysicsInitialization Compile(CelestialGenerationContext? context)
        {
            _ = CompileCore(context, false);
            return CompileCore(context, true);
        }
        private PhysicsInitialization CompileCore(CelestialGenerationContext? context, bool sample)
        {
            if (xmlErrors.Count != 0 || rotation == null || rotation.HasXmlErrors) throw new ArgumentException("InvalidMotionXml");
            var s = new CelestialPlanetParameterSampler(parameters, context, "motion.", sample);
            var value = new CelestialMotionXml {
                orbitRadiusMeters = s.Get(nameof(orbitRadiusMeters), orbitRadiusMeters, 0, double.MaxValue),
                orbitPeriodSeconds = s.Get(nameof(orbitPeriodSeconds), orbitPeriodSeconds, 0, double.MaxValue),
                orbitPhaseRadians = s.Get(nameof(orbitPhaseRadians), orbitPhaseRadians, double.MinValue, double.MaxValue),
                orbitNormalX = s.Get(nameof(orbitNormalX), orbitNormalX, double.MinValue, double.MaxValue),
                orbitNormalY = s.Get(nameof(orbitNormalY), orbitNormalY, double.MinValue, double.MaxValue),
                orbitNormalZ = s.Get(nameof(orbitNormalZ), orbitNormalZ, double.MinValue, double.MaxValue),
                bodyType = bodyType, frameOriginReference = frameOriginReference,
                rotation = new() {
                    axisX = s.Get("rotation.axisX", rotation.axisX, double.MinValue, double.MaxValue),
                    axisY = s.Get("rotation.axisY", rotation.axisY, double.MinValue, double.MaxValue),
                    axisZ = s.Get("rotation.axisZ", rotation.axisZ, double.MinValue, double.MaxValue),
                    phaseRadians = s.Get("rotation.phaseRadians", rotation.phaseRadians, double.MinValue, double.MaxValue),
                    periodSeconds = string.IsNullOrEmpty(rotation.synchronousTargetReference) ?
                        s.Get("rotation.periodSeconds", rotation.periodSeconds ?? 0, double.Epsilon, double.MaxValue) : rotation.periodSeconds,
                    retrograde = rotation.retrograde, synchronousTargetReference = rotation.synchronousTargetReference }
            };
            void Axis(params string[] keys)
            {
                double closest = keys.Sum(key => { var p = s.Interval(key); double v = p.Minimum > 0 ? p.Minimum : p.Maximum < 0 ? p.Maximum : 0; return v * v; });
                double farthest = keys.Sum(key => { var p = s.Interval(key); double v = Math.Max(Math.Abs(p.Minimum), Math.Abs(p.Maximum)); return v * v; });
                if (!(closest > 0) || !double.IsFinite(farthest)) throw new ArgumentException("Motion axis interval contains zero vector");
            }
            Axis(nameof(orbitNormalX), nameof(orbitNormalY), nameof(orbitNormalZ));
            Axis("rotation.axisX", "rotation.axisY", "rotation.axisZ");
            if (s.Interval(nameof(orbitRadiusMeters)).Maximum > 0 && s.Interval(nameof(orbitPeriodSeconds)).Minimum <= 0)
                throw new ArgumentException("Motion orbit interval requires positive period");
            s.Complete();
            var errors = value.ScalarErrors().ToArray();
            if (errors.Length != 0) throw new ArgumentException(string.Join(",", errors));
            return ExistingEphemerisParameters.CreateInitialization(value);
        }
    }
    public sealed class CelestialRotationXml
    {
        internal bool HasXmlErrors => xmlErrors.Count != 0;
        public double axisX;
        public double axisY = 1;
        public double axisZ;
        public double? periodSeconds;
        public double phaseRadians;
        public bool retrograde;
        public string synchronousTargetReference = "";
        private readonly List<string> xmlErrors = new();
        public void LoadDataFromXmlCustom(XmlNode node) => CelestialMotionXmlFields.Read(this, node, xmlErrors);
        public IEnumerable<string> ConfigErrors()
        {
            foreach (string error in xmlErrors) yield return error;
            if (!CelestialMotionXmlFields.ValidAxis(axisX, axisY, axisZ) || !double.IsFinite(phaseRadians) || synchronousTargetReference == null)
                yield return "InvalidRotation";
            if (string.IsNullOrEmpty(synchronousTargetReference))
            { if (!periodSeconds.HasValue || !double.IsFinite(periodSeconds.Value) || periodSeconds <= 0) yield return "InvalidRotationPeriod"; }
            else if (string.IsNullOrWhiteSpace(synchronousTargetReference) || periodSeconds.HasValue || retrograde)
                yield return "ConflictingSynchronousRotation";
        }
    }
    internal static class CelestialMotionXmlFields
    {
        internal static bool ValidAxis(double x, double y, double z)
        { var value = new DVector3(x, y, z); return value.IsFinite() && double.IsFinite(value.sqrMagnitude) && value.sqrMagnitude > 0; }
        internal static void Read(object owner, XmlNode node, List<string> errors)
        {
            errors.Clear(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                var field = owner.GetType().GetField(child.Name);
                if (field == null || !seen.Add(child.Name)) { errors.Add("UnknownOrDuplicateMotionField:" + child.Name); continue; }
                object value;
                if (field.FieldType == typeof(List<CelestialEnvironmentParameterXml>))
                    value = Verse.DirectXmlToObject.ObjectFromXml<List<CelestialEnvironmentParameterXml>>(child, false);
                else if (field.FieldType == typeof(CelestialRotationXml)) { var rotation = new CelestialRotationXml(); rotation.LoadDataFromXmlCustom(child); value = rotation; }
                else if (child.ChildNodes.Cast<XmlNode>().Any(item => item.NodeType == XmlNodeType.Element)) { errors.Add("InvalidMotionField:" + child.Name); continue; }
                else if (field.FieldType == typeof(string)) value = child.InnerText;
                else if (field.FieldType == typeof(bool) && bool.TryParse(child.InnerText, out bool flag)) value = flag;
                else if ((field.FieldType == typeof(double) || field.FieldType == typeof(double?)) &&
                    double.TryParse(child.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) value = number;
                else { errors.Add("InvalidMotionField:" + child.Name); continue; }
                field.SetValue(owner, value);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace PioneerFramework.Celestial
{
    public abstract class CelestialNumericParameterXml<T> where T : struct
    {
        public T? value;
        public T? min;
        public T? max;
        private readonly List<CelestialError> parseErrors = new();

        public void LoadDataFromXmlCustom(XmlNode root)
        {
            value = min = max = null;
            parseErrors.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode child in root.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                if (child.Name != "value" && child.Name != "min" && child.Name != "max")
                {
                    parseErrors.Add(new CelestialError("UnknownParameterField", Detail: child.Name));
                    continue;
                }
                if (!seen.Add(child.Name))
                {
                    parseErrors.Add(new CelestialError("DuplicateParameterField", Detail: child.Name));
                    continue;
                }
                if (!TryParse(child.InnerText, out T parsed))
                {
                    parseErrors.Add(new CelestialError("InvalidParameterNumber", Detail: child.Name));
                    continue;
                }
                if (child.Name == "value") value = parsed;
                else if (child.Name == "min") min = parsed;
                else max = parsed;
            }
        }

        protected abstract bool TryParse(string text, out T result);

        protected CelestialError[] FormErrors()
        {
            if (parseErrors.Count > 0) return parseErrors.ToArray();
            if (value.HasValue && (min.HasValue || max.HasValue)) return new[] { new CelestialError("ParameterFormConflict") };
            if (!value.HasValue && (!min.HasValue || !max.HasValue)) return new[] { new CelestialError("ParameterFormMissing") };
            return Array.Empty<CelestialError>();
        }
    }

    public sealed class CelestialDoubleParameterXml : CelestialNumericParameterXml<double>
    {
        protected override bool TryParse(string text, out double result)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        public CelestialResult<DoubleParameterSpec> ToSpec()
        {
            var errors = FormErrors();
            if (errors.Length > 0) return CelestialResult.Reject<DoubleParameterSpec>(errors);
            try { return CelestialResult.Ok(value.HasValue ? DoubleParameterSpec.Fixed(value.Value) : DoubleParameterSpec.Uniform(min!.Value, max!.Value)); }
            catch (ArgumentOutOfRangeException exception)
            {
                return CelestialResult.Reject<DoubleParameterSpec>(new CelestialError("InvalidParameterRange", Detail: exception.Message));
            }
        }
    }

    public sealed class CelestialIntParameterXml : CelestialNumericParameterXml<int>
    {
        protected override bool TryParse(string text, out int result)
            => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        public CelestialResult<IntParameterSpec> ToSpec()
        {
            var errors = FormErrors();
            if (errors.Length > 0) return CelestialResult.Reject<IntParameterSpec>(errors);
            try { return CelestialResult.Ok(value.HasValue ? IntParameterSpec.Fixed(value.Value) : IntParameterSpec.Uniform(min!.Value, max!.Value)); }
            catch (ArgumentOutOfRangeException exception)
            {
                return CelestialResult.Reject<IntParameterSpec>(new CelestialError("InvalidParameterRange", Detail: exception.Message));
            }
        }
    }
}

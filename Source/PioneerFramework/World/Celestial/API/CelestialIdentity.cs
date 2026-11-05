using System;

namespace PioneerFramework.Celestial
{
    /// <summary>存档内稳定身份；模板名称与实际天体身份分别管理。</summary>
    public readonly record struct CelestialInstanceId
    {
        public string Value { get; }
        public bool IsValid => !string.IsNullOrWhiteSpace(Value);

        private CelestialInstanceId(string value) => Value = value;

        public static CelestialInstanceId Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("A stable celestial identity must be nonempty and have no surrounding whitespace.", nameof(value));
            return new CelestialInstanceId(value);
        }

        public override string ToString() => Value ?? string.Empty;
    }
}

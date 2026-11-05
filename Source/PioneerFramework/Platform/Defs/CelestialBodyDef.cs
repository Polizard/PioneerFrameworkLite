using System.Collections.Generic;
using System;
using System.Linq;
using PioneerFramework.Celestial;
using Verse;

namespace PioneerFramework
{
    public enum CelestialBodyType
    {
        Star,
        Planet,
        Moon
    }

    /// <summary>
    /// 天体定义。通过 XML 注册，由 DefDatabase 管理。
    /// 物理量使用 double 以兼容高精度计算。
    /// </summary>
    public class CelestialBodyDef : Def
    {
        public string category = "Unclassified";
        public List<string> tags = new();
        public List<CelestialCompProperties> comps = new();

        public CelestialResult<CelestialBodyTemplate> ToTemplate()
        {
            if (comps == null || tags == null)
                return CelestialResult.Reject<CelestialBodyTemplate>(new CelestialError("InvalidDefinitionList"));
            var errors = new List<CelestialError>();
            var definitions = new List<CelestialCompDefinition>();
            foreach (var properties in comps)
            {
                if (properties == null)
                {
                    errors.Add(new CelestialError("InvalidComponent"));
                    continue;
                }
                try
                {
                    string[] configErrors = properties.ConfigErrors().ToArray();
                    if (configErrors.Length > 0)
                        errors.AddRange(configErrors.Select(error => new CelestialError("InvalidCompConfiguration", CompKey: properties.key, Detail: error)));
                    else definitions.Add(properties.ToDefinition());
                }
                catch (Exception exception)
                {
                    errors.Add(new CelestialError("ComponentDefinitionFailed", CompKey: properties.key, Detail: exception.Message));
                }
            }
            return errors.Count > 0
                ? CelestialResult.Reject<CelestialBodyTemplate>(errors.ToArray())
                : CelestialBodyTemplate.Create(defName, label ?? defName, category, tags, definitions);
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) yield return error;
            foreach (var error in ToTemplate().Errors) yield return error.Code + ": " + error.CompKey + " " + error.Detail;
        }

    }
}

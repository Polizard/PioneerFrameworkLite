using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using PioneerFramework.MapGeneration;
using RimWorld;
using Verse;

namespace PioneerFramework.Tests;

// Load the shipped recipe XML using the native inheritance and cross-reference machinery.
// Minimal headless Defs stand in for terrain and biome assets which are loaded before PFL in game.
internal sealed class ShippedLocalMapRecipeDefs : IDisposable
{
    private readonly List<Action> restore = new();
    internal IReadOnlyDictionary<string, LocalMapRecipeDef> Recipes { get; }

    internal ShippedLocalMapRecipeDefs()
    {
        try
        {
            var xml = new XmlDocument();
            xml.Load(CelestialDefaultContentTests.DataPath("CelestialFramework/LocalMapRecipes.xml"));
            var elements = xml.DocumentElement.ChildNodes.OfType<XmlElement>().ToArray();
            foreach (var element in elements) XmlInheritance.TryRegister(element, null);
            XmlInheritance.Resolve();
            try
            {
                var terrainNames = xml.SelectNodes("//terrains/li | //target").Cast<XmlNode>().Select(n => n.InnerText).Distinct();
                foreach (string name in terrainNames)
                {
                    var def = (TerrainDef)RuntimeHelpers.GetUninitializedObject(typeof(TerrainDef));
                    def.defName = name;
                    Add(def);
                }
                foreach (string name in xml.SelectNodes("//biome | //rockBiomes/li").Cast<XmlNode>().Select(n => n.InnerText).Distinct())
                    Add(new BiomeDef { defName = name });
                Recipes = elements.Where(element => element["defName"] != null).Select(element =>
                    DirectXmlToObject.ObjectFromXml<LocalMapRecipeDef>(element, false)).ToDictionary(recipe => recipe.defName);
                DirectXmlCrossRefLoader.ResolveAllWantedCrossReferences(FailMode.LogErrors);
            }
            finally { XmlInheritance.Clear(); }
        }
        catch { Dispose(); throw; }
    }

    private void Add<T>(T definition) where T : Def, new()
    {
        if (DefDatabase<T>.GetNamedSilentFail(definition.defName) != null) return;
        var before = DefDatabase<T>.AllDefsListForReading.ToArray();
        DefDatabase<T>.Add(definition);
        restore.Add(() => {
            DefDatabase<T>.Clear();
            foreach (var def in before) DefDatabase<T>.Add(def);
        });
    }

    public void Dispose()
    {
        for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
        restore.Clear();
    }
}

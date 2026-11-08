using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

public enum VisualMountSurface { Top, Bottom, Explicit }

public sealed class ShipBuildingVisualDef : Def
{
    public int formatVersion = 1;
    public List<VisualNodeXml> nodes = new();
    public float detailedPixels = 8;
    public float hiddenPixels = 2;
    public bool factionTint;

    public IEnumerable<string> VisualErrors()
    {
        if (formatVersion != 1) yield return "Unsupported visual formatVersion.";
        if (!float.IsFinite(detailedPixels) || !float.IsFinite(hiddenPixels) || hiddenPixels <= 0 || detailedPixels <= hiddenPixels)
            yield return "LOD requires finite detailedPixels > hiddenPixels > 0.";
        string? structuralError = null;
        try { CompileStructure(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { structuralError = e.Message; }
        if (structuralError != null) yield return structuralError;
        if (nodes == null) yield break;
        foreach (var node in nodes)
        {
            if (node == null) { yield return "Null node."; continue; }
            if (!ValidColor(node.color)) yield return "Invalid node color: " + node.id;
            if (string.IsNullOrWhiteSpace(node.meshProvider)) yield return "Empty mesh provider: " + node.id;
        }
    }

    internal CompiledVisualProfile CompileStructure()
    {
        if (nodes == null) throw new ArgumentException("Missing nodes.");
        var core = new VisualNode[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) core[i] = nodes[i]?.ToCore() ?? throw new ArgumentException("Null node.");
        return CompiledVisualProfile.Compile(core);
    }

    public override IEnumerable<string> ConfigErrors()
    {
        foreach (var error in base.ConfigErrors()) yield return error;
        foreach (var error in VisualErrors()) yield return error;
    }

    internal static bool ValidColor(Color c) => float.IsFinite(c.r) && float.IsFinite(c.g) && float.IsFinite(c.b)
        && float.IsFinite(c.a) && c.r is >= 0 and <= 1 && c.g is >= 0 and <= 1 && c.b is >= 0 and <= 1 && c.a is >= 0 and <= 1;
}

public sealed class VisualNodeXml
{
    public string id = "";
    public string parentId = "";
    public string mesh = "box";
    public string meshProvider = "PFL.Primitives.v1";
    public Vector3 position;
    public Vector3 rotation;
    public Vector3 scale = Vector3.one;
    public Vector3 pivot;
    public Color color = new(.65f, .7f, .75f);
    public bool bindYaw;
    public bool bindPitch;
    public bool bindDeploy;
    public bool bindScan;
    public bool activeOnly;
    public bool detailedOnly;

    internal VisualNode ToCore()
    {
        var r = System.Numerics.Quaternion.CreateFromYawPitchRoll(rotation.y * VisualMath.DegToRad,
            rotation.x * VisualMath.DegToRad, rotation.z * VisualMath.DegToRad);
        return new(id) { ParentId = parentId, Position = VisualConvert.N(position), Rotation = r,
            Scale = VisualConvert.N(scale), Pivot = VisualConvert.N(pivot), BindYaw = bindYaw,
            BindPitch = bindPitch, BindDeploy = bindDeploy, BindScan = bindScan, ActiveOnly = activeOnly };
    }
}

public sealed class ShipBuildingVisualExtension : DefModExtension
{
    public List<BuildingVisualMount> mounts = new();
}

public sealed class BuildingVisualMount
{
    public string id = "main";
    public ShipBuildingVisualDef visual = null!;
    public string stateProvider = "PFL.Static.v1";
    public VisualMountSurface surface;
    public Vector3 offset;
    public Vector3 explicitPositionShip;
    public Vector3 rotation;
    public bool followBuildingRotation = true;
    public float surfaceOffset = .03f;

    public bool IsValid => !string.IsNullOrWhiteSpace(id) && visual != null && !string.IsNullOrWhiteSpace(stateProvider)
        && Enum.IsDefined(surface) && VisualMath.Finite(VisualConvert.N(offset)) && VisualMath.Finite(VisualConvert.N(explicitPositionShip))
        && VisualMath.Finite(VisualConvert.N(rotation)) && float.IsFinite(surfaceOffset) && surfaceOffset >= 0;
}

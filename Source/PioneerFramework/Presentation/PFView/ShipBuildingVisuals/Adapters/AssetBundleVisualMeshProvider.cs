using System;
using System.Globalization;
using UnityEngine;
using Verse;

namespace PioneerFramework.PFView.ShipBuildingVisuals;

public readonly record struct VisualBundleReference(string PackageId, string AssetName, int Submesh)
{
    public static VisualBundleReference Parse(string text)
    {
        int separator = text?.IndexOf("::", StringComparison.Ordinal) ?? -1;
        if (separator <= 0) throw new ArgumentException("Expected packageId::assets/path#submesh.", nameof(text));
        string package = text![..separator];
        string asset = text[(separator + 2)..];
        int submesh = 0;
        int hash = asset.LastIndexOf('#');
        if (hash >= 0)
        {
            if (!int.TryParse(asset.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out submesh) || submesh < 0)
                throw new ArgumentException("Invalid submesh.", nameof(text));
            asset = asset[..hash];
        }
        if (!package.Contains('.') || package.Contains(':') || package.Contains('/') || package.Contains('\\') || package.Contains(' ')
            || !asset.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || asset.Contains("..", StringComparison.Ordinal)
            || asset.Contains('\\') || asset.Contains(':') || asset.Length <= 7)
            throw new ArgumentException("Invalid package or asset identity.", nameof(text));
        return new(package, asset, submesh);
    }
}

/// <summary>
/// Reads only bundles already owned by RimWorld. It never instantiates prefabs or unloads a mod's assets.
/// Current UnityStubs supports uncompressed, non-streamed triangle meshes; each node selects one submesh.
/// </summary>
public sealed class AssetBundleVisualMeshProvider : IShipBuildingVisualMeshProvider
{
    public VisualMeshData GetMesh(string name)
    {
        var reference = VisualBundleReference.Parse(name);
        Mesh? found = null;
        foreach (var mod in LoadedModManager.RunningModsListForReading)
        {
            if (!string.Equals(mod.PackageId, reference.PackageId, StringComparison.OrdinalIgnoreCase)) continue;
            if (mod.assetBundles?.loadedAssetBundles == null) break;
            foreach (var bundle in mod.assetBundles.loadedAssetBundles)
            {
                if (bundle == null || !bundle.Contains(reference.AssetName)) continue;
                var mesh = bundle.LoadAsset<Mesh>(reference.AssetName);
                if (mesh == null) continue;
                if (found != null && !ReferenceEquals(found, mesh)) throw new ArgumentException("Ambiguous mesh asset: " + name, nameof(name));
                found = mesh;
            }
        }
        if (found == null) throw new ArgumentException("Mesh asset unavailable: " + name, nameof(name));
        if (reference.Submesh >= found.subMeshCount) throw new ArgumentException("Mesh submesh unavailable: " + name, nameof(name));
        var positions = found.vertices; var normals = found.normals; var uvs = found.uv;
        var p = new System.Numerics.Vector3[positions.Length];
        var n = new System.Numerics.Vector3[normals.Length];
        var uv = new System.Numerics.Vector2[uvs.Length];
        for (int i = 0; i < p.Length; i++) p[i] = VisualConvert.N(positions[i]);
        for (int i = 0; i < n.Length; i++) n[i] = VisualConvert.N(normals[i]);
        for (int i = 0; i < uv.Length; i++) uv[i] = new(uvs[i].x, uvs[i].y);
        return new(p, n, uv, found.GetIndices(reference.Submesh));
    }
}

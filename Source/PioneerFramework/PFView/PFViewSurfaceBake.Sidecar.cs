using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PioneerFramework;

internal static partial class PFViewSurfaceBake
{
    private const long TextureBudgetBytes = 96L * 1024 * 1024;
    private static readonly long IdleLifetime = 30L * Stopwatch.Frequency;
    private static readonly Dictionary<int, SidecarRequest> pendingSidecars = new();
    private static (SidecarRequest Request, Task<SidecarPixels?> Work)? decoding;
    private static long nextTrim;

    private sealed record SidecarRequest(int Handle, PlanetLayer? Layer, string Path);
    private sealed record SidecarPixels(int Size, object? Compression, byte[][] Levels, bool Opaque);

    internal static bool IsSidecarPending(int handle) => pendingSidecars.ContainsKey(handle);

    internal static void Update(bool renderWorldPreview = false)
    {
        EnsureContext();
        PumpDiskSweep();   // 只消费“已完成的清理结果”并打一行日志；不会在这里发起扫描（测试/每帧都不碰盘）
        if (decoding is { } active && active.Work.IsCompleted)
        {
            decoding = null;
            CompleteSidecar(active.Request, active.Work);
        }
        if (decoding == null && pendingSidecars.Count > 0)
        {
            var request = pendingSidecars.Values.First();
            string path = request.Path;
            decoding = (request, Task.Run(() => DecodeSidecar(path)));
        }
        if (renderWorldPreview) PumpWorldPreview();

        long now = Stopwatch.GetTimestamp();
        if (baked.Count > 0 && now >= nextTrim)
        {
            nextTrim = now + Stopwatch.Frequency;
            TrimUnused(now);
        }
    }

    private static SidecarPixels? DecodeSidecar(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        var engine = SidecarTextureApi.Instance.Value;
        byte[]? rgba = engine.Decode(bytes, out int width, out int height);
        if (rgba == null)
            throw new InvalidDataException("旁挂文件读不出图");
        if (width != height)
            throw new InvalidDataException($"旁挂文件不是正方形（{width}×{height}）");

        bool opaque = engine.IsOpaque(rgba);
        int mipCount = BitOperations.Log2((uint)width) + 1;
        byte[][] levels = engine.BuildMips(rgba, width, height, mipCount, false, null, false);
        return engine.Compress(width, levels, opaque);
    }

    private static void CompleteSidecar(SidecarRequest request, Task<SidecarPixels?> work)
    {
        // 读回成功会由 StorePatch 刷新文件时间戳（=“刚刚用过”）；失败/被拒绝也要注销 in-flight，
        // 否则一次坏文件会让清理任务永远跳过它。注销必须放在 touch 之后（finally 覆盖全部 return）。
        bool ownsInFlight = false;
        try
        {
            int handle = request.Handle;
            if (!pendingSidecars.TryGetValue(handle, out var current))
            {
                // 请求已被 Clear/ForgetMemory/StorePatch 取消：移除方负责注销 in-flight；
                // 这里必须直接返回，否则会把调用方明确“忘掉”的旧图重新 StorePatch 回内存。
                _ = work.Exception;
                return;
            }
            if (!ReferenceEquals(current, request))
            {
                // 同 handle 已被新请求替换：旧注册由替换方注销，旧结果不得发布。
                _ = work.Exception;
                return;
            }
            ownsInFlight = true;
            pendingSidecars.Remove(handle);
            var layer = request.Layer;
            bool sameOwner = MatchesCelestialOwner(handle, layer);
            bool samePath = false;
            if (sameOwner)
            {
                // PathFor 会读天体生成配置；配置异常不能让 Update 每帧抛出去（缓存校验失败就当没这张图）。
                try { samePath = string.Equals(request.Path, PathFor(cachedSeed, handle), StringComparison.Ordinal); }
                catch (Exception ex)
                {
                    Log.Warning($"[PFView·烘焙] body={handle} 校验旁挂路径失败（按未采用处理）：{ex.Message}");
                }
            }
            if (!sameOwner || !samePath)
            {
                _ = work.Exception;
                loadTried.Remove(handle);
                return;
            }

            Texture2D? texture = null;
            try
            {
                var pixels = work.GetAwaiter().GetResult();
                if (pixels == null) return;
                if (layer == null || !ComputeCapFromLayer(layer, out var center, out float halfAngleDegrees))
                    throw new InvalidDataException("缺 layer 或格心退化，无法定位旁挂图");

                texture = new Texture2D(pixels.Size, pixels.Size, TextureFormat.RGBA32, pixels.Levels.Length,
                    linear: false, createUninitialized: true)
                {
                    name = "PF_Patch_" + handle,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear
                };
                SidecarTextureUpload.Upload(texture, pixels);
                if (!texture.EnsureGpuResident())
                    throw new InvalidOperationException("旁挂贴图上传失败");
                StorePatch(handle, new Patch
                {
                    tex = texture,
                    path = request.Path,
                    center = center,
                    thetaMaxRad = (Mathf.Min(halfAngleDegrees, MaxHalfAngleDeg) + PatchMarginDeg) * Mathf.Deg2Rad
                });
                texture = null;
            }
            catch (Exception ex)
            {
                Log.Warning($"[PFView·烘焙] body={handle} 旁挂图未采用：{ex.Message}");
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }
        finally
        {
            if (ownsInFlight) UntrackCacheFileInFlight(request.Path);
        }
    }

    internal static void MarkInUse(int handle)
    {
        if (!baked.TryGetValue(handle, out var patch)) return;
        long now = Stopwatch.GetTimestamp();
        patch.lastUse = now;
        // 内存命中也要算“用过”：否则一张一直在看、但从未重新读盘的图会在 7 天后被清掉。
        // 这是每帧路径，按 6 小时节流，避免每帧写文件时间戳。
        if (now - patch.lastDiskTouch >= CacheTouchIntervalTicks)
        {
            patch.lastDiskTouch = now;
            TouchCacheFile(patch.path);
        }
        baked[handle] = patch;
    }

    internal static void TrimUnused(long now)
    {
        long bytes = baked.Values.Sum(patch => patch.bytes);
        foreach (var entry in baked.OrderBy(pair => pair.Value.lastUse).ToArray())
        {
            var patch = entry.Value;
            long idle = now - patch.lastUse;
            if (idle < Stopwatch.Frequency || (bytes <= TextureBudgetBytes && idle < IdleLifetime)) break;
            if (!File.Exists(patch.path)) continue;

            if (patch.tex != null) UnityEngine.Object.Destroy(patch.tex);
            baked.Remove(entry.Key);
            loadTried.Remove(entry.Key);
            bytes -= patch.bytes;
            Touch(entry.Key);
        }
    }

    private static long TextureBytes(Texture2D? texture)
    {
        if (texture == null) return 0;
        int blockBytes = texture.format switch
        {
            TextureFormat.DXT1 or TextureFormat.BC4 => 8,
            TextureFormat.DXT5 or TextureFormat.BC5 or TextureFormat.BC6H or TextureFormat.BC7 => 16,
            _ => 0
        };
        long bytes = 0;
        for (int level = 0; level < texture.mipmapCount; level++)
        {
            int width = Math.Max(1, texture.width >> level), height = Math.Max(1, texture.height >> level);
            bytes += blockBytes == 0 ? 4L * width * height : (long)blockBytes * ((width + 3) / 4) * ((height + 3) / 4);
        }
        return bytes;
    }

    // The engine moved these CPU texture helpers and the upload format enum between 4977 and 5005.
    // Bind the installed signatures once. Both releases accept engine-native top-down mip data.
    private sealed class SidecarTextureApi
    {
        internal static readonly Lazy<SidecarTextureApi> Instance = new(() => new());
        private static readonly Assembly EngineAssembly = typeof(Waylith.Rendering.ShaderMetadataParser).Assembly;
        internal delegate byte[]? DecodePng(ReadOnlySpan<byte> png, out int width, out int height);
        internal delegate byte[][] GenerateMips(byte[] rgba, int width, int height, int count,
            bool parallel, Func<int, byte[]>? allocate, bool premultiplied);

        internal readonly DecodePng Decode = Resolve("PngDecoder").GetMethod("TryDecodeRgba32")!.CreateDelegate<DecodePng>();
        internal readonly Func<byte[], bool> IsOpaque = Resolve("TexturePixelData").GetMethod("IsOpaqueRgba32")!.CreateDelegate<Func<byte[], bool>>();
        internal readonly GenerateMips BuildMips = Resolve("TexturePixelData").GetMethod("GenerateRgba32MipChain",
            [typeof(byte[]), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(Func<int, byte[]>), typeof(bool)])!
            .CreateDelegate<GenerateMips>();
        private readonly MethodInfo chooseTarget = Resolve("TextureCompressionSettings").GetMethod("SelectTarget")!;
        private readonly MethodInfo encode = Resolve("CpuBlockEncoder").GetMethods().Single(method =>
            method.Name == "EncodeLevels" && method.GetParameters().Length == 6);
        private static Type Resolve(string name) => EngineAssembly.GetType("Waylith.Graphics.Textures." + name) ??
            EngineAssembly.GetType("Waylith.Graphics.Textures.BlockCompression." + name) ??
            EngineAssembly.GetType("Waylith.Rendering." + name, throwOnError: true)!;

        internal SidecarPixels Compress(int size, byte[][] levels, bool opaque)
        {
            object? target = chooseTarget.Invoke(null, [size, size, opaque, null]);
            if (target?.ToString() == "None") return new(size, null, levels, opaque);
            try
            {
                var compressed = (byte[][])encode.Invoke(null, [levels, size, size, levels.Length, target, false])!;
                return new(size, target, compressed, opaque);
            }
            catch (TargetInvocationException)
            {
                // Preserve the usable uncompressed mips if the configured encoder fails.
                return new(size, null, levels, opaque);
            }
        }
    }

    private static class SidecarTextureUpload
    {
        private static readonly MethodInfo LoadedFormat = typeof(Waylith.Rendering.GpuTextureService).GetMethod("ToLoadedFormat")!;
        private static readonly MethodInfo ReplacePixels = typeof(Waylith.Rendering.Texture2D).GetMethod("ReplaceCpuData")!;
        private static readonly object Rgba32 = Enum.Parse(ReplacePixels.GetParameters()[2].ParameterType, "Rgba32");

        internal static void Upload(Texture2D texture, SidecarPixels pixels)
        {
            texture.CoreTexture.Opaque = pixels.Opaque;
            object? format = pixels.Compression == null ? Rgba32 : LoadedFormat.Invoke(null, [pixels.Compression]);
            ReplacePixels.Invoke(texture.CoreTexture,
                [pixels.Size, pixels.Size, format, pixels.Levels, pixels.Levels.Length, false, true]);
        }
    }
}

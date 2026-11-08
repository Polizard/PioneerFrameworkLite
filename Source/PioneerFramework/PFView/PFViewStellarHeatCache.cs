using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Vector3 = System.Numerics.Vector3;

namespace PioneerFramework
{
    /// <summary>PFView 窗口拥有的实验数值缓存。后台只计算标量，渲染线程负责上传和释放。</summary>
    internal sealed class PFViewStellarHeatCache : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private Task<Color32[]>? bake;

        internal Texture2D? Texture { get; private set; }
        internal bool Failed { get; private set; }

        internal PFViewStellarHeatCache()
        {
            CancellationToken token = cancellation.Token;
            bake = Task.Run(() => PFViewStellarHeatCacheCore.Bake(token), token);
        }

        internal Texture2D? GetTexture()
        {
            if (bake?.IsCompleted != true) return Texture;

            Texture2D? upload = null;
            try
            {
                Color32[] pixels = bake.GetAwaiter().GetResult();
                upload = new Texture2D(PFViewStellarHeatCacheCore.Width, PFViewStellarHeatCacheCore.Height,
                    TextureFormat.RGBA32, mipChain: false, linear: true)
                {
                    name = "PF_StellarHeatCache",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                upload.SetPixels32(pixels);
                upload.Apply(updateMipmaps: false, makeNoLongerReadable: true);
                if (!upload.EnsureGpuResident())
                    throw new InvalidOperationException("The photosphere cache could not be uploaded to the GPU.");
                Texture = upload;
            }
            catch (Exception ex)
            {
                if (upload != null) UnityEngine.Object.Destroy(upload);
                Failed = true;
                Log.Warning("[PFView·光球缓存] 数值缓存准备失败，继续使用原版颗粒计算。 " + ex.Message);
            }
            finally
            {
                bake = null;
            }
            return Texture;
        }

        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
            if (bake != null)
            {
                // 关闭窗口时不等待计算，仍观察已经发生或稍后发生的失败。
                _ = bake.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                bake = null;
            }
            if (Texture != null) UnityEngine.Object.Destroy(Texture);
            Texture = null;
        }
    }

    /// <summary>
    /// 只采样旧 GLSL 中由局部方向和 drift 决定的 surfaceHeat。相机旋转、噪声密度和采样深度仍由 GLSL 计算。
    /// 64 帧覆盖一个 drift 周期。四个相邻相位存入同一经纬度分块的 RGBA，四周补边供 GPU 双线性插值。
    /// </summary>
    internal static class PFViewStellarHeatCacheCore
    {
        private const int Columns = 4;
        private const int Rows = 4;
        private const int FramesPerTile = 4;
        internal const int FrameCount = Columns * Rows * FramesPerTile;
        private const int Longitudes = 512;
        private const int Latitudes = 256;
        internal const int TileWidth = Longitudes + 2;
        internal const int TileHeight = Latitudes + 2;
        internal const int Width = Columns * TileWidth;
        internal const int Height = Rows * TileHeight;
        private const float HeatRange = 1.16f;

        internal static float FramePosition(float phase)
            => Fract(PFViewStellarVisualCore.FlowAngle(phase) / MathF.Tau) * FrameCount;

        internal static Color32[] Bake(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var pixels = new Color32[Width * Height];
            for (int frame = 0; frame < FrameCount; frame++)
                BakeFrame(pixels, frame, token);
            return pixels;
        }

        internal static void BakeFrame(Color32[] pixels, int frame, CancellationToken token)
        {
            float flow = frame * (MathF.Tau / FrameCount);
            Vector4 frameDrift = PFViewStellarVisualCore.DriftAtFlow(flow);
            var drift = new Vector3(frameDrift.x, frameDrift.y, frameDrift.z);
            int tile = frame / FramesPerTile;
            int channel = frame % FramesPerTile;
            int firstColumn = tile % Columns * TileWidth;
            int firstRow = tile / Columns * TileHeight;

            for (int y = 0; y < Latitudes; y++)
            {
                token.ThrowIfCancellationRequested();
                float theta = MathF.PI * ((y + .5f) / Latitudes);
                (float sinTheta, float cosTheta) = MathF.SinCos(theta);
                int rowStart = (firstRow + y + 1) * Width + firstColumn;
                for (int x = 0; x < Longitudes; x++)
                {
                    float phi = MathF.Tau * ((x + .5f) / Longitudes - .5f);
                    (float sinPhi, float cosPhi) = MathF.SinCos(phi);
                    var direction = new Vector3(sinTheta * cosPhi, cosTheta, sinTheta * sinPhi);
                    float heat = SurfaceHeat(direction, drift);
                    byte value = (byte)Math.Clamp((int)MathF.Round(heat * (255f / HeatRange)), 0, 255);
                    ref Color32 pixel = ref pixels[rowStart + x + 1];
                    switch (channel)
                    {
                        case 0: pixel.r = value; break;
                        case 1: pixel.g = value; break;
                        case 2: pixel.b = value; break;
                        default: pixel.a = value; break;
                    }
                }
                pixels[rowStart] = pixels[rowStart + Longitudes];
                pixels[rowStart + TileWidth - 1] = pixels[rowStart + 1];
            }

            int top = firstRow * Width + firstColumn;
            int bottom = (firstRow + TileHeight - 1) * Width + firstColumn;
            Array.Copy(pixels, top + Width, pixels, top, TileWidth);
            Array.Copy(pixels, bottom - Width, pixels, bottom, TileWidth);
        }

        private static float SurfaceHeat(Vector3 direction, Vector3 drift)
        {
            Vector3 p = direction * 7f;
            var warp = new Vector3(Noise(p + drift),
                Noise(p + new Vector3(drift.Y, drift.Z, drift.X) + new Vector3(7f)),
                Noise(p + new Vector3(drift.Z, drift.X, drift.Y) + new Vector3(9f))) - new Vector3(.5f);
            float cells = Granulation(direction * 49f + warp * 1.9f + drift * 1.1f);
            return .59f + .48f * cells + .09f * Noise(direction * 130f + warp * 2f);
        }

        private static float Granulation(Vector3 p)
        {
            Vector3 cell = Floor(p);
            Vector3 fraction = p - cell;
            float energy = 0f;
            for (int z = -1; z <= 1; z++)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                var offset = new Vector3(x, y, z);
                Vector3 s = cell + offset;
                var jitter = new Vector3(Hash(s), Hash(s + new Vector3(37, 11, 19)), Hash(s + new Vector3(5, 53, 7)));
                Vector3 delta = offset + new Vector3(.16f) + jitter * .68f - fraction;
                energy += MathF.Exp(-Vector3.Dot(delta, delta) * 8f) * (.7f + .3f * Hash(s + new Vector3(23, 17, 61)));
            }
            float t = Math.Clamp((energy - .035f) / (.65f - .035f), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        private static float Noise(Vector3 p)
        {
            Vector3 i = Floor(p);
            Vector3 f = p - i;
            f = f * f * f * (f * (f * 6f - new Vector3(15f)) + new Vector3(10f));
            return Mix(
                Mix(Mix(Hash(i), Hash(i + new Vector3(1, 0, 0)), f.X),
                    Mix(Hash(i + new Vector3(0, 1, 0)), Hash(i + new Vector3(1, 1, 0)), f.X), f.Y),
                Mix(Mix(Hash(i + new Vector3(0, 0, 1)), Hash(i + new Vector3(1, 0, 1)), f.X),
                    Mix(Hash(i + new Vector3(0, 1, 1)), Hash(i + Vector3.One), f.X), f.Y), f.Z);
        }

        private static float Hash(Vector3 p)
        {
            p *= new Vector3(.1031f, .1030f, .0973f);
            p -= Floor(p);
            p += new Vector3(Vector3.Dot(p, new Vector3(p.Y, p.X, p.Z) + new Vector3(33.33f)));
            return Fract((p.X + p.Y) * p.Z);
        }

        private static Vector3 Floor(Vector3 p) => new(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        private static float Fract(float x) => x - MathF.Floor(x);
        private static float Mix(float a, float b, float t) => a * (1f - t) + b * t;
    }
}

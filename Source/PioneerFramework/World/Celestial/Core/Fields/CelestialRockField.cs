using System;
using System.Collections.Generic;
using System.Threading;

namespace PioneerFramework.Celestial
{
    /// <summary>
    /// **岩质行星场地貌**（2026-11-08，B 方案；设计参考「天体运行」的 RockLab，
    /// 但按我们的框架口径重写：输出仍是既有 <see cref="CelestialPlanetTerrainSample"/> 的 5 个通道，
    /// 世界层生成、biome 规则、地图生态与视觉全部沿用现有消费者，不新增 comp / Def / 存档实体）。
    ///
    /// ## B1：静态地貌（O(1)/采样）
    /// · 大陆/海底：低频 fBm；· 陨坑：抖动网格（每格至多一个，O(27) 邻域）；
    /// · 山系：脊状噪声取幂；· 干河谷：低频脊状"沟"；· 地质：substrate 噪声。
    ///
    /// ## B2：真水系（每天体一次预计算 + 每次采样 O(1) 查表）
    /// 在 N=4096 的 Fibonacci 球面网格上：
    /// 1. 用同一套静态地貌算基础高程；
    /// 2. 空间哈希找每个点的最近邻居，按"最陡下降"连边（无更低邻居 = 汇/湖）；
    /// 3. 按高程降序把流量累加到下游（1 + 全部上游汇水）；
    /// 4. 采样时按最近网格点的归一化流量**刻河道并抬高湿度**（干河床/古湖盆），
    ///    低洼汇水区同时压平 roughness ⇒ 干湖盆在 biome/hilliness 上都可读。
    ///
    /// 缓存：`(配置数值, seed)` → 一份流域图，`ConcurrentDictionary` + `Lazy` 保证并发只建一次；
    /// 上限 <see cref="HydrologyCacheLimit"/> 份，超出整体清空（只影响重建成本，不影响确定性）。
    ///
    /// 版本纪律：改变本文件任何数值语义都必须让 `CelestialPlanetTerrain.AlgorithmVersion` +1
    ///（Waves 不受影响；Rock 内容随世界层 header 版本被显式拒绝/重建）。
    /// </summary>
    public static class CelestialRockField
    {
        /// <summary>流域图网格点数（2 的幂附近，Fibonacci 球面均匀分布）。</summary>
        private const int FlowGridPoints = 16384;

        /// <summary>流域图缓存上限（份）。超限整体清空：只增加重建成本，不改变结果。</summary>
        private const int HydrologyCacheLimit = 16;

        private readonly struct HydrologyKey : IEquatable<HydrologyKey>
        {
            public readonly double ContinentScale;
            public readonly double Amplitude;
            public readonly double Offset;
            public readonly double Roughness;
            public readonly double MountainWeight;
            public readonly int Seed;
            public HydrologyKey(CelestialPlanetTerrainConfiguration configuration, int seed)
            {
                ContinentScale = configuration.ContinentScale;
                Amplitude = configuration.ElevationAmplitudeMeters;
                Offset = configuration.ElevationOffsetMeters;
                Roughness = configuration.Roughness;
                MountainWeight = configuration.MountainWeight;
                Seed = seed;
            }
            public bool Equals(HydrologyKey other) =>
                ContinentScale.Equals(other.ContinentScale) && Amplitude.Equals(other.Amplitude) &&
                Offset.Equals(other.Offset) && Roughness.Equals(other.Roughness) &&
                MountainWeight.Equals(other.MountainWeight) && Seed == other.Seed;
            public override bool Equals(object? obj) => obj is HydrologyKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(ContinentScale, Amplitude, Offset, Roughness, MountainWeight, Seed);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<HydrologyKey, Lazy<Hydrology>> HydrologyCache = new();

        /// <summary>显式山脊带：一条大圆（球面上的"山脉走向"）+ 半宽 + 幅度。参考模组的"连续山系"的最小版本，
        /// 但用 O(链数) 解析求距、不预计算网格。种子决定条数/走向/宽度。</summary>
        private readonly struct MountainChain
        {
            public readonly double Nx, Ny, Nz;   // 大圆平面法线（链沿该大圆走）
            public readonly double HalfWidth;     // 角半径（弧度）
            public readonly double Amplitude;     // 相对高程幅度
            public MountainChain(double nx, double ny, double nz, double halfWidth, double amplitude)
            { Nx = nx; Ny = ny; Nz = nz; HalfWidth = halfWidth; Amplitude = amplitude; }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, MountainChain[]> ChainCache = new();
        private const int ChainCacheLimit = 32;

        private static MountainChain[] ChainsFor(int seed)
        {
            if (ChainCache.TryGetValue(seed, out var cached)) return cached;
            if (ChainCache.Count >= ChainCacheLimit) ChainCache.Clear();
            var chains = BuildChains(seed);
            ChainCache[seed] = chains;
            return chains;
        }

        private static MountainChain[] BuildChains(int seed)
        {
            int count = 3 + (int)(Hash01(seed, 7, 11, 13, 0xC1) * 4.0);   // 3~6 条
            var result = new MountainChain[count];
            for (int i = 0; i < count; i++)
            {
                double ux = Hash01(seed, i, 1, 0, 0xD1) * 2.0 - 1.0;
                double uy = Hash01(seed, i, 2, 0, 0xD1) * 2.0 - 1.0;
                double uz = Hash01(seed, i, 3, 0, 0xD1) * 2.0 - 1.0;
                double length = Math.Sqrt(ux * ux + uy * uy + uz * uz);
                if (length < 1e-6) { ux = 0; uy = 1; uz = 0; length = 1; }
                double halfWidth = 0.014 + 0.026 * Hash01(seed, i, 4, 0, 0xD1);   // 0.014~0.040 rad ≈ 35~100 km
                double amplitude = 0.45 + 0.55 * Hash01(seed, i, 5, 0, 0xD1);
                result[i] = new MountainChain(ux / length, uy / length, uz / length, halfWidth, amplitude);
            }
            return result;
        }

        /// <summary>山脊带贡献（相对高程）与最大带强（0..1，驱动 hilliness）。</summary>
        private static double ChainRelief(double x, double y, double z, int seed, double roughKnob, out double chainScore)
        {
            chainScore = 0.0;
            double sum = 0.0;
            foreach (var chain in ChainsFor(seed))
            {
                double distanceToGreatCircle = Math.Asin(Math.Clamp(Math.Abs(x * chain.Nx + y * chain.Ny + z * chain.Nz), 0.0, 1.0));
                double t = 1.0 - Math.Clamp(distanceToGreatCircle / chain.HalfWidth, 0.0, 1.0);
                if (t <= 0.0) continue;
                double profile = t * t * (3.0 - 2.0 * t);
                // 沿链的起伏：脊状噪声让山系不是一条光滑的墙。
                double along = 1.0 - Math.Abs(2.0 * Fbm(x, y, z, 18.0, seed, unchecked((int)0x51ED270B), 3) - 1.0);
                double modulated = profile * (0.55 + 0.45 * along * (0.6 + 0.4 * roughKnob));
                sum += chain.Amplitude * modulated * 0.9;
                if (modulated > chainScore) chainScore = modulated;
            }
            return sum;
        }

        /// <summary>测试/诊断用：当前缓存份数。</summary>
        internal static int CachedHydrologyCount => HydrologyCache.Count;

        /// <summary>陨坑抖动网格的目标频率（每半球约 freq 个格）。roughness 越大坑越密。</summary>
        private static int CraterFrequency(CelestialPlanetTerrainConfiguration configuration)
        {
            int frequency = 6 + (int)Math.Round(configuration.Roughness * 10);
            return Math.Clamp(frequency, 4, 20);
        }

        /// <summary>岩质场地貌采样（纯函数；同参数逐位相同）。</summary>
        public static CelestialPlanetTerrainSample Sample(CelestialPlanetTerrainConfiguration configuration, double x, double y, double z, int seed)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            Normalize(ref x, ref y, ref z);
            var baseSample = SampleBase(configuration, x, y, z, seed, out double mountain, out _);

            // 水系：查最近流域点（O(1)），按归一化汇水刻河道。
            Hydrology hydrology = GetHydrology(configuration, seed);
            double flow = hydrology.FlowAt(x, y, z);
            // 河道：阈值 0.10~0.45、深 0.55 幅度 —— 上一版太浅（几百米），被山脊完全盖住。
            double river = SmoothStep(0.10, 0.45, flow);
            double lake = SmoothStep(0.65, 0.95, flow) * (hydrology.IsSinkAt(x, y, z) ? 1.0 : 0.0);

            double elevationMeters = Math.Clamp(baseSample.ElevationMeters - configuration.ElevationAmplitudeMeters * (0.55 * river + 0.18 * lake),
                configuration.ElevationOffsetMeters - configuration.ElevationAmplitudeMeters,
                configuration.ElevationOffsetMeters + configuration.ElevationAmplitudeMeters);
            double roughness = Math.Clamp(baseSample.Roughness * (1.0 - 0.45 * river - 0.25 * lake), 0.0, 1.0);
            double moisture = Math.Clamp(baseSample.Moisture + 1.15 * river + 0.55 * lake - 0.20 * mountain, 0.0, 1.0);
            return new CelestialPlanetTerrainSample(elevationMeters, roughness, baseSample.MountainScore, baseSample.Geology, moisture);
        }

        /// <summary>不含水系的静态地貌（流域图构建与 B1 判据共用）。</summary>
        internal static CelestialPlanetTerrainSample SampleBase(CelestialPlanetTerrainConfiguration configuration,
            double x, double y, double z, int seed, out double mountainOut, out double craterOut)
        {
            double amplitude = configuration.ElevationAmplitudeMeters;
            double offset = configuration.ElevationOffsetMeters;
            double roughKnob = configuration.Roughness;
            double mountainKnob = configuration.MountainWeight;

            double macro = Fbm(x, y, z, configuration.ContinentScale, seed, unchecked((int)0x51ED270B), 4);
            double elevation = macro;

            double ridge = 1.0 - Math.Abs(2.0 * Fbm(x, y, z, configuration.ContinentScale * 2.2, seed, unchecked((int)0x2C1B3C6D), 4) - 1.0);
            // B2 校准：ridge^3 让"山脉评分"几乎永远到不了既有 hilliness 阈值（0.3/0.5/0.75）⇒ 全图 Flat。
            // 改成 ridge^1.8 并把权重归一：mountainShape 覆盖 0..1，再由 mountainWeight 决定实际起伏强弱。
            double mountainShape = Math.Pow(Math.Clamp(ridge, 0.0, 1.0), 1.8);
            double mountain = mountainShape * mountainKnob * 0.60;
            elevation += mountain;

            // B3 目标 A：显式山脊带（3~6 条大圆山脉）——这是"看得出是山脊"的关键；纯噪声脊状场太糊。
            double chain = ChainRelief(x, y, z, seed, roughKnob, out double chainScore);
            elevation += chain;

            double crater = CraterField(x, y, z, configuration, seed);
            elevation += crater;

            double channelRidge = 1.0 - Math.Abs(2.0 * Fbm(x, y, z, configuration.ContinentScale * 1.6, seed, unchecked((int)0x7F4A7C15), 3) - 1.0);
            double channel = Math.Clamp((channelRidge - 0.72) / 0.28, 0.0, 1.0);
            channel = channel * channel * (3.0 - 2.0 * channel) * (0.35 + 0.65 * roughKnob);
            elevation -= channel * 0.22;

            double detail = Fbm(x, y, z, configuration.ContinentScale * 12.0, seed, unchecked((int)0x165667B1), 3);
            double roughness = Math.Clamp(roughKnob * (0.55 + 0.45 * (0.5 + 0.5 * detail)) + crater * 0.35 + mountain * 0.60 + chain * 0.55, 0.0, 1.0);

            double elevationMeters = offset + amplitude * Math.Clamp(elevation, -1.0, 1.0);
            double mountainScore = Math.Clamp(Math.Max(mountainShape * Math.Clamp(mountainKnob * 1.8, 0.0, 1.0), chainScore * 1.25), 0.0, 1.0);
            double geology = Math.Clamp(0.5 + 0.42 * Fbm(x, y, z, configuration.ContinentScale * 3.1, seed, unchecked((int)0x27D4EB2F), 3) - 0.25 * mountainScore, 0.0, 1.0);
            // 基础湿度压在 ≤0.75：给"水系湿度信号"留出 ≥0.8 的判据空间（biome 规则按湿度识别河床）。
            double moisture = Math.Clamp(0.45 + 0.30 * Fbm(x, y, z, configuration.ContinentScale * 1.3, seed, unchecked((int)0x9E3779B9), 3) + 0.30 * channel - 0.20 * mountainScore, 0.0, 0.75);
            mountainOut = mountain; craterOut = crater;
            return new CelestialPlanetTerrainSample(elevationMeters, roughness, mountainScore, geology, moisture);
        }

        private static Hydrology GetHydrology(CelestialPlanetTerrainConfiguration configuration, int seed)
        {
            var key = new HydrologyKey(configuration, seed);
            if (HydrologyCache.Count >= HydrologyCacheLimit) HydrologyCache.Clear();
            return HydrologyCache.GetOrAdd(key, _ => new Lazy<Hydrology>(
                () => new Hydrology(configuration, seed), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        /// <summary>
        /// 每天体一份的流域图：Fibonacci 球面网格 + 空间哈希 + 最陡下降 + 汇水累积。
        /// 构建是确定性的（固定点序、固定邻居排序、平局按小下标），与调用线程/顺序无关。
        /// </summary>
        private sealed class Hydrology
        {
            private const double GoldenAngle = 2.39996322972865332;
            private const int HashResolution = 64;
            private const int MaxNeighbours = 10;

            private readonly float[] xs = new float[FlowGridPoints];
            private readonly float[] ys = new float[FlowGridPoints];
            private readonly float[] zs = new float[FlowGridPoints];
            private readonly float[] flow = new float[FlowGridPoints];
            private readonly bool[] sink = new bool[FlowGridPoints];
            private readonly Dictionary<long, List<int>> buckets = new();

            public Hydrology(CelestialPlanetTerrainConfiguration configuration, int seed)
            {
                BuildPoints();
                var elevation = new double[FlowGridPoints];
                for (int i = 0; i < FlowGridPoints; i++)
                    elevation[i] = SampleBase(configuration, xs[i], ys[i], zs[i], seed, out _, out _).ElevationMeters;

                BuildBuckets();
                var downstream = new int[FlowGridPoints];
                Array.Fill(downstream, -1);
                double meanSpacing = Math.Sqrt(4.0 * Math.PI / FlowGridPoints);
                double searchRadius = meanSpacing * 2.2;
                var candidates = new List<(double Distance, int Index)>(32);
                for (int i = 0; i < FlowGridPoints; i++)
                {
                    candidates.Clear();
                    CollectCandidates(xs[i], ys[i], zs[i], searchRadius, candidates);
                    candidates.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Index.CompareTo(b.Index));
                    int best = -1; double bestSlope = 0.0;
                    int considered = 0;
                    foreach (var candidate in candidates)
                    {
                        if (candidate.Index == i) continue;
                        if (++considered > MaxNeighbours) break;
                        double drop = elevation[i] - elevation[candidate.Index];
                        if (drop <= 0.0) continue;
                        double slope = drop / Math.Max(candidate.Distance, 1e-9);
                        if (slope > bestSlope) { bestSlope = slope; best = candidate.Index; }
                    }
                    downstream[i] = best;
                }

                // 汇水累积：按高程降序（高→低），把每个点的水量并给下游；平局按小下标。
                var order = new int[FlowGridPoints];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                Array.Sort(order, (a, b) => elevation[a] != elevation[b] ? elevation[b].CompareTo(elevation[a]) : a.CompareTo(b));
                var accumulation = new double[FlowGridPoints];
                Array.Fill(accumulation, 1.0);
                foreach (int i in order)
                {
                    int down = downstream[i];
                    if (down < 0) { sink[i] = true; continue; }
                    accumulation[down] += accumulation[i];
                }
                double max = 1.0;
                for (int i = 0; i < FlowGridPoints; i++) if (accumulation[i] > max) max = accumulation[i];
                for (int i = 0; i < FlowGridPoints; i++) flow[i] = (float)(accumulation[i] / max);
            }

            private void BuildPoints()
            {
                for (int i = 0; i < FlowGridPoints; i++)
                {
                    double y = 1.0 - (i + 0.5) * 2.0 / FlowGridPoints;
                    double radius = Math.Sqrt(Math.Max(0.0, 1.0 - y * y));
                    double theta = i * GoldenAngle;
                    xs[i] = (float)(Math.Cos(theta) * radius);
                    ys[i] = (float)y;
                    zs[i] = (float)(Math.Sin(theta) * radius);
                }
            }

            private void BuildBuckets()
            {
                for (int i = 0; i < FlowGridPoints; i++)
                {
                    long key = BucketKey(xs[i], ys[i], zs[i]);
                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                    list.Add(i);
                }
            }

            private static long BucketKey(double x, double y, double z)
            {
                int bx = Math.Clamp((int)((x * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                int by = Math.Clamp((int)((y * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                int bz = Math.Clamp((int)((z * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                return ((long)bz * HashResolution + by) * HashResolution + bx;
            }

            private void CollectCandidates(double x, double y, double z, double searchRadius, List<(double, int)> into)
            {
                double cosRadius = Math.Cos(searchRadius);
                int bx = Math.Clamp((int)((x * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                int by = Math.Clamp((int)((y * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                int bz = Math.Clamp((int)((z * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                for (int oz = -1; oz <= 1; oz++)
                {
                    int cx = bx + ox, cy = by + oy, cz = bz + oz;
                    if (cx < 0 || cy < 0 || cz < 0 || cx >= HashResolution || cy >= HashResolution || cz >= HashResolution) continue;
                    long key = ((long)cz * HashResolution + cy) * HashResolution + cx;
                    if (!buckets.TryGetValue(key, out var list)) continue;
                    foreach (int index in list)
                    {
                        double dot = x * xs[index] + y * ys[index] + z * zs[index];
                        if (dot < cosRadius) continue;
                        double dx = x - xs[index], dy = y - ys[index], dz = z - zs[index];
                        into.Add((dx * dx + dy * dy + dz * dz, index));
                    }
                }
            }

            /// <summary>最近网格点的归一化汇水量 [0,1]。</summary>
            public double FlowAt(double x, double y, double z)
            {
                int nearest = Nearest(x, y, z);
                return nearest < 0 ? 0.0 : flow[nearest];
            }

            /// <summary>最近网格点是否为汇（湖泊/海盆）。</summary>
            public bool IsSinkAt(double x, double y, double z)
            {
                int nearest = Nearest(x, y, z);
                return nearest >= 0 && sink[nearest];
            }

            private int Nearest(double x, double y, double z)
            {
                int best = -1; double bestDistance = double.MaxValue;
                int bx = Math.Clamp((int)((x * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                int by = Math.Clamp((int)((y * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                int bz = Math.Clamp((int)((z * 0.5 + 0.5) * HashResolution), 0, HashResolution - 1);
                for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                for (int oz = -1; oz <= 1; oz++)
                {
                    int cx = bx + ox, cy = by + oy, cz = bz + oz;
                    if (cx < 0 || cy < 0 || cz < 0 || cx >= HashResolution || cy >= HashResolution || cz >= HashResolution) continue;
                    long key = ((long)cz * HashResolution + cy) * HashResolution + cx;
                    if (!buckets.TryGetValue(key, out var list)) continue;
                    foreach (int index in list)
                    {
                        double dx = x - xs[index], dy = y - ys[index], dz = z - zs[index];
                        double distance = dx * dx + dy * dy + dz * dz;
                        if (distance < bestDistance || (distance == bestDistance && index < best))
                        { bestDistance = distance; best = index; }
                    }
                }
                return best;
            }
        }

        private static double SmoothStep(double edge0, double edge1, double value)
        {
            if (edge1 <= edge0) return value >= edge1 ? 1.0 : 0.0;
            double t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        private static void Normalize(ref double x, ref double y, ref double z)
        {
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (!double.IsFinite(length) || length < 1e-12) throw new ArgumentException("Degenerate direction.");
            x /= length; y /= length; z /= length;
        }

        /// <summary>陨坑帽（返回的是"相对高程"，单位 = 高程幅度的 1）。</summary>
        private static double CraterField(double x, double y, double z, CelestialPlanetTerrainConfiguration configuration, int seed)
        {
            int frequency = CraterFrequency(configuration);
            double scale = frequency;
            double gx = x * scale, gy = y * scale, gz = z * scale;
            int cx = (int)Math.Floor(gx), cy = (int)Math.Floor(gy), cz = (int)Math.Floor(gz);
            double depth = 0.09 * configuration.Roughness + 0.03;
            double sum = 0.0;
            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            for (int oz = -1; oz <= 1; oz++)
            {
                int ix = cx + ox, iy = cy + oy, iz = cz + oz;
                double occupancy = Hash01(seed, ix, iy, iz, 0x11);
                if (occupancy > 0.55) continue;
                double jx = ix + Hash01(seed, ix, iy, iz, 0x23) * 2.0 - 1.0;
                double jy = iy + Hash01(seed, ix, iy, iz, 0x35) * 2.0 - 1.0;
                double jz = iz + Hash01(seed, ix, iy, iz, 0x47) * 2.0 - 1.0;
                double jl = Math.Sqrt(jx * jx + jy * jy + jz * jz);
                if (jl < 1e-9) continue;
                double dot = (x * jx + y * jy + z * jz) / jl;
                double angular = Math.Acos(Math.Clamp(dot, -1.0, 1.0));
                double radius = (0.18 + 0.42 * Hash01(seed, ix, iy, iz, 0x59)) / frequency * Math.PI;
                if (angular > radius * 1.45) continue;
                double size = 0.55 + 0.45 * Hash01(seed, ix, iy, iz, 0x6B);
                if (angular <= radius)
                {
                    double r = angular / radius;
                    sum -= depth * size * (1.0 - r * r);
                }
                else
                {
                    double ring = (angular - radius) / (radius * 0.30);
                    sum += depth * size * 0.45 * Math.Exp(-ring * ring);
                }
            }
            return sum;
        }

        private static double Fbm(double x, double y, double z, double scale, int seed, int channel, int octaves)
        {
            double sum = 0.0, amplitude = 1.0, frequency = scale;
            double normalization = 0.0;
            for (int octave = 0; octave < octaves; octave++)
            {
                sum += amplitude * ValueNoise(x * frequency, y * frequency, z * frequency, seed, channel + octave);
                normalization += amplitude;
                amplitude *= 0.5;
                frequency *= 2.03;
            }
            return normalization > 0 ? sum / normalization : 0.0;
        }

        private static double ValueNoise(double x, double y, double z, int seed, int channel)
        {
            double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
            int ix = (int)fx, iy = (int)fy, iz = (int)fz;
            double tx = Smooth(x - fx), ty = Smooth(y - fy), tz = Smooth(z - fz);
            double c000 = Hash01(seed, ix, iy, iz, channel);
            double c100 = Hash01(seed, ix + 1, iy, iz, channel);
            double c010 = Hash01(seed, ix, iy + 1, iz, channel);
            double c110 = Hash01(seed, ix + 1, iy + 1, iz, channel);
            double c001 = Hash01(seed, ix, iy, iz + 1, channel);
            double c101 = Hash01(seed, ix + 1, iy, iz + 1, channel);
            double c011 = Hash01(seed, ix, iy + 1, iz + 1, channel);
            double c111 = Hash01(seed, ix + 1, iy + 1, iz + 1, channel);
            double x00 = Lerp(c000, c100, tx), x10 = Lerp(c010, c110, tx);
            double x01 = Lerp(c001, c101, tx), x11 = Lerp(c011, c111, tx);
            double y0 = Lerp(x00, x10, ty), y1 = Lerp(x01, x11, ty);
            return Lerp(y0, y1, tz) * 2.0 - 1.0;
        }

        private static double Smooth(double t) => t * t * (3.0 - 2.0 * t);
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double Hash01(int seed, int x, int y, int z, int channel)
        {
            unchecked
            {
                uint h = (uint)seed * 0x9E3779B1u;
                h ^= (uint)x * 0x85EBCA77u; h = (h << 13) | (h >> 19);
                h ^= (uint)y * 0xC2B2AE3Du; h = (h << 17) | (h >> 15);
                h ^= (uint)z * 0x27D4EB2Fu; h = (h << 11) | (h >> 21);
                h ^= (uint)channel * 0x165667B1u;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return h / 4294967296.0;
            }
        }
    }
}
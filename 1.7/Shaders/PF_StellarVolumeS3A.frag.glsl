#version 450

// PFView stellar volume — S3-a "region-skipping" integration (2026-11-07, experimental).
//
// Why this file exists:
//   PF_StellarVolume.frag.glsl marches the whole 1.65R shell in BOTH passes even though most of
//   the outer corona (r > 1.42R) contributes almost nothing (P0 model: ~0 alpha; 90% of halo steps,
//   81% of surface steps sit at r > 1.18). This variant keeps the approved legacy sampling grid and
//   phase in the dense region (r <= OUTER), and only changes the outer part:
//     * entry: phase-aligned skip of the r > OUTER lead-in, integrated with 3 coarse midpoint samples;
//     * exit:  same 3-sample coarse integration for the outward r > OUTER tail;
//     * rays that never enter OUTER (impact > OUTER) use OUTER_STEP instead of the fine step;
//     * surface pass may discard impact > SURFACE_CULL_IMPACT (offline field-scan proof; see
//       Docs/superpowers/plans/2026-11-07-pfview-stellar-fullres-performance.md).
//   The per-sample field/emission model, .995 opacity partition, real gl_FragDepth, UBO (6x vec4,
//   96 B), heat-cache slot and all early-discard guards are IDENTICAL to the legacy shader.
//   Visual equivalence is validated offline against the legacy shader (same pixels, same phase);
//   in-game A/B is still required before this becomes the default (debug toggle S3AEnabled).

layout(location = 0) in vec3 vLocalPosition;
layout(location = 1) flat in vec4 vClip0;
layout(location = 2) flat in vec4 vClip1;
layout(location = 3) flat in vec4 vClip2;
layout(location = 4) flat in vec4 vClip3;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _StellarEye;
    vec4 _StellarTime;
    vec4 _StellarParams; // steps, halo pass, glow strength, proxy extent
    vec4 _StellarDrift;
    vec4 _StellarArchHeight0;
    vec4 _StellarArchHeight1;
} uMaterial;
layout(set = 2, binding = 1) uniform sampler2D _StellarHeatCache;

// Matches PFViewStellarHeatCacheCore. Each tile packs four 512x256 frames into RGBA.
vec4 cachedHeatTile(vec2 uv, float tileIndex)
{
    vec2 tile = vec2(mod(tileIndex, 4.0), floor(tileIndex / 4.0));
    vec2 pixel = tile * vec2(514.0, 258.0) + vec2(1.0)
        + vec2(uv.x * 512.0, clamp(uv.y * 256.0, .5, 255.5));
    // UnityStubs.SetPixels32 reverses CPU rows when uploading to Waylith.
    vec2 atlasUv = vec2(pixel.x / 2056.0, 1.0 - pixel.y / 1032.0);
    return textureLod(_StellarHeatCache, atlasUv, 0.0);
}

float cachedSurfaceHeat(vec3 q)
{
    float longitude = atan(q.z, q.x == 0.0 ? 1e-8 : q.x);
    vec2 uv = vec2(longitude / 6.28318530718 + .5, acos(clamp(q.y, -1.0, 1.0)) / 3.14159265359);
    float position = uMaterial._StellarTime.y;
    float frameIndex = floor(position);
    float tileIndex = floor(frameIndex / 4.0);
    int channel = int(mod(frameIndex, 4.0));
    vec4 phases = cachedHeatTile(uv, tileIndex);
    float nextHeat = channel < 3 ? phases[channel + 1] : cachedHeatTile(uv, mod(tileIndex + 1.0, 16.0)).r;
    return mix(phases[channel], nextHeat, fract(position)) * 1.16;
}

float hash(vec3 p)
{
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.x + p.y) * p.z);
}

float noise(vec3 p)
{
    vec3 i = floor(p), f = fract(p);
    f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return mix(mix(mix(hash(i), hash(i + vec3(1,0,0)), f.x),
        mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
        mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x),
        mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}

const mat3 twist = mat3(0.00,0.80,0.60, -0.80,0.36,-0.48, -0.60,-0.48,0.64);

float fbm(vec3 p)
{
    float v = 0.52 * noise(p); p = twist * p * 2.03;
    v += 0.27 * noise(p); p = twist * p * 2.01;
    v += 0.14 * noise(p); p = twist * p * 2.03;
    return v + 0.07 * noise(p);
}

vec3 spin(vec3 p, float angle)
{
    float c = cos(angle), s = sin(angle);
    return vec3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
}

float granulation(vec3 p)
{
    vec3 cell = floor(p), f = fract(p);
    float energy = 0.0;
    for (int z = -1; z <= 1; ++z)
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        vec3 g = vec3(float(x), float(y), float(z)), s = cell + g;
        vec3 jitter = vec3(hash(s), hash(s + vec3(37,11,19)), hash(s + vec3(5,53,7)));
        vec3 delta = g + 0.16 + jitter * 0.68 - f;
        energy += exp(-dot(delta, delta) * 8.0) * (0.7 + 0.3 * hash(s + vec3(23,17,61)));
    }
    return smoothstep(0.035, 0.65, energy);
}

float square(float x) { return x * x; }
float cube(float x) { return x * x * x; }
float fifth(float x) { float x2 = x * x; return x2 * x2 * x; }

// Fixed magnetic geometry; generated from the original float anchors and angles.
// xyz = basis; w = cos(width), sin(width), or phase offset respectively.
const vec4 archNormals[8] = vec4[8](
    vec4(0.9077335,0.199501872,0.369078457,0.971337974),
    vec4(-0.840799153,-0.180171251,0.510485172,0.980066597),
    vec4(0.210495234,0.862028182,0.461084813,0.990216017),
    vec4(-0.180027008,-0.930139542,0.320048004,0.97366637),
    vec4(0.270283967,0.15015775,0.950999081,0.988771081),
    vec4(-0.620248139,0.620248139,-0.480192095,0.971337974),
    vec4(0.610396922,-0.350227714,-0.710461915,0.982004225),
    vec4(-0.0907795131,0.988488019,-0.121039346,0.99156189));
const vec4 archTangents[8] = vec4[8](
    vec4(-0.274949431,-0.381590039,0.882491827,0.237702623),
    vec4(0.394945323,-0.849083185,0.350821733,0.198669329),
    vec4(0.00403164607,-0.472413719,0.881367683,0.139543116),
    vec4(-0.195423409,0.352698088,0.915103137,0.227977529),
    vec4(0.887175858,-0.422534227,-0.185428798,0.149438143),
    vec4(0.0746814981,-0.562698185,-0.823282123,0.237702623),
    vec4(-0.0959918723,-0.92303586,0.372545779,0.188858896),
    vec4(-0.805158079,-0.00132365827,0.593058765,0.129634142));
const vec4 archBinormals[8] = vec4[8](
    vec4(0.316895425,-0.902545333,-0.291529149,0.400000006),
    vec4(0.370236427,0.496584356,0.785066247,1.79999995),
    vec4(0.977586567,-0.183664769,-0.102916226,3.20000005),
    vec4(-0.964053929,0.102198415,-0.245266214,4.69999981),
    vec4(0.373986095,0.893821836,-0.247420564,1.10000002),
    vec4(-0.780842423,-0.546500683,0.30269143,5.5999999),
    vec4(-0.786257684,-0.159202218,-0.597037256,2.5),
    vec4(0.586071253,0.151293397,0.796009302,3.9000001));

float prominences(vec3 p, float phase)
{
    float density = 0.0;
    for (int j = 0; j < 8; ++j)
    {
        vec4 n = archNormals[j], tangent = archTangents[j], binormal = archBinormals[j];
        float height = j < 4 ? uMaterial._StellarArchHeight0[j] : uMaterial._StellarArchHeight1[j - 4];
        float radial = dot(p, n.xyz) - n.w;
        float along = dot(p, tangent.xyz);
        float across = dot(p, binormal.xyz);
        float arch = length(vec2(radial / height, along / tangent.w));
        // S1(d): cheap reject. Outside these bounds the tube exp() is < 8.5e-8 of its peak
        // (per-arch bound exp(-((0.40-1)/.14)^2)=1.06e-8; measured max |Δdensity| 6.2e-9).
        if (abs(across) > .15 || arch < .40 || arch > 1.60) continue;
        float tube = exp(-square((arch - 1.0) / .14) - square(across / .032));
        float wisps = .65 + .35 * sin(along * 97.0 + phase * 19.0 + binormal.w);
        density += tube * smoothstep(-.025, .015, radial) * wisps;
    }
    return density;
}

// ── S3-a constants/helpers (all values fixed; no new uniforms) ──
const float SHELL = 1.65;
const float OUTER = 1.42;
const float OUTER_STEP = .12;
// Surface cull: max photosphere extent is 1 + (0.028+0.0025+0.15) = 1.1805R. Offline field scan
// must prove no r > SURFACE_CULL_IMPACT ray reaches the .995 partition (set to 1.42 to disable).
// 1.22 was chosen after the 1.19 boundary failed the pre-registered max-opacity < 0.90 gate
// (dense re-scan 0.9086 @ impact 1.1905); the 1.22 band is re-scanned full before delivery.
const float SURFACE_CULL_IMPACT = 1.22;

// One legacy sample: identical field/emission math as PF_StellarVolume.frag.glsl.
struct StellarSample
{
    vec3 emission;
    float density;
};

StellarSample sampleStellar(vec3 p, vec3 ray, float phase, vec3 drift, float surfaceHeat, bool haloPass)
{
    float r = max(length(p), .0001);
    vec3 local = spin(p, -phase);
    vec3 q = local / r;
    float altitude = max(r - 1.0, 0.0);

    vec3 warp = vec3(noise(q * 7.0 + drift), noise(q * 7.0 + drift.yzx + 7.0),
        noise(q * 7.0 + drift.zxy + 9.0)) - .5;
    float stream = fbm(q * 24.0 + warp * 3.0 + vec3(altitude * 5.0, drift.y * .28, altitude * 3.0));
    float filament = cube(smoothstep(.30, .70, stream));
    float envelope = 1.0 - smoothstep(1.17, 1.62, r);
    float corona = uMaterial._StellarTime.z * exp(-altitude * 10.5) * (.018 + filament * .84) * envelope;

    // S1(b): halo far-region pruning. height <= 0.0305 + 0.15*filament, so above this radius
    // photoDensity <= 32*exp(-43*0.2412) < 1e-3 and the photosphere terms are safe to skip
    // (model: Δopacity <=1e-4, ΔRGB <=0.2/255). Keep warp+stream (7 noise) + loops; skip
    // convection/gran/fine (6 noise) and the photosphere/chromosphere emission.
    if (haloPass && r > 1.0 + (0.0305 + 0.15 * filament) + 0.2412)
    {
        float farLoops = (r > .98 && r < 1.42) ? prominences(local, phase) * uMaterial._StellarTime.w * 5.0 : 0.0;
        return StellarSample(vec3(4.6,1.10,.13) * (.65 + stream * .7), corona + farLoops);
    }

    float convection = fbm(q * 19.0 + warp * 2.1 + drift * .2);
    float gran = noise(q * 66.0 + warp * 4.3 + drift);
    float fine = noise(q * 173.0 + warp * 2.0);
    float height = (convection - .5) * .056 + (gran - .5) * .005 + uMaterial._StellarTime.z * .075 * filament;
    float photoDensity = 32.0 * exp(clamp((1.0 - r + height) * 43.0, -22.0, 5.0));
    float loopsDensity = (r > .98 && r < 1.42) ? prominences(local, phase) * uMaterial._StellarTime.w * 5.0 : 0.0;
    float density = photoDensity + corona + loopsDensity;

    // surfaceHeat comes from the same prologue (cached or analytic) as legacy.
    float heat = surfaceHeat * (.94 + .12 * convection);
    float spot = pow(max(dot(q, normalize(vec3(-.44,.24,.85))), 0.0), 900.0);
    heat *= 1.0 - spot * .28;
    vec3 photosphere = vec3(2.70,1.60,.50) * heat + vec3(.85,.65,.27) * fifth(fine);
    float cooling = smoothstep(.975, 1.13, r - height);
    vec3 chromosphere = mix(photosphere, vec3(3.5,.82,.09) * heat, cooling * .80);
    vec3 coronaColor = vec3(4.6,1.10,.13) * (.65 + stream * .7);
    vec3 emission = mix(coronaColor, chromosphere, photoDensity / max(density, .00001));
    return StellarSample(emission, density);
}

// Phase-aligned base step: exactly legacy's `max(impact<1.10 ? .019 : .041, (end-start)/budget)`.
float stellarStep(float impact, float span, float budget)
{
    return max(impact < 1.10 ? .019 : .041, span / budget);
}

// Phase-aligned skip of the r > OUTER lead-in: advances by whole legacy steps so the dense
// sampling grid/phase below stays bit-identical to the legacy shader.
float skipOuterEntry(vec3 eye, vec3 ray, float start, float baseStep, float impact)
{
    float tIn = start;
    // Only inward rays (camera outside the shell, or inside it looking in) have an r > OUTER lead-in.
    // Outward rays start outside OUTER and must keep the legacy march from t = start.
    if (impact <= OUTER && dot(eye, ray) < 0.0)
    {
        for (int i = 0; i < 64; ++i)
        {
            if (length(eye + tIn * ray) <= OUTER) break;
            tIn += baseStep;
        }
    }
    return tIn;
}

// Coarse 3-sample integration of an outer (r > OUTER) tail. Same per-sample field/emission model;
// only the step size is coarse (the outer corona contributes ~1e-3 of the radiance).
void integrateCoarse(vec3 eye, vec3 ray, float t0, float t1, float phase, vec3 drift,
    float surfaceHeat, bool haloPass, inout vec3 radiance, inout float opacity, inout float depthT, inout float opaqueT)
{
    float len = t1 - t0;
    if (len <= 0.0) return;
    for (int j = 0; j < 3; ++j)
    {
        float tm = t0 + len * (float(j) + .5) / 3.0;
        vec3 p = eye + tm * ray;
        StellarSample s = sampleStellar(p, ray, phase, drift, surfaceHeat, haloPass);
        float wgt = len / 3.0;
        float alpha = 1.0 - exp(-s.density * wgt);
        radiance += (1.0 - opacity) * s.emission * alpha;
        opacity += (1.0 - opacity) * alpha;
        if (depthT < 0.0 && opacity >= .01) depthT = tm;
        if (opaqueT < 0.0 && opacity >= .5) opaqueT = tm;
    }
}

void main()
{
    vec3 eye = uMaterial._StellarEye.xyz;
    vec3 ray = normalize(vLocalPosition - eye);
    float b = dot(eye, ray), impact = length(cross(eye, ray));
    bool haloPass = uMaterial._StellarParams.y > .5;
    if (b >= 0.0 && length(eye) >= 1.65) discard;
    if (!haloPass && impact > 1.42) discard;
    // Forward rays through this inner region are already opaque in the surface pass,
    // including when the camera is inside the corona. Outward rays must still integrate.
    if (haloPass && b < 0.0 && impact < .92) discard;

    float phase = uMaterial._StellarTime.x;
    vec3 drift = uMaterial._StellarDrift.xyz;

    // S3-a only: surface pass skips the transparent corona ring; must be backed by an offline
    // field scan showing no opacity >= .995 for r > SURFACE_CULL_IMPACT (see shader header).
    if (!haloPass && impact > SURFACE_CULL_IMPACT) discard;

    float opacity = 0.0, depthT = -1.0, opaqueT = -1.0;
    vec3 radiance = vec3(0.0);

    // Legacy computes h/start/end and the prologue only when the ray intersects the proxy shell;
    // keep that ordering so h <= 0 rays pay no prologue cost (they only get the analytic glow).
    float h = SHELL * SHELL - impact * impact;
    if (h > 0.0)
    {
        float start = max(0.0, -b - sqrt(h));
        float end = -b + sqrt(h);
        float budget = clamp(uMaterial._StellarParams.x, 4.0, 64.0);

        // ── prologue: identical to legacy (heat cache branch) ──
        float photoT = max(0.0, -b - sqrt(max(1.04 * 1.04 - impact * impact, 0.0)));
        vec3 photoQ = spin(normalize(eye + photoT * ray), -phase);
        float surfaceHeat;
        if (uMaterial._StellarEye.w > .5)
        {
            surfaceHeat = cachedSurfaceHeat(photoQ);
        }
        else
        {
            vec3 photoWarp = vec3(noise(photoQ * 7.0 + drift), noise(photoQ * 7.0 + drift.yzx + 7.0),
                noise(photoQ * 7.0 + drift.zxy + 9.0)) - .5;
            float cells = granulation(photoQ * 49.0 + photoWarp * 1.9 + drift * 1.1);
            surfaceHeat = .59 + .48 * cells + .09 * noise(photoQ * 130.0 + photoWarp * 2.0);
        }

        float span = end - start;
        float baseStep = stellarStep(impact, span, budget);

        // Phase-aligned skip of the r > OUTER lead-in: advance by whole legacy steps.
        float tIn = skipOuterEntry(eye, ray, start, baseStep, impact);
        integrateCoarse(eye, ray, start, tIn, phase, drift, surfaceHeat, haloPass, radiance, opacity, depthT, opaqueT);

        float t = tIn;
        for (int i = 0; i < 64; ++i)
        {
            if (t > end || opacity > .997) break;
            if (impact <= OUTER && (b + t) > 0.0 && length(eye + t * ray) > OUTER)
            {
                integrateCoarse(eye, ray, t, end, phase, drift, surfaceHeat, haloPass, radiance, opacity, depthT, opaqueT);
                break;
            }
            float step = impact > OUTER ? OUTER_STEP : baseStep;
            float tm = t + step * .5;
            vec3 p = eye + tm * ray;
            StellarSample s = sampleStellar(p, ray, phase, drift, surfaceHeat, haloPass);
            float alpha = 1.0 - exp(-s.density * step);
            radiance += (1.0 - opacity) * s.emission * alpha;
            opacity += (1.0 - opacity) * alpha;
            if (depthT < 0.0 && opacity >= .01) depthT = tm;
            if (opaqueT < 0.0 && opacity >= .5) opaqueT = tm;
            t += step;
        }
    }

    bool opaque = opacity >= .995;
    if (haloPass == opaque) discard;
    float glowFade = 1.0 - smoothstep(1.6, uMaterial._StellarParams.w, impact);
    float glow = exp(-pow(max(impact - 1.02, 0.0) / .22, 1.4)) * glowFade;
    radiance += vec3(.95,.24,.035) * uMaterial._StellarParams.z * glow;
    if (max(radiance.r, max(radiance.g, radiance.b)) < .00001) discard;

    // The visible plasma depth replaces the proxy sphere's back-face depth.
    float sampleT = opaque ? opaqueT : depthT;
    if (sampleT < 0.0) sampleT = max(0.0001, -b);
    vec3 modelPoint = (eye + ray * sampleT) / (2.0 * uMaterial._StellarParams.w);
    vec4 clip = vClip0 * modelPoint.x + vClip1 * modelPoint.y + vClip2 * modelPoint.z + vClip3;
    if (clip.w <= 0.0) discard;
    gl_FragDepth = clamp(clip.z / clip.w, 0.0, 1.0);
    vec3 color = pow(1.0 - exp(-max(radiance, vec3(0.0)) * .76), vec3(.4545));
    outColor = vec4(color, opaque ? 1.0 : clamp(opacity, 0.0, 1.0));
}

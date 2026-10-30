#version 450

// PFView stellar volume (P2 rewrite, 2026-10-28).
//
// Why this file changed shape:
//   The old implementation marched BOTH passes up to 64 steps through the whole 1.65R shell,
//   then split the result into "opaque" (surface) / "transparent" (halo) at .995 opacity.
//   P0 measured (RTX 4060 Laptop, 2048x1057): 53 ms with both passes vs 6.9 ms with both off at
//   2.35R; surface +21.7 ms / halo +19.7 ms. Most of that work was corona samples that contribute
//   almost nothing to the opaque photosphere.
//
// New structure:
//   * surface pass: find the photosphere surface analytically (ray/sphere + a height-field
//     correction) and shade it ONCE. No volume march at all.
//   * halo pass: integrate ONLY the transparent corona in front of that surface, using cheap
//     density (no photosphere convection/granulation terms), then add the analytic glow.
//   Both passes share `photosphereHit`, so the surface/halo boundary is identical by construction;
//   the old "same steps for both passes" seam constraint no longer applies.
//
// Visual anchors kept: granulation, 8 magnetic arches (`prominences`), chromosphere->corona mix,
// analytic glow, real fragment depth, surface depth write, halo no depth write.

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
    vec4 _StellarParams; // quality steps (64 = full), halo pass, glow strength, proxy extent
    vec4 _StellarDrift;
    vec4 _StellarArchHeight0;
    vec4 _StellarArchHeight1;
} uMaterial;

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
        // Cheap reject: outside these bounds the tube exp() is < 1e-8 of its peak (P1 safety pruning).
        if (abs(across) > 0.15 || arch < 0.40 || arch > 1.60) continue;
        float tube = exp(-square((arch - 1.0) / .14) - square(across / .032));
        float wisps = .65 + .35 * sin(along * 97.0 + phase * 19.0 + binormal.w);
        density += tube * smoothstep(-.025, .015, radial) * wisps;
    }
    return density;
}

const float SHELL = 1.65;
const float PHOTO_MARGIN = 0.02;

// Complete photosphere height field (same constants as the pre-P2 shader).
// Convection/gran/fine only affect the photosphere; corona uses them only through `height`.
float photosphereHeight(vec3 q, float phase)
{
    vec3 drift = uMaterial._StellarDrift.xyz;
    vec3 warp = vec3(noise(q * 7.0 + drift), noise(q * 7.0 + drift.yzx + 7.0),
        noise(q * 7.0 + drift.zxy + 9.0)) - .5;
    float convection = fbm(q * 19.0 + warp * 2.1 + drift * .2);
    float gran = noise(q * 66.0 + warp * 4.3 + drift);
    float stream = fbm(q * 24.0 + warp * 3.0 + vec3(0.0, drift.y * .28, 0.0));
    float filament = cube(smoothstep(.30, .70, stream));
    return (convection - .5) * .056 + (gran - .5) * .005 + uMaterial._StellarTime.z * .075 * filament;
}

// One surface sample: replicates the old photosphere/chromosphere/corona color at the hit point.
vec3 photosphereColor(vec3 q, vec3 local, float r, float phase)
{
    vec3 drift = uMaterial._StellarDrift.xyz;
    vec3 warp = vec3(noise(q * 7.0 + drift), noise(q * 7.0 + drift.yzx + 7.0),
        noise(q * 7.0 + drift.zxy + 9.0)) - .5;
    float convection = fbm(q * 19.0 + warp * 2.1 + drift * .2);
    float gran = noise(q * 66.0 + warp * 4.3 + drift);
    float fine = noise(q * 173.0 + warp * 2.0);
    float stream = fbm(q * 24.0 + warp * 3.0 + vec3(0.0, drift.y * .28, 0.0));
    float filament = cube(smoothstep(.30, .70, stream));
    float height = (convection - .5) * .056 + (gran - .5) * .005 + uMaterial._StellarTime.z * .075 * filament;
    float altitude = max(r - 1.0, 0.0);
    float photoDensity = 32.0 * exp(clamp((1.0 - r + height) * 43.0, -22.0, 5.0));
    float envelope = 1.0 - smoothstep(1.17, 1.62, r);
    float corona = uMaterial._StellarTime.z * exp(-altitude * 10.5) * (.018 + filament * .84) * envelope;
    float loopsDensity = (r > .98 && r < 1.42) ? prominences(local, phase) * uMaterial._StellarTime.w * 5.0 : 0.0;
    float density = photoDensity + corona + loopsDensity;

    float cells = granulation(q * 49.0 + warp * 1.9 + drift * 1.1);
    float surfaceHeat = .59 + .48 * cells + .09 * noise(q * 130.0 + warp * 2.0);
    float heat = surfaceHeat * (.94 + .12 * convection);
    float spot = pow(max(dot(q, normalize(vec3(-.44,.24,.85))), 0.0), 900.0);
    heat *= 1.0 - spot * .28;
    vec3 photosphere = vec3(2.70,1.60,.50) * heat + vec3(.85,.65,.27) * fifth(fine);
    float cooling = smoothstep(.975, 1.13, r - height);
    vec3 chromosphere = mix(photosphere, vec3(3.5,.82,.09) * heat, cooling * .80);
    vec3 coronaColor = vec3(4.6,1.10,.13) * (.65 + stream * .7);
    return mix(coronaColor, chromosphere, photoDensity / max(density, .00001));
}

// Ray/sphere hit at the photosphere, corrected by the height field. The nominal sphere (1.16R)
// bounds the maximum height bump (+0.15R); one fixed-point step then lands on the local surface.
bool photosphereHit(vec3 eye, vec3 ray, out float tHit)
{
    float R = 1.16;
    float b = dot(eye, ray);
    float c = dot(eye, eye) - R * R;
    float disc = b * b - c;
    if (disc <= 0.0) return false;
    float t = -b - sqrt(disc);
    if (t < 0.0) t = 0.0;

    vec3 p = eye + t * ray;
    float r = max(length(p), .0001);
    vec3 q = spin(p, -uMaterial._StellarTime.x) / r;
    float height = photosphereHeight(q, uMaterial._StellarTime.x);
    float target = 1.0 + height - PHOTO_MARGIN;
    float denom = max(dot(normalize(p), ray), 0.2);
    t = max(0.0, t + (r - target) / denom);
    // Grazing rays that the local height field cannot actually reach must not invent a surface.
    float r2 = length(eye + t * ray);
    if (abs(r2 - target) > 0.06) return false;
    tHit = t;
    return true;
}

void writeDepth(vec3 eye, vec3 ray, float t)
{
    vec3 modelPoint = (eye + ray * t) / (2.0 * uMaterial._StellarParams.w);
    vec4 clip = vClip0 * modelPoint.x + vClip1 * modelPoint.y + vClip2 * modelPoint.z + vClip3;
    if (clip.w <= 0.0) discard;
    gl_FragDepth = clamp(clip.z / clip.w, 0.0, 1.0);
}

void main()
{
    vec3 eye = uMaterial._StellarEye.xyz;
    vec3 ray = normalize(vLocalPosition - eye);
    float b = dot(eye, ray), impact = length(cross(eye, ray));
    bool haloPass = uMaterial._StellarParams.y > .5;

    // Rays that leave the shell without ever approaching the star contribute nothing.
    // (literal 1.65, not SHELL, so the static guard test can evaluate this line directly)
    if (b >= 0.0 && length(eye) >= 1.65) discard;
    // Surface pass only covers the photosphere; the corona ring is the halo pass's job.
    if (!haloPass && impact > 1.42) discard;

    float phase = uMaterial._StellarTime.x;
    vec3 drift = uMaterial._StellarDrift.xyz;

    if (!haloPass)
    {
        float tHit;
        if (!photosphereHit(eye, ray, tHit)) discard;
        vec3 p = eye + ray * tHit;
        float r = length(p);
        vec3 local = spin(p, -phase);
        vec3 q = local / max(r, .0001);
        // The photosphere is opaque by construction (ray/sphere + height correction); shade once.
        // The thin corona in front of it is drawn by the halo pass (queue 2900, alpha blend).
        vec3 radiance = photosphereColor(q, local, r, phase);
        vec3 color = pow(1.0 - exp(-max(radiance, vec3(0.0)) * .76), vec3(.4545));
        writeDepth(eye, ray, tHit);
        outColor = vec4(color, 1.0);
        return;
    }

    // ── halo pass: transparent corona in front of the photosphere ──
    float h = SHELL * SHELL - impact * impact;
    float glowFade = 1.0 - smoothstep(1.6, uMaterial._StellarParams.w, impact);
    float glow = exp(-pow(max(impact - 1.02, 0.0) / .22, 1.4)) * glowFade;
    vec3 radiance = vec3(0.0);
    float opacity = 0.0;
    float depthT = max(0.0, -b);

    if (h > 0.0)
    {
        float start = max(0.0, -b - sqrt(h));
        float end = -b + sqrt(h);
        depthT = start;   // front-most shell point (ships in front must still occlude the corona)
        float tHit;
        if (impact < 1.30 && photosphereHit(eye, ray, tHit)) end = min(end, tHit);
        float chord = max(end - start, 0.0);
        if (chord > 0.0)
        {
            // Quality scale: 64 (default) = full; debug/performance tiers lower the cap only.
            float quality = clamp(uMaterial._StellarParams.x / 64.0, .25, 1.0);
            float steps = clamp(ceil(chord / .12), 8.0, 24.0) * quality;
            steps = max(steps, 6.0);
            float stepSize = chord / steps;
            // Warp is smooth along a ray; sample it once per fragment and reuse for all corona steps.
            vec3 first = eye + (start + stepSize * .5) * ray;
            vec3 firstQ = spin(first, -phase) / max(length(first), .0001);
            vec3 warp = vec3(noise(firstQ * 7.0 + drift), noise(firstQ * 7.0 + drift.yzx + 7.0),
                noise(firstQ * 7.0 + drift.zxy + 9.0)) - .5;
            float t = start + stepSize * .5;
            for (int i = 0; i < 24; ++i)
            {
                if (float(i) >= steps || t > end) break;
                vec3 p = eye + t * ray;
                float r = length(p);
                float altitude = max(r - 1.0, 0.0);
                vec3 local = spin(p, -phase);
                vec3 q = local / max(r, .0001);
                float stream = fbm(q * 24.0 + warp * 3.0 + vec3(altitude * 5.0, drift.y * .28, altitude * 3.0));
                float filament = cube(smoothstep(.30, .70, stream));
                float envelope = 1.0 - smoothstep(1.17, 1.62, r);
                float corona = uMaterial._StellarTime.z * exp(-altitude * 10.5) * (.018 + filament * .84) * envelope;
                float loopsDensity = (r > .98 && r < 1.42) ? prominences(local, phase) * uMaterial._StellarTime.w * 5.0 : 0.0;
                float density = corona + loopsDensity;
                float alpha = 1.0 - exp(-density * stepSize);
                vec3 coronaColor = vec3(4.6,1.10,.13) * (.65 + stream * .7);
                radiance += (1.0 - opacity) * coronaColor * alpha;
                opacity += (1.0 - opacity) * alpha;
                if (opacity > .997) break;
                t += stepSize;
            }
        }
    }

    radiance += vec3(.95,.24,.035) * uMaterial._StellarParams.z * glow;
    if (opacity < .0001 && max(radiance.r, max(radiance.g, radiance.b)) < .00001) discard;
    writeDepth(eye, ray, depthT);
    vec3 color = pow(1.0 - exp(-max(radiance, vec3(0.0)) * .76), vec3(.4545));
    outColor = vec4(color, clamp(opacity, 0.0, 1.0));
}

#version 450

// Approved continuous plasma field, sampled in the real stellar sphere's local coordinates.
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
        float tube = exp(-square((arch - 1.0) / .14) - square(across / .032));
        float wisps = .65 + .35 * sin(along * 97.0 + phase * 19.0 + binormal.w);
        density += tube * smoothstep(-.025, .015, radial) * wisps;
    }
    return density;
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
    // At r <= .95, the minimum photosphere density leaves a wide margin above .995 opacity.
    if (haloPass && b < 0.0 && impact < .92) discard;

    float phase = uMaterial._StellarTime.x;
    vec3 drift = uMaterial._StellarDrift.xyz;
    float opacity = 0.0, depthT = -1.0, opaqueT = -1.0;
    vec3 radiance = vec3(0.0);
    float h = 1.65 * 1.65 - impact * impact;
    if (h > 0.0)
    {
        float start = max(0.0, -b - sqrt(h)), end = -b + sqrt(h);
        float photoT = max(0.0, -b - sqrt(max(1.04 * 1.04 - impact * impact, 0.0)));
        vec3 photoQ = spin(normalize(eye + photoT * ray), -phase);
        vec3 photoWarp = vec3(noise(photoQ * 7.0 + drift), noise(photoQ * 7.0 + drift.yzx + 7.0),
            noise(photoQ * 7.0 + drift.zxy + 9.0)) - .5;
        float cells = granulation(photoQ * 49.0 + photoWarp * 1.9 + drift * 1.1);
        float surfaceHeat = .59 + .48 * cells + .09 * noise(photoQ * 130.0 + photoWarp * 2.0);
        // Both passes use identical steps, so the opacity partition cannot leave a seam.
        // 2026-10-28: this is the LEGACY (default) path = the visual users approved before P2.
        // The P0 clamp lower bound is 4 so the debug probe can still A/B step budgets; default is 64.
        float budget = clamp(uMaterial._StellarParams.x, 4.0, 64.0);
        float stepSize = max(impact < 1.10 ? .019 : .041, (end - start) / budget);
        float t = start + stepSize * .5;
        for (int i = 0; i < 64; ++i)
        {
            if (float(i) >= budget || t > end || opacity > .997) break;
            vec3 p = eye + t * ray;
            float r = length(p);
            vec3 local = spin(p, -phase), q = local / max(r, .0001);
            vec3 warp = vec3(noise(q * 7.0 + drift), noise(q * 7.0 + drift.yzx + 7.0),
                noise(q * 7.0 + drift.zxy + 9.0)) - .5;
            float convection = fbm(q * 19.0 + warp * 2.1 + drift * .2);
            float gran = noise(q * 66.0 + warp * 4.3 + drift);
            float fine = noise(q * 173.0 + warp * 2.0);
            float altitude = max(r - 1.0, 0.0);
            float stream = fbm(q * 24.0 + warp * 3.0 + vec3(altitude * 5.0, drift.y * .28, altitude * 3.0));
            float filament = cube(smoothstep(.30, .70, stream));
            float height = (convection - .5) * .056 + (gran - .5) * .005 + uMaterial._StellarTime.z * .075 * filament;
            float photoDensity = 32.0 * exp(clamp((1.0 - r + height) * 43.0, -22.0, 5.0));
            float envelope = 1.0 - smoothstep(1.17, 1.62, r);
            float corona = uMaterial._StellarTime.z * exp(-altitude * 10.5) * (.018 + filament * .84) * envelope;
            float loopsDensity = (r > .98 && r < 1.42) ? prominences(local, phase) * uMaterial._StellarTime.w * 5.0 : 0.0;
            float density = photoDensity + corona + loopsDensity;
            float alpha = 1.0 - exp(-density * stepSize);
            float heat = surfaceHeat * (.94 + .12 * convection);
            float spot = pow(max(dot(q, normalize(vec3(-.44,.24,.85))), 0.0), 900.0);
            heat *= 1.0 - spot * .28;
            vec3 photosphere = vec3(2.70,1.60,.50) * heat + vec3(.85,.65,.27) * fifth(fine);
            float cooling = smoothstep(.975, 1.13, r - height);
            vec3 chromosphere = mix(photosphere, vec3(3.5,.82,.09) * heat, cooling * .80);
            vec3 coronaColor = vec3(4.6,1.10,.13) * (.65 + stream * .7);
            vec3 emission = mix(coronaColor, chromosphere, photoDensity / max(density, .00001));
            radiance += (1.0 - opacity) * emission * alpha;
            opacity += (1.0 - opacity) * alpha;
            if (depthT < 0.0 && opacity >= .01) depthT = t;
            if (opaqueT < 0.0 && opacity >= .5) opaqueT = t;
            t += stepSize;
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

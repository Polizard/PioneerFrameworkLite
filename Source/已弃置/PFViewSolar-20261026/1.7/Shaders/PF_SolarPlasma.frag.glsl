#version 450

// Approved plasma preview: one continuous emitting/absorbing field, no solid sphere pass.
// Camera rays are supplied in inertial solar-radius units; there are no external textures.
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _SolarEye;
    vec4 _SolarRight;
    vec4 _SolarUp;
    vec4 _SolarForward;
    vec4 _SolarGeometry; // x=vertical half-tangent, y=aspect, w=step budget
    vec4 _SolarTime; // x=periodic phase, y=flow, z=corona, w=prominences
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

const vec3 anchors[8] = vec3[8](vec3(.91,.20,.37), vec3(-.84,-.18,.51),
    vec3(.21,.86,.46), vec3(-.18,-.93,.32), vec3(.27,.15,.95),
    vec3(-.62,.62,-.48), vec3(.61,-.35,-.71), vec3(-.09,.98,-.12));
const vec4 loops[8] = vec4[8](vec4(.4,.24,.27,.4), vec4(2.1,.20,.31,1.8),
    vec4(1.2,.14,.22,3.2), vec4(.2,.23,.23,4.7), vec4(2.7,.15,.19,1.1),
    vec4(.8,.24,.28,5.6), vec4(1.4,.19,.23,2.5), vec4(2.2,.13,.17,3.9));

float prominences(vec3 p, float phase)
{
    float density = 0.0;
    for (int j = 0; j < 8; ++j)
    {
        vec3 n = normalize(anchors[j]);
        vec3 side = normalize(cross(n, abs(n.y) > .9 ? vec3(1,0,0) : vec3(0,1,0)));
        vec3 tangent = side * cos(loops[j].x) + cross(n, side) * sin(loops[j].x);
        vec3 binormal = cross(n, tangent);
        float height = loops[j].z * (.85 + .15 * sin(phase * 7.0 + loops[j].w));
        float radial = dot(p, n) - cos(loops[j].y);
        float along = dot(p, tangent);
        float across = dot(p, binormal);
        float arch = length(vec2(radial / height, along / sin(loops[j].y)));
        float tube = exp(-pow((arch - 1.0) / .14, 2.0) - pow(across / .032, 2.0));
        float wisps = .65 + .35 * sin(along * 97.0 + phase * 19.0 + loops[j].w);
        density += tube * smoothstep(-.025, .015, radial) * wisps;
    }
    return density;
}

void main()
{
    vec2 screen = vUv * 2.0 - 1.0;
    vec3 eye = uMaterial._SolarEye.xyz;
    vec3 ray = normalize(uMaterial._SolarForward.xyz + uMaterial._SolarGeometry.x *
        (screen.x * uMaterial._SolarGeometry.y * uMaterial._SolarRight.xyz + screen.y * uMaterial._SolarUp.xyz));
    float b = dot(eye, ray), impact = length(cross(eye, ray));
    float h = 1.65 * 1.65 - impact * impact;
    if (h <= 0.0 || b >= 0.0) { outColor = vec4(0.0); return; }

    float phase = uMaterial._SolarTime.x;
    float flow = phase * 17.0 * uMaterial._SolarTime.y;
    // Integer harmonics join exactly when the bounded clock wraps.
    vec3 drift = vec3(sin(flow), cos(flow), sin(flow * 2.0 + 1.3));
    float photoT = -b - sqrt(max(1.04 * 1.04 - impact * impact, 0.0));
    vec3 photoQ = spin(normalize(eye + photoT * ray), -phase);
    vec3 photoWarp = vec3(noise(photoQ * 7.0 + drift), noise(photoQ * 7.0 + drift.yzx + 7.0),
        noise(photoQ * 7.0 + drift.zxy + 9.0)) - .5;
    float cells = granulation(photoQ * 49.0 + photoWarp * 1.9 + drift * 1.1);
    float surfaceHeat = .59 + .48 * cells + .09 * noise(photoQ * 130.0 + photoWarp * 2.0);
    float start = max(0.0, -b - sqrt(h)), end = -b + sqrt(h);
    float budget = clamp(uMaterial._SolarGeometry.w, 32.0, 112.0);
    float stepSize = max(impact < 1.10 ? .019 : .041, (end - start) / budget);
    float t = start + stepSize * .5, opacity = 0.0;
    vec3 radiance = vec3(0.0);
    for (int i = 0; i < 112; ++i)
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
        float filament = pow(smoothstep(.30, .70, stream), 3.0);
        float height = (convection - .5) * .056 + (gran - .5) * .005 + uMaterial._SolarTime.z * .075 * filament;
        float photoDensity = 32.0 * exp(clamp((1.0 - r + height) * 43.0, -22.0, 5.0));
        float envelope = 1.0 - smoothstep(1.17, 1.62, r);
        float corona = uMaterial._SolarTime.z * exp(-altitude * 10.5) * (.018 + filament * .84) * envelope;
        float loopsDensity = (r > .98 && r < 1.42) ? prominences(local, phase) * uMaterial._SolarTime.w * 5.0 : 0.0;
        float thinDensity = corona + loopsDensity;
        float density = photoDensity + thinDensity;
        float alpha = 1.0 - exp(-density * stepSize);
        float heat = surfaceHeat * (.94 + .12 * convection);
        float spot = pow(max(dot(q, normalize(vec3(-.44,.24,.85))), 0.0), 900.0);
        heat *= 1.0 - spot * .28;
        vec3 photosphere = vec3(2.70,1.60,.50) * heat + vec3(.85,.65,.27) * pow(fine, 5.0);
        float cooling = smoothstep(.975, 1.13, r - height);
        vec3 chromosphere = mix(photosphere, vec3(3.5,.82,.09) * heat, cooling * .80);
        vec3 coronaColor = vec3(4.6,1.10,.13) * (.65 + stream * .7);
        vec3 emission = mix(coronaColor, chromosphere, photoDensity / max(density, .00001));
        radiance += (1.0 - opacity) * emission * alpha;
        opacity += (1.0 - opacity) * alpha;
        t += stepSize;
    }
    outColor = vec4(radiance, opacity);
}

#version 450

// PFView stellar — S3-b offscreen composite (2026-11-20, experimental, default off).
//
// Why this file exists:
//   S3-b renders the star (surface + halo) with a dedicated stellarCamera into a HALF-RESOLUTION
//   RenderTexture on StarLayer, then composites that texture back into the 1.0x main RT with two
//   proxy-sphere draws:
//     * surface composite (Opaque, depth write): sample the star RT; because the RT's surface pixels
//       write alpha=1, only alpha>=0.999 is kept, and gl_FragDepth is an ANALYTIC approximation of
//       the legacy .5-opacity depth (offline scan: boundary r ~= 1.12R; limb rays fall back to the
//       tangent t=-b so the limb never punches a hole).
//     * halo composite (One / OneMinusSrcAlpha, no depth write): only alpha<0.999; gl_FragDepth is
//       the tangent point t=-b, which approximates the legacy halo's first .01-opacity depth for
//       limb rays far better than the 1.65R shell entry (that entry was 0.5-1.0R too close).
//   The star RT must be cleared to transparent black; the surface pass writes alpha=1 and the halo
//   pass accumulates (color emitted, alpha accumulated) exactly like the main-RT blend did.
//
// NOTE: the depth values below are deliberate approximations (offscreen color cannot carry depth).
//       Ship occlusion at the limb can differ from the legacy per-fragment sampleT; this is why
//       the mode is a default-off A/B switch (PFViewStellarProbe.S3BEnabled).

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
layout(set = 2, binding = 1) uniform sampler2D _StellarStarTex;

// The star RT is exactly half the main RT in each dimension; textureSize gives the RT size, so
// screen-space UV is gl_FragCoord / (2 * starSize). The 2x factor is the S3-b downsample ratio.
vec2 starUv()
{
    return clamp(gl_FragCoord.xy / (2.0 * vec2(textureSize(_StellarStarTex, 0))), vec2(0.0), vec2(1.0));
}

void writeDepth(vec3 modelPoint)
{
    vec4 clip = vClip0 * modelPoint.x + vClip1 * modelPoint.y + vClip2 * modelPoint.z + vClip3;
    gl_FragDepth = clamp(clip.z / clip.w, 0.0, 1.0);
}

void main()
{
    vec3 eye = uMaterial._StellarEye.xyz;
    vec3 ray = normalize(vLocalPosition - eye);
    float b = dot(eye, ray);
    float impact = length(cross(eye, ray));
    vec4 star = texture(_StellarStarTex, starUv());
    bool haloPass = uMaterial._StellarParams.y > .5;

    if (!haloPass)
    {
        // Surface composite: only pixels the star surface actually filled (alpha=1 in the RT).
        if (star.a < .999) discard;

        // Analytic photosphere depth. Offline legacy-64 scan (task-16, 2026-11-20): the .5-opacity
        // boundary sits at r ~= 1.11-1.14R for both face-on and limb rays (opacity accumulates before
        // the geometric surface), so a 1.12R sphere matches it to a few mR. Rays outside that sphere
        // are limb/height rays; their .5 boundary is essentially the tangent point, so use t=-b.
        const float PHOTO_DEPTH_R = 1.12;
        float t = -b - sqrt(max(PHOTO_DEPTH_R * PHOTO_DEPTH_R - impact * impact, 0.0));
        t = max(t, 0.0001);
        writeDepth(eye + ray * t);
        outColor = vec4(star.rgb, 1.0);
        return;
    }

    // Halo composite: surface pixels are owned by the surface pass.
    if (star.a >= .999) discard;
    if (star.a < 1e-4 && max(star.r, max(star.g, star.b)) < 1e-5) discard;

    // Legacy halo depthT = first sample with opacity >= .01. Offline scan: for limb halo rays this
    // happens near the tangent point (density rises toward closest approach), not at the 1.65R shell
    // entry (the entry is 0.5-1.0R too close and would wrongly draw the corona over ships). Use t=-b.
    float t = max(-b, 0.0001);
    writeDepth(eye + ray * t);
    outColor = vec4(star.rgb, clamp(star.a, 0.0, 1.0));
}

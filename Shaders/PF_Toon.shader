// PioneerFramework Toon Shader (Cel Shading / 三渲二)
// Target: Unity 2022.3.35f1 Built-in Render Pipeline（工具链钉的版本；旧注释写的 2019.4 已过时）
// 将此文件放入 Unity 项目中，由 tools/PFViewShaderBundle/Build-PFViewToonBundles.ps1
// 构建到 AssetBundle 后放入 mod 的 AssetBundles/ 目录（散装 .shader 运行时不可达）。
//
// V-1.4（2026-10-02，用户："要渐变色、但渐变像阶梯一样分明"）：明暗从"二分"改为
// **N 级阶梯量化**（默认 4 级）——级内纯色、级间微分软化抗锯齿；`_ShadowSteps` 可调。

Shader "PioneerFramework/Toon"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _ShadowThreshold ("Shadow Threshold", Range(0, 1)) = 0.05
        _ShadowSmoothness ("Shadow Smoothness", Range(0, 0.3)) = 0.06
        _ShadowSteps ("Shadow Steps", Range(2, 8)) = 4
        _ShadowColor ("Shadow Color", Color) = (0.15, 0.15, 0.2, 1)
        _OutlineColor ("Outline Color", Color) = (0, 0, 0, 1)
        _OutlineWidth ("Outline Width", Range(0, 0.08)) = 0.03
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        // ── Outline Pass ──
        // 使用 inverted hull 方法：渲染背面 + 沿法线方向挤出顶点
        Pass
        {
            Name "OUTLINE"
            Cull Front
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _OutlineWidth;
            float4 _OutlineColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // 沿法线挤出
                float3 normal = normalize(v.normal);
                float3 extruded = v.vertex.xyz + normal * _OutlineWidth;
                o.pos = UnityObjectToClipPos(extruded);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return _OutlineColor;
            }
            ENDCG
        }

        // ── Main Toon Pass ──
        // N 级阶梯渐变：半兰伯特 → 重映射 → 量化到 _ShadowSteps 级（级内纯色、级间软化抗锯齿）
        Pass
        {
            Name "TOON"
            Tags { "LightMode"="ForwardBase" }
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            float4 _Color;
            float4 _ShadowColor;
            float _ShadowThreshold;
            float _ShadowSmoothness;
            float _ShadowSteps;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                SHADOW_COORDS(2)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                TRANSFER_SHADOW(o);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 normal = normalize(i.worldNormal);
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);

                // 半兰伯特 [0, 1]
                float halfLambert = dot(normal, lightDir) * 0.5 + 0.5;

                // V-1.4：先重映射到 [_ShadowThreshold, 1] → [0, 1]，再**量化成 N 级阶梯**。
                // 级内纯色（赛璐璐上色），级间用 _ShadowSmoothness 轻微软化抗锯齿——
                // "能看到渐变，但一级一级分明"。
                float span = max(1e-4, 1.0 - _ShadowThreshold);
                float t = saturate((halfLambert - _ShadowThreshold) / span);

                float levels = max(2.0, _ShadowSteps);
                float scaled = t * (levels - 1.0);
                float bandIndex = floor(scaled);
                float bandFrac = scaled - bandIndex;
                float bandBlend = smoothstep(0.5 - _ShadowSmoothness, 0.5 + _ShadowSmoothness, bandFrac);
                float toon = (bandIndex + bandBlend) / (levels - 1.0);

                // 阴影衰减（接收 Unity 阴影贴图）
                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                toon *= atten;

                // 暗面 → 亮面：阶梯式渐变
                float3 col = lerp(_ShadowColor.rgb, _Color.rgb, toon);

                // 微弱环境光，避免暗面全黑
                col += _Color.rgb * 0.03;

                return float4(col, 1);
            }
            ENDCG
        }

        // ── Shadow Caster Pass ──
        // 让物体能投射和接收阴影
        Pass
        {
            Name "SHADOWCASTER"
            Tags { "LightMode"="ShadowCaster" }
            Cull Back
            ZWrite On
            ZTest LEqual
            Offset 1, 1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                V2F_SHADOW_CASTER;
            };

            v2f vert(appdata v)
            {
                v2f o;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                SHADOW_CASTER_FRAGMENT(i);
            }
            ENDCG
        }
    }

    FallBack "Diffuse"
}

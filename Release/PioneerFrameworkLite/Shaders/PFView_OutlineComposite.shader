// PFView outer-ring overlay.
// Subtracting the original union mask removes ship interiors and part seams.
Shader "Hidden/PioneerFramework/PFView/OutlineComposite"
{
    Properties
    {
        _MainTex ("Dilated Mask", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" }

        Pass
        {
            Name "COMPOSITE"
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            // These values are frame-local CommandBuffer globals. Keep them
            // outside Properties: a Material default would take precedence
            // and replace the original mask with a black fallback texture.
            sampler2D _PFViewOriginalMask;
            fixed4 _PFViewOutlineColor;
            int _PFViewOutlineRadius;
            #define PFVIEW_DISTANCE_SCALE (1.0 / 128.0)

            fixed4 frag(v2f_img input) : SV_Target
            {
                float encodedDistance = tex2D(_MainTex, input.uv).r;
                float original = tex2D(
                    _PFViewOriginalMask,
                    input.uv).r;
                int radius = min(max(_PFViewOutlineRadius, 0), 8);
                int radiusSquared = radius * radius;
                float diskThreshold =
                    1.0 -
                    radiusSquared * PFVIEW_DISTANCE_SCALE -
                    (1.0 / 255.0);
                float dilated = step(
                    diskThreshold,
                    encodedDistance);
                float outline = saturate(dilated - original);

                return fixed4(
                    _PFViewOutlineColor.rgb,
                    _PFViewOutlineColor.a * outline);
            }
            ENDCG
        }
    }

    FallBack Off
}

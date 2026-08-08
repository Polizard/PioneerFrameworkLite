// PFView circular squared-distance mask expansion.
// Width is expressed directly in source RenderTexture pixels. The horizontal
// pass encodes dx^2 and the vertical pass adds dy^2, allowing the composite
// shader to threshold an approximately Euclidean disk without a 17x17 kernel.
Shader "Hidden/PioneerFramework/PFView/OutlineDilate"
{
    Properties
    {
        _MainTex ("Mask", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Cull Off
        ZWrite Off
        ZTest Always

        CGINCLUDE
        #pragma target 3.0
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;
        int _PFViewOutlineRadius;
        #define PFVIEW_DISTANCE_SCALE (1.0 / 128.0)

        fixed4 fragHorizontalDistance(v2f_img input) : SV_Target
        {
            int radius = min(max(_PFViewOutlineRadius, 0), 8);
            float maximumEncodedDistance = 0;

            [loop]
            for (int offset = -radius; offset <= radius; offset++)
            {
                float2 sampleUv = saturate(
                    input.uv +
                    float2(_MainTex_TexelSize.x * offset, 0));
                float encodedDistance =
                    tex2D(_MainTex, sampleUv).r -
                    offset * offset * PFVIEW_DISTANCE_SCALE;
                maximumEncodedDistance = max(
                    maximumEncodedDistance,
                    encodedDistance);
            }

            return fixed4(maximumEncodedDistance, 0, 0, 1);
        }

        fixed4 fragVerticalDistance(v2f_img input) : SV_Target
        {
            int radius = min(max(_PFViewOutlineRadius, 0), 8);
            float maximumEncodedDistance = 0;

            [loop]
            for (int offset = -radius; offset <= radius; offset++)
            {
                float2 sampleUv = saturate(
                    input.uv +
                    float2(0, _MainTex_TexelSize.y * offset));
                float encodedDistance =
                    tex2D(_MainTex, sampleUv).r -
                    offset * offset * PFVIEW_DISTANCE_SCALE;
                maximumEncodedDistance = max(
                    maximumEncodedDistance,
                    encodedDistance);
            }

            return fixed4(maximumEncodedDistance, 0, 0, 1);
        }
        ENDCG

        Pass
        {
            Name "HORIZONTAL_DISTANCE"

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragHorizontalDistance
            ENDCG
        }

        Pass
        {
            Name "VERTICAL_DISTANCE"

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragVerticalDistance
            ENDCG
        }
    }

    FallBack Off
}

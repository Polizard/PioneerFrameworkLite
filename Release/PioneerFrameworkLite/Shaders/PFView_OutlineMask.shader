// PFView visible-ship mask.
// Compile this shader into a RimWorld-compatible AssetBundle before use.
Shader "Hidden/PioneerFramework/PFView/OutlineMask"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            Name "MASK"
            ZTest LEqual
            ZWrite Off
            Cull Back
            ColorMask R

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                return fixed4(1, 0, 0, 1);
            }
            ENDCG
        }
    }

    FallBack Off
}

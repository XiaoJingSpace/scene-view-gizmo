Shader "Hidden/SceneViewGizmo/Solid"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Overlay"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest LEqual
            Cull Back
            Lighting Off
            Fog { Mode Off }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            float4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float3 normalView : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.normalView = mul((float3x3)UNITY_MATRIX_V, UnityObjectToWorldNormal(input.normal));
                return output;
            }

            float4 frag(v2f input) : SV_Target
            {
                float3 normal = normalize(input.normalView);
                float3 lightDir = normalize(float3(-0.2, 0.8, 0.45));
                float shade = saturate(dot(normal, lightDir)) * 0.58 + 0.42;
                return float4(_Color.rgb * shade, _Color.a);
            }
            ENDCG
        }
    }

    FallBack Off
}

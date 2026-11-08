Shader "Hidden/SceneViewGizmo/Grid"
{
    Properties
    {
        _LineColor ("Line Color", Color) = (1, 1, 1, 1)
        _CellSizeX ("Cell Size X", Float) = 1
        _CellSizeZ ("Cell Size Z", Float) = 1
        _CamFarPlaneDist ("Camera far plane", Float) = 1000
        _CamWorldPos ("Camera position", Vector) = (0, 0, 0, 0)
        _GridScale ("Grid scale", Vector) = (1, 1, 0, 0)
        _GridFrac ("Grid fraction", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual
            Offset -1, -1

            CGPROGRAM
            #include "UnityCG.cginc"
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.5

            float4 _LineColor;
            float _CellSizeX;
            float _CellSizeZ;
            float _CamFarPlaneDist;
            float3 _CamWorldPos;
            float4 _GridScale;
            float4 _GridFrac;

            struct vInput
            {
                float4 vertexPos : POSITION;
            };

            struct vOutput
            {
                float2 coords : TEXCOORD0;
                float3 viewPos : TEXCOORD1;
                float4 clipPos : SV_POSITION;
            };

            vOutput vert(vInput input)
            {
                vOutput output;
                output.clipPos = UnityObjectToClipPos(input.vertexPos);
                output.viewPos = mul(UNITY_MATRIX_MV, input.vertexPos).xyz;
                float2 local = input.vertexPos.xz * _GridScale.xy;
                output.coords = local * float2(1.0 / _CellSizeX, 1.0 / _CellSizeZ) + _GridFrac.xy;
                return output;
            }

            float CamFade(float3 viewPos, float camFarPlaneDist, float3 camWorldPos)
            {
                float farPlaneDist = camFarPlaneDist;
                farPlaneDist *= (0.15 * (1000.0 / camFarPlaneDist));
                farPlaneDist *= max(1.0, abs(camWorldPos.y) / 10.0);
                return saturate(1.0 - abs(viewPos.z) / farPlaneDist);
            }

            float4 frag(vOutput input) : SV_Target
            {
                float2 grid = abs(frac(input.coords - 0.5) - 0.5) / fwidth(input.coords);
                float stroke = min(grid.x, grid.y);
                float fade = CamFade(input.viewPos, _CamFarPlaneDist, _CamWorldPos);
                return float4(_LineColor.rgb, fade * _LineColor.a * (1.0 - min(stroke, 1.0)));
            }
            ENDCG
        }
    }
}

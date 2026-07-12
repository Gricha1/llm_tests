Shader "LowPoly/SimpleWater"
{
    Properties
    {
        _WaterNormal("Water Normal", 2D) = "bump" {}
        _NormalScale("Normal Scale", Float) = 0.35
        _DeepColor("Deep Color", Color) = (0.12, 0.42, 0.82, 1)
        _ShalowColor("Shalow Color", Color) = (0.35, 0.72, 0.98, 0.92)
        _WaterSmoothness("Water Smoothness", Range(0, 1)) = 0.85
        _WaterSpecular("Water Specular", Range(0, 1)) = 0.35
        _Alpha("Alpha", Range(0, 1)) = 0.78
        _PondCircle("Pond Circle", Range(0, 1)) = 1
        _WavesAmplitude("Waves Amplitude", Float) = 0.01
        _WavesAmount("Waves Amount", Float) = 8
        [HideInInspector] _texcoord("", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_WaterNormal);
            SAMPLER(sampler_WaterNormal);

            CBUFFER_START(UnityPerMaterial)
                float4 _WaterNormal_ST;
                float _NormalScale;
                half4 _DeepColor;
                half4 _ShalowColor;
                half _WaterSmoothness;
                half _WaterSpecular;
                half _Alpha;
                half _PondCircle;
                half _WavesAmplitude;
                half _WavesAmount;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 viewDirWS : TEXCOORD3;
                half fogFactor : TEXCOORD4;
            };

            float3 SampleWaterNormal(float2 uv, float2 panSpeed)
            {
                float2 panUv = uv * _WaterNormal_ST.xy + _WaterNormal_ST.zw + _Time.y * panSpeed;
                float3 n = UnpackNormal(SAMPLE_TEXTURE2D(_WaterNormal, sampler_WaterNormal, panUv));
                n.xy *= _NormalScale;
                return normalize(n);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;

                float3 positionOS = input.positionOS.xyz;
                positionOS.y += sin((_WavesAmount * positionOS.z) + _Time.y) * _WavesAmplitude * input.normalOS.y;

                VertexPositionInputs vertexInput = GetVertexPositionInputs(positionOS);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.uv = input.uv;
                output.normalWS = normalInput.normalWS;
                output.viewDirWS = GetWorldSpaceViewDir(vertexInput.positionWS);
                output.fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalTS = SampleWaterNormal(input.uv, float2(-0.03, 0.0))
                    + SampleWaterNormal(input.uv, float2(0.04, 0.04));
                normalTS = normalize(normalTS);

                float3 normalWS = normalize(input.normalWS + normalTS);
                float3 viewDirWS = normalize(input.viewDirWS);

                half fresnel = pow(saturate(1.0 - dot(normalWS, viewDirWS)), 3.0);
                half3 albedo = lerp(_DeepColor.rgb, _ShalowColor.rgb, fresnel);

                Light mainLight = GetMainLight();
                float3 halfDir = normalize(mainLight.direction + viewDirWS);
                half specPower = lerp(16.0, 128.0, _WaterSmoothness);
                half spec = pow(saturate(dot(normalWS, halfDir)), specPower) * _WaterSpecular;

                half3 color = albedo + mainLight.color * spec;
                color += SampleSH(normalWS) * albedo * 0.35;
                color = MixFog(color, input.fogFactor);

                half alpha = _Alpha;
                if (_PondCircle > 0.01)
                {
                    float2 centered = input.uv - 0.5;
                    float dist = length(centered) * 2.0;
                    half roundMask = 1.0 - smoothstep(0.9, 1.0, dist);
                    alpha *= lerp(1.0, roundMask, _PondCircle);
                    if (alpha < 0.01)
                        discard;
                }

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

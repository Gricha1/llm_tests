Shader "Hidden/Sentis/SliceSet"
{
    Properties
    {
    }
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma multi_compile_local _ BLOCKWISE
            #pragma multi_compile_local _ INT
            #pragma vertex vert
            #pragma fragment frag

            #include "CommonVertexShader.cginc"
            #include "CommonPixelShader.cginc"

            DECLARE_TENSOR_BLOCK_STRIDE_O;

            #ifdef INT
            #define DTYPE4 int4
            #define DTYPE int
            DECLARE_TENSOR(X, int);
            DECLARE_TENSOR(V, int);
            #else
            #define DTYPE4 float4
            #define DTYPE float
            DECLARE_TENSOR(X, float);
            DECLARE_TENSOR(V, float);
            #endif
            DECLARE_TENSOR_BLOCK_STRIDE(X, DTYPE);
            DECLARE_TENSOR_BLOCK_STRIDE(V, DTYPE);

            uint StridesV[8];
            uint Starts[8];
            uint Steps[8];
            uint ShapeO[8];
            uint ShapeV[8];

            DTYPE4 frag(v2f i, UNITY_VPOS_TYPE screenPos : VPOS) : SV_Target
            {
                DTYPE4 v = 0;
                #ifdef BLOCKWISE
                uint blockIndexO = GetBlockIndexO(screenPos);
                uint n = blockIndexO;
                uint blockIndexV = 0;
                uint mask = 1;
                for (uint j = 0; j < 8; j++)
                {
                    uint m = n % ShapeO[j];
                    // uint-деление вместо signed int — иначе Vulkan warning «integer divides may be much slower».
                    uint step = max(Steps[j], 1u);
                    uint diff = (m >= Starts[j]) ? (m - Starts[j]) : 0u;
                    uint maxD = ShapeV[j] > 0u ? ShapeV[j] - 1u : 0u;
                    uint d = min(diff / step, maxD);
                    blockIndexV += d * StridesV[j];
                    mask *= (m == Starts[j] + d * Steps[j]) ? 1u : 0u;
                    n /= ShapeO[j];
                }
                v = mask * SampleBlockV(blockIndexV) + (1 - mask) * SampleElementsX(blockIndexO);
                #else
                uint4 indexO4 = GetIndexO(screenPos);
                uint4 n4 = indexO4;
                uint4 indexV4 = 0;
                uint4 mask4 = 1;
                for (uint j = 0; j < 8; j++)
                {
                    uint4 m4 = n4 % ShapeO[j];
                    uint4 step4 = (uint4)max(Steps[j], 1u);
                    uint4 starts4 = (uint4)Starts[j];
                    uint4 shapeV4 = (uint4)ShapeV[j];
                    uint4 diff4 = (m4 >= starts4) ? (m4 - starts4) : (uint4)0;
                    uint4 maxD4 = shapeV4 > 0 ? (shapeV4 - 1u) : (uint4)0;
                    uint4 d4 = min(diff4 / step4, maxD4);
                    indexV4 += d4 * StridesV[j];
                    mask4 *= (m4 == starts4 + d4 * Steps[j]) ? (uint4)1 : (uint4)0;
                    n4 /= ShapeO[j];
                }
                v = mask4 * SampleElementsV(indexV4) + (1 - mask4) * SampleElementsX(indexO4);
                #endif

                return v;
            }
            ENDCG
        }
    }
}

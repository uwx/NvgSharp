// NvgSharp FillImage fragment shader
#include "NvgCommon.hlsli"

float4 main(PSInput input) : SV_TARGET
{
    float scissor = scissorMask(input.fpos);
#ifdef EDGE_AA
    float strokeAlpha = strokeMask(input.ftcoord);
    if (strokeAlpha < strokeThr) discard;
#else
    float strokeAlpha = 1.0;
#endif
    float2 pt = (mul(float3(input.fpos, 1.0), (float3x3)paintMat)).xy / extent.xy;
    float4 color = g_texture.Sample(g_sampler, pt);
    color = float4(color.xyz * color.w, color.w);
    color *= innerCol;
    color *= strokeAlpha * scissor;
    return color;
}

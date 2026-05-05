// NvgSharp FillGradient fragment shader
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
    float2 pt = (mul((float3x3)paintMat, float3(input.fpos, 1.0))).xy;
    float d = clamp((sdroundrect(pt, extent.xy, radius) + feather * 0.5) / feather, 0.0, 1.0);
    float4 color = lerp(innerCol, outerCol, d);
    color *= strokeAlpha * scissor;
    return color;
}

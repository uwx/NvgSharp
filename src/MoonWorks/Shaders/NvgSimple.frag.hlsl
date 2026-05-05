// NvgSharp Simple fragment shader (stencil fill pass)
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
    return float4(1, 1, 1, 1);
}

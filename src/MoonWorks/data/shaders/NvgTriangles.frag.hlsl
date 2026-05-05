// NvgSharp Triangles fragment shader
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
    float4 color = g_texture.Sample(g_sampler, input.ftcoord);
    color *= scissor;
    return color * innerCol;
}

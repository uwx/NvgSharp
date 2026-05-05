// NvgSharp shared fragment include for MoonWorks (ShaderCross HLSL)

cbuffer FragmentUniforms : register(b0, space3)
{
    float4x4 scissorMat;
    float4x4 paintMat;
    float4 innerCol;
    float4 outerCol;
    float2 scissorExt;
    float2 scissorScale;
    float2 extent;
    float radius;
    float feather;
    float strokeMult;
    float strokeThr;
};

Texture2D g_texture : register(t0, space2);
SamplerState g_sampler : register(s0, space2);

struct PSInput
{
    float4 Position : SV_Position;
    float2 ftcoord  : TEXCOORD0;
    float2 fpos     : TEXCOORD1;
};

float sdroundrect(float2 pt, float2 ext, float rad)
{
    float2 ext2 = ext - float2(rad, rad);
    float2 d = abs(pt) - ext2;
    return min(max(d.x, d.y), 0.0) + length(max(d, 0.0)) - rad;
}

float scissorMask(float2 p)
{
    float2 sc = (abs((mul((float3x3)scissorMat, float3(p.x, p.y, 1.0))).xy) - scissorExt.xy);
    sc = float2(0.5, 0.5) - sc * scissorScale.xy;
    return clamp(sc.x, 0.0, 1.0) * clamp(sc.y, 0.0, 1.0);
}

#ifdef EDGE_AA
float strokeMask(float2 ftcoord)
{
    return min(1.0, (1.0 - abs(ftcoord.x * 2.0 - 1.0)) * strokeMult) * min(1.0, ftcoord.y);
}
#endif

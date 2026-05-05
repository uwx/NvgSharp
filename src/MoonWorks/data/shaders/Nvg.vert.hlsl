// NvgSharp vertex shader for MoonWorks (ShaderCross HLSL)

cbuffer VertexUniforms : register(b0, space1)
{
    float4x4 transformMat;
};

struct VSInput
{
    float2 Position : TEXCOORD0;
    float2 TexCoord : TEXCOORD1;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float2 ftcoord  : TEXCOORD0;
    float2 fpos     : TEXCOORD1;
};

VSOutput main(VSInput input)
{
    VSOutput output;
    output.ftcoord = input.TexCoord;
    output.fpos = input.Position;
    output.Position = mul(transformMat, float4(input.Position.x, input.Position.y, 0, 1));
    return output;
}

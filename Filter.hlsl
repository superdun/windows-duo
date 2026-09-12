cbuffer Params : register(b0)
{
    float4 Col0;
    float4 Col1;
    float4 Col2;
    float4 ScreenAndOrigin; // screen size, padded origin
    float4 PaddedAndBlur;   // padded size, max radius, blur strength
    float4 Shape;           // blur floor, max dim, pixel scale, max level
    float4 Light;           // dim floor, dim strength, dim reach, hinge along x
};

Texture2D DesktopTex : register(t0);
SamplerState LinearClamp : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float4 PSDownsample(VSOut i) : SV_Target
{
    float2 texel = ScreenAndOrigin.xy;
    float srcMip = ScreenAndOrigin.z;
    float2 uv = i.uv;
    const float w[5] = { 0.0625, 0.25, 0.375, 0.25, 0.0625 };
    float4 colour = 0;
    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            colour += w[x + 2] * w[y + 2] * DesktopTex.SampleLevel(
                LinearClamp, uv + float2(x, y) * texel, srcMip);
        }
    }
    return colour;
}

float4 PSMain(VSOut i) : SV_Target
{
    float2 screenSize = ScreenAndOrigin.xy;
    float2 paddedOrigin = ScreenAndOrigin.zw;
    float2 paddedSize = PaddedAndBlur.xy;
    float maxRadius = PaddedAndBlur.z;
    float strength = PaddedAndBlur.w;
    float blurFloor = Shape.x;
    float maxDim = Shape.y;
    float pixelScale = Shape.z;
    float maxLevel = Shape.w;
    float dimFloor = Light.x;
    float dimStrength = Light.y;
    float dimReach = Light.z;
    float hingeAlongX = Light.w;

    float2 screenPoint = float2(i.pos.x / pixelScale, screenSize.y - i.pos.y / pixelScale);
    float3x3 screenToPicture = float3x3(
        Col0.x, Col1.x, Col2.x,
        Col0.y, Col1.y, Col2.y,
        Col0.z, Col1.z, Col2.z);
    float3 mapped = mul(screenToPicture, float3(screenPoint, 1.0));
    if (abs(mapped.z) < 1e-6)
    {
        return float4(0, 0, 0, 1);
    }

    float2 picturePoint = mapped.xy / mapped.z;
    float2 unit = (picturePoint - paddedOrigin) / paddedSize;
    if (unit.x < 0.0 || unit.x > 1.0 || unit.y < 0.0 || unit.y > 1.0)
    {
        return float4(0, 0, 0, 1);
    }

    float2 texCoord = float2(unit.x, 1.0 - unit.y);
    float height = hingeAlongX > 0.5
        ? clamp(picturePoint.x / screenSize.x, 0.0, 1.0)
        : clamp(picturePoint.y / screenSize.y, 0.0, 1.0);

    float blur = strength * (blurFloor + (1.0 - blurFloor) * height);
    float mipLevel = clamp(log2(max(blur * maxRadius, 1.0)), 0.0, maxLevel);
    float4 colour = DesktopTex.SampleLevel(LinearClamp, texCoord, mipLevel);

    float spread = smoothstep(0.0, max(dimReach, 0.02), height);
    float fade = dimStrength * (dimFloor + (1.0 - dimFloor) * spread);
    colour.rgb *= pow(max(1.0 - maxDim * fade, 0.0), 2.2);
    colour.rgb *= saturate(1.0 - Col0.w);
    return float4(colour.rgb, 1.0);
}

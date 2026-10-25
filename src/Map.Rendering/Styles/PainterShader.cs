using Map.Core.Documents;
using SkiaSharp;

namespace Map.Rendering.Styles;

/// <summary>
/// Approved WetSource 1, BlockSource 5, TwoInkSource 3/4. All pigment constants and
/// compositing are from those mockups. Form fields and brush scale belong to each asset;
/// all procedural samples use document coordinates, including a translated export tile.
/// </summary>
internal static class PainterShader
{
    private const string Source = """
uniform shader scene;
uniform shader mbig;
uniform shader mfacet;
uniform shader mwide;
uniform shader features;
uniform shader direction;
uniform shader geo;
uniform shader rsoft;
uniform float2 origin;
uniform float2 docPerPx;
uniform float variant;
uniform float strength;
uniform float3 paper;
uniform float wetWash;
uniform float edges;
uniform float granulation;
uniform float crests;
uniform float seaStrength;
uniform float paperStrength;
uniform float gouges;
uniform float seaCuts;
uniform float woodGrain;
uniform float squeeze;
uniform float starved;
uniform float waterLines;
uniform float contour;
uniform float stoneCrayon;
uniform float tintPlate;
uniform float scraped;

float2 raster(float2 p) { return (p - origin) / docPerPx; }
float4 bigAt(float2 p) { return mbig.eval(raster(p)); }
float4 facetAt(float2 p) { return mfacet.eval(raster(p)); }
float3 unpre(float4 c) { return c.a > 0.0001 ? c.rgb / c.a : float3(0); }
float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p); float2 f = fract(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + float2(1,0)), u.x), mix(hash(i + float2(0,1)), hash(i + float2(1,1)), u.x), u.y);
}
float fbm(float2 p) { float v = 0.0; float a = 0.5; for (int k = 0; k < 4; k++) { v += a * noise(p); p *= 2.03; a *= 0.5; } return v; }
float lum(float3 c) { return dot(c,float3(0.299,0.587,0.114)); }
float3 hex(float r,float g,float b) { return float3(r,g,b) / 255.0; }
float onWhite(float4 c) { return lum(c.rgb + (1.0 - c.a)); }
float toneOf(float4 c) { return clamp((0.94 - onWhite(c)) / 0.66,0.0,1.0); }
float3 glaze(float3 under,float3 pigment,float amt,float d) {
    float3 c = mix(float3(1),pigment,clamp(amt,0.0,1.0));
    c = c * (1.0 - (1.0 - c) * (d - 1.0));
    return under * clamp(c,0.0,1.0);
}
float lostFound(float2 p) { return mix(0.012,0.09,smoothstep(0.5,0.72,noise(p / 55.0 + 3.0))); }
float wash(float f,float t,float2 p) { float s = lostFound(p); return smoothstep(t-s,t+s,f); }
float pool(float f,float t) { return mix(1.0,0.65 + 0.7 * (1.0 - smoothstep(t,t+0.16,f)),edges); }
float3 ink(float3 under,float3 pigment,float amt) { return under * mix(float3(1),pigment,clamp(amt,0.0,1.0)); }
float block(float f,float t) { return smoothstep(t-0.012,t+0.012,f); }

half4 paintPixel(float2 pixel) {
    float2 p = origin + pixel * docPerPx;
    float4 g = geo.eval(pixel);
    float dL = g.r * 255.0; float dW = g.g * 255.0;
    float landF = smoothstep(-0.8,0.8,dW-dL); float seaF = 1.0-landF;
    float riv = g.b * smoothstep(0.5,3.0,dW);
    float4 f = features.eval(pixel);
    float3 form = unpre(f);
    float3 dir = unpre(direction.eval(pixel));
    float ms = dir.b > 0.001 ? dir.b : 1.0;
    float2 q = p / ms;
    float2 across = dir.rg * 2.0 - 1.0;
    float2 along = float2(-across.y,across.x);
    float4 mb = bigAt(p); float4 mf = facetAt(p); float4 mw = mwide.eval(pixel);
    float massA = smoothstep(0.47,0.53,mb.a);
    float up = form.g * massA;
    // The top term is already shifted in asset space as well (kept for bokashi, never sampled from a neighbour).
    float top = form.b * massA;
    float crest = form.r;
    float2 ga = float2(bigAt(p+float2(1.5,0)).a-bigAt(p-float2(1.5,0)).a,
                       bigAt(p+float2(0,1.5)).a-bigAt(p-float2(0,1.5)).a) / 3.0;
    float outlineD = abs(mb.a-0.5) / max(length(ga),1e-4);
    float key = 1.0-smoothstep(0.9*ms,1.6*ms,outlineD);
    float seaT = smoothstep(0.0,300.0,dL);
    float tBig = toneOf(mb)*massA;
    float tFacet = toneOf(mf)*smoothstep(0.45,0.55,mf.a);
    float t = (toneOf(mb)*0.55+toneOf(mf)*0.45)*massA;
    float3 col = paper;

    if (variant < 1.5) {
        // WetSource variant 1, including the shared sea/land/paper recipe.
        float jit = (fbm(p/16.0)-0.5)*5.0;
        dL += jit; dW -= jit;
        landF = smoothstep(-0.8,0.8,dW-dL); seaF = 1.0-landF;
        float tooth = fbm(p/3.0)*0.55+noise(p/1.15)*0.45;
        float turb = fbm(p/70.0);
        float d = 1.0+0.5*(turb-0.5)+0.12*(noise(p/2.2)-0.5)*granulation;
        col *= 1.0-0.03*(noise(p/300.0)-0.5)*paperStrength;
        seaT = smoothstep(0.0,280.0,dL);
        float3 sea = mix(mix(hex(80,190,200),hex(40,110,190),seaT),hex(120,90,170),smoothstep(0.55,0.8,fbm(p/160.0))*0.6);
        col = glaze(col,sea,seaF*(0.55+0.2*turb)*seaStrength,d);
        col = glaze(col,hex(40,110,190),seaF*(1.0-smoothstep(0.0,5.0,dL))*0.3*seaStrength,1.0);
        float halo = smoothstep(0.02,0.4,mw.a);
        float3 landC = mix(hex(240,204,120),hex(236,186,160),smoothstep(0.35,0.75,noise(p/380.0+4.0))*0.6);
        col = glaze(col,landC,landF*(1.0-0.6*halo)*0.4,1.0+0.2*(turb-0.5));
        col = glaze(col,hex(80,190,200),(riv*0.5+rsoft.eval(pixel).a*landF*0.15)*seaStrength,d);
        float2 wob = float2(noise(q/40.0)-0.5,noise(q/40.0+17.0)-0.5)*7.0*ms;
        mb = bigAt(p+wob);
        massA = smoothstep(0.35,0.6,mb.a+0.18*(fbm(q/9.0)-0.5));
        tBig = toneOf(mb)*massA;
        up = form.g*massA;
        float3 wet = mix(hex(232,176,176),mix(hex(104,166,220),hex(128,104,190),smoothstep(0.35,0.8,fbm(p/60.0))),smoothstep(0.1,0.6,up));
        float bleed = smoothstep(0.05,0.55,mw.a+0.2*(fbm(q/18.0)-0.5));
        float pigmentTooth = fbm(q/3.0)*0.55+noise(q/1.15)*0.45;
        float valley = 1.0-smoothstep(0.3,0.7,pigmentTooth);
        float dg = d+0.35*(valley-0.5)*granulation;
        float3 shadowC = mix(hex(96,110,190),hex(120,92,170),smoothstep(0.3,0.7,fbm(p/45.0+9.0)));
        float3 deepC = mix(hex(72,70,150),hex(90,60,130),smoothstep(0.3,0.7,fbm(p/45.0+1.0)));
        col = glaze(col,wet,bleed*0.3*wetWash,d);
        float tj = tBig+0.08*(fbm(q/26.0)-0.5);
        col = glaze(col,shadowC,wash(tj,0.36,q)*pool(tj,0.36)*0.36,dg);
        col = glaze(col,deepC,wash(tj,0.66,q)*pool(tj,0.66)*0.36,dg);
        col = mix(col,paper,massA*(1.0-smoothstep(0.04,0.14,tj))*smoothstep(0.4,0.8,up)*0.6);
        col = glaze(col,hex(50,50,110),crest*step(0.5,noise(q/22.0))*0.4*crests,1.0);
        col *= 1.0-0.035*(tooth-0.5)*paperStrength;
        col += (hash(floor(p))-0.5)*0.01*paperStrength;
    } else if (variant < 2.5) {
        // BlockSource variant 5: Sōsaku-hanga, the gouge's trace.
        float fib = smoothstep(0.78,0.95,noise(float2(p.x*0.9+p.y*0.2,p.y*0.04-p.x*0.01)));
        fib += smoothstep(0.8,0.96,noise(float2(p.y*0.8-p.x*0.3,p.x*0.05)+13.0));
        float grain = 0.5+0.5*sin((p.y+9.0*fbm(float2(p.x/140.0,p.y/30.0)))*0.9);
        float press = 0.94+0.06*fbm(p/26.0);
        col *= mix(1.0,(1.0-0.025*fib)*(0.99+0.02*noise(p/240.0)),paperStrength);
        float2 sp = float2(dot(q,across)/2.2,dot(q,along)/22.0);
        float gouge = noise(sp)*0.7+noise(sp*float2(0.5,2.0)+4.0)*0.3;
        col = ink(col,hex(46,62,104),seaF*0.9*press);
        float band = fract((dL+6.0*noise(p/90.0))/22.0);
        float cutSea = seaF*(1.0-smoothstep(0.0,0.12,abs(band-0.5)-0.02*noise(p/4.0)))
            *step(0.42,noise(float2(p.x/55.0,dL/22.0)))*(1.0-smoothstep(30.0,200.0,dL));
        col = mix(col,paper,cutSea*0.85*seaCuts);
        col = ink(col,hex(214,150,110),landF*0.5*press);
        col = ink(col,hex(140,70,46),landF*(1.0-smoothstep(0.0,5.0,dW))*0.6);
        col = ink(col,hex(46,62,104),riv*0.9);
        t = tFacet*0.5+tBig*0.5;
        float shadow = block(t+0.22*(gouge-0.5),0.5);
        float half_ = block(t+0.3*(gouge-0.5),0.3)*(1.0-shadow);
        float vcut = smoothstep(0.55,0.62,gouge);
        float blackBlock = massA*mix(1.0,shadow*(1.0-0.8*vcut*smoothstep(0.5,0.65,1.0-t))+half_*(1.0-vcut),gouges);
        col = ink(col,hex(232,196,150),massA*0.45);
        col = mix(col,hex(30,26,24),blackBlock*0.92);
        key = 1.0-smoothstep(1.4*ms,2.4*ms,outlineD);
        col = mix(col,hex(30,26,24),key*massA*0.9*contour);
        float mountainGrain = 0.5+0.5*sin((q.y+9.0*fbm(float2(q.x/140.0,q.y/30.0)))*0.9);
        col *= 1.0-0.06*((grain-0.5)*landF+(mountainGrain-0.5)*blackBlock)*woodGrain;
        col += (hash(floor(p))-0.5)*0.01*paperStrength;
    } else {
        // TwoInkSource variants 3 and 4. Both multiply the paper; no opaque screen-print compositing.
        float c1 = 0.0; float c2 = 0.0;
        float3 ink1 = float3(0); float3 ink2 = float3(0);
        col *= 1.0-0.02*(noise(p/1.3)-0.5)*paperStrength;
        if (variant < 3.5) {
            ink1 = hex(200,30,46); ink2 = hex(26,24,26);
            float2 gp = mix(p,q,massA);
            float starve = smoothstep(0.35,0.65,noise(gp/1.6)*0.6+noise(gp/9.0)*0.4);
            float redSolid = massA*smoothstep(0.26,0.3,t)*(1.0-smoothstep(0.68,0.72,t));
            float landRed = landF*(1.0-smoothstep(0.0,10.0,dW))*smoothstep(0.0,2.0,dW);
            c1 = max(redSolid,landRed)*mix(1.0,0.82+0.18*starve,starved);
            float blackSolid = massA*smoothstep(0.68,0.72,t);
            float wl = abs(fract((dL+2.0*noise(p/40.0))/11.0)-0.5)*11.0;
            float seaBlack = seaF*(1.0-smoothstep(0.9,1.6,wl))*(1.0-smoothstep(30.0,46.0,dL));
            c2 = max(max(blackSolid,key*massA*contour),max(crest*contour,seaBlack*waterLines))*mix(1.0,0.85+0.15*starve,starved);
            c2 = max(c2,(seaF*(1.0-smoothstep(0.7,1.6,abs(dL-0.5)))+riv*0.85)*contour);
            float rim = redSolid*((1.0-smoothstep(0.28,0.32,t))+smoothstep(0.65,0.69,t))+landRed*(1.0-smoothstep(2.0,4.0,dW));
            c1 = max(c1,clamp(rim,0.0,1.0)*squeeze);
        } else {
            ink1 = hex(214,190,140); ink2 = hex(34,30,30);
            float2 gp = mix(p,q,massA);
            float stone = fbm(gp/1.1)*0.65+noise(gp/0.7)*0.35;
            float crayon = t*0.9*stoneCrayon+(crest*0.6+key*0.7)*contour;
            float seaCrayon = seaF*(0.18+0.3*seaT)*(0.6+0.4*step(0.5,fract(p.y/5.0+0.3*noise(p/60.0))))*seaStrength;
            float landCrayon = landF*(1.0-massA)*(1.0-smoothstep(0.0,14.0,dW))*0.35*contour;
            c2 = smoothstep(stone-0.05,stone+0.05,max(max(crayon,seaCrayon),max(landCrayon,riv*0.6*contour))*0.95);
            float scrape = massA*smoothstep(0.55,0.8,up)*(1.0-smoothstep(0.08,0.16,t))
                +seaF*(1.0-smoothstep(2.0,7.0,dL))*step(0.4,noise(p/5.0));
            c1 = 0.85*(1.0-clamp(scrape,0.0,1.0)*scraped)*tintPlate;
        }
        col *= mix(float3(1),ink1,c1)*mix(float3(1),ink2,c2);
        col += (hash(floor(p))-0.5)*0.008*paperStrength;
    }
    float4 src = scene.eval(pixel);
    float3 original = src.rgb+paper*(1.0-src.a);
    return half4(half3(mix(original,clamp(col,0.0,1.0),strength)),1.0);
}
half4 main(float2 pixel) {
    // The mockups' 50% images filter the 100% print. Integrate that same document
    // grain instead of taking every other speck (which aliases the lithographic stone).
    if (max(docPerPx.x,docPerPx.y) > 1.001) {
        return (paintPixel(pixel+float2(-0.25,-0.25))+paintPixel(pixel+float2(0.25,-0.25))
              +paintPixel(pixel+float2(-0.25,0.25))+paintPixel(pixel+float2(0.25,0.25))) * 0.25;
    }
    return paintPixel(pixel);
}
""";

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() => PainterFormMaps.Compile(Source));

    public static SKShader Build(SKImage scene, SKImage wetBig, SKImage big, SKImage facets, SKImage wide,
        SKImage wetFeatures, SKImage features, SKImage direction, SKImage geo, SKImage riverSoft,
        SKPoint origin, SKPoint docPerPx, MapStyle style)
    {
        var wet = style.Preset == MapStylePreset.WatercolorBigShapes;
        var builder = new SKRuntimeShaderBuilder(Effect.Value);
        var children = new List<SKShader>();
        void Child(string name, SKImage image)
        {
            var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
            children.Add(shader); builder.Children[name] = shader;
        }
        Child("scene", scene); Child("mbig", wet ? wetBig : big); Child("mfacet", facets);
        Child("mwide", wide); Child("features", wet ? wetFeatures : features); Child("direction", direction);
        Child("geo", geo); Child("rsoft", riverSoft);
        builder.Uniforms["origin"] = new[] { origin.X, origin.Y };
        builder.Uniforms["docPerPx"] = new[] { docPerPx.X, docPerPx.Y };
        var (variant, paper) = style.Preset switch
        {
            MapStylePreset.WatercolorBigShapes => (1f, new[] { 249f, 246f, 238f }),
            MapStylePreset.UkiyoeSosakuHanga => (2f, new[] { 236f, 226f, 200f }),
            MapStylePreset.PrintLetterpress => (3f, new[] { 241f, 236f, 224f }),
            _ => (4f, new[] { 242f, 237f, 226f }),
        };
        builder.Uniforms["variant"] = variant;
        builder.Uniforms["paper"] = paper.Select(v => v / 255).ToArray();
        builder.Uniforms["strength"] = (float)style.Strength;
        void Amount(string name, StyleEffectKind kind) => builder.Uniforms[name] = (float)style.Effect(kind).Effective;
        Amount("wetWash", StyleEffectKind.Bleed); Amount("edges", StyleEffectKind.Edges);
        Amount("granulation", StyleEffectKind.Granulation); Amount("crests", StyleEffectKind.Crest);
        Amount("seaStrength", StyleEffectKind.Sea); Amount("paperStrength", StyleEffectKind.Paper);
        Amount("gouges", StyleEffectKind.Gouge); Amount("seaCuts", StyleEffectKind.SeaCuts);
        Amount("woodGrain", StyleEffectKind.WoodGrain); Amount("squeeze", StyleEffectKind.InkSqueeze);
        Amount("starved", StyleEffectKind.Starved); Amount("waterLines", StyleEffectKind.WaterLines);
        Amount("contour", StyleEffectKind.Contour); Amount("stoneCrayon", StyleEffectKind.StoneCrayon);
        Amount("tintPlate", StyleEffectKind.TintPlate); Amount("scraped", StyleEffectKind.Scraped);
        var result = builder.Build();
        foreach (var child in children) child.Dispose();
        return result;
    }
}

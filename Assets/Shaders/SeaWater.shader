Shader "SEA/Water"
{
    // 代码内置的风格化海面(Built-in RP, 无贴图):
    //  1) 顶点正弦复合波(世界空间做差商法线)
    //  2) 视线菲涅尔在深/浅色间过渡
    //  3) 流动浪脊高光条带
    Properties
    {
        _Deep     ("Deep",     Color) = (0.015, 0.10, 0.16, 1)
        _Shallow  ("Shallow",  Color) = (0.07, 0.30, 0.38, 1)
        _Crest    ("Crest",    Color) = (0.78, 0.93, 1.0, 1)
        _Speed    ("Speed",    Float) = 1.15
        _Freq     ("Frequency",Float) = 0.30
        _Amp      ("Amplitude",Float) = 0.55
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "ForceNoShadowCasting"="True" }
        Pass
        {
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            fixed4 _Deep, _Shallow, _Crest;
            float _Speed, _Freq, _Amp;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 nrm : TEXCOORD1; };

            // 复合波高(xz 世界坐标)
            float H(float2 p, float t)
            {
                float h = sin(p.x*_Freq + t)
                        + 0.6f*sin((p.x*0.7f + p.y*1.35f)*_Freq + t*1.35f)
                        + 0.42f*cos((p.x*1.9f - p.y*0.55f)*_Freq + t*0.65f);
                return h*_Amp;
            }

            v2f vert (appdata v)
            {
                v2f o;
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float t = _Time.y * _Speed;
                float h  = H(wp.xz, t);
                float hx = H(wp.xz + float2(0.7f, 0.0f), t) - h;
                float hz = H(wp.xz + float2(0.0f, 0.7f), t) - h;
                wp.y += h;
                o.wpos = wp;
                o.nrm  = normalize(float3(-hx, 0.55f, -hz));
                o.pos  = UnityWorldToClipPos(wp);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 V = normalize(_WorldSpaceCameraPos - i.wpos);
                float ndv = abs(dot(normalize(i.nrm), V));
                // 深→浅 随视线掠角
                float fres = pow(1.0f - ndv, 1.8f);
                fixed3 col = lerp(_Deep.rgb, _Shallow.rgb, saturate(fres*1.5f));
                // 流动浪脊亮条
                float band = sin(i.wpos.x*1.05f + i.wpos.z*0.9f + _Time.y*1.7f)
                           + 0.5f*sin((i.wpos.x - i.wpos.z)*1.6f + _Time.y*2.3f);
                float crest = smoothstep(0.55f, 1.15f, band);
                col = lerp(col, _Crest.rgb, crest*0.5f);
                return fixed4(col, 1.0f);
            }
            ENDCG
        }
    }
    Fallback "Diffuse"
}

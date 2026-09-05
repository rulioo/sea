Shader "SEA/Land"
{
    // 凸起大陆的顶面着色: 顶点色(沙岸/绿洲/干旱/积雪带) × 简单朗伯光
    // 海岸坡度来自陆地网格顶(海面上)与海床网格(海面下)之间的大斜边, 光照在斜坡上成形。
    Properties
    {
        _Tint     ("Tint",     Color)  = (1, 1, 1, 1)
        _SunDir   ("Sun Dir",  Vector) = (0.45, 0.85, 0.35, 0)
        _Ambient  ("Ambient",  Float)  = 0.42
        _Diffuse  ("Diffuse",  Float)  = 0.80
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "ForceNoShadowCasting"="True" }
        Pass
        {
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            fixed4 _Tint;
            float4 _SunDir;
            float _Ambient, _Diffuse;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float3 nrm : TEXCOORD0; fixed4 col : COLOR0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.nrm = UnityObjectToWorldNormal(v.normal);
                o.col = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.nrm);
                float dif = saturate(dot(n, normalize(_SunDir.xyz)));
                fixed3 c = _Tint.rgb * i.col.rgb * (_Ambient + _Diffuse * dif);
                return fixed4(c, 1.0);
            }
            ENDCG
        }
    }
    Fallback "Diffuse"
}

Shader "SEA/Fog"
{
    // 海图黑雾的球面版。
    //
    // 【为什么不能沿用 Sprites/Default 贴一块大平面】
    //   平面时代的雾罩是一块比海图略高的板子(h=7), 俯视正投影下"板上一点"与"地表一点"
    //   恰好对齐, 所以直接拿板的 UV 当探明图采样就是对的。球上完全不成立:
    //     ① 壳抬到 R+h, 视线穿过壳的位置与地表点**错开** —— h 越大错得越离谱,
    //        雾洞会整体偏离城市(在球缘附近尤其明显, 那里视线几乎是擦着的);
    //     ② 壳比地球大, 远看球缘外会多出一圈黑边(壳自己的轮廓);
    //     ③ 镜头推近到 d < h 就钻进壳内部了, 近半球被裁掉 → 雾整片消失。
    //   所以这里不采样"壳上的点", 而是**每个片元现场把视线打回半径 R 的地球**,
    //   用那个交点反推经纬采样探明图 —— 采样点被钉死在地表, ① 于是视差精确为 0,
    //   壳抬多高都不影响雾洞位置; 打不到地球的片元直接 discard, ② 的黑边随之消失。
    //   (③ 由 SeaFog.ShellY 与镜头最近距离的约束解决, 见 SeaFog.cs。)
    //
    //   渲染次序仍是那条明梯(数字越大越晚画、越盖人):
    //       黑雾 3500(本 shader, 由 SeaFog.Build 设 material.renderQueue) → 航线 3600 → 船 3700
    //   ZWrite Off + Blend 让雾"盖"而不是"挡": 陆/海/港圈/旗在它之下照常写深度,
    //   航线与船在它之后画所以照常压在雾上。
    //   注意 ZTest 必须 LEqual —— 靠它把**背半球**那一片壳挡掉(那片片元在地球背面,
    //   深度大于地表, 不画)。要是图省事写成 Always, 背面的黑雾会糊到正面上来。
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 1)   // 白色 = 原色(黑来自探明纹理的 RGB, 不是这里)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "ForceNoShadowCasting"="True" }
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off          // 球壳绕序无头验不了 → 两面都画; 多出来的那面是背半球, 深度测试会挡掉
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            fixed4 _Color;
            sampler2D _MainTex;

            // 地球半径 —— 与 SeaGlobe.R = 180/π 一致(1 世界单位 = 1° 弧长)
            #define SEA_R 57.29577951

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // 板时代这里只是把 UV 传下去; 现在**顶点世界坐标才是主数据** ——
                //   片元要靠它现算视线方向。壳网格多粗都不影响结果(见 frag)。
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 相机 → 本片元 的视线, 与**半径 R 的地球**求交(球心在原点, 与 SeaGlobe 同约定)
                float3 ro = _WorldSpaceCameraPos;
                float3 rd = normalize(i.wpos - ro);
                float b = dot(ro, rd);
                float c = dot(ro, ro) - SEA_R * SEA_R;
                float disc = b * b - c;
                if (disc < 0.0f) discard;     // 视线与地球不相交 → 这片壳在球缘之外的空中, 丢掉(黑边即由此消失)
                float t = -b - sqrt(disc);    // 近根 = 视线打在**正面**的那层地表
                if (t < 0.0f) discard;        // 交点在身后(相机已在地球内部, 不该发生)—— 也别画
                float3 hit = ro + rd * t;
                float3 n = normalize(hit);

                // 世界方向 → 经纬, 与 SeaGlobe.ToWorld 同一套约定:
                //   x = R·cosφ·sinθ, y = R·sinφ, z = -R·cosφ·cosθ  ⇒  θ = atan2(x, -z), φ = asin(y/R)
                float lat = asin(clamp(n.y, -1.0f, 1.0f));
                float lon = atan2(n.x, -n.z);
                // 经纬 → UV: 经度 -180..180 → 0..1(1/2π), 纬度 -90..90 → 0..1(1/π)。与 SeaFog 的格心约定对齐。
                float2 uv = float2(lon * 0.15915494f + 0.5f, lat * 0.31830989f + 0.5f);

                // 黑来自纹理 RGB(未探明格 = 纯黑 alpha 255), _Color 只是统一染色旋钮 —— 与 Sprites/Default 同口径
                return tex2D(_MainTex, uv) * _Color;
            }
            ENDCG
        }
    }
    Fallback Off
}

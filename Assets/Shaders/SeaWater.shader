Shader "SEA/Water"
{
    // 代码内置的风格化海面(Built-in RP, 无贴图):
    //  1) 顶点正弦复合波(角域做差商法线)
    //  2) 视线菲涅尔在深/浅色间过渡
    //  3) 流动浪脊高光条带
    //
    // 【为什么整套波域从"世界 xz"换成"角域 (θ,φ)"】
    //   海面现在是一整颗球, 顶点 UV 就是经纬归一值(见 SeaPlay.BuildOcean)。
    //   原来那套用世界 xz 当波域, 贴到球上有两个硬伤:
    //     ① 极点处 xz → (0,0), 所有波挤成一个点;
    //     ② 同样的 xz 间距在高纬对应的经度跨度大得多, 波会沿纬线被拉长。
    //   改用角域后 ①② 都没了。还有一个白送的好处: θ 方向所有系数取**整数圈**,
    //   于是 u=0 与 u=1(±180° 经线)天然接得上 —— 平铺在平面上必然出现的接缝, 这里是零。
    //   (v 方向不闭合也无妨: φ 从 -π/2 走到 π/2, 两端是极点, 见下面的极区淡出。)
    //   Cull Off 保留: 球面绕序已由 SeaPlay.FaceOutward 校正过, 但无头环境验不了"翻没翻",
    //   而两颗球里海面是背景层, 多画一遍背面在这个体量下没有可测代价。
    Properties
    {
        _Deep     ("Deep",     Color) = (0.015, 0.10, 0.16, 1)
        _Shallow  ("Shallow",  Color) = (0.07, 0.30, 0.38, 1)
        _Crest    ("Crest",    Color) = (0.78, 0.93, 1.0, 1)
        _Speed    ("Speed",    Float) = 1.15
        _Freq     ("Frequency",Float) = 0.30
        _Amp      ("Amplitude",Float) = 0.55
        _Ripple   ("Ripple",   Float) = 12.0   // 波峰对法线的扰动强度(角域坡度本身很小, 见 vert)
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
            float _Speed, _Freq, _Amp, _Ripple;

            // 海洋球半径 —— 与 SeaGlobe.R = 180/π 一致, 用来把角域差商折回真实坡度
            #define SEA_R 57.29577951
            // 差商步长(弧度): 0.01 rad ≈ 0.57°, 在 1° 的网格间距下不会采到相邻格
            #define SEA_D 0.01

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 nrm : TEXCOORD1; float2 uv : TEXCOORD2; };

            // UV → 角域 (θ, φ), 单位弧度。角度值与数值无关, 只是把经纬铺开成周期域。
            float2 A(float2 uv) { return float2(uv.x * 6.2831853f, uv.y * 3.14159265f); }

            // 复合波高(角域)。θ 方向系数全取整数 → 以 2π 为周期, ±180° 接缝天然闭合。
            //   原式的相对系数(0.7/1.35/1.9/0.55)在球上凑不出闭合周期, 所以整体重排成整数对;
            //   观感仍是三层错落的复合浪, 只是不再沿"世界 xz"铺, 而是沿经纬铺。
            float H(float2 p, float t)
            {
                float h = sin( 3.0f*p.x + 1.0f*p.y + t)
                        + 0.6f  * sin( 4.0f*p.x - 2.0f*p.y + t*1.35f)
                        + 0.42f * cos(-5.0f*p.x + 3.0f*p.y + t*0.65f);
                // 极区淡出: 球网格在最后一行是 361 个**重合**的极点顶点, 若它们波高各不相同,
                //   极点周围一圈三角会被扯开。让 |φ| 逼近 π/2 时振幅线性归零, 361 个顶点就一致了。
                float fade = saturate((1.5707963f - abs(p.y)) * 6.0f);
                return h * _Amp * fade;
            }

            v2f vert (appdata v)
            {
                v2f o;
                float3 up = normalize(v.vertex.xyz);   // 海洋球球心在原点 → 顶点方向就是外法线
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
                float t = _Time.y * _Speed;
                float2 p = A(v.uv);
                float h  = H(p, t);
                float hx = H(p + float2(SEA_D, 0.0f), t) - h;
                float hy = H(p + float2(0.0f, SEA_D), t) - h;
                wp += up * h;                          // 抬升沿**法线**(球上的"上"), 不再是 world +Y
                o.wpos = wp;
                o.uv = v.uv;

                // 法线扰动: 先把角域差商折成真实坡度(Δh ÷ 步长对应的弧长), 再沿切向的东/北偏。
                //   角度坡度本身只有千分之几(振幅 0.04 / 半径 57), 直接拿去做菲涅尔等于没效果,
                //   所以乘一个 _Ripple 放大 —— 这是纯观感旋钮, 只能靠眼睛定。
                float3 eastRaw = cross(float3(0.0f, 1.0f, 0.0f), up);
                float el = length(eastRaw);
                // 两极处 up ∥ Y, cross 退化 → 给个任意切向即可(那里 fade 已把 h 压成 0)
                float3 east  = el > 1e-4f ? eastRaw / el : float3(1.0f, 0.0f, 0.0f);
                float3 north = cross(up, east);
                float slopeE = hx / (SEA_D * SEA_R);
                float slopeN = hy / (SEA_D * SEA_R);
                // 主轴仍是球面法线 —— 哪怕 _Ripple 调坏了, 掠角菲涅尔(球缘发亮)也还在
                o.nrm = normalize(up - east * slopeE * _Ripple - north * slopeN * _Ripple);
                o.pos = UnityWorldToClipPos(wp);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 V = normalize(_WorldSpaceCameraPos - i.wpos);
                float ndv = abs(dot(normalize(i.nrm), V));
                // 深→浅 随视线掠角: 本意是"球缘发亮"那圈掠角辉光。
                //   指数从 1.8 提到 3.5 是**球体化带出来的回修**, 不是改美术方向:
                //   平面图时代相机恒定 82° 近乎垂直俯视, 满屏 ndv≈1 → 这项几乎不出场;
                //   改成地球仪后整颗球的边缘一大片都是掠角, 1.8 的幂让浅色铺满小半个球,
                //   海面看着像起了一层白雾(见 dev/shots/04 左半屏)。抬指数 = 把辉光压回球缘那一圈,
                //   既保住原本"球缘亮"的意图, 又让球心一侧恢复深蓝。
                float fres = pow(1.0f - ndv, 3.5f);
                fixed3 col = lerp(_Deep.rgb, _Shallow.rgb, saturate(fres*1.5f));
                // 流动浪脊亮条(同样搬到角域; θ 系数取整数 → 接缝连续)
                float2 p = A(i.uv);
                float band = sin( 2.0f*(p.x + p.y) + _Time.y*1.7f)
                           + 0.5f * sin( 3.0f*(p.x - p.y) + _Time.y*2.3f);
                float crest = smoothstep(0.55f, 1.15f, band);
                col = lerp(col, _Crest.rgb, crest*0.5f);
                return fixed4(col, 1.0f);
            }
            ENDCG
        }
    }
    Fallback "Diffuse"
}

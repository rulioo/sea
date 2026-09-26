Shader "SEA/Route"
{
    // 计划航线(黄色虚线)的无光照着色。
    //   本来要两件事: ① 压过海图黑雾(还没探明的水域也该看得见"我打算怎么走"—— 计划航线本来就画在海图上);
    //   ② 被船盖住(船 3700 后画)。
    //   原来是靠 ZTest Always 做到 ① —— 不看深度, 无条件画上去。**球上不能这么干**:
    //   地球背面的航线会穿过球体浮到正面来, 一张图同时显示两面的航线, 完全没法看。
    //   改成 ZTest LEqual 之后 ① 仍然成立, 而且不需要任何额外机制:
    //   黑雾是**透明**的(Blend + ZWrite Off), 它根本不写深度缓冲 → 航线的 LEqual 比的是
    //   地球表面的深度(陆地/海面都在航线之下) → 正面照过; 而背半球的航线深度远大于正面的地球
    //   表面, 被挡掉 —— 正是想要的。
    //   渲染次序仍是一条明梯(数字越大越晚画、越盖人):
    //       黑雾 3500(SeaFog.cs) → 本航线 3600 → 船 3700(SeaPlay.ShipRenderQueue)
    //   注意: 几何本身老老实实贴在球面之上 RouteY 处(RouteY 现在沿**法线**抬升), 没有别的花招。
    Properties
    {
        _Color ("Color", Color) = (1, 0.86, 0.22, 1)
    }
    SubShader
    {
        // Transparent+600 = 3600, 刚好压过黑雾(3500)又在船(3700)之下 —— 见上方次序说明。
        Tags { "RenderType"="Transparent" "Queue"="Transparent+600" "ForceNoShadowCasting"="True" }
        Pass
        {
            ZTest LEqual               // 球上必须比深度, 否则背半球的航线会浮到正面来 —— 见上方说明
            ZWrite Off
            Cull Off                  // 方块朝向随航向翻转, 两面都要看得见
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target { return _Color; }
            ENDCG
        }
    }
    Fallback Off
}

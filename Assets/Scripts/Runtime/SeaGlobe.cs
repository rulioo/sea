using System.Collections.Generic;
using UnityEngine;

namespace Sea
{
    // =============================================================
    // SEA · 海图球面投影(把平面经纬图贴到一颗地球上)
    //   - 模拟层(SeaMapGen / SeaRoute / 经济 / 存档)永远只认经纬度, 一行都不用改;
    //     本类只管"经纬度 → 世界坐标"这最后一跳(以及反跳), 呈现层统一通过它上球。
    //   - 半径取 180/π 是为了让 **1 世界单位恰好 = 1° 弧长**(DegToWorld == 1):
    //     于是 RouteY / BallFloatY / 船模尺寸 / 虚线长 等一堆照"度"估出来的世界单位
    //     常量原值继续有效, 球面弧长与度数重合, 不必整体缩放。
    //   - 球心在原点。经度 0 / 纬度 0 落在 -Z 方向 —— 默认机位(站在 -Z 侧看向原点)
    //     看过去正好是"东在右、北在上", 与原来平图的观感一致。
    //   - 图只覆盖 lon -110..140 / lat -40..70(见 SeaMapGen), 球上其余部分是空海:
    //     这是"先空着"的临时态, 不是 bug。
    // =============================================================
    public static class SeaGlobe
    {
        // 周长 = 2πR = 360, 于是 1 单位 = 1°(度与世界单位在球面上重合)
        public const float R = 180f / Mathf.PI;   // ≈ 57.29578
        public const float DegToWorld = 1f;       // == R * Deg2Rad, 恒等(见上)
        public const float WorldToDeg = 1f;

        const double Deg2Rad = Mathf.Deg2Rad;     // 走 double 算三角, 减少往返误差
        const float Rad2DegF = Mathf.Rad2Deg;

        // ---------------- 基本映射 ----------------

        // 经纬度 → 球面世界坐标
        public static Vector3 ToWorld(double lon, double lat)
        {
            double th = lon * Deg2Rad, ph = lat * Deg2Rad;
            double cp = System.Math.Cos(ph);
            return new Vector3(
                (float)(R * cp * System.Math.Sin(th)),
                (float)(R * System.Math.Sin(ph)),
                (float)(-R * cp * System.Math.Cos(th)));
        }

        // 球面世界坐标 → 经纬度(射线打到球面的交点、船上位置反推经纬都用它)
        public static void ToLonLat(Vector3 p, out double lon, out double lat)
        {
            Vector3 n = p.normalized;
            lat = System.Math.Asin(Mathf.Clamp(n.y, -1f, 1f)) * Rad2DegF;
            lon = System.Math.Atan2(n.x, -n.z) * Rad2DegF;
        }

        // 球面法线 —— 球上的"上"方向(一切原来的 +Y 抬起都改成沿它)
        public static Vector3 Up(Vector3 p) => p.normalized;

        // 把点拉回半径 R 的球面, 再沿法线抬高 up
        public static Vector3 ToSurface(Vector3 p, float up) => Up(p) * (R + up);

        // 球面局部坐标系: up=法线, north=朝北极的切向, east=朝东的切向
        public static void SurfaceFrame(double lon, double lat,
                                        out Vector3 up, out Vector3 north, out Vector3 east)
        {
            double th = lon * Deg2Rad, ph = lat * Deg2Rad;
            double st = System.Math.Sin(th), ct = System.Math.Cos(th);
            double sp = System.Math.Sin(ph), cp = System.Math.Cos(ph);
            up = new Vector3((float)(cp * st), (float)sp, (float)(-cp * ct));
            north = new Vector3((float)(-sp * st), (float)cp, (float)(sp * ct));
            east = new Vector3((float)ct, 0f, (float)st);
        }

        // 球面局部朝向: X=东 Y=法线 Z=北 —— 港标/旗杆/船模都挂在它下面,
        // 于是"杆子朝上""旗面朝外""船艏绕法线转"这些局部几何原样成立。
        public static Quaternion SurfaceRotation(double lon, double lat)
        {
            Vector3 up, north, east;
            SurfaceFrame(lon, lat, out up, out north, out east);
            return Quaternion.LookRotation(north, up);
        }

        // 贴着球面、按"上/东/北"三个切向偏移落位(港标浮球、旗杆、船都用它)
        public static Vector3 Offset(Vector3 surfacePoint, float up, float east, float north)
        {
            double lon, lat;
            ToLonLat(surfacePoint, out lon, out lat);
            Vector3 u, nr, e;
            SurfaceFrame(lon, lat, out u, out nr, out e);
            return u * (R + up) + e * east + nr * north;
        }

        // ---------------- 距离 ----------------

        // 两点球面大圆角距(度)。参数是 map 空间点(x=经度, z=-纬度)。
        public static float GreatCircleDeg(Vector3 a, Vector3 b)
            => GreatCircleDegLonLat(a.x, -a.z, b.x, -b.z);

        // haversine: 两极附近与对跖点都稳(平面欧氏在球上会高估, 见下方 ArcSubdivide 注释)
        public static float GreatCircleDegLonLat(double lon1, double lat1, double lon2, double lat2)
        {
            double p1 = lat1 * Deg2Rad, p2 = lat2 * Deg2Rad;
            double dp = p2 - p1, dl = (lon2 - lon1) * Deg2Rad;
            double sdp = System.Math.Sin(dp * 0.5), sdl = System.Math.Sin(dl * 0.5);
            double h = sdp * sdp + System.Math.Cos(p1) * System.Math.Cos(p2) * sdl * sdl;
            h = System.Math.Min(1.0, System.Math.Max(0.0, h));
            return (float)(2.0 * System.Math.Asin(System.Math.Sqrt(h)) * Rad2DegF);
        }

        // map 空间两点的大圆中点(球面 slerp 取半), 同样回 map 空间。
        //   为什么不用线性平均: 远洋航线上"两点连线的中点"会明显偏向高纬(平面直线在球上贴着大圆内侧),
        //   拿来当相机焦点会让镜头看着偏出去一截。
        public static Vector3 GreatCircleMid(Vector3 mapA, Vector3 mapB)
        {
            Vector3 na = ToWorld(mapA.x, -mapA.z).normalized;
            Vector3 nb = ToWorld(mapB.x, -mapB.z).normalized;
            Vector3 nm = na + nb;
            if (nm.sqrMagnitude < 1e-9f)   // 正好对跖: 中点不唯一, 随便取一个正交方向即可
                nm = Vector3.Cross(na, System.Math.Abs(na.y) < 0.9f ? Vector3.up : Vector3.right);
            ToLonLat(nm, out double lo, out double la);
            return new Vector3((float)lo, 0f, (float)(-la));
        }

        // ---------------- 拾取 ----------------

        // 射线 × 半径 R 的球(球心在原点)。命中返回近交点, 未命中返回 false。
        // 用"最近点参数"式而非裸 b²-4ac: 后者在掠射(球缘)时两次相减抵消, 丢精度丢得厉害。
        public static bool RaySphere(Vector3 origin, Vector3 dir, out Vector3 hit)
        {
            hit = Vector3.zero;
            float len = dir.magnitude;
            if (len < 1e-6f) return false;
            Vector3 d = dir / len;

            float tClosest = -Vector3.Dot(origin, d);      // 射线上离球心最近的点
            Vector3 p = origin + d * tClosest;
            float d2 = Vector3.Dot(p, p);
            float r2 = R * R;
            if (d2 > r2) return false;                     // 擦不到球

            float half = Mathf.Sqrt(r2 - d2);
            float t = tClosest - half;                     // 近交点
            if (t < 0f) t = tClosest + half;               // 相机在球内(理论上不会): 取远交点
            if (t < 0f) return false;                      // 球整个在身后
            hit = origin + d * t;
            return true;
        }

        // ---------------- 折线加密 ----------------

        // 把 map 空间折线按"每段不超过 maxDeg 度"加密。
        //   为什么必须做: 两点之间的**直线弦**会切进球体内部, 弦高 sag = R(1-cos(Δ/2))。
        //   Δ=20° 时 sag ≈ 0.87 单位, 远大于航线抬升 RouteY(0.46) —— 不加密的话虚线会
        //   沉进地里; 更糟的是船走的 RoutePointAt 就是同一份折线, 船也会钻到地面以下。
        //   所以虚线 / 船 / 标记必须共用这一份加密结果, 不能只给虚线加密。
        //
        //   实现要点: 插值走**球面 slerp**而不是 map 空间线性插值。
        //   线性插值看似更简单, 但它插出来的不是大圆, 而是一条"等距圆柱直线" ——
        //   这条线通常比大圆长, 于是按大圆距离算出来的 n 份里, 总有几份实际超过 maxDeg
        //   (实测 1.0231° 超出 1° 上限)。slerp 则保证每份恰好是 θ/n, 上限是硬保证。
        public static List<Vector3> ArcSubdivide(List<Vector3> mapPts, float maxDeg)
        {
            var outp = new List<Vector3>();
            if (mapPts == null || mapPts.Count == 0) return outp;
            outp.Add(mapPts[0]);
            float step = Mathf.Max(0.01f, maxDeg);
            for (int i = 1; i < mapPts.Count; i++)
            {
                Vector3 a = mapPts[i - 1];
                Vector3 b = mapPts[i];
                Vector3 na = ToWorld(a.x, -a.z).normalized;
                Vector3 nb = ToWorld(b.x, -b.z).normalized;
                double theta = System.Math.Atan2(Vector3.Cross(na, nb).magnitude, Vector3.Dot(na, nb));
                int n = Mathf.Max(1, Mathf.CeilToInt((float)(theta * Rad2DegF) / step));
                if (theta < 1e-9) { outp.Add(b); continue; }   // 重合点(或近似): 直接收尾
                double sinT = System.Math.Sin(theta);
                for (int k = 1; k <= n; k++)
                {
                    double t = (double)k / n;
                    Vector3 nk = ((float)(System.Math.Sin((1.0 - t) * theta) / sinT)) * na
                               + ((float)(System.Math.Sin(t * theta) / sinT)) * nb;
                    ToLonLat(nk, out double lo, out double la);
                    outp.Add(new Vector3((float)lo, 0f, (float)(-la)));   // 回 map 空间
                }
            }
            return outp;
        }
    }
}

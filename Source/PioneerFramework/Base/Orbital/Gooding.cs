using System;
using System.Runtime.CompilerServices;
// 数学工具的唯一权威是移植来的 MechJebLib.Utils.Statics（J2）。
// 曾用我们自己手抄的 PioneerFramework.MJStatics —— 它与上游已经漂移（Asinh/Atanh/Acosh 是朴素一行式），
// 已于 2026-09-18 删除。本文件只用它的 SafeAcos，而那一项两份逐字相同。
using MechJebLib.Lambert;
using static MechJebLib.Utils.Statics;
using static System.Math;

namespace PioneerFramework
{
    /// <summary>
    /// MechJeb2 的 Gooding Lambert 求解器直接移植。
    /// </summary>
    public static class Gooding
    {
        // 最小有效速度阈值，避免全零失败被当作有效解
        private const double MIN_VALID_SPEED = 1e-9;

        /// <summary>
        /// 级数收敛迭代上限（2026-09-10 性能第 19 条）。
        /// TLAMB 的两处级数循环以「T != TOLD」为终止条件（Gooding 1990 Fortran 的忠实移植）：
        /// 正常输入必然收敛，但 NaN 传播时 NaN != NaN 恒真 → 死循环挂死 UI 线程（规划器
        /// 事件驱动，冻结整个游戏）。退化几何（共面 / 零角动量 / 极端半长轴）恰是玩家会在
        /// 规划器里试出来的输入，故加硬上限并走"无解"异常分支——调用点已有 try/catch 兜底
        /// （规划器回落为 Lambert 无解提示，制导本次跳过）。
        /// </summary>
        public const int MAX_SERIES_ITER = 200;

        // ── 探针计数（纯 C#、无 Verse 依赖——本文件同时被 Pure.Tests 链接编译，故不用 Log/Find）──
        // 由 PerfProbe 汇总打印读取。判据：DebugSeriesCapHits 正常玩法下必须恒为 0；
        // DebugMaxSeriesIter 用于确认 200 的余量（真实收敛所需迭代数应远小于它）。
        /// <summary>TLAMB 调用次数。</summary>
        public static long DebugTlambCalls;
        /// <summary>级数迭代触顶次数（触顶 = 判定不收敛并抛异常）。</summary>
        public static long DebugSeriesCapHits;
        /// <summary>实测最大级数迭代数。</summary>
        public static int DebugMaxSeriesIter;

        public static (DVector3 Vi, DVector3 Vf) Solve(double mu, DVector3 r1, DVector3 r2, double tof,
            TransferGeometry direction = TransferGeometry.ShortWay, int nrev = 0, DVector3? h = null)
        {
            if (tof <= 0)
                throw new ArgumentException("Gooding Lambert requires positive time of flight");

            DVector3 ux1 = r1.normalized;
            DVector3 ux2 = r2.normalized;

            DVector3 uz1 = DVector3.Cross(ux1, ux2);
            double uz1mag = uz1.magnitude;

            // 处理共线情况（霍曼转移的精确解）
            if (uz1mag < 1e-12)
            {
                return HohmannSolve(mu, r1.magnitude, r2.magnitude, ux1, direction);
            }

            uz1 /= uz1mag;

            double theta = SafeAcos(DVector3.Dot(ux1, ux2));

            bool flip = direction == TransferGeometry.LongWay || direction == TransferGeometry.Retrograde;
            if (direction == TransferGeometry.Prograde || direction == TransferGeometry.Retrograde)
            {
                if (h == null)
                    throw new ArgumentException("Prograde/Retrograde require normal vector h");
                flip ^= DVector3.Dot(uz1, h.Value) < 0;
            }

            if (flip)
            {
                theta = TAU - theta;
                uz1 = -uz1;
            }

            DVector3 uz2 = uz1;
            DVector3 uy1 = DVector3.Cross(uz1, ux1).normalized;
            DVector3 uy2 = DVector3.Cross(uz2, ux2).normalized;
            theta += TAU * Abs(nrev);

            (int n, double VR11, double VT11, double VR12, double VT12,
                double VR21, double VT21, double VR22, double VT22) = VLAMB(mu, r1.magnitude, r2.magnitude, theta, tof);

            if (n < 0)
                throw new Exception($"Gooding VLAMB failed: no tminimum found");
            if (n == 0)
                throw new Exception($"Gooding VLAMB failed: no solution for given time of flight");

            DVector3 Vi, Vf;
            if (nrev > 0 && n > 1 && VR11 * VR11 + VT11 * VT11 < MIN_VALID_SPEED)
            {
                Vi = VR21 * ux1 + VT21 * uy1;
                Vf = VR22 * ux2 + VT22 * uy2;
            }
            else
            {
                Vi = VR11 * ux1 + VT11 * uy1;
                Vf = VR12 * ux2 + VT12 * uy2;
            }

            // 最终安全检查：速度不能为零
            if (Vi.magnitude < MIN_VALID_SPEED)
                throw new Exception("Gooding returned zero velocity for Vi");
            if (Vf.magnitude < MIN_VALID_SPEED)
                throw new Exception("Gooding returned zero velocity for Vf");

            return (Vi, Vf);
        }

        /// <summary>
        /// 共线轨道的精确霍曼转移解（避免 Lambert 在 θ≈π 处的奇异性）。
        /// 使用径向单位向量计算轨道平面内的切向方向。
        /// </summary>
        private static (DVector3 Vi, DVector3 Vf) HohmannSolve(double mu, double r1, double r2, DVector3 radialDir, TransferGeometry direction)
        {
            double a = (r1 + r2) * 0.5;
            double v1mag = Sqrt(mu * (2.0 / r1 - 1.0 / a));
            double v2mag = Sqrt(mu * (2.0 / r2 - 1.0 / a));

            // 切向方向：在轨道平面内垂直于径向。
            // 取径向与任意不共线参考轴的叉积得到轨道平面内的切线。
            DVector3 tangent = DVector3.Cross(radialDir, DVector3.forward);
            if (tangent.sqrMagnitude < 0.01)
                tangent = DVector3.Cross(radialDir, DVector3.up);
            tangent = tangent.normalized;

            // ShortWay: 沿切线正方向；LongWay: 沿切线负方向
            double sign = (direction == TransferGeometry.LongWay || direction == TransferGeometry.Retrograde) ? -1.0 : 1.0;
            DVector3 Vi = tangent * (sign * v1mag);
            DVector3 Vf = tangent * (sign * v2mag);

            return (Vi, Vf);
        }

        // ====== 以下为 MechJeb2 Gooding 核心函数直接移植 ======

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static (int N, double VR11, double VT11, double VR12, double VT12, double VR21, double VT21, double VR22,
            double VT22) VLAMB(double GM, double R1, double R2, double TH, double TDELT)
        {
            double VR11 = 0.0, VT11 = 0.0, VR12 = 0.0, VT12 = 0.0;
            double VR21 = 0.0, VT21 = 0.0, VR22 = 0.0, VT22 = 0.0;
            int M = (int)Floor(TH / TAU);
            double THR2 = TH * 0.5 - M * PI;
            double DR = R1 - R2;
            double R1R2 = R1 * R2;
            double R1R2TH = 4.0 * R1R2 * Pow(Sin(THR2), 2);
            double CSQ = DR * DR + R1R2TH;
            double C = Sqrt(CSQ);
            double S = (R1 + R2 + C) * 0.5;
            double GMS = Sqrt(GM * S * 0.5);
            double QSQFM1 = C / S;
            double Q = Sqrt(R1R2) * Cos(THR2) / S;
            double RHO, SIG;
            if (C != 0.0)
            {
                RHO = DR / C;
                SIG = R1R2TH / CSQ;
            }
            else
            {
                RHO = 0.0;
                SIG = 1.0;
            }

            double T = 4.0 * GMS * TDELT / (S * S);

            (int N, double X1, double X2) = XLAMB(M, Q, QSQFM1, T);

            for (int I = 1; I <= N; I++)
            {
                double X = I == 1 ? X1 : X2;

                (_, double QZMINX, double QZPLX, double ZPLQX) = TLAMB(M, Q, QSQFM1, X, -1);

                double VT2 = GMS * ZPLQX * Sqrt(SIG);
                double VR1 = GMS * (QZMINX - QZPLX * RHO) / R1;
                double VT1 = VT2 / R1;
                double VR2 = -GMS * (QZMINX + QZPLX * RHO) / R2;
                VT2 /= R2;

                if (I == 1)
                {
                    VR11 = VR1; VT11 = VT1;
                    VR12 = VR2; VT12 = VT2;
                }
                else
                {
                    VR21 = VR1; VT21 = VT1;
                    VR22 = VR2; VT22 = VT2;
                }
            }

            return (N, VR11, VT11, VR12, VT12, VR21, VT21, VR22, VT22);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (int N, double X, double XPL) XLAMB(int M, double Q, double QSQFM1, double TIN)
        {
            const double TOL = 3e-7;
            const double C0 = 1.7;
            const double C1 = 0.5;
            const double C2 = 0.03;
            const double C3 = 0.15;
            const double C41 = 1.0;
            const double C42 = 0.24;

            double THR2 = Atan2(QSQFM1, 2.0 * Q) / PI;
            double T, DT, D2T, D3T;
            double D2T2 = 0.0;
            double TMIN = 0.0;
            double TDIFF, TDIFFM = 0.0, XM = 0.0, W;
            double X = 0.0, XPL = 0.0;
            int N;

            if (M == 0)
            {
                N = 1;
                double T0;
                (T0, _, _, _) = TLAMB(M, Q, QSQFM1, 0.0, 0);

                TDIFF = TIN - T0;
                if (TDIFF <= 0.0)
                {
                    X = T0 * TDIFF / (-4.0 * TIN);
                }
                else
                {
                    X = -TDIFF / (TDIFF + 4.0);
                    W = X + C0 * Sqrt(2.0 * (1.0 - THR2));
                    if (W < 0.0)
                        X -= Sqrt(Pow(-W, 1.0 / 8.0)) * (X + Sqrt(TDIFF / (TDIFF + 1.5 * T0)));
                    W = 4.0 / (4.0 + TDIFF);
                    X *= 1.0 + X * (C1 * W - C2 * X * Sqrt(W));
                }
                goto Five;
            }

            // Multi-rev case
            XM = 1.0 / (1.5 * (M + 0.5) * PI);
            if (THR2 < 0.5)
                XM = Pow(2.0 * THR2, 1.0 / 8.0) * XM;
            if (THR2 > 0.5)
                XM = (2.0 - Pow(2.0 - 2.0 * THR2, 1.0 / 8.0)) * XM;

            for (int I = 1; I <= 12; I++)
            {
                (TMIN, DT, D2T, D3T) = TLAMB(M, Q, QSQFM1, XM, 3);
                if (D2T == 0.0) goto Two;
                double XMOLD = XM;
                XM -= DT * D2T / (D2T * D2T - DT * D3T * 0.5);
                if (Abs(XMOLD / XM - 1.0) <= TOL) goto Two;
            }

            return (-1, X, XPL);

        Two:
            TDIFFM = TIN - TMIN;
            if (TDIFFM < 0.0) return (0, X, XPL);
            if (TDIFFM == 0.0) return (1, XM, XPL);

            N = 3;
            if (D2T == 0.0) D2T = 6.0 * M * PI;
            X = Sqrt(TDIFFM / (D2T * 0.5 + TDIFFM / Pow(1.0 - XM, 2.0)));
            W = XM + X;
            W = W * 4.0 / (4.0 + TDIFFM) + Pow(1.0 - W, 2.0);
            X = X * (1.0 - (1.0 + M + C41 * (THR2 - 0.5)) / (1.0 + C3 * M) * X * (C1 * W + C2 * X * Sqrt(W))) + XM;
            D2T2 = D2T * 0.5;
            if (X >= 1.0) { N = 1; goto Three; }

        Five:
            for (int I = 1; I <= 3; I++)
            {
                (T, DT, D2T, _) = TLAMB(M, Q, QSQFM1, X, 2);
                T = TIN - T;
                if (DT != 0.0)
                    X += T * DT / (DT * DT + T * D2T * 0.5);
            }

            if (N != 3) return (N, X, XPL);

            N = 2;
            XPL = X;

        Three:
            double T0b;
            (T0b, _, _, _) = TLAMB(M, Q, QSQFM1, 0.0, 0);

            double TDIFF0 = T0b - TMIN;
            TDIFF = TIN - T0b;
            if (TDIFF <= 0.0)
            {
                X = XM - Sqrt(TDIFFM / (D2T2 - TDIFFM * (D2T2 / TDIFF0 - 1.0 / (XM * XM))));
            }
            else
            {
                X = -TDIFF / (TDIFF + 4.0);
                W = X + C0 * Sqrt(2.0 * (1.0 - THR2));
                if (W < 0.0)
                    X -= Sqrt(Pow(-W, 1.0 / 8.0)) * (X + Sqrt(TDIFF / (TDIFF + 1.5 * T0b)));
                W = 4.0 / (4.0 + TDIFF);
                X *= 1.0 + (1.0 + M + C42 * (THR2 - 0.5)) / (1.0 + C3 * M) * X * (C1 * W - C2 * X * Sqrt(W));
                if (X <= -1.0)
                {
                    N -= 1;
                    if (N == 1) X = XPL;
                }
            }
            goto Five;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double T, double DT, double D2T, double D3T) TLAMB(int M, double Q, double QSQFM1, double X, int N)
        {
            DebugTlambCalls++;
            const double SW = 0.4;
            bool LM1 = N == -1;
            bool L1 = N >= 1;
            bool L2 = N >= 2;
            bool L3 = N == 3;
            double QSQ = Q * Q;
            double XSQ = X * X;
            double U = (1.0 - X) * (1.0 + X);
            double T = 0.0, DT = 0.0, D2T = 0.0, D3T = 0.0;

            if (LM1 || M > 0 || X < 0.0 || Abs(U) > SW)
            {
                double Y = Sqrt(Abs(U));
                double Z = Sqrt(QSQFM1 + QSQ * XSQ);
                double QX = Q * X;
                double A = 0.0, B = 0.0, AA = 0.0, BB = 0.0;

                if (QX <= 0.0) { A = Z - QX; B = Q * Z - X; }
                if (QX < 0.0 && LM1) { AA = QSQFM1 / A; BB = QSQFM1 * (QSQ * U - XSQ) / B; }
                if ((QX == 0.0 && LM1) || QX > 0.0) { AA = Z + QX; BB = Q * Z + X; }
                if (QX > 0.0) { A = QSQFM1 / AA; B = QSQFM1 * (QSQ * U - XSQ) / BB; }

                if (!LM1)
                {
                    double G;
                    if (QX * U >= 0.0)
                        G = X * Z + Q * U;
                    else
                        G = (XSQ - QSQ * U) / (X * Z - Q * U);

                    double F = A * Y;
                    if (X <= 1.0)
                        T = M * PI + Atan2(F, G);
                    else
                    {
                        if (F > SW)
                            T = Log(F + G);
                        else
                        {
                            double FG1 = F / (G + 1.0);
                            double TERM = 2.0 * FG1;
                            double FG1SQ = FG1 * FG1;
                            T = TERM;
                            double TWOI1 = 1.0;
                            double TOLD;
                            bool converged = false;
                            int iter = 0;
                            do
                            {
                                TWOI1 += 2.0;
                                TERM *= FG1SQ;
                                TOLD = T;
                                T += TERM / TWOI1;
                                converged = (T == TOLD);
                            } while (!converged && ++iter < MAX_SERIES_ITER);
                            if (iter > DebugMaxSeriesIter) DebugMaxSeriesIter = iter;
                            if (!converged)
                            {
                                DebugSeriesCapHits++;
                                throw new Exception("Gooding TLAMB series did not converge");
                            }
                        }
                    }

                    T = 2.0 * (T / Y + B) / U;
                    if (L1 && Z != 0.0)
                    {
                        double QZ = Q / Z;
                        double QZ2 = QZ * QZ;
                        QZ *= QZ2; // now QZ = Q/Z^3
                        DT = (3.0 * X * T - 4.0 * (A + QX * QSQFM1) / Z) / U;
                        if (L2) D2T = (3.0 * T + 5.0 * X * DT + 4.0 * QZ * QSQFM1) / U;
                        if (L3) D3T = (8.0 * DT + 7.0 * X * D2T - 12.0 * QZ * QZ2 * X * QSQFM1) / U;
                    }
                }
                else
                {
                    DT = B; D2T = BB; D3T = AA;
                }
            }
            else
            {
                // 级数展开
                double U0I = 1.0, U1I = 0.0, U2I = 0.0, U3I = 0.0;
                if (L1) U1I = 1.0;
                if (L2) U2I = 1.0;
                if (L3) U3I = 1.0;
                double TERM = 4.0;
                double TQ = Q * QSQFM1;
                int I = 0;
                double TQSUM = (Q < 0.5) ? (1.0 - Q * QSQ) : ((1.0 / (1.0 + Q) + Q) * QSQFM1);
                double TTMOLD = TERM / 3.0;
                T = TTMOLD * TQSUM;
                double TOLD;
                // 上限 = N（级数阶数下限，必须跑满）+ MAX_SERIES_ITER 收敛余量：不会截断任何真收敛的输入
                int maxIter = (N > 0 ? N : 0) + MAX_SERIES_ITER;
                bool converged2 = false;
                int iter2 = 0;
                do
                {
                    I++;
                    int P = I;
                    U0I *= U;
                    if (L1 && I > 1) U1I *= U;
                    if (L2 && I > 2) U2I *= U;
                    if (L3 && I > 3) U3I *= U;
                    TERM = TERM * (P - 0.5) / P;
                    TQ *= QSQ;
                    TQSUM += TQ;
                    TOLD = T;
                    double TTERM = TERM / (2.0 * P + 3.0);
                    double TQTERM = TTERM * TQSUM;
                    T -= U0I * ((1.5 * P + 0.25) * TQTERM / (P * P - 0.25) - TTMOLD * TQ);
                    TTMOLD = TTERM;
                    TQTERM *= P;
                    if (L1) DT += TQTERM * U1I;
                    if (L2) D2T += TQTERM * U2I * (P - 1.0);
                    if (L3) D3T += TQTERM * U3I * (P - 1.0) * (P - 2.0);
                    // 原终止条件 (I < N || T != TOLD) 的等价取反：跑满 N 阶且本级增量已归零
                    converged2 = I >= N && T == TOLD;
                } while (!converged2 && ++iter2 < maxIter);

                if (iter2 > DebugMaxSeriesIter) DebugMaxSeriesIter = iter2;
                if (!converged2)
                {
                    DebugSeriesCapHits++;
                    throw new Exception("Gooding TLAMB series expansion did not converge");
                }

                if (L3) D3T = 8.0 * X * (1.5 * D2T - XSQ * D3T);
                if (L2) D2T = 2.0 * (2.0 * XSQ * D2T - DT);
                if (L1) DT = -2.0 * X * DT;
                T /= XSQ;
            }

            return (T, DT, D2T, D3T);
        }
    }
}

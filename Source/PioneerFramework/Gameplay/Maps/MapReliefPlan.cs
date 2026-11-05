using System;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// 地图浮雕的**纯逻辑**：逐格分类网格 → 矩形盒体清单（同类合并、超限降采样）。
    ///
    /// 分类口径（用户 2026-10-02 裁定，三色三档）：
    ///   0 = 不画（纯太空、无建造痕迹）
    ///   1 = 结构梁（有地板/建筑，但不成房间）
    ///   2 = 舱室（成房间、未全屋顶）
    ///   3 = 装甲（成房间且全屋顶）
    ///
    /// 无 Verse / Unity 依赖（只吃 byte[]/int）⇒ 链进 `PioneerFramework.Pure.Tests`，
    /// 钉住"面积守恒 / 不重叠 / 确定性 / 超限单调降采样"。
    /// 分类**怎么算**在 Map 侧（`SpaceMapComp.RebuildRelief`）——那部分要读地形/房间/屋顶，天然不纯。
    /// </summary>
    public static class MapReliefPlan
    {
        public const byte None = 0;
        public const byte Beam = 1;    // 结构梁
        public const byte Cabin = 2;   // 舱室
        public const byte Armor = 3;   // 装甲

        /// <summary>盒体数上限。每盒 24 顶点 / 36 索引 ⇒ 12000 盒 ≈ 顶点 28.8 万（UInt32 索引），
        /// 是"最坏情况仍可一帧送出"的量级；超过则 2×2 降采样（块内取**最大**分类）再合并。</summary>
        public const int DefaultMaxBoxes = 12000;

        /// <summary>轴对齐盒体（格坐标，闭区间 [x0..x1]×[z0..z1]；cls ∈ 1..3）。</summary>
        public struct Box
        {
            public int x0;
            public int z0;
            public int x1;
            public int z1;
            public byte cls;
        }

        /// <summary>
        /// 分类网格 → 盒体清单。先按 1 格精度合并；盒体数超限则逐级 2×2 降采样
        /// （块内取最大分类 ⇒ 只增不减覆盖，绝不把已画的格子弄丢），直到达标（或降到 64× 为止）。
        /// </summary>
        public static List<Box> Build(byte[] classes, int w, int h, int maxBoxes = DefaultMaxBoxes)
        {
            var boxes = new List<Box>();
            if (classes == null || w <= 0 || h <= 0 || classes.Length < w * h)
                return boxes;

            int coarsen = 1;
            boxes = MergeGrid(classes, w, h, coarsen);
            while (boxes.Count > maxBoxes && coarsen < 64)
            {
                coarsen *= 2;
                boxes = MergeGrid(classes, w, h, coarsen);
            }
            return boxes;
        }

        /// <summary>蔓延轮数上限（RimWorld 的墙 1 格、自建舱壁 1–2 格，8 轮远远够；纯保险）。</summary>
        public const int DefaultPropagationRounds = 8;

        /// <summary>
        /// 把"参与形成房间的结构格"归到房间档（用户 2026-10-02："参与形成房间的墙体也计入房间渲染模式"）。
        ///
        /// 迭代式最大蔓延：**每轮读上一轮的快照**（⇒ 结果与扫描方向无关，逐格结果确定）。
        ///   · 第 1 轮：任何 <see cref="Beam"/> 格只要正交邻接房间档 ⇒ 取邻格最大档
        ///     （这一轮覆盖门：门可通行、不隔房间，但它确实是房间围护的一部分）；
        ///   · 之后只沿 <paramref name="solid"/> = 1 的格继续（2 格厚的舱壁外层、环舱一圈的墙都算房间的一部分）。
        /// 为什么不会漏到甲板上：可通行格必有区划，而"贴着房间的可通行格"必与该房间同区
        /// （区划是极大的可通行连通块）⇒ 它本身就是房间档，不存在"甲板被邻屋带跑"。
        ///
        /// 效果（纯测试钉的口径）：每个结构格归**最近**的那个房间；到多个房间等距时取**高档**；
        /// 一旦归房就不再改（先到先得）——两面都是房间的一道厚墙会按"离谁近算谁"分掉。
        /// </summary>
        /// <param name="classes">就地升级的分类网格（0/1/2/3）。</param>
        /// <param name="solid">1 = 不可通行结构格（墙/机柜…），0 = 可通行（地板/门）。与 classes 同长。</param>
        public static void PropagateRoomClassToStructure(byte[] classes, byte[] solid, int w, int h,
            int maxRounds = DefaultPropagationRounds)
        {
            int cells = w * h;
            if (classes == null || solid == null || w <= 0 || h <= 0
                || classes.Length < cells || solid.Length < cells)
                return;

            var prev = new byte[cells];
            for (int round = 0; round < maxRounds; round++)
            {
                Buffer.BlockCopy(classes, 0, prev, 0, cells);
                bool changed = false;

                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = z * w + x;
                        if (prev[i] != Beam)
                            continue;                       // 只处理尚未归房的结构格
                        if (round > 0 && solid[i] == 0)
                            continue;                       // 第 1 轮之后：只沿不可通行格蔓延

                        byte best = Beam;
                        if (x > 0 && prev[i - 1] > best) best = prev[i - 1];
                        if (x + 1 < w && prev[i + 1] > best) best = prev[i + 1];
                        if (z > 0 && prev[i - w] > best) best = prev[i - w];
                        if (z + 1 < h && prev[i + w] > best) best = prev[i + w];

                        if (best > Beam && classes[i] != best)
                        {
                            classes[i] = best;
                            changed = true;
                        }
                    }
                }

                if (!changed)
                    break;
            }
        }

        /// <summary>按 k×k 降采样后做"行游程 + 跨行同类同区间延伸"的贪心矩形合并。k=1 时在原始格上合并。</summary>
        private static List<Box> MergeGrid(byte[] classes, int w, int h, int k)
        {
            int gw = (w + k - 1) / k;
            int gh = (h + k - 1) / k;

            // 降采样网格：块内取最大分类（0 可被覆盖 ⇒ 形状只会变胖，不会丢）。
            byte[] grid;
            if (k == 1)
            {
                grid = classes;
            }
            else
            {
                grid = new byte[gw * gh];
                for (int gz = 0; gz < gh; gz++)
                {
                    int zMax = Math.Min(h, (gz + 1) * k);
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int xMax = Math.Min(w, (gx + 1) * k);
                        byte best = None;
                        for (int z = gz * k; z < zMax; z++)
                        {
                            int row = z * w;
                            for (int x = gx * k; x < xMax; x++)
                            {
                                byte c = classes[row + x];
                                if (c > best) best = c;
                            }
                        }
                        grid[gz * gw + gx] = best;
                    }
                }
            }

            var boxes = new List<Box>();
            var openRects = new List<Box>();   // 上一行仍未闭合、本行可能继续延伸的矩形
            var runs = new List<Box>();        // 本行游程缓冲（复用）
            var nextOpen = new List<Box>();
            for (int gz = 0; gz < gh; gz++)
            {
                runs.Clear();
                int row = gz * gw;
                int x = 0;
                while (x < gw)
                {
                    byte c = grid[row + x];
                    if (c == None) { x++; continue; }
                    int x0 = x;
                    while (x + 1 < gw && grid[row + x + 1] == c) x++;
                    runs.Add(new Box { x0 = x0, z0 = gz, x1 = x, z1 = gz, cls = c });
                    x++;
                }

                nextOpen.Clear();
                for (int i = 0; i < runs.Count; i++)
                {
                    Box run = runs[i];
                    int match = -1;
                    for (int j = 0; j < openRects.Count; j++)
                    {
                        Box open = openRects[j];
                        if (open.cls == run.cls && open.x0 == run.x0 && open.x1 == run.x1)
                        {
                            match = j;
                            break;
                        }
                    }
                    if (match >= 0)
                    {
                        Box ext = openRects[match];
                        ext.z1 = gz;
                        nextOpen.Add(ext);
                        openRects.RemoveAt(match);
                    }
                    else
                    {
                        nextOpen.Add(run);
                    }
                }
                boxes.AddRange(openRects);   // 本行没能延伸的 ⇒ 到此闭合
                openRects.Clear();
                var swap = openRects;
                openRects = nextOpen;
                nextOpen = swap;
            }
            boxes.AddRange(openRects);

            if (k > 1)
            {
                for (int i = 0; i < boxes.Count; i++)
                {
                    Box b = boxes[i];
                    b.x0 *= k;
                    b.z0 *= k;
                    b.x1 = Math.Min(w - 1, (b.x1 + 1) * k - 1);
                    b.z1 = Math.Min(h - 1, (b.z1 + 1) * k - 1);
                    boxes[i] = b;
                }
            }
            return boxes;
        }
    }
}

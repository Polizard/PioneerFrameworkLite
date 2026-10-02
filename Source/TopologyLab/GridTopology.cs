// ============================================================================
//  方格连通性实验室 —— 核心实现
//
//  要回答的问题：在方格地图上"拿掉一格 / 放上一格"之后，图形断没断、断成几块。
//
//  三件事撑起全部结论（依据见 DesignNotes/奥德赛飞船系统研究-20260924.md §14/§15）：
//    ① 绕数 = 连通块数 − 孔洞数  —— 平面图欧拉公式在方格上的离散形式
//         χ = w/4 = c − h        ⇒ 维护 w 与 h 即可 O(1) 得到 c
//    ② w 局部可算：每个格点看周围 4 格，按 1 / 3 / 对角 给 +1 / −1 / ±2
//         （Gray 1971 的 2×2 凸凹计数，工程上叫 bit-quad）
//    ③ 前景与背景必须用互补配对（4 邻域前景+8 邻域背景，或反过来），
//         混用会破坏 Jordan 定理 ⇒ 角点的"对角"项随配对变号
//
//  由此得到的不对称解法（也正是文献里的做法）：
//    · 拿掉一格 ⇒ 背景只会"合并"（并查集 O(α) 精确给 Δh），
//                 于是 Δc = Δw/4 + Δh 也是 O(1) ⇒ 只有 Δc ≠ 0 才需要修复前景
//    · 放上一格 ⇒ 前景只会"合并"（并查集 O(α) 精确给 Δc），
//                 于是 Δh = Δc − Δw/4 也是 O(1) ⇒ 背景结构的修复可以推迟/批量
//    · 真的断裂时：从被改动格的邻居各自 flood，每块重挂新根
//
//  文献：Łącki–Sankowski STACS 2015（平面图删除 O(1) 摊还；双图并查集；
//        断裂判据 = "删边两侧是否同面"）；Pătraşcu–Demaine 2004（全动态 Ω(log n)
//        下界，平面图同样成立 ⇒ 完全动态的 O(1) 不可能）。
//
//  ★ 坐标纪律（踩过一次，务必看清）：
//      · 内部坐标 (x, y)：0..W-1 / 0..H-1，对外 API 用它
//      · padded 索引 i：i = (y+1)*Size + (x+1)，内部数组用它
//      · 两者不可混用：凡是要按 padded 索引取邻居的，先取 px=i%Size, py=i/Size
// ============================================================================

using System;
using System.Collections.Generic;

namespace TopologyLab
{
    /// <summary>连通性约定。前景与背景必须互补配对。</summary>
    public enum Conn
    {
        /// <summary>前景 4 邻域 + 背景 8 邻域（"必须共边才算连"）。</summary>
        Fg4Bg8 = 0,

        /// <summary>前景 8 邻域 + 背景 4 邻域（"对角相碰也算连"）。</summary>
        Fg8Bg4 = 1,
    }

    /// <summary>一次全量分析的结果（暴力基准，也是正确性权威）。</summary>
    public sealed class Analysis
    {
        public int Comps;          // 前景连通块数 c
        public int Holes;          // 孔洞数 h（不连外部背景的背景连通分量数）
        public int[] FgLabel;      // 每格前景标签（0 = 空格）
        public int[] BgLabel;      // 每格背景标签（0 = 前景）
        public bool[] BgTouch;     // 背景标签是否连到外部（按标签索引）
    }

    /// <summary>方格地图 + 暴力全量算法（O(格子数)，正确性权威）。</summary>
    public static class GridBrute
    {
        private static readonly int[] Off4 = { 1, 0, -1, 0, 0, 1, 0, -1 };
        private static readonly int[] Off8 =
        {
            1, 0, -1, 0, 0, 1, 0, -1,
            1, 1, 1, -1, -1, 1, -1, -1,
        };

        public static int[] FgOffsets(Conn conn) => conn == Conn.Fg4Bg8 ? Off4 : Off8;
        public static int[] BgOffsets(Conn conn) => conn == Conn.Fg4Bg8 ? Off8 : Off4;

        /// <summary>全量重算绕数（四分之一转为单位）。</summary>
        public static long CornerSum(bool[] present, int size, int hsize, Conn conn)
        {
            long w = 0;
            for (int vy = 0; vy <= hsize; vy++)
                for (int vx = 0; vx <= size; vx++)
                    w += VertexContrib(present, size, hsize, vx, vy, conn);
            return w;
        }

        /// <summary>
        /// 单个格点的绕数贡献。恰好 1 格 = 凸角 +1；恰好 3 格 = 凹角 −1；
        /// 2 格对角 = ±2（符号即"共点算不算连"的开关）。
        /// </summary>
        public static int VertexContrib(bool[] present, int size, int hsize, int vx, int vy, Conn conn)
        {
            bool a = CellAt(present, size, hsize, vx - 1, vy - 1); // NW
            bool b = CellAt(present, size, hsize, vx, vy - 1);     // NE
            bool c = CellAt(present, size, hsize, vx - 1, vy);     // SW
            bool d = CellAt(present, size, hsize, vx, vy);         // SE
            int n = (a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0) + (d ? 1 : 0);
            if (n == 1) return 1;
            if (n == 3) return -1;
            if (n == 2 && ((a && d) || (b && c))) return conn == Conn.Fg4Bg8 ? 2 : -2;
            return 0;
        }

        public static bool CellAt(bool[] present, int size, int hsize, int x, int y)
        {
            if (x < 0 || y < 0 || x >= size || y >= hsize) return false;
            return present[y * size + x];
        }

        /// <summary>全量分析：前景分量 + 孔洞 + 每格标签。</summary>
        public static Analysis Analyze(bool[] present, int size, int hsize, Conn conn)
        {
            var res = new Analysis
            {
                FgLabel = new int[size * hsize],
                BgLabel = new int[size * hsize],
                BgTouch = new bool[size * hsize],
            };
            var queue = new int[size * hsize];

            // ---- 前景分量 ----
            int[] fgOff = FgOffsets(conn);
            int next = 1;
            for (int i = 0; i < present.Length; i++)
            {
                if (!present[i] || res.FgLabel[i] != 0) continue;
                int label = next++;
                int head = 0, tail = 0;
                queue[tail++] = i;
                res.FgLabel[i] = label;
                while (head < tail)
                {
                    int cur = queue[head++];
                    int cx = cur % size, cy = cur / size;
                    for (int k = 0; k < fgOff.Length; k += 2)
                    {
                        int nx = cx + fgOff[k], ny = cy + fgOff[k + 1];
                        if (nx < 0 || ny < 0 || nx >= size || ny >= hsize) continue;
                        int ni = ny * size + nx;
                        if (!present[ni] || res.FgLabel[ni] != 0) continue;
                        res.FgLabel[ni] = label;
                        queue[tail++] = ni;
                    }
                }
            }
            res.Comps = next - 1;

            // ---- 背景分量 + 是否连到外部 ----
            int[] bgOff = BgOffsets(conn);
            next = 1;
            for (int i = 0; i < present.Length; i++)
            {
                if (present[i] || res.BgLabel[i] != 0) continue;
                int label = next++;
                int head = 0, tail = 0;
                queue[tail++] = i;
                res.BgLabel[i] = label;
                bool touch = false;
                while (head < tail)
                {
                    int cur = queue[head++];
                    int cx = cur % size, cy = cur / size;
                    if (cx == 0 || cy == 0 || cx == size - 1 || cy == hsize - 1) touch = true;
                    for (int k = 0; k < bgOff.Length; k += 2)
                    {
                        int nx = cx + bgOff[k], ny = cy + bgOff[k + 1];
                        if (nx < 0 || ny < 0 || nx >= size || ny >= hsize) continue;
                        int ni = ny * size + nx;
                        if (present[ni] || res.BgLabel[ni] != 0) continue;
                        res.BgLabel[ni] = label;
                        queue[tail++] = ni;
                    }
                }
                res.BgTouch[label] = touch;
            }
            for (int l = 1; l < next; l++) if (!res.BgTouch[l]) res.Holes++;
            return res;
        }
    }


    /// <summary>
    /// 并查集：节点可以"只增不减"地分配——因为并查集**不支持删除**，
    /// 而我们的格子会被反复拿走/放回。做法：每次"放上一格"给它一个**全新节点**，
    /// 旧节点不回收（靠 Compact 定期整理），于是永远不会出现
    /// "被拿走的格子还挂着孩子、放回时把别人的分量并进来"这种错合并。
    /// </summary>
    internal sealed class UnionFind
    {
        public int[] Parent = new int[1024];
        public int[] Size = new int[1024];
        public int NodeCount;
        public bool GuardFind;

        public int NewSingleton()
        {
            if (NodeCount == 0) NodeCount = 1;   // 0 保留给"无节点"
            if (NodeCount >= Parent.Length)
            {
                Array.Resize(ref Parent, Parent.Length * 2);
                Array.Resize(ref Size, Size.Length * 2);
            }
            int p = NodeCount++;
            Parent[p] = p;
            Size[p] = 1;
            return p;
        }

        public int Find(int i)
        {
            int root = i;
            if (GuardFind)
            {
                int steps = 0;
                while (Parent[root] != root)
                {
                    root = Parent[root];
                    if (++steps > Parent.Length) throw new InvalidOperationException($"Find 成环（起点 {i}）");
                }
            }
            else
            {
                while (Parent[root] != root) root = Parent[root];
            }
            while (Parent[i] != root) { int nx = Parent[i]; Parent[i] = root; i = nx; }
            return root;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (Size[ra] < Size[rb]) { int t = ra; ra = rb; rb = t; }
            Parent[rb] = ra;
            Size[ra] += Size[rb];
            Size[rb] = 0;
        }

        public void Reset()
        {
            NodeCount = 0;
        }
    }

    /// <summary>
    /// 增量实现：绕数 w（O(1) 局部计数）+ 前景并查集 + 背景并查集 + 孔洞数 h。
    /// 恒等式 χ = w/4 = c − h 用来在 O(1) 内判断"这次改动到底有没有改变连通块数"，
    /// 只有真的变了才去 flood 修复（且只重挂受影响的那几块）。
    ///
    /// 关键设计（撞过墙才定下来的）：
    ///   · 并查集**不支持删除节点**，而格子会被反复拿走/放回 ⇒
    ///     每次"放上一格"给它一个**全新节点**，永不复用旧节点，
    ///     否则"被拿走的格子还挂着孩子、放回时把别人的分量并进来"⇒ 错合并。
    ///   · 节点只增不减 ⇒ 定期 Compact（按整图重建，O(格子数)），
    ///     并在"局部修复的对不上账"时当作兜底（正确性优先）。
    /// </summary>
    public sealed class IncTopology
    {
        public readonly int Size, HSize, W, H;
        private readonly Conn _conn;
        private readonly int[] _fgOff, _bgOff;
        private readonly bool[] _present;
        private readonly int _cells;

        // 每格 → 并查集节点（0 = 无）
        private readonly int[] _fgNode;
        private readonly int[] _bgNode;
        private readonly UnionFind _fg = new UnionFind();
        private readonly UnionFind _bg = new UnionFind();
        private bool[] _bgTouch = new bool[2048];
        private int _fgCount, _holes, _wCorner;

        private readonly int[] _mark;
        private int _markGen;
        private readonly int[] _queue;

        // 四种互不干扰的暂存（踩过坑：把 List 当定长数组用，靠"上一步残留的 Count"侥幸不炸）
        private readonly int[] _fgRoots = new int[8];
        private readonly int[] _bgRoots = new int[8];
        private readonly List<int> _bgSeeds = new List<int>(16);
        private readonly List<int> _fgSeeds = new List<int>(16);
        private readonly List<List<int>> _pieces = new List<List<int>>();

        /// <summary>关掉 O(1) 检测 = "脏标记 + 局部 BFS"基线（同样工作，只是每次都要 flood）。</summary>
        public bool AlwaysRepair;

        /// <summary>修复时扫描过的格子数（观测代价用，不影响正确性）。</summary>
        public long StatRepairCells;

        /// <summary>兜底 compaction 触发次数（观测用）。</summary>
        public long StatCompactions;

        /// <summary>诊断：修复过程追踪（默认不挂，挂了也不影响热路径）。</summary>
        public Action<string> Trace;

        /// <summary>诊断：Find 成环保护（成环会死循环，比报错难查得多）。</summary>
        public bool GuardFind
        {
            get => _fg.GuardFind;
            set { _fg.GuardFind = value; _bg.GuardFind = value; }
        }

        public IncTopology(int w, int h, Conn conn)
        {
            W = w; H = h; Size = w + 2; HSize = h + 2;
            _conn = conn;
            _fgOff = GridBrute.FgOffsets(conn);
            _bgOff = GridBrute.BgOffsets(conn);
            _cells = Size * HSize;
            _present = new bool[_cells];
            _fgNode = new int[_cells];
            _bgNode = new int[_cells];
            _mark = new int[_cells];
            _queue = new int[_cells];
            if (_bgTouch.Length < 8) _bgTouch = new bool[8];

            // 初始全空 ⇒ 整张图（含 padding）是一个连外部的背景分量
            int root = NewBgNode();
            _bgTouch[root] = true;
            for (int i = 0; i < _cells; i++) _bgNode[i] = root;
            _bg.Size[root] = _cells;
        }

        public bool Present(int x, int y) => _present[Idx(x, y)];
        public int ComponentCount => _fgCount;
        public int HoleCount => _holes;
        public long CornerSum => _wCorner;
        public int RootOf(int paddedIndex) => _bg.Find(_bgNode[paddedIndex]); // 诊断用

        private int Idx(int x, int y) => (y + 1) * Size + (x + 1);

        private int NewFgNode() => _fg.NewSingleton();

        private int NewBgNode()
        {
            int n = _bg.NewSingleton();
            if (n >= _bgTouch.Length) Array.Resize(ref _bgTouch, Math.Max(n + 1, _bgTouch.Length * 2));
            return n;
        }

        // ------------------------------------------------------------------
        //  绕数：只影响被改动格的 4 个角点 ⇒ O(1)
        // ------------------------------------------------------------------
        private int DeltaWForAdd(int x, int y)
        {
            int i = Idx(x, y);
            bool old = _present[i];
            _present[i] = false;
            int before = CellVertexSum(x, y);
            _present[i] = true;
            int after = CellVertexSum(x, y);
            _present[i] = old;
            return after - before;
        }

        private int CellVertexSum(int x, int y)
        {
            int vx = x + 1, vy = y + 1;
            return GridBrute.VertexContrib(_present, Size, HSize, vx, vy, _conn)
                 + GridBrute.VertexContrib(_present, Size, HSize, vx + 1, vy, _conn)
                 + GridBrute.VertexContrib(_present, Size, HSize, vx, vy + 1, _conn)
                 + GridBrute.VertexContrib(_present, Size, HSize, vx + 1, vy + 1, _conn);
        }

        // ------------------------------------------------------------------
        //  放上一格（入参：内部坐标）
        // ------------------------------------------------------------------
        public void Add(int x, int y)
        {
            int i = Idx(x, y);
            if (_present[i]) return;

            int dw = DeltaWForAdd(x, y);
            if (dw % 4 != 0) throw new InvalidOperationException($"Δw 不是 4 的倍数：{dw}");
            _wCorner += dw;
            _present[i] = true;

            // 前景：本格拿一个全新节点，再与邻居所在分量合并（精确给出 Δc）
            int me = NewFgNode();
            _fgNode[i] = me;
            int k = GatherFgRoots(x, y);
            int dc;
            if (k == 0) dc = +1;
            else
            {
                for (int j = 0; j < k; j++) _fg.Union(_fgRoots[j], me);
                dc = -(k - 1);
            }
            _fgCount += dc;

            // 洞数由恒等式来（O(1)，不需要背景结构）
            int dhAdd = dc - dw / 4;
            _holes += dhAdd;

            // 背景：该格离开背景。它的 bg 节点留在树里当"穿线"，
            // 由 BgRepair 负责把被它连起来的部分拆开（或整块重挂）。
            int oldBgRoot = _bg.Find(_bgNode[i]);
            _bgNode[i] = 0;
            _bg.Size[oldBgRoot]--;
            if (_bg.Size[oldBgRoot] < 0) _bg.Size[oldBgRoot] = 0;

            // 背景结构是否必须重排？有一条**可证明无害的跳过**：
            //   背景只会在"放上一格"时分裂。设原分量为 C（是否连外部 = _bgTouch[oldBgRoot]），
            //   分裂成若干块后 Δh = (其中"有限块"的个数) − (C 有限 ? 1 : 0)。
            //   于是：若 C 连外部且 Δh == 0 ⇒ **一块有限块都没产生** ⇒ 所有块都不连外部。
            //   而背景结构唯一被用到的地方是将来"拿掉一格"时数"有几个不同的邻居分量且有限"——
            //   把两个都连外部的块当成一块，`finiteBefore` 与合并后的 touch 都不变 ⇒ Δh 完全一致。
            //   ⇒ 这种情况可以整块保留，省掉一次泛洪（实测这是单格操作的最大开销来源）。
            if (_bg.Size[oldBgRoot] > 0 || AlwaysRepair)
            {
                bool harmlessSkip = !AlwaysRepair && dhAdd == 0 && _bgTouch[oldBgRoot];
                if (!harmlessSkip)
                {
                    int px = i % Size, py = i / Size;
                    _bgSeeds.Clear();
                    for (int t = 0; t < _bgOff.Length; t += 2)
                    {
                        int nx = px + _bgOff[t], ny = py + _bgOff[t + 1];
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                        _bgSeeds.Add(ny * Size + nx);
                    }
                    if (_bgSeeds.Count > 0) BgRepair(_bgSeeds);
                }
            }

            MaybeCompact();
        }

        // ------------------------------------------------------------------
        //  拿掉一格（入参：内部坐标）
        // ------------------------------------------------------------------
        public void Remove(int x, int y)
        {
            int i = Idx(x, y);
            if (!_present[i]) return;

            int dw = -DeltaWForAdd(x, y);
            if (dw % 4 != 0) throw new InvalidOperationException($"Δw 不是 4 的倍数：{dw}");
            _wCorner += dw;
            _present[i] = false;

            // 背景：只会合并 ⇒ 并查集 O(α) 精确得到 Δh
            int dh = BgAttach(i);
            _holes += dh;

            // 前景：Δc = Δw/4 + Δh ⇒ O(1) 得知是否断裂
            //   注意 Δc 的合法范围是 [−1, +3]：被拿掉的格子本身就是孤块时 c 会减少 1
            //   （我一开始写成"不可能减少块数"，是错的）。
            int dc = dw / 4 + dh;
            if (dc < -1) throw new InvalidOperationException($"拿掉一格最多减少 1 块，得到 {dc}");
            _fgCount += dc;

            _fgNode[i] = 0; // 该节点留在树里当穿线，由修复决定拆不拆
            if (AlwaysRepair || dc > 0)
            {
                int splits = FgRepairSplit(x, y);
                if (dc > 0 && splits != dc)
                    throw new InvalidOperationException($"实际断裂 {splits} 处，增量计数预测 {dc}");
            }

            MaybeCompact();
        }

        // ------------------------------------------------------------------
        //  前景：收集邻居所在分量的根（入参：内部坐标）
        // ------------------------------------------------------------------
        private int GatherFgRoots(int x, int y)
        {
            int k = 0;
            for (int t = 0; t < _fgOff.Length; t += 2)
            {
                int nx = x + _fgOff[t], ny = y + _fgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int ni = Idx(nx, ny);
                if (!_present[ni] || _fgNode[ni] == 0) continue;
                int r = _fg.Find(_fgNode[ni]);
                bool dup = false;
                for (int j = 0; j < k; j++) if (_fgRoots[j] == r) { dup = true; break; }
                if (!dup) _fgRoots[k++] = r;
            }
            return k;
        }

        // ------------------------------------------------------------------
        //  背景：把一格并入（入参：padded 索引；只会合并 ⇒ 精确 Δh）
        // ------------------------------------------------------------------
        private int BgAttach(int i)
        {
            int me = NewBgNode();
            _bgNode[i] = me;
            int x = i % Size, y = i / Size;
            int kr = 0;
            for (int t = 0; t < _bgOff.Length; t += 2)
            {
                int nx = x + _bgOff[t], ny = y + _bgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                int ni = ny * Size + nx;
                if (_present[ni] || _bgNode[ni] == 0) continue;
                int r = _bg.Find(_bgNode[ni]);
                bool dup = false;
                for (int j = 0; j < kr; j++) if (_bgRoots[j] == r) { dup = true; break; }
                if (!dup) _bgRoots[kr++] = r;
            }

            if (kr == 0)
            {
                _bgTouch[me] = false;
                return +1; // 凭空出现一个不含外部的背景区域 = 多一个洞
            }

            int finiteBefore = 0;
            for (int j = 0; j < kr; j++) if (!_bgTouch[_bgRoots[j]]) finiteBefore++;
            bool touch = false;
            for (int j = 0; j < kr; j++) touch |= _bgTouch[_bgRoots[j]];
            for (int j = 0; j < kr; j++) _bg.Union(_bgRoots[j], me);
            int root = _bg.Find(me);
            _bgTouch[root] = touch;
            return (touch ? 0 : 1) - finiteBefore;
        }

        /// <summary>
        /// 背景结构修复：种子 = 被改动格的背景邻居（padded 索引）。
        /// 必须分两阶段：先只泛洪标记、再统一换节点；
        /// 否则中途改指针会破坏 flood 自己用的 Find 判据（踩过一次）。
        /// </summary>
        private void BgRepair(List<int> seeds)
        {
            _markGen++;
            for (int s = 0; s < seeds.Count; s++)
            {
                int start = seeds[s];
                if (_present[start] || _bgNode[start] == 0 || _mark[start] == _markGen) continue;
                int oldRoot = _bg.Find(_bgNode[start]);

                int head = 0, tail = 0;
                _queue[tail++] = start;
                _mark[start] = _markGen;
                bool touch = false;
                int count = 0;
                while (head < tail)
                {
                    int cur = _queue[head++];
                    count++;
                    int cx = cur % Size, cy = cur / Size;
                    if (cx == 0 || cy == 0 || cx == Size - 1 || cy == HSize - 1) touch = true;
                    for (int t = 0; t < _bgOff.Length; t += 2)
                    {
                        int nx = cx + _bgOff[t], ny = cy + _bgOff[t + 1];
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                        int ni = ny * Size + nx;
                        if (_present[ni] || _bgNode[ni] == 0 || _mark[ni] == _markGen) continue;
                        if (_bg.Find(_bgNode[ni]) != oldRoot) continue;
                        _mark[ni] = _markGen;
                        _queue[tail++] = ni;
                    }
                    StatRepairCells++;
                }

                Trace?.Invoke($"BgRepair: start={start} oldRoot={oldRoot} count={count} touch={touch}");
                // 第二阶段：整块换成一个新节点；老节点留给"没被泛洪到的那些块"
                int fresh = NewBgNode();
                _bgTouch[fresh] = touch;
                _bg.Size[fresh] = count;
                for (int q = 0; q < tail; q++) _bgNode[_queue[q]] = fresh;
                if (fresh != oldRoot)
                {
                    _bg.Size[oldRoot] -= count;
                    if (_bg.Size[oldRoot] < 0) _bg.Size[oldRoot] = 0;
                }
            }
        }

        /// <summary>
        /// 前景断裂修复：从被拿掉格的邻居各自 flood，每块换一个新节点。
        /// 返回实际断裂出的块数（应等于 Δc）。入参：内部坐标。
        /// </summary>
        private int FgRepairSplit(int x, int y)
        {
            _pieces.Clear();
            _markGen++;
            for (int t = 0; t < _fgOff.Length; t += 2)
            {
                int nx = x + _fgOff[t], ny = y + _fgOff[t + 1];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int ni = Idx(nx, ny);
                if (!_present[ni] || _mark[ni] == _markGen) continue;
                _pieces.Add(FloodFgPiece(ni));
            }

            int splits = _pieces.Count - 1;
            if (splits <= 0) return 0;

            for (int p = 0; p < _pieces.Count; p++)
            {
                var piece = _pieces[p];
                int node = NewFgNode();
                _fg.Size[node] = piece.Count;
                for (int c = 0; c < piece.Count; c++) _fgNode[piece[c]] = node;
            }
            return splits;
        }

        private List<int> FloodFgPiece(int start)
        {
            var piece = new List<int>(64);
            int head = 0, tail = 0;
            _queue[tail++] = start;
            _mark[start] = _markGen;
            while (head < tail)
            {
                int cur = _queue[head++];
                piece.Add(cur);
                int cx = cur % Size, cy = cur / Size;
                for (int k = 0; k < _fgOff.Length; k += 2)
                {
                    int ax = cx + _fgOff[k], ay = cy + _fgOff[k + 1];
                    if (ax < 0 || ay < 0 || ax >= Size || ay >= HSize) continue;
                    int ai = ay * Size + ax;
                    if (!_present[ai] || _mark[ai] == _markGen) continue;
                    _mark[ai] = _markGen;
                    _queue[tail++] = ai;
                }
                StatRepairCells++;
            }
            return piece;
        }

        // ------------------------------------------------------------------
        //  批量：一批只做一轮修复
        // ------------------------------------------------------------------
        /// <summary>一口气放多格（入参：padded 索引列表）。</summary>
        public void AddBatch(IReadOnlyList<int> cells)
        {
            _bgSeeds.Clear();
            for (int c = 0; c < cells.Count; c++)
            {
                int i = cells[c];
                if (_present[i]) continue;
                int x = i % Size, y = i / Size;
                int dw = DeltaWForAdd(x - 1, y - 1);
                _wCorner += dw;
                _present[i] = true;

                int me = NewFgNode();
                _fgNode[i] = me;
                int k = GatherFgRoots(x - 1, y - 1);
                int dc;
                if (k == 0) dc = +1;
                else { for (int j = 0; j < k; j++) _fg.Union(_fgRoots[j], me); dc = -(k - 1); }
                _fgCount += dc;
                _holes += dc - dw / 4;

                int oldBgRoot = _bg.Find(_bgNode[i]);
                _bgNode[i] = 0;
                _bg.Size[oldBgRoot]--;
                if (_bg.Size[oldBgRoot] < 0) _bg.Size[oldBgRoot] = 0;

                for (int t = 0; t < _bgOff.Length; t += 2)
                {
                    int nx = x + _bgOff[t], ny = y + _bgOff[t + 1];
                    if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                    int ni = ny * Size + nx;
                    if (!_present[ni]) _bgSeeds.Add(ni);
                }
            }
            if (_bgSeeds.Count > 0) BgRepair(_bgSeeds);
            MaybeCompact();
        }

        /// <summary>一口气拿掉多格（入参：padded 索引列表）。</summary>
        public void RemoveBatch(IReadOnlyList<int> cells)
        {
            _fgSeeds.Clear();
            int dcTotal = 0;      // 净变化（写进 _fgCount）
            int dcPositive = 0;   // 正的部分之和 = "一共断了几次"（决定要不要修复、修复几块）
            for (int c = 0; c < cells.Count; c++)
            {
                int i = cells[c];
                if (!_present[i]) continue;
                int x = i % Size, y = i / Size;
                int dw = -DeltaWForAdd(x - 1, y - 1);
                _wCorner += dw;
                _present[i] = false;
                int dh = BgAttach(i);
                _holes += dh;
                int dcCell = dw / 4 + dh;
                dcTotal += dcCell;
                if (dcCell > 0) dcPositive += dcCell;

                _fgNode[i] = 0;
                for (int t = 0; t < _fgOff.Length; t += 2)
                {
                    int nx = x + _fgOff[t], ny = y + _fgOff[t + 1];
                    if (nx < 1 || ny < 1 || nx > W || ny > H) continue;
                    int ni = ny * Size + nx;
                    if (_present[ni]) _fgSeeds.Add(ni);
                }
            }
            _fgCount += dcTotal;
            // ★ 关键：必须按"正的部分"判断要不要修复。
            //   一批里可能既有"拿掉孤块"（Δc = −1）又有"切断链条"（Δc = +1），净值为 0，
            //   但断裂确实发生了 ⇒ 只看净值会把断裂漏掉（踩过一次）。
            if (dcPositive > 0 || AlwaysRepair)
            {
                int splits = FgRepairBatchSplit(_fgSeeds);
                if (dcPositive > 0 && splits != dcPositive)
                {
                    // 局部修复对不上账 ⇒ 兜底全量重建（正确性优先，成本 O(格子数)）
                    Trace?.Invoke($"批量修复对不上账（{splits} vs 应断 {dcPositive}）⇒ 兜底 Compact");
                    CompactFg();
                }
            }
            MaybeCompact();
        }

        /// <summary>
        /// 批量修复：把所有"与改动相邻的块"各泛洪一次、换新节点。
        /// 与单格修复的区别：一批只扫一轮，而不是每格一轮。
        /// </summary>
        private int FgRepairBatchSplit(List<int> seeds)
        {
            _markGen++;
            var groupRoot = new List<int>();
            var groupPieces = new List<int>();

            for (int s = 0; s < seeds.Count; s++)
            {
                int start = seeds[s];
                if (!_present[start] || _mark[start] == _markGen) continue;
                int oldRoot = _fg.Find(_fgNode[start]);
                var piece = FloodFgPiece(start);
                int g = groupRoot.IndexOf(oldRoot);
                if (g < 0) { groupRoot.Add(oldRoot); groupPieces.Add(0); g = groupRoot.Count - 1; }
                groupPieces[g]++;

                // 立刻换新节点（泛洪用的是 present + mark，不依赖节点，安全）
                int node = NewFgNode();
                _fg.Size[node] = piece.Count;
                for (int c = 0; c < piece.Count; c++) _fgNode[piece[c]] = node;
            }

            int splits = 0;
            for (int g = 0; g < groupRoot.Count; g++) splits += groupPieces[g] - 1;
            // 覆盖性无法在换节点后复原 ⇒ 交给调用方 `splits != dcPositive ⇒ 兜底 Compact` 兜住
            return splits;
        }

        // ------------------------------------------------------------------
        //  兜底：整图重建并查集（O(格子数)）
        // ------------------------------------------------------------------
        private void MaybeCompact()
        {
            if (_fg.NodeCount > 4 * _cells || _bg.NodeCount > 4 * _cells) CompactAll();
        }

        /// <summary>整图重建前景/背景结构（节点池整理的唯一手段，也是兜底手段）。</summary>
        public void CompactAll()
        {
            StatCompactions++;
            CompactFg();
            CompactBg();
        }

        private void CompactFg()
        {
            _fg.Reset();
            for (int i = 0; i < _cells; i++) _fgNode[i] = 0;
            _markGen++;
            int comps = 0;
            for (int i = 0; i < _present.Length; i++)
            {
                if (!_present[i] || _fgNode[i] != 0) continue;
                var piece = FloodFgPiece(i);
                int node = NewFgNode();
                _fg.Size[node] = piece.Count;
                for (int c = 0; c < piece.Count; c++) _fgNode[piece[c]] = node;
                comps++;
            }
            _fgCount = comps;
        }

        private void CompactBg()
        {
            _bg.Reset();
            _bgTouch = new bool[2048];
            _bgNode[0] = 0;
            for (int i = 0; i < _cells; i++) _bgNode[i] = 0;
            _markGen++;
            int holes = 0;
            for (int i = 0; i < _cells; i++)
            {
                if (_present[i] || _bgNode[i] != 0) continue;
                // 泛洪一个背景分量
                int head = 0, tail = 0;
                _queue[tail++] = i;
                _mark[i] = _markGen;
                bool touch = false;
                var cellsOfComp = new List<int>(64);
                while (head < tail)
                {
                    int cur = _queue[head++];
                    cellsOfComp.Add(cur);
                    int cx = cur % Size, cy = cur / Size;
                    if (cx == 0 || cy == 0 || cx == Size - 1 || cy == HSize - 1) touch = true;
                    for (int t = 0; t < _bgOff.Length; t += 2)
                    {
                        int nx = cx + _bgOff[t], ny = cy + _bgOff[t + 1];
                        if (nx < 0 || ny < 0 || nx >= Size || ny >= HSize) continue;
                        int ni = ny * Size + nx;
                        if (_present[ni] || _mark[ni] == _markGen) continue;
                        _mark[ni] = _markGen;
                        _queue[tail++] = ni;
                    }
                    StatRepairCells++;
                }
                int node = NewBgNode();
                _bgTouch[node] = touch;
                _bg.Size[node] = cellsOfComp.Count;
                for (int c = 0; c < cellsOfComp.Count; c++) _bgNode[cellsOfComp[c]] = node;
                if (!touch) holes++;
            }
            if (holes != _holes)
                throw new InvalidOperationException($"Compact 对账失败：重建孔洞 {holes} vs 维护值 {_holes}");
        }

        // ------------------------------------------------------------------
        //  校验：与暴力全量对拍
        // ------------------------------------------------------------------
        public bool[] ClonePresent() => (bool[])_present.Clone();

        /// <summary>把并查集展开成每格标签（仅测试用，O(格子数)）。</summary>
        public int[] CanonicalLabels()
        {
            var labels = new int[_cells];
            for (int i = 0; i < _cells; i++)
                if (_present[i] && _fgNode[i] != 0) labels[i] = _fg.Find(_fgNode[i]) + 1;
            return labels;
        }

        /// <summary>判据：增量状态必须与暴力全量完全一致。返回空串表示通过。</summary>
        public string Verify(bool[] present, int size, int hsize, bool checkPartition = true)
        {
            var brute = GridBrute.Analyze(present, size, hsize, _conn);
            if (_fgCount != brute.Comps)
                return $"连通块数：增量 {_fgCount} vs 暴力 {brute.Comps}";
            if (_holes != brute.Holes)
                return $"孔洞数：增量 {_holes} vs 暴力 {brute.Holes}";
            if (_wCorner % 4 != 0)
                return $"绕数不是 4 的倍数：{_wCorner}";
            long chi = _wCorner / 4;
            if (chi != (long)brute.Comps - brute.Holes)
                return $"恒等式失效：w/4 = {chi}，但 c − h = {brute.Comps} − {brute.Holes}";
            if (!checkPartition) return "";

            var rootToBrute = new Dictionary<int, int>();
            var bruteToRoot = new Dictionary<int, int>();
            int[] mine = CanonicalLabels();
            for (int i = 0; i < mine.Length; i++)
            {
                int a = mine[i], b = brute.FgLabel[i];
                if ((a == 0) != (b == 0)) return $"第 {i} 格的前景身份不一致";
                if (a == 0) continue;
                if (rootToBrute.TryGetValue(a, out int b1)) { if (b1 != b) return $"根 {a} 跨越了两个暴力分量"; }
                else rootToBrute[a] = b;
                if (bruteToRoot.TryGetValue(b, out int a1)) { if (a1 != a) return $"暴力分量 {b} 被拆成了多个根"; }
                else bruteToRoot[b] = a;
            }
            // 背景只查"必须成立"的两件事（原因见 Add 里的"可证明无害的跳过"）：
            //   ① 每格"是否连外部"必须与暴力一致；
            //   ② **有限**（不连外部 = 孔洞）分量必须精确分区 —— 孔洞计数靠它；
            //   连外部的块允许被合并成一块（不影响任何将来的 Δh）。
            var bgRootTouch = new Dictionary<int, bool>();
            var finiteBruteToRoot = new Dictionary<int, int>();
            var finiteRootToBrute = new Dictionary<int, int>();
            for (int i = 0; i < _cells; i++)
            {
                if (_present[i]) continue;
                int a = _bgNode[i] == 0 ? -1 : _bg.Find(_bgNode[i]);
                int b = brute.BgLabel[i];
                bool bruteTouch = brute.BgTouch[b];
                if (bgRootTouch.TryGetValue(a, out bool t0))
                {
                    if (t0 != bruteTouch) return $"背景根 {a} 的是否连外部与暴力不一致（格 {i % Size},{i / Size}）";
                }
                else bgRootTouch[a] = bruteTouch;
                if (bruteTouch) continue; // 连外部的块允许合并
                if (finiteRootToBrute.TryGetValue(a, out int b1))
                {
                    if (b1 != b) return $"有限背景根 {a} 跨越了两个孔洞（格 {i % Size},{i / Size}）";
                }
                else finiteRootToBrute[a] = b;
                if (finiteBruteToRoot.TryGetValue(b, out int a1))
                {
                    if (a1 != a) return $"孔洞 {b} 被拆成了多个根（格 {i % Size},{i / Size}：根 {a} vs {a1}）";
                }
                else finiteBruteToRoot[b] = a;
            }
            return "";
        }
    }
}

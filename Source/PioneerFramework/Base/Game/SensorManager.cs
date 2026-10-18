using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **认知层 [V3]（S2 = 本体，2026-10-02）**：每阵营一份接触视图，每物理步做一次配对扫描。
    ///
    /// ## 它是什么
    /// "**谁能看见什么**"成为一等状态：物理层只提供 ①目标有多响（`Group.SignalStrength`，带失效钩子的缓存）
    /// ②观测者看得多远（`Group.DetectTech` / `RadarCap`，[V7] 的建筑级投影每步写进来）。
    /// 本组件把二者配上几何，按 §1.8 定型的判据出一张**接触视图**；决策类消费者读它（S3 才接线）。
    ///
    /// ## 四条口径（全部是已裁定的，别在别处另写）
    /// 1. **配对律**：外循环 = **观测者**，内循环 = **敌方匿踪目标**（[I] 第 1 条；同阵营不进判据）。
    /// 2. **判据 = `d² ≤ A_obs·B_tgt`**（`DetectionCore.IsVisibleByProduct`，无 sqrt 无除法）；
    ///    **粗筛就是判据本身**，不查网格（实测 `GetGroupsInRange` 在本尺度是负优化）。
    /// 3. **刷新时机 = 每物理步无条件 + 状态门控**（用户 2026-10-02 裁定 ③）：门控 = 有观测者 ∧ 有匿踪组；
    ///    **不是"读触发"**——那会让"谁先发现谁"取决于 UI 开没开。
    /// 4. **记忆三态**（`ContactMemory`）：Live = 本步刷新；Shadow = 断刷新但未过期（σ 随龄涨，**仍可用**）；
    ///    Lost = 过期。**一个时间戳**：更差的观测**不刷新**记录（否则"有人看着"会把陈旧内容一直续命），
    ///    旧内容靠 σ 增长让位给新鲜但更差的观测（见 `TrackMemoryTests.AgedOwnSensorContact_EventuallyLosesToAFreshShare`）。
    ///
    /// ## 两个读入口（[L](c) 的两种读法，别混）
    /// · `AppendVisible(faction, into)` —— **全量读**（UI/索敌）：公开投影**现场构造**（不物化）+ 匿踪接触追加；
    /// · `TryGetContact(faction, targetGroupId, out c)` —— **点查询**（快环制导，每飞弹每步）：
    ///   **绝不触发刷新**（刷新只发生在本组件的物理步里）。
    ///
    /// ## 落盘边界（设计稿 §5.2）
    /// **落盘**：匿踪接触（记忆/过期是"信息缺失"，推不出来）。**不落盘**：公开投影（每步从 `groups` 现算）。
    /// 假目标（[E5]）同样必须落盘，但**注入方还没有**⇒ 本版不做，留到 [E5] 与它的生产者一起落地。
    ///
    /// ## 成本（实测口径）
    /// 2×(20 舰 + 1000 弹)：跨阵营剪枝后 40 000 对/步 ⇒ **372 µs/步（Release，串行）**、
    /// 178 µs（按观测者并行）、最坏档 520 µs。见 `MissileSwarmMonteCarloBench` 与 §1.8 [T]。
    /// </summary>
    public class SensorManager : GameComponent
    {
        public SensorManager(Game game) { }

        // ── 组件引用缓存（同 CombatManager/AVM 的写法；GetComponent 是组件表线性扫描）──
        private readonly GameComponentRef<SpaceGameComp> spaceGameRef = new();

        // ── 每阵营一份视图 ──
        private Dictionary<int, FactionPicture> pictures = new();

        // ── 每步复用缓冲（物理步上跑 ⇒ 零分配；不新建 HashSet/List）──
        private readonly List<SpaceEntityGroup> observersScratch = new();
        private readonly List<SpaceEntityGroup> stealthScratch = new();
        private readonly List<SpaceEntityGroup> jammersScratch = new();   // [E3] 干扰机（开机且有强度）

        /// <summary>
        /// **dev 真值开关**（用户 2026-10-02 要求）：开启后**绘制与点击绕过认知层**，直接看到真实的棋子。
        /// 调试/量测用（对比"认知层挡掉了什么"）；**不是玩法机制**——不落盘、不影响判定、不影响战斗，
        /// 只改"画不画 / 点不点得到"。默认关闭；调试页签「80 调试日志」有勾选框。
        /// </summary>
        public static bool DebugShowGroundTruth ;

        /// <summary>
        /// **该阵营此刻能不能看见这条 group** —— 绘制 / 点击 / 列表的**统一准入判据**（[V3] 的第一批消费者）。
        /// 三档：①公开组（不匿踪）⇒ 任何阵营可见；②我方单位 ⇒ 自家可见（自有遥测，与探测无关）；
        /// ③匿踪组 ⇒ 只有持有**未过期**接触的阵营可见（Live 或 Shadow；Lost 即不可见）。
        /// 认知层缺位（组件没加载）时由调用方定（绘制侧按"放行 = 旧行为"处理）。
        /// </summary>
        public bool IsVisibleTo(int factionId, SpaceEntityGroup g)
        {
            if (g == null || !g.IsActive) return false;
            if (!g.IsStealth) return true;                    // 公开（应答机）
            if (g.FactionId == factionId) return true;        // 自己人
            return ContactPhaseOf(factionId, g.GroupId) != TrackPhase.Lost;
        }

        /// <summary>玩家阵营版（世界视图的绘制/点击用它；`Faction.OfPlayer` 解析不到 ⇒ 放行 = 旧行为）。</summary>
        public bool PlayerCanSee(SpaceEntityGroup g)
        {
            var player = Faction.OfPlayer;
            if (player == null) return g != null && g.IsActive;
            return IsVisibleTo(player.loadID, g);
        }

        /// <summary>
        /// **玩家向目标列表的相位标注**（[V3] 收尾 · B1 交互装门，2026-10-11）：影子期 ⇒ 「 影子（最后已知）」；
        /// 公开组 / Live / 玩家派系取不到 ⇒ 空串。词的唯一口径在 `TargetListCore.PhaseSuffix`（纯函数、带纯测试）；
        /// 本方法只负责把"玩家派系 + 是不是匿踪 + 当前相位"三个事实凑齐。
        /// ⚠ 列表的**准入**用 <see cref="PlayerCanSee"/>（影子算看得见、失联不算）——标注与准入是两件事。
        /// </summary>
        public string PlayerPhaseSuffix(SpaceEntityGroup g)
        {
            if (g == null || !g.IsStealth) return "";
            var player = Faction.OfPlayer;
            if (player == null) return "";
            return TargetListCore.PhaseSuffix(isStealth: true, phase: ContactPhaseOf(player.loadID, g.GroupId));
        }

        /// <summary>最近一次扫描的对数（读数：成本与"剪枝是否生效"的证据；不是判据）。</summary>
        public long DebugPairCount { get; private set; }

        // ── 干扰快照（[E3]/裁定⑥，2026-10-02）：**只读**给表现层"我在被干扰"的数值 + 状态。
        //   结算仍在扫描里只有一份（[E3]），这里只是把每个观测者当步吃到的 J 抄一份出来；
        //   每步重建，不落盘（表现层读数，不进任何判据）。
        /// <summary>某编队最近一次物理步扫描吃到的干扰（净值 + 含/不含干扰的观测因子）。</summary>
        public struct JamInfo
        {
            /// <summary>原始 J：敌方干扰机按"干扰机→观测方"平方反比折算后的合计。</summary>
            public double jam;
            /// <summary>含干扰的观测因子（`ObserverFactor(tech, jam)`）。</summary>
            public double factorWithJam;
            /// <summary>无干扰对照因子（`ObserverFactor(tech, 0)`）——用于读"灵敏度降至 X%"。</summary>
            public double factorNoJam;
        }

        private readonly Dictionary<int, JamInfo> jamByObserver = new();

        /// <summary>只读快照：某编队当步的受扰情况；无条目 = 本步没吃到干扰。</summary>
        public bool TryGetJamInfo(int groupId, out JamInfo info) => jamByObserver.TryGetValue(groupId, out info);

        // ── 假目标（[E5]，2026-10-02）：**没有真值对应物** ⇒ 推不出来 ⇒ 必须落盘（设计稿 §5.2）。
        //   · 感知方式与真目标**同构**：同一个距离判据（用假接触**自报的信号强度**）；
        //   · **依赖对方开机**（§11.4 补的语义）：只有**主动雷达开机**的观测者才会被"回波"骗到
        //     （DRFM 语义：对方静默聆听时骗不了它）；
        //   · **识破 = 深层提示**：观测者 `eccmResist > 欺骗强度` ⇒ 该接触仍然出现，但打上 `isDecoy = true`
        //     （"技术够高才看得到这是假的"，§11.5/§11.10）——不把它删掉，因为融合"取更真"要看得见它。
        private Dictionary<int, DecoyRecord> decoys = new();
        private int nextDecoyId = 1;
        private readonly List<int> decoyScratch = new();

        /// <summary>**注入一条假接触**（[E5]；生产者 = 决策侧 / 诱饵武器，尚未接线）。返回 decoyId。</summary>
        public int InjectDecoy(int ownerFactionId, DVector3 position, DVector3 velocity,
            float signalStrength, double lifetimeSeconds, float apparentRadius = 20f)
        {
            double now = TimeScaleManager.GetPhysicsTickSafe();
            int id = nextDecoyId++;
            decoys[id] = new DecoyRecord
            {
                contact = new Contact
                {
                    targetGroupId = -1,               // 假接触**没有**真值对应物（不变式）
                    decoyId = id,
                    position = position,
                    velocity = velocity,
                    lastUpdatePhysics = now,
                    positionSigma = 0.0,
                    source = ContactSource.Decoy,
                    bearingSigmaRad = 0.0,
                    isRadiationSource = false,
                    isDecoy = false,                  // "是否被识破"由**每个观测者**在感知时判定
                    apparentRadius = apparentRadius,
                },
                ownerFactionId = ownerFactionId,
                strength = signalStrength,
                expirePhysics = now + lifetimeSeconds,
            };
            return id;
        }

        /// <summary>清掉某条假接触（被消耗 / 手动撤销）。</summary>
        public bool RemoveDecoy(int decoyId) => decoys.Remove(decoyId);

        /// <summary>在册假目标条数（读数/验收）。</summary>
        public int DecoyCount => decoys.Count;

        /// <summary>最近一次扫描的命中数（读数）。</summary>
        public long DebugHitCount { get; private set; }

        // 观测不确定度的默认分量（**待定标**；与 `ContactFusion.SigmaRefMeters` 同族）。
        private const double BearingSigmaRadDefault = 0.01;
        private const double RangeSigmaRelativeDefault = 0.02;
        private const double JamSigmaFactorDefault = 1.0;

        // ═══════════════════════════════════════════════════════════════════
        //  驱动：每物理步一次（SpaceGameComp 显式调用，且在决策类消费者之前）
        // ═══════════════════════════════════════════════════════════════════

        internal void OnPhysicsStepCompleted()
        {
            var sg = SubscribedGame();          // 顺便确保 [C8] 摧毁事件已订阅（换局/读档按实例变化重订）
            if (sg == null) return;
            double now = TimeScaleManager.GetPhysicsTickSafe();

            // ── N1 一次 O(N) 过滤 + **状态门控**（裁定 ③）──
            observersScratch.Clear();
            stealthScratch.Clear();
            jammersScratch.Clear();
            foreach (var g in sg.GetAllGroups())
            {
                if (g == null || !g.IsActive) continue;
                if (g.IsStealth) stealthScratch.Add(g);
                if (g.DetectTech.sensitivityMeters > 0f) observersScratch.Add(g);
                // [E3] 干扰机（**开机且有强度**才进表；数量级是个位数——[M5] 已登记"别为它做预过滤"）
                if (g.JammingEnabled && g.JamStrength > 0f) jammersScratch.Add(g);
            }

            // 记忆过期：只扫各阵营自己的接触（O(接触数)，且与组数无关）
            ExpireAll(now);

            // ⚠ **假目标感知放在早退之前**（2026-10-02 修）：没有匿踪目标时原来直接 return，
            //   于是"场上只有观测者 + 一条假接触"的场面里，诱饵**永远感知不到**（电子战总控实测到这条）。
            //   诱饵**不是** group ⇒ 它不受"有没有匿踪组"这个门控。
            // ── [E5] 假目标感知：与真目标**同一个判据**，但①只有**开机**的观测者会被骗（DRFM 语义）
            //    ②识破靠 per-observer 的 `eccmResist > 欺骗强度` ⇒ 打 `isDecoy` 标记（深层提示）。
            if (decoys.Count > 0)
            {
                ExpireDecoys(now);
                for (int i = 0; i < observersScratch.Count; i++)
                {
                    var obs = observersScratch[i];
                    if (!obs.ActivePingEnabled) continue;              // 静默聆听 ⇒ 骗不到它
                    var tech = obs.DetectTech;
                    double a = DetectionCore.ObserverFactor(in tech, 0.0);
                    if (!(a > 0.0)) continue;
                    DVector3 p = obs.CurrentOrbit.positionSystem.v;
                    foreach (var kv in decoys)
                    {
                        var rec = kv.Value;
                        if (rec.ownerFactionId == obs.FactionId) continue;      // 自己投的诱饵不骗自己
                        DVector3 d = rec.contact.position - p;
                        if (!DetectionCore.IsVisibleByProduct(rec.strength, d.sqrMagnitude, in tech, 0.0)) continue;
                        SubmitDecoy(obs.FactionId, rec, obs, now, tech.eccmResist);
                    }
                }
            }

            jamByObserver.Clear();
            if (stealthScratch.Count == 0 || observersScratch.Count == 0) { DebugPairCount = 0; DebugHitCount = 0; return; }

            // ── N2/N3/N4：观测者 × 敌方匿踪 ──
            long pairs = 0, hits = 0;
            for (int i = 0; i < observersScratch.Count; i++)
            {
                var obs = observersScratch[i];
                var tech = obs.DetectTech;                       // 结构体副本（下面可能被主动增益改）
                var rc = obs.RadarCap;
                bool activeBeam = false;
                if (obs.ActivePingEnabled)
                {
                    if (rc.activeGainBonus > 0f) tech.sensitivityMeters *= 1f + rc.activeGainBonus;   // 距离倍率 ⇒ 灵敏度
                    activeBeam = rc.beamRangeMeters > 0f;        // 开机的主动雷达**有锥**（被动灵敏度是全向的）
                }
                int obsFaction = obs.FactionId;
                DVector3 p = obs.CurrentOrbit.positionSystem.v;
                // [E3] 压制式干扰（§11.3）：**敌方**干扰机按"干扰机→观测方"距离平方反比折算成 J_eff。
                // 作用方式 = 抬高观测方的阈值（`ObserverFactor` 分母）⇒ **烧穿是免费推论**：
                // 目标/自己靠近时信号按平方涨、阈值不变 ⇒ 必然在某个距离上重新可见（无需新机制）。
                double jam = 0.0;
                for (int k = 0; k < jammersScratch.Count; k++)
                {
                    var jm = jammersScratch[k];
                    if (jm.FactionId == obsFaction) continue;               // 自己人干扰自己没意义（也不该扣）
                    jam += DetectionCore.JamEffort(jm.JamStrength, (jm.CurrentOrbit.positionSystem.v - p).magnitude);
                }
                double a = DetectionCore.ObserverFactor(in tech, jam);
                // [E3]/裁定⑥（2026-10-02）：给表现层留**只读快照**（"我在被干扰"的数值 + 状态）。
                // 只在真有干扰时记（否则表会被全体观测者撑满）；记录发生在"可见性判定"之前，
                // 所以"被压到看不见"这种状态同样能在面板上读出来。
                if (jam > 0.0)
                    jamByObserver[obs.GroupId] = new JamInfo
                    {
                        jam = jam,
                        factorWithJam = a,
                        factorNoJam = DetectionCore.ObserverFactor(in tech, 0.0),
                    };
                if (!(a > 0.0)) continue;
                DVector3 boresight = activeBeam ? obs.RadarBoresight(now) : default;

                for (int j = 0; j < stealthScratch.Count; j++)
                {
                    var tgt = stealthScratch[j];
                    if (tgt.FactionId == obsFaction) continue;   // 同阵营不进判据
                    pairs++;
                    DVector3 d = tgt.CurrentOrbit.positionSystem.v - p;
                    double d2 = d.sqrMagnitude;
                    double b = tgt.SignalStrength;               // 目标因子（缓存值）
                    if (!DetectionCore.IsVisibleByProduct(b, d2, in tech, jam)) continue;
                    if (activeBeam && !RadarBeamPasses(obs, boresight, d, d2)) continue;
                    hits++;
                    Submit(obsFaction, tgt, obs, now, d2, tech.memorySeconds);
                }
            }
            DebugPairCount = pairs;
            DebugHitCount = hits;
        }

        /// <summary>把一条假接触写进该观测者阵营的视图（与真接触**同构**：同一个列表、同样的读法）。</summary>
        private void SubmitDecoy(int factionId, DecoyRecord rec, SpaceEntityGroup observer, double now, float observerEccm)
        {
            var pic = Picture(factionId);
            var c = rec.contact;
            c.lastUpdatePhysics = now;                                   // 假接触"每次被照到"都刷新（它没有记忆概念）
            c.isDecoy = observerEccm > rec.strength;                     // **识破 = 深层提示**（per-observer）
            var recIn = new ContactRecord { contact = c, observerGroupId = observer.GroupId, memorySeconds = 0f };
            int idx = pic.IndexOfDecoy(c.decoyId);
            if (idx >= 0)
            {
                // ⚠ **去伪存真**（用户 2026-10-02 裁定：同阵营直接共享）：识破标记必须**单调**——
                //   一旦有成员识破，全阵营就按"这是假的"看，**不许**被"没识破的成员"洗回未标记
                //   （否则标记随观测者遍历顺序翻来翻去 = 后写者赢的假融合）。
                if (pic.tracks[idx].contact.isDecoy) recIn.contact.isDecoy = true;
                pic.tracks[idx] = recIn;
            }
            else pic.tracks.Add(recIn);
        }

        private void ExpireDecoys(double now)
        {
            if (decoys.Count == 0) return;
            decoyScratch.Clear();
            foreach (var kv in decoys) if (now >= kv.Value.expirePhysics) decoyScratch.Add(kv.Key);
            for (int i = 0; i < decoyScratch.Count; i++) decoys.Remove(decoyScratch[i]);
        }

        /// <summary>波束窄相（**平方口径**：`dot² ≥ cos²half·d²` 且 `d² ≤ range²`，无 sqrt）。</summary>
        private static bool RadarBeamPasses(SpaceEntityGroup obs, DVector3 boresight, DVector3 delta, double d2)
        {
            var rc = obs.RadarCap;
            double range = rc.beamRangeMeters;
            if (d2 > range * range) return false;
            double dot = boresight.x * delta.x + boresight.y * delta.y + boresight.z * delta.z;
            if (!(dot > 0.0)) return false;
            double ch = System.Math.Cos(rc.beamHalfAngleDeg * System.Math.PI / 180.0);
            return dot * dot >= ch * ch * d2;
        }

        /// <summary>
        /// **融合写入**（"取更真"，`ContactFusion`）：构造只发生在**确定要写**时（[T] 定论 2 —— 实测 92% 的
        /// 命中并不写，先构造 88 字节的 `Contact` 再判会让成本翻倍）。
        /// 胜负 = (保真度, 更小的 σ, 更小的观测者 id) —— 显式 tie-break ⇒ 与观测者遍历顺序无关（确定性）。
        /// </summary>
        private void Submit(int factionId, SpaceEntityGroup target, SpaceEntityGroup observer, double now, double d2, float observerMemory)
        {
            var pic = Picture(factionId);
            double dist = System.Math.Sqrt(d2);
            double sigma = DetectionCore.PositionSigmaMeters(dist, 0.0,
                BearingSigmaRadDefault, RangeSigmaRelativeDefault, JamSigmaFactorDefault);

            int idx = pic.IndexOf(target.GroupId);
            if (idx >= 0)
            {
                var cur = pic.tracks[idx].contact;
                double curSigma = ContactMemory.SigmaAt(now, cur.lastUpdatePhysics, cur.positionSigma);
                var curProbe = cur; curProbe.positionSigma = curSigma;
                var newProbe = cur; newProbe.positionSigma = sigma;
                double fidCur = ContactFusion.Fidelity(in curProbe);
                double fidNew = ContactFusion.Fidelity(in newProbe);
                bool wins = fidNew > fidCur
                            || (fidNew == fidCur && sigma < curSigma)
                            || (fidNew == fidCur && sigma == curSigma && observer.GroupId < pic.tracks[idx].observerGroupId);
                if (!wins) return;                                   // 更差 ⇒ 不写、**也不刷新时间戳**（见类文档口径 4）
            }

            var c = new Contact
            {
                targetGroupId = target.GroupId,
                decoyId = -1,
                position = target.CurrentOrbit.positionSystem.v,
                velocity = target.CurrentOrbit.velocitySystem.v,
                lastUpdatePhysics = now,
                positionSigma = sigma,
                source = ContactSource.OwnSensor,
                bearingSigmaRad = BearingSigmaRadDefault,
                isRadiationSource = target.ActivePingEnabled,
                isDecoy = false,
                apparentRadius = EstimateRadius(target),
            };
            var rec = new ContactRecord { contact = c, observerGroupId = observer.GroupId, memorySeconds = observerMemory };
            if (idx >= 0) pic.tracks[idx] = rec; else pic.tracks.Add(rec);
        }

        /// <summary>目标视在半径（制导/瞄准要用）：取"船体系包围盒半对角线"标量、无实体时 0。
        /// （这里只读**组级**可达的那一份；实体级细节由消费方按需读真值——认知层不为此多存字段。）</summary>
        private static float EstimateRadius(SpaceEntityGroup g)
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return 0f;
            float r = 0f;
            foreach (int eid in g.GetEntityIds())
            {
                var e = sg.GetEntityById(eid);
                if (e == null) continue;
                float m = (float)e.AabbHalfSize.magnitude;
                if (m > r) r = m;
            }
            return r;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  读入口（**只有两个**：全量读 + 点查询）
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// **全量读**（UI / 索敌 / 列表）：把该阵营"现在能看见的"追加进 <paramref name="into" />。
        /// · 公开组（`IsStealth == false`）**现场构造**（不物化、不进存档）；
        /// · 匿踪接触（Live + Shadow）追加，**σ 取读时值**（`ContactMemory.SigmaAt`，影子期随时间涨）。
        /// ⚠ 追加式是**硬规则**（[M3]）：每帧绘制/列表都读它，做成 `IEnumerable` 会每帧一个枚举器分配。
        /// </summary>
        public void AppendVisible(int factionId, List<Contact> into)
        {
            if (into == null) return;
            var sg = spaceGameRef.Get();
            if (sg == null) return;
            double now = TimeScaleManager.GetPhysicsTickSafe();

            foreach (var g in sg.GetAllGroups())
            {
                if (g == null || !g.IsActive) continue;
                // 两类直接入图（都**不**经判据、与记忆无关）：
                //  · 公开组（任何阵营的"应答机"）⇒ 进所有阵营视图；
                //  · **我方单位**（含自己的匿踪导弹）⇒ 进自家视图。
                //    ⚠ 口径说明（S3 待确认）：我方单位对我是**已知事实**（自有遥测），不是"探测结果"——
                //    按 `Public` 入图（精确、σ=0）。这不动红线 #1（物理不读认知），也不是"决策读真值选目标"：
                //    自己人从来不是目标筛选的对象。
                bool own = g.FactionId == factionId;
                if (g.IsStealth && !own) continue;
                into.Add(new Contact
                {
                    targetGroupId = g.GroupId,
                    decoyId = -1,
                    position = g.CurrentOrbit.positionSystem.v,
                    velocity = g.CurrentOrbit.velocitySystem.v,
                    lastUpdatePhysics = now,
                    positionSigma = 0.0,
                    source = ContactSource.Public,               // 自报坐标：精确但未经自舰测量验证
                    bearingSigmaRad = 0.0,
                    isRadiationSource = g.ActivePingEnabled,
                    isDecoy = false,
                    apparentRadius = EstimateRadius(g),
                });
            }

            var pic = PictureIfAny(factionId);
            if (pic == null) return;
            for (int i = 0; i < pic.tracks.Count; i++)
            {
                var rec = pic.tracks[i];
                // 假接触：**生命期读时判**（过期即不出现，与接触的记忆同族）
                if (rec.contact.decoyId >= 0)
                {
                    // 假接触**只受自己的生命期管**（没有"记忆窗口"这回事；否则观测者一关机，
                    // 它还会以影子形式残留 T 秒 —— 与"依赖对方开机"的语义矛盾）。
                    if (!decoys.TryGetValue(rec.contact.decoyId, out var dr) || now >= dr.expirePhysics) continue;
                    into.Add(rec.contact);
                    continue;
                }
                if (ContactMemory.PhaseOf(now, rec.contact.lastUpdatePhysics, rec.memorySeconds) == TrackPhase.Lost) continue;
                var c = rec.contact;
                c.positionSigma = ContactMemory.SigmaAt(now, c.lastUpdatePhysics, c.positionSigma);
                into.Add(c);
            }
        }

        /// <summary>
        /// **点查询**（快环：每枚在飞弹每物理步查一次目标接触）：**绝不触发刷新**——
        /// 刷新只发生在本组件的物理步里（[L](c)：否则每步几十次全量扫描，比每周期一次还慢几十倍）。
        /// Live 与 Shadow 都返回 true（**影子可用**：可打击/可制导）；Lost 返回 false。
        /// </summary>
        public bool TryGetContact(int factionId, int targetGroupId, out Contact contact)
        {
            contact = default(Contact);
            var pic = PictureIfAny(factionId);
            if (pic == null) return false;
            int idx = pic.IndexOf(targetGroupId);
            if (idx < 0) return false;
            var rec = pic.tracks[idx];
            double now = TimeScaleManager.GetPhysicsTickSafe();
            if (rec.contact.decoyId >= 0)
            {
                if (!decoys.TryGetValue(rec.contact.decoyId, out var drec) || now >= drec.expirePhysics) return false;
                contact = rec.contact;
                return true;                       // 假接触只受自己的生命期管（同 AppendVisible）
            }
            if (ContactMemory.PhaseOf(now, rec.contact.lastUpdatePhysics, rec.memorySeconds) == TrackPhase.Lost) return false;
            contact = rec.contact;
            contact.positionSigma = ContactMemory.SigmaAt(now, contact.lastUpdatePhysics, contact.positionSigma);
            return true;
        }

        /// <summary>
        /// **已知状态**（制导/瞄准用，[V3] S3c / 设计稿 §9）：告诉我"这条 group 此刻在哪、多快、多大"。
        /// · **公开组**（应答机）与**我方单位** ⇒ 直接给真值（自报坐标 / 自有遥测；精确、σ=0）；
        /// · **匿踪组** ⇒ 查本阵营接触（Live/Shadow 都算；**Lost ⇒ false ⇒ 调用方按"追丢"处理**）。
        /// 返回的是**接触原样**（位置 = 最后已知点、σ = 读时值）；要打影子请自己
        /// `ContactMemory.CoastedPosition`（画面上的接触点**不**外推，两者分开）。
        /// </summary>
        public bool TryGetKnownState(int factionId, SpaceEntityGroup g, out Contact c)
        {
            c = default(Contact);
            if (g == null || !g.IsActive) return false;
            if (!g.IsStealth || g.FactionId == factionId)
            {
                c = new Contact
                {
                    targetGroupId = g.GroupId,
                    decoyId = -1,
                    position = g.CurrentOrbit.positionSystem.v,
                    velocity = g.CurrentOrbit.velocitySystem.v,
                    lastUpdatePhysics = TimeScaleManager.GetPhysicsTickSafe(),
                    positionSigma = 0.0,
                    source = ContactSource.Public,
                    bearingSigmaRad = 0.0,
                    isRadiationSource = g.ActivePingEnabled,
                    isDecoy = false,
                    apparentRadius = EstimateRadius(g),
                };
                return true;
            }
            return TryGetContact(factionId, g.GroupId, out c);
        }

        /// <summary>该阵营此刻对某目标的相位（Live / Shadow / Lost）；没有该接触 ⇒ Lost。</summary>
        public TrackPhase ContactPhaseOf(int factionId, int targetGroupId)
        {
            var pic = PictureIfAny(factionId);
            if (pic == null) return TrackPhase.Lost;
            int idx = pic.IndexOf(targetGroupId);
            if (idx < 0) return TrackPhase.Lost;
            var rec = pic.tracks[idx];
            return ContactMemory.PhaseOf(TimeScaleManager.GetPhysicsTickSafe(), rec.contact.lastUpdatePhysics, rec.memorySeconds);
        }

        /// <summary>该阵营当前持有的接触条数（读数/验收用）。</summary>
        public int TrackCount(int factionId)
        {
            var pic = PictureIfAny(factionId);
            return pic?.tracks.Count ?? 0;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  过期与存储
        // ═══════════════════════════════════════════════════════════════════

        // ── [C8] 摧毁 ⇒ 清接触（2026-10-02：一处真实的缺口——目标被销毁后，接触会以"幽灵"形式
        //   在记忆窗口内继续存在（图上还挂着一个已经不存在的目标）。事件源 = `SpaceGameComp.DestroyGroup`
        //   的唯一收束点（与 CombatManager 同点）；订阅写法照 `ConstellationGameComp`。
        private SpaceGameComp? subscribedComp;
        private SpaceGameComp? SubscribedGame()
        {
            var sg = spaceGameRef.Get();
            if (sg == null) return null;
            if (!ReferenceEquals(subscribedComp, sg))
            {
                if (subscribedComp != null) { subscribedComp.GroupDestroyed -= OnGroupDestroyed; subscribedComp.GroupLanded -= OnGroupDestroyed; }
                sg.GroupDestroyed += OnGroupDestroyed;
                sg.GroupLanded += OnGroupDestroyed;
                subscribedComp = sg;
            }
            return sg;
        }

        private void OnGroupDestroyed(int groupId)
        {
            if (pictures.Count == 0) return;
            foreach (var kv in pictures)
            {
                var tracks = kv.Value.tracks;
                for (int i = tracks.Count - 1; i >= 0; i--)
                    if (tracks[i].contact.targetGroupId == groupId) tracks.RemoveAt(i);
            }
        }

        private void ExpireAll(double now)
        {
            if (pictures.Count == 0) return;
            foreach (var kv in pictures)
            {
                var tracks = kv.Value.tracks;
                for (int i = tracks.Count - 1; i >= 0; i--)
                {
                    if (ContactMemory.PhaseOf(now, tracks[i].contact.lastUpdatePhysics, tracks[i].memorySeconds) == TrackPhase.Lost)
                        tracks.RemoveAt(i);
                }
            }
        }

        private FactionPicture Picture(int factionId)
        {
            if (!pictures.TryGetValue(factionId, out var pic))
            {
                pic = new FactionPicture();
                pictures[factionId] = pic;
            }
            return pic;
        }

        private FactionPicture? PictureIfAny(int factionId)
            => pictures.TryGetValue(factionId, out var pic) ? pic : null;

        /// <summary>每阵营一份的接触视图。</summary>
        public sealed class FactionPicture : IExposable
        {
            // 顺序 = 插入顺序（**不是**优先级顺序）；胜负判据在 `Submit`，与枚举顺序无关。
            public List<ContactRecord> tracks = new();

            public void ExposeData()
            {
                Scribe_Collections.Look(ref tracks, "tracks", LookMode.Deep);
                if (Scribe.mode == LoadSaveMode.PostLoadInit && tracks == null) tracks = new List<ContactRecord>();
            }

            public int IndexOfDecoy(int decoyId)
            {
                for (int i = 0; i < tracks.Count; i++)
                    if (tracks[i].contact.decoyId == decoyId) return i;
                return -1;
            }

            public int IndexOf(int targetGroupId)
            {
                for (int i = 0; i < tracks.Count; i++)
                    if (tracks[i].contact.targetGroupId == targetGroupId) return i;
                return -1;
            }
        }

        /// <summary>一条接触 + "谁在看着它、记多久"（**Verse 侧包装**：纯 `Contact` 结构体不认识 Scribe）。</summary>
        public sealed class ContactRecord : IExposable
        {
            public Contact contact;
            /// <summary>写入这条记录的观测者（确定性 tie-break 的第三键；也是 EW 排查"谁在看我"的线索）。</summary>
            public int observerGroupId = -1;
            /// <summary>该观测者的记忆窗口（写时快照；0 ⇒ 读时用默认兜底）。</summary>
            public float memorySeconds;

            public void ExposeData()
            {
                Scribe_Values.Look(ref contact.targetGroupId, "targetGroupId", -1);
                Scribe_Values.Look(ref contact.decoyId, "decoyId", -1);
                Scribe_Values.Look(ref contact.position.x, "px");
                Scribe_Values.Look(ref contact.position.y, "py");
                Scribe_Values.Look(ref contact.position.z, "pz");
                Scribe_Values.Look(ref contact.velocity.x, "vx");
                Scribe_Values.Look(ref contact.velocity.y, "vy");
                Scribe_Values.Look(ref contact.velocity.z, "vz");
                Scribe_Values.Look(ref contact.lastUpdatePhysics, "lastUpdate");
                Scribe_Values.Look(ref contact.positionSigma, "sigma");
                Scribe_Values.Look(ref contact.bearingSigmaRad, "bearingSigma");
                Scribe_Values.Look(ref contact.source, "source", ContactSource.OwnSensor);
                Scribe_Values.Look(ref contact.isRadiationSource, "isRadiationSource", false);
                Scribe_Values.Look(ref contact.isDecoy, "isDecoy", false);
                Scribe_Values.Look(ref contact.apparentRadius, "apparentRadius");
                Scribe_Values.Look(ref observerGroupId, "observerGroupId", -1);
                Scribe_Values.Look(ref memorySeconds, "memorySeconds");
            }
        }

        /// <summary>假目标记录（**没有真值源 ⇒ 落盘**；设计稿 §5.2）。</summary>
        public sealed class DecoyRecord : IExposable
        {
            public Contact contact;
            public int ownerFactionId = -1;
            /// <summary>欺骗强度（观测者 `eccmResist` 大于它即**识破**）。</summary>
            public float strength;
            public double expirePhysics;

            public void ExposeData()
            {
                Scribe_Values.Look(ref contact.decoyId, "decoyId", -1);
                Scribe_Values.Look(ref contact.position.x, "px");
                Scribe_Values.Look(ref contact.position.y, "py");
                Scribe_Values.Look(ref contact.position.z, "pz");
                Scribe_Values.Look(ref contact.velocity.x, "vx");
                Scribe_Values.Look(ref contact.velocity.y, "vy");
                Scribe_Values.Look(ref contact.velocity.z, "vz");
                Scribe_Values.Look(ref contact.apparentRadius, "radius", 20f);
                Scribe_Values.Look(ref ownerFactionId, "owner", -1);
                Scribe_Values.Look(ref strength, "strength");
                Scribe_Values.Look(ref expirePhysics, "expire");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // 只落"推不出来"的部分：匿踪接触（记忆/过期 = 信息缺失）。公开投影每步从 groups 现算 ⇒ 不落盘。
            Scribe_Collections.Look(ref pictures, "pictures", LookMode.Value, LookMode.Deep);
            Scribe_Collections.Look(ref decoys, "decoys", LookMode.Value, LookMode.Deep);
            Scribe_Values.Look(ref nextDecoyId, "nextDecoyId", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (pictures == null) pictures = new Dictionary<int, FactionPicture>();
                if (decoys == null) decoys = new Dictionary<int, DecoyRecord>();
                if (nextDecoyId < 1) nextDecoyId = 1;
            }
        }
    }
}

using System;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Pure.Tests
{
    /// <summary>
    /// **点亮的记忆三态**（[V3] 的"限时标签"；2026-10-02 用户裁定 ③：每步无条件 + 状态门控）。
    ///
    /// 三态：**新鲜 Live → 影子 Shadow → 失联 Lost**；影子期 σ 随时间涨，且**影子仍可用**（可打击/可制导）。
    /// 判据全是"实现正确则不可能"的硬逻辑 + 边界精确到 ULP；不抄实现、不比绝对常数（除"默认兜底"这一条登记项）。
    ///
    /// ## 反例纪律
    /// 抖动那条**必须能分辨两种实现**：有记忆 ⇒ 消失次数由窗口决定；无记忆（T=0）⇒ 由步数决定。
    /// 只断言"转换很少"是不会红的判据，所以两条臂都跑、且互相比较。
    /// </summary>
    public class TrackMemoryTests
    {
        private const double Dt = 2.0;          // 物理步
        private const double T = 30.0;          // 记忆窗口（s）

        // ── ② 抖动：边界附近的周期性进出，**消失次数由记忆窗口决定，而不是由步数决定** ──

        [Fact]
        public void BoundaryChatter_IsBoundedByMemoryWindow_NotByStepCount()
        {
            var tech = new SensorTech { sensitivityMeters = 1.0e6f };
            const double signal = 1.0;
            double r = DetectionCore.EffectiveDetectRange(signal, tech, 0.0);

            int withMemory = CountDisappearances(r, tech, signal, T);
            // ⚠ 无记忆臂**不能用 T=0**：那是"观测者未设 T ⇒ 用默认"的语义（本库刚立的兜底规则），
            //   两条臂会变成同一条 ⇒ 判据没有分辨力。用"窗口≈0"的显式小值当"无记忆"实现。
            int noMemory = CountDisappearances(r, tech, signal, 1e-9);

            // 上界：总时长 / 窗口（+1）——与振荡周期无关
            // 62 步：前 45 步振荡（90 s），末 17 步（34 s）永久失去 —— **必须 > T**，
            // 边界是严格大于（age == T 仍是影子）⇒ 只留 15 步（30 s）会停在 Shadow 上、到不了 Lost。
            int steps = 62;
            int bound = (int)Math.Ceiling(steps * Dt / T) + 1;
            Assert.True(withMemory <= bound,
                string.Format("有记忆时消失 {0} 次 > 上界 {1}（窗口没生效？）", withMemory, bound));
            Assert.True(withMemory >= 1, "末尾有一段永久失去 ⇒ 至少应消失一次（判据不是空的）");
            Assert.True(noMemory > withMemory * 3,
                string.Format("无记忆臂应当抖得多得多（无记忆 {0} 次 vs 有记忆 {1} 次）——两条臂分不开说明判据没牙齿",
                    noMemory, withMemory));
        }

        /// <summary>跑一遍"目标在有效距离附近振荡、末尾永久失去"的序列，数**进入 Lost 的次数**。</summary>
        private static int CountDisappearances(double r, in SensorTech tech, double signal, double memory)
        {
            int steps = 62;                                 // 与调用侧一致（末段必须超过 T 才能走到 Lost）
            int oscillateUntil = 45;                        // 前 45 步振荡（90 s），之后永久失去
            double now = 0.0, lastUpdate = double.NegativeInfinity;
            var prev = TrackPhase.Lost;
            int lost = 0;
            for (int i = 1; i <= steps; i++)
            {
                now = i * Dt;
                bool visible = i <= oscillateUntil
                    && DetectionCore.IsVisible(signal, r * (1.0 + 0.05 * Math.Sin(2.0 * Math.PI * i / 6.0)), tech, 0.0);
                if (visible) lastUpdate = now;              // 刷新的唯一入口
                var phase = ContactMemory.PhaseOf(now, lastUpdate, memory);
                if (phase == TrackPhase.Lost && prev != TrackPhase.Lost) lost++;
                prev = phase;
            }
            return lost;
        }

        // ── ③ 到期精确到边界 + 默认兜底 ──

        [Fact]
        public void Expiry_IsExact_And_DefaultMemoryAppliesWhenUnset()
        {
            const double last = 100.0;

            Assert.Equal(TrackPhase.Live, ContactMemory.PhaseOf(last, last, T));                  // 同刻 ⇒ 新鲜
            Assert.Equal(TrackPhase.Shadow, ContactMemory.PhaseOf(last + T, last, T));            // **恰好等于窗口** ⇒ 仍是影子
            Assert.Equal(TrackPhase.Lost, ContactMemory.PhaseOf(last + T + 1e-9, last, T));       // 超出 ⇒ 失联

            // 观测者未设 T（0 / NaN / 负）⇒ 用唯一兜底
            foreach (double unset in new[] { 0.0, double.NaN, -5.0 })
                Assert.Equal(DetectionCore.DefaultMemorySeconds, ContactMemory.EffectiveMemorySeconds(unset), 12);
            Assert.Equal(TrackPhase.Shadow,
                ContactMemory.PhaseOf(last + DetectionCore.DefaultMemorySeconds, last, 0.0));

            // 与 `Contact.IsStaleAt`（显式窗口口径）共用同一条龄口径
            var c = new Contact { targetGroupId = 1, decoyId = -1, lastUpdatePhysics = last };
            Assert.False(c.IsStaleAt(last + T, T));
            Assert.True(c.IsStaleAt(last + T + 1e-9, T));
        }

        // ── ④ 影子期 σ：随龄线性增长、有上限；旧接触最终让位给更差但更新的观测 ──

        [Fact]
        public void ShadowSigma_GrowsWithAge_AndCapsOut()
        {
            const double s0 = 100.0, t0 = 50.0;
            Assert.Equal(s0, ContactMemory.SigmaAt(t0, t0, s0), 9);                                // 刷新当刻 = 刷新值

            double at10 = ContactMemory.SigmaAt(t0 + 10.0, t0, s0);
            double at20 = ContactMemory.SigmaAt(t0 + 20.0, t0, s0);
            Assert.Equal(s0 + 10.0 * ContactMemory.SigmaGrowthMetersPerSecond, at10, 6);
            Assert.True(at20 > at10, "影子期 σ 必须随龄增长（记忆不免费）");
            Assert.Equal(ContactMemory.SigmaCapMeters, ContactMemory.SigmaAt(t0 + 1.0e9, t0, s0), 6);   // 有上限
        }

        [Fact]
        public void AgedOwnSensorContact_EventuallyLosesToAFreshShare()
        {
            const double last = 0.0;
            var aged = new Contact
            {
                targetGroupId = 7, decoyId = -1,
                source = ContactSource.OwnSensor, positionSigma = 0.0, lastUpdatePhysics = last,
            };
            var fresh = new Contact
            {
                targetGroupId = 7, decoyId = -1,
                source = ContactSource.FactionShare, positionSigma = 0.0, lastUpdatePhysics = 0.0,
            };

            int flips = 0;
            bool prev = false;
            for (double now = 1.0; now <= 600.0; now += 1.0)
            {
                var old = aged;
                old.positionSigma = ContactMemory.SigmaAt(now, last, 0.0);
                bool replace = ContactFusion.ShouldReplace(in old, in fresh);
                if (replace != prev) { flips++; prev = replace; }
            }

            Assert.Equal(1, flips);       // 单调交叉：只翻一次（不是来回抖）
            Assert.True(prev, "一条不放旧的接触最终必须让位给更差但更新的观测（否则记忆=永久全知）");
        }

        // ── ⑤ 关机 ⇒ 到期；开机 ⇒ 即时恢复（用真信号模型，不用手搓的数） ──

        [Fact]
        public void GoingDark_ExpiresAfterMemory_AndPingingRestoresImmediately()
        {
            var tech = new SensorTech { sensitivityMeters = 5.0e6f, memorySeconds = (float)T };
            const double d = 1.0e6;                                   // 1,000 km

            double sOn = SignalModel.Total(new SignalInputs { rcsRadiusMeters = 1.5, activePing = true });
            double sOff = SignalModel.Total(new SignalInputs { rcsRadiusMeters = 1.5 });
            Assert.True(DetectionCore.IsVisible(sOn, d, tech, 0.0), "前提：开机（S≈10）在 1,000 km 上必须看得见");
            Assert.False(DetectionCore.IsVisible(sOff, d, tech, 0.0), "前提：静默（S≈6e-4）在 1,000 km 上看不见");

            // 开机 10 步 ⇒ 一路 Live
            double now = 0.0, lastUpdate = 0.0;
            for (int i = 1; i <= 10; i++)
            {
                now = i * Dt;
                if (DetectionCore.IsVisible(sOn, d, tech, 0.0)) lastUpdate = now;
                Assert.Equal(TrackPhase.Live, ContactMemory.PhaseOf(now, lastUpdate, T));
            }

            // 关机：窗口内是影子、超出后失联
            double tDark = now;
            Assert.Equal(TrackPhase.Shadow, ContactMemory.PhaseOf(tDark + T * 0.5, lastUpdate, T));
            Assert.Equal(TrackPhase.Lost, ContactMemory.PhaseOf(tDark + T + 1.0, lastUpdate, T));

            // 再开机：**同一步立刻回到新鲜**（抖动的另一半：快进）
            double tBack = tDark + T + 2.0;
            if (DetectionCore.IsVisible(sOn, d, tech, 0.0)) lastUpdate = tBack;
            Assert.Equal(TrackPhase.Live, ContactMemory.PhaseOf(tBack, lastUpdate, T));
        }

        // ── ⑦ 影子外推（S3c：制导要"它大概在哪"，画面上的接触点**不**外推） ──

        [Fact]
        public void CoastedPosition_ExtrapolatesAlongVelocity_IdentityWhenFresh()
        {
            var c = new Contact
            {
                targetGroupId = 1, decoyId = -1,
                position = new DVector3(1.0e6, 0.0, 0.0),
                velocity = new DVector3(0.0, 0.0, 100.0),
                lastUpdatePhysics = 50.0,
            };
            var fresh = ContactMemory.CoastedPosition(in c, 50.0);
            Assert.Equal(c.position.x, fresh.x, 9);              // 同刻 ⇒ 原样（不引入时间偏移）
            Assert.Equal(c.position.z, fresh.z, 9);

            var later = ContactMemory.CoastedPosition(in c, 60.0);   // +10 s
            Assert.Equal(1.0e6, later.x, 6);
            Assert.Equal(1000.0, later.z, 6);                    // 100 m/s × 10 s

            // 外推**不改接触本体**（画面上的点必须停在最后已知位置）
            Assert.Equal(0.0, c.position.z, 9);
        }

        // ── ⑥ 确定性：同输入重放 ⇒ 状态序列逐位相同（无隐藏状态） ──

        [Fact]
        public void ReplayingTheSameInputs_ReproducesTheSamePhaseSequence()
        {
            string a = RunSequence();
            string b = RunSequence();
            Assert.Equal(a, b);
            Assert.Contains("Live", a);      // 采样到的序列必须含三态（不做空洞判据）
            Assert.Contains("Shadow", a);
            Assert.Contains("Lost", a);
        }

        private static string RunSequence()
        {
            var tech = new SensorTech { sensitivityMeters = 1.0e6f };
            double r = DetectionCore.EffectiveDetectRange(1.0, tech, 0.0);
            double now = 0.0, lastUpdate = double.NegativeInfinity;
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i <= 40; i++)
            {
                now = i * Dt;
                // 第 21–38 步整段失去目标（36 s > T=30 s ⇒ 一定会出现 Lost；零星空窗只会到 Shadow）
                bool visible = !(i > 20 && i <= 38) && DetectionCore.IsVisible(1.0, r * 0.9, tech, 0.0);
                if (visible) lastUpdate = now;
                sb.Append(ContactMemory.PhaseOf(now, lastUpdate, T)).Append('|');
            }
            return sb.ToString();
        }
    }
}

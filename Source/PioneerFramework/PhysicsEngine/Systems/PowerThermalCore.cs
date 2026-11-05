using System;

namespace PioneerFramework.Components
{
    /// <summary>
    /// **电/热闭环的纯函数内核**（2026-10-22 部件功能化 §5 步 4/5）。
    ///
    /// 设计约束（照设计稿 §0-1/§5 原文）：
    /// · **规则不进步**：这里只吃**快照**（`ShipStats` 的若干 float + 活动系数），
    ///   每群每步 O(1)，**不遍历组件**。谁把组件循环写进这里就是违规；
    /// · **功率守恒**：分配 ≤ 供给（含电池放电）；**优先级 推进 &gt; 护盾 &gt; 武器 &gt; 传感器**；
    /// · **单调性**：供给 ↑ ⇒ brownout 不降；需求 ↑ ⇒ brownout 不升；
    /// · 输出一个 **0–1 的系数**给消费端（`brownout`），消费端只乘，不做循环。
    ///
    /// 术语：
    /// · `brownout = 1` = 全供；`0` = 完全饿死。每通道各有一个系数（优先级就是靠它体现的）；
    /// · `overheat = 1` = 不超温；`&lt;1` = 按超温比例降效（**默认公式，可调**）。
    ///
    /// 纯函数 ⇒ 台架可逐条判（`PowerThermalCoreTests`），也保证同样的输入永远出同样的数（确定性）。
    /// </summary>
    public static class PowerThermalCore
    {
        /// <summary>数值下限（防除零；W 量级下 1e-6 足够小且不会把实数截断）。</summary>
        private const float Eps = 1e-6f;

        /// <summary>一步的输入快照（全部来自聚合 + 群级活动开关，**不是**逐件状态）。</summary>
        public struct Inputs
        {
            /// <summary>发电（W）：Σ `ShipStats.powerGen`。</summary>
            public float genWatts;

            /// <summary>电池放电上限（W）：Σ `ShipStats.powerDischargeLimit`。</summary>
            public float dischargeLimitWatts;

            /// <summary>电池充电上限（W）：Σ `ShipStats.powerChargeLimit`。</summary>
            public float chargeLimitWatts;

            /// <summary>电池容量（J）：Σ `ShipStats.powerStorage`（= 容量口径，不是当前存量）。</summary>
            public float capacityJoules;

            /// <summary>当前存量（J，群级状态，持久化）。</summary>
            public float storedJoules;

            /// <summary>四个通道的**满负荷标称**功耗（W）。</summary>
            public float nominalPropulsion;
            public float nominalShield;
            public float nominalWeapon;
            public float nominalSensor;

            /// <summary>四个通道的**活动系数**（0–1）：0 = 本步不用电（如未点火）。</summary>
            public float activityPropulsion;
            public float activityShield;
            public float activityWeapon;
            public float activitySensor;

            /// <summary>产热 / 散热（W）：Σ `ShipStats.heatProduction` / `heatDissipation`。</summary>
            public float heatProduction;
            public float heatDissipation;

            /// <summary>本步时长（s，物理秒）。≤0 ⇒ 只算分配、不记账（预热/测试）。</summary>
            public float dtSeconds;
        }

        /// <summary>一步的输出（brownout 各通道 + 更新后的存量 + 超温系数）。</summary>
        public struct Outputs
        {
            public float supplyWatts;      // 可用供给（发电 + 放电，已按存量夹过）
            public float demandWatts;      // 需求（标称 × 活动）
            public float usedWatts;        // 实际分配

            public float brownout;         // 总系数 = used / demand（demand=0 ⇒ 1）
            public float brownoutPropulsion;
            public float brownoutShield;
            public float brownoutWeapon;
            public float brownoutSensor;

            public float batteryDischargeWatts;
            public float batteryChargeWatts;
            public float storedJoules;     // 更新后的存量（夹在 [0, capacity]）

            public float overheat;         // 1 = 不超温；<1 = 降效系数
        }

        /// <summary>
        /// 分配一步。顺序固定 = **推进 → 护盾 → 武器 → 传感器**（§5 的优先级），
        /// 低优先级只能吃高优先级剩下的；剩余发电先充电（受充电上限约束），不足再由电池放电（受放电上限/存量约束）。
        /// </summary>
        public static Outputs Step(in Inputs i)
        {
            float dt = i.dtSeconds;
            bool bookkeeping = dt > 0f;

            // ── 需求（标称 × 活动；活动系数夹到 [0,1]）──
            float dProp = Demand(i.nominalPropulsion, i.activityPropulsion);
            float dShield = Demand(i.nominalShield, i.activityShield);
            float dWeapon = Demand(i.nominalWeapon, i.activityWeapon);
            float dSensor = Demand(i.nominalSensor, i.activitySensor);
            float demand = dProp + dShield + dWeapon + dSensor;

            // ── 供给：发电 + 电池可放（受放电上限与存量约束）──
            float stored = Clamp(i.storedJoules, 0f, Math.Max(0f, i.capacityJoules));
            float batteryAvailable = 0f;
            if (bookkeeping && stored > 0f)
                batteryAvailable = Math.Min(Math.Max(0f, i.dischargeLimitWatts), stored / dt);
            float supply = Math.Max(0f, i.genWatts) + batteryAvailable;

            // ── 按优先级分配（只吃剩余）──
            float remaining = supply;
            float tProp = Take(dProp, ref remaining);
            float tShield = Take(dShield, ref remaining);
            float tWeapon = Take(dWeapon, ref remaining);
            float tSensor = Take(dSensor, ref remaining);
            float used = tProp + tShield + tWeapon + tSensor;

            // ── 记账：发电优先、电池补差额；有剩余先充电（受充电上限/容量约束）──
            float batteryUsed = 0f;
            float batteryCharged = 0f;
            if (bookkeeping)
            {
                float gen = Math.Max(0f, i.genWatts);
                batteryUsed = Math.Max(0f, used - gen);
                stored -= batteryUsed * dt;

                float surplus = Math.Max(0f, gen - used);
                if (surplus > 0f && i.chargeLimitWatts > 0f)
                {
                    float room = Math.Max(0f, i.capacityJoules) - stored;
                    batteryCharged = Math.Min(Math.Min(surplus, i.chargeLimitWatts), room / dt);
                    if (batteryCharged < 0f) batteryCharged = 0f;
                    stored += batteryCharged * dt;
                }
                stored = Clamp(stored, 0f, Math.Max(0f, i.capacityJoules));
            }

            Outputs o;
            o.supplyWatts = supply;
            o.demandWatts = demand;
            o.usedWatts = used;
            o.brownout = demand > Eps ? used / demand : 1f;
            o.brownoutPropulsion = Ratio(tProp, dProp);
            o.brownoutShield = Ratio(tShield, dShield);
            o.brownoutWeapon = Ratio(tWeapon, dWeapon);
            o.brownoutSensor = Ratio(tSensor, dSensor);
            o.batteryDischargeWatts = batteryUsed;
            o.batteryChargeWatts = batteryCharged;
            o.storedJoules = stored;
            o.overheat = OverheatFactor(i.heatProduction, i.heatDissipation);
            return o;
        }

        /// <summary>
        /// **某个通道能不能被"全额供给"**（用户 2026-10-26 裁定：推进**欠电即拒绝点火**，
        /// 不是按比例降推力）。判据 = 发电 + 电池可放 ≥ 该通道需求；电池可放受放电上限与
        /// 存量/dt 双约束（与 `Step` 同一口径）。
        ///
        /// 只做"够不够"的布尔判断，**不记账**（不动存量）⇒ 可以在点火前反复问。
        /// `demand ≤ 0` ⇒ 恒 true（不耗电的旧船逐位不变）。
        /// </summary>
        public static bool CanSupplyChannel(in Inputs i, float demand)
        {
            if (!(demand > 0f)) return true;

            float dt = i.dtSeconds > 0f ? i.dtSeconds : 0f;
            float battery = 0f;
            if (dt > 0f)
            {
                float stored = Clamp(i.storedJoules, 0f, Math.Max(0f, i.capacityJoules));
                if (stored > 0f)
                    battery = Math.Min(Math.Max(0f, i.dischargeLimitWatts), stored / dt);
            }
            else
            {
                // 没有步长（还没跑过物理步）⇒ 只看放电上限（存量按"能放满一拍"保守处理）
                battery = Math.Max(0f, i.dischargeLimitWatts);
            }
            return Math.Max(0f, i.genWatts) + battery >= demand;
        }

        /// <summary>
        /// **超温降效系数**（默认公式，可调）：`excess = max(0, 产热 − 散热)`，
        /// `overheat = 1 / (1 + excess / max(散热, 1))`。
        /// · 不超温 ⇒ **恰 1.0**（×1.0 精确，未接热源时逐位不变）；
        /// · 超一倍散热 ⇒ 0.5；超无穷 ⇒ →0；
        /// · 对 `excess` **单调递减**、对散热**单调递增**（判据见测试）。
        /// ⚠ 刻意**不做传导网络/温度场**（§5 原文）：这里只有"总产热 vs 总散热"的标量。
        /// </summary>
        public static float OverheatFactor(float heatProduction, float heatDissipation)
        {
            float excess = heatProduction - heatDissipation;
            if (!(excess > 0f)) return 1f;
            // 散热 → 0 时必须让系数 → 0（否则 `max(diss, 1)` 会在 0 处造出一个非单调的台阶：
            // 散 0 W 得 0.0025、散 50 W 反而 0.125 —— 台架当场抓住过）。用 1e-3 W 做分母下限。
            float diss = heatDissipation > 1e-3f ? heatDissipation : 1e-3f;
            return 1f / (1f + excess / diss);
        }

        private static float Demand(float nominal, float activity)
        {
            if (!(nominal > 0f)) return 0f;
            float a = Clamp(activity, 0f, 1f);
            return nominal * a;
        }

        private static float Take(float demand, ref float remaining)
        {
            if (!(demand > 0f)) return 0f;
            float take = demand <= remaining ? demand : remaining;
            remaining -= take;
            return take;
        }

        private static float Ratio(float taken, float demand) => demand > Eps ? Clamp(taken / demand, 0f, 1f) : 1f;

        private static float Clamp(float v, float lo, float hi)
        {
            if (!(v > lo)) return lo;     // 含 NaN ⇒ lo
            return v > hi ? hi : v;
        }
    }
}

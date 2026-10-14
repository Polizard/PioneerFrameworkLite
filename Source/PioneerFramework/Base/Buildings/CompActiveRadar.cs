using System.Collections.Generic;
using RimWorld;
using Verse;
using PioneerFramework.Combat;

namespace PioneerFramework
{
    /// <summary>
    /// 主动雷达建筑 Comp 的配置（**[V7]/[E2]**）。挂在建筑 ThingDef 的 comps 列表里。
    /// 与传感器阵列同量纲，但多了**主动**那一半：增益加成 + 开机时的信号增量 + **波束几何**（半锥角/距离上限）。
    /// </summary>
    public class CompProperties_ActiveRadar : CompProperties
    {
        /// <summary>主动增益**加成**（≥0）：有效探测距离倍率 = `1 + 本值`。</summary>
        public float activeGainBonus = 2f;

        /// <summary>主动开机时的信号增量（S_REF 倍数；加在 `SignalModel.ActivePingMultiplier` 基准之上）。</summary>
        public float activeRadiation = 4f;

        /// <summary>波束半锥角（度）——[V7] 三模式（跟踪/手选/扫掠）共用的几何。</summary>
        public float beamHalfAngleDeg = 15f;

        /// <summary>波束距离上限（m）。</summary>
        public float beamRangeMeters = 20_000_000f;

        /// <summary>扫掠角速度（deg/s，physicsTick 轴）：扫掠模式下相位 = `physicsTick × 本值`。
        /// ⚠ 它就是"信息处理速度"在雷达上的化身（以后要改成有限处理速度时，旋钮在这里）。</summary>
        public float scanRateDegPerSec = 6f;

        public CompProperties_ActiveRadar()
        {
            compClass = typeof(CompActiveRadar);
        }
    }

    /// <summary>
    /// **主动雷达建筑（[V7]/[E2]，2026-10-01）** —— 与 `CompSensorArray` **同一个模子**：
    /// **登记**（`PostSpawnSetup` → 所属地图的 `SpaceMapComp`；`PostDeSpawn` 注销）与**被读取**
    /// （`SpaceMapComp.RadarCapability()` 汇总 → `SpaceGameComp` 每**物理步**投影进 `Group.RadarCap`）。
    ///
    /// ⚠ **空壳**：不 Tick、不自己改 Group（pocket map 隐藏时 `Thing.CompTick` 不可靠）。
    /// ⚠ **开机开关不在本 comp 上**——W1 枢纽的**唯一开关**是 `SpaceEntityGroup.ActivePingEnabled`
    ///   （设计稿 §11.2；没装雷达的组也能开，但只有装了才"看得更远"，见 `RadarCapability`）。
    /// ⚠ 波束的**指向**（三模式：跟踪 / 手选方位 / 扫掠）是**组级**状态（多台雷达共用一个外沿），
    ///   本 comp 只提供几何参数；指向状态见 `SpaceEntityGroup` 的雷达节（[E2]/[V3] 落地时接）。
    /// ⚠ 贡献**随毁损而掉**：由投影侧按 `HitPoints / MaxHitPoints` 折算（用户 2026-10-01 裁定）。
    /// </summary>
    public class CompActiveRadar : ThingComp
    {
        public CompProperties_ActiveRadar Props => (CompProperties_ActiveRadar)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent.Map?.GetComponent<SpaceMapComp>()?.RegisterActiveRadar(parent);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode)
        {
            base.PostDeSpawn(map, mode);
            map?.GetComponent<SpaceMapComp>()?.UnregisterActiveRadar(parent);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Gizmo（**[E2] 交互装门**，2026-10-11；计划 §7 [E2]：开关挂"有传感器组件的建筑"）
        // ═══════════════════════════════════════════════════════════════════
        //
        // ① **开机 / 静默** —— 写组级 `ActivePingEnabled`（W1 枢纽：开机 ⇒ 看得更远 + 自己更响）；
        // ② **波束三模式** —— 扫掠 / 手选方位 / 跟踪目标（用户 2026-10-01 裁定"三者我都要"），
        //    写的也全是**组级**状态（`RadarAimMode` / `RadarManualDir` / `RadarTrackGroupId`）。
        // ⇒ 建筑只提供能力；开关、取舍、指向都在群上（设计稿 §11.2；多台雷达共用一个外沿）。
        //
        // ⚠ 玩家闸 = **船的派系**（归属链），不是建筑自己的 `Thing.Faction` —— 调试直生的建筑没有派系，
        //   挂 `parent.Faction != Faction.OfPlayer` 会让调试场景里按钮永远不出现（2026-10-07 踩过）。见 `ShipBuildingUI`。

        /// <summary>手选方位的六个候选：**全部走 `ThrustAxis`**（船体局部六向 —— 与主推轴 / SAS 同一套命名与换系口）。</summary>
        private static readonly (ThrustAxis axis, string label)[] DirectionChoices =
        [(ThrustAxis.Forward, "船头（+Z）"),
            (ThrustAxis.Back,    "船尾（-Z，主推方向）"),
            (ThrustAxis.Left,    "左舷（-X）"),
            (ThrustAxis.Right,   "右舷（+X）"),
            (ThrustAxis.Up,      "上方（+Y，甲板朝天）"),
            (ThrustAxis.Down,    "下方（-Y，舱底朝下）"),
        ];

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra()) yield return g;

            var group = ShipBuildingUI.GetOwnerGroup(parent);
            if (group == null || !ShipBuildingUI.IsPlayerOwned(parent, group)) yield break;

            // 波束几何读数（能力面由投影每物理步写入 ⇒ 是当前值）——放各按钮的 desc 里，省一次开窗
            var cap = group.RadarCap;
            string geo = $"（波束：半锥角 {cap.beamHalfAngleDeg:F0}° · 距离上限 {cap.beamRangeMeters / 1000f:F0} km · 扫掠 {cap.scanRateDegPerSec:F0}°/s）";

            // ① 开机 / 静默（W1 枢纽的唯一开关）
            yield return new Command_Toggle
            {
                defaultLabel = group.ActivePingEnabled ? "雷达开机（辐射中）" : "雷达静默（被动）",
                defaultDesc = "主动探测开关（W1 取舍）：开机后探测距离 ×(1+增益)，代价是自身信号显著升高——更早被对方发现；"
                    + "静默 = 只听不说。\n" + geo,
                icon = TexCommand.DesirePower,
                isActive = () => group.ActivePingEnabled,
                // 读数（2026-10-11 补：用户报"看不见波束锥"时，日志里分不清"没开机"还是"没画"）
                toggleAction = () =>
                {
                    group.ActivePingEnabled = !group.ActivePingEnabled;
                    Log.Message($"[雷达] G{group.GroupId} 开机 → {group.ActivePingEnabled}");
                },
            };

            // ② 波束三模式 = 三按钮 + 两个选择器（手选方位 / 跟踪目标）；按钮标签带"当前"读数
            bool scan = group.RadarAimMode == SpaceEntityGroup.RadarAim.Scan;
            bool manual = group.RadarAimMode == SpaceEntityGroup.RadarAim.Manual;
            bool track = group.RadarAimMode == SpaceEntityGroup.RadarAim.Track;
            yield return new Command_Action
            {
                defaultLabel = scan ? "波束：扫掠（当前）" : "波束：扫掠",
                defaultDesc = "相位随物理钟推进、在赤道面内转圈（默认模式；跟踪拿不到目标时也退化为它）。\n" + geo,
                icon = TexCommand.FireAtWill,
                action = () => SetBeamMode(group, SpaceEntityGroup.RadarAim.Scan, "扫掠"),
            };
            yield return new Command_Action
            {
                defaultLabel = manual ? "波束：手选方位…（当前）" : "波束：手选方位…",
                defaultDesc = "把波束**固定指向船体的某一个方向**（船头/船尾/左舷/右舷/上方/下方；按本船当前姿态换算到惯性系）。\n" + geo,
                icon = TexCommand.Attack,
                action = () => ShowDirectionPicker(group),
            };
            yield return new Command_Action
            {
                defaultLabel = track
                    ? $"波束：跟踪 G{group.RadarTrackGroupId}…（当前）"
                    : "波束：跟踪目标…",
                defaultDesc = "从**看得见的**编队里选一个，波束持续指向它的当前位置；它失联/被毁后自动退化为扫掠。\n" + geo,
                icon = TexButton.Info,
                action = () => ShowTrackPicker(group),
            };
        }

        private static void SetBeamMode(SpaceEntityGroup group, SpaceEntityGroup.RadarAim mode, string note)
        {
            group.RadarAimMode = mode;
            Log.Message($"[雷达] G{group.GroupId} 波束模式 → {note}");
        }

        /// <summary>
        /// 手选方位：**不手写姿态数学** —— 六向全部走 `group.GetThrustAxisSystem(axis)`，
        /// 那是主推轴 / SAS 自动对准用的**同一个换系口**（`DVector3.Rotate(axis.ShipVector(), CurrentOrbit.rotation)`）。
        /// </summary>
        private void ShowDirectionPicker(SpaceEntityGroup group)
        {
            var options = new List<FloatMenuOption>();
            foreach (var (axis, label) in DirectionChoices)
            {
                ThrustAxis captured = axis;
                options.Add(new FloatMenuOption(label, () =>
                {
                    DVector3 dir = group.GetThrustAxisSystem(captured);
                    group.RadarManualDir = dir;                        // setter 自动归一
                    group.RadarAimMode = SpaceEntityGroup.RadarAim.Manual;
                    Log.Message($"[雷达] G{group.GroupId} 手选方位 = {label}｜惯性系 ({dir.x:F2},{dir.y:F2},{dir.z:F2})");
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        /// <summary>
        /// 跟踪对象选择器：**只列看得见的**（`SensorManager.PlayerCanSee`，影子算看得见 —— 与视图/开火同一条判据），
        /// 影子期条目带 `TargetListCore` 的标注（"最后已知位置"）。
        /// </summary>
        private void ShowTrackPicker(SpaceEntityGroup group)
        {
            var sg = Current.Game?.GetComponent<SpaceGameComp>();
            if (sg == null) return;
            var sensor = Current.Game?.GetComponent<SensorManager>();
            var cm = Current.Game?.GetComponent<CombatManager>();
            DVector3 myPos = cm != null ? cm.GetGroupPos(group) : DVector3.zero;

            var options = new List<FloatMenuOption>();
            foreach (var g in sg.GetAllGroups())
            {
                if (!g.IsActive || g.GroupId == group.GroupId) continue;
                if (sensor != null && !sensor.PlayerCanSee(g)) continue;   // [V3] 列表准入（红线：列表读认知层）

                string dist = cm != null
                    ? $"（{(cm.GetGroupPos(g) - myPos).magnitude / 1000.0:F0} km）"
                    : "";
                string suffix = sensor != null ? sensor.PlayerPhaseSuffix(g) : "";
                var captured = g;
                options.Add(new FloatMenuOption($"G{g.GroupId}{dist}{suffix}", () =>
                {
                    group.RadarTrackGroupId = captured.GroupId;
                    group.RadarAimMode = SpaceEntityGroup.RadarAim.Track;
                    Log.Message($"[雷达] G{group.GroupId} 跟踪目标 → G{captured.GroupId}");
                }));
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("没有看得见的编队", null));
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}

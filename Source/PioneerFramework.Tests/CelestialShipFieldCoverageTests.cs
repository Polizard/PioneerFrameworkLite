using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PioneerFramework;
using Xunit;

namespace PioneerFramework.Tests;

/// <summary>检查实际编译后的字段读取及写回；新增字段不能仅更新字符串清单便绕过复制和发布。</summary>
public class CelestialShipFieldCoverageTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IEnumerable<object[]> CopiedTypes => new[] {
        typeof(SpaceGameComp), typeof(SpaceEntityGroup), typeof(SpaceEntity), typeof(SpacePart), typeof(PropulsionData), typeof(SpaceMapComp)
    }.Select(type => new object[] { type });

    [Theory]
    [MemberData(nameof(CopiedTypes))]
    public void EveryInputFieldIsActuallyReadAndWrittenOrHasAnExplicitLifetimePolicy(Type type)
    {
        var excluded = InputLifetimePolicies(type);
        var method = type.GetMethod("CopyCelestialInputFieldsTo", Methods);
        Assert.NotNull(method);
        AssertFieldCoverage(type, method, excluded, new Dictionary<string, string>());
    }

    [Fact]
    public void EveryGroupFieldIsActuallyPublishedOrHasAnExplicitIdentityScratchOrResetPolicy()
    {
        var excluded = new Dictionary<string, string> {
            ["preparedPhysicsHost"] = "准备图的宿主路由只在私有副本使用，实时组必须继续使用原有宿主。",
            ["celestialBuffers"] = "私有容器池的身份和所有权不向实时组发布。",
            ["celestialSource"] = "回收副本的来源标记只属于私有准备图。",
            ["entityIds"] = "本步不增删组成员，保留实时成员集合身份。",
            ["thrustSliceScratch"] = "推进切片工作列表在每次 Prepare 重新填充。",
            ["inertiaScratch"] = "惯量计算临时槽不作为模拟结果发布。",
            // 2026-10-31（合并）：十三批新增字段的**发布策略**——
            // 只有"这一步算出来的物理输出"才回写实时组（电/热/能量/落地/轮子），
            // 输入与结构量一律单向下发（复制进副本），预演步不得把副本值写回实时组。
            ["attitudeAxis"] = "姿态请求是玩家/控制器输入，单向下发。",
            ["attitudePriority"] = "姿态请求是玩家/控制器输入，单向下发。",
            ["attitudeRateScale"] = "姿态请求是玩家/控制器输入，单向下发。",
            ["attitudeTargetSystem"] = "姿态请求是玩家/控制器输入，单向下发。",
            ["autoStageEnabled"] = "自动分级参数是策略输入，单向下发。",
            ["autoStageFuelFraction"] = "自动分级参数是策略输入，单向下发。",
            ["autoStageMinSeconds"] = "自动分级参数是策略输入，单向下发。",
            // 注：推进网络选择 `<burnEnabledNetworks>k__BackingField` 已在既有发布路径里回写，无需排除。
            ["enabledNetworkDefName"] = "推进网络选择是输入，单向下发。",
            ["enabledNetworkRcsOnly"] = "推进网络选择是输入，单向下发。",
            ["playerEnabledNetworks"] = "玩家网络选择是输入，单向下发。",
            ["RequiresControlCore"] = "控制核心是结构事实（由零件决定），单向下发。",
            ["HasIntactControlCore"] = "控制核心是结构事实（由零件决定），单向下发。",
            ["maskSource"] = "网络掩码缓存属于副本内部；maskSource 是实时推进源引用，禁止回写。",
            ["maskVersion"] = "网络掩码缓存属于副本内部，不回写。",
            ["networkMask"] = "网络掩码缓存属于副本内部，不回写。",
            ["networkSelectionVersion"] = "网络掩码缓存属于副本内部，不回写。",
            ["burnZeroDvSteps"] = "燃烧诊断计数只服务读数，不进入发布的物理状态。",
            ["burnDrySteps"] = "燃烧诊断计数只服务读数，不进入发布的物理状态。",
            ["impulseProbeSteps"] = "燃烧诊断计数只服务读数，不进入发布的物理状态。",
#if DEBUG
            ["debugPosCacheHits"] = "DEBUG 查询命中计数不属于已提交模拟状态。",
            ["debugPosCacheMisses"] = "DEBUG 查询未命中计数不属于已提交模拟状态。",
            ["debugVelCacheHits"] = "DEBUG 查询命中计数不属于已提交模拟状态。",
            ["debugVelCacheMisses"] = "DEBUG 查询未命中计数不属于已提交模拟状态。",
#endif
        };
        var resets = new Dictionary<string, string> {
            ["cachedPropulsionAggregator"] = "发布后清空聚合器；私有准备图引用不能进入实时缓存。",
            ["cachedMapPropulsionAggregator"] = "发布后清空地图聚合器；下一次使用时按实时组重建。",
            ["cachedPropulsionStamp"] = "聚合器失效时重置修订戳，禁止复用旧结果。",
            ["cachedMapPropulsionStamp"] = "地图聚合器失效时重置修订戳。",
        };
        var method = typeof(SpaceEntityGroup).GetMethod("PublishCelestialStep", Methods);
        Assert.NotNull(method);
        AssertFieldCoverage(typeof(SpaceEntityGroup), method, excluded, resets);
    }

    private static Dictionary<string, string> InputLifetimePolicies(Type type)
    {
        var result = new Dictionary<string, string>();
        void Add(string names, string reason)
        {
            foreach (string name in names.Split('|')) result.Add(name, reason);
        }
        if (type == typeof(SpaceGameComp))
        {
            Add("celestialStepBuffers", "宿主自己的准备缓冲池由 MemberwiseClone 保留，不是每步输入。");
            Add("timeScaleRef|sensorMgrRef|combatMgrRef|avmRef|celestialRef", "readonly 宿主组件定位缓存由首次 MemberwiseClone 共享；回收副本沿用同一宿主会话。");
            // 2026-10-31（合并）：十三批新加的"计划/设计表 + 调试计数器"——不是每步物理输入，
            // 且是可变容器/设计期 id ⇒ 预演副本既不复制也不得改写它们（否则预演会污染实时计划表）。
            Add("autoStagePlans|payloadDesigns|nextPayloadDesignId", "计划表/设计表/设计期 id 分配不是物理输入；副本不得改写实时计划。");
            Add("ladderPlanCalls|ladderWarmBg", "阶梯耗时计数与后台预热文本纯属开发期读数，不进入物理。");
        }
        if (type == typeof(SpaceEntityGroup))
        {
            // 2026-10-31（合并）：十三批的推进网络选择缓存。`maskSource` 是指向实时推进源的引用，
            // 跨图共享会让副本烧错源；掩码由副本按 `burnEnabledNetworks` 输入重建。
            Add("maskSource|maskVersion|networkMask|networkSelectionVersion",
                "推进网络选择缓存由副本按输入重建；maskSource 是指向实时推进源的引用，禁止跨图共享。");
        }
        if (type == typeof(SpaceEntityGroup) || type == typeof(SpaceEntity) || type == typeof(SpacePart) || type == typeof(SpaceMapComp))
            Add("celestialBuffers|celestialSource", "准备图回收使用的容器身份和来源标记由 CopyForCelestialStep 管理，不从实时输入覆盖。");
        if (type == typeof(SpaceMapComp))
            Add("spaceWeapons|sensorArrays|jammers|activeRadars|networks|thrusters|intakes|totalThrustByAxis",
                "readonly 原生设备和网络结构作为步输入共享，质量状态由独立 propellantMass 容器复制。");
        return result;
    }

    private static void AssertFieldCoverage(Type type, MethodInfo method, Dictionary<string, string> excluded, Dictionary<string, string> resets)
    {
        var fields = type.GetFields(Fields).Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var policy in excluded.Concat(resets))
        {
            Assert.Contains(policy.Key, fields);
            Assert.False(string.IsNullOrWhiteSpace(policy.Value));
        }
        Assert.Empty(excluded.Keys.Intersect(resets.Keys));
        var accesses = ActualFieldAccesses(method, type).ToArray();
        var writes = accesses.Where(access => access.Write).Select(access => access.Field.Name).ToHashSet(StringComparer.Ordinal);
        var reads = accesses.Where(access => !access.Write).Select(access => access.Field.Name).ToHashSet(StringComparer.Ordinal);
        var expectedWrites = fields.Except(excluded.Keys).Order().ToArray();
        var expectedReads = expectedWrites.Except(resets.Keys).Order().ToArray();
        Assert.Equal(expectedWrites, writes.Order().ToArray());
        Assert.Equal(expectedReads, reads.Order().ToArray());

        // 同名字段必须来自同名输入；仅核对读取/写入集合会漏掉两个字段互换的错误。
        FieldInfo lastRead = null;
        foreach (var access in accesses)
        {
            if (!access.Write) lastRead = access.Field;
            else if (!resets.ContainsKey(access.Field.Name)) Assert.Equal(access.Field, lastRead);
        }
    }

    private static IEnumerable<(bool Write, FieldInfo Field)> ActualFieldAccesses(MethodBase method, Type type)
    {
        foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
        {
            if (instruction.operand is FieldInfo field && field.DeclaringType == type &&
                (instruction.opcode == OpCodes.Ldfld || instruction.opcode == OpCodes.Stfld))
                yield return (instruction.opcode == OpCodes.Stfld, field);
            // 自动属性也必须核对真实 backing field，不把一个 setter 名字视作已经发布的证据。
            if (instruction.operand is MethodInfo accessor && accessor.DeclaringType == type && accessor.IsSpecialName &&
                (accessor.Name.StartsWith("get_", StringComparison.Ordinal) || accessor.Name.StartsWith("set_", StringComparison.Ordinal)))
                foreach (var access in ActualFieldAccesses(accessor, type)) yield return access;
        }
    }
}

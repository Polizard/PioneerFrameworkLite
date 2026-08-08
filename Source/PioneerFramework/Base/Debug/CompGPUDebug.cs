using UnityEngine;
using Verse;
using RimWorld;
using System.Text;
using System.Linq;
using System.Collections.Generic;

namespace PioneerFramework
{
    /// <summary>
    /// GPU计算技术验证调试Comp属性
    /// </summary>
    public class CompProperties_GPUDebug : CompProperties
    {
        public CompProperties_GPUDebug()
        {
            this.compClass = typeof(CompGPUDebug);
        }
    }

    /// <summary>
    /// GPU计算技术验证调试Comp
    /// 提供Gizmo用于打开GPU测试窗口
    /// </summary>
    public class CompGPUDebug : ThingComp
    {
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
                yield return g;

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            // 打开GPU计算测试窗口
            yield return new Command_Action
            {
                defaultLabel = "GPU计算测试",
                defaultDesc = "测试ComputeShader和Unity Physics GPU加速支持",
                icon = TexButton.Rename,
                action = () =>
                {
                    var existing = Find.WindowStack.Windows
                        .FirstOrDefault(w => w is Window_GPUComputeTest);

                    if (existing != null)
                        existing.Close();

                    Find.WindowStack.Add(new Window_GPUComputeTest());
                }
            };

            // 快速GPU信息检测
            yield return new Command_Action
            {
                defaultLabel = "GPU信息",
                defaultDesc = "显示当前GPU信息到Log",
                icon = TexButton.Info,
                action = () => DumpGPUInfo()
            };

            // ComputeShader支持检测
            yield return new Command_Action
            {
                defaultLabel = "ComputeShader检测",
                defaultDesc = "检测系统是否支持ComputeShader",
                action = () => CheckComputeShaderSupport()
            };

            // Unity Physics GPU后端检测
            yield return new Command_Action
            {
                defaultLabel = "Physics GPU检测",
                defaultDesc = "检测Unity Physics是否使用GPU加速",
                action = () => CheckPhysicsGPUAcceleration()
            };
        }

        private void DumpGPUInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== GPU信息 ===");
            sb.AppendLine("GPU名称: " + SystemInfo.graphicsDeviceName);
            sb.AppendLine("GPU类型: " + SystemInfo.graphicsDeviceType);
            sb.AppendLine("显存大小: " + SystemInfo.graphicsMemorySize + " MB");
            sb.AppendLine("Shader版本: " + SystemInfo.graphicsShaderLevel);
            sb.AppendLine("ComputeShader支持: " + SystemInfo.supportsComputeShaders);

            Log.Message(sb.ToString());
            Messages.Message("GPU信息已输出到Log", MessageTypeDefOf.TaskCompletion);
        }

        private void CheckComputeShaderSupport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== ComputeShader支持检测 ===");
            sb.AppendLine("supportsComputeShaders: " + SystemInfo.supportsComputeShaders);
            sb.AppendLine("graphicsDeviceType: " + SystemInfo.graphicsDeviceType);

            bool canRun = SystemInfo.supportsComputeShaders;
            sb.AppendLine("结论: " + (canRun ? "支持ComputeShader" : "不支持ComputeShader"));

            if (!canRun)
            {
                sb.AppendLine("警告: 当前平台不支持ComputeShader");
                sb.AppendLine("可能原因:");
                sb.AppendLine("  - GPU太旧或不支持DX11+");
                sb.AppendLine("  - Unity版本过旧");
                sb.AppendLine("  - WebGL等不支持的平台");
            }

            Log.Message(sb.ToString());
            Messages.Message(
                canRun ? "ComputeShader支持!" : "ComputeShader不支持",
                MessageTypeDefOf.TaskCompletion);
        }

        private void CheckPhysicsGPUAcceleration()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Unity Physics GPU加速检测 ===");
            sb.AppendLine("Unity版本: " + Application.unityVersion);
            sb.AppendLine("平台: " + Application.platform);
            sb.AppendLine("Physics.autoSyncTransforms: " + Physics.autoSyncTransforms);

            sb.AppendLine();
            sb.AppendLine("注意: Unity PhysX传统上不使用GPU加速");
            sb.AppendLine("      但DOTS Physics提供了CPU多核并行");
            sb.AppendLine("      如需GPU加速，可考虑:");
            sb.AppendLine("        1. ComputeShader手动实现特定算法");
            sb.AppendLine("        2. GPU Instancing批量渲染");
            sb.AppendLine("        3. DOTS + Job System CPU并行");

            Log.Message(sb.ToString());
            Messages.Message("Physics信息已输出到Log", MessageTypeDefOf.TaskCompletion);
        }
    }
}

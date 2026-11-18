using UnityEngine;
using Verse;
using RimWorld;
using System.Text;

namespace PioneerFramework
{
    /// <summary>
    /// 测试 RimWorld 运行时是否启用了 Unity 3D Physics 模块。
    /// 打开弹窗 → 点"运行测试" → 创建 BoxCollider 打射线 → 报告结果。
    /// </summary>
    public class Window_Physics3DTest : Window
    {
        private string log = "";
        private GameObject testObj;
        private bool test1Success;
        private bool test2Success;
        private bool test3Success;
        private Vector2 scrollPos;

        public Window_Physics3DTest()
        {
            doCloseButton = true;
            doCloseX = true;
            forcePause = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            optionalTitle = "3D Physics 测试";
            windowRect = new Rect(200f, 200f, 600f, 450f);
        }

        public override void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("Unity 版本: " + Application.unityVersion);
            listing.Label("平台: " + Application.platform);

            listing.Gap(12f);

            if (listing.ButtonText("运行测试", null))
                RunTest();

            listing.Gap(12f);

            // 结果显示
            if (!string.IsNullOrEmpty(log))
            {
                var logRect = listing.GetRect(inRect.height - 180f);
                Widgets.LabelScrollable(logRect, log, ref scrollPos);
            }

            listing.End();
        }

        private void RunTest()
        {
            test1Success = false;
            test2Success = false;
            test3Success = false;

            var sb = new StringBuilder();
            sb.AppendLine("===== 3D Physics 测试开始 =====");
            sb.AppendLine();

            Cleanup();

            // ── 测试 1: 创建 BoxCollider ──
            sb.AppendLine("[测试 1] 创建 GameObject + BoxCollider...");
            try
            {
                testObj = new GameObject("Physics3DTestCollider");
                testObj.transform.position = new Vector3(0, 0, 10f);
                var collider = testObj.AddComponent<BoxCollider>();
                collider.size = Vector3.one * 2f;
                test1Success = true;
                sb.AppendLine("  → 成功");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("  ✗ 失败: " + e.GetType().Name + " - " + e.Message);
            }

            // ── 测试 2: Physics.Raycast ──
            sb.AppendLine();
            sb.AppendLine("[测试 2] Physics.Raycast...");
            if (!test1Success)
            {
                sb.AppendLine("  → 跳过（测试 1 失败，无 Collider 可检测）");
            }
            else
            {
                try
                {
                    Ray ray = new Ray(new Vector3(0, 0, 0), new Vector3(0, 0, 1));
                    RaycastHit hit;
                    bool hitSomething = Physics.Raycast(ray, out hit, 100f);

                    if (hitSomething)
                    {
                        test2Success = true;
                        sb.AppendLine("  → 命中!  target: " + hit.collider.gameObject.name);
                        sb.AppendLine("  → 命中点: " + hit.point);
                    }
                    else
                    {
                        sb.AppendLine("  ✗ 未命中（可能 Layer 或碰撞矩阵未配置）");
                    }
                }
                catch (System.Exception e)
                {
                    sb.AppendLine("  ✗ 异常: " + e.GetType().Name + " - " + e.Message);
                }
            }

            // ── 测试 3: Physics.SphereCast ──
            sb.AppendLine();
            sb.AppendLine("[测试 3] Physics.SphereCast...");
            if (!test1Success)
            {
                sb.AppendLine("  → 跳过（测试 1 失败）");
            }
            else
            {
                try
                {
                    Ray ray2 = new Ray(new Vector3(-5, 0, 0), new Vector3(0, 0, 1));
                    RaycastHit hit2;
                    bool sphereHit = Physics.SphereCast(ray2, 1f, out hit2, 100f);
                    if (sphereHit)
                    {
                        test3Success = true;
                        sb.AppendLine("  → 命中");
                    }
                    else
                    {
                        sb.AppendLine("  ✗ 未命中");
                    }
                }
                catch (System.Exception e)
                {
                    sb.AppendLine("  ✗ 异常: " + e.GetType().Name + " - " + e.Message);
                }
            }

            // ── 结论 ──
            sb.AppendLine();
            sb.AppendLine("===== 结论 =====");
            if (!test1Success)
            {
                sb.AppendLine("3D Physics 模块不可用（BoxCollider 创建失败）");
                sb.AppendLine("→ 需使用纯 C# 数学方案（~60 行 OBB 检测）");
            }
            else if (test2Success || test3Success)
            {
                sb.AppendLine("3D Physics 正常工作！");
                sb.AppendLine("→ 可直接使用 Physics.Raycast / SphereCast");
            }
            else
            {
                sb.AppendLine("BoxCollider 可用但射线检测均未命中");
                sb.AppendLine("→ 可能是 Layer 或碰撞矩阵问题，可尝试设置 layer=0 重试");
            }

            log = sb.ToString();
        }

        public override void Close(bool doCloseSound = true)
        {
            Cleanup();
            base.Close(doCloseSound);
        }

        private void Cleanup()
        {
            if (testObj != null)
            {
                Object.Destroy(testObj);
                testObj = null;
            }
        }
    }
}

using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.Launch
{
    /// <summary>
    /// 统一火箭发射窗口：选择“原版 TransportPod 发射”或“PFL 入轨发射”。
    /// </summary>
    public class Dialog_RocketLaunch : Window
    {
        private readonly Thing rocket;
        private RocketLaunchContext context;
        private RocketLaunchMode selectedMode;

        public Dialog_RocketLaunch(Thing rocket)
        {
            this.rocket = rocket;
            doCloseX = true;
            doCloseButton = false;
            closeOnCancel = true;
            absorbInputAroundWindow = true;
            forcePause = true;

            RocketLaunchManager.TryGetLaunchContext(rocket, out context);
            selectedMode = context?.PadComp?.LaunchMode ?? RocketLaunchMode.PflOrbit;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (context == null)
            {
                Widgets.Label(inRect, "无法获取火箭发射上下文，请重新选择火箭。");
                return;
            }

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 30), "火箭发射");
            Text.Font = GameFont.Small;

            float y = inRect.y + 38f;
            float lineHeight = 24f;

            // ── 火箭信息 ──
            string rocketName = context.Rocket?.def?.label ?? context.Rocket?.def?.defName ?? "未知火箭";
            Widgets.Label(new Rect(inRect.x, y, inRect.width, lineHeight), "当前火箭：" + rocketName);
            y += lineHeight;

            if (context.PadComp != null)
                Widgets.Label(new Rect(inRect.x, y, inRect.width, lineHeight), "发射台：" + context.PadComp.CompInspectStringExtra().Replace("\n", " / "));
            y += lineHeight;

            string designText = context.Spec?.rocketDefName ?? "无设计";
            Widgets.Label(new Rect(inRect.x, y, inRect.width, lineHeight), "设计型号：" + designText);
            y += lineHeight + 8f;

            // ── 方式选择 ──
            Widgets.Label(new Rect(inRect.x, y, inRect.width, lineHeight), "发射方式：");
            y += lineHeight;

            Rect vanillaRect = new Rect(inRect.x + 16f, y, inRect.width - 16f, 30f);
            string vanillaLabel = (selectedMode == RocketLaunchMode.VanillaTransportPod ? "● " : "○ ") + "原版 TransportPod 发射（世界地格）";
            if (Widgets.ButtonText(vanillaRect, vanillaLabel))
                selectedMode = RocketLaunchMode.VanillaTransportPod;
            y += 36f;

            Rect pflRect = new Rect(inRect.x + 16f, y, inRect.width - 16f, 30f);
            string pflLabel = (selectedMode == RocketLaunchMode.PflOrbit ? "● " : "○ ") + "PFL 入轨发射（生成轨道实体）";
            if (Widgets.ButtonText(pflRect, pflLabel))
                selectedMode = RocketLaunchMode.PflOrbit;
            y += 40f;

            // ── 条件检查 ──
            List<string> reasons = RocketLaunchManager.GetLaunchBlockReasons(context, selectedMode);
            if (reasons.Count == 0)
            {
                GUI.color = new Color(0.6f, 1f, 0.6f);
                Widgets.Label(new Rect(inRect.x, y, inRect.width, lineHeight), "当前条件满足，可以发射。");
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = new Color(1f, 0.6f, 0.6f);
                foreach (string reason in reasons)
                {
                    Widgets.Label(new Rect(inRect.x, y, inRect.width, lineHeight), "• " + reason);
                    y += lineHeight;
                }
                GUI.color = Color.white;
            }

            y += 12f;

            // ── 按钮 ──
            float buttonY = inRect.yMax - 36f;
            Rect cancelRect = new Rect(inRect.xMax - 180f, buttonY, 80f, 30f);
            Rect confirmRect = new Rect(inRect.xMax - 90f, buttonY, 80f, 30f);

            if (Widgets.ButtonText(cancelRect, "取消"))
                Close();

            bool canConfirm = reasons.Count == 0;
            if (Widgets.ButtonText(confirmRect, "发射", true, true, canConfirm))
            {
                context.PadComp?.SetLaunchMode(selectedMode);

                if (selectedMode == RocketLaunchMode.VanillaTransportPod)
                {
                    if (RocketLaunchManager.TryLaunch(rocket, selectedMode, out string error))
                        Close();
                    else
                        Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    if (RocketLaunchManager.TryLaunch(rocket, selectedMode, out string error))
                        Close();
                    else
                        Messages.Message(error, MessageTypeDefOf.RejectInput, false);
                }
            }
        }
    }
}

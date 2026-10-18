using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PioneerFramework.Combat;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private enum PFViewUiInstrument { Navball, Radar }
        private readonly PFViewUiOperationContext operationContext = new();
        private PFViewPanelMode navballMode;
        private PFViewPanelMode radarMode;
        private PFViewUiVisibility instrumentVisibility;
        private Game? uiGame;

        private void ResetUiSession()
        {
            ResetCameraDrag();
            objectSearchFocused = false;
            navballMode = PFViewPanelMode.Automatic;
            radarMode = PFViewPanelMode.Automatic;
            operationContext.Reset();
            objectsDirty = true;
        }

        private void ClearInspection()
        {
            selectedGroupId = -1;
            inspectedContactKey = null;
            preferObjectList = true;
        }

        private void InvalidateInspection()
        {
            ClearInspection();
            QueueToast("PFV_InspectionLost".Translate());
        }

        private void ResolveUiContext()
        {
            if (!ReferenceEquals(uiGame, Current.Game))
            {
                if (uiGame != null)
                {
                    ClearInspection();
                    viewHistory.Clear();
                    RestoreOrInitCamera();
                    contactDisplayIds.Clear();
                    visibleContacts.Clear();
                    nextContactDisplayId = 0;
                }
                ResetUiSession();
                uiGame = Current.Game;
            }
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            if (SteeringController.IsOpen)
            {
                var steering = space?.GetGroupById(SteeringController.GroupId);
                if (!CanOperateGroup(steering, true, out string reason))
                {
                    int invalid = SteeringController.GroupId;
                    EndSteering();
                    operationContext.InvalidateSource(invalid);
                    QueueToast(reason);
                }
            }
            if (operationContext.WeaponsOpen)
            {
                int sourceId = operationContext.ResolveSource(SteeringController.GroupId);
                if (!CanOperateGroup(space?.GetGroupById(sourceId), false, out _))
                {
                    operationContext.CloseWeapons();
                    QueueToast("PFV_SourceLost".Translate());
                }
            }
            if (selectedGroupId >= 0 && !CanInspectEntity(space?.GetGroupById(selectedGroupId)))
                InvalidateInspection();
            if (targetGroupId >= 0 && !TryGetKnownContact(targetGroupId, out _))
            {
                targetGroupId = -1;
                QueueToast("PFV_TargetLost".Translate());
            }
            instrumentVisibility = PFViewUiPolicy.Resolve(SteeringController.IsOpen,
                operationContext.WeaponsOpen, navballMode, radarMode);
        }

        private bool CanOperateGroup([NotNullWhen(true)] SpaceEntityGroup? group, bool requireSteeringConsole, out string reason)
        {
            reason = "PFV_NotOperable".Translate();
            var player = Faction.OfPlayer;
            if (group == null || !group.IsActive || player == null || group.FactionId != player.loadID) return false;
            if (requireSteeringConsole && !CompSteeringConsole.HasConsole(group.GetLinkedMapComp()?.map))
            {
                reason = "PFV_NoConsole".Translate();
                return false;
            }
            reason = "";
            return true;
        }

        private SpaceEntityGroup? ResolveOperationGroup()
        {
            int id = operationContext.ResolveSource(SteeringController.GroupId);
            var group = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(id);
            return CanOperateGroup(group, false, out _) ? group : null;
        }

        private bool TryGetKnownContact(int groupId, out Contact known)
        {
            known = default;
            var group = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(groupId);
            var player = Faction.OfPlayer;
            if (group == null || !group.IsActive || player == null) return false;
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sensor != null) return sensor.TryGetKnownState(player.loadID, group, out known);
            // 缺少感知组件时只允许我方遥测，不能恢复隐藏敌方真值。
            if (group.FactionId != player.loadID) return false;
            known = new Contact { targetGroupId = groupId, decoyId = -1,
                position = GetGroupRelPos(group), velocity = group.CurrentOrbit.velocitySystem.v };
            return true;
        }

        private bool CanInspectEntity([NotNullWhen(true)] SpaceEntityGroup? group)
            => group != null && PFViewUiPolicy.CanShowEntity(group.IsActive,
                Faction.OfPlayer?.loadID == group.FactionId, group.IsStealth,
                TryGetKnownContact(group.GroupId, out _));

        private void TryOpenWeaponsFor(int groupId)
        {
            var group = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(groupId);
            bool allowed = CanOperateGroup(group, false, out string reason);
            if (!operationContext.TryOpenWeapons(groupId, allowed, SteeringController.GroupId))
                QueueToast(allowed ? "PFV_KeepOperator".Translate() : reason);
            else preferObjectList = false;
        }

        private void CloseWeaponsPanel() => operationContext.CloseWeapons();

        private void ShowInstrumentModeMenu(PFViewUiInstrument instrument)
        {
            var options = new List<FloatMenuOption>();
            foreach (PFViewPanelMode mode in System.Enum.GetValues<PFViewPanelMode>())
            {
                PFViewPanelMode captured = mode;
                bool selected = (instrument == PFViewUiInstrument.Navball ? navballMode : radarMode) == mode;
                options.Add(new((selected ? "✓ " : "") + ("PFV_Panel_" + mode).Translate(), () =>
                {
                    if (instrument == PFViewUiInstrument.Navball) navballMode = captured;
                    else radarMode = captured;
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void OpenInstrumentWindow(PFViewUiInstrument instrument)
            => Find.WindowStack.Add(new Window_PFViewInstrument(this, instrument == PFViewUiInstrument.Radar));

        internal void DrawDetachedInstrument(bool radar, Rect rect)
        {
            Rect saved = radar ? rRadar : rNavball;
            try
            {
                if (radar) { rRadar = rect; DrawRadarPanel(ResolveOperationGroup()); }
                else { rNavball = rect; DrawNavballPanel(ResolveOperationGroup()); }
            }
            finally
            {
                if (radar) rRadar = saved;
                else rNavball = saved;
            }
        }

        private void DrawSteeringStrip()
        {
            if (rSteeringStrip.width <= 0) return;
            Widgets.DrawBoxSolid(rSteeringStrip, PFViewPalette.PanelBg);
            Widgets.Label(new(rSteeringStrip.x + 6, rSteeringStrip.y + 4, rSteeringStrip.width - 104, 24),
                "PFV_Operating".Translate("G" + SteeringController.GroupId));
            if (Widgets.ButtonText(new(rSteeringStrip.xMax - 94, rSteeringStrip.y + 2, 90, 27), "PFV_EndSteering".Translate()))
                EndSteering();
        }

        private void EndSteering()
        {
            var window = FindSteeringWindow();
            if (window != null) window.Close();
            else Window_Steering.StopControl();
        }

        private bool TryFireAtTarget(int sourceGroupId, WeaponInstance weapon)
        {
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            var combat = Current.Game?.GetComponent<CombatManager>();
            var source = space?.GetGroupById(sourceGroupId);
            bool operable = operationContext.WeaponsOpen && ResolveOperationGroup()?.GroupId == sourceGroupId
                && CanOperateGroup(source, false, out _);
            bool knownTarget = TryGetKnownContact(targetGroupId, out Contact target);
            bool ready = combat != null && weapon != null && weapon.CanFire()
                && combat.GetGroupWeapons(sourceGroupId).Contains(weapon);
            if (!PFViewUiOperationContext.CanFire(sourceGroupId, targetGroupId, operable, knownTarget, ready))
            {
                QueueToast("PFV_CannotFire".Translate());
                return false;
            }
            // SensorManager 的已知状态与 CombatManager 使用同一系统坐标契约；影子期不读取真值位置。
            combat!.FireWeapon(sourceGroupId, weapon!, target.position, targetVelocity: target.velocity,
                targetGroupId: targetGroupId);
            return true;
        }
    }
}

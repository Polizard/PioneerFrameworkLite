using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PioneerFramework.Combat;
using RimWorld;
using UnityEngine;
using Verse;
using PioneerFramework.SpaceAI;

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
            navigableGroupMaps.Clear();
            fleetSnapshots.Clear();
            fleetAlertsOnly = false;
            showFleetMembers = false;
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
            if (weaponTarget.IsSet && !TryGetWeaponTarget(out _))
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

        /// <summary>
        /// **仪表基准**（2026-10-18 用户裁定③）：雷达 / 姿态球**总是显示当前选中的编队**（查看对象），
        /// 不再跟随"操作编队"——操作编队只决定命令来源。这样只要左侧选中了编队，两块仪表就有内容；
        /// 没有选中编队时返回 null（面板画空态）。
        /// </summary>
        private SpaceEntityGroup? ResolveInstrumentGroup()
            => selectedGroupId >= 0
                ? Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(selectedGroupId)
                : null;

        private SpaceEntityGroup? ResolveNavballGroup()
            => SteeringController.IsOpen
                ? Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(SteeringController.GroupId)
                : ResolveInstrumentGroup();

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

        private bool TryGetWeaponTarget(out Contact contact)
        {
            var sensor = Current.Game?.GetComponent<SensorManager>();
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            int faction = Faction.OfPlayer?.loadID ?? -1;
            return ContactTargetService.TryResolve(sensor, space, faction,
                new ContactTarget(weaponTarget.GroupId, weaponTarget.DecoyId),
                TimeScaleManager.GetPhysicsTickSafe(), out contact) && !contact.isDecoy;
        }

        private string ContactDisplayLabel(in Contact contact)
        {
            if (contactDisplayIds.TryGetValue(ContactKey(contact), out string? id))
                return "PFV_Contact".Translate(id);
            var group = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(contact.targetGroupId);
            return CanInspectEntity(group) ? "PFV_Group".Translate("G" + contact.targetGroupId)
                : "PFV_Contact".Translate("?");
        }

        private string WeaponTargetLabel => TryGetWeaponTarget(out var contact) ? ContactDisplayLabel(contact) : "—";

        private void SetTargetContact(in Contact contact)
        {
            var candidate = new PFViewUiTarget(contact.targetGroupId, contact.decoyId);
            var sensor = Current.Game?.GetComponent<SensorManager>();
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            bool known = ContactTargetService.TryResolve(sensor, space, Faction.OfPlayer?.loadID ?? -1,
                new ContactTarget(candidate.GroupId, candidate.DecoyId), TimeScaleManager.GetPhysicsTickSafe(), out var current)
                && !current.isDecoy;
            int source = ResolveOperationGroup()?.GroupId ?? -1;
            if (!known || !candidate.IsSet || (source >= 0 && candidate.GroupId == source))
            { QueueToast("PFV_CannotFire".Translate()); return; }
            weaponTarget = candidate;
            QueueToast("PFV_TargetSelected".Translate(ContactDisplayLabel(current)));
        }

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
                if (radar) { rRadar = rect; DrawRadarPanel(ResolveInstrumentGroup()); }
                else { rNavball = rect; DrawNavballPanel(ResolveNavballGroup()); }
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
            if (SteeringController.IsOpen) Window_Steering.StopControl();
        }

        private bool TryFireAtTarget(int sourceGroupId, WeaponInstance weapon)
        {
            var space = Current.Game?.GetComponent<SpaceGameComp>();
            var combat = Current.Game?.GetComponent<CombatManager>();
            var source = space?.GetGroupById(sourceGroupId);
            bool operable = operationContext.WeaponsOpen && ResolveOperationGroup()?.GroupId == sourceGroupId
                && CanOperateGroup(source, false, out _);
            bool knownTarget = TryGetWeaponTarget(out _);
            bool ready = combat != null && weapon != null && weapon.CanFire()
                && combat.GetGroupWeapons(sourceGroupId).Contains(weapon);
            if (!PFViewUiOperationContext.CanFire(sourceGroupId, weaponTarget, operable, knownTarget, ready))
            {
                QueueToast("PFV_CannotFire".Translate());
                return false;
            }
            // 使用真实设备的炮口与共同接触开火入口，继续遵守影子、假接触、弹药和射程限制。
            if (combat!.TryGetMapWeaponMount(sourceGroupId, weapon!, out var entity, out var building)
                && ContactTargetService.TryFire(space!, Current.Game.GetComponent<SensorManager>(), combat,
                    source!, entity!, building!, weapon!, new ContactTarget(weaponTarget.GroupId, weaponTarget.DecoyId),
                    building!.TryGetComp<CompMissileLauncher>()?.EffectiveVolleyCount() ?? 1)) return true;
            QueueToast("PFV_CannotFire".Translate());
            return false;
        }
    }
}

using System.Collections.Generic;
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
            presentedBodySpheres.Clear();
            navigableGroupMaps.Clear();
            fleetSnapshots.Clear();
            fleetAlertsOnly = false;
            showFleetMembers = false;
            objectSearchFocused = false;
            navballMode = PFViewPanelMode.Automatic;
            radarMode = PFViewPanelMode.Expanded;
            drawerTab = PFViewDrawerTab.None;
            preferObjectList = true;
            ResetWeaponSelection();
            operationContext.Reset();
            objectsDirty = true;
        }

        private void ClearInspection()
        {
            ClearTilePick();
            ClearInspectedSurfaceTile();
            selectedGroupId = -1;
            inspectedContactKey = null;
            preferObjectList = true;
            if (drawerTab == PFViewDrawerTab.Details) drawerTab = PFViewDrawerTab.None;
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
            if (SteeringController.IsOpen)
            {
                if (!CanOperateGroup(SteeringController.GroupId, true, out string reason))
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
                if (!CanOperateGroup(sourceId, false, out _))
                {
                    operationContext.CloseWeapons();
                    QueueToast("PFV_SourceLost".Translate());
                }
            }
            if (selectedGroupId >= 0 && !CanInspectGroup(selectedGroupId))
                InvalidateInspection();
            SynchronizeCockpitSource();
            if (weaponTarget.IsSet && !TryGetWeaponTarget(out _))
            {
                targetGroupId = -1;
                QueueToast("PFV_TargetLost".Translate());
            }
            instrumentVisibility = PFViewUiPolicy.Resolve(SteeringController.IsOpen,
                operationContext.WeaponsOpen, navballMode, radarMode);
        }

        private bool CanOperateGroup(int groupId, bool requireSteeringConsole, out string reason)
        {
            reason = "PFV_NotOperable".Translate();
            var player = Faction.OfPlayer;
            if (groupId < 0 || player == null || !PhysicsApi.TryGetSpacecraft(groupId, out var snap)
                || !snap.IsActive || snap.FactionId != player.loadID) return false;
            // 控制核心（2026-10-26）：装过但已失去 ⇒ 不可操作（复用通用不可操作文案，暂不新增翻译键）
            if (!snap.IsControllable) return false;
            if (requireSteeringConsole
                && (!PhysicsQueries.TryGetLinkedMap(groupId, out var map) || !CompSteeringConsole.HasConsole(map)))
            {
                reason = "PFV_NoConsole".Translate();
                return false;
            }
            reason = "";
            return true;
        }

        private int ResolveOperationGroupId()
        {
            int id = operationContext.ResolveSource(SteeringController.GroupId);
            return CanOperateGroup(id, false, out _) ? id : -1;
        }

        private SpaceEntityGroup? ResolveCockpitGroup()
        {
            var source = ResolveOperationGroup();
            if (source != null) return source;
            var group = Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(selectedGroupId);
            return group?.IsActive == true && Faction.OfPlayer?.loadID == group.FactionId ? group : null;
        }

        private SpaceEntityGroup? ResolveOperationGroup()
            => Current.Game?.GetComponent<SpaceGameComp>()?.GetGroupById(ResolveOperationGroupId());

        private int ResolveInstrumentGroup() => ResolveCockpitGroup()?.GroupId ?? -1;

        private int ResolveNavballGroupId()
            => SteeringController.IsOpen ? SteeringController.GroupId : selectedGroupId;


        private bool TryGetKnownContact(int groupId, out Contact known)
        {
            known = default;
            var player = Faction.OfPlayer;
            if (groupId < 0 || player == null || !PhysicsApi.TryGetSpacecraft(groupId, out var snap) || !snap.IsActive)
                return false;
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sensor != null) return sensor.TryGetKnownState(player.loadID, groupId, out known);
            // 缺少感知组件时只允许我方遥测，不能恢复隐藏敌方真值。
            if (snap.FactionId != player.loadID) return false;
            PhysicsQueries.TryGetGroupExactPosition(groupId, TimeScaleManager.GetPhysicsTickSafe(), out var position);
            known = new Contact { targetGroupId = groupId, decoyId = -1,
                position = position, velocity = snap.VelocitySystem };
            return true;
        }

        /// <summary>按 id 判“能不能看”（2026-11-01 批 5b：走快照，不碰引擎对象）。</summary>
        private bool CanInspectGroup(int groupId)
            => groupId >= 0 && PhysicsApi.TryGetSpacecraft(groupId, out var snap)
               && PFViewUiPolicy.CanShowEntity(snap.IsActive,
                   Faction.OfPlayer?.loadID == snap.FactionId, snap.IsStealth,
                   TryGetKnownContact(groupId, out _));

        private bool TryGetWeaponTarget(out Contact contact)
        {
            var sensor = Current.Game?.GetComponent<SensorManager>();
            int faction = Faction.OfPlayer?.loadID ?? -1;
            return ContactTargetService.TryResolve(sensor, faction,
                new ContactTarget(weaponTarget.GroupId, weaponTarget.DecoyId),
                TimeScaleManager.GetPhysicsTickSafe(), out contact) && !contact.isDecoy;
        }

        private string ContactDisplayLabel(in Contact contact)
        {
            if (contactDisplayIds.TryGetValue(ContactKey(contact), out string? id))
                return "PFV_Contact".Translate(id);
            return CanInspectGroup(contact.targetGroupId) ? "PFV_Group".Translate("G" + contact.targetGroupId)
                : "PFV_Contact".Translate("?");
        }

        private string WeaponTargetLabel => TryGetWeaponTarget(out var contact) ? ContactDisplayLabel(contact) : "—";

        private void SetTargetContact(in Contact contact)
        {
            var candidate = new PFViewUiTarget(contact.targetGroupId, contact.decoyId);
            var sensor = Current.Game?.GetComponent<SensorManager>();
            bool known = ContactTargetService.TryResolve(sensor, Faction.OfPlayer?.loadID ?? -1,
                new ContactTarget(candidate.GroupId, candidate.DecoyId), TimeScaleManager.GetPhysicsTickSafe(), out var current)
                && !current.isDecoy;
            int source = ResolveOperationGroupId();
            if (!known || !candidate.IsSet || (source >= 0 && candidate.GroupId == source))
            { QueueToast("PFV_CannotFire".Translate()); return; }
            weaponTarget = candidate;
            QueueToast("PFV_TargetSelected".Translate(ContactDisplayLabel(current)));
        }

        private void TryOpenWeaponsFor(int groupId)
        {
            bool allowed = CanOperateGroup(groupId, false, out string reason);
            if (!operationContext.TryOpenWeapons(groupId, allowed, SteeringController.GroupId))
                QueueToast(allowed ? "PFV_KeepOperator".Translate() : reason);
            else
            {
                SynchronizeCockpitSource();
                SetDrawer(PFViewDrawerTab.Weapons);
            }
        }

        private void CloseWeaponsPanel()
        {
            operationContext.CloseWeapons();
            ResetWeaponSelection();
            SynchronizeCockpitSource();
        }

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
                else { rNavball = rect; DrawNavballPanel(ResolveNavballGroupId()); }
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
            SynchronizeCockpitSource();
        }

        private bool TryFireAtTarget(int sourceGroupId, WeaponInstance weapon, bool reportFailure = true)
        {
            var combat = Current.Game?.GetComponent<CombatManager>();
            bool operable = operationContext.WeaponsOpen && ResolveOperationGroupId() == sourceGroupId
                && CanOperateGroup(sourceGroupId, false, out _);
            bool knownTarget = TryGetWeaponTarget(out _);
            bool ready = combat != null && weapon?.def != null
                && combat.GetGroupWeapons(sourceGroupId).Contains(weapon) && weapon.CanFire();
            if (!PFViewUiOperationContext.CanFire(sourceGroupId, weaponTarget, operable, knownTarget, ready))
            {
                if (reportFailure) QueueToast("PFV_CannotFire".Translate());
                return false;
            }
            // 使用真实设备的炮口与共同接触开火入口，继续遵守影子、假接触、弹药和射程限制。
            var sensor = Current.Game?.GetComponent<SensorManager>();
            if (sensor != null && combat != null
                && ContactTargetService.TryFire(sensor, combat, sourceGroupId, weapon!,
                    new ContactTarget(weaponTarget.GroupId, weaponTarget.DecoyId))) return true;
            if (reportFailure) QueueToast("PFV_CannotFire".Translate());
            return false;
        }
    }
}

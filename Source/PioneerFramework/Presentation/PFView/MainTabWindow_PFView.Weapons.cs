using System;
using System.Collections.Generic;
using System.Globalization;
using PioneerFramework.Combat;
using PioneerFramework.SpaceAI;
using UnityEngine;
using Verse;

namespace PioneerFramework
{
    public partial class MainTabWindow_PFView
    {
        private readonly HashSet<WeaponInstance> selectedCockpitWeapons = new();
        private int cockpitWeaponGroupId = -1;
        private Vector2 cockpitWeaponScroll;

        private void ResetWeaponSelection()
        {
            selectedCockpitWeapons.Clear();
            cockpitWeaponGroupId = -1;
            cockpitWeaponScroll = Vector2.zero;
            weaponTarget = PFViewUiTarget.None;
        }

        private void SynchronizeCockpitSource()
        {
            int steering = SteeringController.IsOpen ? SteeringController.GroupId : -1;
            if (operationContext.ObserveSourceChange(steering)) ResetWeaponSelection();
            int source = operationContext.ResolveSource(steering);
            if (source >= 0 && weaponTarget.GroupId == source) weaponTarget = PFViewUiTarget.None;
            if (selectedCockpitWeapons.Count > 0)
            {
                var combat = Current.Game?.GetComponent<CombatManager>();
                selectedCockpitWeapons.RemoveWhere(weapon =>
                    combat?.TryGetMapWeaponMount(cockpitWeaponGroupId, weapon, out _, out _) != true);
            }
        }

        private string? CockpitTargetBlockReason(SpaceEntityGroup? source, out Contact contact)
        {
            contact = default;
            if (!operationContext.WeaponsOpen) return "PFV_CockpitFireControlOff";
            if (source == null || ResolveOperationGroup()?.GroupId != source.GroupId) return "PFV_NotOperable";
            if (!TryGetWeaponTarget(out contact) || contact.targetGroupId == source.GroupId)
                return "PFV_CockpitNoTarget";
            return null;
        }

        private void RequestCockpitSalvo(Game? game, int sourceId, PFViewUiTarget target, bool fireAll)
        {
            if (game == null || !ReferenceEquals(Current.Game, game) || weaponTarget != target)
            {
                QueueToast("PFV_CannotFire".Translate());
                return;
            }
            var source = ResolveOperationGroup();
            var combat = game.GetComponent<CombatManager>();
            if (source?.GroupId != sourceId || combat == null || CockpitTargetBlockReason(source, out _) != null)
            {
                QueueToast("PFV_CannotFire".Translate());
                return;
            }

            var (fired, attempted) = PFViewWeaponSalvo.Fire(combat.GetGroupWeapons(sourceId),
                fireAll ? null : selectedCockpitWeapons, weapon => ReferenceEquals(Current.Game, game)
                    && weaponTarget == target && TryFireAtTarget(sourceId, weapon, reportFailure: false));
            if (attempted == 0) QueueToast("PFV_CannotFire".Translate());
            else QueueToast("PFV_CockpitSalvoResult".Translate(fired, attempted - fired));
        }

        private void DrawCockpitWeapons(Rect rect, SpaceEntityGroup? cockpit)
        {
            if (rect.width <= 0 || rect.height < 72f) return;
            Widgets.DrawBoxSolid(rect, PFViewPalette.PanelBg);
            UiKit.SpacecraftUIDecor.DrawCornerLBrackets(rect, PFViewPalette.Border);
            var header = new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 25);
            string title = "PFV_CockpitFireControl".Translate(CockpitGroupLabel(cockpit), WeaponTargetLabel);
            MiniLabel(header, title, PFViewPalette.TextPrimary);
            TooltipHandler.TipRegion(header, title);

            int id = cockpit?.GroupId ?? -1;
            if (cockpitWeaponGroupId != id)
            {
                cockpitWeaponGroupId = id;
                selectedCockpitWeapons.Clear();
                cockpitWeaponScroll = Vector2.zero;
            }
            var game = Current.Game;
            var combat = game?.GetComponent<CombatManager>();
            IReadOnlyList<WeaponInstance> weapons = cockpit == null || combat == null
                ? Array.Empty<WeaponInstance>() : combat.GetGroupWeapons(id);

            var controls = new Rect(rect.x + 8, rect.y + 33, 80, rect.height - 41);
            var list = new Rect(controls.xMax + 8, controls.y, rect.xMax - controls.xMax - 16, controls.height);
            DrawCockpitWeaponActions(controls, game, cockpit, combat, weapons);
            if (weapons.Count == 0)
            {
                string key = cockpit == null ? "PFV_NoSelection"
                    : fleetSnapshots.TryGetValue(id, out var fleet) && fleet.UnloadedMaps
                        ? "PFV_CockpitWeaponMount" : "PFV_CockpitNoWeapons";
                Widgets.Label(new Rect(list.x, list.y, list.width, Mathf.Min(52, list.height)), key.Translate());
                return;
            }

            const float rowHeight = 42;
            float contentHeight = weapons.Count * rowHeight;
            var content = new Rect(0, 0, list.width - (contentHeight > list.height ? 18 : 0),
                Mathf.Max(contentHeight, list.height));
            Widgets.BeginScrollView(list, ref cockpitWeaponScroll, content);
            for (int i = 0; i < weapons.Count; i++)
            {
                float y = i * rowHeight;
                if (y + rowHeight < cockpitWeaponScroll.y || y > cockpitWeaponScroll.y + list.height) continue;
                var weapon = weapons[i];
                var row = new Rect(0, y, content.width, rowHeight - 2);
                bool selected = selectedCockpitWeapons.Contains(weapon);
                if (selected || Mouse.IsOver(row))
                    Widgets.DrawBoxSolid(row, selected ? PFViewPalette.PanelBgRaised : PFViewPalette.PanelBgHover);
                var check = new Rect(4, y + 4, 14, 14);
                Widgets.DrawBox(check);
                if (selected) MiniLabel(check, "✓", PFViewPalette.TextPrimary, TextAnchor.MiddleCenter);
                string name = weapon.def?.LabelCap.ToString() ?? "—";
                string ammo = weapon.ammo < 0 ? "∞" : weapon.ammo.ToString(CultureInfo.InvariantCulture);
                string status = PFViewWeaponSalvo.WeaponState(weapon).Translate();
                string icon = weapon.def?.weaponType switch
                {
                    WeaponType.Projectile => "●",
                    WeaponType.Ray => "╱",
                    WeaponType.Missile => "▲",
                    WeaponType.Drone => "◆",
                    _ => "?"
                };
                MiniLabel(new(22, y + 2, 16, 19), icon, PFViewPalette.TextPrimary);
                MiniLabel(new(41, y + 2, row.width - 46, 19), name, PFViewPalette.TextPrimary);
                string ammoStatus = "PFV_CockpitAmmo".Translate(ammo) + " · " + status;
                MiniLabel(new(22, y + 20, row.width - 27, 19), ammoStatus, PFViewPalette.TextSecondary);
                if (Mouse.IsOver(row))
                {
                    string range = weapon.def == null ? "—"
                        : PFViewUiObjectListCore.FormatDistance(ContactTargetService.EffectiveRange(weapon));
                    TooltipHandler.TipRegion(row, name + "\n" + ammoStatus + "\n" + "PFV_EngagementRange".Translate(range)
                        + "\n" + "PFV_CockpitWeaponMultiSelect".Translate());
                }
                bool toggle = Event.current.control || check.Contains(Event.current.mousePosition);
                if (Widgets.ButtonInvisible(row))
                {
                    if (!toggle) selectedCockpitWeapons.Clear();
                    if (!toggle || !selectedCockpitWeapons.Remove(weapon)) selectedCockpitWeapons.Add(weapon);
                }
            }
            Widgets.EndScrollView();
        }

        private void DrawCockpitWeaponActions(Rect rect, Game? game, SpaceEntityGroup? cockpit, CombatManager? combat,
            IReadOnlyList<WeaponInstance> weapons)
        {
            bool active = operationContext.WeaponsOpen;
            var single = selectedCockpitWeapons.GetEnumerator();
            var weapon = selectedCockpitWeapons.Count == 1 && single.MoveNext() ? single.Current : null;
            int id = cockpit?.GroupId ?? -1;
            var target = weaponTarget;
            bool canEnable = CanOperateGroup(cockpit, false, out string reason);
            string? targetBlocked = CockpitTargetBlockReason(cockpit, out var contact);
            string? selectedBlocked = targetBlocked ?? PFViewWeaponSalvo.BlockReason(cockpit!, combat, weapons, contact, selectedCockpitWeapons);
            string? allBlocked = targetBlocked ?? PFViewWeaponSalvo.BlockReason(cockpit!, combat, weapons, contact, null);
            var inspected = game?.GetComponent<SpaceGameComp>()?.GetGroupById(selectedGroupId);
            int alternate = active && !SteeringController.IsOpen && CanOperateGroup(inspected, false, out _)
                && inspected.GroupId != id ? inspected.GroupId : -1;
            string toggle = (alternate >= 0 ? "PFV_CockpitFireSource" : active
                ? "PFV_CockpitEndFireControl" : "PFV_CockpitEnableFireControl").Translate();
            string shot = "PFV_CockpitSelectShot".Translate();
            string clear = "PFV_ClearTarget".Translate();
            string fireSelected = "PFV_CockpitFireSelected".Translate();
            string fireAll = "PFV_CockpitFireAll".Translate();
            bool canSelectShot = active && weapon?.def?.UsesShotComponents == true && canEnable;
            if (rect.height < 156f)
            {
                if (rect.height >= 28f && CockpitButton(new(rect.x, rect.y, rect.width, 28), "PFV_CockpitActions".Translate()))
                    Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                    {
                        new(toggle, active || canEnable ? () => RequestFireControl(game, id, alternate, active) : null),
                        new(clear, active && target.IsSet ? () => SetTargetGroup(-1) : null),
                        new(shot, canSelectShot ? () => OpenCockpitShotMenu(id, weapon!) : null),
                        new(selectedBlocked == null ? fireSelected : fireSelected + " · " + selectedBlocked.Translate(),
                            selectedBlocked == null ? () => RequestCockpitSalvo(game, id, target, fireAll: false) : null),
                        new(allBlocked == null ? fireAll : fireAll + " · " + allBlocked.Translate(),
                            allBlocked == null ? () => RequestCockpitSalvo(game, id, target, fireAll: true) : null)
                    }));
                return;
            }
            Text.Font = GameFont.Tiny;
            if (CockpitButton(new(rect.x, rect.y, rect.width, 28), toggle, active || canEnable, reason))
                RequestFireControl(game, id, alternate, active);
            if (CockpitButton(new(rect.x, rect.y + 32, rect.width, 28), "PFV_CockpitClearShort".Translate(),
                active && target.IsSet, clear)) SetTargetGroup(-1);
            if (CockpitButton(new(rect.x, rect.y + 64, rect.width, 28), shot, canSelectShot,
                canSelectShot ? ShotSelectionUI.Label(weapon!) : "PFV_CockpitSingleShotTip".Translate().ToString()))
                OpenCockpitShotMenu(id, weapon!);
            if (CockpitButton(new(rect.x, rect.y + 96, rect.width, 28), fireSelected, selectedBlocked == null,
                selectedBlocked == null ? "PFV_CockpitFireSelectedTip".Translate(selectedCockpitWeapons.Count).ToString()
                    : selectedBlocked.Translate().ToString()))
                RequestCockpitSalvo(game, id, target, fireAll: false);
            if (CockpitButton(new(rect.x, rect.y + 128, rect.width, 28), fireAll, allBlocked == null,
                (allBlocked ?? "PFV_CockpitFireAllTip").Translate()))
                RequestCockpitSalvo(game, id, target, fireAll: true);
            Text.Font = GameFont.Small;
        }

        private void RequestFireControl(Game? game, int id, int alternate, bool wasActive)
        {
            if (!ReferenceEquals(game, Current.Game) || operationContext.WeaponsOpen != wasActive) return;
            if (!wasActive)
            {
                TryOpenWeaponsFor(id);
                return;
            }
            if (ResolveOperationGroup()?.GroupId != id) return;
            if (alternate < 0)
            {
                CloseWeaponsPanel();
                return;
            }

            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new("PFV_CockpitEndFireControl".Translate(), () =>
                {
                    if (ReferenceEquals(game, Current.Game) && operationContext.WeaponsOpen
                        && ResolveOperationGroup()?.GroupId == id) CloseWeaponsPanel();
                }),
                new("PFV_ChangeOperator".Translate("G" + alternate), () =>
                {
                    if (ReferenceEquals(game, Current.Game) && operationContext.WeaponsOpen
                        && ResolveOperationGroup()?.GroupId == id) TryOpenWeaponsFor(alternate);
                })
            }));
        }

        private void OpenCockpitShotMenu(int id, WeaponInstance weapon)
        {
            var game = Current.Game;
            if (!CanSelectCockpitShot(game, id, weapon))
            {
                QueueToast("PFV_CockpitWeaponMount".Translate());
                return;
            }

            // Match ShotSelectionUI's options and host selection contract. Revalidate the mount
            // in the callback because it can leave the operating group while the menu is open.
            var options = new List<FloatMenuOption>();
            foreach (var reference in weapon.def.allowedShots)
            {
                var shot = reference?.Resolve();
                if (shot == null) continue;
                options.Add(new FloatMenuOption(shot.LabelCap, () =>
                {
                    if (!CanSelectCockpitShot(game, id, weapon))
                        QueueToast("PFV_CockpitWeaponMount".Translate());
                    else if (!weapon.TrySelectShot(shot))
                        QueueToast("PF_Shot_Invalid".Translate());
                }));
            }
            if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
        }

        private bool CanSelectCockpitShot(Game? game, int id, WeaponInstance weapon)
            => game != null && Current.Game == game && operationContext.WeaponsOpen
                && selectedCockpitWeapons.Count == 1 && selectedCockpitWeapons.Contains(weapon)
                && ResolveOperationGroup()?.GroupId == id
                && game.GetComponent<CombatManager>()?.TryGetMapWeaponMount(id, weapon, out _, out _) == true;
    }
}

using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PioneerFramework.Combat
{
    public static class ShotSelectionUI
    {
        public static string Label(WeaponInstance weapon) => weapon.TryGetSelectedShot(out var shot) && shot != null
            ? shot.LabelCap.ToString() : "PF_Shot_Invalid".Translate().ToString();

        public static Command_Action Command(WeaponInstance weapon) => new()
        {
            defaultLabel = "PF_Shot_Select".Translate(Label(weapon)),
            defaultDesc = "PF_Shot_SelectDesc".Translate(),
            icon = TexCommand.Attack,
            action = () => Open(weapon)
        };

        public static void Open(WeaponInstance weapon)
        {
            var options = new List<FloatMenuOption>();
            foreach (var reference in weapon.def.allowedShots)
            {
                var shot = reference?.Resolve();
                if (shot == null) continue;
                var captured = shot;
                options.Add(new FloatMenuOption(shot.LabelCap, () => weapon.TrySelectShot(captured)));
            }
            if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PioneerFramework.GroundAssembly;

public partial class Window_RocketAssemblerC
{
    private Vector2 materialDeliveryScroll;

    private void DrawAssemblyOrder(Rect rail)
    {
        Panel(rail);
        Rect inner = rail.ContractedBy(12f);
        Label(new Rect(inner.x, inner.y, inner.width, 29f), comp.AssemblyStatusText,
            GameFont.Small, comp.MaterialOrderError != null ? Red : Gold);
        Label(new Rect(inner.x, inner.y + 32f, inner.width, 22f),
            "PF_AssemblyProgress".Translate(comp.Progress01.ToString("P0"), comp.WorkDone.ToString("F0"),
                comp.WorkRequired.ToString("F0")), GameFont.Small, Dim);
        Label(new Rect(inner.x, inner.y + 62f, inner.width, 20f), "PF_AssemblyMaterials".Translate(),
            GameFont.Small, Color.white);

        bool canFinish = Prefs.DevMode && comp.IsOpenBuildOrder(comp.BuildOrderVersion) && comp.HasAssemblyMaterials;
        float actionsY = inner.yMax - (canFinish ? 100f : 70f);
        Rect materials = new(inner.x, inner.y + 88f, inner.width, Mathf.Max(0, actionsY - inner.y - 96f));
        if (materials.height > 0)
        {
            var rows = comp.MaterialRows().ToList();
            const float rowHeight = 39f;
            Rect content = WorkbenchScrollContent(materials, rows.Count * rowHeight);
            Widgets.BeginScrollView(materials, ref materialDeliveryScroll, content);
            for (int index = 0; index < rows.Count; index++)
            {
                AssemblyMaterialRow row = rows[index];
                string line = "PF_AssemblyDeliveryLine".Translate(row.Label, row.Delivered.ToString("N0"),
                    row.Required.ToString("N0"), row.Missing.ToString("N0"));
                Rect lineRect = new(2f, index * rowHeight, content.width - 4f, rowHeight - 3f);
                Label(lineRect, line, GameFont.Tiny, row.Missing > 0 ? Color.white : WorkbenchGood);
                TooltipHandler.TipRegion(lineRect, line);
            }
            Widgets.EndScrollView();
        }

        if (comp.IsRefundPending)
        {
            if (Btn(new Rect(inner.x, actionsY, inner.width, 30f), "PF_AssemblyRetryRefund".Translate()))
                comp.CancelBuild();
            return;
        }

        string pauseLabel = (comp.IsBuildPaused ? "PF_AssemblyResume" : "PF_AssemblyPause").Translate();
        if (Btn(new Rect(inner.x, actionsY, inner.width, 30f), pauseLabel))
        {
            if (comp.IsBuildPaused) comp.ResumeBuild();
            else comp.PauseBuild();
        }
        if (Btn(new Rect(inner.x, actionsY + 34f, inner.width, 30f), "PF_AssemblyCancel".Translate()))
            RocketAssemblyMaterialsUi.ConfirmCancellation(comp);
        if (canFinish && BtnLink(new Rect(inner.x, actionsY + 68f, inner.width, 24f), "PF_AssemblyCompleteNow".Translate()))
            comp.ForceComplete();
    }
}

internal static class RocketAssemblyMaterialsUi
{
    internal static string QuoteText(CompRocketAssembler comp, IReadOnlyList<ThingDefCountClass> materials)
    {
        var lines = materials.Select(row => (string)"PF_AssemblyCostLine".Translate(row.thingDef.LabelCap,
            row.count.ToString("N0"))).ToList();
        var defaults = comp.Design.parts.Select(part => part?.defName).OfType<string>().Where(name => name.Length != 0)
            .Distinct(StringComparer.Ordinal).Where(comp.UsesDefaultAssemblyCost)
            .Select(name => DefDatabase<ShipPartDef>.GetNamedSilentFail(name)?.label ?? name).ToList();
        if (defaults.Count != 0)
        {
            lines.Add(string.Empty);
            lines.Add("PF_AssemblyDefaultCost".Translate());
            lines.AddRange(defaults);
        }
        return string.Join("\n", lines);
    }

    internal static void ShowQuote(CompRocketAssembler comp)
    {
        string text = comp.TryQuoteAssemblyMaterials(out var materials, out string reason)
            ? QuoteText(comp, materials) : reason;
        Find.WindowStack.Add(new Window_RocketAssemblyMaterialQuote(text));
    }

    internal static void ConfirmCancellation(CompRocketAssembler comp)
    {
        int version = comp.BuildOrderVersion;
        var contents = comp.GetDirectlyHeldThings().GroupBy(thing => thing.def)
            .Select(group => (string)"PF_AssemblyCostLine".Translate(group.Key.LabelCap,
                group.Sum(thing => thing.stackCount).ToString("N0"))).ToList();
        string text = "PF_AssemblyCancelConfirm".Translate(comp.WorkDone.ToString("F0"));
        if (contents.Count > 0)
            text += "\n\n" + "PF_AssemblyReturnMaterials".Translate() + "\n" + string.Join("\n", contents);
        var dialog = Dialog_MessageBox.CreateConfirmation(text, () =>
        {
            if (comp.State == AssemblerState.Building && comp.BuildOrderVersion == version) comp.CancelBuild();
        }, true);
        dialog.forcePause = true;
        Find.WindowStack.Add(dialog);
    }
}

internal sealed class Window_RocketAssemblyMaterialQuote : Window
{
    private readonly string quote;
    private Vector2 scroll;

    public Window_RocketAssemblyMaterialQuote(string quote)
    {
        this.quote = quote;
        doCloseX = true;
        absorbInputAroundWindow = true;
    }

    public override Vector2 InitialSize => new(Mathf.Min(520f, UI.screenWidth), Mathf.Min(460f, UI.screenHeight));

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0, 0, inRect.width - 30f, 36f), "PF_AssemblyMaterials".Translate());
        Text.Font = GameFont.Small;
        var view = new Rect(0, 42f, inRect.width, inRect.height - 42f);
        float width = view.width - 18f;
        var content = new Rect(0, 0, width, Mathf.Max(view.height, Text.CalcHeight(quote, width)));
        Widgets.BeginScrollView(view, ref scroll, content);
        Widgets.Label(content, quote);
        Widgets.EndScrollView();
    }
}

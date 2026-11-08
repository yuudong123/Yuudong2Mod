using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod.SurgeryManager
{
    // 이식 계획 프리셋 편집 창. 왼쪽은 프리셋 목록, 오른쪽은 고른 프리셋의 부위별 항목이다.
    public class Dialog_SurgeryPresets : Window
    {
        private readonly MainTabWindow_SurgeryManager owner;
        private SurgeryPreset selected;
        private Vector2 listScroll;
        private Vector2 entryScroll;

        public Dialog_SurgeryPresets(MainTabWindow_SurgeryManager owner)
        {
            this.owner = owner;
            selected = Plans.presets.FirstOrDefault();
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(820f, 640f);

        private static GameComponent_SurgeryPlans Plans => GameComponent_SurgeryPlans.Instance;

        public override void PostClose()
        {
            base.PostClose();
            owner?.Invalidate();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "Yuudong2.Surgery.Editor.Title".Translate());
            Text.Font = GameFont.Small;
            Rect body = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f - CloseButSize.y - 10f);
            DoPresetList(new Rect(body.x, body.y, 210f, body.height));
            if (selected != null)
            {
                DoEntries(new Rect(body.x + 222f, body.y, body.width - 222f, body.height));
            }
        }

        private void DoPresetList(Rect rect)
        {
            float buttons = 30f * 3 + 12f;
            Rect outRect = new Rect(rect.x, rect.y, rect.width, rect.height - buttons);
            Rect view = new Rect(0f, 0f, rect.width - 16f, Plans.presets.Count * 30f);
            Widgets.BeginScrollView(outRect, ref listScroll, view);
            for (int i = 0; i < Plans.presets.Count; i++)
            {
                SurgeryPreset preset = Plans.presets[i];
                Rect row = new Rect(0f, i * 30f, view.width, 28f);
                if (preset == selected)
                {
                    Widgets.DrawHighlightSelected(row);
                }

                if (Widgets.ButtonText(row, preset.label, drawBackground: false))
                {
                    selected = preset;
                }
            }

            Widgets.EndScrollView();

            float y = outRect.yMax + 6f;
            if (Widgets.ButtonText(new Rect(rect.x, y, rect.width, 28f), "Yuudong2.Surgery.Editor.New".Translate()))
            {
                selected = new SurgeryPreset { id = Plans.NextId(), label = "Yuudong2.Surgery.Editor.NewName".Translate(Plans.presets.Count + 1) };
                Plans.presets.Add(selected);
            }

            y += 30f;
            if (Widgets.ButtonText(new Rect(rect.x, y, rect.width / 2f - 2f, 28f), "Yuudong2.Surgery.Editor.Copy".Translate()) && selected != null)
            {
                SurgeryPreset copy = new SurgeryPreset
                {
                    id = Plans.NextId(),
                    label = selected.label + " (2)",
                    entries = selected.entries.Select(e => new SurgeryPlanEntry(e.hediff, e.part)).ToList(),
                };
                Plans.presets.Add(copy);
                selected = copy;
            }

            if (Widgets.ButtonText(new Rect(rect.x + rect.width / 2f + 2f, y, rect.width / 2f - 2f, 28f), "Yuudong2.Surgery.Editor.Delete".Translate()) && selected != null && Plans.presets.Count > 1)
            {
                Plans.RemovePreset(selected);
                selected = Plans.presets.FirstOrDefault();
            }

            y += 30f;
            if (Widgets.ButtonText(new Rect(rect.x, y, rect.width, 28f), "Yuudong2.Surgery.Editor.AddDefault".Translate()))
            {
                selected = DefaultPresets.Create(Plans.NextId());
                Plans.presets.Add(selected);
            }
        }

        private void DoEntries(Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y + 4f, 50f, 24f), "Yuudong2.Surgery.Editor.Name".Translate());
            selected.label = Widgets.TextField(new Rect(rect.x + 54f, rect.y, rect.width - 54f, 28f), selected.label);

            Rect add = new Rect(rect.x, rect.y + 34f, 160f, 28f);
            if (Widgets.ButtonText(add, "Yuudong2.Surgery.Editor.AddEntry".Translate()))
            {
                OpenPartMenu();
            }

            List<SurgeryPlanEntry> entries = selected.entries
                .OrderBy(e => e.part.label)
                .ThenBy(e => e.hediff.label)
                .ToList();
            Rect outRect = new Rect(rect.x, add.yMax + 6f, rect.width, rect.yMax - add.yMax - 6f);
            Rect view = new Rect(0f, 0f, rect.width - 16f, Mathf.Max(entries.Count * 28f, 28f));
            Widgets.BeginScrollView(outRect, ref entryScroll, view);
            if (entries.Count == 0)
            {
                Widgets.Label(new Rect(0f, 0f, view.width, 28f), "Yuudong2.Surgery.Editor.Empty".Translate());
            }

            for (int i = 0; i < entries.Count; i++)
            {
                SurgeryPlanEntry entry = entries[i];
                Rect row = new Rect(0f, i * 28f, view.width, 26f);
                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(row);
                }

                ThingDef item = entry.hediff.spawnThingOnRemoved;
                if (item != null)
                {
                    Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 1f, 24f, 24f), item);
                }

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + 30f, row.y, 130f, row.height), entry.part.LabelCap);
                Widgets.Label(new Rect(row.x + 164f, row.y, row.width - 200f, row.height), entry.hediff.LabelCap);
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonImage(new Rect(row.xMax - 24f, row.y + 3f, 20f, 20f), TexButton.Delete))
                {
                    selected.entries.Remove(entry);
                }
            }

            Widgets.EndScrollView();
        }

        private void OpenPartMenu()
        {
            List<BodyPartDef> parts = SurgeryPlanner.InstallRecipes
                .SelectMany(r => r.appliedOnFixedBodyParts)
                .Distinct()
                .OrderBy(p => p.label)
                .ToList();
            List<FloatMenuOption> options = parts.Select(p => new FloatMenuOption(p.LabelCap, () => OpenItemMenu(p))).ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void OpenItemMenu(BodyPartDef part)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (HediffDef hediff in SurgeryPlanner.InstallRecipes
                .Where(r => r.appliedOnFixedBodyParts.Contains(part))
                .Select(r => r.addsHediff)
                .Distinct()
                .OrderBy(h => h.label))
            {
                if (selected.Contains(hediff, part))
                {
                    continue;
                }

                HediffDef chosen = hediff;
                ThingDef item = hediff.spawnThingOnRemoved;
                options.Add(new FloatMenuOption(hediff.LabelCap, () => AddEntry(chosen, part), item, (ThingStyleDef)null));
            }

            if (options.Count == 0)
            {
                options.Add(new FloatMenuOption("Yuudong2.Surgery.Editor.Empty".Translate(), null));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        // 같은 부위에서 서로 막는 임플란트(피부샘끼리, 외골격끼리 등)는 계획에 같이 넣지 않는다.
        private void AddEntry(HediffDef hediff, BodyPartDef part)
        {
            RecipeDef recipe = SurgeryPlanner.RecipesFor(hediff, part).FirstOrDefault();
            foreach (SurgeryPlanEntry other in selected.entries.Where(e => e.part == part))
            {
                if (recipe != null && !recipe.CompatibleWithHediff(other.hediff))
                {
                    Messages.Message("Yuudong2.Surgery.Editor.Incompatible".Translate(hediff.LabelCap, other.hediff.LabelCap), MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
            }

            selected.entries.Add(new SurgeryPlanEntry(hediff, part));
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Yuudong2Mod.SurgeryManager
{
    // 우하단 "수술 관리" 탭. 정착민마다 부위별로 달린 부품·임플란트와 계획 대비 상태를 한눈에 보고, 부족한 수술을 예약한다.
    public class MainTabWindow_SurgeryManager : MainTabWindow
    {
        private const float RowHeight = 34f;
        private const float HeaderHeight = 24f;
        private const float NameWidth = 130f;
        private const float PresetWidth = 120f;
        private const float ProgressWidth = 56f;
        private const float QueueWidth = 56f;
        private const float MinSlotWidth = 58f;
        private const int RefreshFrames = 30;

        private static readonly Color DoneColor = new Color(0.2f, 0.45f, 0.2f, 0.45f);
        private static readonly Color ProgressColor = new Color(0.65f, 0.4f, 0.1f, 0.45f);
        private static readonly Color QueuedColor = new Color(0.6f, 0.55f, 0.1f, 0.45f);
        private static readonly Color MissingColor = new Color(0.6f, 0.15f, 0.15f, 0.5f);
        private static readonly Color BlockedColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);

        private Vector2 scroll;
        private List<Slot> slots;
        private readonly Dictionary<Pawn, List<PlanStatus>> statusCache = new Dictionary<Pawn, List<PlanStatus>>();
        private int cacheFrame = -9999;

        private const float LineHeight = 20f;
        private const float TabBarHeight = 32f;

        private Vector2 healthScroll;
        private readonly Dictionary<Pawn, HealthSummary> healthCache = new Dictionary<Pawn, HealthSummary>();
        private readonly List<TabRecord> tabs = new List<TabRecord>();

        private class Slot
        {
            public string label;
            public HashSet<BodyPartDef> defs;
        }

        public override Vector2 RequestedTabSize => new Vector2(Mathf.Min(UI.screenWidth, 1600f), Mathf.Min(UI.screenHeight - 35f, 200f + RowHeight * Mathf.Max(4, Colonists().Count())));

        private static GameComponent_SurgeryPlans Plans => GameComponent_SurgeryPlans.Instance;

        private static IEnumerable<Pawn> Colonists()
        {
            return Find.CurrentMap?.mapPawns.FreeColonists ?? Enumerable.Empty<Pawn>();
        }

        public override void PreOpen()
        {
            base.PreOpen();
            Invalidate();
        }

        public void Invalidate()
        {
            cacheFrame = -9999;
        }

        private List<Slot> Slots
        {
            get
            {
                if (slots == null)
                {
                    slots = new List<Slot>();
                    AddSlot("Brain");
                    AddSlot("Eye");
                    AddSlot("Ear");
                    AddSlot("Nose");
                    AddSlot("Jaw", "Tongue");
                    AddSlot("Heart");
                    AddSlot("Lung");
                    AddSlot("Kidney");
                    AddSlot("Liver");
                    AddSlot("Stomach");
                    AddSlot("Ribcage", "Sternum");
                    AddSlot("Torso");
                    AddSlot("Spine", "Pelvis");
                    AddSlot("RK_BodyPart_Tail");
                    AddSlot("Arm", "Shoulder", "Clavicle", "Humerus", "Radius", "Hand", "Finger");
                    AddSlot("Leg", "Femur", "Tibia", "Foot", "Toe");
                }

                return slots;
            }
        }

        private void AddSlot(params string[] defNames)
        {
            List<BodyPartDef> defs = defNames.Select(n => DefDatabase<BodyPartDef>.GetNamedSilentFail(n)).Where(d => d != null).ToList();
            if (defs.Count > 0 && defs[0].defName == defNames[0])
            {
                slots.Add(new Slot { label = defs[0].LabelCap, defs = new HashSet<BodyPartDef>(defs) });
            }
        }

        private List<PlanStatus> StatusFor(Pawn pawn)
        {
            if (Time.frameCount - cacheFrame > RefreshFrames)
            {
                statusCache.Clear();
                healthCache.Clear();
                cacheFrame = Time.frameCount;
            }

            if (!statusCache.TryGetValue(pawn, out List<PlanStatus> list))
            {
                list = SurgeryPlanner.Evaluate(pawn, Plans.PresetFor(pawn));
                statusCache[pawn] = list;
            }

            return list;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Plans == null)
            {
                return;
            }

            Text.Font = GameFont.Small;
            Rect body = new Rect(inRect.x, inRect.y + TabBarHeight, inRect.width, inRect.height - TabBarHeight);
            tabs.Clear();
            tabs.Add(new TabRecord("Yuudong2.Surgery.Tab.Plans".Translate(), () => Plans.openTab = 0, Plans.openTab == 0));
            tabs.Add(new TabRecord("Yuudong2.Surgery.Tab.Health".Translate(), () => Plans.openTab = 1, Plans.openTab == 1));
            Widgets.DrawMenuSection(body);
            TabDrawer.DrawTabs(body, tabs);
            inRect = body.ContractedBy(8f);
            if (Plans.openTab == 1)
            {
                DoHealthTab(inRect);
                return;
            }

            Rect top = new Rect(inRect.x, inRect.y, inRect.width, 30f);
            DoTopBar(top);
            Rect materials = new Rect(inRect.x, top.yMax + 4f, inRect.width, 30f);
            DoMaterials(materials);

            List<Pawn> pawns = Colonists().ToList();
            if (Plans.onlyIncomplete)
            {
                pawns = pawns.Where(p => StatusFor(p).Any(s => s.state != PlanState.Done)).ToList();
            }

            switch (Plans.sortMode)
            {
                case 1:
                    pawns = pawns.OrderBy(p => p.LabelShort).ToList();
                    break;
                case 2:
                    pawns = pawns.OrderBy(p => Progress(StatusFor(p))).ThenBy(p => p.LabelShort).ToList();
                    break;
                default:
                    PlayerPawnsDisplayOrderUtility.Sort(pawns);
                    break;
            }

            float fixedWidth = NameWidth + PresetWidth + ProgressWidth + QueueWidth;
            float slotWidth = Mathf.Max(MinSlotWidth, (inRect.width - 16f - fixedWidth) / Slots.Count);
            float tableWidth = fixedWidth + slotWidth * Slots.Count;

            Rect header = new Rect(inRect.x, materials.yMax + 6f, inRect.width, HeaderHeight);
            Rect outRect = new Rect(inRect.x, header.yMax, inRect.width, inRect.yMax - header.yMax);
            Rect viewRect = new Rect(0f, 0f, tableWidth, RowHeight * pawns.Count);

            // 열 머리는 가로 스크롤을 따라간다.
            GUI.BeginGroup(header);
            DoHeader(new Rect(-scroll.x, 0f, tableWidth, HeaderHeight), slotWidth);
            GUI.EndGroup();

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            // 기본 순서일 때는 이름 칸을 끌어서 순서를 바꾼다. 정착민 막대·작업 탭과 같은 순서라 거기에도 반영된다.
            int reorderGroup = -1;
            if (Plans.sortMode == 0)
            {
                List<Pawn> shown = pawns;
                reorderGroup = ReorderableWidget.NewGroup((from, to) => Reorder(shown, from, to), ReorderableDirection.Vertical, viewRect);
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                Rect row = new Rect(0f, i * RowHeight, tableWidth, RowHeight);
                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(row);
                }

                if (reorderGroup >= 0)
                {
                    ReorderableWidget.Reorderable(reorderGroup, new Rect(row.x, row.y, NameWidth, row.height));
                }

                DoRow(row, pawns[i], slotWidth);
            }

            Widgets.EndScrollView();
        }

        private HealthSummary HealthFor(Pawn pawn)
        {
            StatusFor(pawn);
            if (!healthCache.TryGetValue(pawn, out HealthSummary summary))
            {
                summary = HealthSummary.Build(pawn);
                healthCache[pawn] = summary;
            }

            return summary;
        }

        private void DoHealthTab(Rect inRect)
        {
            Rect top = new Rect(inRect.x, inRect.y, inRect.width, 30f);
            Checkbox(top.x, top, "Yuudong2.Health.OnlyProblems", ref Plans.onlyHealthProblems);

            List<Pawn> pawns = Colonists().ToList();
            PlayerPawnsDisplayOrderUtility.Sort(pawns);
            if (Plans.onlyHealthProblems)
            {
                pawns = pawns.Where(p => HealthFor(p).Any).ToList();
            }

            float columnWidth = Mathf.Max(150f, (inRect.width - 16f - NameWidth) / HealthSummary.ColumnCount);
            float tableWidth = NameWidth + columnWidth * HealthSummary.ColumnCount;
            Rect header = new Rect(inRect.x, top.yMax + 6f, inRect.width, HeaderHeight);
            Rect outRect = new Rect(inRect.x, header.yMax, inRect.width, inRect.yMax - header.yMax);
            float height = 0f;
            foreach (Pawn pawn in pawns)
            {
                height += HealthRowHeight(HealthFor(pawn));
            }

            GUI.BeginGroup(header);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(-healthScroll.x, 0f, NameWidth, HeaderHeight), "Yuudong2.Surgery.Colonist".Translate());
            for (int i = 0; i < HealthSummary.ColumnCount; i++)
            {
                Widgets.Label(new Rect(-healthScroll.x + NameWidth + columnWidth * i, 0f, columnWidth, HeaderHeight), ("Yuudong2.Health.Column." + (HealthColumn)i).Translate());
            }

            Text.Anchor = TextAnchor.UpperLeft;
            GUI.EndGroup();

            Widgets.BeginScrollView(outRect, ref healthScroll, new Rect(0f, 0f, tableWidth, height));
            float y = 0f;
            for (int i = 0; i < pawns.Count; i++)
            {
                HealthSummary summary = HealthFor(pawns[i]);
                Rect row = new Rect(0f, y, tableWidth, HealthRowHeight(summary));
                y += row.height;
                if (row.yMax < healthScroll.y || row.y > healthScroll.y + outRect.height)
                {
                    continue;
                }

                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(row);
                }

                DoHealthRow(row, pawns[i], summary, columnWidth);
            }

            Widgets.EndScrollView();
        }

        private static float HealthRowHeight(HealthSummary summary)
        {
            return Mathf.Max(RowHeight, summary.MaxLines * LineHeight + 6f);
        }

        private static void DoHealthRow(Rect row, Pawn pawn, HealthSummary summary, float columnWidth)
        {
            Rect nameRect = new Rect(row.x, row.y, NameWidth, RowHeight);
            Widgets.ThingIcon(new Rect(nameRect.x + 2f, nameRect.y + 3f, 28f, 28f), pawn);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(nameRect.x + 34f, nameRect.y, nameRect.width - 34f, nameRect.height), pawn.LabelShortCap.Truncate(nameRect.width - 36f));
            if (Widgets.ButtonInvisible(nameRect))
            {
                CameraJumper.TryJumpAndSelect(pawn);
            }

            Text.WordWrap = false;
            for (int c = 0; c < HealthSummary.ColumnCount; c++)
            {
                List<HealthLine> lines = summary.columns[c];
                for (int i = 0; i < lines.Count; i++)
                {
                    Rect rect = new Rect(row.x + NameWidth + columnWidth * c + 4f, row.y + 3f + LineHeight * i, columnWidth - 8f, LineHeight);
                    GUI.color = lines[i].color;
                    Widgets.Label(rect, lines[i].text.Truncate(rect.width));
                    GUI.color = Color.white;
                    if (Mouse.IsOver(rect))
                    {
                        Widgets.DrawHighlight(rect);
                        TooltipHandler.TipRegion(rect, lines[i].tip ?? lines[i].text);
                    }
                }
            }

            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void DoTopBar(Rect rect)
        {
            float x = rect.x;
            if (Widgets.ButtonText(new Rect(x, rect.y, 120f, rect.height), "Yuudong2.Surgery.Presets".Translate()))
            {
                Find.WindowStack.Add(new Dialog_SurgeryPresets(this));
            }

            x += 126f;
            if (Widgets.ButtonText(new Rect(x, rect.y, 120f, rect.height), "Yuudong2.Surgery.QueueAll".Translate()))
            {
                QueueFor(Colonists().ToList());
            }

            x += 136f;
            string sortKey = Plans.sortMode == 1 ? "Yuudong2.Surgery.SortName" : Plans.sortMode == 2 ? "Yuudong2.Surgery.SortProgress" : "Yuudong2.Surgery.SortDefault";
            Rect sortRect = new Rect(x, rect.y, 140f, rect.height);
            if (Widgets.ButtonText(sortRect, sortKey.Translate()))
            {
                Plans.sortMode = (Plans.sortMode + 1) % 3;
            }

            TooltipHandler.TipRegion(sortRect, "Yuudong2.Surgery.SortDefault.Desc".Translate());

            x += 156f;
            x = Checkbox(x, rect, "Yuudong2.Surgery.OnlyIncomplete", ref Plans.onlyIncomplete);
            x = Checkbox(x, rect, "Yuudong2.Surgery.OnlyWithMaterials", ref Plans.onlyWithMaterials);
            Checkbox(x, rect, "Yuudong2.Surgery.NanobotsOneAtATime", ref Plans.nanobotsOneAtATime);
        }

        private static float Checkbox(float x, Rect rect, string key, ref bool value)
        {
            string label = key.Translate();
            float width = Text.CalcSize(label).x + 34f;
            Widgets.CheckboxLabeled(new Rect(x, rect.y + 3f, width, 24f), label, ref value);
            TooltipHandler.TipRegion(new Rect(x, rect.y, width, rect.height), (key + ".Desc").Translate());
            return x + width + 16f;
        }

        // 모든 정착민의 계획을 다 채우는 데 필요한 주재료와 지금 맵에 있는 개수.
        private void DoMaterials(Rect rect)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }

            Dictionary<ThingDef, int> need = new Dictionary<ThingDef, int>();
            foreach (Pawn pawn in Colonists())
            {
                foreach (PlanStatus status in StatusFor(pawn))
                {
                    if ((status.state == PlanState.Missing || status.state == PlanState.Queued || status.state == PlanState.Blocked) && status.recipe != null)
                    {
                        ThingDef item = SurgeryPlanner.MainItem(status.recipe);
                        if (item != null)
                        {
                            need[item] = (need.TryGetValue(item, out int n) ? n : 0) + 1;
                        }
                    }
                }
            }

            Widgets.Label(new Rect(rect.x, rect.y + 4f, 60f, 24f), "Yuudong2.Surgery.Materials".Translate());
            float x = rect.x + 64f;
            foreach (KeyValuePair<ThingDef, int> kv in need.OrderBy(kv => kv.Key.label))
            {
                int have = SurgeryPlanner.CountOnMap(map, kv.Key);
                string text = have + "/" + kv.Value;
                float width = 26f + Text.CalcSize(text).x + 8f;
                if (x + width > rect.xMax)
                {
                    break;
                }

                Rect cell = new Rect(x, rect.y + 2f, width, 26f);
                Widgets.ThingIcon(new Rect(cell.x, cell.y + 1f, 24f, 24f), kv.Key);
                GUI.color = have >= kv.Value ? Color.white : ColorLibrary.RedReadable;
                Widgets.Label(new Rect(cell.x + 26f, cell.y + 2f, width - 26f, 24f), text);
                GUI.color = Color.white;
                TooltipHandler.TipRegion(cell, "Yuudong2.Surgery.MaterialTip".Translate(kv.Key.LabelCap, have, kv.Value));
                if (Widgets.ButtonInvisible(cell))
                {
                    JumpToBench(map, kv.Key);
                }

                x += width + 6f;
            }
        }

        private static void JumpToBench(Map map, ThingDef item)
        {
            HashSet<ThingDef> benches = new HashSet<ThingDef>();
            foreach (RecipeDef recipe in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                if (recipe.products != null && recipe.products.Any(p => p.thingDef == item))
                {
                    foreach (ThingDef user in recipe.AllRecipeUsers)
                    {
                        benches.Add(user);
                    }
                }
            }

            Building bench = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => benches.Contains(b.def));
            if (bench != null)
            {
                CameraJumper.TryJumpAndSelect(bench);
            }
            else
            {
                Messages.Message("Yuudong2.Surgery.NoBench".Translate(item.LabelCap), MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        private void DoHeader(Rect rect, float slotWidth)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            float x = rect.x;
            Widgets.Label(new Rect(x, rect.y, NameWidth, rect.height), "Yuudong2.Surgery.Colonist".Translate());
            x += NameWidth;
            Widgets.Label(new Rect(x, rect.y, PresetWidth, rect.height), "Yuudong2.Surgery.Plan".Translate());
            x += PresetWidth;
            Widgets.Label(new Rect(x, rect.y, ProgressWidth, rect.height), "Yuudong2.Surgery.Progress".Translate());
            x += ProgressWidth + QueueWidth;
            foreach (Slot slot in Slots)
            {
                Widgets.Label(new Rect(x, rect.y, slotWidth, rect.height), slot.label.Truncate(slotWidth - 4f));
                x += slotWidth;
            }

            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static float Progress(List<PlanStatus> statuses)
        {
            return statuses.Count == 0 ? 1f : (float)statuses.Count(s => s.state == PlanState.Done) / statuses.Count;
        }

        private void DoRow(Rect row, Pawn pawn, float slotWidth)
        {
            List<PlanStatus> statuses = StatusFor(pawn);
            float x = row.x;

            Rect nameRect = new Rect(x, row.y, NameWidth, row.height);
            Widgets.ThingIcon(new Rect(nameRect.x + 2f, nameRect.y + 3f, 28f, 28f), pawn);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(nameRect.x + 34f, nameRect.y, nameRect.width - 34f, nameRect.height), pawn.LabelShortCap.Truncate(nameRect.width - 36f));
            Text.Anchor = TextAnchor.UpperLeft;
            if (Widgets.ButtonInvisible(nameRect))
            {
                CameraJumper.TryJumpAndSelect(pawn);
            }

            x += NameWidth;
            SurgeryPreset preset = Plans.PresetFor(pawn);
            if (Widgets.ButtonText(new Rect(x + 2f, row.y + 4f, PresetWidth - 4f, row.height - 8f), (preset?.label ?? "Yuudong2.Surgery.NoPlan".Translate().ToString()).Truncate(PresetWidth - 12f)))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("Yuudong2.Surgery.NoPlan".Translate(), () => { Plans.Assign(pawn, null); Invalidate(); }),
                };
                foreach (SurgeryPreset p in Plans.presets)
                {
                    SurgeryPreset chosen = p;
                    options.Add(new FloatMenuOption(p.label, () => { Plans.Assign(pawn, chosen); Invalidate(); }));
                }

                Find.WindowStack.Add(new FloatMenu(options));
            }

            x += PresetWidth;
            int done = statuses.Count(s => s.state == PlanState.Done);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(x, row.y, ProgressWidth, row.height), done + "/" + statuses.Count);
            Text.Anchor = TextAnchor.UpperLeft;
            x += ProgressWidth;

            bool anyMissing = statuses.Any(s => s.state == PlanState.Missing);
            Rect queueRect = new Rect(x + 2f, row.y + 4f, QueueWidth - 4f, row.height - 8f);
            if (anyMissing)
            {
                if (Widgets.ButtonText(queueRect, "Yuudong2.Surgery.Queue".Translate()))
                {
                    QueueFor(new List<Pawn> { pawn });
                }
            }

            x += QueueWidth;
            foreach (Slot slot in Slots)
            {
                DoCell(new Rect(x, row.y, slotWidth, row.height), pawn, slot, statuses);
                x += slotWidth;
            }
        }

        private void DoCell(Rect rect, Pawn pawn, Slot slot, List<PlanStatus> statuses)
        {
            List<PlanStatus> mine = statuses.Where(s => slot.defs.Contains(s.entry.part)).ToList();
            bool hasParts = pawn.RaceProps.body.AllParts.Any(p => slot.defs.Contains(p.def));
            Rect inner = rect.ContractedBy(1f);
            if (!hasParts)
            {
                Widgets.DrawBoxSolid(inner, new Color(0f, 0f, 0f, 0.35f));
                return;
            }

            if (mine.Count > 0)
            {
                Widgets.DrawBoxSolid(inner, CellColor(mine));
            }

            List<Hediff> installed = pawn.health.hediffSet.hediffs
                .Where(h => h.Part != null && slot.defs.Contains(h.Part.def) && (h is Hediff_AddedPart || h is Hediff_Implant || h.def.countsAsAddedPartOrImplant))
                .ToList();
            DrawIcons(inner, installed);

            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
                TooltipHandler.TipRegion(rect, () => CellTooltip(slot, installed, mine), pawn.thingIDNumber * 131 + slot.label.GetHashCode());
            }

            if (Widgets.ButtonInvisible(rect))
            {
                CameraJumper.TryJumpAndSelect(pawn);
                InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Health));
            }
        }

        private static Color CellColor(List<PlanStatus> statuses)
        {
            if (statuses.Any(s => s.state == PlanState.Missing))
            {
                return MissingColor;
            }

            if (statuses.Any(s => s.state == PlanState.Blocked))
            {
                return BlockedColor;
            }

            if (statuses.Any(s => s.state == PlanState.Queued))
            {
                return QueuedColor;
            }

            if (statuses.Any(s => s.state == PlanState.InProgress))
            {
                return ProgressColor;
            }

            return DoneColor;
        }

        private static void DrawIcons(Rect rect, List<Hediff> hediffs)
        {
            if (hediffs.Count == 0)
            {
                return;
            }

            float size = hediffs.Count <= Mathf.FloorToInt(rect.width / 22f) ? 22f : 15f;
            int perRow = Mathf.Max(1, Mathf.FloorToInt(rect.width / size));
            int rows = Mathf.CeilToInt((float)hediffs.Count / perRow);
            float top = rect.y + (rect.height - rows * size) / 2f;
            for (int i = 0; i < hediffs.Count; i++)
            {
                int r = i / perRow;
                int c = i % perRow;
                int countInRow = Mathf.Min(perRow, hediffs.Count - r * perRow);
                float left = rect.x + (rect.width - countInRow * size) / 2f;
                Rect icon = new Rect(left + c * size, top + r * size, size, size);
                ThingDef item = SurgeryPlanner.IconFor(hediffs[i].def);
                if (item != null)
                {
                    Widgets.ThingIcon(icon, item);
                }
                else
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(icon, hediffs[i].LabelBaseCap.Substring(0, Mathf.Min(2, hediffs[i].LabelBaseCap.Length)));
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.Font = GameFont.Small;
                }
            }
        }

        private static string CellTooltip(Slot slot, List<Hediff> installed, List<PlanStatus> statuses)
        {
            List<string> lines = new List<string> { slot.label };
            if (installed.Count > 0)
            {
                lines.Add("");
                lines.Add("Yuudong2.Surgery.Installed".Translate());
                foreach (Hediff hediff in installed)
                {
                    lines.Add("  " + hediff.LabelCap + " (" + hediff.Part.LabelCap + ")");
                }
            }

            if (statuses.Count > 0)
            {
                lines.Add("");
                lines.Add("Yuudong2.Surgery.Plan".Translate());
                foreach (PlanStatus status in statuses)
                {
                    lines.Add("  " + status.entry.hediff.LabelCap + " (" + status.part.LabelCap + "): " + StateLabel(status));
                }
            }

            return string.Join("\n", lines);
        }

        private static string StateLabel(PlanStatus status)
        {
            switch (status.state)
            {
                case PlanState.Done:
                    return "Yuudong2.Surgery.State.Done".Translate();
                case PlanState.InProgress:
                    return "Yuudong2.Surgery.State.InProgress".Translate(status.daysLeft.ToString("0.0"));
                case PlanState.Queued:
                    return "Yuudong2.Surgery.State.Queued".Translate();
                case PlanState.Missing:
                    return "Yuudong2.Surgery.State.Missing".Translate();
                default:
                    return "Yuudong2.Surgery.State.Blocked".Translate(status.reason);
            }
        }

        // 보이는 정착민끼리 순서를 바꾼다. 원래 가지고 있던 표시 순서 값들을 새 순서대로 다시 나눠 줘서, 안 보이는 정착민의 자리는 그대로 둔다.
        private void Reorder(List<Pawn> shown, int from, int to)
        {
            if (from == to || from < 0 || from >= shown.Count)
            {
                return;
            }

            List<Pawn> order = new List<Pawn>(shown);
            List<int> values = order.Select(p => p.playerSettings?.displayOrder ?? 0).OrderBy(v => v).ToList();
            if (values.Distinct().Count() != values.Count)
            {
                int start = values[0];
                values = Enumerable.Range(start, order.Count).ToList();
            }

            Pawn moved = order[from];
            order.RemoveAt(from);
            order.Insert(to > from ? to - 1 : to, moved);
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i].playerSettings != null)
                {
                    order[i].playerSettings.displayOrder = values[i];
                }
            }

            Find.ColonistBar.MarkColonistsDirty();
            MainTabWindowUtility.NotifyAllPawnTables_PawnsChanged();
        }

        private void QueueFor(List<Pawn> pawns)
        {
            Dictionary<ThingDef, int> used = new Dictionary<ThingDef, int>();
            int queued = 0;
            int noMaterials = 0;
            foreach (Pawn pawn in pawns)
            {
                SurgeryPlanner.QueueResult result = SurgeryPlanner.QueueMissing(pawn, Plans.PresetFor(pawn), Plans, used);
                queued += result.queued;
                noMaterials += result.noMaterials;
            }

            Invalidate();
            if (queued == 0 && noMaterials == 0)
            {
                Messages.Message("Yuudong2.Surgery.NothingToQueue".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            string text = "Yuudong2.Surgery.QueuedMessage".Translate(queued);
            if (noMaterials > 0)
            {
                text += " " + "Yuudong2.Surgery.SkippedMaterials".Translate(noMaterials);
            }

            Messages.Message(text, MessageTypeDefOf.TaskCompletion, historical: false);
            SoundDefOf.Click.PlayOneShotOnCamera();
        }
    }

    // 기능이 꺼져 있으면 버튼을 숨긴다.
    public class MainButtonWorker_SurgeryManager : MainButtonWorker_ToggleTab
    {
        public override bool Visible => base.Visible && global::Yuudong2Mod.Yuudong2Mod.AppliedFeatures.Contains(Features.SurgeryManager);
    }
}

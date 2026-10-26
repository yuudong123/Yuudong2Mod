using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    // Replace Stuff의 재질 교체 기능만 포팅. 건물을 해체하지 않고 그 자리에서 재질만 바꾼다.
    // 원본과 달리 모든 예약 메서드를 패치하지 않고, 교체 중 판정은 대응표로 바로 찾는다.
    public class ReplaceFrame : Frame
    {
        private const float MaxDeconstructWork = 3000f;

        public Thing oldThing;
        public ThingDef oldStuff;

        // 벽 교체(다른 벽으로 바꾸거나 환풍벽 방향을 바꾸는 경우)일 때 원래 벽의 Def. 재질만 바꾸거나 예전 저장 파일이면 null이다.
        public ThingDef oldDef;

        public bool IsWallSwap => oldDef != null;

        public float WorkToReplace => def.entityDefToBuild.GetStatValueAbstract(StatDefOf.WorkToBuild, Stuff);

        public float WorkToDeconstruct => ReplaceStuffUtility.WorkToDeconstruct(IsWallSwap ? oldDef : def.entityDefToBuild, oldStuff);

        public float TotalWork => WorkToDeconstruct + WorkToReplace;

        public override string Label
        {
            get
            {
                string text = def.entityDefToBuild.label + "Yuudong2.Replace.Tag".Translate();
                return Stuff != null ? Stuff.label + " " + text : text;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref oldThing, "oldThing");
            Scribe_Defs.Look(ref oldStuff, "oldStuff");
            Scribe_Defs.Look(ref oldDef, "oldDef");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // 맵을 불러올 때 기존 건물과 프레임이 서로를 지우지 않도록 스폰 전에 등록한다.
                ReplaceStuffUtility.Register(this);
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            ReplaceStuffUtility.Register(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            ReplaceStuffUtility.Unregister(this);
            base.DeSpawn(mode);
        }

        public int TotalStuffNeeded()
        {
            return ReplaceStuffUtility.StuffCountFor(def.entityDefToBuild, Stuff);
        }

        public void CompleteReplace(Pawn worker)
        {
            if (IsWallSwap && oldThing != null && oldThing.Spawned)
            {
                WallSwapUtility.FinalizeSwap(this, worker);
                return;
            }

            if (oldThing != null && oldThing.Spawned)
            {
                ReplaceStuffUtility.FinalizeReplace(oldThing, Stuff, worker);
                resourceContainer.ClearAndDestroyContents();
                Destroy();
                if (worker != null)
                {
                    worker.records.Increment(RecordDefOf.ThingsConstructed);
                }
            }
            else
            {
                // 수정: 기존 건물이 어떤 이유로든 사라졌으면 재료를 떨구지 않고 새 재질로 건물을 지어 마무리한다.
                Log.Warning("[Yuudong2Mod] 재질 교체 대상 건물이 사라져서 새 재질로 새로 지었습니다: " + LabelEntityToBuild);
                Map map = Map;
                IntVec3 position = Position;
                Rot4 rotation = Rotation;
                Faction faction = Faction;
                resourceContainer.ClearAndDestroyContents();
                Destroy();
                if (def.entityDefToBuild is ThingDef builtDef)
                {
                    Thing built = ThingMaker.MakeThing(builtDef, Stuff);
                    built.SetFactionDirect(faction);
                    CompQuality quality = built.TryGetComp<CompQuality>();
                    if (quality != null && worker != null)
                    {
                        quality.SetQuality(QualityUtility.GenerateQualityCreatedByPawn(worker, SkillDefOf.Construction), ArtGenerationContext.Colony);
                    }

                    GenSpawn.Spawn(built, position, map, rotation, WipeMode.FullRefund);
                }
            }
        }

        public void FailReplace(Pawn worker)
        {
            float deconstructWork = WorkToDeconstruct;
            workDone = Mathf.Min(workDone, deconstructWork);
            if (workDone >= deconstructWork)
            {
                GenLeaving.DoLeavingsFor(this, Map, DestroyMode.FailConstruction);
                MoteMaker.ThrowText(DrawPos, Map, "TextMote_ConstructionFail".Translate());
                if (Faction == Faction.OfPlayer && WorkToReplace > 1400f)
                {
                    Messages.Message("MessageConstructionFailed".Translate(LabelEntityToBuild, worker.LabelShort, worker.Named("WORKER")),
                        new TargetInfo(Position, Map), MessageTypeDefOf.NegativeEvent);
                }
            }
        }

        public override string GetInspectString()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("ContainedResources".Translate() + ":");
            foreach (ThingDefCountClass cost in ReplaceStuffUtility.MaterialCost(this))
            {
                builder.AppendLine(cost.thingDef.LabelCap + ": " + resourceContainer.TotalStackCountOfDef(cost.thingDef) + " / " + cost.count);
            }

            builder.Append("WorkLeft".Translate() + ": " + WorkLeft.ToStringWorkAmount());
            return builder.ToString();
        }

        public static float MaxDeconstruct => MaxDeconstructWork;
    }

    internal static class ReplaceStuffUtility
    {
        private const string FrameDefPrefix = "Yuudong2_ReplaceFrame_";

        private static readonly Dictionary<ThingDef, ThingDef> frameDefs = new Dictionary<ThingDef, ThingDef>();
        private static readonly HashSet<ThingDef> frameDefSet = new HashSet<ThingDef>();
        private static readonly ConditionalWeakTable<Thing, ReplaceFrame> framesByOldThing = new ConditionalWeakTable<Thing, ReplaceFrame>();
        private static readonly Dictionary<(BuildableDef, ThingDef), List<ThingDefCountClass>> costCache = new Dictionary<(BuildableDef, ThingDef), List<ThingDefCountClass>>();

        private static readonly Func<Thing, DestroyMode, Func<int, int>> leaveCalculator =
            AccessTools.MethodDelegate<Func<Thing, DestroyMode, Func<int, int>>>(AccessTools.Method(typeof(GenLeaving), "GetBuildingResourcesLeaveCalculator"));

        private static readonly AccessTools.FieldRef<StatDef, StatWorker> statWorker = AccessTools.FieldRefAccess<StatDef, StatWorker>("workerInt");
        private static readonly FieldInfo temporaryStatCache = AccessTools.Field(typeof(StatWorker), "temporaryStatCache");
        private static readonly FieldInfo immutableStatCache = AccessTools.Field(typeof(StatWorker), "immutableStatCache");

        // ---------- 프레임 Def 생성 (바닐라 암시적 Def 생성 단계에서 호출) ----------

        public static void GenerateFrameDefs(bool hotReload)
        {
            if (hotReload)
            {
                return;
            }

            ModContentPack content = LoadedModManager.GetMod<Yuudong2ModSettingsMod>()?.Content;
            foreach (ThingDef def in new List<ThingDef>(DefDatabase<ThingDef>.AllDefsListForReading))
            {
                if (def.designationCategory == null || !def.IsBuildingArtificial || def.IsFrame || def.IsBlueprint
                    || (!def.MadeFromStuff && !WallSwapUtility.GloomyWalls.Contains(def.defName)))
                {
                    continue;
                }

                ThingDef frame = NewFrameDef(def, content);
                frameDefs[def] = frame;
                frameDefSet.Add(frame);
                DefGenerator.AddImpliedDef(frame);
            }
        }

        private static ThingDef NewFrameDef(ThingDef def, ModContentPack content)
        {
            ThingDef frame = new ThingDef
            {
                defName = FrameDefPrefix + def.defName,
                label = def.label + "Yuudong2.Replace.Tag".Translate(),
                description = def.description,
                isFrameInt = true,
                category = ThingCategory.Building,
                thingClass = typeof(ReplaceFrame),
                altitudeLayer = AltitudeLayer.BuildingOnTop,
                useHitPoints = true,
                selectable = true,
                drawerType = DrawerType.RealtimeOnly,
                building = new BuildingProperties { isEdifice = false },
                scatterableOnMapGen = false,
                leaveResourcesWhenKilled = true,
                size = def.size,
                fillPercent = 0.2f,
                pathCost = 10,
                passability = def.passability,
                rotatable = def.rotatable,
                constructEffect = def.constructEffect,
                constructionSkillPrerequisite = def.constructionSkillPrerequisite,
                artisticSkillPrerequisite = def.artisticSkillPrerequisite,
                clearBuildingArea = false,
                stuffCategories = def.stuffCategories,
                entityDefToBuild = def,
                modContentPack = content,
            };
            frame.comps.Add(new CompProperties_Forbiddable());
            frame.SetStatBaseValue(StatDefOf.MaxHitPoints, def.BaseMaxHitPoints * 0.25f);
            frame.SetStatBaseValue(StatDefOf.Beauty, -8f);
            return frame;
        }

        public static bool IsReplaceFrameDef(BuildableDef def)
        {
            return def is ThingDef thingDef && frameDefSet.Contains(thingDef);
        }

        // ---------- 교체 중 판정 ----------

        public static void Register(ReplaceFrame frame)
        {
            if (frame.oldThing == null)
            {
                return;
            }

            framesByOldThing.AddOrUpdate(frame.oldThing, frame);
        }

        public static void Unregister(ReplaceFrame frame)
        {
            if (frame.oldThing != null && framesByOldThing.TryGetValue(frame.oldThing, out ReplaceFrame current) && current == frame)
            {
                framesByOldThing.Remove(frame.oldThing);
            }
        }

        // 교체 작업이 시작된 건물은 작업대·포탑으로 쓰지 않는다.
        public static bool IsBeingReplaced(Thing thing)
        {
            return thing != null && framesByOldThing.TryGetValue(thing, out ReplaceFrame frame) && frame.Spawned && frame.workDone > 0f;
        }

        // 이미 교체 프레임이 있으면 건물 대신 그 프레임을 바꾼다.
        public static bool HasReplaceFrame(Thing thing)
        {
            return framesByOldThing.TryGetValue(thing, out ReplaceFrame frame) && frame.Spawned;
        }

        // ---------- 비용과 작업량 ----------

        public static int StuffCountFor(BuildableDef def, ThingDef stuff)
        {
            return Mathf.Max(1, Mathf.RoundToInt(def.costStuffCount / stuff.VolumePerUnit));
        }

        public static List<ThingDefCountClass> MaterialCost(ReplaceFrame frame)
        {
            if (frame.IsWallSwap)
            {
                return frame.def.entityDefToBuild.CostListAdjusted(frame.Stuff);
            }

            (BuildableDef, ThingDef) key = (frame.def.entityDefToBuild, frame.Stuff);
            if (!costCache.TryGetValue(key, out List<ThingDefCountClass> cost))
            {
                cost = new List<ThingDefCountClass> { new ThingDefCountClass(frame.Stuff, StuffCountFor(key.Item1, key.Item2)) };
                costCache[key] = cost;
            }

            return cost;
        }

        public static float WorkToDeconstruct(BuildableDef built, ThingDef oldStuff)
        {
            return Mathf.Min(built.GetStatValueAbstract(StatDefOf.WorkToBuild, oldStuff), ReplaceFrame.MaxDeconstruct);
        }

        // ---------- 교체 가능 여부와 실행 ----------

        public static bool CanReplaceStuffFor(ThingDef stuff, Thing thing, BuildableDef matchDef = null)
        {
            if (stuff == null || (thing.Faction != Faction.OfPlayer && thing.Faction != null))
            {
                return false;
            }

            BuildableDef built = GenConstruct.BuiltDefOf(thing.def);
            if (built == null || (matchDef != null && built != matchDef))
            {
                return false;
            }

            if (thing is Blueprint blueprint)
            {
                if (blueprint is Blueprint_Install || blueprint.EntityToBuildStuff() == stuff)
                {
                    return false;
                }
            }
            else if (thing is Frame frame)
            {
                if (frame.EntityToBuildStuff() == stuff)
                {
                    return false;
                }
            }
            else if (!frameDefs.ContainsKey(thing.def) || thing.Stuff == stuff || HasReplaceFrame(thing))
            {
                return false;
            }

            if (!GenConstruct.CanBuildOnTerrain(built, thing.Position, thing.Map, thing.Rotation, thing, stuff))
            {
                return false;
            }

            foreach (ThingDef allowed in GenStuff.AllowedStuffsFor(built))
            {
                if (allowed == stuff)
                {
                    return true;
                }
            }

            return false;
        }

        public static Thing FindReplaceable(IntVec3 cell, Map map, ThingDef stuff, BuildableDef matchDef = null, Rot4? rotation = null)
        {
            Thing fallback = null;
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing.Position != cell || (rotation.HasValue && thing.Rotation != rotation.Value && !RotationIrrelevant(thing.def)))
                {
                    continue;
                }

                if (!CanReplaceStuffFor(stuff, thing, matchDef))
                {
                    continue;
                }

                // 청사진이나 건설 중인 프레임이 있으면 그쪽을 먼저 바꾼다.
                if (thing is Blueprint_Build || thing is Frame)
                {
                    return thing;
                }

                fallback = fallback ?? thing;
            }

            return fallback;
        }

        public static bool RotationIrrelevant(ThingDef def)
        {
            ThingDef built = GenConstruct.BuiltDefOf(def) as ThingDef ?? def;
            return !built.rotatable || typeof(Building_Door).IsAssignableFrom(built.thingClass);
        }

        public static void DoReplace(Thing thing, ThingDef stuff)
        {
            IntVec3 position = thing.Position;
            Rot4 rotation = thing.Rotation;
            Map map = thing.Map;
            CellRect rect = GenAdj.OccupiedRect(position, rotation, thing.def.size);

            if (thing is Blueprint_Build blueprint)
            {
                BuildableDef built = blueprint.def.entityDefToBuild;
                blueprint.Destroy(DestroyMode.Cancel);
                GenConstruct.PlaceBlueprintForBuild(built, position, map, rotation, Faction.OfPlayer, stuff);
            }
            else if (thing is ReplaceFrame replaceFrame)
            {
                Thing target = replaceFrame.oldThing;
                float progress = Mathf.Min(replaceFrame.workDone, replaceFrame.WorkToDeconstruct);
                replaceFrame.Destroy(DestroyMode.Cancel);
                ThingDef swapTo = replaceFrame.IsWallSwap ? replaceFrame.def.entityDefToBuild as ThingDef : null;
                if (target != null && target.Spawned && (swapTo != null || target.Stuff != stuff))
                {
                    ReplaceFrame newFrame = PlaceReplaceFrame(target, stuff, swapTo, replaceFrame.Rotation);
                    if (newFrame != null)
                    {
                        newFrame.workDone = progress;
                    }
                }
            }
            else if (thing is Frame frame)
            {
                BuildableDef built = frame.def.entityDefToBuild;
                frame.Destroy(DestroyMode.Cancel);
                GenConstruct.PlaceBlueprintForBuild(built, position, map, rotation, Faction.OfPlayer, stuff);
            }
            else if (DebugSettings.godMode)
            {
                FinalizeReplace(thing, stuff);
            }
            else
            {
                PlaceReplaceFrame(thing, stuff);
            }

            FleckMaker.ThrowMetaPuffs(rect, map);
        }

        // targetDef가 있으면 벽 교체(다른 벽으로 바꾸기), 없으면 같은 건물의 재질 교체다.
        public static ReplaceFrame PlaceReplaceFrame(Thing oldThing, ThingDef stuff, ThingDef targetDef = null, Rot4? rotation = null)
        {
            ThingDef built = targetDef ?? oldThing.def;
            if (!frameDefs.TryGetValue(built, out ThingDef frameDef))
            {
                return null;
            }

            ReplaceFrame frame = (ReplaceFrame)ThingMaker.MakeThing(frameDef, built.MadeFromStuff ? stuff : null);
            frame.SetFactionDirect(Faction.OfPlayer);
            oldThing.SetFaction(Faction.OfPlayer);
            frame.oldThing = oldThing;
            frame.oldStuff = oldThing.Stuff;
            bool swap = targetDef != null && (targetDef != oldThing.def || (rotation.HasValue && rotation.Value != oldThing.Rotation));
            frame.oldDef = swap ? oldThing.def : null;
            Map map = oldThing.Map;
            SpawnWithoutWipe(frame, oldThing.Position, map, rotation ?? oldThing.Rotation, false);
            if (!oldThing.Spawned)
            {
                Log.Error("[Yuudong2Mod] 교체 프레임을 놓는 중에 " + oldThing.LabelCap + "이(가) 사라졌습니다. 이 로그를 알려 주세요.");
            }

            return frame;
        }

        // GenSpawn.Spawn과 같지만 그 칸의 기존 물건을 지우는 판정(SpawningWipes)을 거치지 않는다.
        // 수정: 교체 프레임을 놓는 순간 기존 건물이 지워지던 문제. 판정 패치가 어떤 경로로든 우회되면 건물이 사라지므로 판정 자체를 쓰지 않는다.
        public static void SpawnWithoutWipe(Thing thing, IntVec3 position, Map map, Rot4 rotation, bool respawningAfterLoad)
        {
            thing.Rotation = rotation;
            thing.Position = position;
            thing.holdingOwner?.Remove(thing);
            thing.SpawnSetup(map, respawningAfterLoad);
        }

        // 교체 대상으로 등록된 건물인가 (프레임이 아직 스폰 전이어도, 불러오기 중이어도).
        public static bool IsReplaceTarget(Thing thing)
        {
            return thing != null && framesByOldThing.TryGetValue(thing, out ReplaceFrame _);
        }

        public static bool HasFrameDefFor(ThingDef def)
        {
            return frameDefs.ContainsKey(def);
        }

        public static ReplaceFrame FrameFor(Thing thing)
        {
            return framesByOldThing.TryGetValue(thing, out ReplaceFrame frame) && frame.Spawned ? frame : null;
        }

        public static void FinalizeReplace(Thing thing, ThingDef stuff, Pawn worker = null)
        {
            DropOldStuff(thing);
            thing.SetStuffDirect(stuff);
            RemoveFromStatCaches(thing);
            thing.HitPoints = thing.MaxHitPoints;
            thing.Notify_ColorChanged();
            if (worker != null)
            {
                CompQuality quality = thing.TryGetComp<CompQuality>();
                if (quality != null)
                {
                    quality.SetQuality(QualityUtility.GenerateQualityCreatedByPawn(worker, SkillDefOf.Construction), ArtGenerationContext.Colony);
                    QualityUtility.SendCraftNotification(thing, worker);
                }
            }
        }

        // 해체할 때처럼 기존 재질 일부를 돌려준다.
        private static void DropOldStuff(Thing oldThing)
        {
            if (Current.ProgramState != ProgramState.Playing || oldThing.Stuff == null || !GenLeaving.CanBuildingLeaveResources(oldThing, DestroyMode.Deconstruct))
            {
                return;
            }

            int count = leaveCalculator(oldThing, DestroyMode.Deconstruct)(StuffCountFor(oldThing.def, oldThing.Stuff));
            if (count <= 0)
            {
                return;
            }

            Thing refund = ThingMaker.MakeThing(oldThing.Stuff);
            refund.stackCount = count;
            GenDrop.TryDropSpawn(refund, oldThing.Position, oldThing.Map, ThingPlaceMode.Near, out Thing _);
        }

        private static void RemoveFromStatCaches(Thing thing)
        {
            foreach (StatDef stat in DefDatabase<StatDef>.AllDefsListForReading)
            {
                StatWorker worker = statWorker(stat);
                if (worker == null)
                {
                    continue;
                }

                (temporaryStatCache?.GetValue(worker) as IDictionary)?.Remove(thing);
                (immutableStatCache?.GetValue(worker) as IDictionary)?.Remove(thing);
            }
        }
    }

    [StaticConstructorOnStartup]
    public class Designator_ReplaceStuff : Designator
    {
        private static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("UI/Designators/Paint_Top");
        private static readonly Vector2 DragPriceDrawOffset = new Vector2(19f, 17f);

        private ThingDef stuffDef = ThingDefOf.WoodLog;

        public Designator_ReplaceStuff()
        {
            soundDragSustain = SoundDefOf.Designate_DragBuilding;
            soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
            soundSucceeded = SoundDefOf.Designate_PlaceBuilding;
            defaultLabel = "Yuudong2.Replace.Label".Translate();
            defaultDesc = "Yuudong2.Replace.Desc".Translate();
            icon = Icon;
        }

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;

        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            GizmoResult result = base.GizmoOnGUI(topLeft, maxWidth, parms);
            float width = GetWidth(maxWidth);
            Widgets.ThingIcon(new Rect(topLeft.x + width / 2f, topLeft.y, width / 2f, width / 2f), stuffDef);
            return result;
        }

        public override void ProcessInput(Event ev)
        {
            if (!CheckCanInteract())
            {
                return;
            }

            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (ThingDef stuff in Map.resourceCounter.AllCountedAmounts.Keys)
            {
                if (!stuff.IsStuff || (!DebugSettings.godMode && Map.listerThings.ThingsOfDef(stuff).Count == 0))
                {
                    continue;
                }

                ThingDef chosen = stuff;
                options.Add(new FloatMenuOption(stuff.LabelCap, () =>
                {
                    base.ProcessInput(ev);
                    Find.DesignatorManager.Select(this);
                    stuffDef = chosen;
                }, stuff));
            }

            if (options.Count == 0)
            {
                Messages.Message("NoStuffsToBuildWith".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            Find.WindowStack.Add(new FloatMenu(options) { vanishIfMouseDistant = true });
            Find.DesignatorManager.Select(this);
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 cell)
        {
            if (!cell.InBounds(Map))
            {
                return false;
            }

            return ReplaceStuffUtility.FindReplaceable(cell, Map, stuffDef) != null;
        }

        public override void DesignateSingleCell(IntVec3 cell)
        {
            Thing thing = ReplaceStuffUtility.FindReplaceable(cell, Map, stuffDef);
            if (thing != null)
            {
                ReplaceStuffUtility.DoReplace(thing, stuffDef);
            }
        }

        public override void DrawMouseAttachments()
        {
            base.DrawMouseAttachments();
            if (ArchitectCategoryTab.InfoRect.Contains(UI.MousePositionOnUIInverted))
            {
                return;
            }

            int total = 0;
            foreach (IntVec3 cell in Find.DesignatorManager.Dragger.DragCells)
            {
                Thing thing = ReplaceStuffUtility.FindReplaceable(cell, Map, stuffDef);
                if (thing != null && !(thing is Frame) && !(thing is Blueprint))
                {
                    total += ReplaceStuffUtility.StuffCountFor(GenConstruct.BuiltDefOf(thing.def), stuffDef);
                }
            }

            Vector2 origin = Event.current.mousePosition + DragPriceDrawOffset;
            GUI.color = stuffDef.uiIconColor;
            GUI.DrawTexture(new Rect(origin.x, origin.y, 27f, 27f), stuffDef.uiIcon);
            string text = total.ToString();
            if (Map.resourceCounter.GetCount(stuffDef) < total)
            {
                GUI.color = Color.red;
                text += " (" + "NotEnoughStoredLower".Translate() + ")";
            }
            else
            {
                GUI.color = Color.white;
            }

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(origin.x + 29f, origin.y, 999f, 29f), text);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        public override void SelectedUpdate()
        {
            if (ArchitectCategoryTab.InfoRect.Contains(UI.MousePositionOnUIInverted))
            {
                return;
            }

            IntVec3 cell = UI.MouseCell();
            if (cell.InBounds(Map))
            {
                Color color = CanDesignateCell(cell).Accepted ? new Color(0.5f, 1f, 0.6f, 0.4f) : new Color(1f, 0f, 0f, 0.4f);
                GhostDrawer.DrawGhostThing(cell, Rot4.North, stuffDef, null, color, AltitudeLayer.Blueprint);
            }
        }

        public override void DrawPanelReadout(ref float curY, float width)
        {
            Widgets.InfoCardButton(width - 24f - 6f, 6f, stuffDef);
            Text.Font = GameFont.Tiny;
        }

        public override void RenderHighlight(List<IntVec3> dragCells)
        {
            DesignatorUtility.RenderHighlightOverSelectableCells(this, dragCells);
        }
    }

    // ---------- Frame의 비가상 메서드를 교체 프레임용으로 돌린다 ----------

    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    internal static class Frame_CompleteConstruction_ReplaceStuff_Patch
    {
        private static bool Prefix(Frame __instance, Pawn worker)
        {
            if (!(__instance is ReplaceFrame frame))
            {
                return true;
            }

            frame.CompleteReplace(worker);
            return false;
        }
    }

    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(Frame), nameof(Frame.FailConstruction))]
    internal static class Frame_FailConstruction_ReplaceStuff_Patch
    {
        private static bool Prefix(Frame __instance, Pawn worker)
        {
            if (!(__instance is ReplaceFrame frame))
            {
                return true;
            }

            frame.FailReplace(worker);
            return false;
        }
    }

    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(Frame), nameof(Frame.TotalMaterialCost))]
    internal static class Frame_TotalMaterialCost_ReplaceStuff_Patch
    {
        private static bool Prefix(Frame __instance, ref List<ThingDefCountClass> __result)
        {
            if (!(__instance is ReplaceFrame frame))
            {
                return true;
            }

            __result = ReplaceStuffUtility.MaterialCost(frame);
            return false;
        }
    }

    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(Frame), nameof(Frame.WorkToBuild), MethodType.Getter)]
    internal static class Frame_WorkToBuild_ReplaceStuff_Patch
    {
        private static bool Prefix(Frame __instance, ref float __result)
        {
            if (!(__instance is ReplaceFrame frame))
            {
                return true;
            }

            __result = frame.TotalWork;
            return false;
        }
    }

    // 교체 프레임을 놓아도 기존 건물을 지우지 않는다.
    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.SpawningWipes))]
    internal static class GenSpawn_SpawningWipes_ReplaceStuff_Patch
    {
        private static bool Prefix(BuildableDef newEntDef, BuildableDef oldEntDef, ref bool __result)
        {
            if (!ReplaceStuffUtility.IsReplaceFrameDef(newEntDef) && !ReplaceStuffUtility.IsReplaceFrameDef(oldEntDef))
            {
                return true;
            }

            __result = false;
            return false;
        }
    }

    // 맵을 불러올 때 건물은 SpawnBuildingAsPossible로 놓이는데, 교체 프레임과 대상 건물이 같은 칸에 있으면
    // 먼저 놓인 쪽 때문에 다른 쪽이 환불(파괴)될 수 있다. 둘 다 판정 없이 그 자리에 놓는다.
    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.SpawnBuildingAsPossible))]
    internal static class GenSpawn_SpawnBuildingAsPossible_ReplaceStuff_Patch
    {
        private static bool Prefix(Building building, Map map, bool respawningAfterLoad)
        {
            if (!(building is ReplaceFrame) && !ReplaceStuffUtility.IsReplaceTarget(building))
            {
                return true;
            }

            ReplaceStuffUtility.SpawnWithoutWipe(building, building.Position, map, building.Rotation, respawningAfterLoad);
            return false;
        }
    }

    // 수정: 교체 중인 건물은 해체할 수 없다. 1.7의 "막는 건물 해체" 작업(DeconstructForBlueprint)과
    // 건설 작업의 막는 물체 처리, 해체 지정이 모두 이 판정을 거친다.
    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(Building), nameof(Building.DeconstructibleBy))]
    internal static class Building_DeconstructibleBy_ReplaceStuff_Patch
    {
        private static void Postfix(Building __instance, ref AcceptanceReport __result)
        {
            if (__result.Accepted && ReplaceStuffUtility.HasReplaceFrame(__instance))
            {
                __result = "Yuudong2.Replace.BeingReplaced".Translate();
            }
        }
    }

    // 원본 Replace Stuff와 같이 교체 프레임에는 붙은 건물이 없다고 본다. 벽을 교체할 때 프레임이 사라지면서 벽등이 부서지지 않게 한다.
    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.GetAttachedBuildings))]
    internal static class GenConstruct_GetAttachedBuildings_ReplaceStuff_Patch
    {
        private static bool Prefix(Thing thing, ref List<Thing> __result)
        {
            if (!(thing is ReplaceFrame))
            {
                return true;
            }

            __result = new List<Thing>();
            return false;
        }
    }

    // 기존 건물이 교체 작업을 막지 않는다.
    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.BlocksConstruction))]
    internal static class GenConstruct_BlocksConstruction_ReplaceStuff_Patch
    {
        private static void Postfix(Thing constructible, ref bool __result)
        {
            if (__result && constructible is ReplaceFrame)
            {
                __result = false;
            }
        }
    }

    // 교체 프레임이 부서져도 그 자리에 새 건물 청사진을 만들지 않는다.
    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(ThingUtility), nameof(ThingUtility.CheckAutoRebuildOnDestroyed))]
    internal static class ThingUtility_CheckAutoRebuildOnDestroyed_ReplaceStuff_Patch
    {
        private static bool Prefix(Thing thing, ref Blueprint_Build __result)
        {
            if (!(thing is ReplaceFrame))
            {
                return true;
            }

            __result = null;
            return false;
        }
    }

    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(Building_WorkTable), nameof(Building_WorkTable.UsableForBillsAfterFueling))]
    internal static class Building_WorkTable_UsableForBillsAfterFueling_ReplaceStuff_Patch
    {
        private static void Postfix(Building_WorkTable __instance, ref bool __result)
        {
            if (__result && ReplaceStuffUtility.IsBeingReplaced(__instance))
            {
                __result = false;
                JobFailReason.Is("Yuudong2.Replace.BeingReplaced".Translate());
            }
        }
    }

    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.TryStartShootSomething))]
    internal static class Building_TurretGun_TryStartShootSomething_ReplaceStuff_Patch
    {
        private static bool Prefix(Building_TurretGun __instance)
        {
            return !ReplaceStuffUtility.IsBeingReplaced(__instance);
        }
    }

    // ---------- 같은 건물을 다른 재질로 지으면 해체 없이 재질 교체로 바꾼다 ----------

    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt_NewTemp))]
    internal static class GenConstruct_CanPlaceBlueprintAt_ReplaceStuff_Patch
    {
        private static string identicalThing;
        private static string identicalBlueprint;

        private static void Postfix(ref AcceptanceReport __result, BuildableDef entDef, IntVec3 center, Rot4 rot, Map map, ThingDef stuffDef)
        {
            if (__result.Accepted || stuffDef == null || !entDef.MadeFromStuff || !(entDef is ThingDef))
            {
                return;
            }

            identicalThing = identicalThing ?? "IdenticalThingExists".Translate();
            identicalBlueprint = identicalBlueprint ?? "IdenticalBlueprintExists".Translate();
            if (__result.Reason != identicalThing && __result.Reason != identicalBlueprint)
            {
                return;
            }

            if (ReplaceStuffUtility.FindReplaceable(center, map, stuffDef, entDef, rot) != null)
            {
                __result = AcceptanceReport.WasAccepted;
            }
        }
    }

    [HarmonyPatchCategory(Features.ReplaceStuff)]
    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell))]
    internal static class Designator_Build_DesignateSingleCell_ReplaceStuff_Patch
    {
        private static bool Prefix(Designator_Build __instance, IntVec3 c, BuildableDef ___entDef, ref Rot4 ___placingRot)
        {
            if (!(___entDef is ThingDef thingDef) || !thingDef.MadeFromStuff || DebugSettings.godMode)
            {
                return true;
            }

            ThingDef stuff = __instance.StuffDef;
            if (stuff == null || ___entDef.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) == 0f)
            {
                return true;
            }

            Rot4 rotation = ___placingRot;
            if (typeof(Building_Door).IsAssignableFrom(thingDef.thingClass))
            {
                rotation = DoorUtility.DoorRotationAt(c, __instance.Map, thingDef.building.preferConnectingToFences);
            }

            Thing replaceable = ReplaceStuffUtility.FindReplaceable(c, __instance.Map, stuff, ___entDef, rotation);
            if (replaceable == null)
            {
                return true;
            }

            ReplaceStuffUtility.DoReplace(replaceable, stuff);
            return false;
        }
    }
}

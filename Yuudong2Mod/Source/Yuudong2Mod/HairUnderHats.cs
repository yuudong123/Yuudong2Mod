using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 모자를 써도 머리카락을 숨기지 않고, 모자를 머리카락 위에 그린다.
    // Show Hair With Hats, Dubs Apparel Tweaks의 머리카락 기능을 대신한다.
    internal static class HairUnderHatsUtility
    {
        public static bool HidesHair(ThingDef def)
        {
            ApparelProperties apparel = def.apparel;
            if (apparel.renderSkipFlags != null)
            {
                return apparel.renderSkipFlags.Contains(RenderSkipFlagDefOf.Hair);
            }

            return apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.UpperHead) || apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead);
        }

        public static bool HidesBeard(ThingDef def)
        {
            ApparelProperties apparel = def.apparel;
            if (apparel.renderSkipFlags != null)
            {
                return apparel.renderSkipFlags.Contains(RenderSkipFlagDefOf.Beard);
            }

            return apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead);
        }

        public static bool IsFullHeadgear(ThingDef def)
        {
            ApparelProperties apparel = def.apparel;
            return apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead)
                || (apparel.renderSkipFlags != null && apparel.renderSkipFlags.Contains(RenderSkipFlagDefOf.Beard));
        }

        public static bool IsHeadgear(ThingDef def)
        {
            return def.IsApparel && (HidesHair(def) || HidesBeard(def) || def.apparel.LastLayer == ApparelLayerDefOf.Overhead);
        }

        public static void AdjustSkipFlags(Pawn pawn, ref PawnDrawParms parms)
        {
            ulong hairFlag = RenderSkipFlagDefOf.Hair;
            ulong beardFlag = RenderSkipFlagDefOf.Beard;
            bool hairSkipped = (parms.skipFlags & hairFlag) != 0;
            bool beardSkipped = (parms.skipFlags & beardFlag) != 0;
            if (!hairSkipped && !beardSkipped)
            {
                return;
            }

            if (pawn?.apparel == null || !pawn.RaceProps.Humanlike || !PawnRenderNodeWorker_Apparel_Head.HeadgearVisible(parms))
            {
                return;
            }

            Yuudong2Settings settings = Yuudong2Settings.Current;
            if (settings.hairOnlyPlayerPawns && pawn.Faction != Faction.OfPlayer)
            {
                return;
            }

            bool hairHiddenByApparel = false;
            bool beardHiddenByApparel = false;
            bool keepHair = pawn.story?.hairDef != null && settings.hairHiddenUnderHeadgear.Contains(pawn.story.hairDef.defName);
            bool keepBeard = false;
            foreach (Apparel apparel in pawn.apparel.WornApparel)
            {
                ThingDef def = apparel.def;
                bool hidesHair = HidesHair(def);
                bool hidesBeard = HidesBeard(def);
                if (!hidesHair && !hidesBeard)
                {
                    continue;
                }

                hairHiddenByApparel |= hidesHair;
                beardHiddenByApparel |= hidesBeard;
                if (settings.hairHidingHeadgear.Contains(def.defName))
                {
                    keepHair |= hidesHair;
                    keepBeard |= hidesBeard;
                    continue;
                }

                bool full = IsFullHeadgear(def);
                if (hidesHair && !(full ? settings.hairUnderFullHeadgear : settings.hairUnderUpperHeadgear))
                {
                    keepHair = true;
                }

                if (hidesBeard && !settings.beardUnderFullHeadgear)
                {
                    keepBeard = true;
                }
            }

            if (hairSkipped && hairHiddenByApparel && !keepHair)
            {
                parms.skipFlags &= ~hairFlag;
            }

            if (beardSkipped && beardHiddenByApparel && !keepBeard)
            {
                parms.skipFlags &= ~beardFlag;
            }
        }

        public static PawnRenderNode FindHairNode(PawnRenderNode node)
        {
            if (node == null)
            {
                return null;
            }

            if (node is PawnRenderNode_Hair)
            {
                return node;
            }

            if (node.children == null)
            {
                return null;
            }

            foreach (PawnRenderNode child in node.children)
            {
                PawnRenderNode found = FindHairNode(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        public static void RefreshAllPawnGraphics()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
            {
                return;
            }

            foreach (Map map in Find.Maps)
            {
                foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.RaceProps.Humanlike)
                    {
                        pawn.Drawer.renderer.SetAllGraphicsDirty();
                    }
                }
            }

            PortraitsCache.Clear();
        }
    }

    [HarmonyPatchCategory(Features.HairUnderHats)]
    [HarmonyPatch(typeof(PawnRenderTree), "AdjustParms")]
    internal static class PawnRenderTree_AdjustParms_HairUnderHats_Patch
    {
        private static void Postfix(PawnRenderTree __instance, ref PawnDrawParms parms)
        {
            HairUnderHatsUtility.AdjustSkipFlags(__instance.pawn, ref parms);
        }
    }

    [HarmonyPatchCategory(Features.HairUnderHats)]
    [HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.LayerFor))]
    internal static class PawnRenderNodeWorker_LayerFor_HairUnderHats_Patch
    {
        private static void Postfix(PawnRenderNode node, PawnDrawParms parms, ref float __result)
        {
            if (!(node is PawnRenderNode_Apparel apparelNode) || !apparelNode.useHeadMesh || !Yuudong2Settings.Current.headgearAboveHair)
            {
                return;
            }

            PawnRenderNode hairNode = HairUnderHatsUtility.FindHairNode(node.tree?.rootNode);
            if (hairNode == null)
            {
                return;
            }

            float hairLayer = hairNode.Worker.LayerFor(hairNode, parms);
            if (__result <= hairLayer)
            {
                __result = hairLayer + 0.5f;
            }
        }
    }

    [HarmonyPatchCategory(Features.HairUnderHats)]
    [HarmonyPatch(typeof(PawnRenderNode_Apparel), nameof(PawnRenderNode_Apparel.MeshSetFor))]
    internal static class PawnRenderNode_Apparel_MeshSetFor_HairUnderHats_Patch
    {
        private static void Postfix(PawnRenderNode_Apparel __instance, Pawn pawn, ref GraphicMeshSet __result)
        {
            float scale = Yuudong2Settings.Current.headgearScale;
            if (__instance.apparel == null || !__instance.useHeadMesh || Mathf.Abs(scale - 1f) < 0.001f || pawn == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }

            Vector2? overrideSize = __instance.Props.overrideMeshSize;
            __result = overrideSize.HasValue
                ? MeshPool.GetMeshSetForSize(overrideSize.Value.x * scale, overrideSize.Value.y * scale)
                : HumanlikeMeshPoolUtility.GetHumanlikeHeadSetForPawn(pawn, scale, scale);
        }
    }

    internal static class HairUnderHatsSettingsUI
    {
        private const float RowHeight = 26f;

        private static string headgearSearch = "";
        private static string hairSearch = "";
        private static Vector2 headgearScroll;
        private static Vector2 hairScroll;
        private static List<ThingDef> headgearDefs;
        private static List<HairDef> hairDefs;

        public static void Draw(Rect rect, Yuudong2Settings settings)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("Yuudong2.Hair.UnderUpper".Translate(), ref settings.hairUnderUpperHeadgear, "Yuudong2.Hair.UnderUpper.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Hair.UnderFull".Translate(), ref settings.hairUnderFullHeadgear, "Yuudong2.Hair.UnderFull.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Hair.BeardUnderFull".Translate(), ref settings.beardUnderFullHeadgear, "Yuudong2.Hair.BeardUnderFull.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Hair.AboveHair".Translate(), ref settings.headgearAboveHair, "Yuudong2.Hair.AboveHair.Desc".Translate());
            listing.CheckboxLabeled("Yuudong2.Hair.OnlyPlayer".Translate(), ref settings.hairOnlyPlayerPawns, "Yuudong2.Hair.OnlyPlayer.Desc".Translate());
            settings.headgearScale = Mathf.Round(listing.SliderLabeled("Yuudong2.Hair.Scale".Translate(settings.headgearScale.ToStringPercent()), settings.headgearScale, 1f, 1.25f, 0.5f, "Yuudong2.Hair.Scale.Desc".Translate()) * 100f) / 100f;
            listing.GapLine();
            float top = listing.CurHeight;
            listing.End();

            if (headgearDefs == null)
            {
                headgearDefs = DefDatabase<ThingDef>.AllDefsListForReading.Where(HairUnderHatsUtility.IsHeadgear).OrderBy(d => d.label).ToList();
                hairDefs = DefDatabase<HairDef>.AllDefsListForReading.OrderBy(d => d.label).ToList();
            }

            float columnWidth = (rect.width - 20f) / 2f;
            Rect left = new Rect(rect.x, rect.y + top, columnWidth, rect.height - top);
            Rect right = new Rect(left.xMax + 20f, left.y, columnWidth, left.height);
            DrawDefList(left, "Yuudong2.Hair.HeadgearList".Translate(), headgearDefs, settings.hairHidingHeadgear, ref headgearSearch, ref headgearScroll);
            DrawDefList(right, "Yuudong2.Hair.HairList".Translate(), hairDefs, settings.hairHiddenUnderHeadgear, ref hairSearch, ref hairScroll);
        }

        private static void DrawDefList<T>(Rect rect, string title, List<T> defs, HashSet<string> hidden, ref string search, ref Vector2 scroll) where T : Def
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f), title);
            search = Widgets.TextField(new Rect(rect.x, rect.y + 26f, rect.width - 80f, 24f), search);
            if (Widgets.ButtonText(new Rect(rect.xMax - 76f, rect.y + 26f, 76f, 24f), "Yuudong2.Hair.ClearAll".Translate()))
            {
                hidden.Clear();
            }

            string filter = search;
            List<T> shown = string.IsNullOrEmpty(filter)
                ? defs
                : defs.Where(d => (d.label ?? d.defName).IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0
                    || d.defName.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            Rect outRect = new Rect(rect.x, rect.y + 56f, rect.width, rect.height - 56f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, shown.Count * RowHeight);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            for (int i = 0; i < shown.Count; i++)
            {
                T def = shown[i];
                Rect row = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);
                if (row.yMax < scroll.y || row.y > scroll.y + outRect.height)
                {
                    continue;
                }

                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(row);
                }

                bool isHidden = hidden.Contains(def.defName);
                Widgets.CheckboxLabeled(row, def.LabelCap + " (" + def.modContentPack?.Name + ")", ref isHidden);
                if (isHidden)
                {
                    hidden.Add(def.defName);
                }
                else
                {
                    hidden.Remove(def.defName);
                }
            }

            Widgets.EndScrollView();
        }
    }
}

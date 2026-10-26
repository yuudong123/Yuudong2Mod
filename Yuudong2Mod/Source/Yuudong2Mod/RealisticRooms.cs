using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // Realistic Rooms Rewritten 포팅. 방 크기 등급(좁음~매우 넓음)에 필요한 넓이를 낮추고,
    // 실내 흙·모래 오염의 아름다움 감소를 -4로 줄인다. 세부 설정은 재시작 없이 바로 적용된다.
    internal static class RealisticRoomsUtility
    {
        public static readonly string[] StageLabels = { "rather tight", "average-sized", "somewhat spacious", "quite spacious", "very spacious", "extremely spacious" };
        public static readonly float[] DefaultThresholds = { 6.5f, 16.5f, 28.5f, 49.5f, 84.5f, 174.5f };
        private const float FilthBeauty = -4f;
        private static readonly string[] FilthDefs = { "Filth_Dirt", "Filth_Sand" };

        // 오염 설정을 끌 때 되돌릴 원래 아름다움 값 (Def가 원래 값을 가지고 있지 않았으면 null)
        private static Dictionary<string, float?> originalFilthBeauty;

        public static void Apply()
        {
            RoomStatDef space = DefDatabase<RoomStatDef>.GetNamedSilentFail("Space");
            float[] thresholds = Yuudong2Settings.Current.roomSpaceThresholds;
            if (space?.scoreStages != null)
            {
                foreach (RoomStatScoreStage stage in space.scoreStages)
                {
                    int index = System.Array.IndexOf(StageLabels, stage.untranslatedLabel);
                    if (index >= 0)
                    {
                        stage.minScore = thresholds[index];
                    }
                }
            }

            if (originalFilthBeauty == null)
            {
                originalFilthBeauty = new Dictionary<string, float?>();
                foreach (string defName in FilthDefs)
                {
                    StatModifier beauty = FindBeauty(DefDatabase<ThingDef>.GetNamedSilentFail(defName));
                    originalFilthBeauty[defName] = beauty?.value;
                }
            }

            foreach (string defName in FilthDefs)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def == null)
                {
                    continue;
                }

                if (def.statBases == null)
                {
                    def.statBases = new List<StatModifier>();
                }

                StatModifier beauty = FindBeauty(def);
                float? original = originalFilthBeauty[defName];
                if (Yuudong2Settings.Current.roomFilthTweak)
                {
                    if (beauty == null)
                    {
                        def.statBases.Add(new StatModifier { stat = StatDefOf.Beauty, value = FilthBeauty });
                    }
                    else
                    {
                        beauty.value = FilthBeauty;
                    }
                }
                else if (beauty != null)
                {
                    if (original.HasValue)
                    {
                        beauty.value = original.Value;
                    }
                    else
                    {
                        def.statBases.Remove(beauty);
                    }
                }
            }
        }

        private static StatModifier FindBeauty(ThingDef def)
        {
            return def?.statBases?.Find(s => s.stat == StatDefOf.Beauty);
        }

        private static readonly string[] buffers = new string[DefaultThresholds.Length];

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.Rooms.Note".Translate());
            listing.GapLine();
            float[] t = s.roomSpaceThresholds;
            for (int i = 0; i < t.Length; i++)
            {
                // 아래 등급은 위 등급보다 클 수 없다.
                float max = i + 1 < t.Length ? t[i + 1] : 400f;
                Rect row = listing.GetRect(28f);
                Widgets.Label(row.LeftPart(0.4f), ("Yuudong2.Rooms.Stage" + i).Translate());
                Rect right = row.RightPart(0.58f);
                Widgets.TextFieldNumeric(right.RightPart(0.24f), ref t[i], ref buffers[i], 0f, max);
                float slid = Mathf.Min(Widgets.HorizontalSlider(right.LeftPart(0.72f), t[i], 0f, max, true, null, null, null, 0.5f), max);
                if (slid != t[i])
                {
                    t[i] = slid;
                    buffers[i] = null;
                }
                listing.Gap(4f);
            }

            if (listing.ButtonText("Yuudong2.Rooms.Reset".Translate(), null, 0.4f))
            {
                DefaultThresholds.CopyTo(t, 0);
                System.Array.Clear(buffers, 0, buffers.Length);
            }

            listing.GapLine();
            listing.CheckboxLabeled("Yuudong2.Rooms.Filth".Translate(), ref s.roomFilthTweak, "Yuudong2.Rooms.Filth.Desc".Translate());
            listing.End();

            if (Yuudong2Mod.AppliedFeatures.Contains(Features.RealisticRooms))
            {
                Apply();
            }
        }
    }
}

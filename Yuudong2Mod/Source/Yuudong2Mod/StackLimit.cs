using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Yuudong2Mod
{
    // 겹칠 수 있는 아이템(원래 한 칸에 2개 이상 쌓이는 것)의 한 칸 최대 개수에 배수를 곱한다.
    // 설정 창에서 배수를 바꾸면 재시작 없이 바로 다시 곱한다.
    public static class StackLimitUtility
    {
        public const int MaxMultiplier = 20;

        // 처음 곱하기 전의 원래 값. 다시 곱할 때 여기서 시작한다.
        private static readonly Dictionary<ThingDef, int> original = new Dictionary<ThingDef, int>();

        public static int Multiplier => Mathf.Clamp(Yuudong2Settings.Current.stackLimitMultiplier, 1, MaxMultiplier);

        public static int OriginalLimit(ThingDef def)
        {
            return original.TryGetValue(def, out int limit) ? limit : def.stackLimit;
        }

        public static void Apply()
        {
            if (original.Count == 0)
            {
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (def.category == ThingCategory.Item && def.stackLimit > 1)
                    {
                        original[def] = def.stackLimit;
                    }
                }
            }
            int multiplier = Multiplier;
            foreach (KeyValuePair<ThingDef, int> pair in original)
            {
                pair.Key.stackLimit = Mathf.Max(1, (int)Mathf.Min((long)pair.Value * multiplier, int.MaxValue));
            }
        }

        public static void DrawSettings(Rect rect, Yuudong2Settings s)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Yuudong2.StackLimit.Multiplier".Translate(s.stackLimitMultiplier), -1f, "Yuudong2.StackLimit.Multiplier.Desc".Translate());
            s.stackLimitMultiplier = Mathf.RoundToInt(listing.Slider(s.stackLimitMultiplier, 1f, MaxMultiplier));
            listing.End();
        }
    }
}

using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Gloomy 가구 작업대가 대응하는 바닐라 작업대와 같은 레시피와 작업 담당을 갖게 맞춘다.
    // 바닐라와 다른 모드는 레시피를 작업대 목록(recipes)이나 레시피 쪽 recipeUsers로 붙이는데,
    // Gloomy는 일부 작업대만 따라가게 패치해서 이유식·사이카이트 차 같은 레시피가 빠졌다.
    internal static class GloomyWorkbenchSync
    {
        private static readonly Dictionary<string, string> Counterparts = new Dictionary<string, string>
        {
            { "RGK_FueledStove", "FueledStove" },
            { "GL_ElectricStove", "ElectricStove" },
            { "GL_Bonfire", "FueledStove" },
            { "GL_FueledSmithy", "FueledSmithy" },
            { "GL_ElectricSmithy", "ElectricSmithy" },
            { "GL_HandTailoringBench", "HandTailoringBench" },
            { "GL_ElectricTailoringBench", "ElectricTailoringBench" },
            { "GL_TableButcher", "TableButcher" },
        };

        private static readonly AccessTools.FieldRef<ThingDef, List<RecipeDef>> allRecipesCached = AccessTools.FieldRefAccess<ThingDef, List<RecipeDef>>("allRecipesCached");

        public static void Apply()
        {
            int addedRecipes = 0;
            int addedWorkGivers = 0;
            foreach (KeyValuePair<string, string> pair in Counterparts)
            {
                ThingDef gloomy = DefDatabase<ThingDef>.GetNamedSilentFail(pair.Key);
                ThingDef vanilla = DefDatabase<ThingDef>.GetNamedSilentFail(pair.Value);
                if (gloomy == null || vanilla == null)
                {
                    continue;
                }

                List<RecipeDef> have = gloomy.AllRecipes;
                foreach (RecipeDef recipe in vanilla.AllRecipes)
                {
                    if (have.Contains(recipe))
                    {
                        continue;
                    }

                    if (gloomy.recipes == null)
                    {
                        gloomy.recipes = new List<RecipeDef>();
                    }

                    gloomy.recipes.Add(recipe);
                    addedRecipes++;
                }

                allRecipesCached(gloomy) = null;

                foreach (WorkGiverDef workGiver in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                {
                    List<ThingDef> givers = workGiver.fixedBillGiverDefs;
                    if (givers != null && givers.Contains(vanilla) && !givers.Contains(gloomy))
                    {
                        givers.Add(gloomy);
                        addedWorkGivers++;
                    }
                }
            }

            if (addedRecipes > 0 || addedWorkGivers > 0)
            {
                Log.Message("[Yuudong2Mod] Gloomy 작업대에 바닐라 레시피 " + addedRecipes + "개, 작업 담당 " + addedWorkGivers + "곳을 맞췄습니다.");
            }
        }
    }
}

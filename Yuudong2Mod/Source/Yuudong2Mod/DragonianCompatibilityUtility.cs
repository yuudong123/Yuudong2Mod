using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    internal static class DragonianCompatibilityUtility
    {
        private const string DragonianPackageId = "RooAndGloomy.DragonianRaceMod";
        private const string DragonbornGeneDefName = "Dragonian_Dragonborn";

        public static void ApplyDefPatches()
        {
            if (!DragonianActive)
            {
                return;
            }

            GeneDef dragonborn = DefDatabase<GeneDef>.GetNamedSilentFail(DragonbornGeneDefName);
            if (dragonborn?.exclusionTags == null)
            {
                return;
            }

            dragonborn.exclusionTags.Clear();
        }

        private static bool DragonianActive
        {
            get
            {
                return ModsConfig.IsActive(DragonianPackageId) || ModsConfig.IsActive(DragonianPackageId.ToLowerInvariant());
            }
        }
    }
}

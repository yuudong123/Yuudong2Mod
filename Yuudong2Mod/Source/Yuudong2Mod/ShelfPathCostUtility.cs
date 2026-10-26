using Verse;

namespace Yuudong2Mod
{
    internal static class ShelfPathCostUtility
    {
        public static void RemoveShelfPathCosts()
        {
            RemovePathCost("Shelf");
            RemovePathCost("ShelfSmall");
        }

        private static void RemovePathCost(string defName)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                return;
            }

            def.pathCost = 0;
            def.pathCostIgnoreRepeat = false;
            def.passability = Traversability.Standable;
        }
    }
}

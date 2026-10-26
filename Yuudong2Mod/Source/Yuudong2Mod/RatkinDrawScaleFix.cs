using System;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Yuudong2Mod
{
    // NewRatkin의 GeneDrawScaleCache는 일반 Dictionary에 폰별 크기를 저장하는데,
    // 폰 그리기 준비가 여러 스레드에서 동시에 돌면 Dictionary가 깨져 그 뒤로 모든 폰 그리기가 실패한다(폰이 안 보임).
    // 같은 계산을 동시 접근에 안전한 사전으로 대신한다.
    internal static class RatkinDrawScaleFix
    {
        private static readonly ConcurrentDictionary<Pawn, float> cache = new ConcurrentDictionary<Pawn, float>();

        private static Func<Pawn, Gene, float> drawScaleForPawn;

        public static Type CacheType => AccessTools.TypeByName("NewRatkin.GeneDrawScaleCache");

        public static bool Ready()
        {
            if (drawScaleForPawn != null)
            {
                return true;
            }

            MethodInfo method = AccessTools.Method(AccessTools.TypeByName("NewRatkin.Gene_DrawScale"), "DrawScaleForPawn", new[] { typeof(Pawn), typeof(Gene) });
            if (method == null)
            {
                return false;
            }

            drawScaleForPawn = AccessTools.MethodDelegate<Func<Pawn, Gene, float>>(method);
            return true;
        }

        public static float Get(Pawn pawn)
        {
            return cache.GetOrAdd(pawn, p => drawScaleForPawn(p, null));
        }

        // 원본과 같이 새 값을 저장하고, 값이 바뀌었으면 true를 돌려준다.
        public static bool Recalculate(Pawn pawn, out float drawScale)
        {
            drawScale = drawScaleForPawn(pawn, null);
            bool hadOld = cache.TryGetValue(pawn, out float old);
            cache[pawn] = drawScale;
            return hadOld ? Math.Abs(old - drawScale) > 0.0001f : Math.Abs(drawScale - 1f) > 0.0001f;
        }
    }

    [HarmonyPatchCategory(Features.RatkinDrawScaleFix)]
    [HarmonyPatch]
    internal static class GeneDrawScaleCache_Get_Patch
    {
        private static bool Prepare()
        {
            return RatkinDrawScaleFix.CacheType != null && RatkinDrawScaleFix.Ready();
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(RatkinDrawScaleFix.CacheType, "Get", new[] { typeof(Pawn) });
        }

        private static bool Prefix(Pawn pawn, ref float __result)
        {
            if (pawn == null)
            {
                return true;
            }

            __result = RatkinDrawScaleFix.Get(pawn);
            return false;
        }
    }

    [HarmonyPatchCategory(Features.RatkinDrawScaleFix)]
    [HarmonyPatch]
    internal static class GeneDrawScaleCache_Recalculate_Patch
    {
        private static bool Prepare()
        {
            return RatkinDrawScaleFix.CacheType != null && RatkinDrawScaleFix.Ready();
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(RatkinDrawScaleFix.CacheType, "Recalculate", new[] { typeof(Pawn), typeof(float).MakeByRefType() });
        }

        private static bool Prefix(Pawn pawn, ref float drawScale, ref bool __result)
        {
            if (pawn == null)
            {
                return true;
            }

            __result = RatkinDrawScaleFix.Recalculate(pawn, out drawScale);
            return false;
        }
    }
}

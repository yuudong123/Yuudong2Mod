using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 바닐라 문은 '자유 통행'(열려 있고 곧 닫히지 않음) 상태가 바뀔 때마다 맵 전체 도달 가능 캐시를 비운다.
    // 정착민이 문을 지날 때마다 일어나서, 작업 찾기에서 쓰는 도달 가능 검사가 캐시를 거의 못 쓰고 지역을 다시 훑는다.
    // 자유 통행 여부로 결과가 바뀌는 항목(그 문을 열 수 없는 폰, 폰 없는 검사, 닫힌 문을 못 지나는 이동 방식)만 지운다.
    // 문을 열 수 있는 폰은 문이 열렸든 닫혔든 지나갈 수 있으므로 그 항목은 그대로 둔다.
    internal static class DoorReachabilityCacheUtility
    {
        private static readonly AccessTools.FieldRef<Reachability, ReachabilityCache> CacheOf = AccessTools.FieldRefAccess<Reachability, ReachabilityCache>("cache");
        private static readonly AccessTools.FieldRef<Building_Door, bool> FreePassageWhenCleared = AccessTools.FieldRefAccess<Building_Door, bool>("freePassageWhenClearedReachabilityCache");
        private static readonly Action<ReachabilityCache, Building_Door> RemoveAffected = BuildRemoveAffected();

        public static void OnFreePassageChanged(Building_Door door, Map map)
        {
            ReachabilityCache cache = CacheOf(map.reachability);
            if (RemoveAffected == null || cache == null)
            {
                map.reachability.ClearCache();
            }
            else
            {
                RemoveAffected(cache, door);
            }

            FreePassageWhenCleared(door) = door.FreePassage;
        }

        public static bool DependsOnFreePassage(TraverseParms parms, Building_Door door)
        {
            switch (parms.mode)
            {
                case TraverseMode.ByPawn:
                    Pawn pawn = parms.pawn;
                    return pawn == null || !pawn.Spawned || !door.PawnCanOpen(pawn);
                case TraverseMode.NoPassClosedDoors:
                case TraverseMode.NoPassClosedDoorsOrWater:
                    return true;
                default:
                    return false;
            }
        }

        // 캐시 키가 비공개 구조체라서, 키 형식에 맞춘 제네릭 메서드를 한 번 만들어 둔다. 실패하면 null을 돌려 바닐라처럼 전체를 비운다.
        private static Action<ReachabilityCache, Building_Door> BuildRemoveAffected()
        {
            try
            {
                FieldInfo dictField = AccessTools.Field(typeof(ReachabilityCache), "cacheDict");
                Type keyType = dictField.FieldType.GetGenericArguments()[0];
                PropertyInfo parmsProperty = keyType.GetProperty("TraverseParms");
                ParameterExpression key = Expression.Parameter(keyType);
                Delegate getter = Expression.Lambda(typeof(Func<,>).MakeGenericType(keyType, typeof(TraverseParms)), Expression.Property(key, parmsProperty), key).Compile();

                MethodInfo create = typeof(KeyAccess<>).MakeGenericType(keyType).GetMethod(nameof(KeyAccess<int>.Create));
                return (Action<ReachabilityCache, Building_Door>)create.Invoke(null, new object[] { dictField, getter });
            }
            catch (Exception e)
            {
                Log.Warning($"[Yuudong2Mod] Door reachability cache: falling back to full clears ({e.Message})");
                return null;
            }
        }

        private static class KeyAccess<TKey>
        {
            private static FieldInfo dictFieldInfo;
            private static Func<TKey, TraverseParms> parmsOf;

            private static readonly List<TKey> Affected = new List<TKey>();

            public static Action<ReachabilityCache, Building_Door> Create(FieldInfo dictField, Delegate parms)
            {
                dictFieldInfo = dictField;
                parmsOf = (Func<TKey, TraverseParms>)parms;
                return RemoveAffected;
            }

            private static void RemoveAffected(ReachabilityCache cache, Building_Door door)
            {
                Dictionary<TKey, bool> dict = (Dictionary<TKey, bool>)dictFieldInfo.GetValue(cache);
                Affected.Clear();
                foreach (KeyValuePair<TKey, bool> entry in dict)
                {
                    if (DependsOnFreePassage(parmsOf(entry.Key), door))
                    {
                        Affected.Add(entry.Key);
                    }
                }

                for (int i = 0; i < Affected.Count; i++)
                {
                    dict.Remove(Affected[i]);
                }

                Affected.Clear();
            }
        }
    }

    // 문이 생기거나 사라지거나 세력이 바뀔 때의 전체 비우기는 그대로 두고, 매 틱 자유 통행 변화를 보는 곳만 바꾼다.
    [HarmonyPatchCategory(Features.DoorReachabilityCache)]
    [HarmonyPatch(typeof(Building_Door), "Tick")]
    internal static class Building_Door_Tick_DoorReachabilityCache_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo clear = AccessTools.Method(typeof(Building_Door), "ClearReachabilityCache");
            MethodInfo replacement = AccessTools.Method(typeof(DoorReachabilityCacheUtility), nameof(DoorReachabilityCacheUtility.OnFreePassageChanged));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(clear))
                {
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    replaced++;
                    continue;
                }

                yield return instruction;
            }

            if (replaced != 1)
            {
                throw new InvalidOperationException("Building_Door.ClearReachabilityCache call not found in Building_Door.Tick");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    // 청소·수확·칠하기는 주변 대상을 묶어서 한 작업으로 만들고, 출발점에서의 거리 순서로 한 번만 정렬한다.
    // 출발점을 중심으로 한 동심원 순서라서 거리가 비슷한 양쪽 대상을 지그재그로 오간다.
    // 가까운 것부터 잇는 순서를 2-opt로 다듬고, 바닐라 순서와 총 이동 거리를 비교해 짧은 쪽을 쓴다.
    internal static class WorkRouteOrderUtility
    {
        private const int MaxTwoOptPasses = 50;

        private static int[] bestOrder = new int[0];
        private static int[] candidate = new int[0];
        private static bool[] used = new bool[0];

        // xs·zs는 대상 위치, order는 바닐라 순서(대상 번호)다. 더 짧은 순서를 찾으면 order를 고쳐 쓰고 true를 돌려준다.
        public static bool Optimize(int startX, int startZ, int[] xs, int[] zs, int[] order)
        {
            int n = order.Length;
            if (n < 3)
            {
                return false;
            }

            EnsureCapacity(n);
            float vanilla = Length(startX, startZ, xs, zs, order, n);
            float best = vanilla;
            Array.Copy(order, bestOrder, n);

            // 1. 바닐라 순서를 2-opt로 다듬는다.
            Array.Copy(order, candidate, n);
            float length = TwoOpt(startX, startZ, xs, zs, candidate, n);
            if (length < best)
            {
                best = length;
                Array.Copy(candidate, bestOrder, n);
            }

            // 2. 지금 위치에서 가장 가까운 것부터 이은 순서를 2-opt로 다듬는다.
            NearestNeighbor(startX, startZ, xs, zs, candidate, n);
            length = TwoOpt(startX, startZ, xs, zs, candidate, n);
            if (length < best)
            {
                best = length;
                Array.Copy(candidate, bestOrder, n);
            }

            if (best >= vanilla - 0.01f)
            {
                return false;
            }

            Array.Copy(bestOrder, order, n);
            return true;
        }

        public static float Length(int startX, int startZ, int[] xs, int[] zs, int[] order, int n)
        {
            float total = 0f;
            int x = startX;
            int z = startZ;
            for (int i = 0; i < n; i++)
            {
                int j = order[i];
                total += Distance(x, z, xs[j], zs[j]);
                x = xs[j];
                z = zs[j];
            }

            return total;
        }

        private static void EnsureCapacity(int n)
        {
            if (bestOrder.Length < n)
            {
                bestOrder = new int[n];
                candidate = new int[n];
                used = new bool[n];
            }
        }

        private static float Distance(int x1, int z1, int x2, int z2)
        {
            int dx = x1 - x2;
            int dz = z1 - z2;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private static void NearestNeighbor(int startX, int startZ, int[] xs, int[] zs, int[] result, int n)
        {
            Array.Clear(used, 0, n);
            int x = startX;
            int z = startZ;
            for (int i = 0; i < n; i++)
            {
                int nearest = -1;
                int nearestDistance = int.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    int dx = xs[j] - x;
                    int dz = zs[j] - z;
                    int distance = dx * dx + dz * dz;
                    if (distance < nearestDistance)
                    {
                        nearest = j;
                        nearestDistance = distance;
                    }
                }

                used[nearest] = true;
                result[i] = nearest;
                x = xs[nearest];
                z = zs[nearest];
            }
        }

        // 출발점이 고정되고 돌아오지 않는 경로에서, 구간을 뒤집어 짧아지면 뒤집는다.
        private static float TwoOpt(int startX, int startZ, int[] xs, int[] zs, int[] route, int n)
        {
            for (int pass = 0; pass < MaxTwoOptPasses; pass++)
            {
                bool improved = false;
                for (int i = 0; i < n - 1; i++)
                {
                    int prevX = i == 0 ? startX : xs[route[i - 1]];
                    int prevZ = i == 0 ? startZ : zs[route[i - 1]];
                    int a = route[i];
                    for (int k = i + 1; k < n; k++)
                    {
                        int b = route[k];
                        float delta = Distance(prevX, prevZ, xs[b], zs[b]) - Distance(prevX, prevZ, xs[a], zs[a]);
                        if (k < n - 1)
                        {
                            int next = route[k + 1];
                            delta += Distance(xs[a], zs[a], xs[next], zs[next]) - Distance(xs[b], zs[b], xs[next], zs[next]);
                        }

                        if (delta < -0.01f)
                        {
                            Array.Reverse(route, i, k - i + 1);
                            a = route[i];
                            improved = true;
                        }
                    }
                }

                if (!improved)
                {
                    break;
                }
            }

            return Length(startX, startZ, xs, zs, route, n);
        }
    }

    [HarmonyPatchCategory(Features.WorkRouteOrder)]
    [HarmonyPatch]
    internal static class WorkRouteOrder_Patch
    {
        private static int[] xs = new int[0];
        private static int[] zs = new int[0];
        private static int[] order = new int[0];
        private static readonly List<LocalTargetInfo> Reordered = new List<LocalTargetInfo>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorkGiver_CleanFilth), nameof(WorkGiver_CleanFilth.JobOnThing));
            yield return AccessTools.Method(typeof(WorkGiver_GrowerHarvest), nameof(WorkGiver_GrowerHarvest.JobOnCell));
            yield return AccessTools.Method(typeof(WorkGiver_PaintBuilding), nameof(WorkGiver_PaintBuilding.JobOnThing));
            yield return AccessTools.Method(typeof(WorkGiver_PaintFloor), nameof(WorkGiver_PaintFloor.JobOnCell));
            yield return AccessTools.Method(typeof(WorkGiver_RemovePaintBuilding), nameof(WorkGiver_RemovePaintBuilding.JobOnThing));
            yield return AccessTools.Method(typeof(WorkGiver_RemovePaintFloor), nameof(WorkGiver_RemovePaintFloor.JobOnCell));
        }

        private static void Postfix(Job __result, Pawn pawn)
        {
            List<LocalTargetInfo> queue = __result?.targetQueueA;
            if (queue == null || queue.Count < 3 || pawn == null)
            {
                return;
            }

            int n = queue.Count;
            if (xs.Length < n)
            {
                xs = new int[n];
                zs = new int[n];
            }

            if (order.Length != n)
            {
                order = new int[n];
            }

            for (int i = 0; i < n; i++)
            {
                IntVec3 cell = queue[i].Cell;
                xs[i] = cell.x;
                zs[i] = cell.z;
                order[i] = i;
            }

            IntVec3 start = pawn.Position;
            if (!WorkRouteOrderUtility.Optimize(start.x, start.z, xs, zs, order))
            {
                return;
            }

            Reordered.Clear();
            for (int i = 0; i < n; i++)
            {
                Reordered.Add(queue[order[i]]);
            }

            queue.Clear();
            queue.AddRange(Reordered);
            Reordered.Clear();
        }
    }
}

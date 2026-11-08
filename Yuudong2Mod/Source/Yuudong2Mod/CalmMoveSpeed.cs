using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Yuudong2Mod
{
    // 급할 것 없는 사람 폰은 이동 속도가 종족 기본 속도의 120%를 넘지 않는다. 생체공학 다리·조깅 특성·민첩 등으로 빨라져도 평소엔 이 속도로 걷는다.
    // 소집 중, 본인이 전투 중(최근 공격했거나 다침, 전투·사냥 작업), 도망·광폭, 구조·불 끄기·출혈 환자 치료, 플레이어가 직접 시킨 일, Allow Tool 긴급 운반·마무리처럼 급한 일에는 원래 속도를 쓴다.
    // 적대 세력 폰과 동물은 건드리지 않는다. 스탯 창의 이동 속도는 원래 값 그대로 보인다.
    internal static class CalmMoveSpeedUtility
    {
        private const float SpeedLimitFactor = 1.2f;
        private const int RecentCombatTicks = 1800;

        private static HashSet<JobDef> urgentJobs;

        public static float SlowdownFactor(Pawn pawn)
        {
            if (!pawn.RaceProps.Humanlike || pawn.Drafted || pawn.Downed || !pawn.Spawned || pawn.HostileTo(Faction.OfPlayer))
            {
                return 1f;
            }

            float speed = pawn.GetStatValue(StatDefOf.MoveSpeed);
            float limit = pawn.def.GetStatValueAbstract(StatDefOf.MoveSpeed) * SpeedLimitFactor;
            if (limit <= 0f || speed <= limit || IsUrgent(pawn))
            {
                return 1f;
            }

            return speed / limit;
        }

        private static bool IsUrgent(Pawn pawn)
        {
            Pawn_MindState mind = pawn.mindState;
            int now = Find.TickManager.TicksGame;
            if (mind != null && (mind.enemyTarget != null || now - mind.lastHarmTick < RecentCombatTicks || now - mind.lastAttackTargetTick < RecentCombatTicks))
            {
                return true;
            }

            MentalStateDef mentalState = pawn.MentalStateDef;
            if (mentalState != null && (mentalState.IsAggro || mentalState == MentalStateDefOf.PanicFlee))
            {
                return true;
            }

            Job job = pawn.CurJob;
            if (job == null)
            {
                return false;
            }

            // 플레이어가 우클릭 메뉴로 직접 시킨 일(거래, 대화, 우선 작업 등)도 급한 일로 본다.
            if (job.playerForced || job.locomotionUrgency == LocomotionUrgency.Sprint || UrgentJobs.Contains(job.def))
            {
                return true;
            }

            // Allow Tool의 긴급 운반·마무리 일격은 일반 운반·공격과 작업 종류가 같아서 작업을 준 작업 유형으로 구별한다.
            string workType = job.workGiverDef?.workType?.defName;
            if (workType == "HaulingUrgent" || workType == "FinishingOff")
            {
                return true;
            }

            return job.def == JobDefOf.TendPatient && job.targetA.Pawn is Pawn patient && patient.health.hediffSet.BleedRateTotal > 0f;
        }

        private static HashSet<JobDef> UrgentJobs
        {
            get
            {
                if (urgentJobs == null)
                {
                    urgentJobs = new HashSet<JobDef>
                    {
                        JobDefOf.AttackMelee, JobDefOf.AttackStatic, JobDefOf.Wait_Combat, JobDefOf.Hunt,
                        JobDefOf.Flee, JobDefOf.FleeAndCower, JobDefOf.Rescue,
                        JobDefOf.BeatFire, JobDefOf.ExtinguishSelf, JobDefOf.ExtinguishFiresNearby,
                    };
                    urgentJobs.Remove(null);
                }

                return urgentJobs;
            }
        }
    }

    [HarmonyPatchCategory(Features.CalmMoveSpeed)]
    [HarmonyPatch(typeof(Pawn), "TicksPerMove")]
    internal static class Pawn_TicksPerMove_CalmMoveSpeed_Patch
    {
        private static void Postfix(Pawn __instance, ref float __result)
        {
            float factor = CalmMoveSpeedUtility.SlowdownFactor(__instance);
            if (factor > 1f)
            {
                __result = UnityEngine.Mathf.Min(__result * factor, 450f);
            }
        }
    }
}

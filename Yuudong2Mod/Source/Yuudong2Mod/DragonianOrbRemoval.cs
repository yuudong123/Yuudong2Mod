using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Yuudong2Mod
{
    // DragonianMix의 보주는 드라고니안 종족 모드의 기본 드라고니안 보주만 남기고 변형 보주와 실드 모듈을 지웠다.
    // 지운 보주의 ThingDef·HediffDef는 세이브를 불러오기 위한 껍데기(DragonianMix/1.7/Defs/DRM_LegacyOrbs.xml)로만 남고,
    // 불러온 직후 기본 보주로 바뀐다. 바닐라 호환 변환기는 모드 목록이 그대로인 세이브에서는 돌지 않아서 여기서 직접 처리한다.
    public static class DragonianOrbRemoval
    {
        private const string BaseOrb = "DragonianOrb";

        private static readonly string[] OrbKinds = { "Fake", "Flare", "Thunder", "Frozen", "Hyperheal", "Harden", "Shield", "Ender", "Acidifier", "Necro", "Toxic" };
        private static readonly string[] ShieldModules = { "Booster", "AdvancedBooster", "AdaptiveModule", "BufferModule" };

        // 껍데기로 남긴 Def. 아이템과 이식 헤디프가 같은 이름을 쓴다(가짜 보주는 헤디프만 있다).
        internal static readonly HashSet<string> LegacyNames = new HashSet<string>(
            OrbKinds.Select(kind => kind + "DragonianOrb").Concat(ShieldModules.Select(module => "ShieldDragonianOrb_" + module)));

        // 완전히 지운 Def
        internal static readonly HashSet<string> RemovedRecipes = new HashSet<string>(
            OrbKinds.Select(kind => "Install" + kind + "DragonianOrb").Concat(ShieldModules.SelectMany(module => new[]
            {
                "InstallShieldDragonianOrb_" + module,
                "RemoveShieldDragonianOrb_" + module,
                "Make_ShieldDragonianOrb_" + module,
            })));

        internal static readonly HashSet<string> RemovedResearch = new HashSet<string> { "DragonianShieldModule" };

        // 세이브에 남은 지운 Def 참조를 오류 없이 null로 읽게 한다.
        public static void RegisterRemovedDefs()
        {
            var removedDefs = (List<Tuple<string, Type>>)AccessTools.Field(typeof(BackCompatibility), "RemovedDefs").GetValue(null);
            removedDefs.AddRange(RemovedRecipes.Select(name => Tuple.Create(name, typeof(RecipeDef))));
            removedDefs.AddRange(RemovedResearch.Select(name => Tuple.Create(name, typeof(ResearchProjectDef))));
            removedDefs.Add(Tuple.Create("DROrbs", typeof(ThingCategoryDef)));
        }

        internal static void RemoveChildren(XmlNode list, Func<XmlElement, bool> predicate)
        {
            if (list == null)
            {
                return;
            }

            foreach (XmlElement li in list.ChildNodes.OfType<XmlElement>().Where(predicate).ToList())
            {
                list.RemoveChild(li);
            }
        }

        // 저장된 사전은 keys와 values 목록이 같은 순서로 짝을 이룬다.
        internal static void RemoveDictionaryEntries(XmlNode dict, HashSet<string> keys)
        {
            List<XmlElement> keyNodes = dict?["keys"]?.ChildNodes.OfType<XmlElement>().ToList();
            List<XmlElement> valueNodes = dict?["values"]?.ChildNodes.OfType<XmlElement>().ToList();
            if (keyNodes == null || valueNodes == null || keyNodes.Count != valueNodes.Count)
            {
                return;
            }

            for (int i = 0; i < keyNodes.Count; i++)
            {
                if (keys.Contains(keyNodes[i].InnerText))
                {
                    keyNodes[i].ParentNode.RemoveChild(keyNodes[i]);
                    valueNodes[i].ParentNode.RemoveChild(valueNodes[i]);
                }
            }
        }

        public static void ReplaceLegacyOrbs()
        {
            ThingDef baseOrb = DefDatabase<ThingDef>.GetNamedSilentFail(BaseOrb);
            var legacyThings = new HashSet<ThingDef>(LegacyNames.Select(name => DefDatabase<ThingDef>.GetNamedSilentFail(name)).Where(def => def != null));
            var legacyHediffs = new HashSet<HediffDef>(LegacyNames.Select(name => DefDatabase<HediffDef>.GetNamedSilentFail(name)).Where(def => def != null));
            if (baseOrb == null || (legacyThings.Count == 0 && legacyHediffs.Count == 0))
            {
                return;
            }

            var things = new HashSet<Thing>();
            var buffer = new List<Thing>();
            void Collect(IThingHolder holder)
            {
                ThingOwnerUtility.GetAllThingsRecursively(holder, buffer);
                things.AddRange(buffer);
            }

            foreach (Map map in Find.Maps)
            {
                Collect(map);
            }

            foreach (WorldObject worldObject in Find.WorldObjects.AllWorldObjects)
            {
                if (worldObject is IThingHolder holder)
                {
                    Collect(holder);
                }

                if (worldObject is Settlement settlement && settlement.trader != null)
                {
                    Collect(settlement.trader);
                }
            }

            var pawns = new HashSet<Pawn>(PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead);
            foreach (Pawn pawn in Find.WorldPawns.AllPawnsAliveOrDead)
            {
                Collect(pawn);
            }

            pawns.AddRange(things.OfType<Pawn>());

            int implants = 0;
            int items = 0;
            foreach (Pawn pawn in pawns)
            {
                List<Hediff> hediffs = pawn.health?.hediffSet?.hediffs.Where(h => legacyHediffs.Contains(h.def)).ToList();
                if (!hediffs.NullOrEmpty())
                {
                    // 변형 보주를 빼면 그 부위는 타고난 드라고니안 보주로 돌아간다.
                    foreach (Hediff hediff in hediffs)
                    {
                        pawn.health.hediffSet.hediffs.Remove(hediff);
                    }

                    implants += hediffs.Count;
                    pawn.health.hediffSet.DirtyCache();
                    pawn.abilities?.Notify_TemporaryAbilitiesChanged();
                    pawn.GetComp<DragonianMix.DRMCompShield>()?.Notify_ModulesChanged();
                }

                // 입은 채로는 기본 보주(옷이 아님)로 바꿀 수 없으니 소지품으로 넣는다.
                List<Apparel> worn = pawn.apparel?.WornApparel.Where(a => legacyThings.Contains(a.def)).ToList();
                if (!worn.NullOrEmpty())
                {
                    foreach (Apparel apparel in worn)
                    {
                        int count = apparel.stackCount;
                        pawn.apparel.Remove(apparel);
                        apparel.Destroy();
                        foreach (Thing orb in MakeOrbs(baseOrb, count))
                        {
                            if (!(pawn.inventory?.innerContainer.TryAdd(orb) ?? false))
                            {
                                PlaceNearOrDiscard(orb, pawn);
                            }
                        }

                        items += count;
                    }
                }
            }

            foreach (Thing thing in things.Where(t => !t.Destroyed && legacyThings.Contains(t.def)).ToList())
            {
                items += thing.stackCount;
                ReplaceThing(thing, baseOrb);
            }

            if (implants + items > 0)
            {
                Log.Message($"[Yuudong2Mod] 지운 보주를 드라고니안 보주로 바꿨습니다. 이식 {implants}개, 물건 {items}개.");
            }
        }

        private static void ReplaceThing(Thing thing, ThingDef baseOrb)
        {
            int count = thing.stackCount;
            if (thing.Spawned)
            {
                IntVec3 cell = thing.Position;
                Map map = thing.Map;
                bool forbidden = thing.IsForbidden(Faction.OfPlayer);
                thing.Destroy();
                foreach (Thing orb in MakeOrbs(baseOrb, count))
                {
                    if (GenPlace.TryPlaceThing(orb, cell, map, ThingPlaceMode.Near) && forbidden)
                    {
                        orb.SetForbidden(true, warnOnFail: false);
                    }
                }

                return;
            }

            ThingOwner owner = thing.holdingOwner;
            owner?.Remove(thing);
            thing.Destroy();
            foreach (Thing orb in MakeOrbs(baseOrb, count))
            {
                if (owner == null || !owner.TryAdd(orb))
                {
                    PlaceNearOrDiscard(orb, owner?.Owner);
                }
            }
        }

        private static IEnumerable<Thing> MakeOrbs(ThingDef baseOrb, int count)
        {
            while (count > 0)
            {
                Thing orb = ThingMaker.MakeThing(baseOrb);
                orb.stackCount = Math.Min(count, baseOrb.stackLimit);
                count -= orb.stackCount;
                yield return orb;
            }
        }

        private static void PlaceNearOrDiscard(Thing orb, IThingHolder near)
        {
            Map map = near == null ? null : ThingOwnerUtility.GetRootMap(near);
            if (map == null || !GenPlace.TryPlaceThing(orb, ThingOwnerUtility.GetRootPosition(near), map, ThingPlaceMode.Near))
            {
                orb.Destroy();
            }
        }
    }

    // 껍데기 이식 헤디프의 능력은 불러오지 않는다. 1.7은 헤디프의 능력을 생성자 인자 없이 만드는데,
    // 변형 보주가 주던 Psycast에는 그런 생성자가 없어 빈 능력이 남고 그 폰을 고를 때마다 오류가 났다.
    [HarmonyPatchCategory(Features.DragonianMix)]
    [HarmonyPatch(typeof(Hediff), nameof(Hediff.ExposeData))]
    internal static class Hediff_ExposeData_LegacyOrb_Patch
    {
        private static void Prefix()
        {
            if (Scribe.mode != LoadSaveMode.LoadingVars)
            {
                return;
            }

            XmlNode node = Scribe.loader.curXmlParent;
            XmlNode abilities = node?["abilities"];
            if (abilities != null && DragonianOrbRemoval.LegacyNames.Contains(node["def"]?.InnerText))
            {
                node.RemoveChild(abilities);
            }
        }
    }

    // 지운 수술·제작 레시피로 걸어 둔 작업 지시서를 미리 빼서 "레시피가 없는 작업 지시서" 오류를 막는다.
    [HarmonyPatchCategory(Features.DragonianMix)]
    [HarmonyPatch(typeof(BillStack), nameof(BillStack.ExposeData))]
    internal static class BillStack_ExposeData_LegacyOrb_Patch
    {
        private static void Prefix()
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                DragonianOrbRemoval.RemoveChildren(Scribe.loader.curXmlParent?["bills"],
                    li => DragonianOrbRemoval.RemovedRecipes.Contains(li["recipe"]?.InnerText));
            }
        }
    }

    // 지운 연구의 진행도를 미리 빼서 "사전에 null 키" 오류를 막는다.
    [HarmonyPatchCategory(Features.DragonianMix)]
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.ExposeData))]
    internal static class ResearchManager_ExposeData_LegacyOrb_Patch
    {
        private static void Prefix()
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                DragonianOrbRemoval.RemoveDictionaryEntries(Scribe.loader.curXmlParent?["progress"], DragonianOrbRemoval.RemovedResearch);
            }
        }
    }

    // 불러오기는 별도 스레드에서 돌아서, 물건을 만들고 놓는 일은 불러오기가 끝난 뒤 주 스레드에서 한다.
    [HarmonyPatchCategory(Features.DragonianMix)]
    [HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
    internal static class Game_LoadGame_LegacyOrb_Patch
    {
        private static void Postfix()
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    DragonianOrbRemoval.ReplaceLegacyOrbs();
                }
                catch (Exception ex)
                {
                    Log.Error("[Yuudong2Mod] 지운 보주를 드라고니안 보주로 바꾸지 못했습니다: " + ex);
                }
            });
        }
    }
}

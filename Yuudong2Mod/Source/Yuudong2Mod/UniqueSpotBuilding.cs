using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // 맵마다 하나만 존재하는 지정 지점. 새로 놓으면 기존 지점을 제거한다.
    public class UniqueSpotBuilding : Building
    {
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (respawningAfterLoad)
            {
                return;
            }

            List<Building> existing = new List<Building>(map.listerBuildings.AllBuildingsColonistOfDef(def));
            foreach (Building building in existing)
            {
                if (building != this && !building.Destroyed)
                {
                    building.Destroy();
                    Messages.Message("Yuudong2.SpotMoved".Translate(def.LabelCap), this, MessageTypeDefOf.NeutralEvent, false);
                }
            }
        }
    }
}

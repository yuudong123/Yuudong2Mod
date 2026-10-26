using System.Xml;
using Verse;

namespace Yuudong2Mod
{
    // 설정에서 기능이 켜져 있으면 enabled, 꺼져 있으면 disabled 작업을 적용하는 XML 패치 작업.
    public class PatchOperationFeature : PatchOperation
    {
        // XML에서 값을 채운다.
#pragma warning disable CS0649
        private string feature;
        private PatchOperation enabled;
        private PatchOperation disabled;
#pragma warning restore CS0649

        protected override bool ApplyWorker(XmlDocument xml)
        {
            PatchOperation operation = Features.Enabled(feature) ? enabled : disabled;
            return operation == null || operation.Apply(xml);
        }
    }
}

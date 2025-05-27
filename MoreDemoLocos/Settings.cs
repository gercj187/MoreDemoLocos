using UnityModManagerNet;

namespace MoreDemoLocos
{
    public class Settings : UnityModManager.ModSettings
    {
        public int locoCountPerType = 1;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}

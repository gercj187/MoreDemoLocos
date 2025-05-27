using UnityModManagerNet;
using UnityEngine;
using HarmonyLib;

namespace MoreDemoLocos
{
    public static class Main
    {
        public static Settings settings;
        public static UnityModManager.ModEntry.ModLogger logger;

        static bool Load(UnityModManager.ModEntry modEntry)
        {
            logger = modEntry.Logger;
            settings = Settings.Load<Settings>(modEntry);

            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;

            var harmony = new Harmony("com.cj187.moredemolocos");
            harmony.PatchAll();

            logger.Log("[MoreDemoLocos] Mod geladen und Harmony-Patches angewendet.");

            return true;
        }

        static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label($"Amount of restoration locos each type: {settings.locoCountPerType}");
            settings.locoCountPerType = (int)GUILayout.HorizontalSlider(settings.locoCountPerType, 1, 10);
        }

        static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            settings.Save(modEntry);
        }

        public static void StartManager()
        {
            logger.Log($"[MoreDemoLocos] Start with {settings.locoCountPerType} Locos each Type.");

            GameObject obj = new GameObject("CJ_MultiRestorationManager");
            Object.DontDestroyOnLoad(obj);
            obj.AddComponent<MultiRestorationManager>();
        }
    }
}

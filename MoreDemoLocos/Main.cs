using UnityModManagerNet;
using UnityEngine;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace MoreDemoLocos
{
    public static class Main
    {
        public static JObject SaveRoot;
        public static UnityModManager.ModEntry Mod;
        public static Settings Settings;
        public static MultiRestorationManager Manager;
        public static SaveGameData CurrentSaveGame;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);

            modEntry.OnGUI = Settings.Draw;
            modEntry.OnSaveGUI = Settings.Save;

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll();

            if (Manager == null)
            {
                var go = new GameObject("MoreDemoLocos_Manager");
                Object.DontDestroyOnLoad(go);
                Manager = go.AddComponent<MultiRestorationManager>();
            }

            Log("Loaded.");
            return true;
        }

        public static void RequestSpawn(string liveryId, int maxCount)
        {
            if (!Settings.EnableDebug)
                return;

            if (Manager == null)
            {
                LogDebug("Spawn request ignored: Manager not initialized.");
                return;
            }

            LogDebug($"Manual spawn request: {liveryId}");
            Manager.SpawnForType(liveryId, maxCount);
        }

        // ============================================
        // LOGGING
        // ============================================

        public static void Log(string msg)
		{
			Mod.Logger.Log(msg);
		}

		public static void LogDebug(string msg)
		{
			if (!Settings.EnableDebug)
				return;

			Mod.Logger.Log(msg);
		}
    }
}
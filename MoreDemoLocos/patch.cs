using HarmonyLib;

[HarmonyPatch(typeof(StartGameData_NewCareer), "Initialize")]
public static class Patch_StartGameData_NewCareer_Initialize
{
    private static bool alreadyStarted = false;

    static void Postfix()
    {
        if (alreadyStarted) return;
        alreadyStarted = true;

        //MoreDemoLocos.Main.logger.Log("[MoreDemoLocos] Neue Karriere erkannt (via Initialize) – Manager wird gestartet.");
        MoreDemoLocos.Main.StartManager();
    }
}

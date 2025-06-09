using HarmonyLib;
using DV.LocoRestoration;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.JObjectExtstensions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MoreDemoLocos
{
    [HarmonyPatch(typeof(StartGameData_NewCareer), "Initialize")]
    public static class Patch_StartGameData_NewCareer_Initialize
    {
        private static bool alreadyStarted = false;

        static void Postfix()
        {
            if (alreadyStarted) return;
            alreadyStarted = true;

            Main.logger.Log(" New Career started! Load UMM-Settings...");
            Main.StartManager();
        }
    }
}

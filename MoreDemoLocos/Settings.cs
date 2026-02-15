using UnityModManagerNet;
using UnityEngine;
using DV.LocoRestoration;

namespace MoreDemoLocos
{
    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        // =========================
        // DEBUG TOGGLE
        // =========================
        public bool EnableDebug = false;
        public bool ShowKnownIssues = false;
		private Vector2 knownIssuesScroll;

        // =========================
        // Max clone counts per loco
        // =========================
        public int MaxDE2 = 4;
        public int MaxDM3 = 5;
        public int MaxDH4 = 2;
        public int MaxDE6 = 1;
        public int MaxS060 = 3;
        public int MaxS282 = 1;

        // =========================
        // Respawn trigger selection
        // =========================
        public enum RespawnTriggerState
        {
            RerailedCars,
            PartInstalled,
            LocoServiced,
            PaintJobDone
        }

        public RespawnTriggerState respawnTrigger = RespawnTriggerState.LocoServiced;

        public static LocoRestorationController.RestorationState
            ToRestorationState(RespawnTriggerState trigger)
        {
            switch (trigger)
            {
                case RespawnTriggerState.RerailedCars:
                    return LocoRestorationController.RestorationState.S3_RerailedCars;

                case RespawnTriggerState.PartInstalled:
                    return LocoRestorationController.RestorationState.S8_PartInstalled;

                case RespawnTriggerState.LocoServiced:
                    return LocoRestorationController.RestorationState.S9_LocoServiced;

                default:
                    return LocoRestorationController.RestorationState.S10_PaintJobDone;
            }
        }

        // =========================
        // UI
        // =========================
        public void Draw(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("More Demo Locos – Restoration Clones", GUILayout.Height(25));

            // ---- Clone limits ----
            DrawSlider("DE2", ref MaxDE2);
            DrawSlider("DM3", ref MaxDM3);
            DrawSlider("DH4", ref MaxDH4);
            DrawSlider("DE6", ref MaxDE6);
            DrawSlider("S060", ref MaxS060);
            DrawSlider("S282", ref MaxS282);

            GUILayout.Space(10);

            // ---- Respawn trigger ----
            GUILayout.Label("Respawn Trigger (Restoration Milestone)");

            GUILayout.BeginHorizontal();
			if (EnableDebug)
			{
				DrawStateButton("DEBUG Rerailed (S3)", RespawnTriggerState.RerailedCars);
				DrawStateButton("DEBUG Part Installed (S8)", RespawnTriggerState.PartInstalled);
			}
			DrawStateButton("Loco Serviced (S9)", RespawnTriggerState.LocoServiced);
			DrawStateButton("Paint Job Done (S10)", RespawnTriggerState.PaintJobDone);
			GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // ---- Debug Spawn Buttons ----
            if (EnableDebug)
            {
                GUILayout.Label("Debug Spawn (simulates restoration reached)");

                if (GUILayout.Button("Spawn DE2"))
                    Main.RequestSpawn("LocoDE2", MaxDE2);

                if (GUILayout.Button("Spawn DM3"))
                    Main.RequestSpawn("LocoDM3", MaxDM3);

                if (GUILayout.Button("Spawn DH4"))
                    Main.RequestSpawn("LocoDH4", MaxDH4);

                if (GUILayout.Button("Spawn DE6"))
                    Main.RequestSpawn("LocoDE6", MaxDE6);

                if (GUILayout.Button("Spawn S060"))
                    Main.RequestSpawn("LocoS060", MaxS060);

                if (GUILayout.Button("Spawn S282"))
                    Main.RequestSpawn("LocoS282", MaxS282);
            }
			GUILayout.Space(5);
			ShowKnownIssues = GUILayout.Toggle(ShowKnownIssues, "Known Issues:");

			if (ShowKnownIssues)
			{
				GUILayout.Space(5);
				GUILayout.BeginVertical(GUI.skin.box);
				knownIssuesScroll = GUILayout.BeginScrollView(
					knownIssuesScroll,
					GUILayout.Height(150)
				);

				GUILayout.Label(
					"• Museum locomotives are intentionally removed from Crew Vehicle list.\n" +
					"• Clones cannot be deleted / teleported home via Comms Radio.\n" +
					"• A game restart is required to properly refresh the restoration state in the museum after each restoration.\n" +
					"\nCommunity Support:\n" +
					"I truly appreciate any support or technical insight from the community.\n" +
					"My goal is to offer this mod in the most stable and error-free way possible.\n" +
					"I have reached out to the official developers regarding certain engine limitations, but unfortunately have not received a response."
				);
				GUILayout.EndScrollView();
				GUILayout.EndVertical();
			}			
			GUILayout.Space(5);
        }

        private void DrawSlider(string label, ref int value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(50));

            value = Mathf.Clamp(
                Mathf.RoundToInt(
                    GUILayout.HorizontalSlider(value, 0, 5, GUILayout.Width(250))
                ),
                0,
                5
            );

            GUILayout.Label(value.ToString(), GUILayout.Width(30));
            GUILayout.EndHorizontal();
        }
		
		private void DrawStateButton(string label, RespawnTriggerState state)
		{
			Color originalColor = GUI.backgroundColor;

			if (respawnTrigger == state)
				GUI.backgroundColor = Color.green;

			if (GUILayout.Button(label, GUILayout.Width(150)))
				respawnTrigger = state;

			GUI.backgroundColor = originalColor;
		}

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void OnChange() { }
    }
}
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEngine;
using UObject = UnityEngine.Object;
using URandom = UnityEngine.Random;
using HarmonyLib;
using DV;
using DV.Customization;
using DV.LocoRestoration;
using DV.ThingTypes;
using DV.Simulation;
using DV.Garages;
using DV.Utils;
using DV.PointSet;
using DV.CabControls;
using DV.Shops;
using DV.PitStops;
using Newtonsoft.Json.Linq;

namespace MoreDemoLocos
{
	public class MoreDemoLocoClone : MonoBehaviour{}
	
	// =========================================================
	// WORLD LOAD HOOK (NUR INIT, KEIN SAVE!)
	// =========================================================
	[HarmonyPatch(typeof(WorldStreamingInit), "Awake")]
	static class WorldStreamingInitPatch
	{
		static void Postfix()
		{
			if (Main.Manager == null)
				return;

			Main.LogDebug("World loaded – delayed init");
			CoroutineManager.Instance.Run(InitLater());
		}

		private static System.Collections.IEnumerator InitLater()
		{
			// warten bis ALLES lebt
			while (CarSpawner.Instance == null || CarSpawner.Instance.PoolSetupInProgress)
				yield return null;

			ReloadSavegameState();

			Main.Manager.InitIfNeeded();
			Main.LogDebug("World init + save reload completed");
		}

		private static void ReloadSavegameState()
		{
			MoreDemoLocosState.SaveLoaded = true;
			Main.LogDebug("SaveLoaded = true");
		}
	}

    // =========================================================
    // SHARED SAVE STATE (ZENTRAL!)
    // =========================================================
    internal static class MoreDemoLocosState
    {
        internal static readonly Dictionary<string, (int current, int max)> CloneState = new();
        internal static readonly Dictionary<string, JObject> HomeLocos = new();
		internal static bool SaveDirty = false;
		internal static bool SaveLoaded = false;
        internal static void MarkDirty() => SaveDirty = true;
    }
	
	internal static class RestorationRuntimeRefresher
	{
		internal static void ForceFireRestorationStateChanged(LocoRestorationController ctrl)
		{
			if (ctrl == null)
				return;

			var handlerField = typeof(LocoRestorationController)
				.GetField("StateChanged", BindingFlags.Instance | BindingFlags.NonPublic);

			if (handlerField == null)
			{
				Main.LogDebug("ERROR: StateChanged field not found");
				return;
			}

			var del = handlerField.GetValue(ctrl) as MulticastDelegate;
			if (del == null)
			{
				Main.LogDebug("No StateChanged listeners registered");
				return;
			}

			foreach (var d in del.GetInvocationList())
			{
				try
				{
					d.DynamicInvoke(
						ctrl,
						ctrl.locoLivery,
						ctrl.State
					);
				}
				catch (System.Exception e)
				{
					Main.LogDebug($"ERROR invoking StateChanged: {e}");
				}
			}

			Main.LogDebug("StateChanged FORCED for museum refresh");
		}
		
		internal static void ForcePitStopRefresh(LocoRestorationController ctrl)
		{
			if (ctrl == null)
				return;

			var refreshers = UObject
				.FindObjectsOfType<PitStopRefresherForLocoRestoration>();

			foreach (var refresher in refreshers)
			{
				if (refresher == null || refresher.restorationControllers == null)
					continue;

				if (!refresher.restorationControllers.Contains(ctrl))
					continue;

				var pitStop = refresher.GetComponent<PitStop>();
				if (pitStop != null)
				{
					pitStop.RefreshPitStopCarPresence();
					Main.LogDebug("PitStop refreshed for clone restoration");
				}
			}
		}
	}
	
	internal static class RestorationSavegameInjector
	{
		internal static void ForceResetToState2(
			LocoRestorationController ctrl,
			TrainCar cloneLoco
		)
		{
			if (ctrl == null || cloneLoco == null)
				return;

			var save = SaveGameManager.Instance?.data;
			if (save == null)
				return;

			var dataObject = Traverse.Create(save)
				.Field("dataObject")
				.GetValue<JObject>();

			if (dataObject == null)
				return;

			// === Restoration_Locos sicherstellen ===
			if (!(dataObject["Restoration_Locos"] is JObject restorationLocos))
			{
				restorationLocos = new JObject();
				dataObject["Restoration_Locos"] = restorationLocos;
			}

			// === EINTRAG EXPLIZIT ÜBERSCHREIBEN ===
			restorationLocos[ctrl.SaveID] = new JObject
			{
				["state"] = (int)LocoRestorationController.RestorationState.S2_LocoUnblocked,
				["loco"]  = cloneLoco.CarGUID
			};

			Main.LogDebug(
				$"FORCE Savegame reset: {ctrl.SaveID} -> S2, loco={cloneLoco.CarGUID}"
			);
		}
	}
	
	internal static class RestorationControllerHardReset
	{
		internal static void ForceControllerResetToS2(LocoRestorationController ctrl)
		{
			if (ctrl == null)
				return;

			// 1) currentState hart zurücksetzen
			Traverse.Create(ctrl)
				.Field("currentState")
				.SetValue(LocoRestorationController.RestorationState.S2_LocoUnblocked);

			// 2) completedStates LEEREN (EXTREM WICHTIG)
			Traverse.Create(ctrl)
				.Field("completedStates")
				.SetValue(new HashSet<LocoRestorationController.RestorationState>());

			// 3) cachedCompletedStates ebenfalls leeren
			Traverse.Create(ctrl)
				.Field("cachedCompletedStates")
				.SetValue(new HashSet<LocoRestorationController.RestorationState>());

			// 4) State offiziell anwenden (triggert interne Logik)
			Traverse.Create(ctrl)
				.Method(
					"SetState",
					LocoRestorationController.RestorationState.S2_LocoUnblocked
				)
				.GetValue();

			Main.LogDebug($"HARD controller reset -> S2 ({ctrl.locoLivery?.id})");
		}
	}

    // =========================================================
    // LOAD FROM SAVE
    // =========================================================
    [HarmonyPatch(typeof(StartGameData_FromSaveGame), "MakeCurrent")]
    static class Patch_Load_MoreDemoLocos
    {
        static void Postfix(StartGameData_FromSaveGame __instance)
        {
            var saveData = Traverse.Create(__instance)
                .Field("saveGameData")
                .GetValue<SaveGameData>();
            if (saveData == null) return;

            var dataObject = Traverse.Create(saveData)
                .Field("dataObject")
                .GetValue<JObject>();
            if (dataObject == null) return;
            MoreDemoLocosState.CloneState.Clear();
            if (dataObject.TryGetValue("MoreDemoLocos", out var token) && token is JObject mdl)
            {
                foreach (var p in mdl.Properties())
                {
                    var o = (JObject)p.Value;
                    int current = o["current"].Value<int>();
                    int max = o["max"].Value<int>();
                    MoreDemoLocosState.CloneState[p.Name] = (current, max);
                }
            }
			MoreDemoLocosState.HomeLocos.Clear();
			if (dataObject.TryGetValue("MoreDemoHome", out var homeTok) && homeTok is JObject homeObj)
			{
				foreach (var p in homeObj.Properties())
				{
					MoreDemoLocosState.HomeLocos[p.Name] = (JObject)p.Value;
				}
			}
			MoreDemoLocosState.SaveLoaded = true;
            Main.LogDebug("Save loaded into RAM.");
        }
    }
	
	/*	
    // =========================================================
    // UNFINISHED TELEPORT!!!!!!				BROKEN!!!!!
    // =========================================================
	[HarmonyPatch(typeof(CommsRadioCarDeleter), "OnUse")]
	static class Patch_RadioDelete_ConfirmOnly
	{
		static bool Prefix(CommsRadioCarDeleter __instance)
		{
			// ? WICHTIG: NUR beim echten CONFIRM eingreifen
			if (__instance.CurrentState != CommsRadioCarDeleter.State.ConfirmDelete)
				return true;

			var car = Traverse.Create(__instance)
				.Field("carToDelete")
				.GetValue<TrainCar>();

			if (car == null)
				return true;

			if (!MoreDemoLocosState.HomeLocos.ContainsKey(car.CarGUID))
				return true;

			Main.LogDebug($"CONFIRM DELETE BLOCKED – HOME CLONE {car.CarGUID}");

			// ?? EXTREM WICHTIG: UI & State zurücksetzen
			__instance.SendMessage(
				"ClearFlags",
				SendMessageOptions.DontRequireReceiver
			);
			
			ReturnCloneToGarage(car);
			return false;
		}
		
		private static void ReturnCloneToGarage(TrainCar car)
		{
			if (car == null)
				return;

			// 1) BESTER FALL: HomeGarageReference (Vanilla-Weg)
			var homeRef = car.GetComponent<HomeGarageReference>();
			if (homeRef != null && homeRef.garageCarSpawner != null)
			{
				Main.LogDebug($"Returning HOME clone via HomeGarageReference: {car.CarGUID}");
				homeRef.garageCarSpawner.ReturnCarHome(car);
				return;
			}

			// 2) FALLBACK: irgendein GarageCarSpawner -> DV validiert intern
			var spawners = Resources.FindObjectsOfTypeAll<GarageCarSpawner>();
			foreach (var spawner in spawners)
			{
				if (spawner == null)
					continue;

				Main.LogDebug($"Trying fallback GarageCarSpawner: {spawner.name}");
				spawner.ReturnCarHome(car);
				return;
			}

			// 3) NOTFALL: löschen
			Main.LogDebug($"WARNING: No garage found, deleting clone: {car.CarGUID}");
			SingletonBehaviour<CarSpawner>.Instance.DeleteCar(car);
		}
	}*/
	
    // =========================================================
    // DISABLE CLONE DELETE!
    // =========================================================
	[HarmonyPatch(typeof(CommsRadioCarDeleter), "OnUse")]
	static class Patch_RadioDelete_BlockClones
	{
		static bool Prefix(CommsRadioCarDeleter __instance)
		{
			// nur beim CONFIRM
			if (__instance.CurrentState != CommsRadioCarDeleter.State.ConfirmDelete)
				return true;

			var car = Traverse.Create(__instance)
				.Field("carToDelete")
				.GetValue<TrainCar>();

			if (car == null)
				return true;

			// nur unsere Klone
			if (!MoreDemoLocosState.HomeLocos.ContainsKey(car.CarGUID))
				return true;

			// HARD BLOCK
			car.preventDelete = true;

			Main.LogDebug($"DELETE BLOCKED (placeholder) – CLONE {car.CarGUID}");

			// UI & State sauber zurücksetzen
			__instance.SendMessage(
				"ClearFlags",
				SendMessageOptions.DontRequireReceiver
			);

			// Vanilla komplett abbrechen
			return false;
		}
	}

    // =========================================================
    // SAVE TO SAVEGAME (WIE SHOPREWORK)
    // =========================================================
    [HarmonyPatch(typeof(SaveGameManager), "Save")]
    static class Patch_SaveGameManager_Save_MoreDemoLocos
    {
        static void Prefix()
        {
            if (!MoreDemoLocosState.SaveDirty)
				return;

            var saveData = SaveGameManager.Instance?.data;
            if (saveData == null) return;

            var trav = Traverse.Create(saveData).Field("dataObject");
            var dataObject = trav.GetValue<JObject>() ?? new JObject();
            if (trav.GetValue<JObject>() == null)
                trav.SetValue(dataObject);

            var mdl = new JObject();
            foreach (var kv in MoreDemoLocosState.CloneState)
            {
                mdl[kv.Key] = new JObject
                {
                    ["current"] = kv.Value.current,
                    ["max"] = kv.Value.max
                };
            }
			
			var home = new JObject();
			foreach (var kv in MoreDemoLocosState.HomeLocos)
			{
				home[kv.Key] = kv.Value;
			}

			dataObject["MoreDemoHome"] = home;

            dataObject["MoreDemoLocos"] = mdl;
            MoreDemoLocosState.SaveDirty = false;

            Main.LogDebug("Save wrote clone state.");
        }
    }
	
	internal static class CrewVehicleHelper
	{
		internal static bool IsUniqueFromSavegame(TrainCar car)
		{
			if (car == null)
				return false;

			var saveData = SaveGameManager.Instance?.data;
			if (saveData == null)
				return false;

			var dataObject = Traverse.Create(saveData)
				.Field("dataObject")
				.GetValue<JObject>();

			if (dataObject == null)
				return false;

			if (!dataObject.TryGetValue("cars", out var carsTok))
				return false;

			foreach (var c in carsTok.Children<JObject>())
			{
				if (c["carGuid"]?.Value<string>() == car.CarGUID)
					return c["unique"]?.Value<bool>() == true;
			}

			return false;
		}

		internal static bool IsEligibleCrewVehicle(TrainCar car)
		{
			if (car == null)
				return false;

			var customization = car.GetComponent<TrainCarCustomization>();
			var modState = customization?.Serialize();

			// NUR explizit als ORIGINAL markierte Loks
			return MoreDemoLocoSaveFlags.IsOriginal(modState);
		}
	}
	
	internal static class MoreDemoLocoSaveFlags
	{
		public const string ORIGINAL = "original";
		public const string CLONE = "clone";

		public static void MarkOriginal(TrainCar car)
		{
			if (car == null) return;

			var customization = car.GetComponent<TrainCarCustomization>();
			if (customization == null) return;

			var data = customization.Serialize() ?? new JObject();
			data[ORIGINAL] = true;
			data.Remove(CLONE);

			customization.Deserialize(data);
		}

		public static void MarkClone(TrainCar car)
		{
			if (car == null) return;

			var customization = car.GetComponent<TrainCarCustomization>();
			if (customization == null) return;

			var data = customization.Serialize() ?? new JObject();
			data[CLONE] = true;
			data.Remove(ORIGINAL);

			customization.Deserialize(data);
		}

		public static bool IsOriginal(JObject modState)
			=> modState?["original"]?.Value<bool>() == true;

		public static bool IsClone(JObject modState)
			=> modState?["clone"]?.Value<bool>() == true;
	}

	[HarmonyPatch(typeof(CarSpawner), "SpawnCrewVehicle")]
	static class Patch_CarSpawner_SpawnCrewVehicle_LogOnly
	{
		static void Prefix(TrainCarLivery livery)
		{
			if (!livery.id.StartsWith("Loco"))
				return;

			Main.LogDebug($"SpawnCrewVehicle request for {livery.id}");
		}
	}
	
	[HarmonyPatch(typeof(GarageCarSpawner), "GetCar")]
	static class Patch_GarageCarSpawner_GetCar_AllowOriginals
	{
		static void Postfix(
			GarageCarSpawner __instance,
			TrainCarLivery livery,
			ref TrainCar __result
		)
		{
			// Vanilla hat bereits eine Lok gefunden ? NICHT eingreifen
			if (__result != null)
				return;

			// Nur für Loks
			if (livery == null || !livery.id.StartsWith("Loco"))
				return;

			foreach (var car in UObject.FindObjectsOfType<TrainCar>())
			{
				if (car == null)
					continue;

				if (car.carLivery != livery)
					continue;

				var customization = car.GetComponent<TrainCarCustomization>();
				var modState = customization?.Serialize();

				// ?? NUR explizit ORIGINAL markierte Loks akzeptieren
				if (!MoreDemoLocoSaveFlags.IsOriginal(modState))
					continue;

				Main.LogDebug($"GetCar override -> ORIGINAL {car.CarGUID}");

				__result = car;
				return;
			}
		}
	}
	
	[HarmonyPatch(typeof(CarSpawner), "SpawnCrewVehicle")]
	static class Patch_SpawnCrewVehicle_ForceOriginal
	{
		static bool Prefix(
			TrainCarLivery livery,
			ref TrainCar __result
		)
		{
			if (livery == null || !livery.id.StartsWith("Loco"))
				return true;

			foreach (var car in UObject.FindObjectsOfType<TrainCar>())
			{
				if (car == null)
					continue;

				if (car.carLivery != livery)
					continue;

				var customization = car.GetComponent<TrainCarCustomization>();
				var modState = customization?.Serialize();

				if (!MoreDemoLocoSaveFlags.IsOriginal(modState))
					continue;

				Main.LogDebug($"FORCE CrewVehicle ORIGINAL {car.CarGUID}");

				__result = car;
				return false; // ?? DV STOPPEN. KEIN SAVEGAME. KEIN CACHE.
			}

			Main.LogDebug($"No ORIGINAL found for {livery.id}");
			return true;
		}
	}

    // =========================================================
    // GAME LOGIC
    // =========================================================
    public class MultiRestorationManager : MonoBehaviour
    {
        private List<LocoRestorationController> originals;

        private readonly HashSet<(LocoRestorationController, LocoRestorationController.RestorationState)>
            triggeredStates = new();

        private int GetMaxForLivery(string id) => id switch
        {
            "LocoDE2" => Main.Settings.MaxDE2,
            "LocoDM3" => Main.Settings.MaxDM3,
            "LocoDH4" => Main.Settings.MaxDH4,
            "LocoDE6" => Main.Settings.MaxDE6,
            "LocoS060" => Main.Settings.MaxS060,
            "LocoS282A" => Main.Settings.MaxS282,
            _ => 0
        };

        public void InitIfNeeded()
        {
            if (originals != null) return;

            originals = FindObjectsOfType<LocoRestorationController>()
                .Where(c => c?.locoLivery != null && c.spawnPoints?.Length > 0)
                .ToList();

            Main.LogDebug($"Found {originals.Count} restoration controllers");
        }
		
		public void SpawnForType(string liveryId, int count)
		{
			if (count <= 0)
				return;

			InitIfNeeded();

			string normalizedId = liveryId == "LocoS282" ? "LocoS282A" : liveryId;

			var baseController = FindObjectsOfType<LocoRestorationController>()
				.FirstOrDefault(c => c.locoLivery != null && c.locoLivery.id == normalizedId);

			if (baseController == null)
			{
				Main.LogDebug($"No base controller for {liveryId}");
				return;
			}

			for (int i = 0; i < count; i++)
			{
				TrySpawn(baseController);
			}
		}

        public void OnRestorationMilestoneReached(LocoRestorationController controller,LocoRestorationController.RestorationState state,bool simulated)
        {
            InitIfNeeded();

            if (!simulated)
            {
                var key = (controller, state);
                if (!triggeredStates.Add(key)) return;
            }

            TrySpawn(controller);
        }

        private void TrySpawn(LocoRestorationController controller)
		{

			string id = controller.locoLivery.id;
			if (id == "LocoS282") id = "LocoS282A";			
			
			if (!MoreDemoLocosState.SaveLoaded)
			{
				Main.LogDebug($"Spawn blocked for {id}: save not loaded yet");
				return;
			}

			// --- FALL B: NOCH KEIN SAVEGAME-EINTRAG ---
			if (!MoreDemoLocosState.CloneState.TryGetValue(id, out var state))
			{
				int max = GetMaxForLivery(id);

				if (max <= 0)
				{
					Main.LogDebug($"Spawn blocked for {id}: max=0");
					return;
				}

				// ERSTER Spawn ? current = 1
				TriggerRespawn(controller);

				MoreDemoLocosState.CloneState[id] = (1, max);
				MoreDemoLocosState.MarkDirty();

				Main.LogDebug($"First clone spawned for {id} (1/{max})");
				return;
			}

			// --- FALL A: EINTRAG EXISTIERT ---
			if (state.current >= state.max)
			{
				Main.LogDebug($"Spawn blocked for {id} ({state.current}/{state.max})");
				return;
			}

			// --- Spawn erlaubt ---
			TriggerRespawn(controller);

			MoreDemoLocosState.CloneState[id] = (state.current + 1, state.max);
			MoreDemoLocosState.MarkDirty();

			Main.LogDebug(
				$"Clone spawned for {id} " +
				$"({state.current + 1}/{state.max})"
			);
		}

        private void TriggerRespawn(LocoRestorationController baseController)
		{
			var prefab = baseController.locoLivery.prefab;
			var bounds = prefab.GetComponent<TrainCar>().Bounds;

			var shuffled = baseController.spawnPoints
				.Where(p => p != null)
				.OrderBy(_ => URandom.value)
				.ToList();

			Transform chosen = null;

			foreach (var sp in shuffled)
			{
				Vector3 pos =
					sp.transform.position +
					sp.transform.up * bounds.center.y +
					sp.transform.forward * bounds.center.z;

				if (!CarSpawner.IsBoxOverlappingSimple(
						pos,
						bounds.extents,
						sp.transform.rotation))
				{
					chosen = sp.transform;
					break;
				}
			}

			if (chosen == null)
			{
				Main.LogDebug(
					$"No free spawn spot for {baseController.locoLivery.id}, using fallback"
				);
				chosen = baseController.spawnPoints[0].transform;
			}

			var clone = Instantiate(
				baseController.gameObject,
				baseController.transform.parent
			);
			
			clone.AddComponent<MoreDemoLocoClone>();

			var ctrl = clone.GetComponent<LocoRestorationController>();			
			CoroutineManager.Instance.Run(RegisterCloneWhenReady(ctrl));			
			ctrl.spawnPoints = baseController.spawnPoints;

			/*
			Main.LogDebug(
				$"Spawned clone for {ctrl.locoLivery.id} " +
				$"at {chosen.name} pos={chosen.position}"
			);
			*/
		}
		
		private static IEnumerator RegisterCloneWhenReady(LocoRestorationController ctrl)
		{
			for (int i = 0; i < 10; i++)
			{
				var loco = Traverse.Create(ctrl)
					.Field("loco")
					.GetValue<TrainCar>();

				if (loco != null)
				{
					// 1) Clone markieren
					loco.gameObject.AddComponent<MoreDemoLocoClone>();
					MoreDemoLocoSaveFlags.MarkClone(loco);

					// 2) Delete blockieren
					loco.preventDelete = true;

					// 3) KEIN Garage/Home
					var homeRef = loco.GetComponent<HomeGarageReference>();
					if (homeRef != null)
						UObject.Destroy(homeRef);

					// 4) PLAYER-CONTROL BLOCKIEREN (DV-SICHER)
					var controls = loco.GetComponentsInChildren<MonoBehaviour>(true)
						.Where(c => c.GetType().Namespace == "DV.CabControls");

					foreach (var c in controls)
					{
						c.enabled = false;
					}

					// ============================================
					// ?? SaveGame-Registrierung (nur für DICH)
					// ============================================

					var entry = new JObject
					{
						["livery"] = ctrl.locoLivery.id
					};

					var second = Traverse.Create(ctrl)
						.Field("secondCar")
						.GetValue<TrainCar>();

					if (second != null)
						entry["secondCar"] = second.CarGUID;

					MoreDemoLocosState.HomeLocos[loco.CarGUID] = entry;
					MoreDemoLocosState.MarkDirty();

					Main.LogDebug($"Registered clone: {loco.CarGUID}");

					// ===================================================
					// 1) RUNTIME-STATE HART ZURÜCKSETZEN (ZUERST!!!)
					// ===================================================
					RestorationControllerHardReset.ForceControllerResetToS2(ctrl);

					// ===================================================
					// 2) SAVEGAME (für nächsten Load)
					// ===================================================
					RestorationSavegameInjector.ForceResetToState2(ctrl, loco);

					// ===================================================
					// 3) EVENTS FEUERN (JETZT IST STATE STABIL)
					// ===================================================
					RestorationRuntimeRefresher.ForceFireRestorationStateChanged(ctrl);
					RestorationRuntimeRefresher.ForcePitStopRefresh(ctrl);

					// ===================================================
					// 4) UI neu binden (optional, aber stabil)
					// ===================================================
					CoroutineManager.Instance.Run(PostLoadEquivalentRefresh(ctrl));

					Main.LogDebug($"Clone {loco.CarGUID} actually spawned at {loco.transform.position}");
					
					yield break;
				}

				yield return null;
			}

			Main.LogDebug("ERROR: Clone loco never initialized");
		}
		
		private static void ReRegisterOriginalAfterClone(TrainCarLivery livery)
		{
			if (!GarageCarSpawner.Spawners.TryGetValue(livery, out var spawner))
				return;

			foreach (var car in UObject.FindObjectsOfType<TrainCar>())
			{
				if (car == null)
					continue;

				if (car.carLivery != livery)
					continue;

				var customization = car.GetComponent<TrainCarCustomization>();
				var modState = customization?.Serialize();

				if (!MoreDemoLocoSaveFlags.IsOriginal(modState))
					continue;

				Main.LogDebug($"Re-register ORIGINAL after clone: {car.CarGUID}");
				spawner.OverrideSpawnedCarReference(car);
				return;
			}
		}

        // =========================================================
        // RESTORATION VIEW HOOK
        // =========================================================
        [HarmonyPatch(typeof(LocoRestorationView), "OnLocoRestorationStateChanged")]
		static class LocoRestorationViewPatch
		{
			static void Postfix(LocoRestorationController controller,TrainCarLivery livery,LocoRestorationController.RestorationState state)
			{
				if (controller == null || Main.Manager == null)
					return;

				var trigger = Settings.ToRestorationState(Main.Settings.respawnTrigger);
				if (state != trigger)
					return;

				// === NEU: ORIGINAL setzen ===
				var loco = Traverse.Create(controller)
					.Field("loco")
					.GetValue<TrainCar>();

				if (loco != null)
				{
					var customization = loco.GetComponent<TrainCarCustomization>();
					var modState = customization?.Serialize();

					if (!MoreDemoLocoSaveFlags.IsOriginal(modState))
					{
						MoreDemoLocoSaveFlags.MarkOriginal(loco);
						Main.LogDebug($"Restoration trigger reached: {state} for {livery?.id}");
						Main.LogDebug($"ORIGINAL set for {loco.CarGUID}");
					}
				}

				Main.Manager.OnRestorationMilestoneReached(controller, state, false);
			}
		}
		
		// =========================================================
		// POST-LOAD EQUIVALENT REFRESH (SAVEGAME-LIKE)
		// =========================================================
		private static IEnumerator PostLoadEquivalentRefresh(LocoRestorationController ctrl)
		{
			if (ctrl == null)
				yield break;

			// WICHTIG: WARTEN BIS DV "fertig geladen" meldet
			while (!AStartGameData.carsAndJobsLoadingFinished)
				yield return null;

			// extra Sicherheit
			yield return null;

			Main.LogDebug("PostLoadEquivalentRefresh START");

			// 2) MUSEUM VIEW
			foreach (var view in UObject.FindObjectsOfType<LocoRestorationView>())
			{
				if (view != null && view.controller == ctrl)
				{
					view.OnDisable();
					view.OnEnable();
				}
			}

			// 3) GARAGE
			if (ctrl.garageSpawner != null)
			{
				ctrl.garageSpawner.AllowSpawning();
				ctrl.garageSpawner.ForceCarsRespawn();
			}

			// 4) HOME GARAGE
			var loco = Traverse.Create(ctrl)
				.Field("loco")
				.GetValue<TrainCar>();

			if (loco != null && ctrl.garageSpawner != null)
			{
				var home = loco.GetComponent<HomeGarageReference>()
					?? loco.gameObject.AddComponent<HomeGarageReference>();

				home.garageCarSpawner = ctrl.garageSpawner;
			}

			// 5) CASH REGISTER (REFLECTION)
			ResetCashRegister(ctrl.orderPartsModule);
			ResetCashRegister(ctrl.installPartsModule);
			ForceMuseumProgressReset(ctrl);

			Main.LogDebug("PostLoadEquivalentRefresh DONE");
		}
		
		private static void ResetCashRegister(GenericThingCashRegisterModule module)
		{
			if (module == null)
				return;

			module.ResetData();

			var init = typeof(CashRegisterModule).GetMethod(
				"InitializeData",
				BindingFlags.Instance | BindingFlags.NonPublic
			);

			init?.Invoke(module, null);
		}
		
		private static void ForceMuseumProgressReset(LocoRestorationController ctrl)
		{
			if (ctrl == null)
				return;

			var views = UObject.FindObjectsOfType<LocoRestorationView>();

			foreach (var view in views)
			{
				if (view == null || view.controller != ctrl)
					continue;

				view.OnDisable();
				view.OnEnable();
			}

			Main.LogDebug("Museum progress cache FORCE reset");
		}
    }
	
	[HarmonyPatch(typeof(CommsRadioCrewVehicle), "UpdateAvailableVehicles")]
	static class Patch_CommsRadioCrewVehicle_RemoveMuseumLocos
	{
		static void Postfix(CommsRadioCrewVehicle __instance)
		{
			var list = Traverse.Create(__instance)
				.Field("availableVehiclesForSpawn")
				.GetValue<List<TrainCarLivery>>();

			if (list == null)
				return;

			int before = list.Count;

			list.RemoveAll(livery =>
			{
				if (livery == null)
					return false;

				// Hat diese Livery einen RestorationController?
				var ctrl = LocoRestorationController.GetForLivery(livery);
				return ctrl != null; // <- HARD BLOCK
			});

			int removed = before - list.Count;

			if (removed > 0)
				Main.LogDebug($"CrewVehicle HARD removed {removed} museum locos");
		}
	}
	
	[HarmonyPatch(typeof(StartGameData_FromSaveGame), "MakeCurrent")]
	static class Patch_ReapplyCrewVehicleFilterAfterLoad
	{
		static void Postfix()
		{
			CoroutineManager.Instance.Run(ReapplyNextFrame());
		}

		private static IEnumerator ReapplyNextFrame()
		{
			// Warten bis alles wirklich initialisiert ist
			yield return null;
			yield return null;

			foreach (var radio in UnityEngine.Object.FindObjectsOfType<CommsRadioCrewVehicle>())
			{
				var method = typeof(CommsRadioCrewVehicle)
					.GetMethod("UpdateAvailableVehicles", BindingFlags.Instance | BindingFlags.NonPublic);

				method?.Invoke(radio, null);
			}

			Main.LogDebug("CrewVehicle museum filter reapplied after save load.");
		}
	}
}

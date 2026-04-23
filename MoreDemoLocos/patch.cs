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
using DV.CashRegister;
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
			
			CoroutineManager.Instance.Run(DelayedMilestone(controller));
        }
		
		private IEnumerator DelayedMilestone(LocoRestorationController controller)
		{
			yield return new WaitForSeconds(5f);

			if (controller == null)
				yield break;

			Main.LogDebug($"Delayed milestone spawn for {controller.locoLivery?.id}");

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

			if (ctrl.garageSpawner != null)
			{
				ctrl.garageSpawner.enabled = false;
			}		
			
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
					// ============================================
					// NEW: OPTIMIZER SAFE MODE (FIXED)
					// ============================================
					loco.ForceOptimizationState(true, false); // sleep = true

					// NEW: Rigidbody zusätzlich sichern
					var rb = loco.GetComponent<Rigidbody>();
					if (rb != null)
					{
						rb.velocity = Vector3.zero;
						rb.angularVelocity = Vector3.zero;
						rb.Sleep(); // wichtig!
					}

					// OPTIONAL DEBUG
					Main.LogDebug($"[SAFE INIT] Clone optimizer blocked: {loco.CarGUID}");

					// ============================================
					// ORIGINAL CODE
					// ============================================

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

					CoroutineManager.Instance.Run(ApplyStateDelayed(ctrl, loco));

					Main.LogDebug($"Clone {loco.CarGUID} actually spawned at {loco.transform.position}");
					
					yield break;
				}

				yield return null;
			}

			Main.LogDebug("ERROR: Clone loco never initialized");
		}
		
		private static IEnumerator ApplyStateDelayed(LocoRestorationController ctrl, TrainCar loco)
		{
			// ============================================
			// WAIT UNTIL CONTROLLER EXISTS
			// ============================================
			while (ctrl == null)
				yield return null;

			var loadingDoneField = typeof(LocoRestorationController)
				.GetField("loadingDone", BindingFlags.Instance | BindingFlags.NonPublic);

			// ============================================
			// WAIT UNTIL DV FINISHED INIT
			// ============================================
			while (!(bool)loadingDoneField.GetValue(ctrl))
				yield return null;

			yield return null;

			Main.LogDebug("Controller fully initialized – applying reset NOW");

			// ============================================
			// NEW: Erst wieder "aktivieren" wenn fertig
			// ============================================
			loco.ForceOptimizationState(false, false); // wake up

			loco.preventRerail = false;
			loco.preventCouple = false;

			// ============================================
			// 2) WAIT DV APPLY
			// ============================================
			yield return null;
			yield return null;

			// ============================================
			// 3) DEBUG TELEPORT (DEIN WUNSCH)
			// ============================================
			if (Main.Settings.EnableDebug)
			{
				Main.LogDebug("Teleporting clone to player in 5 seconds...");

				yield return new WaitForSeconds(5f);

				var player = PlayerManager.PlayerTransform;

				if (player == null)
				{
					Main.LogDebug("ERROR: PlayerTransform not found!");
				}
				else
				{
					Vector3 targetPos = player.position + player.forward * 10f; // 10m vor Spieler

					loco.transform.position = targetPos;
					loco.transform.rotation = Quaternion.LookRotation(player.forward);

					var rb = loco.GetComponent<Rigidbody>();
					if (rb != null)
					{
						rb.velocity = Vector3.zero;
						rb.angularVelocity = Vector3.zero;
					}

					Main.LogDebug($"Clone teleported to player: {targetPos}");
				}
			}

			// ============================================
			// 4) DEBUG CHECK
			// ============================================
			Main.LogDebug(
				$"IsRerailAllowed = {loco.IsRerailAllowed} | " +
				$"velocity={loco.GetComponent<Rigidbody>()?.velocity.magnitude}"
			);

			// ============================================
			// 5) CONTINUE FLOW
			// ============================================
			CoroutineManager.Instance.Run(AfterCloneSetup(ctrl));
		}
		
		private static IEnumerator AfterCloneSetup(LocoRestorationController ctrl)
		{
			yield return null;
			yield return null;
			yield return null;

			// Views neu verbinden
			RebindViewsToController(ctrl);

			// ?? WICHTIG: 1 Frame warten
			yield return null;

			// ?? nochmal warten damit UI sauber zieht
			yield return null;

			// danach normal dein Refresh
			CoroutineManager.Instance.Run(PostLoadEquivalentRefresh(ctrl));

			Main.LogDebug("Clone setup DONE (SAFE)");
		}
		
		private static void RebindViewsToController(LocoRestorationController newCtrl)
		{
			var method = typeof(LocoRestorationView)
				.GetMethod(
					"OnLocoRestorationStateChanged",
					BindingFlags.Instance | BindingFlags.NonPublic
				);

			foreach (var view in UObject.FindObjectsOfType<LocoRestorationView>())
			{
				if (view == null)
					continue;

				if (view.controller == null)
					continue;

				if (view.controller.locoLivery != newCtrl.locoLivery)
					continue;

				// 1) alten Controller trennen
				view.OnDisable();

				// 2) neuen Controller setzen
				view.controller = newCtrl;

				// 3) neu verbinden
				view.OnEnable();

				// 4) RICHTIGER STATE CALL (FIX!)
				method?.Invoke(view, new object[]
				{
					newCtrl,
					newCtrl.locoLivery,
					newCtrl.State
				});

				Main.LogDebug($"View REBOUND + STATE APPLIED: {newCtrl.locoLivery.id}");
			}
		}
		
		private static void FixOrderPartsFlow(LocoRestorationController ctrl)
		{
			if (ctrl == null)
				return;

			if (ctrl.State != LocoRestorationController.RestorationState.S4_OnDestinationTrack)
			{
				Main.LogDebug("FixOrderPartsFlow skipped: not in S4");
				return;
			}

			var module = ctrl.orderPartsModule;
			if (module == null)
			{
				Main.LogDebug("FixOrderPartsFlow ERROR: module null");
				return;
			}

			Main.LogDebug("FIX: Rebuilding OrderParts flow");

			// ?? DAS IST DER KEY
			module.AddThingToCart();

			// Event wieder dran hängen
			var method = typeof(LocoRestorationController)
				.GetMethod("OnPartsOrdered", BindingFlags.Instance | BindingFlags.NonPublic);

			if (method != null)
			{
				Action action = () => method.Invoke(ctrl, null);

				module.ThingBought -= action;
				module.ThingBought += action;
			}

			Traverse.Create(module)
				.Field("OnUnitsToBuyChanged")
				.GetValue<Action>()?.Invoke();

			Main.LogDebug("FIX DONE: OrderParts active");
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
				ctrl.garageSpawner.enabled = false;
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
			CoroutineManager.Instance.Run(ForceFullCashRegisterRefresh(ctrl.orderPartsModule));
			CoroutineManager.Instance.Run(ForceFullCashRegisterRefresh(ctrl.installPartsModule));
			ForceMuseumProgressReset(ctrl);

			Main.LogDebug("PostLoadEquivalentRefresh DONE");
		}
		
		private static IEnumerator ForceFullCashRegisterRefresh(GenericThingCashRegisterModule module)
		{
			if (module == null)
				yield break;

			var register = module.GetComponentInParent<CashRegisterWithModules>();
			if (register == null)
				yield break;

			Main.LogDebug("CashRegister HARD REBUILD START");

			// ============================================
			// 1) ALLE MODULE RESETTEN
			// ============================================
			foreach (var m in register.registerModules)
			{
				if (m == null)
					continue;
			}

			yield return null;

			// ============================================
			// 2) InitializeData MANUELL ERNEUT AUFRUFEN
			// ============================================
			var initMethod = typeof(CashRegisterModule)
				.GetMethod("InitializeData", BindingFlags.Instance | BindingFlags.NonPublic);

			foreach (var m in register.registerModules)
			{
				if (m == null)
					continue;

				initMethod?.Invoke(m, null);

				// FORCE UI UPDATE
				Traverse.Create(m)
					.Field("OnUnitsToBuyChanged")
					.GetValue<Action>()?.Invoke();
			}

			yield return null;

			// ============================================
			// 3) REGISTER UI HARD REFRESH
			// ============================================
			register.gameObject.SetActive(false);
			yield return null;
			register.gameObject.SetActive(true);

			yield return null;
			yield return null;

			// ============================================
			// 4) EXTRA: FORCE BUTTON SYNC
			// ============================================
			foreach (var m in register.registerModules)
			{
				if (m == null)
					continue;

				// trick: toggle value ? UI rebuild
				var current = m.GetTotalUnitsInBasket();

				if (current > 0f)
				{
					Traverse.Create(m)
						.Method("SetUnitsToBuy", current)
						.GetValue();
				}
			}

			Main.LogDebug("CashRegister HARD REBUILD DONE");
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
	
	[HarmonyPatch(typeof(LocoRestorationController), "OnTrackChanged")]
	static class Patch_OnTrackChanged_CloneFix
	{
		static void Postfix(LocoRestorationController __instance)
		{
			if (__instance == null)
				return;

			// ?? NUR S4
			if (__instance.State != LocoRestorationController.RestorationState.S4_OnDestinationTrack)
				return;

			var loco = Traverse.Create(__instance)
				.Field("loco")
				.GetValue<TrainCar>();

			if (loco == null)
				return;

			// ?? NUR CLONES
			if (loco.GetComponent<MoreDemoLocoClone>() == null)
				return;

			var module = __instance.orderPartsModule;
			if (module == null)
			{
				Main.LogDebug("CloneFix ERROR: module null");
				return;
			}

			Main.LogDebug("CLONE S4 reached -> FORCE orderParts");

			// =====================================================
			// ?? KRITISCHER FIX (OHNE DAS SIEHT MAN NICHTS!)
			// =====================================================
			module.Data.resourceName = "Loco Parts";  // ? MUSS gesetzt sein!
			module.Data.pricePerUnit = module.price;  // safety

			// =====================================================
			// ?? RESET (OHNE CancelShopping!)
			// =====================================================
			module.Data.unitsToBuy = 0f;

			// =====================================================
			// ?? ADD ITEM
			// =====================================================
			module.AddThingToCart();

			// =====================================================
			// ?? FORCE UI UPDATE
			// =====================================================
			Traverse.Create(module)
				.Field("OnUnitsToBuyChanged")
				.GetValue<Action>()?.Invoke();

			// =====================================================
			// ?? DEBUG
			// =====================================================
			Main.LogDebug(
				$"DEBUG: units={module.Data.unitsToBuy}, name={module.Data.resourceName}"
			);

			Main.LogDebug("CLONE orderParts FIX DONE");
		}
	}
}

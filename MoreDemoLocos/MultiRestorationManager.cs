using System;
using System.Collections.Generic;
using System.Linq;
using DV.LocoRestoration;
using UnityEngine;

namespace MoreDemoLocos
{
    public class MultiRestorationManager : MonoBehaviour
    {
        public List<LocoRestorationController> originalControllers;

        private void Start()
        {
            originalControllers = FindObjectsOfType<LocoRestorationController>()
                .Where(c => c != null && c.locoLivery != null)
                .ToList();

            if (!originalControllers.Any())
            {
                Invoke(nameof(Retry), 2f);
                return;
            }

            SpawnClones();
        }

        private void Retry()
        {
            originalControllers = FindObjectsOfType<LocoRestorationController>()
                .Where(c => c != null && c.locoLivery != null)
                .ToList();

            if (!originalControllers.Any())
            {
                Main.logger.Log(" Cant clone Restoration locos!");
                return;
            }

            SpawnClones();
        }

		private void SpawnClones()
		{
			int countPerType = Main.settings.locoCountPerType;
			var allPoints = FindObjectsOfType<LocoRestorationSpawnPoint>().ToList();
			allPoints.Shuffle();

			var usedPoints = new HashSet<LocoRestorationSpawnPoint>();

			foreach (var original in originalControllers)
			{
				string liveryId = original.locoLivery.id;

				var matchingPoints = allPoints
					.Where(p => original.spawnPoints.Contains(p) && !usedPoints.Contains(p))
					.ToList();

				int spawnCount = Mathf.Min(countPerType - 1, matchingPoints.Count);
                Main.logger.Log($" Spawn {spawnCount} additional restoration locos '{liveryId}'");

				for (int i = 0; i < spawnCount; i++)
				{
					var point = matchingPoints[i];
					CreateRestorationClone(original, i + 1, point);
					usedPoints.Add(point);
				}
			}
		}

		private void CreateRestorationClone(LocoRestorationController baseController, int index, LocoRestorationSpawnPoint point)
		{
			var cloneGO = Instantiate(baseController.gameObject, baseController.transform.parent);
			var controller = cloneGO.GetComponent<LocoRestorationController>();
			controller.spawnPoints = new[] { point };
			Main.logger.Log($" Clone {index} for '{baseController.locoLivery.id}' at '{point.name}'.");
		}
	}

	public static class ListExtensions
	{
		private static readonly System.Random rng = new System.Random();
		
		public static void Shuffle<T>(this IList<T> list)
		{
			int n = list.Count;
			while (n > 1)
			{
				int k = rng.Next(n--);
				(list[n], list[k]) = (list[k], list[n]);
			}
		}
	}
}


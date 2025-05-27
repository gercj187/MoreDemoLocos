using System;
using System.Collections.Generic;
using System.Linq;
using DV.LocoRestoration;
using UnityEngine;

namespace MoreDemoLocos
{
    public class MultiRestorationManager : MonoBehaviour
    {
        [Tooltip("Welche Loktypen sollen vervielfacht werden")]
        public List<LocoRestorationController> originalControllers;

        private void Start()
        {
            originalControllers = FindObjectsOfType<LocoRestorationController>()
                .Where(c => c != null && c.locoLivery != null)
                .ToList();

            if (!originalControllers.Any())
            {
                //Main.logger.Log("[MoreDemoLocos] Keine RestorationController gefunden (wahrscheinlich noch nicht geladen). Versuche es in 2 Sekunden erneut...");
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
                //Main.logger.Log("[MoreDemoLocos] Auch beim zweiten Versuch keine Controller gefunden.");
                return;
            }

            SpawnClones();
        }

        private void SpawnClones()
        {
            foreach (var original in originalControllers)
            {
                int count = Main.settings.locoCountPerType;
                Main.logger.Log($"[MoreDemoLocos] Erzeuge {count}x '{original.locoLivery.id}'");

                for (int i = 1; i < count; i++) // i=1, weil Original schon existiert
                {
                    CreateRestorationClone(original, i);
                }
            }
        }

        private void CreateRestorationClone(LocoRestorationController baseController, int index)
        {
            var spawnPoints = baseController.spawnPoints.ToList();
            spawnPoints.Shuffle(); // eigene Erweiterung unten

            var chosenPoint = spawnPoints.FirstOrDefault(p => !p.pointUsed);
            if (chosenPoint == null)
            {
                //Main.logger.Log($"[MoreDemoLocos] Kein freier Spawnpunkt für '{baseController.locoLivery.id}' (Instanz {index}) gefunden.");
                return;
            }

            var cloneGO = Instantiate(baseController.gameObject, baseController.transform.parent);
            var controller = cloneGO.GetComponent<LocoRestorationController>();
            chosenPoint.pointUsed = true;

            string uniqueId = baseController.SaveID + "_Clone" + index;
            //Main.logger.Log($"[MoreDemoLocos] Clone '{uniqueId}' platziert bei {chosenPoint.name}");
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

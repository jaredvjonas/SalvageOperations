using System.Collections.Generic;
using System.Linq;
using BattleTech;
using BattleTech.StringInterpolation;
using Harmony;
using Localize;
using UnityEngine;

// ReSharper disable InconsistentNaming
// ReSharper disable UnusedMember.Global

namespace SalvageOperations.Patches
{
    // trigger hotkey
    [HarmonyPatch(typeof(SimGameState), "Update")]
    public static class SimGameState_Update_Patch
    {
        public static void Postfix(SimGameState __instance)
        {
            if (Main.Settings.DependsOnArgoUpgrade && !__instance.PurchasedArgoUpgrades.Contains(Main.Settings.ArgoUpgrade))
                return;
            var hotkey = (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(Main.Settings.Hotkey);
            if (hotkey)
            {
               // Logger.Log("Hotkey Triggered");
                Main.GlobalBuild();
            }
        }
    }

    public class Comparer : IEqualityComparer<ChassisDef>
    {
        public bool Equals(ChassisDef x, ChassisDef y)
        {
            return x.Description.Id == y.Description.Id;
        }

        public int GetHashCode(ChassisDef obj)
        {
            return obj.Description.Id.GetHashCode();
        }
    }

    // where the mayhem starts
    [HarmonyPatch(typeof(SimGameState), "AddMechPart")]
    public static class SimGameState_AddMechPart_Patch
    {
        public static bool Prefix(SimGameState __instance, string id)
        {
            if (Main.Settings.DependsOnArgoUpgrade && !__instance.PurchasedArgoUpgrades.Contains(Main.Settings.ArgoUpgrade))
                return true;

            // this function replaces the function from SimGameState, prefix return false
            // just add the piece
            if (id != null)
                __instance.AddItemStat(id, "MECHPART", false);

            // we're in the middle of resolving a contract, add the piece to contract
            if (Main.IsResolvingContract)
            {
                if (!Main.SalvageFromContract.ContainsKey(id))
                    Main.SalvageFromContract[id] = 0;

                Main.SalvageFromContract[id]++;
                return false;
            }

            // TODO: what happens when you buy multiple pieces from the store at once and can build for each?
            // not in contract, just try to build with what we have
            if (!__instance.CompanyTags.Contains("SO_Salvaging"))
            {
                Main.ExcludedVariantHolder = __instance.DataManager.MechDefs.Get(id);
                Main.TryBuildMechs(__instance, new Dictionary<string, int> {{id, 1}});
                Main.ConvertCompanyTags(true);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(SimGameState), "ResolveCompleteContract")]
    public static class SimGameState_ResolveCompleteContract_Patch
    {
        public static void Prefix(SimGameState __instance)
        {
            if (Main.Settings.DependsOnArgoUpgrade && !__instance.PurchasedArgoUpgrades.Contains(Main.Settings.ArgoUpgrade))
                return;

            Main.ContractStart();
            __instance.CompanyTags.Add("SO_Salvaging");
            Main.HasBeenBuilt.Clear();
        }

        public static void Postfix(SimGameState __instance)
        {
            if (Main.Settings.DependsOnArgoUpgrade && !__instance.PurchasedArgoUpgrades.Contains(Main.Settings.ArgoUpgrade))
                return;

            foreach (var mechID in Main.SalvageFromContract.Keys)
            {

                var mechDef = __instance.DataManager.MechDefs.Get(mechID);
                if (!Main.HasBeenBuilt.ContainsKey(mechDef.Description.Name))
                {
                    Main.ExcludedVariantHolder = mechDef;
                    Main.TryBuildMechs(__instance, new Dictionary<string, int> { { mechID, 1 } });
                }
            }

            __instance.CompanyTags.Remove("SO_Salvaging");
            Main.ConvertCompanyTags(true);
            Main.ContractEnd();
        }
    }

    [HarmonyPatch(typeof(SimGameState), "BuildSimGameStatsResults")]
    public static class SimGameState_BuildSimGameStatsResults_Patch
    {
        public static void Postfix(List<ResultDescriptionEntry> __result, SimGameStat[] stats, GameContext context, string prefix)
        {
            var sim = UnityGameInstance.BattleTechGame.Simulation;
            if (Main.Settings.DependsOnArgoUpgrade && !sim.PurchasedArgoUpgrades.Contains(Main.Settings.ArgoUpgrade))
                return;

            if (stats.All(stat => !stat.name.StartsWith("Item.MECHPART.") && !stat.name.StartsWith("Item.MechDef.")))
                return;

            // remove blank result descriptions
            var removeResultDescription = new List<ResultDescriptionEntry>();
            foreach (var descriptionEntry in __result)
            {
                if (descriptionEntry.Text.ToString(false).Contains("[[DM.SimGameStatDescDefs[], ]]"))
                    removeResultDescription.Add(descriptionEntry);
            }

            foreach (var entry in removeResultDescription)
                __result.Remove(entry);

            // add "real" descriptions for MECHPARTs or MechDefs
            var gameContext = new GameContext(context);
            foreach (var stat in stats)
            {
                if (!stat.name.StartsWith("Item.MECHPART.") && !stat.name.StartsWith("Item.MechDef."))
                    continue;

                var text = DescribeStat(sim, stat.name, stat.value);
                __result.Add(new ResultDescriptionEntry(new Text(
                    $"{prefix} {Interpolator.Interpolate(text, gameContext, false)}"), gameContext, stat.name));
            }
        }

        // resolve names directly: the [[DM.MechDefs[...]]] interpolation comes back blank
        internal static string DescribeStat(SimGameState sim, string statName, string value)
        {
            var split = statName.Split('.');
            var type = split[1];
            var mechID = split[2];
            var num = int.Parse(value);

            switch (type)
            {
                case "MechDef":
                    var chassisDef = sim.DataManager.ChassisDefs.Exists(mechID) ? sim.DataManager.ChassisDefs.Get(mechID) : null;
                    var chassisMech = sim.DataManager.MechDefs.Exists(mechID.Replace("chassisdef", "mechdef")) ? sim.DataManager.MechDefs.Get(mechID.Replace("chassisdef", "mechdef")) : null;
                    return $"Added {chassisMech?.Description.UIName ?? chassisDef?.Description.UIName ?? mechID} to 'Mech storage";
                case "MECHPART":
                    var partMech = sim.DataManager.MechDefs.Exists(mechID) ? sim.DataManager.MechDefs.Get(mechID) : null;
                    var partName = partMech?.Description.UIName ?? mechID;
                    return num > 0 ? $"Added {num} {partName} Parts" : $"Removed {num * -1} {partName} Parts";
            }
            return "";
        }
    }

    // CustomDeploy's prefix replaces BuildSimGameResults with its own copy that never calls
    // BuildSimGameStatsResults, so the patch above never fires; relabel the finished entries here instead
    [HarmonyPatch(typeof(SimGameState), "BuildSimGameResults", new[] { typeof(SimGameEventResult[]), typeof(GameContext), typeof(SimGameStatDescDef.DescriptionTense?), typeof(Pilot) })]
    public static class SimGameState_BuildSimGameResults_Patch
    {
        public static void Postfix(List<ResultDescriptionEntry> __result, SimGameEventResult[] resultsList)
        {
            var sim = UnityGameInstance.BattleTechGame.Simulation;
            if (sim == null || __result == null || resultsList == null)
                return;
            if (Main.Settings.DependsOnArgoUpgrade && !sim.PurchasedArgoUpgrades.Contains(Main.Settings.ArgoUpgrade))
                return;

            var values = new Dictionary<string, string>();
            foreach (var result in resultsList)
            {
                if (result?.Stats == null)
                    continue;
                foreach (var stat in result.Stats)
                    if (stat.name != null && (stat.name.StartsWith("Item.MECHPART.") || stat.name.StartsWith("Item.MechDef.")))
                        values[stat.name] = stat.value;
            }

            if (values.Count == 0)
                return;

            foreach (var entry in __result)
            {
                if (entry?.statName == null || !values.TryGetValue(entry.statName, out var value))
                    continue;
                entry.Text = new Text($"• {SimGameState_BuildSimGameStatsResults_Patch.DescribeStat(sim, entry.statName, value)}\n");
            }
        }
    }

    [HarmonyPatch(typeof(SimGameState), "ApplySimGameEventResult", new[] { typeof(SimGameEventResult), typeof(List<object>), typeof(SimGameEventTracker) }) ]
    public static class SimGameState_ApplySimGameEventResult_Patch
    {
        public static void Postfix()
        {
            var sim = UnityGameInstance.BattleTechGame.Simulation;
            if (Main.Settings.DependsOnArgoUpgrade && !sim.PurchasedArgoUpgrades.Contains(Main.Settings.ArgoUpgrade))
                return;
            
            Main.ConvertCompanyTags(true);
        }
    }
}
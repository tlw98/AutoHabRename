using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using PavonisInteractive.TerraInvicta;
using UnityModManagerNet;

namespace AutoHabRename
{
    public static class Main
    {
        public static bool enabled;
        public static UnityModManager.ModEntry mod;
        public static Settings settings;

        public static Dictionary<string, string> ModuleNames = new Dictionary<string, string>();
        public static Dictionary<string, string> OrbitNames = new Dictionary<string, string>();

        private static bool Load(UnityModManager.ModEntry modEntry)
        {
            settings = UnityModManager.ModSettings.Load<Settings>(modEntry);
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            mod = modEntry;
            modEntry.OnToggle = OnToggle;

            // .txt to prevent TI from thinking they are .json overrides
            ModuleNames = LoadFlatJson(modEntry, "module_names.txt");
            OrbitNames = LoadFlatJson(modEntry, "orbit_names.txt");

            var harmony = new Harmony(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            return true;
        }
        
        private static Dictionary<string, string> LoadFlatJson(UnityModManager.ModEntry modEntry, string fileName)
        {
            var path = Path.Combine(modEntry.Path, fileName);
            var result = new Dictionary<string, string>();
            try
            {
                var json = File.ReadAllText(path);
                // Regex to parse json instead of using a library, ran into issues (at least on Arch based linux)
                var matches = Regex.Matches(json, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                foreach (Match m in matches)
                {
                    var key = Regex.Unescape(m.Groups[1].Value);
                    var value = Regex.Unescape(m.Groups[2].Value);
                    result[key] = value;
                }

                modEntry.Logger.Log($"Loaded {result.Count} entries from {fileName}.");
            }
            catch (Exception e)
            {
                modEntry.Logger.LogException($"Failed to load {fileName}", e);
            }

            return result;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            enabled = value;
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            settings.Draw(modEntry);
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            settings.Save(modEntry);
        }
    }

    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        // Intention was to allow values to change in the UI
        // However, too many entries to manually do UMM Draws like below
        // TODO, investigate alternative later
        /*
        [Draw("ClimateLabs", Collapsible = true)]
        public string ClimateLabs = "CL";

        [Draw("InformationScienceLabs", Collapsible = true)]
        public string InformationScienceLabs = "IS";

        [Draw("Low Earth Orbit prefix", Collapsible = true)]
        public string LowEarthOrbitPrefix = "LEO";

        [Draw("Low Earth Orbit 2 prefix", Collapsible = true)]
        public string LowEarthOrbitPrefix2 = "LEO #2";
        */

        public void OnChange()
        {
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }

    public static class HabRenamePatches
    {
        // ! at the beginning of a station name prevents renaming
        private static bool IsRenameLocked(string currentName)
        {
            return !string.IsNullOrEmpty(currentName) && currentName.StartsWith("!");
        }

        private static string GetPatternForOrbit(string orbitDataName)
        {
            return Main.OrbitNames.TryGetValue(orbitDataName, out var pattern) && !string.IsNullOrEmpty(pattern)
                ? pattern
                : null;
        }

        // Shared helper function to build the station name
        // Station name is rebuilt each time a module is constructed or decommissioned
        private static string BuildStationName(TIHabState hab)
        {
            string prefix;
            var orbitPattern = hab.IsStation ? GetPatternForOrbit(hab.ref_orbit.template.dataName) : null;
            if (orbitPattern != null)
            {
                prefix = orbitPattern;
            }
            else
            {
                var current = hab.displayName ?? string.Empty;
                var dashIndex = current.IndexOf(" - ", StringComparison.Ordinal);
                prefix = dashIndex >= 0 ? current.Substring(0, dashIndex) : current;
            }

            var counts = new Dictionary<string, int>();
            foreach (var module in hab.AllModules())
            {
                var dataName = module.moduleTemplate?.dataName;
                if (dataName == null) continue;
                if (!Main.ModuleNames.TryGetValue(dataName, out var abbrev) || string.IsNullOrEmpty(abbrev)) continue;
                counts.TryGetValue(abbrev, out var existing);
                counts[abbrev] = existing + 1;
            }

            var tokens = counts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key} X{kv.Value}")
                .ToList();

            return tokens.Count > 0 ? $"{prefix} - {string.Join(" | ", tokens)}" : prefix;
        }

        // All of the patches below run the same rename logic, just at different times.        
        [HarmonyPatch(typeof(TIHabState), nameof(TIHabState.InitializeNewHab))]
        public static class RenameHabOnCreate
        {
            private static void Postfix(TIHabState __instance, TIFactionState faction)
            {
                if (__instance.IsStation && !faction.player.isAI && !IsRenameLocked(__instance.displayName))
                    __instance.SetDisplayName(BuildStationName(__instance));
            }
        }


        [HarmonyPatch(typeof(TIHabState), nameof(TIHabState.InitiateModuleConstruction))]
        public static class RenameHabOnModuleConstruction
        {
            private static void Postfix(TIHabState __instance)
            {
                if (__instance.IsStation && !__instance.ref_faction.player.isAI &&
                    !IsRenameLocked(__instance.displayName)) __instance.SetDisplayName(BuildStationName(__instance));
            }
        }

        
        [HarmonyPatch(typeof(TIHabState), nameof(TIHabState.CompleteDecommissionModule))]
        public static class RenameHabOnCompleteDecommissionModule
        {
            private static void Postfix(TIHabState __instance)
            {
                if (__instance.IsStation && !__instance.ref_faction.player.isAI &&
                    !IsRenameLocked(__instance.displayName)) __instance.SetDisplayName(BuildStationName(__instance));
            }
        }
    }
}
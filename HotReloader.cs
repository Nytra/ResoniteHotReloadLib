using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ResoniteModLoader;
using HarmonyLib;
using Mono.Cecil;
using System.Globalization;

namespace ResoniteHotReloadLib
{
    public static class HotReloader
    {
        static readonly List<ResoniteMod> HotReloadMods = new List<ResoniteMod>(); // list of mods setup for hot reloading

        static void Msg(string str) => ResoniteMod.Msg(str);
        static void Error(string str) => ResoniteMod.Error(str);

        private static void PrintTypeInfo(Type t)
        {
            Msg("Type FullName: " + t.FullName);
            Msg("Type Assembly FullName: " + t.Assembly.FullName);
        }

        private static string GetDLLPath(Type modInstanceType)
        {
            string dllPath;

            string executingAssemblyLocation = modInstanceType.Assembly.Location;

            string hotReloadModsDir = Path.GetDirectoryName(executingAssemblyLocation) + Path.DirectorySeparatorChar + "HotReloadMods";

            if (!Directory.Exists(hotReloadModsDir))
            {
                Error("HotReloadMods directory does not exist!");
                return null;
            }

            dllPath = hotReloadModsDir + Path.DirectorySeparatorChar + Path.GetFileName(executingAssemblyLocation);

            return dllPath;
        }

        private static bool DLLFileExists(string dllPath)
        {
            // Borrowed from ResoniteModLoader
            string[] foundFiles = Directory.EnumerateFiles(Path.GetDirectoryName(dllPath), "*.dll")
                .Where(file => file.EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase))
                .ToArray();

            bool found = false;
            foreach (string foundFile in foundFiles)
            {
                if (foundFile == dllPath)
                {
                    found = true;
                    break;
                }
            }

            return found;
        }

        public static void RegisterForHotReload(ResoniteMod mod)
        {
            Msg("Begin RegisterForHotReload.");
            Msg("Mod instance type info:");
            PrintTypeInfo(mod.GetType());
            if (!HotReloadMods.Contains(mod))
            {
                HotReloadMods.Add(mod);
                Msg("Mod registered for hot reload.");
            }
            else
            {
                Error("Mod was already registered for hot reload!");
            }
        }

        public static void HotReload(Type unloadType)
        {
            Msg("Begin HotReload.");

            ResoniteMod modInstance = null;

            foreach (ResoniteMod mod in HotReloadMods)
            {
                if (mod.GetType().Name == unloadType.Name)
                {
                    modInstance = mod;
                    break;
                }
            }

            if (modInstance == null)
            {
                Error("Mod instance is null! Mod not setup for hot reload!");
                return;
            }

            Msg("Mod instance info:");
            PrintTypeInfo(modInstance.GetType());
            Msg("Unload type info:");
            PrintTypeInfo(unloadType);

            MethodInfo unloadMethod = AccessTools.Method(unloadType, "BeforeHotReload");
            if (unloadMethod == null)
            {
                Error("Unload type does not have a unload method! Not hot reloading.");
                return;
            }

            Msg("Calling BeforeHotReload method...");
            unloadMethod.Invoke(modInstance, new object[] { });

            string dllPath = GetDLLPath(modInstance.GetType());

            if (dllPath == null)
            {
                Error("Could not get DLL path. Not hot reloading mod.");
                return;
            }

            Msg("Expected DLL Path: " + dllPath);
            if (!DLLFileExists(dllPath))
            {
                Error("DLL file does not exist in HotReloadMods directory! Cannot hot reload.");
                return;
            }

            Msg("Loading the new assembly...");

            var assemblyDefinition = AssemblyDefinition.ReadAssembly(dllPath);
            assemblyDefinition.Name.Name += "-" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
            var memoryStream = new MemoryStream();
            assemblyDefinition.Write(memoryStream);
            Assembly assembly = Assembly.Load(memoryStream.ToArray());

            Msg("Loaded assembly: " + assembly.FullName);

            Type targetType = null;
            foreach (Type type in assembly.GetTypes())
            {
                // The name of the ResoniteMod type 
                if (type.Name == modInstance.GetType().Name)
                {
                    Msg("Found ResoniteMod type in new assembly: " + type.FullName);
                    targetType = type;
                    break;
                }
            }

            if (targetType != null)
            {
                MethodInfo method = AccessTools.Method(targetType, "OnHotReload");
                if (method != null)
                {
                    Msg("Calling OnHotReload method...");
                    method.Invoke(null, new object[] { modInstance });
                }
                else
                {
                    Error("OnHotReload method is null!");
                }
            }
            else
            {
                Error("ResoniteMod type is null!");
            }
        }
    }
}

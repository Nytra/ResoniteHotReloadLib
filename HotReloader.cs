using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ResoniteModLoader;
using HarmonyLib;
using Mono.Cecil;
using System.Globalization;
using System.Collections;

namespace ResoniteHotReloadLib
{
	public static class HotReloader
	{
		static readonly List<ResoniteMod> HotReloadMods = new List<ResoniteMod>(); // list of mods setup for hot reloading

		static void Msg(string str) => ResoniteMod.Msg(str);
		static void Error(string str) => ResoniteMod.Error(str);

		private static void PrintTypeInfo(Type t)
		{
			Msg("Type Assembly FullName: " + t.Assembly.FullName);
			Msg("Type FullName: " + t.FullName);
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

		private static ResoniteMod ReinitializeMod(ResoniteMod originalModInstance, string newDllPath, Assembly newAssembly)
		{
			Msg("Begin ReinitializeMod.");

			Msg("Getting loadedResoniteMod from original ResoniteMod instance...");
			var origLoadedResoniteMod = AccessTools.Field(typeof(ResoniteModBase), "loadedResoniteMod").GetValue(originalModInstance);

			Msg("Getting AssemblyFile from loadedResoniteMod...");
			var origAssemblyFile = AccessTools.Property(origLoadedResoniteMod.GetType(), "ModAssembly").GetValue(origLoadedResoniteMod);

			Msg("Creating new AssemblyFile instance...");
			var newAssemblyFile = AccessTools.CreateInstance(origAssemblyFile.GetType());

			Msg("Setting new AssemblyFile values...");
			AccessTools.Field(newAssemblyFile.GetType(), "<File>k__BackingField").SetValue(newAssemblyFile, newDllPath);
			AccessTools.Property(newAssemblyFile.GetType(), "Assembly").SetValue(newAssemblyFile, newAssembly);
			AccessTools.Field(newAssemblyFile.GetType(), "sha256").SetValue(newAssemblyFile, null);

			Msg("Invoking InitializeMod method...");
			var newLoadedResoniteMod = AccessTools.Method(typeof(ModLoader), "InitializeMod").Invoke(null, new object[] { newAssemblyFile });

			Msg("Getting new ResoniteMod from new LoadedResoniteMod...");
			var newResoniteMod = (ResoniteMod)AccessTools.Property(origLoadedResoniteMod.GetType(), "ResoniteMod").GetValue(newLoadedResoniteMod);

			Msg("Setting FinishedLoading to true in new ResoniteMod instance...");
			AccessTools.Property(newResoniteMod.GetType(), "FinishedLoading").SetValue(newResoniteMod, true);

			Msg("Setting loadedResoniteMod in new ResoniteMod instance");
			AccessTools.Field(typeof(ResoniteModBase), "loadedResoniteMod").SetValue(newResoniteMod, newLoadedResoniteMod);

			Msg("Updating config definition...");
			UpdateConfigDefinition(newResoniteMod, originalModInstance.GetConfiguration());

			Msg("Adding loadedResoniteMod to loadedMods...");
			var loadedMods = (IList)AccessTools.Field(typeof(ModLoader), "LoadedMods").GetValue(null);
			loadedMods.Add(newLoadedResoniteMod);

			Msg("Adding ResoniteMod to assemblyLookupMap...");
			var assemblyLookupMap = (IDictionary)AccessTools.Field(typeof(ModLoader), "AssemblyLookupMap").GetValue(null);
			assemblyLookupMap.Add(newAssembly, newResoniteMod);

			Msg("Returning new ResoniteMod instance...");
			return newResoniteMod;
		}

		private static void UpdateConfigDefinition(ResoniteMod newResoniteMod, ModConfiguration oldConfig)
		{
			Msg("Begin UpdateConfigDefinition.");

			Msg("Invoking BuildConfigurationDefinition for new mod...");
			var newConfigDefinition = (ModConfigurationDefinition)AccessTools.Method(typeof(ResoniteMod), "BuildConfigurationDefinition").Invoke(newResoniteMod, new object[] { });

			foreach(var oldConfigKey in oldConfig.ConfigurationItemDefinitions)
			{
				// copy values from old to new
				foreach(var newConfigKey in newConfigDefinition.ConfigurationItemDefinitions)
				{
					if (newConfigKey.Name == oldConfigKey.Name && newConfigKey.ValueType().FullName == oldConfigKey.ValueType().FullName)
					{
						FieldInfo valueField = AccessTools.Field(typeof(ModConfigurationKey), "Value");
						var oldValue = valueField.GetValue(oldConfigKey);
						if (oldValue != null)
						{
							Msg("Copying not-null value to new key: " + newConfigKey.Name);
							valueField.SetValue(newConfigKey, oldValue);
							AccessTools.Field(typeof(ModConfigurationKey), "HasValue").SetValue(newConfigKey, true);
						}
						break;
					}
				}
			}

			Msg("Updating modInstance configuration definition...");
			AccessTools.Field(typeof(ModConfiguration), "Definition").SetValue(oldConfig, newConfigDefinition);
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
				Error("Unload type does not have a BeforeHotReload method!");
				return;
			}

			Msg("Calling BeforeHotReload method...");
			unloadMethod.Invoke(null, new object[] { });

			string dllPath = GetDLLPath(modInstance.GetType());

			if (dllPath == null)
			{
				Error("Could not get DLL path.");
				return;
			}

			Msg("Expecting to find mod DLL at path: " + dllPath);
			if (!DLLFileExists(dllPath))
			{
				Error("DLL file does not exist in HotReloadMods directory!");
				return;
			}

			Msg("Loading the new assembly...");

			var assemblyDefinition = AssemblyDefinition.ReadAssembly(dllPath);
			assemblyDefinition.Name.Name += "-" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
			var memoryStream = new MemoryStream();
			assemblyDefinition.Write(memoryStream);
			Assembly assembly = Assembly.Load(memoryStream.ToArray());

			Msg("Loaded assembly: " + assembly.FullName);

			Msg("Re-initializing ResoniteMod...");
			var newResoniteMod = ReinitializeMod(modInstance, dllPath, assembly);

			//Type targetType = null;
			//foreach (Type type in assembly.GetTypes())
			//{
				// The name of the ResoniteMod type 
				//if (type.Name == modInstance.GetType().Name)
				//{
					//Msg("Found ResoniteMod type in new assembly: " + type.FullName);
					//targetType = type;
					//break;
				//}
			//}

			if (newResoniteMod != null)
			{
				MethodInfo method = AccessTools.Method(newResoniteMod.GetType(), "OnHotReload");
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
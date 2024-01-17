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
		// list of mods setup for hot reloading
		// this probably doesn't need to be a list and could just be a reference to a single ResoniteMod
		// since the library right now is used by just one mod at a time and not multiple
		// but since I might try to integrate this into RML in the future it would be better to keep this as a list IMO
		static readonly List<ResoniteMod> HotReloadMods = new List<ResoniteMod>();

		static void Msg(string str) => ResoniteMod.Msg(str);
		static void Error(string str) => ResoniteMod.Error(str);
		static void Debug(string str) => ResoniteMod.Debug(str);
		static void Warn(string str) => ResoniteMod.Warn(str);

		private static void PrintTypeInfo(Type t)
		{
			Debug("Type Assembly FullName: " + t.Assembly.FullName);
			Debug("Type FullName: " + t.FullName);
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
			Debug("Begin RegisterForHotReload.");
			Debug("Mod instance type info:");
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

		// Uses a lot of reflection to initialize the mod via RML and the register it
		// This needs to create a new instance of AssemblyFile, give it the new values and then pass it to InitializeMod
		// Then it also registers the mod with RML so that the new mod will show up in ModLoader.Mods
		// This also makes it so when the new mod uses logging it will show the name of the mod in the log string
		private static ResoniteMod InitializeAndRegisterMod(ResoniteMod originalModInstance, string newDllPath, Assembly newAssembly)
		{
			Debug("Begin InitializeAndRegisterMod.");

			Debug("Getting original loadedResoniteMod...");
			var origLoadedResoniteMod = GetLoadedResoniteMod(originalModInstance);
			if (origLoadedResoniteMod == null) return null;

			Debug("Getting original AssemblyFile...");
			var origAssemblyFile = AccessTools.Property(origLoadedResoniteMod.GetType(), "ModAssembly")?.GetValue(origLoadedResoniteMod);
			if (origAssemblyFile == null) return null;

			Debug("Creating new AssemblyFile instance...");
			var newAssemblyFile = AccessTools.CreateInstance(origAssemblyFile.GetType());
			if (newAssemblyFile == null) return null;

			FieldInfo fileField = AccessTools.Field(newAssemblyFile.GetType(), "<File>k__BackingField");
			PropertyInfo assemblyProperty = AccessTools.Property(newAssemblyFile.GetType(), "Assembly");
			FieldInfo sha256Field = AccessTools.Field(newAssemblyFile.GetType(), "sha256");

			Debug("Setting AssemblyFile values...");
			if (!TrySetFieldValue(fileField, newAssemblyFile, newDllPath)) return null;
			if (!TrySetPropertyValue(assemblyProperty, newAssemblyFile, newAssembly)) return null;
			if (!TrySetFieldValue(sha256Field, newAssemblyFile, null)) return null;

			Debug("Invoking InitializeMod method...");
			var newLoadedResoniteMod = AccessTools.Method(typeof(ModLoader), "InitializeMod")?.Invoke(null, new object[] { newAssemblyFile });
			if (newLoadedResoniteMod == null) return null;

			Debug("Getting new ResoniteMod...");
			var newResoniteMod = (ResoniteMod)AccessTools.Property(origLoadedResoniteMod.GetType(), "ResoniteMod")?.GetValue(newLoadedResoniteMod);
			if (newResoniteMod == null) return null;

			// Below code emulates ModLoader.RegisterMod();
			// However it skips adding the mod name to ModNameLookupMap since that would block hot reloading

			// Makes the new mod show up in ModLoader.Mods
			Debug("Updating LoadedMods...");
			var loadedMods = (IList)AccessTools.Field(typeof(ModLoader), "LoadedMods")?.GetValue(null);
			if (loadedMods == null) return null;
			loadedMods.Add(newLoadedResoniteMod);

			// Makes it so when using logging the mod name will show up in the string
			Debug("Updating AssemblyLookupMap...");
			var assemblyLookupMap = (IDictionary)AccessTools.Field(typeof(ModLoader), "AssemblyLookupMap")?.GetValue(null);
			if(assemblyLookupMap == null) return null;
			assemblyLookupMap.Add(newAssembly, newResoniteMod);

			FieldInfo loadedResoniteModField = AccessTools.Field(typeof(ResoniteModBase), "loadedResoniteMod");

			Debug("Setting loadedResoniteMod...");
			if (!TrySetFieldValue(loadedResoniteModField, newResoniteMod, newLoadedResoniteMod)) return null;

			// Makes the config accessible
			Debug("Setting FinishedLoading to true...");
			PropertyInfo finishedLoadingProperty = AccessTools.Property(newResoniteMod.GetType(), "FinishedLoading");
			if (!TrySetPropertyValue(finishedLoadingProperty, newResoniteMod, true)) return null;

			Debug("Returning new ResoniteMod instance...");
			return newResoniteMod;
		}

		//private static ModConfigurationDefinition BuildConfigDefinition(ResoniteMod modInstance)
		//{
		//	var newConfigDefinition = (ModConfigurationDefinition)AccessTools.Method(typeof(ResoniteMod), "BuildConfigurationDefinition")?.Invoke(modInstance, new object[] { });
		//	return newConfigDefinition;
		//}

		private static ModConfigurationDefinition GetConfigDefinition(ModConfiguration modConfig)
		{
			var configDefinition = (ModConfigurationDefinition)AccessTools.Field(typeof(ModConfiguration), "Definition")?.GetValue(modConfig);
			return configDefinition;
		}

		private static object GetLoadedResoniteMod(ResoniteModBase modInstance)
		{
			var loadedResoniteMod = AccessTools.Field(typeof(ResoniteModBase), "loadedResoniteMod")?.GetValue(modInstance);
			return loadedResoniteMod;
		}

		private static void SetConfigNull(ResoniteMod modInstance)
		{
			var loadedResoniteMod = GetLoadedResoniteMod(modInstance);
			AccessTools.Property(loadedResoniteMod.GetType(), "ModConfiguration").SetValue(loadedResoniteMod, null);
		}

		// Updates the original mod config with the definition from new mod
		// So new config keys from the new assembly will work
		private static void UpdateConfigWithNewDefinition(ModConfiguration oldConfig, ModConfigurationDefinition newConfigDefinition)
		{
			Debug("Begin UpdateConfigWithNewDefinition.");

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
							Debug("Copying old key value to new key: " + newConfigKey.Name);
							valueField.SetValue(newConfigKey, oldValue);
							AccessTools.Field(typeof(ModConfigurationKey), "HasValue").SetValue(newConfigKey, true);
						}
						break;
					}
				}
			}

			Debug("Writing configuration definition...");
			AccessTools.Field(typeof(ModConfiguration), "Definition").SetValue(oldConfig, newConfigDefinition);
		}

		private static bool TrySetFieldValue(FieldInfo field, object instance, object value)
		{
			if (field != null)
			{
				try
				{
					field.SetValue(instance, value);
					return true;
				}
				catch (Exception ex)
				{
					Error($"Could not set field {field.Name} value: " + ex.ToString());
				}
			}
			else
			{
				Error($"Field {field.Name} is null");
			}
			return false;
		}

		private static bool TrySetPropertyValue(PropertyInfo property, object instance, object value)
		{
			if (property != null)
			{
				try
				{
					property.SetValue(instance, value);
					return true;
				}
				catch (Exception ex)
				{
					Error($"Could not set property {property.Name} value: " + ex.ToString());
				}
			}
			else
			{
				Error($"Property {property.Name} is null");
			}
			return false;
		}

		public static void HotReload(Type unloadType)
		{
			Msg("Begin HotReload for type: " + unloadType.FullName);

			ResoniteMod originalModInstance = null;

			foreach (ResoniteMod mod in HotReloadMods)
			{
				if (mod.GetType().FullName == unloadType.FullName)
				{
					originalModInstance = mod;
					break;
				}
			}

			if (originalModInstance == null)
			{
				Error("Mod instance is null! Mod not setup for hot reload!");
				return;
			}

			Debug("Mod instance info:");
			PrintTypeInfo(originalModInstance.GetType());
			Debug("Unload type info:");
			PrintTypeInfo(unloadType);

			MethodInfo unloadMethod = AccessTools.Method(unloadType, "BeforeHotReload");
			if (unloadMethod == null)
			{
				Error("Unload type does not have a BeforeHotReload method!");
				return;
			}

			Msg("Calling BeforeHotReload method...");
			unloadMethod.Invoke(null, new object[] { });

			string dllPath = GetDLLPath(originalModInstance.GetType());

			if (dllPath == null)
			{
				Error("Could not get DLL path.");
				return;
			}

			Debug("Expecting to find mod DLL at path: " + dllPath);

			if (!DLLFileExists(dllPath))
			{
				Error("DLL file does not exist in HotReloadMods directory!");
				return;
			}

			Debug("Loading the new assembly...");

			var assemblyDefinition = AssemblyDefinition.ReadAssembly(dllPath);
			assemblyDefinition.Name.Name += "-" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
			var memoryStream = new MemoryStream();
			assemblyDefinition.Write(memoryStream);
			Assembly assembly = Assembly.Load(memoryStream.ToArray());

			Msg("Loaded assembly: " + assembly.FullName);

			Msg("Initializing and registering new ResoniteMod with RML...");
			var newResoniteMod = InitializeAndRegisterMod(originalModInstance, dllPath, assembly);

			if (newResoniteMod != null)
			{
				// Stop the new resonite mod from autosaving the config on shutdown (because its config is not being used currently)
				Debug("Setting new resonite mod config to be null...");
				SetConfigNull(newResoniteMod);

				MethodInfo method = AccessTools.Method(newResoniteMod.GetType(), "OnHotReload");
				if (method != null)
				{
					Msg("Updating config definition...");
					ModConfigurationDefinition newConfigDefinition = GetConfigDefinition(newResoniteMod.GetConfiguration());
					UpdateConfigWithNewDefinition(originalModInstance.GetConfiguration(), newConfigDefinition);

					Msg("Calling OnHotReload method...");

					// Sending the original mod instance here just to remain compatible with ResoniteModSettings
					method.Invoke(null, new object[] { originalModInstance });
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
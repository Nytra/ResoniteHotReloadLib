﻿using System;
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
		// but since I might try to integrate this into RML in the future it would be better to keep this as a list
		static readonly List<ResoniteMod> HotReloadMods = new List<ResoniteMod>();

		static void Msg(string str) => ResoniteMod.Msg(str);
		static void Error(string str) => ResoniteMod.Error(str);
		static void Debug(string str) => ResoniteMod.Debug(str);
		static void Warn(string str) => ResoniteMod.Warn(str);

		// RML Types
		// So when I try to get these types by name it freezes Resonite...
		// So I will get them from objects instead

		static Type LoadedResoniteMod = null;
		static Type AssemblyFile = null;

		// LoadedResoniteMod
		static PropertyInfo LoadedResoniteMod_ModAssembly; 
		static PropertyInfo LoadedResoniteMod_ResoniteMod; 
		static PropertyInfo LoadedResoniteMod_ModConfiguration; 

		// AssemblyFile
		static FieldInfo AssemblyFile_File; 
		static PropertyInfo AssemblyFile_Assembly; 
		static FieldInfo AssemblyFile_sha256;

		// ModLoader
		static MethodInfo ModLoader_InitializeMod = AccessTools.Method(typeof(ModLoader), "InitializeMod");
		static FieldInfo ModLoader_LoadedMods = AccessTools.Field(typeof(ModLoader), "LoadedMods");
		static FieldInfo ModLoader_AssemblyLookupMap = AccessTools.Field(typeof(ModLoader), "AssemblyLookupMap");

		// ResoniteModBase
		static FieldInfo ResoniteModBase_loadedResoniteMod = AccessTools.Field(typeof(ResoniteModBase), "loadedResoniteMod");
		static PropertyInfo ResoniteModBase_FinishedLoading = AccessTools.Property(typeof(ResoniteModBase), "FinishedLoading");

		// ModConfiguration
		static FieldInfo ModConfiguration_Definition = AccessTools.Field(typeof(ModConfiguration), "Definition");
		
		// ModConfigurationKey
		static FieldInfo ModConfigurationKey_Value = AccessTools.Field(typeof(ModConfigurationKey), "Value");
		static FieldInfo ModConfigurationKey_HasValue = AccessTools.Field(typeof(ModConfigurationKey), "HasValue");

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

		// Uses a lot of reflection to initialize the mod via RML and then register it
		// This needs to create a new instance of AssemblyFile, give it the new values and then pass it to InitializeMod
		// Then it also registers the mod with RML so that the new mod will show up in ModLoader.Mods()
		// This also makes it so when the new mod uses logging it will show the name of the mod in the log string
		private static ResoniteMod InitializeAndRegisterMod(ResoniteMod originalModInstance, string newDllPath, Assembly newAssembly)
		{
			Debug("Begin InitializeAndRegisterMod.");

			Debug("Getting original loadedResoniteMod...");
			var origLoadedResoniteMod = GetLoadedResoniteMod(originalModInstance);
			if (origLoadedResoniteMod == null) return null;

			if (LoadedResoniteMod == null)
			{
				LoadedResoniteMod = origLoadedResoniteMod.GetType();
				LoadedResoniteMod_ModAssembly = AccessTools.Property(LoadedResoniteMod, "ModAssembly");
				LoadedResoniteMod_ResoniteMod = AccessTools.Property(LoadedResoniteMod, "ResoniteMod");
				LoadedResoniteMod_ModConfiguration = AccessTools.Property(LoadedResoniteMod, "ModConfiguration");
			}

			Debug("Getting original AssemblyFile...");
			var origAssemblyFile = LoadedResoniteMod_ModAssembly?.GetValue(origLoadedResoniteMod);
			if (origAssemblyFile == null) return null;

			if (AssemblyFile == null)
			{
				AssemblyFile = origAssemblyFile.GetType();
				AssemblyFile_File = AccessTools.Field(AssemblyFile, "<File>k__BackingField");
				AssemblyFile_Assembly = AccessTools.Property(AssemblyFile, "Assembly");
				AssemblyFile_sha256 = AccessTools.Field(AssemblyFile, "sha256");
			}

			Debug("Creating new AssemblyFile instance...");
			var newAssemblyFile = AccessTools.CreateInstance(AssemblyFile);
			if (newAssemblyFile == null) return null;

			Debug("Setting AssemblyFile values...");
			if (!TrySetFieldValue(AssemblyFile_File, newAssemblyFile, newDllPath)) return null;
			if (!TrySetPropertyValue(AssemblyFile_Assembly, newAssemblyFile, newAssembly)) return null;
			if (!TrySetFieldValue(AssemblyFile_sha256, newAssemblyFile, null)) return null;

			Debug("Invoking InitializeMod method...");
			var newLoadedResoniteMod = ModLoader_InitializeMod?.Invoke(null, new object[] { newAssemblyFile });
			if (newLoadedResoniteMod == null) return null;

			Debug("Getting new ResoniteMod...");
			var newResoniteMod = (ResoniteMod)LoadedResoniteMod_ResoniteMod?.GetValue(newLoadedResoniteMod);
			if (newResoniteMod == null) return null;

			// Below code emulates ModLoader.RegisterMod();
			// However it skips adding the mod name to ModNameLookupMap since that would block hot reloading

			// Makes the new mod show up in ModLoader.Mods
			Debug("Updating LoadedMods...");
			var loadedMods = (IList)ModLoader_LoadedMods?.GetValue(null);
			if (loadedMods == null) return null;
			loadedMods.Add(newLoadedResoniteMod);

			// Makes it so when using logging the mod name will show up in the string
			Debug("Updating AssemblyLookupMap...");
			var assemblyLookupMap = (IDictionary)ModLoader_AssemblyLookupMap?.GetValue(null);
			if(assemblyLookupMap == null) return null;
			assemblyLookupMap.Add(newAssembly, newResoniteMod);

			Debug("Setting loadedResoniteMod...");
			if (!TrySetFieldValue(ResoniteModBase_loadedResoniteMod, newResoniteMod, newLoadedResoniteMod)) return null;

			// Makes GetConfiguration() not throw an exception
			Debug("Setting FinishedLoading to true...");
			if (!TrySetPropertyValue(ResoniteModBase_FinishedLoading, newResoniteMod, true)) return null;

			return newResoniteMod;
		}

		//private static ModConfigurationDefinition BuildConfigDefinition(ResoniteMod modInstance)
		//{
		//	var newConfigDefinition = (ModConfigurationDefinition)AccessTools.Method(typeof(ResoniteMod), "BuildConfigurationDefinition")?.Invoke(modInstance, new object[] { });
		//	return newConfigDefinition;
		//}

		private static ModConfigurationDefinition GetConfigDefinition(ModConfiguration modConfig)
		{
			var configDefinition = (ModConfigurationDefinition)ModConfiguration_Definition?.GetValue(modConfig);
			return configDefinition;
		}

		private static object GetLoadedResoniteMod(ResoniteModBase modInstance)
		{
			var loadedResoniteMod = ResoniteModBase_loadedResoniteMod?.GetValue(modInstance);
			return loadedResoniteMod;
		}

		private static bool SetConfigNull(ResoniteMod modInstance)
		{
			var loadedResoniteMod = GetLoadedResoniteMod(modInstance);
			if (loadedResoniteMod == null) return false;
			if (!TrySetPropertyValue(LoadedResoniteMod_ModConfiguration, loadedResoniteMod, null)) return false;
			return true;
		}

		private static bool TryConvertType(object obj, Type type, out object converted)
		{
			converted = null;
			try
			{
				converted = Convert.ChangeType(obj, type);
				return true;
			}
			catch
			{
				Debug("Initial type conversion failed.");
				if (type.IsEnum && obj.GetType().IsEnum)
				{
					Debug("Trying to convert enum type...");
					try
					{
						// thank you ChatGPT
						converted = Enum.ToObject(type, Convert.ChangeType(obj, Enum.GetUnderlyingType(type)));
						if (Enum.IsDefined(type, converted))
						{
							return true;
						}
						Debug("Converted enum value is not defined.");
						converted = null;
					}
					catch
					{
					}
				}
				Error("Could not convert type.");
				return false;
			}
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
						var oldValue = ModConfigurationKey_Value?.GetValue(oldConfigKey);
						if (oldValue != null)
						{
							Debug("Found matching config key: " + newConfigKey.Name + ", Value: " + oldValue.ToString());
							var objectToValidate = oldValue;
							if (oldValue.GetType() != newConfigKey.ValueType())
							{
								Debug("Type mismatch. Trying to convert...");
								if (TryConvertType(oldValue, newConfigKey.ValueType(), out object converted))
								{
									Debug("Type conversion succeeded.");
									objectToValidate = converted;
								}
							}
							if (newConfigKey.Validate(objectToValidate))
							{
								Debug("Value is valid.");
								TrySetFieldValue(ModConfigurationKey_Value, newConfigKey, objectToValidate);
								TrySetFieldValue(ModConfigurationKey_HasValue, newConfigKey, true);
							}
							else
							{
								Debug("Value is not valid.");
							}
						}
						break;
					}
				}
			}
			Debug("Writing configuration definition...");
			TrySetFieldValue(ModConfiguration_Definition, oldConfig, newConfigDefinition);
		}

		// Returns true if success, false if fail
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
					Error($"Could not set value for field {field.Name}: " + ex.ToString());
				}
			}
			else
			{
				Error($"Field {field.Name} is null");
			}
			return false;
		}

		// Returns true if success, false if fail
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
					Error($"Could not set value for property {property.Name}: " + ex.ToString());
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
				MethodInfo method = AccessTools.Method(newResoniteMod.GetType(), "OnHotReload");
				if (method != null)
				{
					Msg("Updating config definition...");
					ModConfigurationDefinition newConfigDefinition = GetConfigDefinition(newResoniteMod.GetConfiguration());
					UpdateConfigWithNewDefinition(originalModInstance.GetConfiguration(), newConfigDefinition);

					// Stop the new resonite mod from autosaving its config on shutdown (because its config will have outdated values since it is not being used)
					// If this fails it might not be a huge problem, but just to be safe I will stop the reload 
					Debug("Setting new resonite mod config to be null...");
					if (!SetConfigNull(newResoniteMod))
					{
						Error("Could not set config to be null!");
						return;
					}

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
				Error("New ResoniteMod instance is null!");
			}
		}
	}
}
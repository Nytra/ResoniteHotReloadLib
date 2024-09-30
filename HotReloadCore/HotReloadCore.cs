using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ResoniteHotReloadLib")]
//[assembly: InternalsVisibleTo("MonkeyHotReloader")]

namespace ResoniteHotReloadLib
{
	internal static class HotReloadCore
	{
		// list of mods setup for hot reloading
		static readonly List<ResoniteMod> _hotReloadMods = new();
		static readonly Dictionary<ResoniteMod, int> _timesHotReloaded = new();
		const string HOT_RELOAD_OPTIONS_PATH = "Hot Reload Mods";
		const string HOT_RELOAD_DIRECTORY_NAME = "HotReloadMods";

		static void Msg(string str) => ResoniteMod.Msg(str);
		static void Error(string str) => ResoniteMod.Error(str);
		static void Debug(string str) => ResoniteMod.Debug(str);
		static void Warn(string str) => ResoniteMod.Warn(str);

		// Mono.Cecil.AssemblyDefinition
		static Type AssemblyDefinition = AccessTools.TypeByName("Mono.Cecil.AssemblyDefinition");
		static PropertyInfo AssemblyDefinition_Name = AccessTools.Property(AssemblyDefinition, "Name");
		static MethodInfo AssemblyDefinition_ReadAssembly = AccessTools.Method(AssemblyDefinition, "ReadAssembly", new Type[] { typeof(string) });
		static MethodInfo AssemblyDefinition_Write = AccessTools.Method(AssemblyDefinition, "Write", new Type[] { typeof(System.IO.Stream) });

		// Mono.Cecil.AssemblyNameDefinition
		static Type AssemblyNameDefinition = AccessTools.TypeByName("Mono.Cecil.AssemblyNameDefinition");
		static PropertyInfo AssemblyNameDefinition_Name = AccessTools.Property(AssemblyNameDefinition, "Name");

		private static void PrintTypeInfo(Type t)
		{
			Debug("Type Assembly FullName: " + t.Assembly.FullName);
			Debug("Type FullName: " + t.FullName);
		}

		private static string GetDLLPath(Type modInstanceType)
		{
			string executingAssemblyLocation = modInstanceType.Assembly.Location;

			string hotReloadModsDir = Path.GetDirectoryName(executingAssemblyLocation) + Path.DirectorySeparatorChar + HOT_RELOAD_DIRECTORY_NAME;

			if (!Directory.Exists(hotReloadModsDir))
			{
				Error("HotReloadMods directory does not exist!");
				return null;
				//throw new Exception($"{HOT_RELOAD_DIRECTORY_NAME} directory does not exist!");
			}

			string dllPath = hotReloadModsDir + Path.DirectorySeparatorChar + Path.GetFileName(executingAssemblyLocation);

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

		internal static void RegisterForHotReload(ResoniteMod mod, Action reloadAction)
		{
			Debug("Begin RegisterForHotReload.");
			Debug("Mod instance type info:");
			PrintTypeInfo(mod.GetType());
			if (!_hotReloadMods.Contains(mod))
			{
				_hotReloadMods.Add(mod);
				_timesHotReloaded.Add(mod, 0);
				AddReloadMenuOption(mod, reloadAction);
				Msg("Mod registered for hot reload.");
			}
			else
			{
				Error("Mod was already registered for hot reload!");
			}
		}

		//private static ModConfigurationDefinition BuildConfigDefinition(ResoniteMod modInstance)
		//{
		//	var newConfigDefinition = (ModConfigurationDefinition)AccessTools.Method(typeof(ResoniteMod), "BuildConfigurationDefinition")?.Invoke(modInstance, new object[] { });
		//	return newConfigDefinition;
		//}

		//private static bool SetConfig(ResoniteMod modInstance, object newConfig)
		//{
		//	if (!TrySetPropertyValue(ResoniteModBase_ModConfiguration, modInstance, newConfig)) return false;
		//	return true;
		//}

		//private static bool TryConvertType(object obj, Type type, out object converted)
		//{
		//	converted = null;
		//	try
		//	{
		//		converted = Convert.ChangeType(obj, type);
		//		return true;
		//	}
		//	catch
		//	{
		//		Debug("Initial type conversion failed.");
		//		if (type.IsEnum && obj.GetType().IsEnum && Enum.GetUnderlyingType(type) == Enum.GetUnderlyingType(obj.GetType()))
		//		{
		//			Debug("Trying to convert enum type...");
		//			try
		//			{
		//				// thank you ChatGPT
		//				converted = Enum.ToObject(type, Convert.ChangeType(obj, Enum.GetUnderlyingType(type)));
		//				if (Enum.IsDefined(type, converted))
		//				{
		//					return true;
		//				}
		//				Debug("Converted enum value is not defined.");
		//				converted = null;
		//			}
		//			catch
		//			{
		//			}
		//		}
		//		return false;
		//	}
		//}

		// Updates the original mod config with the definition from new mod and copies existing values over
		// So new config keys from the new assembly will work
		// And values from the old config will still be accessible
		//private static void UpdateConfigWithNewDefinition(ModConfiguration oldConfig, ModConfigurationDefinition newConfigDefinition)
		//{
		//	Debug("Begin UpdateConfigWithNewDefinition.");

		//	foreach (var oldConfigKey in oldConfig.ConfigurationItemDefinitions)
		//	{
		//		// copy values from old to new
		//		foreach (var newConfigKey in newConfigDefinition.ConfigurationItemDefinitions)
		//		{
		//			if (newConfigKey.Name == oldConfigKey.Name && newConfigKey.ValueType().FullName == oldConfigKey.ValueType().FullName)
		//			{
		//				var oldValue = oldConfigKey.Value;
		//				if (oldConfigKey.HasValue)
		//				{
		//					Debug("Found matching config key: " + newConfigKey.Name + ", Value: " + oldValue.ToString());
		//					var objectToValidate = oldValue;
		//					if (oldValue.GetType() != newConfigKey.ValueType())
		//					{
		//						Debug("Type mismatch. Trying to convert...");
		//						if (TryConvertType(oldValue, newConfigKey.ValueType(), out object converted))
		//						{
		//							Debug("Type conversion succeeded.");
		//							objectToValidate = converted;
		//						}
		//						else
		//						{
		//							Error("Could not convert type.");
		//						}
		//					}
		//					if (newConfigKey.Validate(objectToValidate))
		//					{
		//						Debug("Value is valid.");
		//						newConfigKey.Value = objectToValidate;
		//						newConfigKey.HasValue = true;
		//					}
		//					else
		//					{
		//						Debug("Value is not valid.");
		//					}
		//				}
		//				break;
		//			}
		//		}
		//	}
		//	Debug("Writing configuration definition...");
		//	TrySetFieldValue(ModConfiguration_Definition, oldConfig, newConfigDefinition); // set readonly field with reflection
		//}

		// Returns true if success, false if fail
		//private static bool TrySetFieldValue(FieldInfo field, object instance, object value)
		//{
		//	if (field != null)
		//	{
		//		try
		//		{
		//			field.SetValue(instance, value);
		//			return true;
		//		}
		//		catch (Exception ex)
		//		{
		//			Error($"Could not set value for field {field.Name}: " + ex.ToString());
		//		}
		//	}
		//	else
		//	{
		//		Error($"Field is null for object with type name: {instance?.GetType().Name ?? "NULL"}");
		//	}
		//	return false;
		//}

		// Returns true if success, false if fail
		//private static bool TrySetPropertyValue(PropertyInfo property, object instance, object value)
		//{
		//	if (property != null)
		//	{
		//		try
		//		{
		//			property.SetValue(instance, value);
		//			return true;
		//		}
		//		catch (Exception ex)
		//		{
		//			Error($"Could not set value for property {property.Name}: " + ex.ToString());
		//		}
		//	}
		//	else
		//	{
		//		Error($"Property is null for object with type name: {instance?.GetType().Name ?? "NULL"}");
		//	}
		//	return false;
		//}

		internal static int GetReloadedCountOfModType(Type modType)
		{
			return _timesHotReloaded.FirstOrDefault(kVP => kVP.Key.GetType().FullName == modType.FullName).Value;
		}

		private static string GetReloadString(ResoniteMod mod)
		{
			int count = GetReloadedCountOfModType(mod.GetType());
			return $"({count}) Reload {mod.Name ?? "NULL"} by {mod.Author ?? "NULL"}";
		}

		internal static void AddReloadMenuOption(ResoniteMod mod, Action pressedAction)
		{
			Debug("Begin AddReloadMenuOption");
			if (!Engine.Current.IsInitialized)
			{
				Engine.Current.RunPostInit(AddActionDelegate);
			}
			else
			{
				AddActionDelegate();
			}
			void AddActionDelegate()
			{
				string reloadString = GetReloadString(mod);
				DevCreateNewForm.AddAction(HOT_RELOAD_OPTIONS_PATH, reloadString, (x) =>
				{
					x.Destroy();

					Msg($"Hot reload button pressed for mod {mod.Name ?? "NULL"} by {mod.Author ?? "NULL"}.");

					pressedAction();
				});
				Debug($"Added reload menu option: {reloadString}");
			}
		}

		internal static bool RemoveMenuOption(string path, string optionName)
		{
			Debug("Begin RemoveMenuOption");
			Debug($"Path: {path} optionName: {optionName}");
			object categoryNode = AccessTools.Field(typeof(DevCreateNewForm), "root").GetValue(null);
			object subcategory = AccessTools.Method(categoryNode.GetType(), "GetSubcategory").Invoke(categoryNode, new object[] { path });
			System.Collections.IList elements = (System.Collections.IList)AccessTools.Field(categoryNode.GetType(), "_elements").GetValue(subcategory);
			if (elements == null)
			{
				Debug("Elements is null!");
				return false;
			}
			foreach (object categoryItem in elements)
			{
				var name = (string)AccessTools.Field(categoryNode.GetType().GetGenericArguments()[0], "name").GetValue(categoryItem);
				//var action = (Action<Slot>)AccessTools.Field(categoryItemType, "action").GetValue(categoryItem);
				if (name == optionName)
				{
					elements.Remove(categoryItem);
					Debug("Menu option removed.");
					return true;
				}
			}
			return false;
		}

		internal static (Assembly, ResoniteMod, string) PrepareHotReload(Type unloadType)
		{
			if (!typeof(ResoniteMod).IsAssignableFrom(unloadType))
			{
				Error("Unload type is not a ResoniteMod!");
				return (null, null, null);
				//throw new Exception("Unload type is not an expected type!");
			}

			Debug("Begin prepare hot reload for type: " + unloadType.FullName);

			ResoniteMod originalModInstance = null;
			foreach (ResoniteMod mod in _hotReloadMods)
			{
				if (mod.GetType().FullName == unloadType.FullName)
				{
					originalModInstance = mod;
					break;
				}
			}

			if (originalModInstance == null)
			{
				Error("Original mod instance is null! Mod not registered for hot reload!");
				return (null, null, null);
			}

			Debug("Original mod instance info:");
			PrintTypeInfo(originalModInstance.GetType());
			Debug("Unload type info:");
			PrintTypeInfo(unloadType);

			MethodInfo unloadMethod = AccessTools.Method(unloadType, "BeforeHotReload");
			if (unloadMethod == null)
			{
				Error("Unload type does not have a BeforeHotReload method!");
				return (null, originalModInstance, null);
			}

			MethodInfo reloadMethod = AccessTools.Method(unloadType, "OnHotReload");
			if (reloadMethod == null)
			{
				Error("Unload type does not have a OnHotReload method!");
				return (null, originalModInstance, null);
			}

			string dllPath = GetDLLPath(originalModInstance.GetType());

			if (dllPath == null)
			{
				Error("DLL path is null!");
				return (null, originalModInstance, null);
			}

			Debug("Expecting to find mod DLL at path: " + dllPath);

			if (!DLLFileExists(dllPath))
			{
				Error($"DLL file does not exist in {HOT_RELOAD_DIRECTORY_NAME} directory!");
				return (null, originalModInstance, dllPath);
			}

			Debug("Found the DLL.");

			// Begin the actual reload process

			Msg("Removing hot reload menu option...");
			RemoveMenuOption(HOT_RELOAD_OPTIONS_PATH, GetReloadString(originalModInstance));

			Msg("Calling BeforeHotReload method...");
			unloadMethod.Invoke(null, new object[] { });

			Debug("Loading the new assembly...");

			// Reflection for mono.cecil because these are non-public
			var assemblyDefinition = AssemblyDefinition_ReadAssembly.Invoke(null, new object[] { dllPath });
			var assemblyNameDefinition = AssemblyDefinition_Name.GetValue(assemblyDefinition);
			var assemblyNameDefinitionName = AssemblyNameDefinition_Name.GetValue(assemblyNameDefinition);
			AssemblyNameDefinition_Name.SetValue(assemblyNameDefinition, (string)assemblyNameDefinitionName + "-" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));
			var memoryStream = new MemoryStream();
			AssemblyDefinition_Write.Invoke(assemblyDefinition, new object[] { memoryStream });
			Assembly assembly = Assembly.Load(memoryStream.ToArray());

			Msg("Loaded assembly: " + assembly.FullName);

			return (assembly, originalModInstance, dllPath);
		}

		internal static void FinalizeHotReload(ResoniteMod originalModInstance, ResoniteMod newModInstance, Action reloadAction)
		{
			if (_timesHotReloaded.ContainsKey(originalModInstance))
			{
				_timesHotReloaded[originalModInstance]++;
			}
			else
			{
				Error("timesHotReloaded did not contain original mod instance!");
				return;
			}

			Debug("Adding hot reload menu option...");
			AddReloadMenuOption(newModInstance, reloadAction);
		}

		//	//Msg("Adding hot reload menu option...");
			//	//AddReloadMenuOption(newModInstance, MenuAction);
			//}

			//public static void HotReload(Type unloadType)
			//{


			//	ResoniteMod newResoniteMod = null;
			//	Msg("Initializing and registering new ResoniteMod with RML...");
			//	newResoniteMod = InitializeAndRegisterMod(originalModInstance, dllPath, assembly);

			//	if (newResoniteMod != null)
			//	{
			//		MethodInfo method = AccessTools.Method(newResoniteMod.GetType(), "OnHotReload");
			//		if (method != null)
			//		{
			//			if (newResoniteMod.GetConfiguration() != null)
			//			{
			//				if (originalModInstance.GetConfiguration() != null)
			//				{
			//					Msg("Updating config definition...");
			//					var newConfigDefinition = (ModConfigurationDefinition)ModConfiguration_Definition.GetValue(newResoniteMod.GetConfiguration());
			//					UpdateConfigWithNewDefinition(originalModInstance.GetConfiguration(), newConfigDefinition);
			//				}
			//				else
			//				{
			//					Warn("Original mod config is null! Replacing it with new config...");
			//					if (!SetConfig(originalModInstance, newResoniteMod.GetConfiguration()))
			//					{
			//						Error("Could not set config!");
			//						return;
			//					}
			//				}

			//				// Stop the new resonite mod from autosaving its config on shutdown (because its config will have outdated values since it is not being used)
			//				// If this fails it might not be a huge problem, but just to be safe I will stop the reload
			//				Debug("Setting new resonite mod config to be null...");
			//				if (!SetConfig(newResoniteMod, null))
			//				{
			//					Error("Could not set config to be null!");
			//					return;
			//				}
			//			}
			//			else
			//			{
			//				Msg("Config is null for new mod.");
			//			}





			//			Msg("Calling OnHotReload method...");
			//			// Sending the original mod instance here just to remain compatible with ResoniteModSettings
			//			method.Invoke(null, new object[] { originalModInstance });
			//		}
			//		else
			//		{
			//			Error("OnHotReload method is null in new assembly!");
			//		}
			//	}
			//	else
			//	{
			//		Error("New ResoniteMod instance is null!");
			//	}
			//}
	}
}
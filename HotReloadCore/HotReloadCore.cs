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
				Error($"{HOT_RELOAD_DIRECTORY_NAME} directory does not exist!");
				return null;
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

		internal static int GetReloadedCountOfModType(Type modType)
		{
			return _timesHotReloaded.FirstOrDefault(kVP => kVP.Key.GetType().FullName == modType.FullName).Value;
		}

		private static string GetReloadString(ResoniteMod mod)
		{
			int count = GetReloadedCountOfModType(mod.GetType());
			return $"({count}) Reload {mod.Name ?? "NULL"} by {mod.Author ?? "NULL"}";
		}

		internal static void AddReloadMenuOption(ResoniteMod mod, Action reloadAction)
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

					reloadAction();
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
			Debug("Removing hot reload menu option...");
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
	}
}
using Elements.Assets;
using FrooxEngine;
using HarmonyLib;
using MonkeyLoader.Meta;
using MonkeyLoader.NuGet;
using Mono.Cecil;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using ResoniteModLoader;
using System;
using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Zio;
using Zio.FileSystems;

namespace ResoniteHotReloadLib
{
	internal sealed class HotReloadedMonkeyMod : Mod
	{
		private static readonly Uri _rmlIconUrl = new("https://avatars.githubusercontent.com/u/145755526");

		public override string Description => "Hot Reloaded RML Mod";

		public override IFileSystem FileSystem { get; }

		public override UPath? IconPath => null;

		public override Uri? IconUrl => _rmlIconUrl;

		public override PackageIdentity Identity { get; }

		public override Uri? ProjectUrl { get; }

		public override string? ReleaseNotes => null;

		public override bool SupportsHotReload => false;

		public override NuGetFramework TargetFramework => NuGetHelper.Framework;

		public HotReloadedMonkeyMod(ResoniteMod resoniteMod, string id, MonkeyLoader.MonkeyLoader loader)
			: base(loader, null, false)
		{
			FileSystem = new MemoryFileSystem() { Name = $"Dummy FileSystem for {id}" };

			NuGetVersion version;
			if (!NuGetVersion.TryParse(resoniteMod.Version, out version!))
				version = new(1, 0, 0);

			Identity = new PackageIdentity(id, version);

			if (Uri.TryCreate(resoniteMod.Link, UriKind.Absolute, out var projectUrl))
				ProjectUrl = projectUrl;

			authors.Add(resoniteMod.Author);
			monkeys.Add((IMonkey)resoniteMod);
		}

		public override bool OnLoadEarlyMonkeys() => true;

		public override bool OnLoadMonkeys() => true;
	}

	public static class HotReloader
	{
		// list of mods setup for hot reloading
		static readonly List<ResoniteMod> HotReloadMods = new List<ResoniteMod>();
		static readonly Dictionary<ResoniteMod, int> timesHotReloaded = new Dictionary<ResoniteMod, int>();
		const string HOT_RELOAD_OPTIONS_PATH = "Hot Reload Mods";

		static void Msg(string str) => ResoniteMod.Msg(str);
		static void Error(string str) => ResoniteMod.Error(str);
		static void Debug(string str) => ResoniteMod.Debug(str);
		static void Warn(string str) => ResoniteMod.Warn(str);

		// RML Types
		// When I try to get the AssemblyFile type by name it freezes Resonite...
		// So I will get it from a object instead

		static Type AssemblyFile = null;

		// AssemblyFile
		static FieldInfo AssemblyFile_File = null;

		// ResoniteModBase
		static PropertyInfo ResoniteModBase_ModConfiguration = AccessTools.Property(typeof(ResoniteModBase), "ModConfiguration");

		// ModConfiguration
		static FieldInfo ModConfiguration_Definition = AccessTools.Field(typeof(ModConfiguration), "Definition");

		// Mono.Cecil.AssemblyDefinition
		static Type AssemblyDefinition = AccessTools.TypeByName("Mono.Cecil.AssemblyDefinition");
		static PropertyInfo AssemblyDefinition_Name = AccessTools.Property(AssemblyDefinition, "Name");
		static MethodInfo AssemblyDefinition_ReadAssembly = AccessTools.Method(AssemblyDefinition, "ReadAssembly", new Type[] { typeof(string) });
		static MethodInfo AssemblyDefinition_Write = AccessTools.Method(AssemblyDefinition, "Write", new Type[] { typeof(System.IO.Stream) });

		// Mono.Cecil.AssemblyNameDefinition
		static Type AssemblyNameDefinition = AccessTools.TypeByName("Mono.Cecil.AssemblyNameDefinition");
		static PropertyInfo AssemblyNameDefinition_Name = AccessTools.Property(AssemblyNameDefinition, "Name");

		// MonkeyLoader RML
		static Type RmlMod = typeof(ModLoader).Assembly.GetTypes().FirstOrDefault(type => type.Name == "RmlMod");
		static FieldInfo RmlMod_AssemblyLookupMap = AccessTools.Field(RmlMod, "AssemblyLookupMap");
		static PropertyInfo ResoniteModBase_Mod = AccessTools.Property(typeof(ResoniteModBase), "Mod");

		private static bool IsMonkeyLoaderModType(Type t)
		{
			return t.GetInterfaces().Any(type => type.Name == "IMonkey");
		}

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
				timesHotReloaded.Add(mod, 0);
				AddReloadMenuOption(mod);
				Msg("Mod registered for hot reload.");
			}
			else
			{
				Error("Mod was already registered for hot reload!");
			}
		}

		// initialize the mod via RML and then register it
		// This needs to create a new instance of AssemblyFile, give it the new values and then pass it to InitializeMod
		// Then it also registers the mod with RML so that the new mod will show up in ModLoader.Mods()
		// This also makes it so when the new mod uses logging it will show the name of the mod in the log string
		private static ResoniteMod InitializeAndRegisterMod(ResoniteMod originalModInstance, string newDllPath, Assembly newAssembly)
		{
			Debug("Begin InitializeAndRegisterMod.");

			var origAssemblyFile = originalModInstance.ModAssembly;

			// Cache the assemblyfile type
			if (AssemblyFile == null)
			{
				AssemblyFile = origAssemblyFile.GetType();
				AssemblyFile_File = AccessTools.Field(AssemblyFile, "<File>k__BackingField");
			}

			Debug("Creating new AssemblyFile instance...");
			var newAssemblyFile = (AssemblyFile)AccessTools.CreateInstance(AssemblyFile);

			Debug("Setting AssemblyFile values...");
			if (!TrySetFieldValue(AssemblyFile_File, newAssemblyFile, newDllPath)) return null;
			newAssemblyFile.Assembly = newAssembly;
			newAssemblyFile.sha256 = null;

			Debug("Invoking InitializeMod method to get new ResoniteMod...");
			var newResoniteMod = ModLoader.InitializeMod(newAssemblyFile);
			if (newResoniteMod == null) return null;

			// Below code emulates ModLoader.RegisterMod();
			// However it skips adding the mod name to ModNameLookupMap since that would block hot reloading

			// Makes the new mod show up in ModLoader.Mods
			Debug("Updating LoadedMods...");
			ModLoader.LoadedMods.Add(newResoniteMod);

			// Makes it so when using logging the mod name will show up in the string
			Debug("Updating AssemblyLookupMap...");
			ModLoader.AssemblyLookupMap.Add(newAssembly, newResoniteMod);

			// Makes GetConfiguration() not throw an exception
			Debug("Setting FinishedLoading to true...");
			newResoniteMod.FinishedLoading = true;

			return newResoniteMod;
		}

		private static ResoniteMod InitializeAndRegisterMod_MonkeyLoaderRML(ResoniteMod originalModInstance, string newDllPath, Assembly newAssembly)
		{
			Debug("Begin InitializeAndRegisterMod_MonkeyLoaderRML.");

			var originalMod = (Mod)ResoniteModBase_Mod.GetValue(originalModInstance);

			var lastMod = originalMod.Loader.Mods.FirstOrDefault(m => m.Id == originalMod.Id);

			originalMod.Loader.ShutdownMod(lastMod);

			Type newResoniteModType = newAssembly.GetTypes().FirstOrDefault(typeof(ResoniteMod).IsAssignableFrom);
			if (newResoniteModType == null) return null;

			var newResoniteMod = (ResoniteMod)AccessTools.CreateInstance(newResoniteModType);

			var newMod = new HotReloadedMonkeyMod(newResoniteMod, originalMod.Id, originalMod.Loader);

			ResoniteModBase_Mod.SetValue(newResoniteMod, newMod);

			originalMod.Loader.AddMod(newMod);

			var assemblyLookupMap = (IDictionary)RmlMod_AssemblyLookupMap.GetValue(null);
			if (assemblyLookupMap != null)
			{
				assemblyLookupMap.Add(newAssembly, newResoniteMod);
			}

			//var localeResource = Userspace.UserspaceWorld.GetCoreLocale().Asset.Data;
			//var localeData = new LocaleData();
			//localeData.LocaleCode = localeResource.;
			//foreach (var key in newResoniteMod.GetConfiguration().ConfigurationItemDefinitions)
			//{
			//	//localeResource.LoadDataAdditively()
			//}

			return newResoniteMod;
		}

		//private static ModConfigurationDefinition BuildConfigDefinition(ResoniteMod modInstance)
		//{
		//	var newConfigDefinition = (ModConfigurationDefinition)AccessTools.Method(typeof(ResoniteMod), "BuildConfigurationDefinition")?.Invoke(modInstance, new object[] { });
		//	return newConfigDefinition;
		//}

		private static bool SetConfig(ResoniteMod modInstance, object newConfig)
		{
			if (!TrySetPropertyValue(ResoniteModBase_ModConfiguration, modInstance, newConfig)) return false;
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
				if (type.IsEnum && obj.GetType().IsEnum && Enum.GetUnderlyingType(type) == Enum.GetUnderlyingType(obj.GetType()))
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
				return false;
			}
		}

		// Updates the original mod config with the definition from new mod and copies existing values over
		// So new config keys from the new assembly will work
		// And values from the old config will still be accessible
		private static void UpdateConfigWithNewDefinition(ModConfiguration oldConfig, ModConfigurationDefinition newConfigDefinition)
		{
			Debug("Begin UpdateConfigWithNewDefinition.");

			foreach (var oldConfigKey in oldConfig.ConfigurationItemDefinitions)
			{
				// copy values from old to new
				foreach (var newConfigKey in newConfigDefinition.ConfigurationItemDefinitions)
				{
					if (newConfigKey.Name == oldConfigKey.Name && newConfigKey.ValueType().FullName == oldConfigKey.ValueType().FullName)
					{
						var oldValue = oldConfigKey.Value;
						if (oldConfigKey.HasValue)
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
								else
								{
									Error("Could not convert type.");
								}
							}
							if (newConfigKey.Validate(objectToValidate))
							{
								Debug("Value is valid.");
								newConfigKey.Value = objectToValidate;
								newConfigKey.HasValue = true;
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
			TrySetFieldValue(ModConfiguration_Definition, oldConfig, newConfigDefinition); // set readonly field with reflection
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
				Error($"Field is null for object with type name: {instance?.GetType().Name ?? "NULL"}");
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
				Error($"Property is null for object with type name: {instance?.GetType().Name ?? "NULL"}");
			}
			return false;
		}

		public static int GetReloadedCountOfModType(Type modType)
		{
			return timesHotReloaded.FirstOrDefault(kVP => kVP.Key.GetType().FullName == modType.FullName).Value;
		}

		private static string GetReloadString(ResoniteMod mod)
		{
			int count = GetReloadedCountOfModType(mod.GetType());
			return $"({count}) Reload {mod.Name ?? "NULL"} by {mod.Author ?? "NULL"}";
		}

		private static void AddReloadMenuOption(ResoniteMod mod)
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

					HotReload(mod.GetType());
				});
				Debug($"Added reload menu option: {reloadString}");
			}
		}

		public static bool RemoveMenuOption(string path, string optionName)
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

		public static void HotReload(Type unloadType)
		{
			if (!typeof(ResoniteMod).IsAssignableFrom(unloadType))
			{
				Error("Unload type is not assignable from ResoniteMod!");
				return;
			}

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

			MethodInfo reloadMethod = AccessTools.Method(unloadType, "OnHotReload");
			if (reloadMethod == null)
			{
				Error("Unload type does not have a OnHotReload method!");
				return;
			}

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

			ResoniteMod newResoniteMod = null;
			if (IsMonkeyLoaderModType(unloadType))
			{
				Msg("Initializing and registering new ResoniteMod with MonkeyLoader RML...");
				newResoniteMod = InitializeAndRegisterMod_MonkeyLoaderRML(originalModInstance, dllPath, assembly);
			}
			else
			{
				Msg("Initializing and registering new ResoniteMod with RML...");
				newResoniteMod = InitializeAndRegisterMod(originalModInstance, dllPath, assembly);
			}

			if (newResoniteMod != null)
			{
				MethodInfo method = AccessTools.Method(newResoniteMod.GetType(), "OnHotReload");
				if (method != null)
				{
					if (newResoniteMod.GetConfiguration() != null)
					{
						if (!IsMonkeyLoaderModType(unloadType))
						{
							if (originalModInstance.GetConfiguration() != null)
							{
								Msg("Updating config definition...");
								var newConfigDefinition = (ModConfigurationDefinition)ModConfiguration_Definition.GetValue(newResoniteMod.GetConfiguration());
								UpdateConfigWithNewDefinition(originalModInstance.GetConfiguration(), newConfigDefinition);
							}
							else
							{
								Warn("Original mod config is null! Replacing it with new config...");
								if (!SetConfig(originalModInstance, newResoniteMod.GetConfiguration()))
								{
									Error("Could not set config!");
									return;
								}
							}

							// Stop the new resonite mod from autosaving its config on shutdown (because its config will have outdated values since it is not being used)
							// If this fails it might not be a huge problem, but just to be safe I will stop the reload
							Debug("Setting new resonite mod config to be null...");
							if (!SetConfig(newResoniteMod, null))
							{
								Error("Could not set config to be null!");
								return;
							}
						}
					}
					else
					{
						Msg("Config is null for new mod.");
					}

					if (timesHotReloaded.ContainsKey(originalModInstance))
					{
						timesHotReloaded[originalModInstance]++;
					}
					else
					{
						Error("timesHotReloaded did not contain original mod instance!");
						return;
					}

					Msg("Adding hot reload menu option...");
					AddReloadMenuOption(newResoniteMod);

					Msg("Calling OnHotReload method...");
					if (IsMonkeyLoaderModType(unloadType))
					{
						method.Invoke(null, new object[] { newResoniteMod });
					}
					else
					{
						// Sending the original mod instance here just to remain compatible with ResoniteModSettings
						method.Invoke(null, new object[] { originalModInstance });
					}
				}
				else
				{
					Error("OnHotReload method is null in new assembly!");
				}
			}
			else
			{
				Error("New ResoniteMod instance is null!");
			}
		}
	}
}
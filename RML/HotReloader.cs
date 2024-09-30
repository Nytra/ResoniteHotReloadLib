using HarmonyLib;
using ResoniteModLoader;
using System;
using System.Reflection;

namespace ResoniteHotReloadLib
{
	public static class HotReloader
	{
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

		public static void RegisterForHotReload(ResoniteMod mod)
		{
			HotReloadCore.RegisterForHotReload(mod, () => HotReload(mod.GetType()));
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
			return HotReloadCore.GetReloadedCountOfModType(modType);
		}

		public static bool RemoveMenuOption(string path, string optionName)
		{
			return HotReloadCore.RemoveMenuOption(path, optionName);
		}

		public static void HotReload(Type unloadType)
		{
			Msg("Preparing hot reload...");
			var (assembly, originalModInstance, dllPath) = HotReloadCore.PrepareHotReload(unloadType);

			ResoniteMod newResoniteMod = null;
			Msg("Initializing and registering new ResoniteMod with RML...");
			newResoniteMod = InitializeAndRegisterMod(originalModInstance, dllPath, assembly);

			if (newResoniteMod != null)
			{
				MethodInfo method = AccessTools.Method(newResoniteMod.GetType(), "OnHotReload");
				if (method != null)
				{
					if (newResoniteMod.GetConfiguration() != null)
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
					else
					{
						Msg("Config is null for new mod.");
					}

					Msg("Finalizing hot reload...");
					HotReloadCore.FinalizeHotReload(originalModInstance, newResoniteMod, () => HotReload(newResoniteMod.GetType()));

					Msg("Calling OnHotReload method...");

					// Sending the original mod instance here just to remain compatible with ResoniteModSettings
					method.Invoke(null, new object[] { originalModInstance });
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
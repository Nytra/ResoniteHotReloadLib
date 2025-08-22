using HarmonyLib;
using MonkeyLoader.Meta;
using MonkeyLoader.NuGet;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using ResoniteModLoader;
using System;
using System.Diagnostics.CodeAnalysis;
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

		public Assembly ModAssembly;

		public HotReloadedMonkeyMod(ResoniteMod resoniteMod, string id, MonkeyLoader.MonkeyLoader loader, Assembly assembly)
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
			monkeys.Add(resoniteMod);
			ModAssembly = assembly;
		}

		public override bool OnLoadEarlyMonkeys() => true;

		public override bool OnLoadMonkeys() => true;

		public override bool TryResolveAssembly(MonkeyLoader.AssemblyName assemblyName, [NotNullWhenAttribute(true)] out Assembly assembly)
		{
			if (assemblyName.Name != ModAssembly.GetName().FullName)
			{
				assembly = null;
				return false;
			}

			Logger.Debug(() => $"Resolving assembly {assemblyName.Name} to {ModAssembly.FullName} through HotReloadedMonkeyMod");
			assembly = ModAssembly;
			return true;
		}
	}

	public static class HotReloader
	{
		static void Msg(string str) => ResoniteMod.Msg(str);
		static void Error(string str) => ResoniteMod.Error(str);
		static void Debug(string str) => ResoniteMod.Debug(str);
		static void Warn(string str) => ResoniteMod.Warn(str);

		public static void RegisterForHotReload(ResoniteMod mod)
		{
			HotReloadCore.RegisterForHotReload(mod, () => HotReload(mod.GetType()));
		}

		private static ResoniteMod InitializeAndRegisterMod(ResoniteMod originalModInstance, string newDllPath, Assembly newAssembly)
		{
			Debug("Begin InitializeAndRegisterMod.");

			var originalMod = originalModInstance.Mod;

			var lastMod = originalMod.Loader.Mods.FirstOrDefault(m => m.Id == originalMod.Id);

			originalMod.Loader.ShutdownMod(lastMod);

			Type newResoniteModType = newAssembly.GetTypes().FirstOrDefault(typeof(ResoniteMod).IsAssignableFrom);
			if (newResoniteModType == null) return null;

			var newResoniteMod = (ResoniteMod)AccessTools.CreateInstance(newResoniteModType);

			var newMod = new HotReloadedMonkeyMod(newResoniteMod, originalMod.Id, originalMod.Loader, newAssembly);

			newResoniteMod.Mod = newMod;

			originalMod.Loader.AddMod(newMod);

			RmlMod.AssemblyLookupMap.Add(newAssembly, newResoniteMod);

			//var localeResource = Userspace.UserspaceWorld.GetCoreLocale().Asset.Data;
			//var localeData = new LocaleData();
			//localeData.LocaleCode = localeResource.;
			//foreach (var key in newResoniteMod.GetConfiguration().ConfigurationItemDefinitions)
			//{
			//	//localeResource.LoadDataAdditively()
			//}

			return newResoniteMod;
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
			var (memoryStream, originalModInstance, dllPath) = HotReloadCore.PrepareHotReload(unloadType);

			var assembly = originalModInstance.Mod.Loader.AssemblyLoadStrategy.Load(memoryStream.ToArray());

			ResoniteMod newResoniteMod = null;
			Msg("Initializing and registering new ResoniteMod with MonkeyLoader RML...");
			newResoniteMod = InitializeAndRegisterMod(originalModInstance, dllPath, assembly);

			if (newResoniteMod != null)
			{
				MethodInfo method = AccessTools.Method(newResoniteMod.GetType(), "OnHotReload");
				if (method != null)
				{
					Msg("Finalizing hot reload...");
					HotReloadCore.FinalizeHotReload(originalModInstance, newResoniteMod, () => HotReload(newResoniteMod.GetType()));

					Msg("Calling OnHotReload method...");
					method.Invoke(null, new object[] { newResoniteMod });
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
using HarmonyLib;
using ResoniteModLoader;
using FrooxEngine;
using System.Collections.Generic;
using ResoniteHotReloadLib;

namespace HotReloadExampleMod
{
	public class HotReloadExampleMod : ResoniteMod
	{
		public override string Name => "HotReloadExampleMod";
		public override string Author => "Nytra";
		public override string Version => "1.0.0";
		public override string Link => "https://github.com/Nytra/ResoniteHotReloadLib";

		const string harmonyId = "owo.Nytra.HotReloadExampleMod";
		static ModConfiguration config;

		static List<string> someList = new List<string>();

		public override void OnEngineInit()
		{
			HotReloader.RegisterForHotReload(this);

			// Get the config if needed
			config = GetConfiguration();

			// Call setup method
			Setup();
		}

		// This is the method that should be used to unload your mod
		// This means removing patches, clearing memory that may be in use etc.
		static void BeforeHotReload()
		{
			// Unpatch Harmony
			Harmony harmony = new Harmony(harmonyId);
			harmony.UnpatchAll(harmonyId);

			// clear any memory that is being used
			someList.Clear();
		}

		// This is called in the newly loaded assembly
		// Load your mod here like you normally would in OnEngineInit
		static void OnHotReload(ResoniteMod modInstance)
		{
			// Get the config if needed
			config = modInstance.GetConfiguration();

			// Call setup method
			Setup();
		}

		static void Setup()
		{
			// Patch Harmony
			Harmony harmony = new Harmony(harmonyId);
			harmony.PatchAll();
		}

		[HarmonyPatch(typeof(FullBodyCalibratorDialog), "OnAttach")]
		class HotReloadExamplePatch
		{
			// Print a message when something happens (Just for example)
			public static void Postfix(FullBodyCalibratorDialog __instance)
			{
				Msg("FullBodyCalibratorDialog OnAttach");
			}
		}

		// This kind of patch is not needed anymore since v2.1.0, but you can optionally use it if you really want to
		// [HarmonyPatch(typeof(Userspace), "OnCommonUpdate")]
		// class HotReloadTriggerReloadPatch
		// {
			// public static void Postfix()
			// {
				// Reload the mod when pressing the F3 key (Just for example)
				// if (Engine.Current.InputInterface.GetKeyDown(Key.F3))
				// {
					// HotReloader.HotReload(typeof(HotReloadExampleMod));
				// }
			// }
		// }
	}
}

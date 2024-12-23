# ResoniteHotReloadLib

## Use at your own risk

Library for Resonite mod developers to hot reload their mods without needing to restart the whole game.

This is for convenience of development and WILL result in increased memory usage and possibly other issues which are unknown at this time.

It uses basically the same hot reload method as BepInEx which you can find here: https://github.com/BepInEx/BepInEx.Debug/blob/1a079418674cbbaae5d34fb2055fd77c795ee900/src/ScriptEngine/ScriptEngine.cs#L117

It does work with ResoniteModSettings however you will need to toggle the mod settings page off and on to make the keys update.

## Known Issues

Defining very small anonymous delegates inside patches can break hot-reloading, because (I assume) the JIT compiler 'optimizes' them during runtime. This prevents Harmony from being able to remove the patch.

Adding the `[Range]` attribute to a mod configuration key then reloading will result in the configuration key not becoming a slider in ResoniteModSettings. This requires a game restart to make work.

Calling `Assembly.GetExecutingAssembly().Location` will return a empty string in the reloaded mod. This is because the new assembly is loaded from a byte array instead of directly from the file.

## Pre-requisites

Install ResoniteModLoader: https://github.com/resonite-modding-group/ResoniteModLoader

Make a new folder in `rml_mods` called `HotReloadMods` then compile your mod into that folder in addition to the main `rml_mods` folder.

You can do this with a PostBuildEvent in Visual Studio.

![Screenshot 2024-01-13 193220](https://github.com/Nytra/ResoniteHotReloadLib/assets/14206961/427f9f36-2324-450e-bb6a-044ba6071ff0)

The reason for compiling into a separate folder is that you currently cannot overwrite the file in `rml_mods` while the game is running. So the file in `rml_mods` is used when the game first starts up and then after that the mod will be reloaded from the `HotReloadMods` directory.

You will need to put `ResoniteHotReloadLib.dll` and `ResoniteHotReloadLibCore.dll` in `rml_libs` so that the mods can access it.

### You will also need to implement two new methods in your mod class which will be your constructor and destructor for reloading:

`static void BeforeHotReload()`

and 

`static void OnHotReload(ResoniteMod modInstance)`

Example:

```
static void BeforeHotReload()
{
    // This runs in the current assembly (i.e. the assembly which invokes the Hot Reload)

    // This is where you unload your mod, free up memory, and remove Harmony patches etc.
}

static void OnHotReload(ResoniteMod modInstance)
{
    // This runs in the new assembly (i.e. the one which was loaded fresh for the Hot Reload)

    // Get the config
    config = modInstance.GetConfiguration();

    // Now you can setup your mod again
}
```

If these methods do not exist in your mod class then the hot reload will not work!

## Usage

Add this library as a dependency in your mod, then call `HotReloader.RegisterForHotReload(ResoniteMod modInstance)` where `modInstance` is the instance of your mod.

A good place to do this is in `OnEngineInit`.

Example:

```
using ResoniteHotReloadLib;

public override void OnEngineInit()
{
    // ...
    HotReloader.RegisterForHotReload(this);
    // ...
}
```

Then whenever you want to hot reload the mod you would equip a Dev Tool in-game and open the Create New menu and select "Hot Reload Mods" option then click the button for your mod.

![2024-03-06 02 25 52](https://github.com/Nytra/ResoniteHotReloadLib/assets/14206961/03094209-583a-45e1-9e6c-6037769a1618)

The number in brackets beside the mod name is the number of times the mod has been reloaded during the time Resonite has been running.

![2024-03-06 02 26 06](https://github.com/Nytra/ResoniteHotReloadLib/assets/14206961/a942154e-a37d-4ec9-b914-66d66900c587)

Optionally you can call `HotReloader.HotReload(Type modType)` directly, where `modType` is the Type of your mod (the Type which inherits ResoniteMod).

Example:

```
HotReloader.HotReload(typeof(YourResoniteModTypeHere));
```

Note: The HotReloader will call `BeforeHotReload` on the type that you provide here, so make sure it is the correct type!

There is some example mod code for hot reloading here: https://github.com/Nytra/ResoniteHotReloadLib/blob/main/ExampleMod/ExampleMod.cs

## Public API

These are the public API methods that can be used:

- `public static void RegisterForHotReload(ResoniteMod mod)`
    - Registers the mod for hot reload. It will add it to the internal memory of the library and create a reload button for it in the DevCreateNewForm.

- `public static int GetReloadedCountOfModType(Type modType)`
    - Will find a registered mod with the same full type name as this Type and return the number of times it has been reloaded since Resonite has been running.
 
- `public static bool RemoveMenuOption(string path, string optionName)`
    - Will find a menu option in the DevCreateNewForm matching this path and optionName and remove it.
    - For example `RemoveMenuOption("Editor", "My Custom Menu Option")` will remove a option called "My Custom Menu Option" in the "Editor" category. This can be useful in your `BeforeHotReload` destructor for removing any additional menu options that you may have added manually.
 
- `public static void HotReload(Type unloadType)`
    - Will trigger a hot reload for the mod which matches the full type name of the unloadType. The ResoniteMod type needs to be assignable from the unloadType. The original mod must have already been registered with the library and it needs to have the `OnHotReload` and `BeforeHotReload` constructor and destructor methods.

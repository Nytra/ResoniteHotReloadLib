# ResoniteHotReloadLib

## Use at your own risk

Library for Resonite mod developers to hot reload their mods.

This is for convenience of development and WILL result in increased memory usage and possibly other issues which are unknown at this time.

It uses basically the same hot reload method as BepInEx which you can find here: https://github.com/BepInEx/BepInEx.Debug/blob/1a079418674cbbaae5d34fb2055fd77c795ee900/src/ScriptEngine/ScriptEngine.cs#L117

## Known Issues

Currently changing config key definitions in your code and then hot reloading doesn't work. This is because the reloader doesn't create a new config from the new assembly. I will try to find a way to fix this.

## Pre-requisites

Make a new folder in `rml_mods` called `HotReloadMods` then compile your mod into that folder in addition to the main `rml_mods` folder.

You can do this with a PostBuildEvent in Visual Studio.

![Screenshot 2024-01-13 193220](https://github.com/Nytra/ResoniteHotReloadLib/assets/14206961/427f9f36-2324-450e-bb6a-044ba6071ff0)

You will need to put `ResoniteHotReloadLib.dll` in `rml_mods` and `HotReloadMods` folders so the mods in there can access it.

### You will also need to implement two new methods in your mod class:

`static void BeforeHotReload()`

and 

`static void OnHotReload(ResoniteMod modInstance)`

Example:

```
static void BeforeHotReload()
{
    // This is where you unload your mod, free up memory, and remove Harmony patches etc.
}

static void OnHotReload(ResoniteMod modInstance)
{
    // Get the config
    config = modInstance.GetConfiguration();
    // Now you can setup your mod again
}
```

If these methods do not exist in your mod class then the hot reload will not work!

## Usage

Add this library as a dependency in your mod, then call `HotReloader.RegisterForHotReload(ResoniteMod modInstance)` where `modInstance` is the instance of your mod class.

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

Then whenever you want to hot reload the mod you would call `HotReloader.HotReload(Type modType)` where `modType` is the type of your mod class which inherits `ResoniteMod`.

Example:

```
HotReloader.HotReload(typeof(YourResoniteModTypeHere));
```

Note: The HotReloader will call `BeforeHotReload` on the type that you provide here, so make sure it is the correct type!

There is some example mod code for hot reloading here: https://github.com/Nytra/ResoniteHotReloadLib/blob/main/ExampleMod/ExampleMod.cs

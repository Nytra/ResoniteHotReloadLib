# ResoniteHotReloadLib

Library for Resonite mod developers to hot reload their mods

## Usage

Make a new folder in `rml_mods` called `HotReloadMods` then compile your mod into that folder in addition to the main `rml_mods` folder.

Add this library as a dependency in your mod, then call `HotReloader.RegisterForHotReload(ResoniteMod modInstance)` where `modInstance` is the instance of your mod class.

A good place to do this is in `OnEngineInit`.

Example:

```
using ResoniteHotReloadLib;

public override void OnEngineInit()
{
    ...
    HotReloader.RegisterForHotReload(this);
    ...
}
```

Then whenever you want to hot reload the mod you would call `HotReloader.HotReload(Type modType)` where `modType` is the type of your mod class which inherits `ResoniteMod`.

Example:

```
HotReloader.HotReload(typeof(YourResoniteModTypeHere));
```

## You also need to implement two new methods in your mod class:

`static void BeforeHotReload()`

and 

`static void OnHotReload(ResoniteMod modInstance)`

Example:

```
static void BeforeHotReload()
{
    // This is where you unload your mod and remove Harmony patches etc.
}

static void OnHotReload(ResoniteMod modInstance)
{
    ...
    // Get the config
    config = modInstance.GetConfiguration();
    // After this you can setup your mod again...
    ...
}
```

If these methods do not exist in your mod class then the hot reload will not work.

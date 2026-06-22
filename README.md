# Ostranauts Workshop BepInEx Bridge

This is a small BepInEx plugin that syncs BepInEx payloads from subscribed Ostranauts Workshop items into the real game install.

Workshop item layout:

```text
<workshop item>/
  mod_info.json
  data/
    .keep
  BepInEx/
    plugins/
      SomeWorkshopPlugin.dll
    patchers/
      SomeOptionalPatcher.dll
    config/
      SomeWorkshopPlugin.cfg
```

User install:

```text
Ostranauts/
  BepInEx/
    plugins/
      OstranautsWorkshopBepInExBridge.dll
    patcher/
      WorkshopBepInExBridgePreloader.dll
```

On game startup, the bridge reads Ostranauts' `loading_order.json`, syncs only enabled Workshop paths listed in `aLoadOrder`, then copies:

- `BepInEx/plugins/*` to `Ostranauts/BepInEx/plugins/Workshop/<workshop id>/`
- `BepInEx/patchers/*` to `Ostranauts/BepInEx/patchers/Workshop/<workshop id>/`
- `BepInEx/config/*` to `Ostranauts/BepInEx/config/`

Example supported loading order:

```json
[
  {
    "strName": "Mod Loading Order",
    "aLoadOrder": [
      "core",
      "Mod1",
      "Mod2",
      "E:\\Steam\\steamapps\\workshop\\content\\1022980\\3737566289",
      "E:\\Steam\\steamapps\\workshop\\content\\1022980\\3738765255"
    ]
  }
]
```

The bridge ignores local/non-Workshop entries. This keeps ordinary local Ostranauts mods under the game's native mod loader instead of treating them as BepInEx Workshop payloads.

The bridge tracks files it owns in:

```text
Ostranauts/BepInEx/config/OstranautsWorkshopBepInExBridge.manifest.tsv
```

Newly copied plugins and patchers generally require restarting Ostranauts, because BepInEx scans plugins before this bridge plugin runs.

## Self-update

The bridge ships as its own Workshop item. When Steam downloads a newer version, the bridge cannot overwrite its own loaded DLLs in place, so on the next start the preloader uses the `.old` rename trick: it renames each installed bridge DLL (`OstranautsWorkshopBepInExBridge.Preloader.dll` in `patchers/`, `OstranautsWorkshopBepInExBridge.dll` in `plugins/`) to `*.old`, copies the newer copy from the bridge's Workshop folder into place, then relaunches Ostranauts via Steam and exits. The leftover `*.old` files are deleted on the following start.

"Newer" is decided by file size + last-write time. Disable with `Sync.SelfUpdate=false` if you do not want the bridge to auto-restart the game.

## Safety Defaults

The bridge does not overwrite unmanaged existing files by default. If a destination file already exists and was not created by the bridge, it logs a warning and skips it.

If a Workshop entry is removed from `loading_order.json`, or the listed Workshop folder no longer contains the same `BepInEx` payload, files previously copied by the bridge are removed on the next game start. Modified copied files are kept.

Generated config:

```text
Ostranauts/BepInEx/config/com.ostranauts.workshop.bepinexbridge.cfg
```

Useful settings:

- `Paths.WorkshopRootOverride`: manually point at `steamapps/workshop/content/1022980`.
- `Paths.LoadingOrderPathOverride`: manually point at `loading_order.json`.
- `Sync.FallbackToWorkshopFolderScan`: set `true` to sync all Workshop folders if `loading_order.json` cannot be found. This is off by default so stale unsubscribed folders are not copied.
- `Sync.CopyConfigToRoot`: set `false` to copy configs under `BepInEx/config/Workshop/<workshop id>/` instead of the config root.
- `Sync.SelfUpdate`: set `false` to stop the bridge from replacing its own DLLs and auto-restarting Ostranauts when a newer version is downloaded.
- `Safety.OverwriteUnmanagedFiles`: set `true` only if you want Workshop payloads to replace existing non-bridge-managed files.

## Build

From this folder:

```powershell
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Ostranauts"
```

If BepInEx lives somewhere else, pass:

```powershell
dotnet build -c Release -p:GameDir="C:\...\Ostranauts" -p:BepInExDir="C:\...\Ostranauts\BepInEx"
```

Output:

```text
WorkshopBepInExBridge/bin/Release/OstranautsWorkshopBepInExBridge.dll
```

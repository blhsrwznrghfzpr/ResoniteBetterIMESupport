# ResoniteBetterIMESupport

[![Thunderstore Badge](https://modding.resonite.net/assets/available-on-thunderstore.svg)](https://thunderstore.io/c/resonite/)

A BepInEx plugin for Resonite that improves IME composition handling for input methods such as Japanese.

This mod keeps IME composition text synchronized while editing UIX text, including:

- displaying unconfirmed text while typing with an IME
- suppressing Resonite's F8 VR/Screen mode toggle while an IME composition is unconfirmed

Related issue: [Yellow-Dog-Man/Resonite-Issues#745](https://github.com/Yellow-Dog-Man/Resonite-Issues/issues/745), [Yellow-Dog-Man/Resonite-Issues#2825](https://github.com/Yellow-Dog-Man/Resonite-Issues/issues/2825)

## Project Layout

Resonite runs the Unity renderer and the main engine in separate processes, so this mod is split into two plugins:

- `ResoniteBetterIMESupport.Renderer/ResoniteBetterIMESupport.Renderer.csproj`
  - Renderer-side plugin
  - Targets `net472`
  - Hooks Unity InputSystem IME composition events
  - Sends every `OnIMECompositionChange` composition string to the engine plugin
- `ResoniteBetterIMESupport.Engine/ResoniteBetterIMESupport.Engine.csproj`
  - Engine-side plugin
  - Targets `net10.0`
  - Patches FrooxEngine text editing and text rendering
  - Replaces the active `IText` composition range with each renderer composition update
  - Displays composition through the active TextEditor selection/caret
- `ResoniteBetterIMESupport.Shared/ImeInterprocessMessage.cs`
  - Shared named-pipe IPC layer used by both plugins
- `Directory.Build.props`
  - Shared build metadata and default Resonite/BepisLoader paths

Both plugin sides are required. Installing only one side will not provide working IME composition support.

## Installation

Install [BepisLoader](https://github.com/ResoniteModding/BepisLoader) for Resonite.

Download the latest release ZIP from the [Releases](https://github.com/blhsrwznrghfzpr/ResoniteBetterIMESupport/releases) page and extract it into your BepInEx/BepisLoader profile.

For the Gale default profile, the files should be placed like this:

- Engine plugin:
  - `%APPDATA%\com.kesomannen.gale\resonite\profiles\Default\BepInEx\plugins\blhsrwznrghfzpr-ResoniteBetterIMESupport\blhsrwznrghfzpr-ResoniteBetterIMESupport\ResoniteBetterIMESupport.Engine.dll`
- Renderer plugin:
  - `%APPDATA%\com.kesomannen.gale\resonite\profiles\Default\Renderer\BepInEx\plugins\blhsrwznrghfzpr-ResoniteBetterIMESupport\blhsrwznrghfzpr-ResoniteBetterIMESupport\ResoniteBetterIMESupport.Renderer.dll`

After installation, restart Resonite.

To confirm both sides loaded, check the logs:

- Engine log:
  - `%APPDATA%\com.kesomannen.gale\resonite\profiles\Default\BepInEx\LogOutput.log`
  - Look for `ResoniteBetterIMESupport.Engine loaded.`
- Renderer log:
  - `%APPDATA%\com.kesomannen.gale\resonite\profiles\Default\Renderer\BepInEx\LogOutput.log`
  - Look for `ResoniteBetterIMESupport.Renderer loaded.`

## Development

The project expects the Resonite game files and BepisLoader profile at these default paths:

- `GamePath`: `%LOCALAPPDATA%\RESO Launcher\profiles\01bepis\Game`
- `BepisLoaderProfilePath`: `%APPDATA%\com.kesomannen.gale\resonite\profiles\develop`

`GamePath` is also auto-detected from common Steam install paths. You can set `ResonitePath` as a shorthand for `GamePath`.

Build both plugins:

```powershell
dotnet build ResoniteBetterIMESupport.sln
```

Build and copy both plugins into the Gale development profile:

```powershell
dotnet build ResoniteBetterIMESupport.sln -p:CopyToPlugins=true
```

This copies:

- `ResoniteBetterIMESupport.Renderer.dll` to `$(BepisLoaderProfilePath)\Renderer\BepInEx\plugins\blhsrwznrghfzpr-ResoniteBetterIMESupport\blhsrwznrghfzpr-ResoniteBetterIMESupport`
- `ResoniteBetterIMESupport.Engine.dll` to `$(BepisLoaderProfilePath)\BepInEx\plugins\blhsrwznrghfzpr-ResoniteBetterIMESupport\blhsrwznrghfzpr-ResoniteBetterIMESupport`

You can override paths when building:

```powershell
dotnet build ResoniteBetterIMESupport.sln -p:GamePath="C:\Path\To\Game" -p:BepisLoaderProfilePath="C:\Path\To\Profile"
```

or:

```powershell
dotnet build ResoniteBetterIMESupport.sln -p:ResonitePath="C:\Path\To\Game"
```

If Resonite is running, the engine-side DLL may be locked. Close Resonite and run the copy build again.

## Packaging

Thunderstore packaging is configured by `thunderstore.toml`.

The package contains both plugin sides:

- `Renderer/BepInEx/plugins/blhsrwznrghfzpr-ResoniteBetterIMESupport/ResoniteBetterIMESupport.Renderer.dll`
- `plugins/blhsrwznrghfzpr-ResoniteBetterIMESupport/ResoniteBetterIMESupport.Engine.dll`

Build release binaries before packaging:

```powershell
dotnet build ResoniteBetterIMESupport.sln -c Release
```

Then build the Thunderstore package with the configured tooling.

## Fork Notice

This is a Resonite-focused fork maintained by blhsrwznrghfzpr.

Original project: [hantabaru1014/NeosBetterIMESupport](https://github.com/hantabaru1014/NeosBetterIMESupport)

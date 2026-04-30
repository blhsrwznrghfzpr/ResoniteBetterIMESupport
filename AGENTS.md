# Repository Guidelines

## Project Shape

ResoniteBetterIMESupport is a two-process Resonite/BepInEx mod for IME composition support. Keep the split explicit:

- `ResoniteBetterIMESupport.Renderer` targets `net472` and runs inside the Unity renderer process. It hooks Unity InputSystem/Renderite keyboard behavior and sends every Unity IME composition string to the engine side.
- `ResoniteBetterIMESupport.Engine` targets `net10.0` and runs inside the Resonite engine process. It patches FrooxEngine text editing/rendering, applies composition ranges to the active `IText`, suppresses conflicting editor keys while composition is unsettled, and draws the composition caret.
- `ResoniteBetterIMESupport.Shared` is linked into both plugins and owns shared protocol details such as named-pipe messages and IME editing key sets.

Both plugin sides are required for the feature to work. Avoid changes that make either side appear optional unless the install/package layout is intentionally redesigned.

## Maintaining This File

When work reveals reusable project knowledge, pitfalls, validation steps, local paths, or coding conventions that would help future Codex sessions, update this `AGENTS.md` proactively as part of the same change. Keep additions concise and specific to this repository.

## Build And Package Commands

Use PowerShell on Windows. The normal validation command is:

```powershell
dotnet build ResoniteBetterIMESupport.sln
```

To copy a local build into the Gale development BepisLoader profile:

```powershell
dotnet build ResoniteBetterIMESupport.sln -p:CopyToPlugins=true
```

Useful path overrides:

```powershell
dotnet build ResoniteBetterIMESupport.sln -p:GamePath="C:\Path\To\Game" -p:BepisLoaderProfilePath="C:\Path\To\Profile"
dotnet build ResoniteBetterIMESupport.sln -p:ResonitePath="C:\Path\To\Game"
```

Release/package metadata is duplicated in `Directory.Build.props`, `ResoniteBetterIMESupport.Engine/EnginePlugin.cs`, `ResoniteBetterIMESupport.Renderer/RendererPlugin.cs`, and `thunderstore.toml`; keep versions synchronized when changing releases. Thunderstore packaging expects Release binaries and uses `build.cake`/`tcli` with `thunderstore.toml`. The package output directory is `./build`.

## Runtime Paths

Default development assumptions live in `Directory.Build.props`:

- `GamePath`: `%LOCALAPPDATA%\RESO Launcher\profiles\01bepis\Game`, with common Steam paths as fallback.
- `BepisLoaderProfilePath`: `%APPDATA%\com.kesomannen.gale\resonite\profiles\develop`.

Keep development copy layout and Thunderstore package layout distinct:

- `CopyToPlugins=true` targets Gale's installed package payload folder under the development profile, with a double package-id directory:
  - Engine: `%APPDATA%\com.kesomannen.gale\resonite\profiles\develop\BepInEx\plugins\blhsrwznrghfzpr-ResoniteBetterIMESupport\blhsrwznrghfzpr-ResoniteBetterIMESupport\ResoniteBetterIMESupport.Engine.dll`
  - Renderer: `%APPDATA%\com.kesomannen.gale\resonite\profiles\develop\Renderer\BepInEx\plugins\blhsrwznrghfzpr-ResoniteBetterIMESupport\blhsrwznrghfzpr-ResoniteBetterIMESupport\ResoniteBetterIMESupport.Renderer.dll`
- Thunderstore package-internal layout is configured in `thunderstore.toml` and must remain one package-id directory:
  - Engine: `plugins/blhsrwznrghfzpr-ResoniteBetterIMESupport/ResoniteBetterIMESupport.Engine.dll`
  - Renderer: `Renderer/BepInEx/plugins/blhsrwznrghfzpr-ResoniteBetterIMESupport/ResoniteBetterIMESupport.Renderer.dll`

If copy-to-profile fails, check whether Resonite is still running and locking the engine DLL.

## Implementation Notes

- When changing Harmony patches or behavior that depends on Resonite/FrooxEngine internals, inspect `../reso-decompile/sources` as needed. Use it to confirm target method names, state-machine shapes, private fields/properties, and text-editing behavior before changing transpilers or reflection code.
- Renderer-side IME dispatch is intentionally simple: send every `OnIMECompositionChange` string immediately as a single IPC message containing only `Composition`. Do not infer commits from `typeDelta`, pending frames, or committed-text deltas.
- Engine-side composition application mirrors `NeosBetterIMESupport.InsertComposition`: delete the current editor selection, clear composition state when the incoming composition is empty, otherwise set `SelectionStart` to the caret and insert the incoming composition text.
- When keyboard input transitions from active to inactive while a composition is unsettled, send an empty composition. Do not do this on every inactive `HandleOutputState` frame, because initial inactive frames can interfere with entering edit mode.
- Treat focus changes as IME session boundaries on the renderer side: when keyboard input becomes inactive, discard current composition bookkeeping; when it becomes active again, reinitialize renderer IME state before processing the next composition update.
- Engine-side focus loss should also terminate the current IME session: if an unconfirmed composition range still exists when editing focus is cleared, clear IME bookkeeping while retaining the visible text.
- Preserve the IPC protocol in `Shared/ImeInterprocessMessage.cs`: it currently carries only the composition string. If the message format changes, update both sides together.
- IME IPC should use the fixed `ImeInterprocessChannel.QueueName` and pass it to InterprocessLib's custom `Messenger(ownerId, isAuthority, queueName)` constructor. Keep the InterprocessLib owner id and queue name as separate concepts.
- Keep config definitions shared in `Shared/ImePluginConfig.cs` and synchronize common BepInEx config entries through InterprocessLib `SyncConfigEntry`; the engine side publishes its initial value after the IME bridge starts.
- Keep IME debug logging behind the `Debug/EnableDebugLogging` config entry on both sides. Verbose logs should go through `LogDebugIme`.
- Harmony patches depend on private Resonite/FrooxEngine/Renderite implementation details. Prefer small, targeted patches with clear failure messages when reflected members are missing.
- Be careful with composition deletion/caret behavior for Japanese IME: Backspace/Delete/Home/End/Left/Right during unconfirmed composition should not let Resonite delete the whole visual composition range.
- Only suppress TextEditor editing keys while an unconfirmed composition range actually exists in the edited text. After Enter commits and no composition range remains, Backspace/Delete must pass through normally even if transient IME bookkeeping flags are still settling.
- Treat surrogate pairs carefully when moving caret positions; avoid splitting surrogate code units.
- Resonite's vanilla F8 VR-Screen toggle lives in `FrooxEngine.ScreenModeController.OnCommonUpdate`, which directly checks `InputInterface.GetKeyDown(Key.F8)` and flips `InputInterface.VR_Active`. Suppress it only while an active IME composition range exists so normal F8 behavior returns after commit/cancel.

## Testing Focus

After behavioral changes, test at least these flows in Resonite when possible:

- start composition, update candidates, and commit text normally
- confirm Left/Right/Home/End do not produce renderer-side caret IPC while composition is active
- edit unconfirmed composition with Backspace/Delete without deleting the whole composition range
- commit after editing composition text
- confirm both logs contain the load messages for Engine and Renderer plugins

If only build validation was possible, say so explicitly in the final response.

## Completion Build

At the end of every implementation task, run the replacement build before reporting completion. First enable debug logging in both installed BepInEx config files:

- `%APPDATA%\com.kesomannen.gale\resonite\profiles\develop\BepInEx\config\dev.blhsrwznrghfzpr.ResoniteBetterIMESupport.Engine.cfg`
- `%APPDATA%\com.kesomannen.gale\resonite\profiles\develop\Renderer\BepInEx\config\dev.blhsrwznrghfzpr.ResoniteBetterIMESupport.Renderer.cfg`

Set `EnableDebugLogging = true` in both files, then run this Release copy build:

```powershell
dotnet build ResoniteBetterIMESupport.sln -c Release -p:CopyToPlugins=true
```

Only change the installed local config files for this step. Do not commit changes that alter the plugin's default debug config value or any tracked config defaults.

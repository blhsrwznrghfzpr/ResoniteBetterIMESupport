# Repository Guidelines

## Project Shape

ResoniteBetterIMESupport is a two-process Resonite/BepInEx mod for IME composition support. Keep the split explicit:

- `ResoniteBetterIMESupport.Renderer` targets `net472` and runs inside the Unity renderer process. It hooks Unity InputSystem/Renderite keyboard behavior, tracks IME composition state, and sends composition text, committed text, and caret offsets to the engine side.
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

To copy a local build into the default Gale/BepisLoader profile:

```powershell
dotnet build ResoniteBetterIMESupport.sln -p:CopyToPlugins=true
```

Useful path overrides:

```powershell
dotnet build ResoniteBetterIMESupport.sln -p:GamePath="C:\Path\To\Game" -p:BepisLoaderProfilePath="C:\Path\To\Profile"
dotnet build ResoniteBetterIMESupport.sln -p:ResonitePath="C:\Path\To\Game"
```

Release/package metadata is duplicated in `Directory.Build.props`, plugin constants, and `thunderstore.toml`; keep versions synchronized when changing releases. Thunderstore packaging expects Release binaries and uses `build.cake`/`tcli` with `thunderstore.toml`.

## Runtime Paths

Default development assumptions live in `Directory.Build.props`:

- `GamePath`: `%LOCALAPPDATA%\RESO Launcher\profiles\01bepis\Game`, with common Steam paths as fallback.
- `BepisLoaderProfilePath`: `%APPDATA%\com.kesomannen.gale\resonite\profiles\Default`.

Installed DLL layout must remain:

- Engine: `BepInEx/plugins/blhsrwznrghfzpr-ResoniteBetterIMESupport/ResoniteBetterIMESupport.Engine.dll`
- Renderer: `Renderer/BepInEx/plugins/blhsrwznrghfzpr-ResoniteBetterIMESupport/ResoniteBetterIMESupport.Renderer.dll`

If copy-to-profile fails, check whether Resonite is still running and locking the engine DLL.

## Implementation Notes

- When changing Harmony patches or behavior that depends on Resonite/FrooxEngine internals, inspect `../reso-decompile/sources` as needed. Use it to confirm target method names, state-machine shapes, private fields/properties, and text-editing behavior before changing transpilers or reflection code.
- Japanese IMEs commonly commit the selected clause implicitly when the user starts typing the next word. When an IME pipe message contains committed text and a non-empty next composition, commit the text into the old composition range first, clear `InputInterface.TypeDelta`, then apply the new preedit range.
- The named-pipe path must handle implicit clause commits. If an existing composition is still active and a short starter preedit arrives with no committed text, commit the old composition range before inserting the new preedit; avoid treating CJK candidate updates as starters.
- When the named-pipe fallback handles IME composition or committed text, suppress the corresponding `InputInterface.TypeDelta` until a non-empty update is seen. For implicit commits, also remember the committed composition text and suppress an exact matching later `TypeDelta`; Japanese IMEs can deliver that duplicate commit after several empty keyboard updates.
- When a named-pipe IME message contains both committed text and a new non-empty composition, commit the text first, start the new composition second, and suppress only the exact committed text from later `TypeDelta` updates. Avoid broad TypeDelta suppression in this path because it can consume the first character of the next word.
- Engine-side named-pipe commits can still race with `InputInterface.TypeDelta` in the same editor frame. After applying committed IME text, strip only an exact committed-text prefix from `InputInterface.TypeDelta` so duplicate commit text is removed but trailing newline/submission control characters are preserved.
- Keep renderer-side IME dispatch frame-based: combine the latest composition snapshot and printable `TypeDelta` commit text into one IPC message per update when composition changes, then suppress only the exact committed-text prefix from local `typeDelta`.
- When keyboard input transitions from active to inactive while a composition is unsettled, cancel the visual composition by sending an empty composition with no committed text, then ignore the next empty-composition commit from Unity. Do not do this on every inactive `HandleOutputState` frame, because initial inactive frames can interfere with entering edit mode.
- Treat focus changes as IME session boundaries on the renderer side: when keyboard input becomes inactive, discard any pending composition bookkeeping; when it becomes active again, reinitialize renderer IME state before processing the next composition update.
- Engine-side focus loss should also terminate the current IME session: if an unconfirmed composition range still exists when editing focus is cleared, clear IME bookkeeping while retaining the visible text, then ignore one exact matching stale implicit-commit payload if Unity sends it after refocus.
- Engine-side focus-loss cancel messages use `composition=""`, `committed=""`, and `caretOffset=-1`. They should clear IME bookkeeping but keep the visible composition text as editor text; deleting the composition range makes the first focus-loss look like the typed text was cleared.
- Unity can report a large accumulated `typeDelta` made from repeated preedit snapshots when focus leaves an editor without explicit IME commit. Treat an empty composition update whose committed text clearly repeats the previous composition as a focus-loss cancel, not a real commit.
- Never suppress a line-break `TypeDelta` (`\n` or `\r`) in the engine fallback. Resonite's `TextEditor` relies on Shift being held while that delta is processed to distinguish Shift+Enter newline from Enter submit. If duplicate committed IME text and a line break arrive in the same `TypeDelta`, filter only the duplicate text and leave the line break.
- Preserve the named-pipe protocol in `Shared/ImePipe.cs`: base64 fields separated by tabs, carrying composition, committed text, and caret offset. If the message format changes, update both sides together and consider a pipe-name version bump.
- The named-pipe protocol may include an optional fourth tab-separated `ImeEditAction` field. Renderer sends `Backspace`/`Delete` when Unity reports those keys during an IME composition change; Engine uses it to avoid mistaking composition deletion for an implicit commit before a new composition.
- Renderer-side pipe identity intentionally derives from the parent process when running in a renderer process so the engine and renderer share a session-specific pipe. Be cautious changing process-name or parent-process logic.
- Keep IME debug logging behind the `Debug/EnableDebugLogging` config entry on both sides. Verbose logs should go through `LogDebugIme`.
- Harmony patches depend on private Resonite/FrooxEngine/Renderite implementation details. Prefer small, targeted patches with clear failure messages when reflected members are missing.
- Be careful with composition deletion/caret behavior for Japanese IME: Backspace/Delete/Home/End/Left/Right during unconfirmed composition should not let Resonite delete the whole visual composition range.
- Only suppress TextEditor editing keys while an unconfirmed composition range actually exists in the edited text. After Enter commits and no composition range remains, Backspace/Delete must pass through normally even if transient IME bookkeeping flags are still settling.
- Do not suppress TextEditor control `TypeDelta` values such as `\b`, `\n`, or `\r` in the generic IME duplicate filter. Backspace/Delete protection belongs to active composition range checks, while Shift+Enter/newline handling must remain available as normal editor input.
- When a `TypeDelta` exactly matches the current visual composition, treat it as a real commit signal rather than generic duplicate text. Some IMEs/Unity frames can still report the same composition text immediately after Enter, so ignore stale composition updates after consuming the commit.
- Treat surrogate pairs carefully when moving caret positions; avoid splitting surrogate code units.
- Resonite's vanilla F8 VR-Screen toggle lives in `FrooxEngine.ScreenModeController.OnCommonUpdate`, which directly checks `InputInterface.GetKeyDown(Key.F8)` and flips `InputInterface.VR_Active`. Suppress it only while an active IME composition range exists so normal F8 behavior returns after commit/cancel.

## Testing Focus

After behavioral changes, test at least these flows in Resonite when possible:

- start composition, update candidates, and commit text normally
- move the caret inside unconfirmed composition with Left/Right/Home/End
- edit unconfirmed composition with Backspace/Delete without deleting the whole composition range
- commit after editing composition text
- confirm both logs contain the load messages for Engine and Renderer plugins

If only build validation was possible, say so explicitly in the final response.

## Completion Build

At the end of every implementation task, run the replacement build before reporting completion. First enable debug logging in both installed BepInEx config files:

- `%APPDATA%\com.kesomannen.gale\resonite\profiles\Default\BepInEx\config\dev.blhsrwznrghfzpr.ResoniteBetterIMESupport.Engine.cfg`
- `%APPDATA%\com.kesomannen.gale\resonite\profiles\Default\Renderer\BepInEx\config\dev.blhsrwznrghfzpr.ResoniteBetterIMESupport.Renderer.cfg`

Set `EnableDebugLogging = true` in both files, then run this Release copy build:

```powershell
dotnet build ResoniteBetterIMESupport.sln -c Release -p:CopyToPlugins=true
```

Only change the installed local config files for this step. Do not commit changes that alter the plugin's default debug config value or any tracked config defaults.

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
- For IME compatibility, keep preedit/composition state separate from committed text. Prefer the Renderite `KeyboardState` composition payload when available and keep the named-pipe path as a fallback for older runtimes; do not infer IME state only from physical keys or `typeDelta`.
- Japanese IMEs commonly commit the selected clause implicitly when the user starts typing the next word. When `KeyboardState` contains both committed `typeDelta` and a non-empty next composition in the same update, commit the text into the old composition range first, clear `InputInterface.TypeDelta`, then apply the new preedit range.
- Older Renderite builds may lack `KeyboardState` composition fields, so the named-pipe fallback must also handle implicit clause commits. If an existing composition is still active and a short starter preedit arrives with no committed text, commit the old composition range before inserting the new preedit; avoid treating CJK candidate updates as starters.
- When the named-pipe fallback handles IME composition or committed text, suppress the corresponding `InputInterface.TypeDelta` until a non-empty update is seen. For implicit commits, also remember the committed composition text and suppress an exact matching later `TypeDelta`; Japanese IMEs can deliver that duplicate commit after several empty keyboard updates.
- When a named-pipe IME message contains both committed text and a new non-empty composition, commit the text first, start the new composition second, and suppress only the exact committed text from later `TypeDelta` updates. Avoid broad TypeDelta suppression in this path because it can consume the first character of the next word.
- When keyboard input transitions from active to inactive while a composition is unsettled, cancel the visual composition by sending an empty composition with no committed text, then ignore the next empty-composition commit from Unity. Do not do this on every inactive `HandleOutputState` frame, because initial inactive frames can interfere with entering edit mode.
- Engine-side focus-loss cancel messages use `composition=""`, `committed=""`, and `caretOffset=-1`. They should clear IME bookkeeping but keep the visible composition text as editor text; deleting the composition range makes the first focus-loss look like the typed text was cleared.
- Unity can report a large accumulated `typeDelta` made from repeated preedit snapshots when focus leaves an editor without explicit IME commit. Treat an empty composition update whose committed text clearly repeats the previous composition as a focus-loss cancel, not a real commit.
- Never suppress a line-break `TypeDelta` (`\n` or `\r`) in the engine fallback. Resonite's `TextEditor` relies on Shift being held while that delta is processed to distinguish Shift+Enter newline from Enter submit. If duplicate committed IME text and a line break arrive in the same `TypeDelta`, filter only the duplicate text and leave the line break.
- The unreleased Renderite/Renderite.Unity.Renderer concept implementation already writes `Input.compositionString` into `KeyboardState` during `KeyboardDriver.UpdateState`. When that contract exists, merge the original `KeyboardState` composition in the renderer postfix instead of blindly overwriting it with MOD-local event state.
- Preserve the named-pipe protocol in `Shared/ImePipe.cs`: base64 fields separated by tabs, carrying composition, committed text, and caret offset. If the message format changes, update both sides together and consider a pipe-name version bump.
- Renderer-side pipe identity intentionally derives from the parent process when running in a renderer process so the engine and renderer share a session-specific pipe. Be cautious changing process-name or parent-process logic.
- Keep IME debug logging behind the `Debug/EnableDebugLogging` config entry on both sides. Verbose logs should go through `LogDebugIme`.
- Harmony patches depend on private Resonite/FrooxEngine/Renderite implementation details. Prefer small, targeted patches with clear failure messages when reflected members are missing.
- Be careful with composition deletion/caret behavior for Japanese IME: Backspace/Delete/Home/End/Left/Right during unconfirmed composition should not let Resonite delete the whole visual composition range.
- Only suppress TextEditor editing keys while an unconfirmed composition range actually exists in the edited text. After Enter commits and no composition range remains, Backspace/Delete must pass through normally even if transient IME bookkeeping flags are still settling.
- Do not suppress TextEditor control `TypeDelta` values such as `\b`, `\n`, or `\r` in the generic IME duplicate filter. Backspace/Delete protection belongs to active composition range checks, while Shift+Enter/newline handling must remain available as normal editor input.
- When a `TypeDelta` exactly matches the current visual composition, treat it as a real commit signal rather than generic duplicate text. Some IMEs/Unity frames can still report `compositionActive=true` for the same text immediately after Enter, so ignore that stale active composition after consuming the commit.
- Treat surrogate pairs carefully when moving caret positions; avoid splitting surrogate code units.

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

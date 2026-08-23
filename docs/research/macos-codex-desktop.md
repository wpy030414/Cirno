# OpenAI Codex Desktop on macOS — Research Findings

> Research date: 2026-08-23. Scope: install form, app.asar, processes, CDP/remote debugging, `config.toml` appearance keys, community theming issues, macOS quirks, and Codex Desktop vs Codex IDE clarification.
>
> **Terminology note:** "Codex Desktop" is **not** a separate product. The official "Codex desktop app" is the **ChatGPT desktop app** (same binary, bundle id `com.openai.codex`), launched via `codex app` or the Codex App page. See [Clarification](#clarification-codex-desktop-vs-codex-ide) below.

---

## 1. Install form on macOS

- **Yes, a .app bundle inside a DMG.** Official download URLs (from official app docs page and the `codex` CLI source):
  - Apple Silicon (arm64): `https://persistent.oaistatic.com/codex-app-prod/Codex.dmg`
  - Intel (x86_64): `https://persistent.oaistatic.com/codex-app-prod/Codex-latest-x64.dmg`
  - Sources: [Codex App docs](https://learn.chatgpt.com/codex/app) ("Download for macOS (Apple Silicon)"), and `codex-rs/cli/src/desktop_app/mac.rs` constants `CODEX_DMG_URL_ARM64` / `CODEX_DMG_URL_X64` in [openai/codex](https://github.com/openai/codex/blob/main/codex-rs/cli/src/desktop_app/mac.rs).
- **App bundle name:** the DMG contains `Codex.app`; it is installed to `/Applications/Codex.app` (or `~/Applications/Codex.app`). The same app may exist as **`/Applications/ChatGPT.app`** — both names are searched by the CLI and accepted when the bundle identifier is `com.openai.codex`. Verified locally: `/Applications/ChatGPT.app`, `CFBundleIdentifier = com.openai.codex`, `CFBundleName = "ChatGPT"`, version `26.818.41705`.
  - Sources: `find_existing_codex_app_path` / `install_codex_app_bundle` in [mac.rs](https://github.com/openai/codex/blob/main/codex-rs/cli/src/desktop_app/mac.rs); local `/usr/bin/plutil` inspection.
- **Official install paths:**
  1. **In-app download:** download the DMG from the official page and drag `Codex.app` to Applications (standard macOS DMG flow) — [Codex App docs](https://learn.chatgpt.com/codex/app).
  2. **Via the Codex CLI:** run `codex app [PATH]` — the CLI downloads the DMG, mounts it with `hdiutil attach`, **verifies the code signature**, copies the bundle with `ditto`, then launches. — [mac.rs](https://github.com/openai/codex/blob/main/codex-rs/cli/src/desktop_app/mac.rs) + [app_cmd.rs](https://github.com/openai/codex/blob/main/codex-rs/cli/src/app_cmd.rs).
- **brew / npm:** these install the **CLI only**, not the desktop app. Official: `npm install -g @openai/codex` and `brew install --cask codex` install the Codex CLI; there is **no official brew/npm package for the desktop app**. — [openai/codex README](https://github.com/openai/codex#quickstart).

## 2. app.asar location; Electron/Chromium basis

- **Yes, standard Electron layout.** Verified locally:
  - `/Applications/ChatGPT.app/Contents/Resources/app.asar` — 284 MB on this machine; header is the classic Electron asar pickle (`{"files":{".vite"...}}`).
  - `/Applications/ChatGPT.app/Contents/Resources/app.asar.unpacked`, `default_app`, `electron.icns` also present.
- **Electron/Chromium evidence (direct, on-disk):**
  - `Contents/Frameworks/Codex Framework.framework/Versions/151.0.7922.170/` — Chromium-version-style framework; contains Electron-style helper apps `Helpers/Codex (Renderer).app` and `Helpers/Codex (Service).app` (executables named `Codex`), plus `browser_crashpad_handler` (Chromium crashpad).
  - `Contents/Resources/owl-electron-app.json`: `{"packagedFrom": "/Users/runner/work/openai/openai/codex/codex-apps/electron/out/ChatGPT-darwin-arm64/ChatGPT.app", "runtimeName": "owl"}` — OpenAI's own packaging metadata, build path `codex-apps/electron` confirms Electron tooling.
  - `Contents/Frameworks/Sparkle.framework` — Sparkle auto-updater (macOS).
  - Community statement: "Because Codex is built on Electron…" — [OpenCLI Codex adapter docs](https://github.com/jackwener/opencli/blob/main/docs/adapters/desktop/codex.md).

## 3. Process names, launch with extra args, quit

- **Main process:** `/Applications/ChatGPT.app/Contents/MacOS/ChatGPT` → process name **`ChatGPT`** (bundle executable `CFBundleExecutable = ChatGPT`). Renderer/service helpers are named **`Codex`** (e.g. `.../Helpers/Codex (Renderer).app/Contents/MacOS/Codex`). Verified via `ps aux` on a running install (also `cua_node/bin/node_repl`, `browser_crashpad_handler`, `codex -c features.code_mode_host=true`).
- **pkill:** `pkill -f ChatGPT.app` (main) or `pkill ChatGPT`; `pkill -f "Codex (Renderer)"` for renderers. Quit gracefully: `osascript -e 'tell application "Codex" to quit'` (community pattern from [acodex](https://github.com/maksimzayats/acodex/blob/main/src/acodex/cli/codex.py)).
- **Launch from CLI with extra args:** Chromium switches are passed via macOS `open`:
  - `open -a /Applications/ChatGPT.app --args --remote-debugging-port=9335` — [acodex `CodexSystemOps.launch_app`](https://github.com/maksimzayats/acodex/blob/main/src/acodex/cli/codex.py)
  - `open -na /Applications/ChatGPT.app --args --remote-debugging-address=127.0.0.1 --remote-debugging-port=<port>` (`-n` forces a new instance) — [Codex Dream Skin Workflow Engine common.sh](https://github.com/jiarong0423/codex-dream-skin-workflow-engine/blob/main/macos/scripts/common.sh)
  - Or run the bundle executable directly: `/Applications/Codex.app/Contents/MacOS/ChatGPT --remote-debugging-port=9238 --remote-allow-origins=*` — [OpenCLI codex adapter](https://github.com/jackwener/opencli/blob/main/docs/adapters/desktop/codex.md)
  - Official CLI launch: `codex app [PATH]` executes `open -a <app_path> "codex://threads/new?path=<workspace>"` (custom `codex://` URL scheme) — [mac.rs](https://github.com/openai/codex/blob/main/codex-rs/cli/src/desktop_app/mac.rs).

## 4. `--remote-debugging-port` (CDP) support

- **Not documented by OpenAI** for the app itself (no occurrence in `openai/codex` source or the official docs), but **it works**, because the app is Electron/Chromium and honors standard Chromium command-line switches.
- Community evidence (multiple independent tools):
  - [acodex](https://github.com/maksimzayats/acodex) relaunches Codex with `--remote-debugging-port=<port>` (default `127.0.0.1:45217`); error message: "Start Codex with `--remote-debugging-port` or set `ACODEX_CODEX_APP_CDP_PORT`". Probes `http://127.0.0.1:<port>/json/list`.
  - [OpenCLI codex adapter](https://github.com/jackwener/opencli/blob/main/docs/adapters/desktop/codex.md): `/Applications/Codex.app/Contents/MacOS/ChatGPT --remote-debugging-port=9238 --remote-allow-origins=*`, endpoint `http://127.0.0.1:9238`.
  - [Codex Dream Skin Workflow Engine](https://github.com/jiarong0423/codex-dream-skin-workflow-engine) (macOS, OpenAI Build Week 2026): injects CSS/DOM over the local Chromium DevTools Protocol using `--remote-debugging-address=127.0.0.1 --remote-debugging-port=<port>`; deliberately does **not** modify `/Applications/ChatGPT.app` or `app.asar`.
- **Debugger URL:** `http://127.0.0.1:<port>/` with the standard CDP endpoints (`/json/list`, `/json/version`); the port is user-chosen (community defaults: 45217 acodex, 9238 opencli; this workspace uses 9335). Localhost-only is recommended.
- Official docs mention CDP only for the **in-app browser** feature ("Developer mode → Enable full CDP access" so ChatGPT can use the Chrome DevTools Protocol for browser debugging) — [Developer settings docs](https://learn.chatgpt.com/codex/developer-settings). That is the browser tool, not the app's own window.

## 5. `config.toml`: location, "chromeTheme", appearance keys

- **Location confirmed:** user-level `~/.codex/config.toml`; project overrides in `.codex/config.toml` (loaded only for trusted projects). — [Configuration Reference](https://learn.chatgpt.com/codex/config-file/config-reference), [Config basics](https://learn.chatgpt.com/codex/config-file/config-basic).
- **There is no flat `chromeTheme` key.** The desktop app's UI-chrome colors are configured with two TOML tables (community-documented; **not** listed in the official config reference):
  - `[desktop.appearanceLightChromeTheme]` and `[desktop.appearanceDarkChromeTheme]`
  - Known keys: `surface`, `ink` (text), `accent`, `contrast`, `opaqueWindows`, `fonts` (`ui`/`code`), `semanticColors` (`diffAdded`, `diffRemoved`, `skill`).
  - Code-editor theme: `appearanceLightCodeThemeId` / `appearanceDarkCodeThemeId` (must match a built-in id: `tokyo-night`, `one-dark`, `nord`, `dracula`, `catppuccin`, `gruvbox`, `solarized`, `github-light`, `matrix`, `one`).
  - Sources: [YuChenSSR/claude-warm-codex-app-theme](https://github.com/YuChenSSR/claude-warm-codex-app-theme) (TOML presets), [awesome-codex-themes](https://github.com/mcpso/awesome-codex-themes), [openai/codex#14766](https://github.com/openai/codex/issues/14766).
- **The official config reference documents NO appearance/theme keys for the desktop app.** The `[desktop]` table in the CLI is an **opaque pass-through**: `codex-rs/config/src/config_toml.rs` declares `pub desktop: Option<HashMap<String, JsonValue>>` — "Opaque desktop settings stored alongside the rest of config.toml" — and tests show `appearanceTheme = "dark"`, `selected-avatar-id`, `recentViews`, `desktop.workspace.collapsed` round-tripping. — [config_toml.rs](https://github.com/openai/codex/blob/main/codex-rs/config/src/config_toml.rs), [config_tests.rs](https://github.com/openai/codex/blob/main/codex-rs/core/src/config/config_tests.rs) (`desktop_toml_round_trips_opaque_nested_values`).
- **Official appearance customization** is done in-app: **Settings → Appearance** — base theme (Light/Dark/System), accent/background/foreground colors, UI and code fonts, font sizes, plus **Import**/**Copy theme** for portable `codex-theme-v1:` strings. — [Codex App Settings docs](https://learn.chatgpt.com/codex/app/settings).
- **Theme export format:** `codex-theme-v1:{"codeThemeId":"...","theme":{...},"variant":"light"|"dark"}` (JSON payload; fields `accent`, `ink`, `surface`, `contrast`, `opaqueWindows`, `fonts`, `semanticColors`). — [awesome-codex-themes](https://github.com/mcpso/awesome-codex-themes), [openai/codex#14766](https://github.com/openai/codex/issues/14766).
- **CLI TUI theming (separate):** `[tui]` keys `theme` (kebab-case syntax theme name), `status_line`, `keymap`, `title`; custom TextMate `.tmTheme` files in `~/.codex/themes/`. — [CLI customization docs](https://learn.chatgpt.com/codex/cli-customization), config reference.

## 6. Community / GitHub issues about theming

- [openai/codex#14766](https://github.com/openai/codex/issues/14766) — "Theme importer rejects custom `codeThemeId` values unless they match dropdown IDs" (macOS theme importer bug; Import button disabled for custom ids; workaround: reuse a built-in id).
- [openai/codex#11073](https://github.com/openai/codex/issues/11073) — "Feature request: Custom colorscheme / Codex app" (labeled `app`; predates the appearance feature; asks for presets + config-file colors + follow-system accent).
- [openai/codex#11023](https://github.com/openai/codex/issues/11023) — "Codex desktop app for Linux" (desktop app distribution/process discussion across platforms).
- [openai/codex#1618](https://github.com/openai/codex/issues/1618) and [#2020](https://github.com/openai/codex/issues/2020) — TUI color/theme requests (referenced inside #11073).
- [Fei-Away/Codex-Dream-Skin#309](https://github.com/Fei-Away/Codex-Dream-Skin/issues/309) — macOS CDP-injection skin bug ("Light full task mode inherits white Markdown text").
- Ecosystem / tooling (not issues but primary community references):
  - [mcpso/awesome-codex-themes](https://github.com/mcpso/awesome-codex-themes) — curated list; documents `codex-theme-v1` format, CDP-injection "skins", and the `codeThemeId` gotcha.
  - [YuChenSSR/claude-warm-codex-app-theme](https://github.com/YuChenSSR/claude-warm-codex-app-theme) — `[desktop.appearanceLight/DarkChromeTheme]` TOML presets + installer (writes `appearanceLight/DarkCodeThemeId`).
  - [jiarong0423/codex-dream-skin-workflow-engine](https://github.com/jiarong0423/codex-dream-skin-workflow-engine) — macOS CDP runtime skin engine (zero app modification).
  - [Fei-Away/Codex-Dream-Skin](https://github.com/Fei-Away/Codex-Dream-Skin) — CDP skin project.
  - [maksimzayats/acodex](https://github.com/maksimzayats/acodex) — CDP automation/MCP bridge (default CDP port 45217).
  - [Wangnov/codex-app-mirror](https://github.com/Wangnov/codex-app-mirror) — mirrors official DMGs to GitHub Releases, provides Sparkle update feed.

## 7. macOS-specific quirks

- **Signing / notarization:** the app is signed by OpenAI (Apple Developer team `2DC432GLL2`, bundle `com.openai.codex`) with **hardened runtime** (`codesign` flags `0x10000(runtime)`) and an embedded provisioning profile; it is **NOT App-Sandboxed** (`com.apple.security.app-sandbox = false`). Gatekeeper assessment passes (`spctl --assess`). Verified locally via `codesign -dv` / `codesign -d --entitlements`.
- **`codex app` refuses modified builds:** the CLI verifies the bundle with `codesign --verify --deep --strict -R='identifier "com.openai.codex" and anchor apple generic and certificate leaf[subject.OU] = "2DC432GLL2"'` and refuses to install/launch unverified apps ("refusing to install an unverified Desktop app"). **Ad-hoc re-signing or editing `app.asar` will break `codex app`.** Runtime CDP injection (which leaves the bundle untouched) is the compatible path. — [mac.rs](https://github.com/openai/codex/blob/main/codex-rs/cli/src/desktop_app/mac.rs); Gatekeeper/XProtect diagnostics in [doctor/desktop/macos_security.rs](https://github.com/openai/codex/blob/main/codex-rs/cli/src/doctor/desktop/macos_security.rs).
- **`open --args` only applies on launch:** if the app is already running, `open` typically just activates the existing instance and the new args are ignored → quit first (acodex relaunch quits and waits for exit before relaunching; Dream Skin uses `open -na` to force a new instance). — [acodex codex.py](https://github.com/maksimzayats/acodex/blob/main/src/acodex/cli/codex.py), [common.sh](https://github.com/jiarong0423/codex-dream-skin-workflow-engine/blob/main/macos/scripts/common.sh).
- **Does it read `config.toml` live?** Not officially documented. Community theme installers state you must **restart the app** after editing `~/.codex/config.toml` manually (claude-warm installer README: "Restart Codex after installation so the desktop app reloads `~/.codex/config.toml`"). Changes made through the in-app Settings UI are applied by the app itself and persisted into the `[desktop]` table of `config.toml` (implied by the opaque-table round-trip tests and the config written by theme installers). The app-server also serves config to clients (RPC), but the UI-chrome keys are app-side.
- App updates are handled by **Sparkle** on macOS (`Sparkle.framework` present); official docs: [Manage app updates](https://learn.chatgpt.com/codex/enterprise/manage-app-updates).

## Clarification: Codex Desktop vs Codex IDE

- **Codex Desktop** = the ChatGPT desktop app (Electron, `com.openai.codex`, `/Applications/ChatGPT.app` or `Codex.app`). **This is the only Codex surface with `app.asar` and with `desktop.*` config.toml chrome-theme support.** It is a closed-source binary; the `openai/codex` repo only contains the `codex app` launcher + the app-server protocol it uses.
- **Codex IDE** = the VS Code extension **`openai.chatgpt`** on the VS Code Marketplace (used in VS Code, Cursor, Windsurf; Xcode and JetBrains have their own integrations). It has **no app.asar and no chromeTheme**; it consumes `~/.codex/config.toml` for agent settings but not for UI chrome. — [Codex IDE docs](https://learn.chatgpt.com/codex/ide).
- There is **no `openai/codex-ide` or `openai/codex-desktop` GitHub repo** (both return 404). The desktop app's issues live in `openai/codex` under the `app` label (see #11073/#11023/#14766).
- **Codex CLI** (Rust TUI, `openai/codex`) has its own theming (`[tui]` keys, `/theme`, `.tmTheme` files) — unrelated to the desktop chrome theme.

## Sources

Official (OpenAI):
- https://developers.openai.com/codex/app (Codex App landing → ChatGPT desktop app docs) / https://learn.chatgpt.com/codex/app
- https://learn.chatgpt.com/codex/app/settings (official Appearance / theme import settings)
- https://learn.chatgpt.com/codex/config-file/config-reference (official config.toml reference — no chrome/theme keys)
- https://learn.chatgpt.com/codex/config-file/config-basic (config locations)
- https://learn.chatgpt.com/codex/cli-customization ([tui] theming)
- https://learn.chatgpt.com/codex/developer-settings ("Enable full CDP access" — in-app browser CDP)
- https://learn.chatgpt.com/codex/app-server (app-server RPC / WebSocket transport)
- https://learn.chatgpt.com/codex/ide (Codex IDE = VS Code extension openai.chatgpt)
- https://learn.chatgpt.com/codex/linux/linux-app , https://learn.chatgpt.com/codex/windows/windows-app (platform desktop app docs)
- https://github.com/openai/codex#quickstart (CLI install: npm @openai/codex, brew --cask codex; "run codex app" for the desktop app)
- https://github.com/openai/codex/blob/main/codex-rs/cli/src/desktop_app/mac.rs (DMG URLs, bundle names, codesign verification, open -a + codex:// URL)
- https://github.com/openai/codex/blob/main/codex-rs/cli/src/app_cmd.rs (codex app subcommand)
- https://github.com/openai/codex/blob/main/codex-rs/config/src/config_toml.rs (opaque [desktop] table)
- https://github.com/openai/codex/blob/main/codex-rs/core/src/config/config_tests.rs (desktop TOML round-trip test)
- https://github.com/openai/codex/blob/main/codex-rs/cli/src/doctor/desktop/macos_security.rs (Gatekeeper/XProtect diagnostics)

GitHub issues:
- https://github.com/openai/codex/issues/14766 (theme importer codeThemeId validation)
- https://github.com/openai/codex/issues/11073 (custom colorscheme feature request, label "app")
- https://github.com/openai/codex/issues/11023 (desktop app for Linux)
- https://github.com/openai/codex/issues/1618 , https://github.com/openai/codex/issues/2020 (TUI theme requests)
- https://github.com/Fei-Away/Codex-Dream-Skin/issues/309 (macOS CDP skin bug)

Community / ecosystem:
- https://github.com/mcpso/awesome-codex-themes (theme format + CDP skins)
- https://github.com/YuChenSSR/claude-warm-codex-app-theme (config.toml chrome-theme TOML keys + restart requirement)
- https://github.com/maksimzayats/acodex (CDP relaunch via open --args; quit via osascript; default port 45217)
- https://github.com/jackwener/opencli/blob/main/docs/adapters/desktop/codex.md (direct binary launch with --remote-debugging-port; "built on Electron")
- https://github.com/jiarong0423/codex-dream-skin-workflow-engine (macOS CDP injection engine; open -na --args launch)
- https://github.com/Fei-Away/Codex-Dream-Skin (CDP skin project)
- https://github.com/Wangnov/codex-app-mirror (DMG mirror + Sparkle feed)

Local verification (this machine, 2026-08-23):
- `/Applications/ChatGPT.app` (bundle id com.openai.codex, version 26.818.41705, executable ChatGPT)
- `Contents/Resources/app.asar` (284 MB, Electron asar header), `Contents/Resources/owl-electron-app.json` (runtimeName "owl", packagedFrom codex-apps/electron)
- `Contents/Frameworks/Codex Framework.framework/Versions/151.0.7922.170/Helpers/Codex (Renderer|Service).app`
- `codesign -dv`: hardened runtime; `codesign -d --entitlements`: app-sandbox = false; team 2DC432GLL2
- `ps aux`: main process ChatGPT; helper processes named Codex

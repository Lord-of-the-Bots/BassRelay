# Bass Relay for SimHub — 0.3.0

Movies and music through your cockpit's bass shakers, with controls inside SimHub.

This is a native .NET Framework plugin. Its audio engine runs inside SimHub; there is no background Bass Relay EXE and no need to enable SimHub's web server.

## Install

Requires the Windows .NET Framework version of SimHub. The plugin uses the SimHub 9.12.8 SDK and targets its 32-bit .NET Framework 4.8 host.

1. Exit SimHub. If you are switching permanently to the plugin, you can turn off **Start with Windows** in the standalone Bass Relay app.
2. Extract the ZIP. Copy **BassRelay.SimHub.dll** and **BassRelay.Audio.dll** into your SimHub installation folder, beside **SimHubWPF.exe**. If Windows shows an **Unblock** option in the ZIP's Properties, unblock it before extracting.
3. Start SimHub, enable **Bass Relay** in **Additional plugins**, and open its page.
4. Select the sound card connected to your shaker amplifier. Start with a low strength and play a movie or some music through your usual Windows output.

The plugin uses a separate SimHub settings profile. Your standalone settings are kept intact; select your cards again on first use. Settings are saved through SimHub in `PluginsData/Common/BassRelayPlugin.GeneralSettings.json`.

## Controls

- One row per output sound card, with independent strength and frequency range. Defaults: **40–90 Hz**. Allowed limits: **0–200 Hz**, with the minimum strictly below the maximum.
- Vibration strength depends on the audio level captured from the main Windows output. Adjust the volume in your player or game first, then set the shaker strength. The main Windows device volume slider may not change the level captured by Bass Relay.
- Choose **Not selected** to remove a row; the final empty row stays available.
- **Pause Bass Relay / Resume Bass Relay** stops and resumes Bass Relay on every configured shaker. Normal Windows audio and SimHub's own game effects continue. A manual pause stays active until you resume it.
- **Pause Bass Relay while a game is running in SimHub** is directly on the main page. With it enabled, resuming manually still waits for a running game to stop.
- Sound cards update automatically when connected, disconnected or renamed. The current Windows audio source is shown on the page and follows Windows output changes automatically.
- The page uses SimHub's own controls and theme. Playback status distinguishes manual pause, automatic game pause and unavailable outputs.
- The interface follows SimHub's language automatically, including language changes while the plugin is open. All seven languages supplied with SimHub 9.12.8 are included: English, German, French, Italian, Russian, Korean and Simplified Chinese. There is no separate language selector.
- SimHub actions **BassRelayPlugin.Pause**, **BassRelayPlugin.Resume** and **BassRelayPlugin.TogglePause** can be assigned to buttons in SimHub.

Auto-pause is enabled by default. When SimHub reports **GameRunning**, Bass Relay stops its own output to every shaker, including quiet gaps between game effects. It resumes when that status ends, unless manually paused. Some games report running only while on track. The plugin reads that state directly from SimHub; it does not monitor sound peaks or individual applications.

SimHub must stay running for movies and music. The selected sound card must not be the default Windows output. The plugin follows changes to that default output automatically. It filters the Windows audio mix; it does not decode a separate movie LFE channel or add game telemetry effects to ShakeIt.

## Standalone version and updates

**The enabled plugin takes priority over the standalone application.** It asks a running standalone Bass Relay to close through its normal exit command, then keeps it from starting again for the whole time the plugin is enabled. This includes manual pause, automatic game pause and having no shaker selected. The standalone 1.4.1 app already supports this, so it does not need an update.

Disable the plugin or close SimHub before using the standalone app again. The plugin does not change the standalone app's settings or Windows startup preference, and does not restart it when SimHub closes. The two editions keep separate settings.

If the standalone app cannot close normally, the plugin waits without starting audio and shows a message. It does not forcibly terminate the process. Close the standalone app from its tray menu to allow the plugin to continue.

To update, close SimHub and replace only the two Bass Relay DLLs. Do not replace SimHub's NAudio or other dependencies. To remove, disable Bass Relay, close SimHub and delete those two DLLs. Saved settings can be kept for later.

Feedback about device compatibility and game transitions is welcome in [GitHub issues](https://github.com/Lord-of-the-Bots/BassRelay/issues).

## Build

From the repository root, with a .NET 10 SDK and SimHub 9.12.8 available:

```powershell
.\BassRelay.SimHub\build-plugin.ps1 -SimHubDirectory 'E:\SimHub'
```

The build produces a ZIP containing only the two Bass Relay assemblies and this guide. SimHub's own DLLs are referenced for compilation and are not redistributed. The common audio project builds for both .NET Framework 4.8 and .NET 10; the existing desktop application uses the same audio implementation.

For more information about our projects, visit our website: [https://nswtl.info](https://nswtl.info).

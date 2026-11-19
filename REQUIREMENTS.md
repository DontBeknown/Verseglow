# MusicLyrics v1 requirements

## Goal and scope

MusicLyrics is a lightweight Windows 11 x64 tray app that displays synchronized, animated lyrics above other apps. Users can start a song in Spotify or YouTube, switch to another app or game, and keep reading without interrupting their work or play.

Release targets are Spotify desktop, YouTube music videos in Chrome and Edge, and windowed and borderless games. YouTube requires a usable Windows playback timeline and lyrics for the specific recording or edit. Exclusive fullscreen and per-game layouts are outside v1.

## Playback and lyrics

- Use Windows Global System Media Transport Controls to read track metadata, playback state, and position. Follow the current supported session automatically. Allow the user to select another detected session for the current app run and switch back to Automatic. [Windows media session API](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- Fetch line-timed lyrics using title and artist, plus album and duration when available. Evaluate LRCLIB for coverage and confirm permitted display and caching before shipping an online integration. Skip automatic matches when the metadata is ambiguous or the known durations differ by more than two seconds. Let the user search by title and artist, select a result, or attach a local `.lrc` file. A manual choice overrides automatic matching for the current media item. Local imports work offline. [LRCLIB API documentation](https://lrclib.net/docs)
- Show the current line at its timestamp. Recalculate on seek or resume, keep the current line static on pause, and clear stale lyrics immediately on track change or stop. Ignore lookup results for tracks that have since changed. Provide a per-track **Earlier / Later** adjustment of ±10 seconds in 100 ms steps.
- Hide the overlay when lyrics or a usable playback timeline are unavailable. Show the reason in tray settings and provide the applicable action, such as retrying, matching manually, importing a file, or selecting another session.

## Overlay and placement

- Use a transparent topmost Windows window. In normal use, the overlay passes mouse clicks through to the window underneath and never takes keyboard focus. Provide a configurable global show/hide shortcut and a tray action to edit placement. Use standard Windows windowing APIs. [Topmost windows](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos) · [Click-through layered windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)
- Default to bottom center on the primary monitor. In placement mode, let users select a monitor and move or resize a rectangle with sample lyrics. Save one layout across restarts. Keep the rectangle within the selected monitor, support mixed DPI, and move it to the primary monitor if the selected display disconnects. Wrap long lines inside the rectangle and clip any remaining overflow.

## Animation presets and editor

FireStund’s *Show the Lyrics* mod documents Fade, Pop, and a Wave that introduces letters in sequence. MusicLyrics adapts that variety to a fixed, user-sized screen area with a word-by-word entrance and short transitions that leave time to read. [Mod overview](https://www.curseforge.com/minecraft/mc-mods/show-the-lyrics) · [Animation changelog](https://www.curseforge.com/minecraft/mc-mods/show-the-lyrics/files/9386481)

| Preset | Default behavior |
| --- | --- |
| **Soft Fade** · default | Fade the new line in over 160 ms. Keep the text in place. |
| **Gentle Pop** | Fade in and scale from 96% to 100% over 180 ms with an ease-out. |
| **Word Ripple** | Fade in words in reading order. Reserve the full layout from the first frame so the line stays in place. Use a total transition of 220 ms. Word order is a visual effect driven by the line timestamp. |
| **Instant** | Show the full line immediately. Use this behavior whenever Windows reduced motion is enabled. |

- Start from a built-in preset and edit it with a live preview that loops sample lines independently of playback. Offer controls for font size, text color, background opacity, and transition duration from 80 to 250 ms where applicable. Gentle Pop also exposes scale intensity. Save, rename, and delete custom presets. Reset edits to the selected built-in preset's defaults.
- Start each entrance at the line timestamp. Render the full line immediately after a seek or resume. If the next line arrives during an animation, switch directly to the new line and drop the previous animation. Use Soft Fade for Word Ripple when the renderer cannot preserve correct word order or text shaping. Stop animation rendering while hidden, paused, or between line changes.

## Release acceptance

| Area | Pass criteria |
| --- | --- |
| Playback | Test Spotify desktop and YouTube in Chrome and Edge. Record app and Windows versions and verify start, pause, resume, forward and backward seek, track changes, and competing sessions. Verify automatic matching on an identifiable music video and manual matching on an ambiguous upload. List only tested configurations with reliable timelines. |
| Sync and errors | With known-good LRC timestamps, begin line changes within 250 ms during steady playback and show the correct full line within one second after seeking or resuming. Test an offline LRC import, missing lyrics, a duration mismatch, absent timeline data, and a result that arrives after a track change. |
| Game overlay | Verify one windowed and one borderless game. Confirm click-through, retained game focus, and global show/hide. Restart to check saved placement, then change DPI and disconnect the selected display to check recovery. Publish the tested configurations. |
| Customization | Preview each preset with no music playing. Save and reload a custom preset, rename and delete it, and reset edits. Check reduced motion, rapid consecutive lines, and a long wrapped line. Include mixed-direction text in the Word Ripple check. |
| Performance | On a Windows 11 x64 PC with at least four physical cores and 8 GB RAM, measure release builds with settings closed. Over five minutes per state, require under 0.5% average total CPU while idle, under 2% with Soft Fade and Word Ripple measured in separate active runs, and under 150 MB peak private working set. Run the same repeatable game benchmark three times with the app closed and three times with Word Ripple active. Require a drop of at most 3% in median average FPS. |

Start with a compatibility check for Windows playback timing and click-through over both game modes. The preset timings and resource budgets are proposed v1 targets and require on-device verification.

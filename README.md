# MusicLyrics

MusicLyrics is a Windows 11 x64 tray app for reading synchronized lyrics over another app or game. It follows Windows media sessions and supports offline `.lrc` import. The current build is for internal evaluation.

## Build and run

Run the build and test commands from the project directory with the .NET 10 SDK installed.

```powershell
dotnet test MusicLyrics.Tests -c Release
dotnet publish MusicLyrics -c Release -r win-x64 --self-contained true -o artifacts/win-x64
.\artifacts\win-x64\MusicLyrics.exe
```

Start playback and open MusicLyrics from the Windows notification area. Select **Import LRC** to attach timed lyrics to the current track. Use **Earlier** or **Later** to adjust alignment. The default show/hide shortcut is `Ctrl+Alt+L`. Open **Move or resize overlay** to choose a display, move the overlay, and resize it. The **Presets** tab applies and saves your changes as you make them. Use **Reset to default** to restore the selected preset or **Duplicate as custom** to keep a named copy.

Choose **Paper Pop** for scattered, outlined lettering or **Frosted Cinema** for text that rises into focus. Select **Word by word** under **Entrance** to reveal each word in order across the line. Choose a ready-made color palette or edit its swatches with the color picker. Accent colors are assigned to a few random words in each line. The remaining words use the main color. In **Presets**, select **Preset default (all languages)** and choose an installed font, then select individual languages to override it. **Inherit font** clears that override. For languages that share a writing system, add a language tag such as `[la:fr]` to the LRC file. Untagged text uses the preset font when multiple overrides disagree for the same script.
 
Settings are stored in `%LOCALAPPDATA%\MusicLyrics\settings.json`. The active preset, shortcut, and layout persist. Session selection resets on exit. Attached lyrics and timing adjustment reset when the media item changes or playback stops.

## Diagnostic checks

```powershell
pwsh -NoProfile -File MusicLyrics.Tests/OverlaySmoke.ps1
.\artifacts\win-x64\MusicLyrics.exe --probe
```

Close the regular app before starting the probe so its hotkey is available. The smoke script checks window styles, foreground focus, and the hotkey message handler on a Windows desktop. The probe displays a sample overlay and records media events and positions in `%LOCALAPPDATA%\MusicLyrics\playback-probe.jsonl`. Pass `--probe-log <file>` to choose another path. The log contains the titles and artists observed during the run.

For automatic lookup in this private build, go to the **Settings** tab, enter a contact email or HTTPS project URL, and select **Apply online lookup settings**. Spotify desktop and YouTube in Zen, Chrome, and Edge can trigger a lookup when Windows provides a usable timeline. LRCLIB receives the contact and track metadata. Clear the field and apply to disable lookup. Use **Search timed lyrics** or **Import LRC** when the app cannot identify a unique recording.

The command-line option `--evaluate-online <project-https-url-or-contact-email>` overrides the saved contact for that run. The integration retains the current item's lyrics in memory. Keep public v1 on hold until display and retention are cleared and coverage meets the release criteria. After clearance, publish with `-p:OnlineLyricsCleared=true -p:OnlineLyricsContact=<contact>` to supply a default. [LRCLIB API documentation](https://lrclib.net/docs)

## Release acceptance

Record the machine, Windows build, player and browser versions, and test media. Use the criteria in [REQUIREMENTS.md](REQUIREMENTS.md) and record the results before publishing a supported-configuration list.

| Area | On-device check |
| --- | --- |
| Media sessions | In Spotify desktop and YouTube in Chrome and Edge, test start, pause, resume, both seek directions, track changes, and competing sessions. Compare probe timestamps with the player's visible position. |
| Sync and matching | Use known LRC timestamps and a recording of the player and overlay together to measure steady playback and seek/resume latency. Exercise offline import, an identifiable music video, an ambiguous upload, wrong duration, an unavailable timeline, and a lookup completing after a track change. |
| Overlay | In one windowed and one borderless game, click on the rendered text, verify game input and focus, and use the global shortcut. Restart with a saved secondary-monitor layout, change DPI, then disconnect that monitor. |
| Presets | Preview all nine presets, edit colors and entrance settings, and confirm edits persist across restart. Duplicate, rename, delete, and reset custom presets. Exercise Windows reduced motion, consecutive lines, a long wrapped line, and mixed right-to-left and left-to-right text. |
| Performance | Use a Release build and close Settings. Record five minutes each for idle, Soft Fade, and Word Ripple. Divide elapsed process CPU time by wall-clock time and logical processor count. Sample peak **Working Set - Private**. Run the same game benchmark three times with the app closed and three times with Word Ripple active. Compare the medians against the requirements. |

To capture the five-minute process measurements, run `pwsh -File MusicLyrics.Tests/MeasureResources.ps1 -ProcessId <id>` once per playback state.

### Checks run on November 19, 2026

- Windows 11 build 26200 and Edge 157.0.4341.60 reported a playing YouTube timeline, pause and resume, and seeks to 55 and 8 seconds in the playback probe. A separate local audio fixture also exercised the Windows timeline.
- The Windows overlay smoke test confirmed layered, click-through and no-activation window styles, retained foreground focus, and verified hide and restore through the hotkey handler.
- A five-minute idle run on an 8-core Ryzen 7 6800H with 16 GB RAM measured 0.0000% average total CPU at the script's reporting precision and 50.4 MB peak sampled private working set. This run preceded the final placement editor and browser matching updates.
- Public release still requires full Spotify and Chrome coverage, the remaining YouTube and synchronization cases, live preset and multi-display checks, both game modes, the active performance runs and FPS benchmark, and clearance for LRCLIB display and retention. [LRCLIB API documentation](https://lrclib.net/docs)

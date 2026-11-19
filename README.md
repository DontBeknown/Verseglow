# Verseglow

Follow along with your music while you play or work. Verseglow displays timed lyrics in a click-through overlay on Windows 11, so you can read the current line and keep using the app underneath.

| Paper Pop | Frosted Cinema |
| --- | --- |
| ![Paper Pop preview with bold, outlined lettering](images/Paper-Pop.png) | ![Frosted Cinema preview with cream serif lettering](images/Frosted-Cinema.png) |

## Follow your first song

1. Launch **Verseglow.exe** and play a song in a player that shares playback with Windows, such as Spotify desktop.
2. Double-click the Verseglow icon in the Windows notification area and open **Now playing**.
3. Choose **Import LRC** and pick a timed lyrics file for that recording. If the words appear a little late or early, click **Earlier 100 ms** or **Later 100 ms** until they line up.
4. Press **Ctrl+Alt+L** to hide or show the lyrics. To move them to another corner or display, right-click the tray icon and choose **Edit placement**.

## Make it yours

Open **Presets** and try **Paper Pop** for bold lettering or **Frosted Cinema** for lines that rise into focus. Watch the live preview as you adjust the entrance, colors, and fonts. Changes save automatically. To keep two looks, use **Manage preset** to duplicate one before editing.

## Find lyrics online

To enable automatic lookup, open **Settings**, enter a contact email or HTTPS project URL, and select **Apply online lookup settings**. Verseglow sends that contact and the track details to LRCLIB. If it cannot pick a recording, open **Now playing** and choose **Search timed lyrics**. Importing an LRC file works offline.

<details>
<summary>Running from source</summary>

On Windows 11 x64 with the .NET 10 SDK installed, run this from the project folder.

```powershell
dotnet run --project MusicLyrics -c Release
```

</details>

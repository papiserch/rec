# CS-90 Capture Recorder

A digital cassette recorder for producers: capture whatever Windows is playing, select the part you want, and drag it straight into FL Studio or Ableton Live.

**REC → STOP → SELECT → DRAG TO DAW**

- C# / .NET 8 / WPF, NAudio (WASAPI loopback of the default playback device)
- Recording format: WAV, 32-bit IEEE float, stereo, device sample rate, streamed to disk
- Drag-to-DAW uses native OLE file drag-and-drop (`DataFormats.FileDrop`) with a real WAV file

## Status

Source and CI configuration are written but **have not been compiled or run yet** (they were authored in a Linux sandbox without the .NET SDK or Windows). First step on your side: push, run the **Build CS-90** workflow, and fix any compile errors it reports. Audio capture, playback, waveform editing and drag-and-drop still need to be tested on a real Windows machine with the steps below.

## Build

Requires the .NET 8 SDK (no Visual Studio needed).

```powershell
dotnet restore CS90.sln
dotnet build CS90.sln -c Release
dotnet run --project src/CS90/CS90.csproj
```

Portable single-file build:

```powershell
dotnet publish src/CS90/CS90.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## GitHub Actions

- `.github/workflows/build.yml` — on push / PR / manual run: restore, build, publish win-x64, zip, upload artifact `CS90-portable-win-x64`.
- `.github/workflows/release.yml` — push a tag such as `v1.0.0` to attach the zip to a GitHub Release.

Run it: **Actions → Build CS-90 → Run workflow**, then download the artifact from the finished run.

## Install

Unzip the portable package anywhere and run `CS90.exe`. No installer, no prerequisites (self-contained).

## Using it

1. Press **REC**, play any sound (YouTube, Spotify, a game…).
2. Press **STOP**.
3. Drag across the waveform to select a region (drag the red edges to adjust). Click to move the playhead.
4. **PLAY** previews the selection (or everything if nothing is selected).
5. Drag **DRAG TO DAW** into your DAW. No selection = the whole recording.

Waveform: Ctrl+wheel zooms, wheel scrolls, double-click resets zoom and clears the selection.
**CLEAR** asks for a second click ("SURE?") within 3 seconds. REC is disabled while a recording exists so it can't be overwritten by accident; CLEAR first. The timer shows `MM:SS:CC` (centiseconds).

## Where files go

- Recordings: `%LOCALAPPDATA%\CS90\Recordings`
- Drag exports: `%LOCALAPPDATA%\CS90\Exports` (never deleted automatically, because DAWs may reference these paths)

## Test checklist

**Audio capture** — play a YouTube video, REC for ~10 s, STOP. Confirm the waveform matches, PLAY sounds right, and the exported WAV opens in an editor as 32-bit float stereo at your device rate. Repeat with Spotify and a game. Switch the default playback device mid-recording and confirm recording continues. Record 30+ minutes once and check memory stays flat.

**FL Studio** — select a region, drag DRAG TO DAW onto the Playlist or Channel rack. A new audio clip with only that region should appear. Repeat with no selection (full recording).

**Ableton Live** — drag onto an audio track in Arrangement or Session view. Same checks.

## Project layout

```
src/CS90/
  Audio/       AudioCaptureEngine, AudioFileWriter, AudioPlaybackEngine, FrameConverter, PeakCache
  Services/    RecordingManager, DragDropExporter
  Controls/    CassetteAnimation, WaveformControl (renderer + selection)
  MainWindow.xaml(.cs)
```

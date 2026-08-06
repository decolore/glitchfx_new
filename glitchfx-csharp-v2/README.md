# Glitch FX — C# / WPF port (v2, work in progress)

This folder is a from-scratch C# port of the original macOS Python app (`effects.py`, `settings.py`, `bridge.py`, `export.py`, `audio.py`, `video_reader_native.py` in the repo root) targeting **Windows / WPF / .NET 8**.

Confirmed before starting this port: the backend logic files in `glitchfx_new` (`settings.py`, `effects.py`, `bridge.py`, `export.py`, `audio.py`, `video_reader_native.py`) are byte-identical to the `glitchfx` repo's `main` branch. The only difference is that `glitchfx_new` does not include the `ui/` AppKit package — so this port is based on the shared backend logic plus the same UI structure documented in `glitchfx`'s `ui/` folder (inspector cards, effects panel, preview viewport, output/export panel).

## Status

This is an initial, functional slice, not a pixel-perfect 1:1 port yet:

- **Fully ported**: project/effect/transform/export settings schema, the full effect parameter schemas (`Models/Settings.cs`), the effect pipeline architecture (`Effects/Pipeline.cs`), and most pixel effects (color grade, posterize, edge glow/neon, chromatic aberration, noise, sharpen, glitch blocks, scanlines, invert, vignette, color map, datamosh, pixel sort, VHS, dither, motion glitch, motion trails) using OpenCvSharp — algorithmically faithful to the Python/OpenCV originals.
- **`ColorGrade` hue shift** now wraps around the 0–360° circle instead of clamping.
- **Audio "bass band" envelope** (`Audio/AudioAnalysis.cs`) now runs a real per-window FFT (via OpenCvSharp's `Cv2.Dft`) and sums squared magnitude below ~250 Hz, replacing the earlier one-pole low-pass approximation.
- **Export** (`Export/ExportService.cs`) now overlaps frame decoding, effect processing, and the ffmpeg-encode pipe write on three concurrent stages (reader/processor/writer threads over bounded queues) instead of one fully sequential loop. Frames are still *applied* to the effect pipeline in strict order on a single stage, since stateful effects (Datamosh, MotionTrails, MotionGlitch, animated-param noise) depend on seeing every prior frame in sequence.
- **Preset save/load** now defaults to `%AppData%/GlitchFX/presets` (auto-created) with a timestamped default filename on save, instead of whatever directory Windows last remembered.
- **Simplified for this first pass**: `TextOverlay` uses WPF text rendering (outline/shadow/color/position/scale supported); the 3D perspective animations (`rotate`/`swing`/`tumble`/`float3d`/`jolt`) are approximated as 2D transforms for now instead of the Cocoa-perspective-warp version — this remains the largest open gap (see "Next steps").
- **UI**: `MainWindow` (toolbar + timeline + preview split), `Views/EffectsPanel` (the effects inspector — beat-sync card, shuffle/animate/audio-reactive controls, per-effect cards with sliders/toggles/color pickers generated from the schema) and `Views/OutputPanel` (export + output resolution settings) are implemented as WPF UserControls with a dark/purple theme matching the original app.
- **Bugfix pass done**: went back through every effect file and fixed several `Mat` arithmetic/`MatExpr` misuses (OpenCvSharp's `+`/`-`/`*`/`.Mul()` operators return lazy `MatExpr` objects that don't expose `ConvertTo`/etc. the way a concrete `Mat` does), a couple of double-`Dispose()` bugs in the preview pipeline and channel-split cleanup, a wrong `ColorConversionCodes` enum name, and a bad `Cv2.Round` call in `Posterize`. All of the arithmetic in the effect files now goes through explicit `Cv2.Add/Subtract/Multiply/AddWeighted` calls instead of operators, specifically to avoid this class of bug.

## Requirements

- Windows 10/11, .NET 8 SDK
- `ffmpeg` and `ffprobe` available on `PATH` (used for export + audio analysis, same as the Python version)
- NuGet: `OpenCvSharp4` + `OpenCvSharp4.runtime.win` (restored automatically on build)

## Build

```powershell
cd glitchfx-csharp-v2
dotnet restore
dotnet build
dotnet run --project GlitchFX
```

## Project layout

```
GlitchFX/
  Models/Settings.cs           # ParamDef, EffectSettings, Transform, ExportSettings, ProjectSettings, schemas, sync/bar-timing helpers
  Effects/BaseEffect.cs         # base class + animated-param noise helper
  Effects/ColorEffects.cs       # ColorGrade, Posterize, ColorInvert, Vignette, ColorMap, Dither
  Effects/DistortionEffects.cs  # EdgeGlow, ChromaticAberration, Noise, Sharpen, Scanlines, GlitchBlocks, VHS, PixelSort
  Effects/MotionEffects.cs      # Datamosh, MotionGlitch, MotionTrails
  Effects/TextOverlayEffect.cs
  Effects/Pipeline.cs           # registry, build/apply pipeline, randomize, audio-reaction post-process
  Video/VideoReader.cs          # threaded OpenCvSharp video reader (mirrors video_reader_native.py)
  Audio/AudioAnalysis.cs        # ffmpeg-decode based loudness/band/beat envelopes (mirrors audio.py)
  Export/ExportService.cs       # ffmpeg pipe export (mirrors export.py; reader/processor/writer pipeline)
  Bridge.cs                     # app logic: settings, pipeline cache, preview render, undo/redo, presets
  Views/EffectsPanel.xaml(.cs)  # the effects inspector panel
  Views/OutputPanel.xaml(.cs)   # export + transform settings panel
  Views/PreviewControl.xaml(.cs) # video preview + drag-to-reposition/scale selection
  MainWindow.xaml(.cs)
  App.xaml(.cs)
GlitchFX.UiTests/
  Program.cs                    # FlaUI-driven smoke test: launches GlitchFX.exe (optionally with a
                                 # test video path arg), clicks through Effects/Output/Randomize,
                                 # scrolls the effect cards list into view, and saves a screenshot
                                 # after each step to ./screenshots. See "Testing" below.
.github/workflows/glitchfx-csharp-v2.yml  # CI: builds the solution on windows-latest, generates a
                                           # synthetic test video with ffmpeg, and runs the smoke test
                                           # above, uploading screenshots as a build artifact.
```

## Testing

There is no macOS/Linux CI runner that can build or execute a WPF app, and this app needs a real
Windows desktop session (WPF has no headless mode), so the setup here is:

1. **`dotnet build` on every push** (`.github/workflows/glitchfx-csharp-v2.yml`, `windows-latest`) —
   this is the fastest way to catch compile errors. Check the **Actions** tab on GitHub after a push;
   if a build fails, paste the error log here and it can be fixed immediately.
2. **`GlitchFX.UiTests`** — a small FlaUI-based console script (not a unit-test framework) that launches
   the real `GlitchFX.exe` (optionally auto-loading a video passed as a command-line argument), clicks
   through the Effects tab, Output tab, and Randomize, scrolls the effects list to also capture the
   per-effect cards (color_grade, posterize, edge_glow, etc.) below the Beat Sync/Global cards, and
   saves a PNG screenshot after each step to `GlitchFX.UiTests/screenshots/`. The CI workflow generates
   a small synthetic clip with `ffmpeg`'s `testsrc` source, passes it in, and runs the script automatically
   after building, uploading the screenshots as the `glitchfx-ui-screenshots` artifact (this step is
   allowed to fail without failing the whole build, since UI automation can be flaky on shared runners).
   Run it locally instead with:
   ```powershell
   cd glitchfx-csharp-v2
   dotnet build GlitchFX\GlitchFX.csproj -c Release
   dotnet run --project GlitchFX.UiTests -c Release -- "GlitchFX\bin\Release\net8.0-windows\GlitchFX.exe" ["path\to\video.mp4"]
   ```

**Important limitation:** this assistant does not have a Windows machine, a display, or access to
GitHub Actions run logs/artifacts in this environment — it can write code and push it, but it cannot
itself execute the app, run `dotnet build`, or view the screenshots the workflow produces. To actually
close the loop ("see a bug, fix it"), please either:
- open the **Actions** tab after a push and paste any red/failing log lines back here, or
- download the `glitchfx-ui-screenshots` artifact (or run the script locally) and share what looks wrong,

and fixes can be pushed right away.

## Next steps

- True 3D-perspective text animations (rotate/swing/tumble/float3d/jolt) instead of the current 2D approximation — this is the largest remaining gap and likely needs its own dedicated pass.
- Verify the FFT-based bass-band envelope and multi-threaded export against real audio/video sources once CI screenshots/logs confirm the smoke test itself is green (currently reasoned through and implemented, not yet observed running).
- Further preset UX polish (e.g. a quick-pick recent-presets list) if useful in practice.

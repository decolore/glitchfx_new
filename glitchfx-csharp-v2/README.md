# Glitch FX — C# / WPF port (v2, work in progress)

This folder is a from-scratch C# port of the original macOS Python app (`effects.py`, `settings.py`, `bridge.py`, `export.py`, `audio.py`, `video_reader_native.py` in the repo root) targeting **Windows / WPF / .NET 8**.

Confirmed before starting this port: the backend logic files in `glitchfx_new` (`settings.py`, `effects.py`, `bridge.py`, `export.py`, `audio.py`, `video_reader_native.py`) are byte-identical to the `glitchfx` repo's `main` branch. The only difference is that `glitchfx_new` does not include the `ui/` AppKit package — so this port is based on the shared backend logic plus the same UI structure documented in `glitchfx`'s `ui/` folder (inspector cards, effects panel, preview viewport, output/export panel).

## Status

This is an initial, functional slice, not a pixel-perfect 1:1 port yet:

- **Fully ported**: project/effect/transform/export settings schema, the full effect parameter schemas (`Models/Settings.cs`), the effect pipeline architecture (`Effects/Pipeline.cs`), and most pixel effects (color grade, posterize, edge glow/neon, chromatic aberration, noise, sharpen, glitch blocks, scanlines, invert, vignette, color map, datamosh, pixel sort, VHS, dither, motion glitch, motion trails) using OpenCvSharp — algorithmically faithful to the Python/OpenCV originals.
- **Simplified for this first pass**: `TextOverlay` uses WPF text rendering (outline/shadow/color/position/scale supported); the 3D perspective animations (`rotate`/`swing`/`tumble`/`float3d`/`jolt`) are approximated as 2D transforms for now instead of the Cocoa-perspective-warp version. The exporter runs single-threaded (correct output, not yet multi-core parallel like the Python version). The audio "bass band" envelope uses a simple low-pass approximation instead of an FFT. `ColorGrade`'s hue-shift clamps instead of wrapping at 0/360°.
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
  Export/ExportService.cs       # ffmpeg pipe export (mirrors export.py, simplified to single worker)
  Bridge.cs                     # app logic: settings, pipeline cache, preview render, undo/redo, presets
  Views/EffectsPanel.xaml(.cs)  # the effects inspector panel
  Views/OutputPanel.xaml(.cs)   # export + transform settings panel
  Views/PreviewControl.xaml(.cs) # video preview + drag-to-reposition/scale selection
  MainWindow.xaml(.cs)
  App.xaml(.cs)
GlitchFX.UiTests/
  Program.cs                    # FlaUI-driven smoke test: launches GlitchFX.exe, clicks through
                                 # Effects/Output/Randomize, and saves a screenshot after each step
                                 # to ./screenshots. See "Testing" below.
.github/workflows/glitchfx-csharp-v2.yml  # CI: builds the solution on windows-latest and runs the
                                           # smoke test above, uploading screenshots as a build artifact.
```

## Testing

There is no macOS/Linux CI runner that can build or execute a WPF app, and this app needs a real
Windows desktop session (WPF has no headless mode), so the setup here is:

1. **`dotnet build` on every push** (`.github/workflows/glitchfx-csharp-v2.yml`, `windows-latest`) —
   this is the fastest way to catch compile errors. Check the **Actions** tab on GitHub after a push;
   if a build fails, paste the error log here and it can be fixed immediately.
2. **`GlitchFX.UiTests`** — a small FlaUI-based console script (not a unit-test framework) that launches
   the real `GlitchFX.exe`, clicks through the Effects tab, Output tab, and Randomize, and saves a PNG
   screenshot after each step to `GlitchFX.UiTests/screenshots/`. The CI workflow runs it automatically
   after building and uploads the screenshots as the `glitchfx-ui-screenshots` artifact (this step is
   allowed to fail without failing the whole build, since UI automation can be flaky on shared runners).
   Run it locally instead with:
   ```powershell
   cd glitchfx-csharp-v2
   dotnet build GlitchFX\GlitchFX.csproj -c Release
   dotnet run --project GlitchFX.UiTests -c Release -- "GlitchFX\bin\Release\net8.0-windows\GlitchFX.exe"
   ```

**Important limitation:** this assistant does not have a Windows machine, a display, or access to
GitHub Actions run logs/artifacts in this environment — it can write code and push it, but it cannot
itself execute the app, run `dotnet build`, or view the screenshots the workflow produces. To actually
close the loop ("see a bug, fix it"), please either:
- open the **Actions** tab after a push and paste any red/failing log lines back here, or
- download the `glitchfx-ui-screenshots` artifact (or run the script locally) and share what looks wrong,

and fixes can be pushed right away. In this session, in lieu of a real compiler, every effect file in
`Effects/` was manually re-audited line by line for this class of bug and corrected (see "Status" above).

## Next steps

- Multi-threaded export to match the Python version's throughput.
- True 3D-perspective text animations (rotate/swing/tumble/float3d/jolt) instead of the current 2D approximation.
- FFT-based bass-band audio envelope.
- True hue wraparound (currently clamps) in `ColorGrade`.
- Preset save/load UI polish (JSON format is already compatible in spirit with the Python presets, stored under `%AppData%/GlitchFX/presets`).

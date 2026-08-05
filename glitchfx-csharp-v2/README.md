# Glitch FX — C# / WPF port (v2, work in progress)

This folder is a from-scratch C# port of the original macOS Python app (`effects.py`, `settings.py`, `bridge.py`, `export.py`, `audio.py`, `video_reader_native.py` in the repo root) targeting **Windows / WPF / .NET 8**.

Confirmed before starting this port: the backend logic files in `glitchfx_new` (`settings.py`, `effects.py`, `bridge.py`, `export.py`, `audio.py`, `video_reader_native.py`) are byte-identical to the `glitchfx` repo's `main` branch. The only difference is that `glitchfx_new` does not include the `ui/` AppKit package — so this port is based on the shared backend logic plus the same UI structure documented in `glitchfx`'s `ui/` folder (inspector cards, effects panel, preview viewport, output/export panel).

## Status

This is an initial, functional slice, not a pixel-perfect 1:1 port yet:

- **Fully ported**: project/effect/transform/export settings schema, the full effect parameter schemas (`Models/Settings.cs`), the effect pipeline architecture (`Effects/Pipeline.cs`), and most pixel effects (color grade, posterize, edge glow/neon, chromatic aberration, noise, sharpen, glitch blocks, scanlines, invert, vignette, color map, datamosh, pixel sort, VHS, dither, motion glitch, motion trails) using OpenCvSharp — algorithmically faithful to the Python/OpenCV originals.
- **Simplified for this first pass**: `TextOverlay` uses WPF text rendering (outline/shadow/color/position/scale supported); the 3D perspective animations (`rotate`/`swing`/`tumble`/`float3d`/`jolt`) are approximated as 2D transforms for now instead of the Cocoa-perspective-warp version. The exporter runs single-threaded (correct output, not yet multi-core parallel like the Python version). The audio "bass band" envelope uses a simple low-pass approximation instead of an FFT.
- **UI**: `MainWindow` (toolbar + timeline + preview split), `Views/EffectsPanel` (the effects inspector — beat-sync card, shuffle/animate/audio-reactive controls, per-effect cards with sliders/toggles/color pickers generated from the schema) and `Views/OutputPanel` (export + output resolution settings) are implemented as WPF UserControls with a dark/purple theme matching the original app.

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
```

## Next steps

- Multi-threaded export to match the Python version's throughput.
- True 3D-perspective text animations (rotate/swing/tumble/float3d/jolt) instead of the current 2D approximation.
- FFT-based bass-band audio envelope.
- Preset save/load UI polish (JSON format is already compatible in spirit with the Python presets, stored under `%AppData%/GlitchFX/presets`).

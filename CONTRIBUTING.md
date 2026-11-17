# Contributing to FrameGrip

Thanks for your interest. This page explains how to build FrameGrip from source and lists the developer-only switches used for automated tests and screenshots.

Code comments are mostly in Italian; UI text lives in `src/FrameForge/Strings.cs` as English/Italian pairs. Please add both languages when you add a string.

## Requirements

- Windows 10 or 11, 64-bit
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 Build Tools with the **C++ desktop** workload (MSVC, x64 and x86), for the native hook
- [Inno Setup 6](https://jrsoftware.org/isdl.php), only to build the installer
- Python 3, only if you change the Vulkan overlay shaders

## Repository layout

| Folder | Contents |
| --- | --- |
| `src/FrameForge` | The WPF app (C#, .NET 8): UI, overlay rendering, frame statistics, sensors, game library |
| `src/native` | The hook DLL injected into games (Direct3D 9/11/12, OpenGL, Vulkan layer), the injector and small test games |
| `src/native/third_party` | MinHook (BSD-2-Clause) and the Khronos Vulkan headers (Apache-2.0 / MIT) |
| `src/installer` | Inno Setup script and its build script |
| `src/branding` | Logo and the script that generates the app icon |

## Building

1. **Native part** (hook DLLs, injectors and test games, 64 and 32 bit):

   ```powershell
   .\src\native\build.ps1
   ```

   Output goes to `src/native/bin/x64` and `src/native/bin/x86`.

2. **App** (the project copies the native files next to the app if they were built):

   ```powershell
   dotnet build src\FrameForge\FrameForge.csproj -c Release -o bin\FrameGrip
   ```

   The app asks for administrator rights (frame timing through ETW and injection into elevated games need them).

3. **Installer** (optional): publishes a self-contained build and compiles `FrameGrip.iss`:

   ```powershell
   .\src\installer\build.ps1
   ```

   Output: `dist\FrameGrip-Setup-<version>.exe`.

If you change the Vulkan overlay shaders, regenerate `src/native/vk_shaders.h` with `python src\native\gen_vk_shaders.py` (no shader compiler needed).

Native test scripts: `src\native\test-limiter.ps1` (limiter timing on the D3D11 test game) and `src\native\test-x86.ps1` (32-bit D3D9 and OpenGL test games).

## Developer environment variables

These are for automated tests and screenshots only. Normal users never set them, and with none of them set the app behaves normally.

| Variable | What it does |
| --- | --- |
| `FRAMEGRIP_SETTINGS` | Use this settings file instead of the user's one; also lets a test copy run next to an installed copy. |
| `FRAMEGRIP_MEASURES` | Use this file for saved measurements instead of the user's one. |
| `FRAMEGRIP_NO_VULKAN` | Do not register the Vulkan overlay layer. |
| `FRAMEGRIP_OFFSCREEN` | Open the main window off-screen (for unattended tests). |
| `FRAMEGRIP_SNAPSHOT_DIR` | Folder where the app saves a picture of its own window on request (window message `WM_APP+77`, wParam = number); also hides the "administrator privileges needed" notices so screenshots stay clean. |
| `FRAMEGRIP_TARGET_PID` | Treat this process as the game even when it is not in the foreground (test games). |
| `FRAMEGRIP_IDEAL_DEMO` | Put the "ideal limit" card in a given state (for example `Result` or `compare`) with synthetic data. |
| `FRAMEGRIP_IDEAL_SECONDS` | Shorten the ideal-limit measurement to this many seconds (thresholds scale with it). |
| `FRAMEGRIP_IDEAL_SELFTEST` | Run the ideal-limit analysis on synthetic traces, write the result to this file and exit. |
| `FRAMEGRIP_LIBRARY_DUMP` | Scan the installed-games library, write the list and timings to this file and exit. |
| `FRAMEGRIP_LIBRARY_PROBE` | With `FRAMEGRIP_LIBRARY_DUMP`: `;`-separated executable paths to look up in the library. |
| `FRAMEGRIP_LIBRARY_ROOTS` | Extra `;`-separated folders scanned as Steam-style libraries (fake libraries for tests). |
| `FRAMEGRIP_LAYOUT_BENCH` | Render every overlay style with few and many sensors, save images and timings to this folder and exit. |
| `FRAMEGRIP_LAYOUT_BENCH_TAG` | Suffix for the benchmark report file name. |
| `FRAMEGRIP_LAYOUT_BENCH_SCALE` | Text size used by the benchmark (1 = 100%). |
| `FRAMEGRIP_BENCH_INFO` | Add the "Info" group and fans to the benchmark sensors. |
| `FRAMEGRIP_VERTICAL_TEST` | Render the column styles at several text sizes and screen sizes into this folder. |
| `FRAMEGRIP_VISIBILITY_TEST` | Render every style at 0% and 80% background over a bright and a dark scene into this folder. |
| `FRAMEGRIP_SIZE_TEST` | Check that per-style text sizes are kept when switching styles; write the result to this file. |
| `FRAMEGRIP_GRID_SHOT` | Render selected styles at 0% and 80% background into this folder. |
| `FRAMEGRIP_GRID_SHOT_LAYOUTS` | Comma-separated styles for `FRAMEGRIP_GRID_SHOT` (`list,bar,grid,console,dashboard,rings,icons`). |
| `FRAMEGRIP_GRID_SHOT_TAG` | Suffix for the `FRAMEGRIP_GRID_SHOT` file names. |
| `FRAMEGRIP_GRID_SHOT_PREFIX` | Prefix for the `FRAMEGRIP_GRID_SHOT` file names. |
| `FRAMEGRIP_GRID_SHOT_SCALE` | Text size for `FRAMEGRIP_GRID_SHOT` (1 = 100%). |
| `FRAMEGRIP_BACKDROP` | Image used as the game scene behind `FRAMEGRIP_GRID_SHOT` renders. |

User-facing switch (not a developer variable): `DISABLE_FRAMEGRIP_VK_LAYER=1` disables FrameGrip's Vulkan layer for a game or for the whole system.

## Pull requests

- Keep changes focused; one topic per pull request.
- Make sure both the native build and `dotnet build` finish without warnings.
- For UI changes, include a screenshot in both the dark and the light theme.
- FrameGrip is licensed under GPL-3.0; by contributing you agree that your contribution is released under the same licence.

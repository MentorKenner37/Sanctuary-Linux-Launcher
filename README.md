# Sanctuary Linux Launcher

Recovery and redevelopment repository for the standalone Sanctuary Linux Launcher.

This launcher was originally developed locally as an Avalonia/.NET application and later packaged as a self-contained Linux x64 build. The original source tree was lost, but the surviving published build and PDB symbols preserve enough structure to reconstruct the project.

## Current status

**Recovery / redevelopment in progress.**

The immediate goal is to rebuild a maintainable source project that reproduces the behavior of the surviving launcher demo. Once it is stable again, the launcher can be merged into `OSFR-Linux-Installer`.

## Surviving original source layout

Debug symbols identify the original project files as:

- `Program.cs`
- `App.axaml`
- `App.axaml.cs`
- `MainWindow.axaml`
- `MainWindow.axaml.cs`
- `MainWindow.Features.cs`
- `MainWindow.Join.cs`
- `MainWindow.News.cs`

## Known functionality preserved in the binary

The surviving `LauncherDemo.dll` exposes launcher functionality including:

- Server selection and server manifests
- Login/authentication flow
- Client manifest parsing
- Client verification and updating
- Client file downloads
- Game launching
- Server connectivity testing
- Server logo loading
- Add/use/remove server flows
- Join server flows
- Settings and news handlers

## Recovery artifacts

The `recovery/` directory contains the small, relevant files from the surviving build:

- `LauncherDemo.dll`
- `LauncherDemo.pdb`
- `LauncherDemo.deps.json`
- `LauncherDemo.runtimeconfig.json`
- `SHA256SUMS.txt`

The full self-contained Linux build is intentionally not committed to source control because it contains the entire .NET runtime and Avalonia dependency set. Keep the original archive separately as the reference build.

## Long-term plan

1. Reconstruct the Avalonia project and UI.
2. Recreate launcher logic from the surviving assembly and PDB symbols.
3. Match behavior against the surviving packaged build.
4. Restore build and release automation.
5. Integrate the finished launcher into `OSFR-Linux-Installer`.

## Related project

- `MentorKenner37/OSFR-Linux-Installer`

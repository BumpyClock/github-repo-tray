# Release guide

`scripts\Build-MsixBundle.ps1` produces an unsigned, full-trust MSIX bundle with
NativeAOT binaries for x64, x86, and ARM64. The identity and publisher in
`src\GitHubTray.App\Package.appxmanifest` are development placeholders. The build
does not submit to the Store, create or trust certificates, install the app, or
configure automatic updates.

## Prerequisites

- Windows, .NET 10 SDK, and WinApp CLI 0.6.1 or later.
- Visual Studio 2022 or later, or Build Tools, with Desktop development with C++,
  Windows SDK, and the ARM64 C++ tools.
- Recipients need the architecture-matched Windows App Runtime version declared
  in the generated package manifest. NativeAOT removes the .NET runtime
  dependency, not the Windows App Runtime dependency.

## Release checklist

1. Review the manifest identity, publisher, and four-part version. For an update,
   increase the version and retain the installed product's identity/publisher.
   Do not change identity merely to work around installation errors.
2. Run the [README tests](../README.md#tests), including runtime-identifier checks
   and both settings/cache persistence tests with reflection-based JSON disabled.
3. Run `.\scripts\Build-MsixBundle.ps1` from the repository root. It publishes
   all three profiles with warnings treated as errors, verifies the native
   executable and required package resources, and bundles fresh layouts.
4. Inspect the bundle manifest for x64, x86, and ARM64 application packages with
   matching versions. Retain publish layouts and native PDBs for crash diagnosis.
   Preserve the SDK's `resources.pri`, which embeds compiled XAML; packaging must
   use `--skip-pri` rather than regenerate it.
5. Sign with the intended publisher certificate and verify the signature before
   distribution. Install and smoke-test each target architecture, including
   tray actions, dashboard loading, accessibility, settings persistence, and
   restart. Cross-publishing does not verify runtime behavior.

### NativeAOT runtime checks

The Copilot quota `ItemsSource` must remain a reference-type, read-only list at
the WinRT boundary. Binding a boxed `ImmutableArray<T>` can compile and publish
successfully but fail during initial NativeAOT layout with `0x80070057`.
Check both the initial empty state and populated quota rows in the native app.

The window keeps its platform-owned presenter and queries the generated
`OverlappedPresenter` projection with C#/WinRT's `As<T>()` helper. Do not replace
it with `OverlappedPresenter.Create()`, which restores a native frame around the
otherwise borderless panel.

## Outputs and profiles

```powershell
.\scripts\Build-MsixBundle.ps1
# To select a new output directory:
.\scripts\Build-MsixBundle.ps1 -OutputDirectory .\artifacts\packages\release-candidate
```

The script prints the final path:
`artifacts\packages\<timestamp>\GitHubTray_<version>_x64_x86_arm64.msixbundle`.
It refuses to reuse an existing output directory and never deletes prior builds.

For an individual native publish:

```powershell
dotnet publish .\src\GitHubTray.App\GitHubTray.App.csproj -c Release -p:PublishProfile=win-x64 -warnaserror
dotnet publish .\src\GitHubTray.App\GitHubTray.App.csproj -c Release -p:PublishProfile=win-x86 -warnaserror
dotnet publish .\src\GitHubTray.App\GitHubTray.App.csproj -c Release -p:PublishProfile=win-arm64 -warnaserror
```

The profiles in `src\GitHubTray.App\Properties\PublishProfiles` default to
`artifacts\nativeaot\win-<architecture>`. Verify an individual publish with:

```powershell
.\scripts\Test-NativeAot.ps1 -PublishDirectory .\artifacts\nativeaot\win-x64
```

Use clean output directories when switching deployment modes. The verifier
requires the `DotNetRuntimeDebugHeader` export, checks package resources, and
rejects managed app/JIT payloads. An `.exe` alone is not proof of NativeAOT.
Package the publish layout, not the managed build output.

The bundle script isolates every build in a new directory. It adds Visual
Studio's Installer directory to PATH for the script's lifetime to support linker
discovery, then restores PATH.

## Signing and installation

An unsigned bundle is not directly sideloadable. Use an existing signing
certificate whose subject matches the manifest's `Identity.Publisher`, or arrange
Store signing through the intended distribution process. Production signing
should use a timestamp service.

Development certificate creation, machine trust changes, and replacing an
installed development package require separate approval. Keep private keys and
certificate passwords outside the repository. Do not commit PFX files or put
credentials into publish profiles.

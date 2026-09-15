# Building a release bundle

The current output is an unsigned, full-trust MSIX bundle with NativeAOT binaries
for x64, x86, and ARM64. The product identity and publisher in
`src\GitHubTray.App\Package.appxmanifest` are still development placeholders.
No Store submission, certificate trust, installation, or automatic update flow is
part of the build.

## Prerequisites

- Windows, .NET 10 SDK, and WinApp CLI 0.6.1 or later.
- Visual Studio 2022 or later, or Build Tools, with Desktop development with C++,
  Windows SDK, and the ARM64 C++ tools.
- Recipients need the architecture-matched Windows App Runtime version declared
  in the generated package manifest. NativeAOT removes the .NET runtime
  dependency, not the Windows App Runtime dependency.

## Checklist

1. Review the package identity, publisher, and four-part version in
   `Package.appxmanifest`. Increase the version for an update and use the same
   identity/publisher as the installed product. Do not change identity merely to
   work around installation errors.
2. Run the tests documented in the README, including
   `.\scripts\Test-RuntimeIdentifiers.ps1`. The settings tests must also pass with
   `--filter FullyQualifiedName~SettingsStoreTests -p:JsonSerializerIsReflectionEnabledByDefault=false`.
3. Run `.\scripts\Build-MsixBundle.ps1`. It publishes all three checked-in profiles
   with warnings treated as errors, verifies the native executable and package
   resources, and bundles fresh per-architecture layouts with WinApp CLI.
4. Inspect the resulting bundle manifest for x64, x86, and ARM64 application
   packages with matching versions. Retain the publish layouts and native PDBs
   for crash diagnosis. The SDK's `resources.pri` embeds the compiled XAML;
   packaging must use `--skip-pri` to preserve it.
5. Before distribution, sign with the intended publisher certificate and verify
   the signature. Perform installation and runtime smoke checks on each target
   architecture, including tray actions, dashboard loading, accessibility,
   settings persistence, and restart. Cross-publishing alone does not verify
   those runtime behaviors.

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
# Or select a new output directory explicitly:
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

Profiles default to `artifacts\nativeaot\win-<architecture>`. The bundle script
overrides the output directory to isolate every build. It adds Visual Studio's
Installer directory to PATH only for the script's lifetime to support linker
discovery; it makes no persistent toolchain changes.

## Signing and installation

An unsigned bundle is not directly sideloadable. Use an existing signing
certificate whose subject matches the manifest's `Identity.Publisher`, or arrange
Store signing through the intended distribution process. Production signing
should use a timestamp service.

Development certificate creation, machine trust changes, and replacing an
installed development package require separate approval. Keep private keys and
certificate passwords outside the repository. Do not commit PFX files or put
credentials into publish profiles.

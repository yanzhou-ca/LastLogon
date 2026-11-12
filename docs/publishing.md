# Publishing LastLogon

Two shapes are available. Both produce a single executable.

| Profile | Size | Needs on the target machine |
|---|---|---|
| `win-<platform>` (default) | ~0.3 MB | the .NET 8 Desktop Runtime |
| `standalone-win-<platform>` | ~60 MB | nothing |

The default build is framework dependent: it carries only this application and relies on the
.NET Desktop Runtime that is already present on managed Windows machines. The standalone build
carries the runtime as well, for a machine that does not have it.

## Commands

```powershell
# framework dependent, one file per platform
dotnet publish LastLogon.csproj -c Release -p:Platform=x64 -r win-x64
dotnet publish LastLogon.csproj -c Release -p:Platform=x86 -r win-x86
dotnet publish LastLogon.csproj -c Release -p:Platform=ARM64 -r win-arm64

# self contained, one file per platform
dotnet publish LastLogon.csproj -c Release -p:PublishProfile=standalone-win-x64
dotnet publish LastLogon.csproj -c Release -p:PublishProfile=standalone-win-x86
dotnet publish LastLogon.csproj -c Release -p:PublishProfile=standalone-win-arm64
```

The output lands in `publish\<runtime identifier>\` and `publish\<runtime identifier>-standalone\`.

The publish profile is chosen by the runtime identifier, not by the platform. The project
declares three runtime identifiers, and the build then uses a runtime specific output path
(`bin\x64 win-x64[0]\`) to keep the builds apart; a profile name built from the platform
would not exist there.

Always name the profile explicitly for the self contained builds, because the name cannot be
derived from the runtime identifier.

## Why the payload is what it is

The application itself is 260 KB. Everything else is the framework.

* **The Windows SDK projections are not included.** The `net8.0-windows10.0.19041.0` target
  framework pulls in `Microsoft.Windows.SDK.NET.dll` (24 MB of WinRT metadata) and
  `WinRT.Runtime.dll`. This application is plain WPF and never calls a WinRT API, so the
  `RemoveWindowsSdkFrameworkReference` target in `LastLogon.csproj` drops that framework
  reference before it is processed.
* **ReadyToRun is off.** It adds about 55 MB to the payload and only shortens the start of a
  tool that is used a handful of times a day.
* **Trimming is off and has to stay off.** WPF cannot be trimmed; a trimmed build fails at
  run time. The .NET runtime is therefore the floor for the self contained build.
* **The self contained build is compressed.** `EnableCompressionInSingleFile` roughly halves
  the file on disk at the cost of a slightly slower first start.

## Reproducible restores

`packages.lock.json` files pin the dependency graph. Restore once, then build and publish with
`--no-restore`, so that the lock files are never rewritten:

```powershell
dotnet restore LastLogon.slnx --force-evaluate --use-lock-file
dotnet build LastLogon.slnx -c Release --no-restore
dotnet publish LastLogon.csproj -c Release -p:Platform=x64 --no-restore
dotnet publish LastLogon.csproj -c Release -p:PublishProfile=standalone-win-x64 --no-restore
```

A publish that restores on its own re-evaluates the lock file for that runtime identifier
only, which makes the next `--locked-mode` restore fail. Run the restore above again to put
the lock files back into their canonical state.

# Releases, installing and writing a plugin

## What a release holds

A tag `v<version>` runs `.github/workflows/release.yml`, which publishes a GitHub release with:

- `SubZeroDev.WindowManager-<version>-win-x64.zip`: the app.
- `SubZeroDev.WindowManager.Sdk.<version>.nupkg`: the plugin SDK. The wire protocol assembly ships inside it.
- `SubZeroDev.WindowManager.PackTool.<version>.nupkg`: `wm-pack`, a .NET tool that packages a plugin.

The packages are not on nuget.org. Use them from a local folder feed.

The zip unpacks to:

```text
SubZeroDev.WindowManager.exe     the guardian: start this
host\                            the host, its built-in content and the built-in plugins
runtime\                         the .NET runtime the app runs on
```

The app needs no installed .NET. Plugin workers use the bundled runtime too, unless the machine has its own .NET or `DOTNET_ROOT` is set.

## Installing

Nothing needs administrator rights.

1. Unpack the zip anywhere you can write to, for example `%LOCALAPPDATA%\Programs\SubZeroDev.WindowManager`.
2. Run `SubZeroDev.WindowManager.exe --first-run`. This adds a Start-menu shortcut and starts the app. If `startAtSignIn` is `true` in `settings.json`, it also adds an `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value. Later runs need no flag.

To remove it, run `SubZeroDev.WindowManager.exe --uninstall`, then delete the folder. That removes the shortcut and the Run value. Your settings, layouts and plugins stay in `%LOCALAPPDATA%\SubZeroDev\WindowManager`. Delete that folder too if you want them gone.

## Writing a plugin

`samples/HelloPlugin` is a complete plugin that uses only the released packages. To start your own:

1. Put the two `.nupkg` files in a folder and add it as a package source.
2. Reference `SubZeroDev.WindowManager.Sdk` from a `net10.0` console project.
3. Write a `plugin.json` beside the project and copy it to the output.
4. Build, then package the build output:

   ```bash
   dotnet tool install SubZeroDev.WindowManager.PackTool --tool-path tools --add-source <feed>
   tools/wm-pack bin/Release/net10.0 --out example.hello.zip
   ```

5. In **Settings → Plugins**, install the zip, then enable the plugin.

## Building a release locally

```bash
pwsh build/release.ps1 -Version 0.1.0
```

```bash
pwsh build/release.ps1 -Version 0.1.0 -Verify
```

The first command writes everything under `artifacts/`. The second packs the built-in plugins again and checks that the release holds exactly what `wm-pack` produces.

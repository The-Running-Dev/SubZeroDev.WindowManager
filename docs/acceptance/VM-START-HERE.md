# WindowManager acceptance VM

## One-command setup in the guest

Finish installing Windows 11 x64 and the guest graphics/integration tools. Take a clean VM snapshot. Open **64-bit Windows PowerShell** as your normal test user and paste:

```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; irm 'https://raw.githubusercontent.com/The-Running-Dev/SubZeroDev.WindowManager/acceptance-vm-2026-11-05-r2/tools/Start-AcceptanceVM.ps1' | iex
```

This downloads a pinned public VM prerelease (about 116 MB), checks its SHA-256 and the inner CI build hash, installs to **`C:\WindowManager`**, opens the local instructions in Notepad, and starts the app alongside Explorer. No GitHub login, Git, SDK, elevation or execution-policy change is needed. Leave this PowerShell window open until it prints the installation path. It does not replace the sign-in shell, stop Explorer or reboot.

The prerelease packages the successful `release-0.1.0-ci.23` build from PR #46, not an arbitrary latest build. The inner CI manifest records merge commit `c86a05468b295febdf918d5b9db3b7fb61e49d5c` and workflow run `42055798364`. That run checks packaging and release smoke behavior; it does not qualify native shell acceptance.

The folder must be new on the first run. A repeat verifies the recorded installed files and reuses this same candidate; it does not overwrite an older/manual installation. For a different destination, or installation without launching:

```powershell
$setup = irm 'https://raw.githubusercontent.com/The-Running-Dev/SubZeroDev.WindowManager/acceptance-vm-2026-11-05-r2/tools/Start-AcceptanceVM.ps1'
& ([scriptblock]::Create($setup)) -InstallDirectory 'C:\WindowManager-Test' -NoLaunch
```

After setup, read `C:\WindowManager\TESTING.md` and run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Test-AcceptanceVM.ps1`. **No Codex is needed inside the VM.** The [manual test workflow](VM-MANUAL.md) includes exact commands for recording observations, enabling/restoring the guest shell, and collecting the evidence ZIP to bring back to the development machine. The [assistant prompt](VM-PROMPT.md) is optional. To have a script do most of the checks for you, see the [scripted acceptance run](VM-SCRIPTED.md) (`Invoke-AcceptanceAuto.ps1`, in bundles that include it).

If download or hash verification fails, nothing is launched. The full error is saved to `%TEMP%\WindowManager-bootstrap-error.log`. If the app exits early, inspect `%LOCALAPPDATA%\SubZeroDev\WindowManager\logs`. A successful bootstrap is an installation/startup observation, not a completed acceptance gate. The instructions below still apply, including separately obtaining the native diagnostic and notification prerequisites.

## Acceptance sequence and earlier local ZIP

Use a clean Windows 11 x64 VM, preferably the same Windows build as your intended desktop. Keep Explorer as the default shell during setup. Use the VM console, an ordinary test account, and snapshots you can restore without signing in to the guest. Enable the hypervisor's normal graphics integration so DWM has a working driver.

The supplied `WindowManager-acceptance-win-x64.zip` contains a self-contained acceptance candidate. No .NET SDK is needed to run it. Extract the complete archive to a stable local folder such as `C:\WindowManager`; keep its `host` and `runtime` directories together with the guardian executable. Do not import your personal configuration.

1. Install Windows and guest integration tools, then take a clean snapshot.
2. Extract the archive. Run `SubZeroDev.WindowManager.exe` with no arguments first, alongside Explorer. Check the panels, launcher and normal exit.
3. Keep Explorer configured until we have collected the session-only taskbar comparison described in the [acceptance ledger](../acceptance.md). That experiment needs a separately built native TaskbarProbe; it is not included in this archive. Its result determines whether native progress and overlays can be implemented. The current decoder still decodes nothing.
4. Before cold-sign-in testing, take another snapshot and read [replacement mode and recovery](../replacement-mode.md). From the extracted folder, `SubZeroDev.WindowManager.exe --shell status` records registration; `--shell on` opts this test account into replacement at its next sign-in; `--shell off` restores the previous registration. These commands are for the guest only.
5. Work through the [acceptance ledger](../acceptance.md). Use ordinary app windows and disposable files. Record failures and recovery as well as passes. Keep notification content private.

If the guest shell becomes unusable, restore the snapshot from the hypervisor. Shift at sign-in and the guardian fallback are additional behaviors to test, not substitutes for having snapshot access.

The VM can qualify shell startup/recovery, input, focus, desktop actions, notifications and DWM rendering when its graphics driver supports them. Notification identity setup additionally needs Windows SDK packaging tools and a certificate created and trusted inside the guest; running the self-contained application alone does not qualify it. The VM cannot establish behavior of your physical Wi-Fi/Bluetooth devices, DDC/CI monitors, built-in panel, or real multi-monitor DPI setup. The planned week of daily use also remains separate.

This is a local working-tree build based on main `4951f01`, with uncommitted changes on `fix/desktop-acceptance`. It is not a published release or a claim that the native gates passed. [Build provenance and SHA-256](acceptance-build.json) identify the exact archive. The archive does not include the native probe executable or portable automated tests; those need a matching development build.

## Repository and CI builds

The release workflow runs for this acceptance work and keeps a `release-<version>` artifact on the pull request Actions run. Download and extract its `SubZeroDev.WindowManager-<version>-win-x64.zip` for a build from the PR commit. That artifact includes these guides and an `acceptance-artifacts.json` with the CI commit, archive size and SHA-256. The local `acceptance-build.json` describes only the earlier local ZIP, not a later CI archive.

The ZIP contains the guardian/launcher at the root, the host under `host`, the bundled runtime under `runtime`, built-in plugins, and supporting libraries. Extract all of it to `C:\WindowManager`, then launch `C:\WindowManager\SubZeroDev.WindowManager.exe` with no arguments. Keep Explorer enabled for the initial coexistence check.

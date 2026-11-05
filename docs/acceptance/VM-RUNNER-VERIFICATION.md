# VM installer and manual runner verification

The original bootstrap failed in Windows PowerShell 5.1 at the default install
location. `New-Item -ItemType Directory -Path 'C:\' -Force` produced:

```text
New-Item : The path is not of a legal form.
CategoryInfo : InvalidArgument: (C:\:String) [New-Item], ArgumentException
FullyQualifiedErrorId : CreateDirectoryArgumentError,Microsoft.PowerShell.Commands.NewItemCommand
```

The installer now uses `Directory.CreateDirectory` for the existing parent and
normalizes drive-root paths consistently during temporary-directory cleanup.
Its manual runner/checklist are delivered in the new pinned `r2` bundle; the
application inside remains the CI build identified in bootstrap-build.json.
The earlier immutable release tag retains its original script; use the new URL
in VM-MANUAL.md or VM-START-HERE.md.

Verification on 2026-11-05:

- Windows PowerShell 5.1.26100.9711: the regression harness reproduced the exact
  drive-root failure after reverting the fix in memory, then passed with it.
- PowerShell 5.1.26100.9711 and 7.6.9: real bundle extracted to a fresh directory
  directly under `C:\`, recording 762 installed files. `-NoLaunch` started no
  process. The repeat IEX path verified hashes and requested the expected
  launcher/instruction viewer, with process starts mocked.
- Both versions: corrupt downloads, unrelated existing folders and changed
  installed files were rejected. Baseline capture, CSV observations with quoted
  text, and real ZIP evidence collection passed. The ZIP contained observations,
  logs, provenance and collection status; application settings were excluded.
- Both versions: shell enabling without the snapshot/force flags was rejected.
  Explicit enable/restore actions requested `--shell on` / `--shell off` with
  process starts mocked. No host shell registration or desktop launch occurred.
- Bundle decompression and bundled-document/runner byte comparisons passed;
  workflow YAML and runner artifact paths parsed successfully.

Reproduce from a matching checkout after downloading the public r2 bundle:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\tests\Test-AcceptanceScripts.ps1 -BundlePath C:\Downloads\WindowManager-VM-acceptance-2026-11-05-r2.zip
pwsh -NoProfile -File tools\tests\Test-AcceptanceScripts.ps1 -BundlePath C:\Downloads\WindowManager-VM-acceptance-2026-11-05-r2.zip
```

The harness retains its randomly named `C:\Codex-WM-regression-*` directories for
inspection. It does not execute real registration or launch desktop apps. These
checks qualify the scripts; real VM sign-in, rendering and input acceptance is
still pending in acceptance.md. CI on the new PR is reported separately.

# Keep one bounded retry for the two observed slow-runner failures. Preserve every attempt.
[CmdletBinding()]
param(
    [string] $TestExecutable = 'tests/SubZeroDev.WindowManager.EndToEnd.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/SubZeroDev.WindowManager.EndToEnd.Tests.exe',
    [Parameter(Mandatory)] [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$class = 'SubZeroDev.WindowManager.EndToEnd.Tests.BudgetTests'
$retryable = @(
    "$class.S23_2_replacement_startup_stays_within_budget",
    "$class.S25_11_idle_with_hundreds_of_windows_stays_within_budget"
)
$root = [IO.Path]::GetFullPath($OutputDirectory)
$originalOutput = $env:SUBZERODEV_WM_BUDGET_OUT
try {
    # Every other budget test remains strict. A failure here never triggers a retry.
    $env:SUBZERODEV_WM_BUDGET_OUT = Join-Path $root 'strict'
    New-Item -ItemType Directory -Path $env:SUBZERODEV_WM_BUDGET_OUT -Force | Out-Null
    & $TestExecutable -class $class -method- $retryable[0] -method- $retryable[1] -xml (Join-Path $env:SUBZERODEV_WM_BUDGET_OUT 'results.xml')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    foreach ($method in $retryable) {
        $name = $method.Substring($class.Length + 1)
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $env:SUBZERODEV_WM_BUDGET_OUT = Join-Path $root "$name/attempt-$attempt"
            New-Item -ItemType Directory -Path $env:SUBZERODEV_WM_BUDGET_OUT -Force | Out-Null
            & $TestExecutable -method $method -xml (Join-Path $env:SUBZERODEV_WM_BUDGET_OUT 'results.xml')
            $code = $LASTEXITCODE
            if ($code -eq 0) {
                if ($attempt -eq 2) {
                    Write-Host "::warning::$name passed on its only retry; both attempts are in the budgets artifact."
                    if ($env:GITHUB_STEP_SUMMARY) {
                        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "- $name passed on retry; inspect both attempts in the budgets artifact."
                    }
                }
                break
            }
            if ($attempt -eq 2) { exit $code }
            Write-Host "::warning::$name failed (exit $code); retrying once with unchanged thresholds."
        }
    }
    exit 0
}
finally {
    $env:SUBZERODEV_WM_BUDGET_OUT = $originalOutput
}

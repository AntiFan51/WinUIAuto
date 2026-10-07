$ErrorActionPreference = 'Stop'

powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') -Configuration Validation
if ($LASTEXITCODE -ne 0) {
    throw 'Build failed'
}

& (Join-Path $PSScriptRoot 'bin\Validation\YuanbaoRecorder.Agent.SmokeTest.exe')
if ($LASTEXITCODE -ne 0) {
    throw "UIA smoke test failed with exit code $LASTEXITCODE"
}

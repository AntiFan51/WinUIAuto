param(
    [ValidateSet('Current', 'Validation')]
    [string]$Configuration = 'Current'
)

$ErrorActionPreference = 'Stop'
$ProjectDirectory = $PSScriptRoot
$OutputDirectory = Join-Path $ProjectDirectory "bin\$Configuration"
$VsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$VisualStudioDirectory = if (Test-Path $VsWhere) {
    & $VsWhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
} else {
    $null
}
$Compiler = if ($VisualStudioDirectory) {
    Join-Path $VisualStudioDirectory 'MSBuild\Current\Bin\Roslyn\csc.exe'
} else {
    $null
}
$FrameworkDirectory = [System.Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory().TrimEnd('\')

if (-not $Compiler -or -not (Test-Path $Compiler)) {
    throw 'Visual Studio Roslyn C# compiler was not found'
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$AgentVersion = '0.16.0-wait-and-branch-control-flow'
$GitCommit = try {
    (& git -C $ProjectDirectory rev-parse --short HEAD 2>$null).Trim()
} catch {
    'unknown'
}
if ([string]::IsNullOrWhiteSpace($GitCommit)) { $GitCommit = 'unknown' }
$GitStatus = try {
    & git -C $ProjectDirectory status --porcelain --untracked-files=no -- . 2>$null
} catch {
    $null
}
if ($GitStatus) { $GitCommit = "$GitCommit-dirty" }
$BuiltAt = [DateTime]::UtcNow.ToString('O')
$GeneratedBuildInfo = Join-Path $OutputDirectory 'AgentBuildInfo.Generated.cs'
@"
namespace YuanbaoRecorder.Agent
{
    internal static class AgentBuildInfo
    {
        internal const string Version = "$AgentVersion";
        internal const string Configuration = "$Configuration";
        internal const string GitCommit = "$GitCommit";
        internal const string BuiltAtUtc = "$BuiltAt";
    }
}
"@ | Set-Content -LiteralPath $GeneratedBuildInfo -Encoding UTF8

$references = @(
    (Join-Path $FrameworkDirectory 'System.dll'),
    (Join-Path $FrameworkDirectory 'System.Core.dll'),
    (Join-Path $FrameworkDirectory 'System.Drawing.dll'),
    (Join-Path $FrameworkDirectory 'System.Runtime.Serialization.dll'),
    (Join-Path $FrameworkDirectory 'System.Windows.Forms.dll'),
    (Join-Path $FrameworkDirectory 'WPF\UIAutomationClient.dll'),
    (Join-Path $FrameworkDirectory 'WPF\UIAutomationTypes.dll'),
    (Join-Path $FrameworkDirectory 'WPF\WindowsBase.dll')
)

$sources = Get-ChildItem -LiteralPath $ProjectDirectory -Filter '*.cs' | ForEach-Object { $_.FullName }
$sources += $GeneratedBuildInfo
$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/langversion:latest',
    '/utf8output',
    "/out:$OutputDirectory\YuanbaoRecorder.Agent.exe"
)
$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += $sources

& $Compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE"
}

$smokeArguments = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/langversion:latest',
    '/utf8output',
    '/define:SMOKE_TEST',
    '/main:YuanbaoRecorder.Agent.SmokeTestProgram',
    "/out:$OutputDirectory\YuanbaoRecorder.Agent.SmokeTest.exe"
)
$smokeArguments += $references | ForEach-Object { "/reference:$_" }
$smokeArguments += $sources

& $Compiler $smokeArguments
if ($LASTEXITCODE -ne 0) {
    throw "C# smoke test compilation failed with exit code $LASTEXITCODE"
}

Write-Output (Join-Path $OutputDirectory 'YuanbaoRecorder.Agent.exe')

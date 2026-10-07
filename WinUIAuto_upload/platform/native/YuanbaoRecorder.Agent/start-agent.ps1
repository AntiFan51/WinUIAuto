param(
    [string]$ServerUrl = $env:CACHE_AGENT_SERVER_URL
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    $PortValue = if ($env:PORT) { $env:PORT } else { '4173' }
    $PortNumber = 0
    if (-not [int]::TryParse($PortValue, [ref]$PortNumber) -or $PortNumber -lt 1 -or $PortNumber -gt 65535) {
        throw 'PORT must be an integer between 1 and 65535'
    }
    $ServerUrl = "http://localhost:$PortNumber"
}
$ServerUri = $null
if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref]$ServerUri) -or
    $ServerUri.Scheme -notin @('http', 'https') -or
    $ServerUri.UserInfo -or $ServerUri.Query -or $ServerUri.Fragment -or
    $ServerUri.AbsolutePath -ne '/') {
    throw 'ServerUrl must be an absolute HTTP(S) service origin without credentials, path, query or fragment'
}
$ServerUrl = $ServerUri.AbsoluteUri.TrimEnd('/')
$ProjectDirectory = $PSScriptRoot
$Configuration = 'Current'
$ExpectedVersion = '0.16.0-wait-and-branch-control-flow'
$ExecutablePath = Join-Path $ProjectDirectory "bin\$Configuration\YuanbaoRecorder.Agent.exe"

Get-Process -Name 'YuanbaoRecorder.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$BuildScript = Join-Path $ProjectDirectory 'build.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $BuildScript -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Agent build failed' }

$BuildRoot = [IO.Path]::GetFullPath((Join-Path $ProjectDirectory 'bin'))
foreach ($BuildFile in Get-ChildItem -LiteralPath $BuildRoot -Recurse -Filter 'YuanbaoRecorder.Agent.exe' -File) {
    $BuildPath = [IO.Path]::GetFullPath($BuildFile.FullName)
    if (-not $BuildPath.StartsWith($BuildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Build cleanup target is outside the build directory'
    }
    if ($BuildPath -ne $ExecutablePath) { Remove-Item -LiteralPath $BuildPath -Force }
}

$env:CACHE_AGENT_SERVER_URL = $ServerUrl
$process = Start-Process -FilePath $ExecutablePath -PassThru

$deadline = [DateTime]::UtcNow.AddSeconds(12)
do {
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) { throw 'Agent exited during startup' }
    try {
        $status = Invoke-RestMethod -Uri "$ServerUrl/api/replay/status" -TimeoutSec 2
        $online = $status.agents | Where-Object {
            $_.online -and
            $_.id -like "*-$($process.Id)" -and
            $_.version -eq $ExpectedVersion -and
            $_.buildConfiguration -eq $Configuration -and
            $_.executablePath -eq $ExecutablePath
        } | Select-Object -First 1
        if ($online) {
            Write-Host "Agent ready: $($online.id)"
            Write-Host "Build: $($online.version) / $($online.buildConfiguration) / $($online.gitCommit)"
            Write-Host "Path: $ExecutablePath"
            exit 0
        }
    } catch {
    }
} while ([DateTime]::UtcNow -lt $deadline)

Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
throw 'Agent failed management-server compatibility validation'

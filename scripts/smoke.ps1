# Runs the Playwright smoke test end to end: update DB -> start app -> test -> stop app.
# Usage: powershell -File scripts/smoke.ps1   (from the repo root; needs xaf-postgres running)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$app  = Join-Path $root 'XafTornado/XafTornado.Blazor.Server'
$url  = 'http://localhost:5000'

dotnet build (Join-Path $root 'XafTornado.slnx') -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project $app --no-build -- --updateDatabase --forceUpdate --silent
if ($LASTEXITCODE -eq 1) { Write-Error 'Database update failed'; exit 1 }

$proc = Start-Process dotnet -ArgumentList "run --project `"$app`" --no-build --urls $url" -PassThru -NoNewWindow
try {
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        try { $ok = (Invoke-WebRequest "$url/" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { $ok = $false }
    } until ($ok -or (Get-Date) -gt $deadline)
    if (-not $ok) { Write-Error "App did not answer on $url within 60 s"; exit 1 }

    $env:XAFTORNADO_BASE_URL = $url
    dotnet test (Join-Path $root 'XafTornado/XafTornado.Smoke') --no-build
    exit $LASTEXITCODE
}
finally {
    # Start-Process dotnet spawns the real app as a child; kill the tree.
    & taskkill /PID $proc.Id /T /F | Out-Null
}

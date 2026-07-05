# =====================================================================
#  setup-python-embed.ps1
#  Scarica la distribuzione Python "embeddable" (x64) e la installa in
#  K2\lib\python-embed\, pronta per essere usata da K2.DisplayPad per
#  eseguire gli script Python legati ai tasti.
#
#  Uso:
#    - doppio click su  setup-python-embed.bat
#    - oppure:  powershell -ExecutionPolicy Bypass -File setup-python-embed.ps1
#
#  La distribuzione "embeddable" e' ridistribuibile (PSF License) e non
#  richiede installazione: e' solo una cartella autosufficiente.
# =====================================================================
$ErrorActionPreference = "Stop"

$version = "3.12.8"          # versione Python da scaricare
$arch    = "amd64"           # K2.DisplayPad e' x64 -> serve amd64

$root    = Split-Path -Parent $MyInvocation.MyCommand.Definition
$target  = Join-Path $root "lib\python-embed"
$zipUrl  = "https://www.python.org/ftp/python/$version/python-$version-embed-$arch.zip"
$zipPath = Join-Path $env:TEMP "python-$version-embed-$arch.zip"

Write-Host ""
Write-Host "  K2 - setup Python embeddable $version ($arch)" -ForegroundColor Cyan
Write-Host "  -------------------------------------------------"

if (Test-Path (Join-Path $target "python.exe")) {
    Write-Host "  Python embeddable gia' presente in:" -ForegroundColor Yellow
    Write-Host "    $target"
    Write-Host "  Niente da fare. (per reinstallare, cancella prima quella cartella)"
    return
}

Write-Host "  Download : $zipUrl"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing

Write-Host "  Estrazione in: $target"
New-Item -ItemType Directory -Force -Path $target | Out-Null
Expand-Archive -Path $zipPath -DestinationPath $target -Force
Remove-Item $zipPath -Force

# La distribuzione embeddable parte "isolata": il file pythonXY._pth
# ridefinisce sys.path. K2 usa comunque k2_runner.py che sistema sys.path,
# ma abilitare "import site" evita sorprese se in futuro vorrai usare pip.
$pth = Get-ChildItem -Path $target -Filter "python*._pth" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($pth) {
    $lines = @(Get-Content $pth.FullName)
    if ($lines -notcontains "import site") {
        ($lines + "import site") | Set-Content $pth.FullName -Encoding ASCII
        Write-Host "  Patch ._pth: abilitato 'import site'."
    }
}

Write-Host ""
Write-Host "  OK - Python embeddable installato in:" -ForegroundColor Green
Write-Host "    $target"
& (Join-Path $target "python.exe") --version
Write-Host ""
Write-Host "  Ora K2.DisplayPad puo' eseguire gli script Python dei tasti." -ForegroundColor Green

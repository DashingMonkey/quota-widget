$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'obj\doc-screenshots'
$taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCsc)) {
    $taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskExe = Join-Path $taskOutput 'DocScreenshots.exe'
& $taskCsc /nologo /utf8output /target:exe /main:DocScreenshots "/out:$taskExe" `
    (Join-Path $taskRoot 'QuotaWidget.cs') (Join-Path $PSScriptRoot 'DocScreenshots.cs') `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $taskExe (Join-Path $taskRoot 'docs\images')
exit $LASTEXITCODE

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'obj\behavior-tests'
$taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCsc)) {
    $taskCsc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskExe = Join-Path $taskOutput 'WidgetBehaviorTests.exe'
& $taskCsc /nologo /utf8output /target:exe /main:WidgetBehaviorTests "/out:$taskExe" `
    (Join-Path $taskRoot 'QuotaWidget.cs') (Join-Path $PSScriptRoot 'WidgetBehaviorTests.cs') (Join-Path $PSScriptRoot 'CustomApiTests.cs') `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $taskExe
exit $LASTEXITCODE

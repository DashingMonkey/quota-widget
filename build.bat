@echo off
chcp 65001 >nul
setlocal

cd /d "%~dp0"

REM 查找 .NET Framework 自带的 C# 编译器 csc.exe（优先 64 位）
set "CSC="
for %%F in (
  "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
  "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) do (
  if exist %%F if not defined CSC set "CSC=%%~F"
)

if not defined CSC (
  echo [错误] 未找到 C# 编译器 csc.exe，请确认已安装 .NET Framework 4.x
  pause
  exit /b 1
)

if not exist "QuotaWidget.cs" (
  echo [错误] 当前目录未找到 QuotaWidget.cs
  pause
  exit /b 1
)

REM If QuotaWidget.exe is running, kill it first (csc cannot overwrite a locked exe).
REM WAS_RUNNING remembers the pre-build state; on success only a previously running
REM instance is restarted. Remove the "if defined WAS_RUNNING" check to always start.
set "WAS_RUNNING="
tasklist /FI "IMAGENAME eq QuotaWidget.exe" 2>NUL | findstr /I "QuotaWidget.exe" >NUL
if not errorlevel 1 (
  set "WAS_RUNNING=1"
  echo 检测到 QuotaWidget.exe 正在运行，先关闭 ...
  taskkill /F /IM QuotaWidget.exe >NUL 2>&1
  REM wait 1s for handle release (ping delay: timeout.exe fails with redirected stdin)
  ping -n 2 127.0.0.1 >NUL
)

echo 编译器: %CSC%
echo 正在编译 QuotaWidget.exe ...
echo.

set "ICON="
if exist "QuotaWidget.ico" set "ICON=/win32icon:QuotaWidget.ico"

REM 嵌入 DPI 声明清单（PerMonitorV2，Win7/8 回退 true/pm）
set "MANIFEST="
if exist "app.manifest" set "MANIFEST=/win32manifest:app.manifest"

set "RC=0"
"%CSC%" /nologo /utf8output /target:winexe /optimize+ %ICON% %MANIFEST% /out:QuotaWidget.exe QuotaWidget.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Security.dll

if errorlevel 1 (
  set "RC=1"
  echo.
  echo [失败] 编译出错，请查看上方日志
) else (
  echo.
  echo [成功] 已生成 QuotaWidget.exe
  REM restart only if it was running before the build
  if defined WAS_RUNNING (
    echo 正在启动 QuotaWidget.exe ...
    start "" "%~dp0QuotaWidget.exe"
  )
)

pause
REM 传递编译结果退出码，供 CI/其他脚本判断构建成败
exit /b %RC%

@echo off
setlocal DisableDelayedExpansion
set "PET_CORE_ROOT=%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -STA -Command "try { $files = @(Get-ChildItem -LiteralPath (Join-Path $env:PET_CORE_ROOT 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }); Add-Type -Path $files -ReferencedAssemblies System.dll,System.Core.dll,System.Windows.Forms.dll,System.Drawing.dll,System.Xml.dll -ErrorAction Stop; [NaiPet.Program]::Run($env:PET_CORE_ROOT) } catch { Write-Error $_; exit 1 }"
set "PET_CORE_EXIT=%ERRORLEVEL%"
if errorlevel 1 pause
exit /b %PET_CORE_EXIT%

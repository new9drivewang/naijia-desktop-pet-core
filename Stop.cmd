@echo off
setlocal DisableDelayedExpansion
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -Command "try { $signal = [Threading.EventWaitHandle]::OpenExisting('Local\NaijiaDesktopPetCoreDemo_Stop_v1'); try { [void]$signal.Set() } finally { $signal.Dispose() } } catch [Threading.WaitHandleCannotBeOpenedException] { Write-Host 'Demo is not running.' } catch { Write-Error $_; exit 1 }"
endlocal

@echo off
rem ============================================================
rem  挂载苹果(HFS+)移动硬盘  →  X: 盘 和 C:\MacDisk（只读）
rem  双击运行，UAC 弹窗请点“是”
rem ============================================================

%SystemRoot%\System32\tasklist.exe /FI "IMAGENAME eq HfsMounter.exe" | %SystemRoot%\System32\find.exe /I "HfsMounter.exe" >nul
if errorlevel 1 (
  echo 正在请求管理员权限挂载硬盘...
  powershell -NoProfile -Command "Start-Process -FilePath 'C:\Users\phoen\Documents\GeoQ产品\AI\Driver\HfsMounter\publish\HfsMounter.exe' -ArgumentList '--mount-dir','C:\MacDisk' -Verb RunAs -WindowStyle Minimized"
)

set /a tries=0
:wait
%SystemRoot%\System32\cmd.exe /c "dir /a C:\MacDisk >nul 2>&1 && exit /b 0 || exit /b 1"
if errorlevel 1 goto :waitcheck
goto :subst
:waitcheck
set /a tries+=1
if %tries% lss 20 (
  ping -n 3 127.0.0.1 >nul
  goto :wait
)
echo 挂载超时：请确认已在 UAC 弹窗点“是”，且移动硬盘已插好。
pause
exit /b 1

:subst
if exist X:\ goto :open
%SystemRoot%\System32\subst.exe X: "C:\MacDisk"

:open
%SystemRoot%\explorer.exe X:\
echo.
echo 完成！Mac 硬盘已挂载：
echo   X: 盘（本会话盘符）和 C:\MacDisk（所有程序可见）
echo   注意：只读，不会改动硬盘上的数据。
echo   本窗口可关闭；挂载在后台继续有效，重启后需重新运行本脚本。
ping -n 5 127.0.0.1 >nul

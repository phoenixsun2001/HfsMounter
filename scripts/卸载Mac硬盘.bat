@echo off
rem Ð¶ÔØ Mac Ó²ÅÌ£¨ÒÆ³ý X: ÅÌ·û²¢½áÊø¹ÒÔØ½ø³Ì£©

%SystemRoot%\System32\subst.exe X: /d >nul 2>&1
%SystemRoot%\System32\taskkill.exe /IM HfsMounter.exe /F >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c','taskkill /IM HfsMounter.exe /F' -Verb RunAs -WindowStyle Hidden" >nul 2>&1
)
echo ÒÑÐ¶ÔØ Mac Ó²ÅÌ£¨X: ºÍ C:\MacDisk£©¡£
ping -n 3 127.0.0.1 >nul

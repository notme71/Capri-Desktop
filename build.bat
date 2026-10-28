@echo off
rem Builds Capri.exe, then Capri-Setup.exe (which embeds Capri.exe as Capri.App.exe).
rem Uses the C# compiler that ships with Windows; nothing else needs to be installed.
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find the C# compiler at %CSC%
    exit /b 1
)
if not exist build mkdir build

rem Every sprite in assets\art is embedded as Capri.Art.<name>.png (see tools\make-art.ps1).
set "ART="
for %%f in (assets\art\*.png) do set "ART=!ART! /resource:%%f,Capri.Art.%%~nf.png"

rem Every language file in assets\lang is embedded as Capri.Lang.<code>.txt.
set "LANGS="
for %%f in (assets\lang\*.txt) do set "LANGS=!LANGS! /resource:%%f,Capri.Lang.%%~nf.txt"

echo Building Capri.exe...
"%CSC%" /nologo /target:winexe /optimize+ /out:build\Capri.exe ^
    /win32icon:assets\chip.ico /win32manifest:app\app.manifest ^
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
    /reference:System.Runtime.Serialization.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:"%SystemRoot%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll" /reference:"%SystemRoot%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll" /reference:"%SystemRoot%\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll" ^
    /resource:assets\capri.png,Capri.Character.png ^
    /resource:assets\perch.png,Capri.Perch.png ^
    /resource:assets\chip.ico,Capri.Chip.ico ^
    /resource:assets\chip.png,Capri.Chip.png ^
    /resource:assets\persona.txt,Capri.Persona.txt ^
    !ART! !LANGS! ^
    app\Capri.cs app\Lang.cs app\AssemblyInfo.cs
if errorlevel 1 (
    echo.
    echo Capri.exe failed to build. If Capri is running from the build folder, quit her from the tray and try again.
    if not defined CAPRI_NOPAUSE pause
    exit /b 1
)

echo Building Capri-Setup.exe...
"%CSC%" /nologo /target:winexe /optimize+ /out:build\Capri-Setup.exe ^
    /win32icon:assets\chip.ico /win32manifest:app\app.manifest ^
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
    /reference:System.Core.dll /reference:Microsoft.CSharp.dll ^
    /resource:build\Capri.exe,Capri.App.exe ^
    !LANGS! ^
    /resource:assets\chip.ico,Capri.Chip.ico ^
    /resource:assets\chip.png,Capri.Chip.png ^
    app\Setup.cs app\Lang.cs app\AssemblyInfo.cs
if errorlevel 1 (
    echo.
    echo Capri-Setup.exe failed to build.
    if not defined CAPRI_NOPAUSE pause
    exit /b 1
)

echo Done. Output is in the build folder.
if not defined CAPRI_NOPAUSE if "%~1"=="" echo Quit Capri from her tray, then run build\Capri-Setup.exe to install the new build.
endlocal

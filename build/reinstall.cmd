@echo off
setlocal EnableExtensions
rem ---------------------------------------------------------------------------
rem  Build TSVN.VS2022.dll, package it, and install it into Visual Studio 2022.
rem
rem  Close Visual Studio first. Nothing here starts VSIXInstaller: a VSIX is a
rem  zip and an installed extension is a folder, so build\install-vsix.py unpacks
rem  it in process and no progress window or result dialog appears.
rem
rem  The window stays open only on failure, so an error is still readable after a
rem  double click. A successful run prints its summary and closes.
rem ---------------------------------------------------------------------------

set "BUILD=%~dp0"

pushd "%BUILD%"

echo === TSVN build ^& install ===
echo.

echo ==^> [1/2] Build and package
call "%BUILD%build.cmd"
if errorlevel 1 goto :fail

echo.
echo ==^> [2/2] Install
python "%BUILD%install-vsix.py"
if errorlevel 1 goto :fail

popd
endlocal
echo.
echo === DONE ===
echo Start Visual Studio 2022 and open a versioned file: TortoiseSVN ^> Show Diff Markers.
exit /b 0

:fail
popd
endlocal
echo.
echo === FAILED - nothing was installed ===
pause
exit /b 1

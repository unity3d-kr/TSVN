@echo off
setlocal EnableExtensions
rem ---------------------------------------------------------------------------
rem  Build TSVN.VS2022.dll, package it, and install it into Visual Studio 2022.
rem
rem  This is only a console entry point: reinstall.py does the work, and
rem  reinstall.vbs runs that without opening a window at all. Use this file when
rem  you want to watch the output, and reinstall.vbs when you do not.
rem
rem  Close Visual Studio first: the installed assembly is in use while it runs.
rem
rem  There is deliberately no pause. A pause is what leaves a console sitting on
rem  screen after a failure, outliving the reason it appeared, and the build is
rem  fast enough to re-run to read the output again. install.log keeps the record
rem  either way.
rem
rem    reinstall.cmd                 build, package, install
rem    reinstall.cmd --build-only    stop after packaging
rem    reinstall.cmd --install-only  install the archive that is already built
rem ---------------------------------------------------------------------------

python "%~dp0reinstall.py" %*

exit /b %ERRORLEVEL%

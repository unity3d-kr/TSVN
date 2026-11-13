@echo off
setlocal EnableExtensions
rem Builds TSVN.VS2022.dll and packages it into TSVN.VS2022.vsix.
rem
rem A console entry point only: reinstall.py owns the procedure, so the steps are
rem identical whether the build starts here, from reinstall.cmd, or from
rem reinstall.vbs - which is the one that opens no window.
rem
rem Requires: .NET SDK, .NET Framework 4.8 targeting pack (provides resgen.exe),
rem Python 3.

python "%~dp0reinstall.py" --build-only %*

exit /b %ERRORLEVEL%

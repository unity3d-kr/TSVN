@echo off
setlocal EnableExtensions
rem ---------------------------------------------------------------------------
rem  Remove the TSVN extension from Visual Studio 2022.
rem
rem  This is only a console entry point: install-vsix.py does the work, and
rem  uninstall.vbs runs that without opening a window at all. Use this file when
rem  you want to watch the output, and uninstall.vbs when you do not.
rem
rem  Close Visual Studio first: the installed assembly is in use while it runs.
rem
rem  There is deliberately no pause, and no confirmation prompt: the folder is
rem  moved into %LOCALAPPDATA%\TSVN\backups rather than deleted, so putting it
rem  back is a copy away.
rem ---------------------------------------------------------------------------

python "%~dp0install-vsix.py" --uninstall %*

exit /b %ERRORLEVEL%

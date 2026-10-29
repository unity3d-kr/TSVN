@echo off
rem Builds TSVN.VS2022.dll and packages it into TSVN.VS2022.vsix.
rem Requires: .NET SDK, .NET Framework 4.8 targeting pack (provides resgen.exe), Python 3.
setlocal
pushd "%~dp0"

echo ==^> [1/3] Compiling OptionsDialog.resx
set "RESGEN="
for %%R in (
  "%ProgramFiles(x86)%\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\resgen.exe"
  "%ProgramFiles(x86)%\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.7.2 Tools\resgen.exe"
  "%ProgramFiles(x86)%\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.6.1 Tools\resgen.exe"
) do (
  if exist %%R set "RESGEN=%%~R"
)
if not defined RESGEN (
  echo    resgen.exe not found. Install the .NET Framework 4.8 targeting pack or the Windows SDK.
  popd
  exit /b 1
)
if not exist obj mkdir obj
"%RESGEN%" "..\TSVN.Shared\Options\OptionsDialog.resx" "obj\OptionsDialog.resources"
if errorlevel 1 goto :fail

echo ==^> [2/3] Building TSVN.VS2022.dll
dotnet build TSVN.Build.csproj -c Release --nologo
if errorlevel 1 goto :fail

echo ==^> [3/3] Packaging VSIX
python package-vsix.py --assembly "bin\Release\net48\TSVN.VS2022.dll"
if errorlevel 1 goto :fail

echo.
echo Done: ..\TSVN.VS2022.vsix
popd
endlocal
exit /b 0

:fail
echo.
echo BUILD FAILED
popd
endlocal
exit /b 1

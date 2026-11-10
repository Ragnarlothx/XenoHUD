@echo off
rem Builds dist\XenoHUD\XenoHUD.exe (self-contained, no .NET install needed). web\ is copied next to it.
cd /d "%~dp0src\XenoHUD"
dotnet publish -c Release -o "%~dp0dist\XenoHUD" -nologo -v q || (echo BUILD FAILED & pause & exit /b 1)
echo Built: %~dp0dist\XenoHUD\XenoHUD.exe

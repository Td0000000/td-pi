@echo off
rem td-pi 构建发布脚本 → C:\Ai\td-pi
setlocal

set DOTNET=C:\Program Files (x86)\dotnet\dotnet.exe
if not exist "%DOTNET%" set DOTNET=dotnet

set OUT=C:\Ai\td-pi

echo [td-pi] 编译发布(自包含 win-x64)...
"%DOTNET%" publish "%~dp0src\TdPi.App\TdPi.App.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "%OUT%"
if errorlevel 1 (
    echo [td-pi] 发布失败。
    exit /b 1
)

echo [td-pi] 完成 → %OUT%\td-pi.exe
endlocal

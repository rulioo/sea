@echo off
rem ============================================================
rem  SEA 一键试玩: 打开工程并自动进入 Play
rem  注意: 本文件是 ANSI(GBK/代码页 936)编码, 不是 UTF-8。
rem  如需改 Unity 安装路径, 只改下面 UNITY 一行即可。
rem ============================================================
chcp 936 >nul
setlocal
cd /d "%~dp0"

set "UNITY=G:\Unity\Editors\6000.0.30f1\Editor\Unity.exe"
set "LOG=%~dp0dev\playtest.log"

title SEA 一键试玩

echo.
echo   [SEA] 一键试玩
echo.

if not exist "%UNITY%" (
  echo   [错误] 找不到 Unity: %UNITY%
  echo          请用记事本打开本文件, 把 UNITY 那行改成你的 Unity.exe 路径。
  goto :end
)

if not exist "%~dp0Assets\Scripts\Runtime\SeaPlay.cs" (
  echo   [错误] 项目路径似乎不对: %~dp0
  goto :end
)

tasklist /fi "imagename eq Unity.exe" 2>nul | findstr /i /c:"Unity.exe" >nul && (
  echo   [提示] 检测到已有一个 Unity 正在运行。
  echo          若那正是本工程, 直接切过去按 Ctrl+P 开玩即可。
  goto :end
)

echo   正在启动 Unity 并自动进入 Play
echo   日志文件: %LOG%
echo.
start "" "%UNITY%" -projectPath "%~dp0" -executeMethod Sea.SeaMenu.OpenSceneAndPlay -logFile "%LOG%"
echo   已发出启动命令。Unity 窗口打开后会自动 Play 试玩。
echo   退出 Play 回编辑态后可 Ctrl+P 再试; 关掉 Unity 窗口即结束。

:end
echo.
pause
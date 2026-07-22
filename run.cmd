@echo off
setlocal

REM 切换到当前 cmd 文件所在目录
cd /d "%~dp0"

REM 运行当前目录中的 .NET 项目
dotnet run

REM 程序退出或报错后保留窗口
pause
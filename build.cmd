@echo off
rem PRT 阅读器 构建脚本（Windows 命令行入口，等价于 build.ps1）
rem 用法示例：
rem   build.cmd
rem   build.cmd -Configuration Debug -Task build,selftest
rem   build.cmd -PublishMode single-file
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "CODE=%ERRORLEVEL%"
exit /b %CODE%

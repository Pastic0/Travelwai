@echo off
title TravelwAI

cd /d "%~dp0src"

echo ================================
echo Restore TravelwAI packages
echo ================================
dotnet restore ".\TravelwAI.Web\TravelwAI.Web.csproj"

if errorlevel 1 (
    echo.
    echo Restore bi loi. Kiem tra loi o tren.
    pause
    exit /b %errorlevel%
)

echo.
echo ================================
echo Start TravelwAI
echo ================================
dotnet run --project ".\TravelwAI.Web\TravelwAI.Web.csproj"

echo.
echo TravelwAI da dung hoac bi loi.
pause

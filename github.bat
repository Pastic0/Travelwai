@echo off
chcp 65001 >nul
cd /d "%~dp0"

git init

git add .

git commit -m "first commit"

git remote add origin https://github.com/Pastic0/Travelwai

git branch -M main

git push -u origin main -f

pause

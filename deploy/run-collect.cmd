@echo off
rem What the scheduled task runs. Keeping the redirection here rather than in the
rem task's command line avoids Task Scheduler quoting problems entirely.
rem
rem The task fires every few minutes; Task Scheduler ignores a trigger while a
rem previous run is still going, so passes run back to back with no gap to tune.
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "deploy\collect.ps1" >> "logs\collect.log" 2>&1

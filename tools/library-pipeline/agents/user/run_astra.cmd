@echo off
rem Astra (GPT-6, high) fixes editor FPS on USER; started in the interactive session by a one-shot scheduled task.
cd /d C:\Projects\Map
set CODEX=C:\Users\user1\AppData\Local\OpenAI\Codex\bin\5449c1f0f26f1f98\codex.exe
if not exist D:\MapChecks\astra mkdir D:\MapChecks\astra
echo %DATE% %TIME% start > D:\MapChecks\astra\run.log
"%CODEX%" exec --ignore-user-config -m gpt-6-astra -c model_reasoning_effort="high" -c features.apps=false -c features.memories=false -s danger-full-access --skip-git-repo-check -C C:\Projects\Map -o D:\MapChecks\astra\last.txt - < D:\MapChecks\astra\ASTRA.md > D:\MapChecks\astra\codex.log 2>&1
echo %DATE% %TIME% exit %ERRORLEVEL% >> D:\MapChecks\astra\run.log

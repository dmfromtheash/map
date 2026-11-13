@echo off
rem Sol 6.1 (xhigh) live checks of Map on USER; started in the interactive session by a one-shot scheduled task.
cd /d D:\MapChecks
set CODEX=C:\Users\user1\AppData\Local\OpenAI\Codex\bin\5449c1f0f26f1f98\codex.exe
echo %DATE% %TIME% start > D:\MapChecks\run.log
"%CODEX%" exec --ignore-user-config -m gpt-6.1-sol -c model_reasoning_effort="xhigh" -c features.apps=false -c features.memories=false -s danger-full-access --skip-git-repo-check -C D:\MapChecks -o D:\MapChecks\last.txt - < D:\MapChecks\BRIEF.md > D:\MapChecks\codex.log 2>&1
echo %DATE% %TIME% exit %ERRORLEVEL% >> D:\MapChecks\run.log

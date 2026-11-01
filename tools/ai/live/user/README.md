# Живой проход на ноутбуке USER (DEC-083)

Claude управляет настоящим окном Map на экране USER (владелец за ним не сидит) из основного ПК DM.
Сделано в аудите 2026-11-01 (журнал `docs/journal/2026-11.md`).

1. Скопировать `agent.ps1` и `helpers.ps1` на USER в `C:\MapAudit\live\` (`scp -i C:\Users\Leshe\.map-remote\user1_ed25519
   -o UserKnownHostsFile=C:\Users\Leshe\.map-remote\known_hosts … user1@192.168.31.44:C:/MapAudit/live/`).
2. Запустить исполнителя в сеансе user1 разовой задачей (из SSH окна и ввод не работают — там сеанс 0):
   ```powershell
   & C:\Users\Leshe\.map-remote\r.ps1 '$a = New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\MapAudit\live\agent.ps1"; $p = New-ScheduledTaskPrincipal -UserId "user\user1" -LogonType Interactive; Register-ScheduledTask -TaskName MapLive -Action $a -Principal $p -Force | Out-Null; Start-ScheduledTask MapLive'
   ```
3. Команды — с DM: `tools/ai/live/user/send.ps1 -Code 'Start-Map; Start-Sleep 6; Front; ScreenShot "00-start"' -Fetch 00-start -To <scratchpad>`.
   Кнопки ищутся по имени контрола (`Find "RiverToolButton"`, `Click-El`, `Press`), список видимых — `Dump "Button"`.
   Координаты — физические пиксели экрана 1920×1080 (масштаб Windows 125 %).
4. В конце: `send.ps1 -Code 'New-Item C:\MapAudit\live\stop -ItemType File'`, закрыть свой экземпляр Map, снять задачу
   `Unregister-ScheduledTask MapLive`, удалить `C:\MapAudit\live`. Копию библиотеки `C:\MapAudit\owner-library` не трогать.

Подводные камни: мастер новой карты, каталог и экспорт — отдельные окна (снимок всего экрана, не окна); кнопки ниже края
колонки нужно прокрутить (`Wheel`); Esc в каталоге закрывает каталог; имя «Новая река» — `StartRiverButton`.
GTX 1070 на USER неисправна — рисует Intel HD 630.

# 12. Версии, локальные обновления и откат

## Статус

- `FACT`: версия приложения имеет единый source of truth `0.2.0` в `Directory.Build.props` и
  сверяется с `version.json`.
- `FACT`: версия, записываемая в новый `.mapproj`, берётся из собранного `Map.App.exe`, а не из
  отдельной строки в presentation-коде.
- `FACT`: та же версия показывается в заголовке настоящего Windows-окна; это не заменяет
  проверку assembly/file version собранного EXE.
- `FACT`: `scripts/build-local-release.ps1` создаёт новый неизменяемый self-contained `win-x64`
  bundle с commit identity, версиями схем и SHA-256 каждого payload-файла. Существующий bundle не
  перезаписывается.
- `FACT`: disposable dirty-source probe создал bundle `Map-0.2.0-win-x64-8de17de27546` из 236
  payload-файлов; manifest отметил `cleanSource=false`, assembly имеет `0.2.0.0`, число checksum
  совпало с payload count. Это проверка упаковки, не релиз.
- `PARTIAL`: clean-checkpoint/clean-VM запуск и реальный переход между двумя версиями ещё не
  выполнены.
- `PROPOSED`: installer, file association и автоматическое обновление относятся к Stage 11 и не
  добавлены скрыто.

## Три независимые версии

1. **Версия приложения** следует Semantic Versioning и меняется в `Directory.Build.props` вместе с
   `version.json` и `CHANGELOG.md`.
2. **Schema `.mapproj`** остаётся `1`; она повышается только при изменении файлового контракта и
   наличии копирующей миграции.
3. **Schema Asset Catalog** остаётся `2`; она развивается независимо от версии приложения.

Обновление приложения не должно переписывать карты или единственную старую копию. Открытие старой
схемы и downgrade-совместимость доказываются отдельно на fixtures.

## Локальный release bundle

Сначала нужен чистый checkpoint и restore нужного runtime. Затем:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-local-release.ps1
```

Результат появляется в `artifacts/releases/Map-<version>-win-x64-<commit>/` и содержит
`Map.App.exe`, `checksums.sha256` и `release-manifest.json`. Скрипт:

- не удаляет и не перезаписывает старые bundle;
- не устанавливает приложение и не меняет file association;
- не создаёт Git tag/release и ничего не загружает;
- по умолчанию отказывается собирать dirty snapshot;
- сохраняет `.incomplete` при ошибке publish для диагностики, не выдавая его за релиз.

## Откат

До принятого installer-провайдера откат является side-by-side:

1. проверить SHA-256 и `release-manifest.json` выбранного старого bundle;
2. закрыть текущий Map;
3. запустить `Map.App.exe` из старого bundle;
4. работать с копией карты, если новая версия уже меняла её schema;
5. не удалять более новую версию до проверки open/save/export сценария.

## Почему автообновление пока не добавлено

Исследован [Velopack](https://github.com/velopack/velopack) (MIT): у него есть C#/Avalonia sample,
локальный file source и self-hosting. Он остаётся сильным кандидатом Stage 11, но installer/updater
создаёт новый executable/supply-chain boundary. До принятия зависимости нужны отдельные license,
signing, downgrade, offline-first, antivirus и clean-VM gates. Облачный Velopack Flow не может быть
условием запуска или сохранения Map.

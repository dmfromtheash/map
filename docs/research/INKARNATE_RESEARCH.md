# Исследование Inkarnate: функциональный слепок

## Результат

Публичные источники дают достаточно данных, чтобы спроектировать независимый локальный редактор. Закрытый аккаунтный walkthrough остаётся дополнительной проверкой микровзаимодействий, а не источником кода или внутренних данных.

## Метод

Изучены:

- официальный homepage, FAQ, Terms и хронология Updates до 25.08.2026;
- официальное видео `2.0 Editor Overview | Inkarnate Stream` целиком, включая Q&A, 2:35:49;
- отдельные официальные видео Text, Line & Shape, Stamp, Layers, Brush и Land;
- автоматические субтитры использованы как вспомогательный текст, а неоднозначные места сверялись между длинным и короткими обзорами;
- локальная login-страница была открыта, но пароль/код не запрашивались и автоматический обход закрытого редактора не выполнялся.

## Правовая и исследовательская граница

Актуальные Terms Inkarnate запрещают, среди прочего, reverse engineering и автоматизированный сбор/скрейпинг; материалы и asset libraries являются собственностью правообладателей. Поэтому:

- не используем DevTools/network capture для извлечения внутренней модели;
- не декомпилируем desktop/browser code;
- не скачиваем и не копируем assets, названия packs, демо-карты или UI graphics;
- не клонируем trade dress;
- описываем только наблюдаемые пользовательские результаты;
- проектируем самостоятельную архитектуру и свои assets.

Это проектная предосторожность, не юридическое заключение.

## Актуальность

Inkarnate быстро меняется. Официальный обзор редактора вышел 10.12.2025 и описывает 2.0. Более свежие Updates имеют приоритет:

- 12.01.2026 отдельный Path tool объявлен retired; его возможности перешли в цветной Line mode;
- 06.03.2026 выпущено Windows desktop app для Creator/Studio;
- 29.05.2026 обновлён dashboard: quick create, recent maps, tutorials, Marketplace highlights, Community maps;
- 18.06.2026 dashboard получил cloneable templates;
- 07.08.2026 Parchment World 2.0 появился в early access.

Следовательно, отдельный legacy Path в нашем продукте не планируется, а рекламное утверждение FAQ «только online» уже не описывает весь ассортимент Inkarnate.

## Dashboard

### Наблюдаемое

- quick map creation;
- recent maps;
- templates и cloneable templates;
- tutorials;
- Marketplace highlights;
- Community maps;
- My Maps и загрузка offline backup в старом/редакторном потоке;
- тарифные ограничения на число карт/assets и export resolution.

### Наша независимая офлайн-трансляция

| Inkarnate service concept | Map local concept |
|---|---|
| My Maps | библиотека `.mapproj` и недавних файлов |
| Quick create | локальный wizard |
| Clone template/community map | копия локального template с новым ID |
| Marketplace | импортируемые лицензированные asset packs, без магазина |
| Community | не входит |
| Account backup | обычный переносимый файл + `.bak` + recovery |
| Subscription limits | технические бюджеты устройства, без искусственных лимитов |

## Компоновка редактора 2.0

- слева — инструменты, влияющие на canvas;
- сверху — постоянные undo/redo/export/history и контекстные параметры;
- справа — Objects и Layers;
- снизу — save/autosave/status/shortcuts;
- pan: `Space+drag`, zoom колесом относительно курсора;
- выбранный объект открывает inspector соответствующего типа;
- многие параметры доступны и в context toolbar, и в inspector, что облегчает доступ, но местами создаёт дублирование.

Наша UX-схема сохраняет предметные области, но не обязана повторять расположение.

## Инструменты

### Select (`V` в обзоре)

- выбор одного/нескольких объектов;
- фильтры Stamp/Text/Line/Shape и sublayers `-10..10`;
- transform, flip, opacity/blend/blur и типоспецифичные свойства;
- Objects panel синхронизируется с selection;
- контекстные действия: hide/lock, duplicate/copy, move sublayer/object layer, group, flatten, delete.

### Land / прежний Mask (`M`)

- предметный слой земли;
- add/subtract, edge roughness/size;
- circle, grid blocks, rectangle, ellipse, polygon, fill;
- move area сохраняет land effects;
- right-click Land style: stroke/outline, inner/outer shadow, ripple;
- несколько Land layers;
- доступность зависит от style/preset; подтверждены world/regional и Watercolor Cities.

Универсальные layer masks существуют отдельно и не имеют land edge effects.

### Brush (`B`)

- active Brush layer;
- soft/hard/chalk/cloudy/grainy/hairy presets;
- size/opacity/softness;
- texture/color/eraser;
- hue/saturation/brightness/contrast и texture placement;
- edge/grid block, rectangle, ellipse, polygon;
- edit brush: pressure/pen dynamics, size/opacity/softness behavior, jitter, spacing, scatter;
- сложные кисти упрощают blending textures;
- brush layer рекомендуется располагать относительно Object layers осознанно, например interior paint ниже walls.

Часть иконок pen dynamics в длинном обзоре не имела ясных tooltips; точную семантику нужно проверить вручную и у нас сделать явной.

### Stamp (`O`)

- каталог и наборы stamps;
- случайный asset из набора;
- recents/favorites;
- scale/rotation/sublayer/flip/transform;
- opacity, shadows, blend, blur, clip behavior;
- hue/saturation/brightness/contrast;
- random rotation, angle increments и random flip;
- place one/click-drag; auto-select toggle;
- advanced area/density scatter;
- replace asset;
- объект попадает в активный Object layer.

### Line (`L`)

- free draw и pen nodes;
- stroke: color, custom stamp asset или clipping behavior;
- sublayer, opacity, blend, blur, guides;
- hue/saturation для asset stroke;
- offset, caps, shadows;
- add/move/cut nodes, straight/curved types;
- portals — перемещаемые неразрушаемые разрывы для дверей/окон, согласованные с grid cell size;
- Path как отдельный инструмент устарел в январе 2026.

### Shape

- free draw/pen/nodes/portals;
- brush, rectangle, ellipse, polygon (в обзоре до 12 сторон);
- stroke использует Line asset/color;
- fill: color/texture/clip behavior, scale/color/rotation/offset/opacity;
- add/subtract/merge для помещений/подземелий;
- overall opacity/blend/blur.

### Text (`T`)

- multiline resizable content;
- font, bold, size, rotation, sublayer, flip;
- fill и picker, outline color/thickness, shadow;
- curve, letter spacing, line height, alignment;
- opacity, blend, blur;
- новый текст наследует настройки последнего выбранного текста;
- labels, titles, map keys и пояснения.

### Notes

- точечная заметка на canvas;
- headings, bullet/numbered lists, bold;
- отдельный список notes, edit/delete;
- система в обзоре названа базовой; это возможность улучшить локальный UX, не меняя основную семантику.

## Слои

Пять наблюдаемых типов: Object, Brush, Land, Filter, Grid; отдельно обязательный Base Color.

### Общее

- add, rename, reorder, duplicate, hide, lock, delete;
- новый/дубликат появляется над active layer;
- opacity/blend;
- большинство типов дублируются, кроме ограничений Base Color;
- множественный выбор layers, linking, folder/group layers и merge layers в обзоре отсутствуют;
- flatten Object content превращает его в Brush layer и лишает пообъектной редактируемости, поэтому опасен.

### Object layer

- stamps, text, line, shape и groups;
- search, select all, per-object hide/lock;
- layer shadow, clipping mode, sublayer hide/lock;
- индивидуальный object blend/shadow комбинируется с layer effect;
- разные Object layers имеют независимые shadows.

### Filter layer

- color, texture, color adjustment и misc categories;
- reorder/duplicate;
- mask support;
- точный список/порядок эффектов требует текущей ручной проверки.

### Grid layer

- видимость и настройки из правой панели;
- можно расположить между другими layers;
- mask позволяет локально скрыть grid;
- точная геометрия и настройки требуют ручного walkthrough.

## Layer masks

- right-click compatible layer → Add mask;
- white opaque, black transparent;
- красный overlay показывает affected area;
- маска Object layer скрывает части объектов и shadows только этого слоя;
- маска Brush layer ограничивает painting;
- маска Grid локально скрывает сетку;
- mask можно отключить/скрыть и копировать на совместимый layer;
- практический совет официального обзора: выделять объекты с особой маской в отдельный Object layer, чтобы не скрыть соседние stamps.

## Каталог и custom assets

- navigator можно свернуть;
- packs или tags;
- filters по style/pack и другим признакам;
- одна логическая карточка может иметь badge количества и раскрывать от нескольких до десятков
  ordered variants; выбранный variant показывается как `N/total` и переключается вручную;
- toggle randomize выбирает следующий variant набора после placement, а Recent/Favorites работают
  на уровне пользовательского выбора;
- Inkarnate-wide catalog может загружаться медленнее;
- `Me` содержит uploaded/private assets и exported composites;
- Art Manager отделён от editor; из обзора переход editor→Art Manager не подтверждён;
- custom asset/composite сохраняет grid-referenced scale, поэтому калибровка перед массовым экспортом критична.

Подробный bounded walkthrough и независимая модель Map:
`ASSET_CATALOG_AND_VARIANT_SETS.md`. Названия, hierarchy, tags, UI и assets конкурента не
переносятся.

## Compositions и межкартовый перенос

- сложную сцену можно export as composite и затем использовать как stamp;
- перед composite требуется save;
- фон/filters отключаются для прозрачного результата;
- composite получает обычные stamp shadows по silhouette;
- процесс может быть медленным;
- copy/paste групп между картами быстрее и сохраняет прозрачность/структуру;
- custom/private asset вызывает предупреждение.

## Файл, история и восстановление

- manual save и autosave toggle;
- named snapshot создаёт checkpoint в history;
- snapshot полезен перед большим экспериментом;
- save as clone;
- resize/crop/expand, snap to grid, world→region use case;
- export image;
- offline backup можно скачать и позже загрузить в своём аккаунте;
- recovery пытается исправить missing/black layers/textures;
- remove private assets может удалить много зависимого содержимого;
- старые 1.0 maps могли не конвертироваться или быть несовместимы.

Для локального продукта cloud-specific ограничения заменяются атомарным файлом, backup, versioned migrations и recovery journal.

## Подтверждённые шероховатости, которые не надо копировать

- icon-only pen controls без ясных tooltips;
- нельзя переназначать shortcuts;
- массовая смена shadow color могла сбрасывать offsets;
- merge/link/multi-select layers отсутствуют;
- clip-mask fill semantics в официальном стриме остались неясными и могли быть bug;
- ведущий несколько раз исправлял себя, поэтому сомнительные детали не объявлены фактами;
- flatten опасен и теряет редактируемость.

## Ручной walkthrough аккаунта без автоматизации

Владелец входит сам и не передаёт пароль/OTP. Исследователь наблюдает только действия владельца и делает собственные заметки/скриншоты, если это разрешено интерфейсом и условиями.

Чек-лист:

1. dashboard: блоки, search/sort, templates, create wizard;
2. create blank map каждого типа, зафиксировать параметры;
3. редактор: screenshots пустого состояния и панелей;
4. каждый tool: basic/advanced, context menu, shortcuts;
5. Layers: каждый тип и его menu/options/masks;
6. catalog: packs/tags/filter/recent/favorite, без выгрузки assets;
7. custom asset: использовать только собственный тестовый PNG;
8. save/autosave/history/snapshot/clone/resize/recovery;
9. export dialog и реальный тестовый результат;
10. сравнить desktop app с browser только на наблюдаемом поведении;
11. записать `FACT / OPEN / CHANGED`, не угадывать.

Не открывать network inspector, storage, исходники, API payloads и не запускать automation по закрытым страницам.

# Разбор книги AI-инструментов для проекта Map

Источник: `AI-инструменты_из_Saved_Messages_06-08-2026.xlsx`, все 13 листов, включая TOP-25 и «Мимо и устарело».

## Как оценивалось

Каждая строка прочитана. Это проверка применимости к **нативному офлайн Windows-редактору**, а не повтор рейтинга книги. Для кандидатов на текущую установку дополнительно проверены официальный источник, платформа, лицензия и дублирование уже имеющимися средствами. Глубокий hands-on тест каждого нерелевантного облачного/веб/медиа-сервиса не выполнялся: он не дал бы проекту результата и часто потребовал бы аккаунт/деньги.

Метки:

- `USE` — применяем сейчас как процесс или установленный инструмент;
- `REFERENCE` — изучать как пример, не включать в продукт;
- `LATER` — возможен в отдельном будущем эксперименте после проверки;
- `NO` — не относится к текущей задаче;
- `REJECT` — противоречит границам, лицензированию или создаёт неоправданный риск.

## Итог установки

Установлены только официальный .NET SDK 10.0.400 и Avalonia Templates 12.1.1. Claude Code, document skills, Git и исследовательские утилиты уже были. Ни один сторонний skill bundle, scraper, website cloner, облачный генератор, local LLM или deployment service не установлен.

## 1. Агенты и методики

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| Claude Code — Dynamic Workflows | USE/LATER | Claude Code Opus — исполнитель. На старте один bounded task; параллельную оркестрацию применять только к независимым тестам/ревью после явного решения |
| Claude Cowork | NO | Дублирует файловую работу; пользователь просит консольный Opus |
| OpenCode | LATER | Возможный резервный агент, но сейчас создаст второй контур и не нужен |
| Google Antigravity | NO | Другая IDE/agent stack; не нужна при Claude Code и Windows-контуре |
| Codex `default_mode_request_user_input` | NO | Настройка интерфейса Codex, а не зависимость/метод продукта |
| Loop Engineering | USE (идея) | Взяты малые closed loops: implement → verify → review; никаких бесконечных циклов |
| Harness-first / Poetiq / RSI loops | USE (идея) | Взята мысль о воспроизводимом harness; автономные self-improvement loops не разрешены |
| Как OpenAI использует Codex | REFERENCE | Полезно как набор рабочих практик, но не архитектура Map |
| «Сайт как папка» | NO | Относится к сайтам и прямо не соответствует desktop-программе |

## 2. Skills и репозитории

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| `jakubkrehel/skills`, семь better-* | LATER | Возможен точечный review `better-interface` после чтения кода/лицензии; весь bundle не ставить |
| Agent-Reach | REJECT | Автоматический доступ/скрейпинг площадок не нужен; для Inkarnate противоречит исследовательской границе |
| Claude SEO | NO | SEO относится к сайтам |
| i-have-adhd | NO | Стиль ответов решается проектными инструкциями без third-party skill |
| humanizer | NO | Не влияет на desktop editor |
| claude-video | NO | Видео-производство не входит |
| diffusionstudio/lottie | LATER | Возможна точечная анимация onboarding, но не до UI system и performance proof |
| papercuts | LATER | Интересен как UX-regression/polish checklist на beta, не как фундамент |
| `anthropics/skills` | USE | Официальные document/PDF/spreadsheet skills уже использованы для исследования; не runtime dependency |
| Superpowers | REFERENCE | TDD/review идеи полезны; установка всего auto-activating bundle может конфликтовать с AGENTS/roadmap |
| claude-mem | NO | Документированный handoff/decisions надёжнее и проверяемее для этого проекта; не хранить лишний контекст |
| awesome-claude-code-subagents | NO | На старте один Opus и одна цель; каталог субагентов не нужен |
| awesome-design-md | REJECT/REFERENCE | Общие design-system паттерны можно изучать, но брать язык бренда и копировать trade dress нельзя |
| Repomix | LATER | Уместен, если repo станет слишком большим для review; сейчас пустой/малый и не нуждается |
| everything-claude-code | NO | Чужая полная конфигурация конфликтует с узкими правилами проекта |
| Get Shit Done (GSD) | REFERENCE | Spec-driven идеи уже покрыты нашими docs; ещё один framework не ставить |
| skillsmp.com | NO | Массовый marketplace повышает supply-chain риск; искать skill только под конкретный блокер |
| Anthropic Academy + Cookbooks | REFERENCE | Официальное обучение полезно Opus/тимлиду, но не зависимость проекта |

## 3. Дизайн и UI

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| frontend_aesthetics от Anthropic | USE (принципы) | Взяты целостная система, tokens и anti-slop ограничения; web/CSS формулировки не копируются |
| Nano Banana Pro prompt method | LATER | Возможен для концептов собственных assets после IP/license review |
| gpt-image guide OpenAI | LATER | Полезен для будущих moodboards/assets с инвариантами; не зависимость приложения |
| awesome-design-md | REJECT/REFERENCE | Не брать чужую фирменную систему; допустимо изучать общие компоненты |
| DesignMD Supply | REJECT | Автоматическое извлечение стиля чужого сайта не нужно и опасно для чистой реализации |
| Aura | NO | Web landing builder |
| 21st.dev | NO | React/Next web components |
| aicss.dev | NO | Web UI для AI-чатов, которых в Map нет |
| Neuform | NO | Landing-page builder |
| Mobbin | REFERENCE | Допустим только как насмотренность desktop/mobile workflows, без копирования экранов |
| Галереи для насмотренности | REFERENCE | Общие layout/accessibility идеи; источник не даёт право на копию |
| Микро-сервисы web-эффектов | NO | WebGL/CSS decorations не нужны нативному редактору |
| Liquid glass CodePen | NO | CSS-эффект, не design requirement |
| scroll-world / three.js | NO | 3D web presentation, не editor canvas |
| SuperDev Pro | REJECT | Инспекция/извлечение чужой верстки не нужна; закрытый Inkarnate не исследовать так |
| PromptCard | LATER | Возможен только для собственного moodboard, не как доказательство design tokens |
| ai-website-cloner-template | REJECT | Прямо противоречит независимому собственному desktop-дизайну |
| Claude Design | LATER | Возможен low-fidelity prototype, но данные нужно проверять и финальный дизайн остаётся нашим |

## 4. Инфраструктура и деплой

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| Cloudflare Workers/D1/R2/AI Gateway/Pages/Registrar | NO | Облачный backend/deploy не нужен офлайн-программе |
| Cloudflare Drop | NO | Облачный обмен файлами не входит |
| integrations.sh | NO | External integrations отсутствуют |
| gitmcp.io | NO | Не нужен для локального repo; не отдавать код внешнему посреднику без задачи |
| Chrome DevTools MCP | REFERENCE | Уже доступен для публичного browser research; desktop app им не тестируется |
| npkill | NO | Node dependency cleanup не относится к .NET repo |
| pnpm (из TOP-25) | NO | Web/Node package manager не нужен runtime/build Map |
| Roughdraft | NO | Browser/web preview flow не соответствует нативной проверке |
| Tweeks | NO | Chrome extension для web tweaks |
| Repomix | LATER | См. выше: только при реальной проблеме масштаба repo |
| Чек-лист безопасности AI-внедрения Касперского | USE (вопросы) | Взяты supply-chain, data exposure и secret hygiene; сам продукт без AI |
| Гигиена Saved Messages | REFERENCE | Общая личная безопасность; проект не читает/не меняет сообщения и пароли |

## 5. Медиа и генерация

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| Qwen-Image-Layered | LATER | Самый релевантный будущий эксперимент для разложения **собственной** иллюстрации на слои; проверить hardware/license/output rights |
| TRELLIS.2 | LATER | Будущий 3D→2D asset pipeline, только отдельно от приложения |
| Marble / World Labs | NO/LATER | World-generation cloud не нужен редактору; возможно только как внешний референс |
| Slop in the Face model | NO | Конкретная генеративная эстетика не соответствует ещё не заданному стилю |
| Higgsfield MCP | NO | Платный облачный агрегатор, не нужен офлайн core |
| ElevenLabs Scribe/video editor | NO | Audio/video вне scope |
| OpusClip | NO | Нарезка видео вне scope |
| Clips agent-native | NO | Video workflow и заявленная platform support не нужны |
| Palmier | NO | Media production вне scope |
| SAM Audio | NO | Audio separation вне scope |
| Radiance for ComfyUI | LATER | Возможен внешний asset experiment, не устанавливать до собственного art brief |
| Hailuo image-to-video | NO | Видео не является частью редактора |

## 6. Презентации и документы

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| Правильный pipeline презентации | REFERENCE | Принцип outline→artifact→visual QA уже применён к docs, но презентация не нужна |
| Gamma | NO | Deck generator; не относится к программе |
| Manus | NO | Общий облачный агент дублирует текущий контур |
| Genspark | NO | Облачные документы/decks не нужны |
| Chronicle | NO | Presentation service |
| Napkin | NO | Visual/document helper не нужен текущему deliverable |
| NotebookLM | NO | Cloud notebook/RAG не нужен локальной спецификации |
| Official docx/pdf/pptx/xlsx skills | USE | PDF и spreadsheet skills применены к источникам; не входят в `.exe` |
| Ramp Sheets | NO | Spreadsheet service не нужен |
| Slides skill + batch generation | NO | Нет задачи на слайды |
| Perplexity presentations/documents | NO | Внешний сервис и не текущий artifact |

## 7. Research и данные

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| OpenSERP | NO | Self-hosted search не нужен; официальные источники доступны напрямую |
| Agent-Reach | REJECT | См. границу автоматического доступа |
| Dataroom/local-model research | NO | Нет корпуса/задачи, оправдывающей local LLM и RAG |
| Claude Code `/deep-research` | REFERENCE | Cross-check подход полезен; текущие продуктовые факты сверены по первичным источникам |
| There's An AI For That | NO | Каталог не заменяет конкретную проверку |
| PromptPort | NO | Prompt library не нужна контролируемым stage prompts |
| Promptly.fyi | NO | То же: собственные prompts привязаны к acceptance |
| OpenAI Codex use cases | REFERENCE | Полезны общие patterns, не architecture dependency |
| gpt5-coding-examples | REFERENCE | Примеры могут обучать, но не должны подменять official desktop docs |
| 195 AI trend reports | NO | Не отвечает на продуктовые риски Map |
| a16z top-100 AI apps | NO | Market list не нужен desktop editor scope |
| IdeaBrowser | NO | Idea generator не нужен заданному продукту |

## 8. Модели и новости

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| Kimi K3 | NO | Встроенная/локальная LLM не требуется |
| Qwen 3.6 35B-A3B | NO | То же |
| Gemini Agentic Vision | LATER | Может анализировать собственные concept images, но не часть editor core |
| Gemini Personal Intelligence | NO | Персональные данные/облако не нужны |
| Claude for Chrome | NO | Browser agent не нужен нативному продукту |
| Cursor visual editor | NO | Ориентирован на web visual editing; Opus работает с native repo |
| ChatGPT + Adobe | LATER | Возможен внешний art pipeline, но не установка/зависимость Map |
| Comet/Atlas/Manus/Strawberry browsers | NO | Дополнительные browser agents не нужны |
| Agent promo-code search | NO | Не относится |
| Heretic | REJECT | «Снятие цензуры» модели не нужно и повышает safety/supply-chain риск |
| Ollama | NO | Map не требует AI; установка заняла бы ресурсы без acceptance value |
| Code Wiki | LATER | Может помочь навигации по большому codebase, но handoff/docs достаточно до роста repo |

## 9. RPG и картографические продукты

| Инструмент | Вердикт | Решение для Map |
|---|---|---|
| Kanka | REFERENCE | Полезен как пример сущностей/связей worldbuilding, но Map не становится campaign manager |
| VVD World | REFERENCE | Общий ориентир интерактивного мира; не зависимость |
| Inkarnate | REFERENCE — основной | Функциональный benchmark; только clean-room observable behavior |
| Dungeon Scrawl | REFERENCE | Второй ориентир для dungeon geometry и быстрого UX; не копировать код/UI |
| Редактор интерактивных карт Бюро | REFERENCE | Идея annotations/navigation возможна позже, но telegram-пост не техническая база |
| Reroll | NO/REFERENCE | Pixel character/scene product, не core map editor |
| Янтарион font | NO до лицензии | Не встраивать шрифт без проверенной лицензии и embedding rights |
| D&D combat tracker | NO | Combat rules вне scope |
| DND Master (Apple) | NO | Mobile campaign/combat app, не Windows map editor |
| Perchance Russian generators | NO | Content generation вне scope |
| 760 artifacts/cards | NO | Контент и права не относятся к редактору |
| Opening scenes techniques | NO | Narrative method, не map construction |
| Dungeon generator prompt | NO | Генеративный content prompt не editor engine |
| ChatGPT as D&D GM | NO | RPG bot вне scope |
| Ollama + Heretic for RPG | REJECT | Не нужен и противоречит минимальному безопасному контуру |
| Sneaky Dice pinned collections | NO | External RPG content, не продуктовая зависимость |

## 10. Лист «Мимо и устарело»

Повторные записи Google Antigravity, Palmier, DND Master, Code Wiki, old GSD, moved frontend-design link, NotebookLM domain, PromptPort, skillsmp count, dungeon prompt, Clips Windows claim, VVD, Dungeon Scrawl, Perchance, DesignMD и Napkin проверены через канонические строки выше. Их устаревшие формулировки не используются.

- **Пароль в Saved Messages:** важное личное предупреждение книги, но Map не авторизован читать/менять аккаунты. В проект ничего не копировалось.
- **Городские/военные сводки и бытовые заметки:** не инструменты и не источники проекта; исключены.
- **TOP-25:** это приоритизация для других папок пользователя, не для Map. Она не даёт разрешения массово устанавливать инструменты. Для Map применены только уже доступный Claude Code, официальные document skills и общие process ideas.

## Выбранный минимальный набор

### Сейчас

- Claude Code Opus;
- .NET SDK 10 / Avalonia 12.1 / Skia packages на этапе spike;
- Git, ripgrep;
- official document/PDF/spreadsheet skills для проектной документации;
- локальные tests/benchmarks/diagnostics по мере stage.

### Позже только по отдельной задаче

- Qwen-Image-Layered или TRELLIS.2 для **собственных** assets;
- один тщательно прочитанный better-* / papercuts review skill;
- Repomix при реальной проблеме размера repo;
- Blender/ComfyUI art pipeline после утверждённого art brief.

Такой отбор сохраняет офлайн-архитектуру, уменьшает supply-chain поверхность и не превращает список Saved Messages в случайный набор зависимостей.

## Контроль полного покрытия названий из книги

Ниже сохранены точные варианты названий тех строк, которые в таблицах выше были сокращены или объединены с дублем. Стрелка ведёт к уже данному решению; это не дополнительная рекомендация.

### TOP-25 и варианты названий

- `Claude Code Dynamic Workflows` → Claude Code — USE/LATER.
- `Claude Cowork под Windows` → Cowork — NO.
- `jakubkrehel/skills — 7 better-* скиллов` → точечный LATER.
- `frontend_aesthetics (анти-AI-slop промпт)` → USE только принципы.
- `Cloudflare Workers + AI Gateway` → NO.
- `Официальные скиллы xlsx / docx / pptx / pdf` → USE для источников.
- `npkill + переход на pnpm` → NO.
- `Gemini 3 Flash Agentic Vision` → LATER вне core.
- `Открывающие сцены — 12 приёмов` → NO.
- `Nano Banana Pro по методике ТЗ` → LATER для собственного art brief.
- `Kimi K3 (открытые веса)` → NO.
- `Онлайн-трекер боя D&D 5e` → NO.
- `Сменить пароль из Saved Messages` → личное предупреждение, не действие Map.

### Точные формулировки остальных листов

- `Codex: default_mode_request_user_input` → NO.
- `Loop Engineering (закрытый и открытый цикл)` → USE как ограниченный цикл.
- `Harness-first (Poetiq, RSI-петли)` → USE только harness principle.
- `Как OpenAI использует Codex (перевод на русский)` → REFERENCE.
- `Сайт как папка, а не как конструктор` → NO.
- `anthropics/skills — официальные скиллы` → USE для документов.
- `frontend_aesthetics от Anthropic (анти-AI-slop)` → USE только принципы.
- `Nano Banana Pro: промпт как ТЗ арт-директора` → LATER.
- `gpt-image-1.5: гайд по промптингу от OpenAI` → LATER.
- `awesome-design-md — 73 готовых DESIGN.md` → REJECT/REFERENCE.
- `Микро-сервисы эффектов (подборка от 04.08.2026)` → NO.
- `Liquid glass — микро-конструктор на CodePen` → NO.
- `scroll-world и threejs-пакет` → NO.
- `Cloudflare: Workers, D1, R2, AI Gateway, Pages, Registrar` → NO.
- `Чек-лист безопасности AI-внедрения (Лаборатория Касперского)` → USE как checklist.
- `TRELLIS.2 (Microsoft)` → LATER.
- `Marble (World Labs)` → NO/LATER.
- `Slop in the Face — русский авангард` → NO.
- `ElevenLabs: Scribe v2 Realtime + видеоредактор` → NO.
- `Clips (agent-native)` → NO.
- `SAM Audio (Meta)` → NO.
- `Radiance для ComfyUI` → LATER.
- `Оживление инфографики (image-to-video)` → NO.
- `Правильный пайплайн генерации презентации` → REFERENCE.
- `Официальные скиллы docx / pdf / pptx / xlsx` → USE для источников.
- `Скилл для слайдов + batch-генерация` → NO.
- `Perplexity: презентации и документы` → NO.
- `Dataroom: дешёвый ресёрч на локальной модели` → NO.
- `Claude Code /deep-research` → REFERENCE.
- `Библиотека юзкейсов Codex от OpenAI` → REFERENCE.
- `195 отчётов про AI-тренды` → NO.
- `a16z: топ-100 AI-приложений` → NO.
- `Kimi K3 (Moonshot)` → NO.
- `Gemini 3 Flash — Agentic Vision` → LATER.
- `Cursor: визуальный редактор` → NO.
- `ChatGPT + Adobe (Photoshop, Express, Acrobat)` → LATER external art pipeline.
- `Браузеры с агентами: Comet, Atlas, Manus, Strawberry` → NO.
- `Агент в браузере ищет промокоды` → NO.
- `Heretic — снятие цензуры с локальных LLM` → REJECT.
- `Ollama — локальные модели без танцев с бубном` → NO.
- `Code Wiki (Google)` → LATER only for a large repo.
- `Редактор интерактивных карт (Бюро Парадигмальных Исследований)` → REFERENCE.
- `Янтарион — эльфийский рунический шрифт` → NO until license proof.
- `Сборник генераторов (perchance, русский)` → NO.
- `760 артефактов + карточки артефактов` → NO.
- `Открывающие сцены — 12 приёмов в 4 категориях` → NO.
- `Генератор подземелий (промпт из Twitter под Fable 5)` → NO.
- `ChatGPT как гейм-мастер D&D (RPG-Bot)` → NO.
- `Ollama + Heretic для РП-сцен` → REJECT.
- `Sneaky Dice — гигазакрепы` → NO.

### Точные устаревшие строки

- `Get Shit Done (старый репозиторий)` → использовать только текущую каноническую запись как REFERENCE.
- `frontend-design: прямая ссылка из поста` → старая ссылка, canonical official skills уже учтён.
- `NotebookLM: смена домена` → NO.
- `skillsmp.com: цифра из поста` → устаревшая цифра, NO.
- `«Генератор подземелий» для настолок` → NO.
- `Clips: заявленная поддержка Windows` → утверждение не нужно проекту, NO.
- `perchance zarif-gen-rus` → NO.
- `Пароль в открытом виде в Saved Messages` → личный риск, в Map данные не переносились.
- `Городские и военные сводки, бытовые заметки` → не инструмент и не источник Map.

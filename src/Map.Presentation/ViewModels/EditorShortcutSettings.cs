using System.Collections.ObjectModel;

namespace Map.Presentation.ViewModels;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
}

public enum ShortcutContext
{
    Global,
    Dashboard,
    Editor,
    Canvas,
    Selection,
    Brush,
    Mask,
    Land,
    Text,
    Stamp,
    Notes,
    Mouse,
}

public sealed record ShortcutInput(string Key, ShortcutModifiers Modifiers);

public sealed record ShortcutCommandDefinition(
    string Id,
    string Name,
    string Group,
    ShortcutContext Context,
    string DefaultGesture,
    string Description,
    bool IsEditable = true);

public sealed record ShortcutConflict(
    ShortcutCommandDefinition First,
    ShortcutCommandDefinition Second,
    string Gesture);

/// <summary>Canonical, platform-neutral keyboard gesture stored in the local settings file.</summary>
public readonly record struct ShortcutGesture(string Key, ShortcutModifiers Modifiers)
{
    private static readonly HashSet<string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Space", "Tab", "Enter", "Escape", "Delete", "Backspace",
        "Left", "Right", "Up", "Down",
        "Slash", "Comma", "Period", "Backquote", "LeftBracket", "RightBracket",
    };

    public static bool TryParse(string? value, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        var modifiers = ShortcutModifiers.None;
        string? key = null;
        foreach (var part in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                if (modifiers.HasFlag(ShortcutModifiers.Control)) return false;
                modifiers |= ShortcutModifiers.Control;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                if (modifiers.HasFlag(ShortcutModifiers.Shift)) return false;
                modifiers |= ShortcutModifiers.Shift;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                if (modifiers.HasFlag(ShortcutModifiers.Alt)) return false;
                modifiers |= ShortcutModifiers.Alt;
            }
            else
            {
                if (key is not null) return false;
                key = NormalizeKey(part);
                if (key is null) return false;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new ShortcutGesture(key, modifiers);
        return true;
    }

    private static string? NormalizeKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return char.ToUpperInvariant(key[0]).ToString();
        }

        var symbol = key switch
        {
            "/" => "Slash",
            "," => "Comma",
            "." => "Period",
            "`" => "Backquote",
            "[" => "LeftBracket",
            "]" => "RightBracket",
            _ => null,
        };
        if (symbol is not null) return symbol;

        return NamedKeys.FirstOrDefault(candidate => candidate.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        parts.Add(Key switch
        {
            "Slash" => "/",
            "Comma" => ",",
            "Period" => ".",
            "Backquote" => "`",
            "LeftBracket" => "[",
            "RightBracket" => "]",
            _ => Key,
        });
        return string.Join('+', parts);
    }
}

/// <summary>
/// The one authoritative registry of commands and their current bindings. It is presentation
/// state, shared by every editor window, and is never serialized into a map project.
/// </summary>
public sealed class EditorShortcutSettings
{
    public static class Commands
    {
        public const string NewMap = "new-map";
        public const string OpenMap = "open-map";
        public const string Save = "save";
        public const string Undo = "undo";
        public const string Redo = "redo";
        public const string RedoAlternate = "redo-alternate";
        public const string ShortcutHelp = "shortcut-help";
        public const string ShortcutHelpAlternate = "shortcut-help-alternate";
        public const string ShortcutSettings = "shortcut-settings";
        public const string SidePanel = "side-panel";
        public const string PreviousTool = "previous-tool";
        public const string GridSnap = "grid-snap";
        public const string Pan = "pan";
        public const string Select = "select";
        public const string Mask = "mask";
        public const string Brush = "brush";
        public const string Land = "land";
        public const string Stamp = "stamp";
        public const string Shape = "shape";
        public const string Line = "line";
        public const string Text = "text";
        public const string Notes = "notes";
        public const string River = "river";
        public const string Road = "road";
        public const string Escape = "escape";
        public const string Delete = "delete";
        public const string DeleteAlternate = "delete-alternate";
        public const string NudgeLeft = "nudge-left";
        public const string NudgeRight = "nudge-right";
        public const string NudgeUp = "nudge-up";
        public const string NudgeDown = "nudge-down";
        public const string NudgeLargeLeft = "nudge-large-left";
        public const string NudgeLargeRight = "nudge-large-right";
        public const string NudgeLargeUp = "nudge-large-up";
        public const string NudgeLargeDown = "nudge-large-down";
        public const string PrimaryUp = "primary-up";
        public const string PrimaryDown = "primary-down";
        public const string PrimaryLargeUp = "primary-large-up";
        public const string PrimaryLargeDown = "primary-large-down";
        public const string SecondaryUp = "secondary-up";
        public const string SecondaryDown = "secondary-down";
        public const string SecondaryLargeUp = "secondary-large-up";
        public const string SecondaryLargeDown = "secondary-large-down";
        public const string TertiaryUp = "stamp-area-up";
        public const string TertiaryDown = "stamp-area-down";
        public const string TertiaryLargeUp = "tertiary-large-up";
        public const string TertiaryLargeDown = "tertiary-large-down";
        public const string RotateLeft = "rotate-left";
        public const string RotateRight = "rotate-right";
        public const string PreviousSelection = "previous-selection";
        public const string MaskMode = "mask-mode";
        public const string Copy = "copy";
        public const string Paste = "paste";
        public const string Lock = "lock";
        public const string SelectAll = "select-all";
        public const string Group = "group";
        public const string Ungroup = "ungroup";
        public const string SublayerUp = "sublayer-up";
        public const string SublayerDown = "sublayer-down";
        public const string ObjectUp = "object-up";
        public const string ObjectDown = "object-down";
        public const string ObjectTop = "object-top";
        public const string ObjectBottom = "object-bottom";
        public const string LayerUp = "layer-up";
        public const string LayerDown = "layer-down";
        public const string StampNextVariant = "stamp-next-variant";
        public const string StampPreviousVariant = "stamp-previous-variant";
        public const string StampCycleVariant = "stamp-cycle-variant";
        public const string StampFocusLibrary = "stamp-focus-library";
        public const string StampDensityUp = "stamp-density-up";
        public const string StampDensityDown = "stamp-density-down";
        public const string FinishPath = "finish-path";
        public const string FontPrevious = "font-previous";
        public const string FontNext = "font-next";
    }

    private static readonly IReadOnlyList<ShortcutCommandDefinition> DefinitionList =
    [
        D(Commands.NewMap, "Новая карта", "Файл", ShortcutContext.Global, "Ctrl+N", "Создать новую карту"),
        D(Commands.OpenMap, "Открыть карту", "Файл", ShortcutContext.Global, "Ctrl+O", "Открыть файл .mapproj"),
        D(Commands.Save, "Сохранить", "Файл", ShortcutContext.Editor, "Ctrl+S", "Сохранить открытую карту"),
        D(Commands.Undo, "Отменить", "Правка", ShortcutContext.Editor, "Ctrl+Z", "Отменить последнее изменение"),
        D(Commands.Redo, "Повторить", "Правка", ShortcutContext.Editor, "Ctrl+Shift+Z", "Повторить отменённое изменение"),
        D(Commands.RedoAlternate, "Повторить (доп.)", "Правка", ShortcutContext.Editor, "Ctrl+Y", "Второе сочетание повтора"),
        D(Commands.Copy, "Копировать выделение", "Правка", ShortcutContext.Selection, "Ctrl+C", "Копировать все выделенные объекты"),
        D(Commands.Paste, "Вставить", "Правка", ShortcutContext.Editor, "Ctrl+V", "Вставить скопированные объекты"),
        D(Commands.Lock, "Блокировка выделения", "Правка", ShortcutContext.Selection, "Ctrl+L", "Заблокировать или разблокировать выделение"),
        D(Commands.SelectAll, "Выбрать всё по фильтру", "Выделение", ShortcutContext.Editor, "Ctrl+A", "Выбрать доступные объекты по поиску и фильтру"),
        D(Commands.Group, "Сгруппировать", "Выделение", ShortcutContext.Selection, "Ctrl+G", "Создать группу из выделения"),
        D(Commands.Ungroup, "Разгруппировать", "Выделение", ShortcutContext.Selection, "Ctrl+Shift+G", "Разобрать выбранную группу"),
        D(Commands.ShortcutHelp, "Справка по сочетаниям", "Интерфейс", ShortcutContext.Editor, "K", "Открыть актуальную справку"),
        D(Commands.ShortcutHelpAlternate, "Справка по сочетаниям (доп.)", "Интерфейс", ShortcutContext.Editor, "Shift+/", "Второе сочетание справки"),
        D(Commands.ShortcutSettings, "Настроить сочетания", "Интерфейс", ShortcutContext.Editor, "", "Открыть редактор сочетаний"),
        D(Commands.SidePanel, "Правая панель", "Интерфейс", ShortcutContext.Editor, "/", "Показать или скрыть правую панель"),
        D(Commands.GridSnap, "Привязка к сетке", "Интерфейс", ShortcutContext.Editor, "G", "Включить или выключить привязку"),
        D(Commands.PreviousTool, "Предыдущий инструмент", "Инструменты", ShortcutContext.Canvas, "Tab", "Вернуться к предыдущему инструменту"),
        D(Commands.Pan, "Временная панорама", "Инструменты", ShortcutContext.Canvas, "Space", "Удерживать для панорамы холста"),
        D(Commands.Select, "Инструмент «Выбор»", "Инструменты", ShortcutContext.Editor, "V", "Активировать выбор"),
        D(Commands.Line, "Инструмент «Линия»", "Инструменты", ShortcutContext.Editor, "L", "Активировать линии"),
        D(Commands.Shape, "Инструмент «Фигура»", "Инструменты", ShortcutContext.Editor, "R", "Активировать фигуры"),
        D(Commands.Text, "Инструмент «Текст»", "Инструменты", ShortcutContext.Editor, "T", "Активировать текст"),
        D(Commands.Stamp, "Ассеты — размещение", "Инструменты", ShortcutContext.Editor, "O", "Открыть каталог ассетов и перейти к размещению"),
        D(Commands.Brush, "Инструмент «Кисть»", "Инструменты", ShortcutContext.Editor, "B", "Активировать кисть"),
        D(Commands.Mask, "Инструмент «Маска»", "Инструменты", ShortcutContext.Editor, "M", "Активировать маску"),
        D(Commands.Land, "Инструмент «Земля»", "Инструменты", ShortcutContext.Editor, "P", "Активировать землю (на русской раскладке — клавиша «З»)"),
        D(Commands.Notes, "Заметки", "Инструменты", ShortcutContext.Editor, "N", "Открыть панель заметок"),
        D(Commands.River, "Инструмент «Река»", "Инструменты", ShortcutContext.Editor, "H", "Активировать реки"),
        D(Commands.Road, "Инструмент «Дорога»", "Инструменты", ShortcutContext.Editor, "J", "Активировать дороги"),
        D(Commands.Escape, "Выйти из группы / снять выбор", "Выделение", ShortcutContext.Editor, "Escape", "Закрыть текущий уровень выделения"),
        D(Commands.FinishPath, "Завершить контур", "Инструменты", ShortcutContext.Canvas, "Enter", "Замкнуть контур режима «По узлам»"),
        D(Commands.Delete, "Удалить выделение", "Выделение", ShortcutContext.Selection, "Delete", "Удалить выбранные объекты"),
        D(Commands.DeleteAlternate, "Удалить выделение (доп.)", "Выделение", ShortcutContext.Selection, "Backspace", "Второе сочетание удаления"),
        D(Commands.NudgeLeft, "Сдвинуть влево", "Выделение", ShortcutContext.Selection, "Left", "Сдвинуть на один шаг"),
        D(Commands.NudgeRight, "Сдвинуть вправо", "Выделение", ShortcutContext.Selection, "Right", "Сдвинуть на один шаг"),
        D(Commands.NudgeUp, "Сдвинуть вверх", "Выделение", ShortcutContext.Selection, "Up", "Сдвинуть на один шаг"),
        D(Commands.NudgeDown, "Сдвинуть вниз", "Выделение", ShortcutContext.Selection, "Down", "Сдвинуть на один шаг"),
        D(Commands.NudgeLargeLeft, "Сдвинуть влево крупно", "Выделение", ShortcutContext.Selection, "Shift+Left", "Сдвинуть на десять шагов"),
        D(Commands.NudgeLargeRight, "Сдвинуть вправо крупно", "Выделение", ShortcutContext.Selection, "Shift+Right", "Сдвинуть на десять шагов"),
        D(Commands.NudgeLargeUp, "Сдвинуть вверх крупно", "Выделение", ShortcutContext.Selection, "Shift+Up", "Сдвинуть на десять шагов"),
        D(Commands.NudgeLargeDown, "Сдвинуть вниз крупно", "Выделение", ShortcutContext.Selection, "Shift+Down", "Сдвинуть на десять шагов"),
        D(Commands.PrimaryUp, "Размер +", "Параметры", ShortcutContext.Editor, "W", "Увеличить выбранный или размещаемый объект"),
        D(Commands.PrimaryDown, "Размер −", "Параметры", ShortcutContext.Editor, "S", "Уменьшить выбранный или размещаемый объект"),
        D(Commands.PrimaryLargeUp, "Размер + крупно", "Параметры", ShortcutContext.Editor, "Shift+W", "Увеличить объект крупным шагом"),
        D(Commands.PrimaryLargeDown, "Размер − крупно", "Параметры", ShortcutContext.Editor, "Shift+S", "Уменьшить объект крупным шагом"),
        D(Commands.SecondaryUp, "Вторичный параметр +", "Параметры", ShortcutContext.Editor, "E", "Непрозрачность или редактор заметки"),
        D(Commands.SecondaryDown, "Вторичный параметр −", "Параметры", ShortcutContext.Editor, "Q", "Уменьшить непрозрачность"),
        D(Commands.SecondaryLargeUp, "Вторичный параметр + крупно", "Параметры", ShortcutContext.Editor, "Shift+E", "Крупный шаг непрозрачности"),
        D(Commands.SecondaryLargeDown, "Вторичный параметр − крупно", "Параметры", ShortcutContext.Editor, "Shift+Q", "Крупный шаг непрозрачности"),
        D(Commands.TertiaryUp, "Контекстный параметр +", "Параметры", ShortcutContext.Editor, "C", "Область размещения, мягкость или кривизна"),
        D(Commands.TertiaryDown, "Контекстный параметр −", "Параметры", ShortcutContext.Editor, "Z", "Область размещения, мягкость или кривизна"),
        D(Commands.TertiaryLargeUp, "Контекстный параметр + крупно", "Параметры", ShortcutContext.Editor, "Shift+C", "Крупный контекстный шаг"),
        D(Commands.TertiaryLargeDown, "Контекстный параметр − крупно", "Параметры", ShortcutContext.Editor, "Shift+Z", "Крупный контекстный шаг"),
        D(Commands.RotateLeft, "Повернуть влево", "Параметры", ShortcutContext.Editor, "A", "Повернуть выбранный или размещаемый объект влево"),
        D(Commands.RotateRight, "Повернуть вправо", "Параметры", ShortcutContext.Editor, "D", "Повернуть выбранный или размещаемый объект вправо"),
        D(Commands.PreviousSelection, "Предыдущее выделение", "Выделение", ShortcutContext.Editor, "`", "Вернуть последнее выделение"),
        D(Commands.MaskMode, "Режим маски", "Параметры", ShortcutContext.Mask, "X", "Переключить скрытие и возврат"),
        D(Commands.SublayerUp, "Подслой выше", "Слои", ShortcutContext.Selection, "Ctrl+Up", "Поднять выделение по подслою"),
        D(Commands.SublayerDown, "Подслой ниже", "Слои", ShortcutContext.Selection, "Ctrl+Down", "Опустить выделение по подслою"),
        D(Commands.ObjectUp, "Объект выше", "Слои", ShortcutContext.Selection, "Alt+Up", "Поднять выделение над соседями подслоя"),
        D(Commands.ObjectDown, "Объект ниже", "Слои", ShortcutContext.Selection, "Alt+Down", "Опустить выделение под соседей подслоя"),
        D(Commands.ObjectTop, "Объект наверх", "Слои", ShortcutContext.Selection, "Shift+Alt+Up", "Сделать выделение самым верхним в слое"),
        D(Commands.ObjectBottom, "Объект вниз", "Слои", ShortcutContext.Selection, "Shift+Alt+Down", "Сделать выделение самым нижним в слое"),
        D(Commands.LayerUp, "Слой выше", "Слои", ShortcutContext.Editor, "Ctrl+Shift+Up", "Поднять активный слой"),
        D(Commands.LayerDown, "Слой ниже", "Слои", ShortcutContext.Editor, "Ctrl+Shift+Down", "Опустить активный слой"),
        D(Commands.StampFocusLibrary, "Поиск в каталоге", "Размещение ассета", ShortcutContext.Editor, "F", "Открыть каталог ассетов и перейти к поиску"),
        D(Commands.StampNextVariant, "Следующий вариант", "Размещение ассета", ShortcutContext.Stamp, "Alt+Right", "Выбрать следующий вариант ассета"),
        D(Commands.StampPreviousVariant, "Предыдущий вариант", "Размещение ассета", ShortcutContext.Stamp, "Alt+Left", "Выбрать предыдущий вариант ассета"),
        D(Commands.StampDensityUp, "Плотность размещения +", "Размещение ассета", ShortcutContext.Stamp, "Alt+Q", "Увеличить плотность"),
        D(Commands.StampDensityDown, "Плотность размещения −", "Размещение ассета", ShortcutContext.Stamp, "Alt+A", "Уменьшить плотность"),
        D(Commands.StampCycleVariant, "Вариант и набор колёсиком", "Мышь", ShortcutContext.Mouse, "Alt+Wheel", "Варианты текущего набора, за последним — следующий набор каталога", false),
        D(Commands.FontPrevious, "Предыдущий шрифт", "Текст", ShortcutContext.Selection, "[", "Выбрать предыдущий локальный шрифт"),
        D(Commands.FontNext, "Следующий шрифт", "Текст", ShortcutContext.Selection, "]", "Выбрать следующий локальный шрифт"),
    ];

    private static readonly IReadOnlyDictionary<string, ShortcutCommandDefinition> DefinitionsById =
        new ReadOnlyDictionary<string, ShortcutCommandDefinition>(
            DefinitionList.ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, string> _bindings;
    private readonly IReadOnlyDictionary<string, string> _readOnlyBindings;

    public EditorShortcutSettings()
    {
        _bindings = DefinitionList.ToDictionary(
            definition => definition.Id,
            definition => definition.DefaultGesture,
            StringComparer.OrdinalIgnoreCase);
        _readOnlyBindings = new ReadOnlyDictionary<string, string>(_bindings);
    }

    public EditorShortcutSettings(IReadOnlyDictionary<string, string> current) : this() =>
        ApplyPersisted(current);

    public static IReadOnlyList<ShortcutCommandDefinition> Definitions => DefinitionList;

    public static IReadOnlySet<string> KnownCommandIds { get; } =
        new HashSet<string>(DefinitionList.Select(definition => definition.Id), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Bindings => _readOnlyBindings;

    public event EventHandler? Changed;

    public void Set(string command, string? gesture)
    {
        if (!DefinitionsById.TryGetValue(command?.Trim() ?? "", out var definition))
        {
            throw new ArgumentException("Unknown shortcut command.", nameof(command));
        }

        if (!definition.IsEditable)
        {
            throw new InvalidOperationException($"The {definition.Id} gesture is informational only.");
        }

        var canonical = "";
        if (!string.IsNullOrWhiteSpace(gesture))
        {
            if (!ShortcutGesture.TryParse(gesture, out var parsed))
            {
                throw new ArgumentException("Unsupported keyboard gesture.", nameof(gesture));
            }

            canonical = parsed.ToString();
        }

        if (!string.Equals(_bindings[definition.Id], canonical, StringComparison.Ordinal))
        {
            _bindings[definition.Id] = canonical;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Get(string command) => _bindings.TryGetValue(command, out var value) ? value : "";

    public void Reset(string command)
    {
        if (!DefinitionsById.TryGetValue(command, out var definition) || !definition.IsEditable)
        {
            throw new ArgumentException("Unknown editable shortcut command.", nameof(command));
        }

        Set(command, definition.DefaultGesture);
    }

    public void ResetAll()
    {
        var changed = false;
        foreach (var definition in DefinitionList.Where(definition => definition.IsEditable))
        {
            if (string.Equals(_bindings[definition.Id], definition.DefaultGesture, StringComparison.Ordinal))
            {
                continue;
            }

            _bindings[definition.Id] = definition.DefaultGesture;
            changed = true;
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public int ApplyPersisted(IReadOnlyDictionary<string, string> persisted)
    {
        var applied = 0;
        foreach (var (id, value) in persisted)
        {
            if (!DefinitionsById.TryGetValue(id, out var definition) || !definition.IsEditable)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value) && !ShortcutGesture.TryParse(value, out _))
            {
                continue;
            }

            _bindings[definition.Id] = string.IsNullOrWhiteSpace(value)
                ? ""
                : ShortcutGesture.TryParse(value, out var parsed) ? parsed.ToString() : "";
            applied++;
        }

        return applied;
    }

    public IReadOnlyDictionary<string, string> GetOverrides() =>
        new ReadOnlyDictionary<string, string>(DefinitionList
            .Where(definition => definition.IsEditable
                && !string.Equals(_bindings[definition.Id], definition.DefaultGesture, StringComparison.Ordinal))
            .ToDictionary(definition => definition.Id, definition => _bindings[definition.Id],
                StringComparer.OrdinalIgnoreCase));

    public void ReplaceWith(EditorShortcutSettings source)
    {
        var changed = false;
        foreach (var definition in DefinitionList.Where(definition => definition.IsEditable))
        {
            var value = source.Get(definition.Id);
            if (string.Equals(_bindings[definition.Id], value, StringComparison.Ordinal)) continue;
            _bindings[definition.Id] = value;
            changed = true;
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public string? Resolve(ShortcutInput input, IReadOnlySet<ShortcutContext> activeContexts)
    {
        foreach (var definition in DefinitionList)
        {
            if (!definition.IsEditable
                || (definition.Context != ShortcutContext.Global && !activeContexts.Contains(definition.Context))
                || !ShortcutGesture.TryParse(Get(definition.Id), out var gesture))
            {
                continue;
            }

            if (gesture.Key.Equals(input.Key, StringComparison.OrdinalIgnoreCase)
                && gesture.Modifiers == input.Modifiers)
            {
                return definition.Id;
            }
        }

        return null;
    }

    public IReadOnlyList<ShortcutConflict> GetConflicts()
    {
        var conflicts = new List<ShortcutConflict>();
        var editable = DefinitionList.Where(definition => definition.IsEditable).ToArray();
        for (var firstIndex = 0; firstIndex < editable.Length; firstIndex++)
        {
            var first = editable[firstIndex];
            var firstGesture = Get(first.Id);
            if (firstGesture.Length == 0) continue;

            for (var secondIndex = firstIndex + 1; secondIndex < editable.Length; secondIndex++)
            {
                var second = editable[secondIndex];
                if (!ContextsOverlap(first.Context, second.Context)
                    || !string.Equals(firstGesture, Get(second.Id), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                conflicts.Add(new ShortcutConflict(first, second, firstGesture));
            }
        }

        return conflicts;
    }

    public static string ContextName(ShortcutContext context) => context switch
    {
        ShortcutContext.Global => "Везде",
        ShortcutContext.Dashboard => "Библиотека карт",
        ShortcutContext.Editor => "Редактор",
        ShortcutContext.Canvas => "Холст",
        ShortcutContext.Selection => "Выделение",
        ShortcutContext.Brush => "Кисть",
        ShortcutContext.Mask => "Маска",
        ShortcutContext.Land => "Земля",
        ShortcutContext.Text => "Текст",
        ShortcutContext.Stamp => "Размещение ассета",
        ShortcutContext.Notes => "Заметки",
        _ => "Мышь",
    };

    public static string ToolCommand(EditorTool tool) => tool switch
    {
        EditorTool.Select => Commands.Select,
        EditorTool.Line => Commands.Line,
        EditorTool.Shape => Commands.Shape,
        EditorTool.Text => Commands.Text,
        EditorTool.Stamp => Commands.Stamp,
        EditorTool.Brush => Commands.Brush,
        EditorTool.Mask => Commands.Mask,
        EditorTool.Land => Commands.Land,
        EditorTool.Notes => Commands.Notes,
        EditorTool.River => Commands.River,
        EditorTool.Road => Commands.Road,
        _ => "",
    };

    private static bool ContextsOverlap(ShortcutContext first, ShortcutContext second)
    {
        if (first == ShortcutContext.Global || second == ShortcutContext.Global) return true;
        if (first == second) return true;
        if (first == ShortcutContext.Mouse || second == ShortcutContext.Mouse) return false;
        if (first == ShortcutContext.Dashboard || second == ShortcutContext.Dashboard) return false;
        if (first == ShortcutContext.Editor || second == ShortcutContext.Editor) return true;
        if (first is ShortcutContext.Canvas or ShortcutContext.Selection or ShortcutContext.Notes
            || second is ShortcutContext.Canvas or ShortcutContext.Selection or ShortcutContext.Notes)
        {
            return true;
        }

        return false;
    }

    private static ShortcutCommandDefinition D(
        string id,
        string name,
        string group,
        ShortcutContext context,
        string defaultGesture,
        string description,
        bool editable = true) =>
        new(id, name, group, context, defaultGesture, description, editable);
}

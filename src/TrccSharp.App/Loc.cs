using System.ComponentModel;
using System.Globalization;

namespace TrccSharp.App;

/// <summary>
/// Строки интерфейса на двух языках. Разметка берёт их через индексатор:
/// {Binding [key], Source={x:Static app:Loc.Instance}} — и обновляется сама,
/// когда язык меняется.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static string Language { get; private set; } = "ru";

    public static bool English => Language == "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Язык сменился — код обновляет тексты, заданные вручную.</summary>
    public static event EventHandler? Changed;

    public string this[string key] => T(key);

    public static string Normalize(string? language) => language == "en" ? "en" : "ru";

    public static void Set(string? language)
    {
        var next = Normalize(language);
        if (next == Language) return;

        Language = next;

        var culture = CultureInfo.GetCultureInfo(next == "en" ? "en-US" : "ru-RU");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string key) =>
        Table.TryGetValue(key, out var pair) ? (English ? pair.En : pair.Ru) : key;

    public static string Format(string key, params object[] args) =>
        string.Format(T(key), args);

    private static readonly Dictionary<string, (string Ru, string En)> Table = new()
    {
        ["window.title"] = ("TrccSharp — экран СЖО", "TrccSharp — LCD panel"),
        ["tab.settings"] = ("НАСТРОЙКИ", "SETTINGS"),
        ["tab.themes"] = ("ТЕМЫ", "THEMES"),
        ["header.screen"] = ("ЭКРАН СЖО · 320 × 240 · USB HID", "LCD · 320 × 240 · USB HID"),

        ["section.theme"] = ("ТЕМА", "THEME"),
        ["theme.window"] = ("ОКНО", "WINDOW"),
        ["theme.panel"] = ("ПАНЕЛЬ", "PANEL"),
        ["theme.light"] = ("БЕЛАЯ", "LIGHT"),
        ["theme.dark"] = ("ЧЁРНАЯ", "DARK"),
        ["section.view"] = ("ВИД", "VIEW"),
        ["view.full"] = ("ОБЫЧНЫЙ", "FULL"),
        ["view.minimal"] = ("МИНИМАЛ", "MINIMAL"),

        ["section.media"] = ("МЕДИА", "MEDIA"),
        ["media.pick"] = ("ВЫБРАТЬ ФАЙЛ…", "CHOOSE FILE…"),
        ["media.none"] = ("медиа не выбрано", "no media selected"),

        ["section.background"] = ("ФОН", "BACKGROUND"),
        ["media.pickBg"] = ("ВЫБРАТЬ ФОН…", "CHOOSE IMAGE…"),
        ["media.reset"] = ("СБРОС", "RESET"),
        ["media.reset.tip"] = ("Вернуть фон по умолчанию", "Back to the default background"),
        ["media.defaultBg"] = ("фон по умолчанию", "default background"),
        ["backdrop.dialog"] = ("Картинка для фона", "Background image"),
        ["backdrop.bad"] = ("файл не открылся", "the file could not be opened"),

        ["section.fps"] = ("FPS", "FPS"),
        ["fps.value"] = ("{0} кадр/с", "{0} fps"),
        ["fps.byfile"] = ("{0} кадр/с — по файлу", "{0} fps — from the file"),
        ["fps.static"] = ("статичный кадр — перерисовки нет", "still frame — no redraws"),

        ["section.light"] = ("ЯРКОСТЬ", "BRIGHT"),

        ["section.mythemes"] = ("МОИ ТЕМЫ", "MY THEMES"),
        ["themes.hint"] = ("Сохрани набор: тема, файл, яркость, частота и вариант панели.",
            "Save a set: theme, file, brightness, rate and panel variant."),
        ["themes.name"] = ("Название темы", "Theme name"),
        ["themes.save"] = ("СОХРАНИТЬ", "SAVE"),
        ["themes.apply"] = ("ПРИМЕНИТЬ", "APPLY"),
        ["themes.delete"] = ("УДАЛИТЬ", "DELETE"),
        ["themes.empty"] = ("пока пусто — сохрани текущую настройку", "empty — save the current setup"),
        ["themes.dbl"] = ("двойной щелчок — тоже применить", "double click also applies"),

        ["run.start"] = ("ЗАПУСТИТЬ ВЫВОД", "START OUTPUT"),
        ["run.stop"] = ("ОСТАНОВИТЬ", "STOP"),
        ["status.off"] = ("Не подключено", "Not connected"),
        ["status.connecting"] = ("Подключаюсь к панели…", "Connecting to the panel…"),
        ["status.failed"] = ("Не удалось подключиться", "Connection failed"),
        ["status.stopped"] = ("Остановлено", "Stopped"),
        ["status.connected"] = ("Подключено: {0}×{1}, PM={2} FBL={3}", "Connected: {0}×{1}, PM={2} FBL={3}"),

        ["preview.hint"] = ("ПРЕВЬЮ · КАДР 1:1 С ПАНЕЛЬЮ", "PREVIEW · 1:1 WITH THE PANEL"),
        ["info.stopped"] = ("вывод остановлен", "output stopped"),
        ["info.rate"] = ("частота: {0:0.0} из {1} кадр/с", "rate: {0:0.0} of {1} fps"),
        ["info.static"] = ("частота: {0:0.0} кадр/с · кадр статичный", "rate: {0:0.0} fps · still frame"),
        ["info.nogpu"] = ("нет данных", "no data"),

        ["tile.monitor"] = ("Монитор", "Monitor"),
        ["tile.clock"] = ("Часы", "Clock"),
        ["tile.media"] = ("Медиа", "Media"),
        ["tile.minimal"] = ("Минимал", "Minimal"),
        ["tile.test"] = ("Тест", "Test"),

        ["log.presetSaved"] = ("тема «{0}» сохранена", "theme “{0}” saved"),
        ["log.presetApplied"] = ("тема «{0}» применена", "theme “{0}” applied"),
        ["log.presetDeleted"] = ("тема «{0}» удалена", "theme “{0}” deleted"),
        ["log.pickTheme"] = ("выбери тему в списке", "pick a theme in the list"),
        ["log.presetMissing"] = ("файл темы не найден: {0}", "theme file not found: {0}"),
        ["log.sendError"] = ("ошибка отправки: {0}", "send error: {0}"),
        ["log.video"] = ("видео: {0}", "video: {0}"),
        ["log.noFile"] = ("файл не найден: {0}", "file not found: {0}"),
        ["log.format"] = ("формат не поддерживается: {0}", "format not supported: {0}"),
        ["log.openFail"] = ("не удалось открыть «{0}»: {1}", "could not open “{0}”: {1}"),

        ["media.still"] = ("фото", "still"),
        ["media.gif"] = ("GIF", "GIF"),
        ["media.video"] = ("видео", "video"),

        ["media.dialog"] = ("Гифка, фото или видео для экрана СЖО", "GIF, photo or video for the LCD"),

        ["compact.hide"] = ("Свернуть подписи", "Collapse labels"),
        ["compact.show"] = ("Показать подписи", "Show labels"),

        ["section.startup"] = ("ЗАПУСК", "STARTUP"),
        ["startup.auto"] = ("АВТОЗАПУСК", "AUTOSTART"),
        ["startup.off"] = ("ВЫКЛ", "OFF"),
        ["startup.on"] = ("ВКЛ", "ON"),
        ["startup.tip"] = ("Запускать экран СЖО при входе в Windows",
            "Start the LCD automatically at sign-in"),
        ["log.autostart"] = ("автозапуск: {0}", "autostart: {0}"),

        ["recent.title"] = ("НЕДАВНИЕ", "RECENT"),
        ["recent.tip"] = ("Нажми, чтобы применить снова", "Click to apply again"),

        ["tray.title"] = ("TrccSharp — экран СЖО", "TrccSharp — LCD panel"),
        ["tray.open"] = ("Открыть окно", "Open window"),
        ["tray.exit"] = ("Выход", "Exit"),
        ["tray.hint.title"] = ("TrccSharp продолжает работать", "TrccSharp keeps running"),
        ["tray.hint"] = ("Картинка на экране СЖО остаётся. Выход — в меню иконки.",
            "The LCD keeps its picture. To quit, use the tray menu."),

        ["info.cpu"] = ("CPU {0:0}%", "CPU {0:0}%"),
        ["info.ram"] = ("RAM {0:0.0}/{1:0.0} ГБ", "RAM {0:0.0}/{1:0.0} GB"),
        ["info.gpu"] = ("GPU {0}", "GPU {0}")
    };
}

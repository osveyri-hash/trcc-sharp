using System.Diagnostics;
using System.Text;
using TrccSharp.Core.Hid;
using TrccSharp.Core.Imaging;
using TrccSharp.Core.Protocol;
using TrccSharp.Core.WinUsb;

namespace TrccSharp.Probe;

/// <summary>
/// Диагностическая утилита: находит экран СЖО, проверяет handshake и,
/// если попросить, гоняет на панель тестовую картинку.
/// </summary>
internal static class Program
{
    private const ushort ThermalrightVid = 0x0416;

    private static readonly Dictionary<(int Vid, int Pid), string> KnownDevices = new()
    {
        [(0x0416, 0x5302)] = "HID LCD Type 2 (Nuvoton) — поддерживается",
        [(0x0416, 0x5406)] = "HID LCD Type 3 (F5, ALi) — не реализован",
        [(0x0418, 0x5303)] = "HID LCD Type 3 (TARAN) — не реализован",
        [(0x0418, 0x5304)] = "HID LCD Type 3 (TARAN) — не реализован",
        [(0x0402, 0x3922)] = "SCSI LCD (Warframe старших ревизий) — не реализован",
        [(0x87AD, 0x70DB)] = "USB bulk LCD (GrandVision/Mjolnir) — не реализован",
        [(0x0416, 0x8001)] = "HID LED + сегментный индикатор — не реализован",
        [(0x0416, 0x5408)] = "LY bulk LCD (Trofeo 9.16) — не реализован",
    };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        var options = ParseOptions(args.Skip(1).ToArray());

        try
        {
            return command switch
            {
                "list" => List(),
                "probe" => ProbeCommand(options),
                "pattern" => PatternCommand(options),
                "sweep" => SweepCommand(options),
                "bench" => BenchCommand(options),
                "raw" => RawCommand(options),
                "gpu" => GpuCommand(),
                "help" or "-h" or "--help" => Help(),
                _ => Unknown(command),
            };
        }
        catch (HandshakeException ex)
        {
            Console.Error.WriteLine($"ОШИБКА handshake: {ex.Message}");
            if (ex.RawResponse is { Length: > 0 })
                Console.Error.WriteLine($"  ответ устройства: {Hex(ex.RawResponse)}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ОШИБКА: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int List()
    {
        var usb = UsbEnumerator.Enumerate();
        var hids = HidEnumerator.Enumerate();

        Console.WriteLine("USB-интерфейсы Thermalright:");
        var found = false;
        foreach (var node in usb.Where(n => KnownDevices.ContainsKey((n.VendorId, n.ProductId))))
        {
            found = true;
            Console.WriteLine($"  {node.VidPid} MI_{node.InterfaceNumber:D2}  {KnownDevices[(node.VendorId, node.ProductId)]}");
            Console.WriteLine($"    {node.DevicePath}");
        }

        Console.WriteLine("HID-интерфейсы Thermalright:");
        foreach (var hid in hids.Where(d => d.VendorId == ThermalrightVid))
        {
            found = true;
            Console.WriteLine($"  {hid}");
            Console.WriteLine($"    {hid.DevicePath}");
        }

        if (!found)
        {
            Console.WriteLine("Ничего не найдено. Проверь USB-кабель от помпы и питание СЖО.");
            return 1;
        }

        var target = hids.FirstOrDefault(d => d.VendorId == ThermalrightVid && d.ProductId == 0x5302);
        Console.WriteLine();
        Console.WriteLine(target is null
            ? "Экран 0416:5302 не найден — probe работать не сможет."
            : $"Цель для probe: {target.VendorId:X4}:{target.ProductId:X4}, out-report {target.OutputReportLength} байт");
        return 0;
    }

    private static int ProbeCommand(Dictionary<string, string?> options)
    {
        using var link = OpenTarget(options);
        PrintDeviceInfo(link);

        var lcd = new HidLcdType2(link);
        var result = lcd.Handshake();
        PrintHandshake(result);
        return 0;
    }

    private static int PatternCommand(Dictionary<string, string?> options)
    {
        var noInit = options.ContainsKey("no-init");
        var bootWait = options.TryGetValue("boot-wait", out var bw) && int.TryParse(bw, out var b) ? b : 3;
        var waitAck = options.ContainsKey("ack");
        var seconds = options.TryGetValue("seconds", out var sec) && int.TryParse(sec, out var s) ? s : 15;

        PanelProfile profile;
        ILcdLink link;

        if (noInit)
        {
            // Сначала обычный handshake — только чтобы узнать PM/SUB и профиль.
            using (var probeLink = OpenTarget(options))
            {
                profile = new HidLcdType2(probeLink).Handshake().Profile;
            }

            // Затем переоткрываем канал и НЕ отправляем init: прошивка от него
            // перезагружается, и кадры, ушедшие в этот момент, теряются.
            link = OpenTarget(options);
            PrintDeviceInfo(link);
            Console.WriteLine($"init-пакет не отправляется, пауза {bootWait} с на загрузку панели");
            Thread.Sleep(bootWait * 1000);

            var spontaneous = link.TryRead(300);
            Console.WriteLine(spontaneous is { Length: > 0 }
                ? $"панель сама прислала: {Convert.ToHexString(spontaneous)}"
                : "панель молчит (это нормально для streaming-прошивки)");
        }
        else
        {
            link = OpenTarget(options);
            PrintDeviceInfo(link);
            var handshake = new HidLcdType2(link).Handshake();
            PrintHandshake(handshake);
            profile = handshake.Profile;
            if (bootWait > 0) Thread.Sleep(bootWait * 1000);
        }

        using var lease = link;
        var lcd = new HidLcdType2(link);

        var rotate = options.TryGetValue("rotate", out var rot) && int.TryParse(rot, out var r)
            ? r
            : noInit ? 0 : profile.WireAngle;
        var mirror = options.ContainsKey("mirror");
        var flip = options.TryGetValue("flip", out var f) && f is "v" or "vertical";
        var bigEndian = options.TryGetValue("endian", out var endian)
            ? endian is "be" or "BE"
            : profile.BigEndian;

        Console.WriteLine();
        Console.WriteLine($"Кадр: композиция {profile.Width}x{profile.Height} → поворот {rotate}°" +
                          $"{(mirror ? " + зеркало X" : "")}{(flip ? " + отражение Y" : "")}, " +
                          $"RGB565 {(bigEndian ? "BE" : "LE")}, {seconds} с");

        var surface = options.ContainsKey("edges")
            ? TestPattern.BuildEdgeBars(profile.Width, profile.Height)
            : TestPattern.Build(profile.Width, profile.Height);
        var rotated = surface.Rotate(rotate);
        if (mirror) rotated = rotated.FlipHorizontal();
        if (flip) rotated = rotated.FlipVertical();

        var payload = rotated.ToRgb565(bigEndian);
        Console.WriteLine($"Wire-растр {rotated.Width}x{rotated.Height}, payload {payload.Length} байт");
        Console.WriteLine("На панели ожидается: красный угол — левый верхний.");

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var sent = 0;
        while (DateTime.UtcNow < deadline)
        {
            lcd.SendFrame(payload);
            sent++;
            Console.Write($"\rкадров отправлено: {sent}");

            if (waitAck)
            {
                var ack = link.TryRead(300);
                if (ack is { Length: > 0 })
                    Console.Write($"\r  ACK {Convert.ToHexString(ack)}                          ");
            }

            Thread.Sleep(400);
        }

        Console.WriteLine();
        Console.WriteLine($"Готово, кадров: {sent}.");
        return 0;
    }

    /// <summary>
    /// Перебор вариантов раскладки кадра: пользователь смотрит на панель и
    /// запоминает номер, на котором картинка стала правильной.
    /// </summary>
    private static int SweepCommand(Dictionary<string, string?> options)
    {
        var perVariant = options.TryGetValue("seconds", out var sec) && int.TryParse(sec, out var s) ? s : 7;

        var variants = new (string Name, int Rotate, bool BigEndian)[]
        {
            ("поворот 90°, little-endian", 90, false),
            ("поворот 270°, little-endian", 270, false),
            ("поворот 0°, big-endian", 0, true),
            ("поворот 90°, big-endian", 90, true),
            ("поворот 270°, big-endian", 270, true),
            ("поворот 0°, little-endian", 0, false),
        };

        var surface = TestPattern.Build(320, 240);
        Console.WriteLine($"Перебор {variants.Length} вариантов по {perVariant} с. Следи за панелью и запомни номер.");
        Console.WriteLine("Правильный вариант: 4 цветных угла, чёрный крест, серая шкала внизу.");

        for (var i = 0; i < variants.Length; i++)
        {
            var (name, rotate, bigEndian) = variants[i];
            Console.WriteLine();
            Console.WriteLine($"=== ВАРИАНТ {i + 1}/{variants.Length}: {name} ===");

            using var link = OpenTarget(options);
            Thread.Sleep(2000);

            var rotated = surface.Rotate(rotate);
            var payload = rotated.ToRgb565(bigEndian);
            var lcd = new HidLcdType2(link);

            var deadline = DateTime.UtcNow.AddSeconds(perVariant);
            var sent = 0;
            while (DateTime.UtcNow < deadline)
            {
                lcd.SendFrame(payload);
                sent++;
                Thread.Sleep(300);
            }

            Console.WriteLine($"  отправлено {sent} кадров ({rotated.Width}x{rotated.Height}), пауза");
            Thread.Sleep(1500);
        }

        Console.WriteLine();
        Console.WriteLine("Перебор закончен. Напиши номер варианта, на котором картинка была правильной.");
        return 0;
    }

    /// <summary>
    /// Замер реальной скорости отправки: гоняем кадры без паузы и считаем,
    /// сколько их успевает уйти по USB. По этому числу видно, тянет ли панель
    /// частоту медиа и не деградировал ли транспорт.
    /// </summary>
    private static int BenchCommand(Dictionary<string, string?> options)
    {
        var seconds = options.TryGetValue("seconds", out var sec) && double.TryParse(sec, out var s) ? s : 5;

        using var device = PanelDevice.ConnectDefault();
        device.Connect();

        Console.WriteLine($"Панель: {device.Profile.Width}x{device.Profile.Height}, " +
                          $"PM={device.Info.Pm} FBL={device.Info.Fbl}");

        var surface = TestPattern.Build(device.Profile.Width, device.Profile.Height);

        // Первый кадр — прогрев: после handshake панель ещё поднимается.
        device.Send(surface);

        var stopwatch = Stopwatch.StartNew();
        var frames = 0;

        while (stopwatch.Elapsed.TotalSeconds < seconds)
        {
            device.Send(surface);
            frames++;
        }

        var elapsed = stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine($"Отправлено {frames} кадров за {elapsed:0.00} с — {frames / elapsed:0.0} кадр/с");
        Console.WriteLine($"Полезная нагрузка: {frames * 153_600 / elapsed / 1024:0} КБ/с");
        return 0;
    }

    private static int GpuCommand()
    {
        using var adl = new Core.Sensors.AdlGpu();

        if (!adl.TryInitialize())
        {
            Console.WriteLine($"ADL недоступен: {adl.LastError}");
            return 1;
        }

        Console.WriteLine("ADL инициализирован, активный адаптер найден.");
        Console.WriteLine();
        Console.WriteLine("Что отвечает на этом драйвере:");
        foreach (var line in adl.Diagnose())
            Console.WriteLine($"  {line}");

        Console.WriteLine();
        foreach (var (name, type) in new[]
                 {
                     ("edge", Core.Sensors.AdlGpu.TempEdge),
                     ("junction/hotspot", Core.Sensors.AdlGpu.TempHotspot),
                     ("memory", Core.Sensors.AdlGpu.TempMem),
                     ("vrm", Core.Sensors.AdlGpu.TempVrm)
                 })
        {
            var raw = adl.ReadRawTemperature(type);
            Console.WriteLine(raw is null
                ? $"  {name,-17} нет данных"
                : $"  {name,-17} сырое {raw}, нормализованное {adl.ReadTemperature(type):0.0} °C");
        }

        return 0;
    }

    /// <summary>
    /// Проверка транспорта без handshake: пробуем записи разной длины и чтение,
    /// чтобы понять, принимает ли панель отчёты вообще.
    /// </summary>
    private static int RawCommand(Dictionary<string, string?> options)
    {
        var timeout = options.TryGetValue("timeout", out var t) && int.TryParse(t, out var ms) ? ms : 2000;

        using var link = OpenTarget(options);
        PrintDeviceInfo(link);

        foreach (var size in new[] { 1, 8, 64, 512, 513 })
        {
            var payload = new byte[Math.Min(size, 512)];
            var stopwatch = Stopwatch.StartNew();
            try
            {
                link.WriteChunk(payload);
                Console.WriteLine($"  запись {size,4} байт: ок за {stopwatch.ElapsedMilliseconds} мс");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  запись {size,4} байт: {ex.GetType().Name} за {stopwatch.ElapsedMilliseconds} мс — {ex.Message}");
            }

            Thread.Sleep(200);
        }

        var reply = link.TryRead(timeout);
        Console.WriteLine(reply is { Length: > 0 }
            ? $"  чтение: {Hex(reply)}"
            : $"  чтение: тишина за {timeout} мс");

        return 0;
    }

    private static ILcdLink OpenTarget(Dictionary<string, string?> options)
    {
        var transport = options.TryGetValue("transport", out var t) && t is not null
            ? t.ToLowerInvariant()
            : "auto";

        if (transport == "hid")
            return OpenHid();

        if (transport is "winusb" or "usb")
        {
            var link = WinUsbLink.Open();
            Console.WriteLine("Транспорт: vendor WinUSB (MI_01)");
            return link;
        }

        // auto: сначала vendor WinUSB — именно так кадры шлёт оригинальный TRCC
        try
        {
            var link = WinUsbLink.Open();
            Console.WriteLine("Транспорт: vendor WinUSB (MI_01)");
            return link;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WinUSB недоступен: {ex.Message}");
            Console.WriteLine("Переключаюсь на HID-интерфейс.");
            return OpenHid();
        }
    }

    private static ILcdLink OpenHid()
    {
        var link = HidChannel.Open();
        Console.WriteLine("Транспорт: HID (MI_00)");
        return link;
    }

    private static void PrintDeviceInfo(ILcdLink link)
    {
        Console.WriteLine(link.Description);
        if (link is HidChannel hid)
            Console.WriteLine($"HID отчёты: out {hid.OutputReportLength} байт, in {hid.InputReportLength} байт");
    }

    private static void PrintHandshake(HandshakeResult result)
    {
        Console.WriteLine();
        Console.WriteLine("Handshake OK" + (result.ReportIdStripped > 0 ? $" (срезан Report ID: {result.ReportIdStripped} байт)" : ""));
        Console.Write(result.Describe());
        Console.WriteLine($"  сырой ответ: {Hex(result.RawResponse)}");
    }

    private static string Hex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        foreach (var b in data) sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;

            var key = arg[2..];
            var eq = key.IndexOf('=');
            if (eq > 0)
            {
                result[key[..eq]] = key[(eq + 1)..];
                continue;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                result[key] = args[++i];
            else
                result[key] = null;
        }

        return result;
    }

    private static int Help()
    {
        Console.WriteLine("""
            trcc-probe — диагностика экрана Thermalright

              list                     перечислить найденные USB/HID-интерфейсы
              probe                    открыть панель и выполнить handshake
              raw                      проверить, принимает ли панель отчёты вообще
              pattern                  handshake + отправить тестовую картинку
              bench                    замер реальной скорости отправки кадров

            Опции для probe/pattern:
              --transport hid|winusb   канал до панели (по умолчанию auto)
              --boot-wait N            пауза после handshake, сек (по умолчанию 3)
              --ack                    печатать ответы панели на кадры
              --rotate 0|90|180|270    поворот кадра на провод (по умолчанию из профиля)
              --mirror                 зеркало по X после поворота
              --flip v                 отражение по Y после поворота
              --endian le|be           порядок байт RGB565 (по умолчанию из профиля)
              --seconds N              сколько секунд гонять картинку (по умолчанию 15)
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда: {command}");
        Help();
        return 1;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;

namespace BassRelay.Services;

public static class Localization
{
    private static readonly CultureInfo SystemCulture = ReadWindowsLanguage();
    private static string _language = Resolve(SystemCulture.Name);
    public static string CurrentLanguage => _language;
    public static event Action? Changed;

    // The same semantic keys are available to code and WPF DynamicResource bindings.
    // Entries are Russian, English, Brazilian Portuguese and Spanish, in that order.
    private static readonly Dictionary<string, string[]> Strings = new(StringComparer.Ordinal)
    {
        ["Introduction"] = ["Бас из игр, музыки и фильмов — на ваши шейкеры. Звук с устройства вывода Windows по умолчанию.", "Bass from games, music and movies to your shakers. Uses the default Windows audio output.", "Graves de jogos, músicas e filmes nos seus bass shakers. Usa a saída de áudio padrão do Windows.", "Graves de juegos, música y películas para tus bass shakers. Usa la salida de audio predeterminada de Windows."],
        ["PriorityMainHelp"] = ["Пока игра запущена и SimHub получает от неё данные, передача на шейкеры автоматически приостанавливается. Это можно отключить в настройках.", "While a game is running and SimHub is receiving its data, audio to the shakers pauses automatically. You can turn this off in Settings.", "Enquanto um jogo estiver em execução e o SimHub receber seus dados, o áudio para os shakers será pausado automaticamente. Você pode desativar isso nas configurações.", "Mientras un juego esté en marcha y SimHub reciba sus datos, el audio a los shakers se pausa automáticamente. Puedes desactivar esta opción en Ajustes."],
        ["SimHubFeature"] = ["Когда SimHub получает данные запущенной игры, Bass Relay автоматически уступает ему шейкеры (можно отключить в настройках)", "When SimHub receives data from a running game, Bass Relay automatically hands over the shakers to it (can be disabled in Settings)", "Quando o SimHub recebe dados de um jogo em execução, o Bass Relay libera automaticamente os shakers para ele (pode ser desativado nas configurações)", "Cuando SimHub recibe datos de un juego en marcha, Bass Relay le cede automáticamente los shakers (se puede desactivar en Ajustes)"],
        ["Settings"] = ["Настройки", "Settings", "Configurações", "Ajustes"],
        ["CloseToTray"] = ["Закрывать в трей", "Close to system tray", "Minimizar para a bandeja ao fechar", "Minimizar a la bandeja al cerrar"],
        ["StartWithWindows"] = ["Запускать с Windows", "Start with Windows", "Iniciar com o Windows", "Iniciar con Windows"],
        ["PrioritizeExternalAudio"] = ["Автопауза с SimHub", "Auto-pause with SimHub", "Pausa automática com o SimHub", "Pausa automática con SimHub"],
        ["PriorityHelp"] = ["Приостанавливать передачу на все шейкеры, пока игра запущена и SimHub получает от неё данные, даже между эффектами. Включено по умолчанию.", "Pause audio to all shakers while a game is running and SimHub is receiving its data, including gaps between effects. On by default.", "Pausar o áudio para todos os shakers enquanto um jogo estiver em execução e o SimHub receber seus dados, mesmo entre os efeitos. Ativado por padrão.", "Pausar el audio a todos los shakers mientras un juego esté en marcha y SimHub reciba sus datos, incluso entre efectos. Activado de forma predeterminada."],
        ["Language"] = ["Язык", "Language", "Idioma", "Idioma"],
        ["SystemLanguage"] = ["Как в системе", "System default", "Padrão do sistema", "Idioma del sistema"],
        ["RefreshDevices"] = ["Обновить устройства", "Refresh devices", "Atualizar dispositivos", "Actualizar dispositivos"],
        ["Exit"] = ["Выйти", "Exit", "Sair", "Salir"],
        ["Pause"] = ["Пауза", "Pause", "Pausar", "Pausar"],
        ["Resume"] = ["Продолжить", "Resume", "Retomar", "Reanudar"],
        ["PauseHelp"] = ["Остановить подачу Bass Relay на все шейкеры до нажатия «Продолжить».", "Pause Bass Relay on all shakers until you choose Resume.", "Pausar o Bass Relay em todos os shakers até você escolher Retomar.", "Pausar Bass Relay en todos los shakers hasta que elijas Reanudar."],
        ["ManuallyPaused"] = ["Bass Relay на ручной паузе. Нажмите «Продолжить» в Bass Relay.", "Bass Relay is manually paused. Choose Resume in Bass Relay.", "O Bass Relay foi pausado manualmente. Escolha Retomar no Bass Relay.", "Bass Relay está en pausa manual. Elige Reanudar en Bass Relay."],
        ["AddShaker"] = ["＋ Добавить шейкер", "＋ Add shaker", "＋ Adicionar shaker", "＋ Añadir shaker"],
        ["SelectShakerDevice"] = ["Выберите звуковую карту шейкера {0}", "Select the sound card for shaker {0}", "Selecione a placa de som do shaker {0}", "Selecciona la tarjeta de sonido del shaker {0}"],
        ["ShakerDevice"] = ["Звуковая карта шейкера", "Shaker sound card", "Placa de som do shaker", "Tarjeta de sonido del shaker"],
        ["NoneSelected"] = ["Не выбрано", "Not selected", "Não selecionado", "Sin seleccionar"],
        ["SoundCard"] = ["Звуковая карта", "Sound card", "Placa de som", "Tarjeta de sonido"],
        ["UnavailableDevice"] = ["{0} (недоступна)", "{0} (unavailable)", "{0} (indisponível)", "{0} (no disponible)"],
        ["VibrationStrength"] = ["Сила вибрации", "Vibration strength", "Intensidade", "Intensidad"],
        ["VibrationPercent"] = ["Сила вибрации, процентов", "Vibration strength, percent", "Intensidade da vibração, porcentagem", "Intensidad de vibración, porcentaje"],
        ["LowCut"] = ["Не ниже, Гц", "Min, Hz", "Mín., Hz", "Mín., Hz"],
        ["HighCut"] = ["Не выше, Гц", "Max, Hz", "Máx., Hz", "Máx., Hz"],
        ["FrequencyHelp"] = ["Начните с 20–80 Гц. Подберите частоты по модели шейкера и ощущениям. Допустимо 0–200 Гц. Нижняя граница 0 — без обрезки снизу.", "Start with 20–80 Hz. Adjust to your shaker model and preference. Allowed range: 0–200 Hz. A lower limit of 0 disables the low-frequency cut.", "Comece com 20–80 Hz. Ajuste conforme o modelo do shaker e sua preferência. Valores permitidos: 0–200 Hz. O limite inferior 0 desativa o corte das frequências baixas.", "Empieza con 20–80 Hz. Ajusta según el modelo del shaker y tus preferencias. Valores permitidos: 0–200 Hz. El límite inferior 0 desactiva el corte de frecuencias bajas."],
        ["ShakerUnavailable"] = ["Шейкер недоступен", "Shaker unavailable", "Shaker indisponível", "Shaker no disponible"],
        ["SameDeviceHelp"] = ["Эту карту сейчас использует Windows. Выберите другой основной выход Windows — шейкер включится сам.", "Windows is currently using this card as its default output. Select another default output in Windows and the shaker will resume automatically.", "O Windows está usando esta placa como saída padrão. Selecione outra saída padrão no Windows e o shaker será reativado automaticamente.", "Windows está usando esta tarjeta como salida predeterminada. Elige otra salida predeterminada en Windows y el shaker se reactivará automáticamente."],
        ["FrequencyValidation"] = ["Введите числа от 0 до 200. Первое должно быть меньше второго. Esc — отменить изменения.", "Enter numbers from 0 to 200. The first must be lower than the second. Esc cancels changes.", "Digite números de 0 a 200. O primeiro deve ser menor que o segundo. Esc cancela as alterações.", "Introduce números de 0 a 200. El primero debe ser menor que el segundo. Esc cancela los cambios."],
        ["OpenApp"] = ["Открыть Bass Relay", "Open Bass Relay", "Abrir Bass Relay", "Abrir Bass Relay"],
        ["TrayTitle"] = ["Bass Relay — бас на шейкеры", "Bass Relay — bass to your shakers", "Bass Relay — graves nos seus shakers", "Bass Relay — graves para tus shakers"],
        ["TrayHint"] = ["Работа продолжается в трее. Двойной щелчок по значку откроет окно; «Выйти» завершит приложение.", "Bass Relay is still running in the system tray. Double-click its icon to open the window, or choose Exit to close the app.", "O Bass Relay continua na bandeja do sistema. Clique duas vezes no ícone para abrir a janela ou escolha Sair para encerrar.", "Bass Relay sigue funcionando en la bandeja del sistema. Haz doble clic en el icono para abrir la ventana o elige Salir para cerrar la aplicación."],
        ["UiError"] = ["Ошибка Bass Relay: {0}", "Bass Relay error: {0}", "Erro do Bass Relay: {0}", "Error de Bass Relay: {0}"],
        ["StartupError"] = ["Bass Relay — ошибка запуска", "Bass Relay — startup error", "Bass Relay — erro ao iniciar", "Bass Relay — error al iniciar"],
        ["SaveSettingsError"] = ["Не удалось сохранить настройки: {0}", "Could not save settings: {0}", "Não foi possível salvar as configurações: {0}", "No se pudieron guardar los ajustes: {0}"],
        ["LoadSettingsWarning"] = ["Не удалось прочитать настройки. Используются значения по умолчанию.", "Could not read settings. Using default settings.", "Não foi possível ler as configurações. Serão usados os valores padrão.", "No se pudieron leer los ajustes. Se usarán los valores predeterminados."],
        ["MissingAppIcon"] = ["Не найдена иконка приложения.", "Application icon not found.", "Ícone do aplicativo não encontrado.", "No se encontró el icono de la aplicación."],
        ["InvalidAppPath"] = ["Некорректный путь приложения.", "Invalid application path.", "Caminho do aplicativo inválido.", "La ruta de la aplicación no es válida."],
        ["StartupSettingsUnavailable"] = ["Не удалось открыть настройки автозапуска Windows.", "Could not open Windows startup settings.", "Não foi possível abrir as configurações de inicialização do Windows.", "No se pudieron abrir los ajustes de inicio de Windows."],
        ["AppPathUnknown"] = ["Не определён путь приложения.", "Could not determine the application path.", "Não foi possível determinar o caminho do aplicativo.", "No se pudo determinar la ruta de la aplicación."],
        ["DefaultDeviceBlocked"] = ["Это устройство сейчас выбрано основным выходом Windows. Дублирование отключено, чтобы не создавать звуковую петлю. Выберите другой основной выход в Windows — шейкер включится автоматически.", "This device is the default Windows output. Duplication is disabled to prevent an audio loop. Select another default Windows output and the shaker will resume automatically.", "Este dispositivo é a saída padrão do Windows. A duplicação está desativada para evitar um ciclo de áudio. Selecione outra saída padrão no Windows e o shaker será reativado automaticamente.", "Este dispositivo es la salida predeterminada de Windows. La duplicación está desactivada para evitar un bucle de audio. Elige otra salida predeterminada en Windows y el shaker se reactivará automáticamente."],
        ["DuplicateDeviceBlocked"] = ["Это устройство уже используется другим шейкером. Выберите отдельную звуковую карту или удалите дубликат.", "Another shaker already uses this device. Select a different sound card or remove the duplicate.", "Outro shaker já usa este dispositivo. Selecione outra placa de som ou remova a duplicata.", "Otro shaker ya usa este dispositivo. Selecciona otra tarjeta de sonido o elimina el duplicado."],
        ["FindingDefaultDevice"] = ["Поиск основного устройства Windows…", "Finding the default Windows output…", "Buscando a saída padrão do Windows…", "Buscando la salida predeterminada de Windows…"],
        ["AudioOpenError"] = ["Не удалось открыть аудиоустройства. Повторная попытка автоматически. {0}", "Could not open audio devices. Will retry automatically. {0}", "Não foi possível abrir os dispositivos de áudio. Uma nova tentativa será feita automaticamente. {0}", "No se pudieron abrir los dispositivos de audio. Se reintentará automáticamente. {0}"],
        ["AudioServiceUnavailable"] = ["Аудиослужба Windows недоступна", "Windows audio service is unavailable", "O serviço de áudio do Windows está indisponível", "El servicio de audio de Windows no está disponible"],
        ["DefaultDeviceMissing"] = ["Основное устройство вывода не найдено", "No default output device found", "Nenhuma saída de áudio padrão encontrada", "No se encontró una salida de audio predeterminada"],
        ["CaptureStopped"] = ["Захват системного звука остановлен.", "System audio capture stopped.", "A captura do áudio do sistema parou.", "Se detuvo la captura del audio del sistema."],
        ["ShakerDisabled"] = ["Выключен", "Disabled", "Desativado", "Desactivado"],
        ["SelectDevice"] = ["Выберите звуковую карту шейкера", "Select a shaker sound card", "Selecione a placa de som do shaker", "Selecciona la tarjeta de sonido del shaker"],
        ["DeviceDisconnected"] = ["Устройство отключено. Подключите его — работа возобновится автоматически.", "Device disconnected. Reconnect it to resume automatically.", "Dispositivo desconectado. Reconecte-o para retomar automaticamente.", "Dispositivo desconectado. Vuelve a conectarlo para reanudar automáticamente."],
        ["WaitingDefaultDevice"] = ["Ожидание основного устройства Windows", "Waiting for the default Windows output", "Aguardando a saída padrão do Windows", "Esperando la salida predeterminada de Windows"],
        ["InvalidFrequencyBand"] = ["Введите частоты от 0 до 200 Гц. Первая должна быть меньше второй.", "Enter frequencies from 0 to 200 Hz. The first must be lower than the second.", "Digite frequências de 0 a 200 Hz. A primeira deve ser menor que a segunda.", "Introduce frecuencias de 0 a 200 Hz. La primera debe ser menor que la segunda."],
        ["PlaybackStopped"] = ["Воспроизведение остановлено.", "Playback stopped.", "A reprodução parou.", "Se detuvo la reproducción."],
        ["CaptureError"] = ["Не удалось захватить системный звук. {0}", "Could not capture system audio. {0}", "Não foi possível capturar o áudio do sistema. {0}", "No se pudo capturar el audio del sistema. {0}"],
        ["CaptureUnavailable"] = ["Захват звука временно недоступен.", "Audio capture is temporarily unavailable.", "A captura de áudio está temporariamente indisponível.", "La captura de audio no está disponible temporalmente."],
        ["RetryAutomatically"] = ["{0} Повторная попытка автоматически.", "{0} Will retry automatically.", "{0} Uma nova tentativa será feita automaticamente.", "{0} Se reintentará automáticamente."],
        ["ShakerRunning"] = ["Работает · {0}–{1} Гц", "Running · {0}–{1} Hz", "Ativo · {0}–{1} Hz", "Activo · {0}–{1} Hz"],
        ["OutputOpenError"] = ["Не удалось включить устройство. {0}", "Could not start the device. {0}", "Não foi possível iniciar o dispositivo. {0}", "No se pudo iniciar el dispositivo. {0}"],
        ["ErrorCode"] = ["Код 0x{0}.", "Code 0x{0}.", "Código 0x{0}.", "Código 0x{0}."],
        ["AudioProcessingError"] = ["Ошибка обработки системного звука. {0}", "System audio processing error. {0}", "Erro ao processar o áudio do sistema. {0}", "Error al procesar el audio del sistema. {0}"],
        ["OutputStopped"] = ["Устройство вывода остановлено.", "Output device stopped.", "O dispositivo de saída parou.", "Se detuvo el dispositivo de salida."],
        ["SimHubGameRunning"] = ["Игра запущена, и SimHub получает от неё данные. Bass Relay приостановил передачу на все шейкеры. Автопаузу можно отключить в настройках.", "A game is running and SimHub is receiving its data. Bass Relay has paused audio to all shakers. You can turn off auto-pause in Settings.", "Um jogo está em execução e o SimHub está recebendo seus dados. O Bass Relay pausou o áudio para todos os shakers. Você pode desativar a pausa automática nas configurações.", "Hay un juego en marcha y SimHub está recibiendo sus datos. Bass Relay ha pausado el audio a todos los shakers. Puedes desactivar la pausa automática en Ajustes."],
        ["ConnectingDevice"] = ["Подключение…", "Connecting…", "Conectando…", "Conectando…"],
        ["OtherInstanceOwnsAudio"] = ["Звук уже обрабатывает другая копия Bass Relay. Закройте её, чтобы использовать эту копию.", "Another copy of Bass Relay is already processing audio. Close it to use this copy.", "Outra instância do Bass Relay já está processando o áudio. Feche-a para usar esta instância.", "Otra instancia de Bass Relay ya está procesando el audio. Ciérrala para usar esta instancia."],
        ["WaitingSimHubState"] = ["Ожидание состояния игры от SimHub. До первого обновления звук приостановлен.", "Waiting for the game status from SimHub. Audio is paused until the first update.", "Aguardando o estado do jogo no SimHub. O áudio fica pausado até a primeira atualização.", "Esperando el estado del juego de SimHub. El audio está en pausa hasta la primera actualización."],
        ["PluginStopped"] = ["Плагин Bass Relay остановлен.", "The Bass Relay plugin is stopped.", "O plugin Bass Relay está parado.", "El plugin Bass Relay está detenido."],
        ["PluginStartError"] = ["Не удалось запустить плагин Bass Relay. {0}", "Could not start the Bass Relay plugin. {0}", "Não foi possível iniciar o plugin Bass Relay. {0}", "No se pudo iniciar el plugin Bass Relay. {0}"]
    };

    public static string Text(string key, params object[] args)
    {
        if (!Strings.TryGetValue(key, out var values)) return key;
        string language = _language;
        int index = language switch { "ru" => 0, "pt-BR" => 2, "es" => 3, _ => 1 };
        return args.Length == 0 ? values[index] : string.Format(CultureInfo.GetCultureInfo(language), values[index], args);
    }

    public static bool IsText(string key, string value) =>
        Strings.TryGetValue(key, out var translations) && Array.Exists(translations, text => text == value);

    public static void Apply(string? language, ResourceDictionary? resources = null)
    {
        _language = Resolve(string.IsNullOrWhiteSpace(language) || language!.Equals("system", StringComparison.OrdinalIgnoreCase)
            ? SystemCulture.Name : language);
        // An embedded page owns its translations; never change the host's resources.
        ResourceDictionary? target = resources ?? Application.Current?.Resources;
        if (target is not null)
            foreach (string key in Strings.Keys) target[key] = Text(key);
        Changed?.Invoke();
    }

    private static string Resolve(string language)
    {
        if (language.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return "ru";
        if (language.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        if (language.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "es";
        return "en";
    }

    private static CultureInfo ReadWindowsLanguage()
    {
        // Use the Windows user's display language without changing thread culture.
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()); }
        catch (CultureNotFoundException) { return CultureInfo.CurrentUICulture; }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();
}

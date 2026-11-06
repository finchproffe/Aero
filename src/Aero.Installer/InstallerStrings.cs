using System.Globalization;

namespace Aero.Installer;

internal sealed record InstallerStrings
{
    public required string WindowTitle { get; init; }
    public required string HeroTitle { get; init; }
    public required string HeroSubtitle { get; init; }
    public required string InstalledLabel { get; init; }
    public required string PackageLabel { get; init; }
    public required string NotInstalled { get; init; }
    public required string Checking { get; init; }
    public required string Ready { get; init; }
    public required string AlreadyCurrent { get; init; }
    public required string NewerInstalled { get; init; }
    public required string Install { get; init; }
    public required string Update { get; init; }
    public required string Close { get; init; }
    public required string Cancel { get; init; }
    public required string Retry { get; init; }
    public required string LaunchAfter { get; init; }
    public required string OpenDefaultsAfter { get; init; }
    public required string InstallSecurityDriverPanel { get; init; }
    public required string InstallComponents { get; init; }
    public required string InstallingSecurityDriverPanel { get; init; }
    public required string DataNotice { get; init; }
    public required string ElevationNotice { get; init; }
    public required string Details { get; init; }
    public required string HideDetails { get; init; }
    public required string SuccessTitle { get; init; }
    public required string SuccessBody { get; init; }
    public required string ErrorTitle { get; init; }
    public required string ElevationCancelled { get; init; }
    public required string Extracting { get; init; }
    public required string ReadingPackage { get; init; }
    public required string CheckingCertificate { get; init; }
    public required string ReadingInstalledVersion { get; init; }
    public required string TrustingCertificate { get; init; }
    public required string VerifyingSignature { get; init; }
    public required string ClosingAero { get; init; }
    public required string InstallingPackage { get; init; }
    public required string VerifyingInstall { get; init; }
    public required string StartingAero { get; init; }
    public required string Complete { get; init; }

    public static InstallerStrings ForCulture(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? Russian
            : English;

    private static InstallerStrings Russian => new()
    {
        WindowTitle = "Установка Aero",
        HeroTitle = "Aero Browser",
        HeroSubtitle = "Быстрая и безопасная установка",
        InstalledLabel = "Установлено",
        PackageLabel = "В установщике",
        NotInstalled = "Не установлен",
        Checking = "Проверяем установочный пакет…",
        Ready = "Всё готово к установке",
        AlreadyCurrent = "У вас уже установлена эта версия Aero",
        NewerInstalled = "Установлена более новая версия Aero",
        Install = "Установить Aero",
        Update = "Обновить Aero",
        Close = "Закрыть",
        Cancel = "Отмена",
        Retry = "Повторить",
        LaunchAfter = "Запустить Aero после установки",
        OpenDefaultsAfter = "Открыть выбор браузера по умолчанию",
        InstallSecurityDriverPanel = "Установить Aero Security Driver Panel (необязательно)",
        InstallComponents = "Установить выбранные компоненты",
        InstallingSecurityDriverPanel = "Устанавливаем Aero Security Driver Panel…",
        DataNotice = "Профили, вкладки, пароли и настройки сохранятся при обновлении.",
        ElevationNotice = "Windows попросит разрешение администратора только после нажатия кнопки установки.",
        Details = "Подробности",
        HideDetails = "Скрыть подробности",
        SuccessTitle = "Aero установлен",
        SuccessBody = "Обновление завершено. Можно пользоваться браузером.",
        ErrorTitle = "Не удалось установить Aero",
        ElevationCancelled = "Запрос администратора был отменён.",
        Extracting = "Распаковываем встроенный пакет…",
        ReadingPackage = "Проверяем версию и manifest…",
        CheckingCertificate = "Сверяем сертификат пакета…",
        ReadingInstalledVersion = "Определяем установленную версию…",
        TrustingCertificate = "Добавляем сертификат Aero в доверенные…",
        VerifyingSignature = "Проверяем цифровую подпись…",
        ClosingAero = "Сохраняем сеанс и закрываем Aero…",
        InstallingPackage = "Устанавливаем обновление…",
        VerifyingInstall = "Проверяем результат установки…",
        StartingAero = "Запускаем Aero…",
        Complete = "Готово"
    };

    private static InstallerStrings English => new()
    {
        WindowTitle = "Install Aero",
        HeroTitle = "Aero Browser",
        HeroSubtitle = "Fast and secure setup",
        InstalledLabel = "Installed",
        PackageLabel = "Installer package",
        NotInstalled = "Not installed",
        Checking = "Checking the installation package…",
        Ready = "Everything is ready to install",
        AlreadyCurrent = "This version of Aero is already installed",
        NewerInstalled = "A newer version of Aero is installed",
        Install = "Install Aero",
        Update = "Update Aero",
        Close = "Close",
        Cancel = "Cancel",
        Retry = "Try again",
        LaunchAfter = "Launch Aero after installation",
        OpenDefaultsAfter = "Open default browser settings",
        InstallSecurityDriverPanel = "Install Aero Security Driver Panel (optional)",
        InstallComponents = "Install selected components",
        InstallingSecurityDriverPanel = "Installing Aero Security Driver Panel…",
        DataNotice = "Profiles, tabs, passwords, and settings are preserved during updates.",
        ElevationNotice = "Windows asks for administrator permission only after you start the installation.",
        Details = "Details",
        HideDetails = "Hide details",
        SuccessTitle = "Aero is installed",
        SuccessBody = "The update is complete. Aero is ready to use.",
        ErrorTitle = "Aero could not be installed",
        ElevationCancelled = "The administrator request was cancelled.",
        Extracting = "Extracting the embedded package…",
        ReadingPackage = "Checking the version and manifest…",
        CheckingCertificate = "Matching the package certificate…",
        ReadingInstalledVersion = "Reading the installed version…",
        TrustingCertificate = "Trusting the Aero certificate…",
        VerifyingSignature = "Verifying the digital signature…",
        ClosingAero = "Saving the session and closing Aero…",
        InstallingPackage = "Installing the update…",
        VerifyingInstall = "Verifying the installation…",
        StartingAero = "Starting Aero…",
        Complete = "Complete"
    };
}

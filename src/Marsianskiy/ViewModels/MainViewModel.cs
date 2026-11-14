using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using Marsianskiy.Services;

namespace Marsianskiy.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly StateStore _store = new();
    private readonly MihomoEngine _engine = new();
    private readonly LatencyTester _latency;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private AppState _state = new();
    private VpnConnectionState _connectionState;
    private string _statusText = "VPN отключён. Прямое подключение для всего трафика.";
    private string _message = "Импортируйте подписку локально: URL, файл или буфер обмена.";
    private string _engineMessage = "Сообщений движка нет.";
    private string _conflictWarning = "";
    private VpnServer? _selectedServer;
    private bool _isOnboardingVisible;
    private bool _isSettingsVisible;
    private string _currentPage = "dashboard";
    private string _enabledServicesText = "Включено сервисов: 0";
    private string _searchText = "";
    private string _currentServiceFilter = "all";
    private bool _startWithWindows;
    private string _sessionDurationText = "Сессия: не активна";
    private DateTime _connectedAtUtc;
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _healthCancellation;
    private int _crashRecoveries;
    private DateTime _lastFailoverUtc = DateTime.MinValue;
    private readonly Queue<string> _engineLogLines = new();
    private readonly DispatcherTimer _subscriptionTimer = new() { Interval = TimeSpan.FromHours(1) };

    public MainViewModel()
    {
        _latency = new LatencyTester(_engine);
        _sessionTimer.Tick += (_, _) => SessionDurationText = "Сессия: " + (DateTime.UtcNow - _connectedAtUtc).ToString(@"hh\:mm\:ss");
        _subscriptionTimer.Tick += (_, _) => _ = RefreshSubscriptionAsync();
        RefreshSubscriptionCommand = new AsyncRelayCommand(RefreshSubscriptionAsync, () => SubscriptionRefreshHours > 0 && !string.IsNullOrWhiteSpace(_state.SubscriptionUrl));
        CollectionViewSource.GetDefaultView(Services).Filter = item => item is ServiceDefinition service && MatchesService(service);
        ImportFileCommand = new AsyncRelayCommand(ImportFileAsync);
        ImportClipboardCommand = new AsyncRelayCommand(ImportClipboardAsync);
        AutoSelectCommand = new AsyncRelayCommand(AutoSelectAsync, () => Servers.Count > 0);
        MeasureSelectedCommand = new AsyncRelayCommand(MeasureSelectedAsync, () => SelectedServer is not null);
        ConnectCommand = new AsyncRelayCommand(ConnectOrDisconnectAsync, () => ConnectionState is not (VpnConnectionState.Checking or VpnConnectionState.Connecting));
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync, () => Servers.Count > 0);
        AssignSelectedCommand = new AsyncRelayCommand(AssignSelectedAsync, () => SelectedServer is not null && Servers.Count > 0);
        SaveCountryCommand = new AsyncRelayCommand(SaveCountryAsync, () => SelectedServer is not null);
        EditRoutesCommand = new RelayCommand(EditRoutes);
        ReloadRoutesCommand = new AsyncRelayCommand(ReloadRoutesAsync);
        OpenSettingsCommand = new RelayCommand(() => { CompleteOnboarding(); IsOnboardingVisible = false; IsSettingsVisible = true; });
        CloseSettingsCommand = new RelayCommand(() => IsSettingsVisible = false);
        CloseOnboardingCommand = new RelayCommand(() => { CompleteOnboarding(); IsOnboardingVisible = false; });
        ShowOnboardingCommand = new RelayCommand(() => { IsSettingsVisible = false; IsOnboardingVisible = true; });
        OpenDataDirectoryCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { AppPaths.DataDirectory }, UseShellExecute = false }));
        CreateShortcutCommand = new RelayCommand(() => Message = "Ярлык создан: " + DesktopShortcutService.Create());
        OpenServiceCommand = new RelayCommand<ServiceDefinition>(OpenService);
        NavigateCommand = new RelayCommand<string>(page => CurrentPage = page);
        ApplyProfileCommand = new RelayCommand<string>(ApplyProfile);
        FilterServicesCommand = new RelayCommand<string>(filter =>
        {
            CurrentServiceFilter = filter;
            Message = $"Категория «{filter}»: сервисов в списке {Services.Count(MatchesService)}.";
        });
        RunDiagnosticsCommand = new AsyncRelayCommand(RunRouteDiagnosticsAsync);
        ExportReportCommand = new RelayCommand(ExportSupportReport);
        UpdateMihomoCommand = new AsyncRelayCommand(UpdateMihomoAsync);

        _engine.LogReceived += text => RunOnUi(() =>
        {
            EngineMessage = text;
            _engineLogLines.Enqueue(text);
            while (_engineLogLines.Count > 50) _engineLogLines.Dequeue();
        });
        _engine.UnexpectedExit += text => RunOnUi(() =>
        {
            ConnectionState = VpnConnectionState.Error;
            StatusText = "Сбой VPN. Выбранные сервисы не имеют правила прямого подключения.";
            Message = SecretRedactor.Redact(text, Servers, _state.SubscriptionUrl);
            _ = RecoverAfterCrashAsync(text);
        });
    }

    public ObservableCollection<VpnServer> Servers { get; } = new();
    public ObservableCollection<ServiceDefinition> Services { get; } = new();
    public string EngineVersion => MihomoConfigBuilder.EngineVersion;
    public string DataDirectory => AppPaths.DataDirectory;

    public VpnConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (!Set(ref _connectionState, value)) return;
            Notify(nameof(ConnectButtonText));
            Notify(nameof(IsConnected));
            Notify(nameof(IsBusy));
            UpdateSessionTimer();
            ConnectCommand.Refresh();
        }
    }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string EngineMessage { get => _engineMessage; private set => Set(ref _engineMessage, value); }
    public string ConflictWarning { get => _conflictWarning; private set { if (Set(ref _conflictWarning, value)) Notify(nameof(HasConflictWarning)); } }
    public bool HasConflictWarning => ConflictWarning.Length > 0;
    public VpnServer? SelectedServer { get => _selectedServer; set { Set(ref _selectedServer, value); MeasureSelectedCommand.Refresh(); AssignSelectedCommand.Refresh(); SaveCountryCommand.Refresh(); } }
    public bool IsOnboardingVisible { get => _isOnboardingVisible; private set => Set(ref _isOnboardingVisible, value); }
    public bool IsSettingsVisible { get => _isSettingsVisible; private set => Set(ref _isSettingsVisible, value); }
    public string CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!Set(ref _currentPage, value)) return;
            Notify(nameof(IsDashboardVisible));
            Notify(nameof(IsServicesPageVisible));
            Notify(nameof(IsServersPageVisible));
        }
    }
    public bool IsDashboardVisible => CurrentPage == "dashboard";
    public bool IsServicesPageVisible => CurrentPage == "services";
    public bool IsServersPageVisible => CurrentPage == "servers";
    public string EnabledServicesText { get => _enabledServicesText; private set => Set(ref _enabledServicesText, value); }
    public string CurrentServiceFilter
    {
        get => _currentServiceFilter;
        private set
        {
            if (!Set(ref _currentServiceFilter, value)) return;
            CollectionViewSource.GetDefaultView(Services).Refresh();
        }
    }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            CollectionViewSource.GetDefaultView(Services).Refresh();
        }
    }
    public string SessionDurationText { get => _sessionDurationText; private set => Set(ref _sessionDurationText, value); }
    public string ConnectButtonText => ConnectionState switch
    {
        VpnConnectionState.Checking => "Проверка…",
        VpnConnectionState.Connecting => "Подключение…",
        VpnConnectionState.Connected or VpnConnectionState.Degraded => "Отключить VPN",
        _ => "Подключить VPN"
    };
    public bool IsConnected => ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Degraded;
    public bool IsBusy => ConnectionState is VpnConnectionState.Checking or VpnConnectionState.Connecting;
    public bool GeminiIncludeGoogleSignIn
    {
        get => _state.GeminiIncludeGoogleSignIn;
        set
        {
            if (_state.GeminiIncludeGoogleSignIn == value) return;
            _state.GeminiIncludeGoogleSignIn = value;
            Notify();
            _store.Save(_state);
            _ = RestartIfConnectedAsync();
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;
            _startWithWindows = value;
            Notify();
            _ = SetAutostartAsync(value);
        }
    }

    public bool CloseToTray
    {
        get => _state.CloseToTray;
        set
        {
            if (_state.CloseToTray == value) return;
            _state.CloseToTray = value;
            Notify();
            _store.Save(_state);
        }
    }

    public bool AutoReconnect
    {
        get => _state.AutoReconnect;
        set
        {
            if (_state.AutoReconnect == value) return;
            _state.AutoReconnect = value;
            Notify();
            _store.Save(_state);
        }
    }

    public bool AutoFailover
    {
        get => _state.AutoFailover;
        set { if (_state.AutoFailover == value) return; _state.AutoFailover = value; Notify(); _store.Save(_state); }
    }

    public bool ReduceMotion
    {
        get => _state.ReduceMotion;
        set { if (_state.ReduceMotion == value) return; _state.ReduceMotion = value; Notify(); _store.Save(_state); }
    }

    public int SubscriptionRefreshHours
    {
        get => _state.SubscriptionRefreshHours;
        set { if (_state.SubscriptionRefreshHours == value) return; _state.SubscriptionRefreshHours = value; Notify(); _store.Save(_state); }
    }

    private async Task SetAutostartAsync(bool enabled)
    {
        try
        {
            await WindowsAutostartService.SetAsync(enabled);
            _state.StartWithWindows = enabled;
            _store.Save(_state);
            Message = enabled ? "Автозапуск включён через Планировщик заданий Windows." : "Автозапуск отключён.";
        }
        catch (Exception ex)
        {
            _startWithWindows = !enabled;
            Notify(nameof(StartWithWindows));
            ShowError("Не удалось изменить автозапуск", ex);
        }
    }

    public AsyncRelayCommand ImportFileCommand { get; }
    public AsyncRelayCommand ImportClipboardCommand { get; }
    public AsyncRelayCommand AutoSelectCommand { get; }
    public AsyncRelayCommand MeasureSelectedCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand ReconnectCommand { get; }
    public AsyncRelayCommand AssignSelectedCommand { get; }
    public AsyncRelayCommand SaveCountryCommand { get; }
    public RelayCommand EditRoutesCommand { get; }
    public AsyncRelayCommand ReloadRoutesCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand CloseSettingsCommand { get; }
    public RelayCommand CloseOnboardingCommand { get; }
    public RelayCommand ShowOnboardingCommand { get; }
    public RelayCommand OpenDataDirectoryCommand { get; }
    public RelayCommand CreateShortcutCommand { get; }
    public RelayCommand<ServiceDefinition> OpenServiceCommand { get; }
    public RelayCommand<string> NavigateCommand { get; }
    public RelayCommand<string> FilterServicesCommand { get; }
    public AsyncRelayCommand RunDiagnosticsCommand { get; }
    public AsyncRelayCommand UpdateMihomoCommand { get; }
    public RelayCommand ExportReportCommand { get; }
    public AsyncRelayCommand RefreshSubscriptionCommand { get; }
    public RelayCommand<string> ApplyProfileCommand { get; }

    public void Initialize()
    {
        _state = _store.Load();
        _startWithWindows = _state.StartWithWindows;
        IsOnboardingVisible = !_state.OnboardingCompleted;
        foreach (var server in _state.Servers) Servers.Add(server);
        LoadServiceDefinitions();
        EnabledServicesText = $"Включено сервисов: {Services.Count(x => x.Enabled)}";
        if (Servers.Count == 1)
            Message = "Импортирован один сервер: автоматического выбора между серверами нет. Можно измерить его задержку и подключиться.";
        SelectedServer = Servers.FirstOrDefault();
        AutoSelectCommand.Refresh();
        ReconnectCommand.Refresh();
        RefreshSubscriptionCommand.Refresh();
        if (SubscriptionRefreshHours > 0)
        {
            _subscriptionTimer.Start();
            if (_state.LastSubscriptionRefreshUtc is null || DateTime.UtcNow - _state.LastSubscriptionRefreshUtc.Value > TimeSpan.FromHours(SubscriptionRefreshHours))
                _ = RefreshSubscriptionAsync();
        }
    }

    public async Task RecoverNetworkAsync()
    {
        try { await WindowsDnsGuard.RestoreAsync(); }
        catch (Exception ex) { ShowError("Не удалось восстановить DNS после предыдущей сессии", ex); }
    }

    public async Task ImportUrlAsync(string enteredUrl)
    {
        await _operation.WaitAsync();
        try
        {
            var address = string.IsNullOrWhiteSpace(enteredUrl) ? _state.SubscriptionUrl : enteredUrl.Trim();
            if (address.Length == 0) { Message = "Вставьте URL подписки в скрытое поле. Не отправляйте его в чат."; return; }
            StatusText = "Загрузка подписки напрямую, без сторонних сервисов…";
            var content = await SubscriptionDownloader.DownloadAsync(address, _lifetime.Token);
            await ApplyImportAsync(SubscriptionParser.Parse(content), address);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("Импорт URL не выполнен", ex); }
        finally { _operation.Release(); }
    }

    private async Task ImportFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Локальный импорт подписки",
            Filter = "Подписка (*.txt;*.list;*.sub)|*.txt;*.list;*.sub|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        await _operation.WaitAsync();
        try
        {
            if (new FileInfo(dialog.FileName).Length > 4_000_000) throw new FormatException("Файл подписки больше 4 МБ.");
            var content = await File.ReadAllTextAsync(dialog.FileName, _lifetime.Token);
            await ApplyImportAsync(SubscriptionParser.Parse(content), "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("Импорт файла не выполнен", ex); }
        finally { _operation.Release(); }
    }

    private async Task ImportClipboardAsync()
    {
        if (!Clipboard.ContainsText()) { Message = "В буфере обмена нет текста подписки."; return; }
        var content = Clipboard.GetText();
        await _operation.WaitAsync();
        try { await ApplyImportAsync(SubscriptionParser.Parse(content), ""); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("Импорт буфера не выполнен", ex); }
        finally { _operation.Release(); }
        await Task.CompletedTask;
    }

    private async Task ApplyImportAsync(ImportResult result, string subscriptionUrl, bool preservePreferences = false)
    {
        var wasRunning = IsRunning();
        var previousServers = Servers.ToList();
        var previousTargets = Services.ToDictionary(x => x.Id, x => x.Target);
        _healthCancellation?.Cancel();
        if (wasRunning) { await _engine.StopAsync(); await WindowsDnsGuard.RestoreAsync(); }
        ConnectionState = VpnConnectionState.Disconnected;
        Servers.Clear();
        foreach (var server in result.Servers)
        {
            if (preservePreferences)
            {
                var previous = previousServers.FirstOrDefault(x => x.Protocol == server.Protocol && x.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) && x.Port == server.Port);
                if (previous is not null) server.Country = previous.Country;
            }
            Servers.Add(server);
        }
        _state.Servers = Servers.ToList();
        _state.SubscriptionUrl = subscriptionUrl;
        _state.LastSubscriptionRefreshUtc = DateTime.UtcNow;
        foreach (var service in Services)
        {
            var target = preservePreferences ? previousTargets.GetValueOrDefault(service.Id, "auto") : "auto";
            if (target.StartsWith("server:", StringComparison.Ordinal))
            {
                var oldServer = previousServers.FirstOrDefault(x => x.Id == target[7..]);
                var newServer = oldServer is null ? null : Servers.FirstOrDefault(x => x.Protocol == oldServer.Protocol && x.Host.Equals(oldServer.Host, StringComparison.OrdinalIgnoreCase) && x.Port == oldServer.Port);
                target = newServer is null ? "auto" : "server:" + newServer.Id;
            }
            service.Target = target;
        }
        SavePreferences();
        RebuildTargets();
        SelectedServer = Servers.FirstOrDefault();
        AutoSelectCommand.Refresh();
        ReconnectCommand.Refresh();
        StatusText = "VPN отключён. Серверы обновлены локально.";
        Message = result.Summary;
        if (result.Errors.Count > 0) Message += " " + string.Join(" ", result.Errors.Take(3));
        if (Servers.Count == 1) Message += " Импортирован единственный сервер: автоматического выбора между серверами нет.";
        if (wasRunning)
        {
            await MeasureAllCoreAsync();
            if (Servers.Any(x => x.LatencyMs > 0)) await RestartCoreAsync();
            else StatusText = "После обновления подписки нет доступных узлов. VPN остановлен.";
        }
    }

    private async Task RefreshSubscriptionAsync()
    {
        if (SubscriptionRefreshHours <= 0 || string.IsNullOrWhiteSpace(_state.SubscriptionUrl) || _operation.CurrentCount == 0) return;
        await _operation.WaitAsync();
        try
        {
            StatusText = "Обновление подписки с сохранением ручных стран и маршрутов…";
            var content = await SubscriptionDownloader.DownloadAsync(_state.SubscriptionUrl, _lifetime.Token);
            await ApplyImportAsync(SubscriptionParser.Parse(content), _state.SubscriptionUrl, true);
            Message += " Обновление по расписанию завершено.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("Обновление подписки не выполнено", ex); }
        finally { _operation.Release(); }
    }

    private async Task AutoSelectAsync()
    {
        await _operation.WaitAsync();
        try
        {
            await MeasureAllCoreAsync();
            var available = Servers.Where(x => x.LatencyMs > 0).OrderBy(x => x.LatencyMs).ToList();
            if (available.Count == 0) throw new InvalidOperationException("Ни один сервер из подписки не прошёл HTTPS-проверку. Прямой сервер не подставлялся.");
            SelectedServer = available[0];
            foreach (var service in Services) service.Target = "auto";
            SavePreferences();
            if (Servers.Count == 1)
                Message = $"Импортирован единственный сервер, поэтому выбирать не из чего. Он доступен, задержка {available[0].LatencyMs} мс.";
            else
                Message = $"Автовыбор: {available[0].Name} · {available[0].Country} · {available[0].LatencyMs} мс. Проверялись только серверы этой подписки.";
            if (IsRunning()) await RestartCoreAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("Автовыбор не выполнен", ex); }
        finally { _operation.Release(); }
    }

    private async Task MeasureSelectedAsync()
    {
        if (SelectedServer is null) return;
        await _operation.WaitAsync();
        try
        {
            var server = SelectedServer;
            await MeasureOneCoreAsync(server);
            Message = server.LatencyMs > 0 ? $"{server.Name}: HTTPS через VPN доступен, {server.LatencyMs} мс." : $"{server.Name}: сервер недоступен. {server.LatencyStatus}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("Проверка задержки не выполнена", ex); }
        finally { _operation.Release(); }
    }

    private async Task MeasureAllCoreAsync()
    {
        StatusText = "Измерение HTTPS-задержки через серверы подписки…";
        using var gate = new SemaphoreSlim(4, 4);
        var completed = 0;
        var tasks = Servers.Select(async server =>
        {
            await gate.WaitAsync(_lifetime.Token);
            try { await MeasureOneCoreAsync(server); }
            finally
            {
                gate.Release();
                var done = Interlocked.Increment(ref completed);
                RunOnUi(() => StatusText = $"HTTPS-проверка серверов: {done}/{Servers.Count}, одновременно не более четырёх…");
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task MeasureOneCoreAsync(VpnServer server)
    {
        server.LatencyMs = null;
        server.LatencyStatus = "Проверка…";
        try
        {
            server.LatencyMs = await _latency.MeasureAsync(server, _lifetime.Token);
            server.LatencyStatus = "Доступен";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            server.LatencyMs = null;
            server.LatencyStatus = "Недоступен";
            EngineMessage = SecretRedactor.Redact(ex.Message, Servers, _state.SubscriptionUrl);
        }
    }

    public async Task ConnectOrDisconnectAsync()
    {
        await _operation.WaitAsync();
        try
        {
            if (IsRunning())
            {
                await DisconnectCoreAsync();
                return;
            }
            if (Servers.Count == 0) throw new InvalidOperationException("Сначала импортируйте серверы из своей подписки.");
            if (!Services.Any(x => x.Enabled)) throw new InvalidOperationException("Включите маршрутизацию хотя бы одной карточки.");
            if (Services.Where(x => x.Enabled).Any(x => x.Target == "auto") && Servers.All(x => x.LatencyMs is null or <= 0))
            {
                await MeasureAllCoreAsync();
                if (Servers.All(x => x.LatencyMs is null or <= 0)) throw new InvalidOperationException("Нет доступного сервера для автоматического подключения.");
            }
            var selected = Services.Where(x => x.Enabled).Select(x => TargetResolver.Resolve(x.Target, Servers.ToList())).DistinctBy(x => x.Id).ToList();
            foreach (var server in selected.Where(x => x.LatencyMs is null or <= 0))
            {
                StatusText = $"Проверка доступности выбранного сервера: {server.Name}…";
                await MeasureOneCoreAsync(server);
                if (server.LatencyMs is null or <= 0) throw new InvalidOperationException($"Выбранный сервер «{server.Name}» недоступен. Выберите другой или выполните автовыбор.");
            }
            ConnectionState = VpnConnectionState.Checking;
            StatusText = "Mihomo проверяет сгенерированную конфигурацию…";
            var config = MihomoConfigBuilder.BuildMain(Servers.ToList(), Services.ToList(), _state.GeminiIncludeGoogleSignIn);
            ConnectionState = VpnConnectionState.Connecting;
            StatusText = "Запуск TUN, раздельных маршрутов и DNS…";
            await _engine.StartAsync(config, Servers.ToList());
            await WindowsNetworkTuning.PrepareTunInterfaceAsync();
            await UpdateConflictWarningAsync();
            ConnectionState = VpnConnectionState.Connected;
            _crashRecoveries = 0;
            StatusText = "VPN подключён. Включённые сервисы и их DNS — через выбранные серверы; остальной трафик — напрямую.";
            Message = "Защита от прямого fallback включена: при недоступности узла выбранные сервисы не получают правило DIRECT.";
            StartHealthMonitor();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _engine.StopAsync();
            await WindowsDnsGuard.RestoreAsync();
            ConnectionState = VpnConnectionState.Error;
            StatusText = "VPN не подключён. Проверьте сообщение и настройки.";
            ShowError("Подключение не выполнено", ex);
        }
        finally { _operation.Release(); }
    }

    private async Task ReconnectAsync()
    {
        await _operation.WaitAsync();
        try { await RestartCoreAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("Переподключение не выполнено", ex); }
        finally { _operation.Release(); }
    }

    private async Task RestartIfConnectedAsync()
    {
        if (!IsRunning() || _operation.CurrentCount == 0) return;
        await _operation.WaitAsync();
        try { if (IsRunning()) await RestartCoreAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { ShowError("Не удалось применить изменения", ex); }
        finally { _operation.Release(); }
    }

    private async Task RestartCoreAsync()
    {
        ConnectionState = VpnConnectionState.Checking;
        StatusText = "Проверка новой конфигурации и переподключение…";
        var config = MihomoConfigBuilder.BuildMain(Servers.ToList(), Services.ToList(), _state.GeminiIncludeGoogleSignIn);
        await _engine.StopAsync();
        await WindowsDnsGuard.RestoreAsync();
        await _engine.StartAsync(config, Servers.ToList());
            await WindowsNetworkTuning.PrepareTunInterfaceAsync();
            await UpdateConflictWarningAsync();
        ConnectionState = VpnConnectionState.Connected;
        StatusText = "VPN подключён. Новые маршруты применены; остальной трафик — напрямую.";
        Message = "Переподключение выполнено, конфигурация прошла проверку mihomo -t.";
        StartHealthMonitor();
    }

    private async Task DisconnectCoreAsync()
    {
        _healthCancellation?.Cancel();
        await _engine.StopAsync();
        await WindowsDnsGuard.RestoreAsync();
        StateStore.CleanupStaleRuntimeFiles();
        ConnectionState = VpnConnectionState.Disconnected;
        StatusText = "VPN отключён. Весь трафик снова идёт напрямую; временные конфиги удалены.";
        Message = "Подключение остановлено пользователем.";
    }

    private async Task AssignSelectedAsync()
    {
        if (SelectedServer is null) return;
        foreach (var service in Services) service.Target = "server:" + SelectedServer.Id;
        SavePreferences();
        Message = $"Всем карточкам вручную назначен сервер «{SelectedServer.Name}».";
        await RestartIfConnectedAsync();
    }

    private async Task SaveCountryAsync()
    {
        if (SelectedServer is null) return;
        _state.Servers = Servers.ToList();
        _store.Save(_state);
        RebuildTargets();
        Message = $"Страна сервера «{SelectedServer.Name}» изменена локально на «{SelectedServer.Country}».";
        await RestartIfConnectedAsync();
    }

    private void LoadServiceDefinitions()
    {
        var definitions = ServiceCatalog.Load();
        Services.Clear();
        foreach (var service in definitions)
        {
            if (_state.Services.TryGetValue(service.Id, out var preference))
            {
                service.Enabled = preference.Enabled;
                service.Target = preference.Target;
            }
            service.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ServiceDefinition.Enabled) or nameof(ServiceDefinition.Target))
                {
                    UpdateServiceTargetText(service);
                    SavePreferences();
                    _ = RestartIfConnectedAsync();
                }
            };
            Services.Add(service);
        }
        RebuildTargets();
    }

    private void RebuildTargets()
    {
        foreach (var service in Services)
        {
            var current = service.Target;
            service.Targets.Clear();
            service.Targets.Add(new RouteTarget("auto", "Авто — минимальная задержка"));
            foreach (var country in Servers.Select(x => x.Country).Where(x => x != "Не указана").Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(x => x))
                service.Targets.Add(new RouteTarget("country:" + country, "Страна · " + country));
            foreach (var server in Servers)
                service.Targets.Add(new RouteTarget("server:" + server.Id, $"{server.Name} · {server.Country}"));
            if (service.Targets.All(x => x.Value != current)) service.Target = "auto";
            UpdateServiceTargetText(service);
        }
    }

    private void UpdateServiceTargetText(ServiceDefinition service)
    {
        if (service.Target.StartsWith("server:", StringComparison.Ordinal))
        {
            var server = Servers.FirstOrDefault(x => x.Id == service.Target[7..]);
            service.TargetCountry = server?.Country ?? "Не указана";
            service.TargetMode = "Выбран вручную";
        }
        else if (service.Target.StartsWith("country:", StringComparison.Ordinal))
        {
            service.TargetCountry = service.Target[8..];
            service.TargetMode = "Выбор по стране";
        }
        else
        {
            service.TargetCountry = "Автовыбор";
            service.TargetMode = "Минимальный ping";
        }
    }

    private void ApplyProfile(string profile)
    {
        string[] ids = profile switch
        {
            "games" => ["roblox", "chess", "fortnite"],
            "stream" => ["twitch", "youtube", "tiktok", "discord", "soundcloud", "spotify"],
            "work" => ["notion", "figma", "openai", "gemini", "claude", "cloudflare"],
            _ => []
        };
        foreach (var service in Services) service.Enabled = ids.Contains(service.Id);
        SavePreferences();
        Message = $"Профиль «{profile}»: включено сервисов {ids.Length}. Остальные карточки выключены.";
        _ = RestartIfConnectedAsync();
    }

    private bool MatchesService(ServiceDefinition service)
    {
        var matchesSearch = SearchText.Length == 0 || service.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) || service.Description.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
        if (!matchesSearch) return false;
        return CurrentServiceFilter switch
        {
            "games" => service.Id is "roblox" or "chess" or "fortnite",
            "social" => service.Id is "tiktok" or "twitter" or "facebook" or "instagram" or "telegram" or "whatsapp" or "pinterest" or "discord",
            "ai" => service.Id is "openai" or "gemini" or "claude",
            "other" => service.Id is not ("roblox" or "chess" or "fortnite" or "tiktok" or "twitter" or "facebook" or "instagram" or "telegram" or "whatsapp" or "pinterest" or "discord" or "openai" or "gemini" or "claude"),
            _ => true
        };
    }

    private async Task UpdateMihomoAsync()
    {
        await _operation.WaitAsync();
        try
        {
            if (IsRunning()) await DisconnectCoreAsync();
            StatusText = "Проверка официального релиза mihomo и SHA-256…";
            Message = await MihomoUpdateService.UpdateAsync(_lifetime.Token);
            Notify(nameof(EngineVersion));
            StatusText = "Обновление движка завершено. Можно подключиться снова.";
        }
        catch (Exception ex) { ShowError("Обновление mihomo не выполнено", ex); }
        finally { _operation.Release(); }
    }

    private async Task ReloadRoutesAsync()
    {
        await _operation.WaitAsync();
        try
        {
            SavePreferences();
            LoadServiceDefinitions();
            Message = "Файл services.json прочитан. Домены и CIDR корректны, пересечений между сервисами нет.";
            if (IsRunning()) await RestartCoreAsync();
        }
        catch (Exception ex) { ShowError("Маршруты не применены", ex); }
        finally { _operation.Release(); }
    }

    private void OpenService(ServiceDefinition service)
    {
        var url = service.Id switch
        {
            "twitch" => "https://www.twitch.tv/",
            "openai" => "https://chatgpt.com/",
            "tiktok" => "https://www.tiktok.com/",
            "roblox" => "https://www.roblox.com/",
            "soundcloud" => "https://soundcloud.com/",
            "gemini" => "https://gemini.google.com/",
            "chess" => "https://www.chess.com/",
            "youtube" => "https://www.youtube.com/",
            "twitter" => "https://x.com/",
            "facebook" => "https://www.facebook.com/",
            "2ip" => "https://2ip.io/",
            "instagram" => "https://www.instagram.com/",
            "cloudflare" => "https://one.one.one.one/",
            "telegram" => "https://web.telegram.org/",
            "notion" => "https://www.notion.so/",
            "spotify" => "https://open.spotify.com/",
            "figma" => "https://www.figma.com/",
            "whatsapp" => "https://web.whatsapp.com/",
            "pinterest" => "https://www.pinterest.com/",
            "discord" => "https://discord.com/app",
            "claude" => "https://claude.ai/",
            _ => throw new InvalidOperationException("Для этого сервиса не задан безопасный HTTPS-ярлык.")
        };
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Message = $"Открыт сайт {service.Name}. Маршрут: " + (service.Enabled ? "через назначенный VPN при активном подключении." : "напрямую, маршрутизация карточки выключена.");
    }

    private async Task RunRouteDiagnosticsAsync()
    {
        if (!IsRunning()) { Message = "Сначала подключите VPN, затем запустите единый тест маршрута."; return; }
        if (!Services.Any(x => x.Id == "2ip" && x.Enabled)) { Message = "Для единого теста включите карточку 2ip.io и переподключите VPN."; return; }
        await _operation.WaitAsync();
        try
        {
            StatusText = "Единый тест: сравнение маршрута 2ip с прямым трафиком…";
            Message = await ConnectionDiagnostics.TestRouteAsync(_lifetime.Token);
            StatusText = "Диагностика маршрута завершена.";
        }
        catch (Exception ex) { ShowError("Тест маршрута не выполнен", ex); }
        finally { _operation.Release(); }
    }

    private void ExportSupportReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить отчёт без секретов",
            FileName = "marsianskiy-support.txt",
            Filter = "Текстовый отчёт (*.txt)|*.txt",
            CheckPathExists = true
        };
        if (dialog.ShowDialog() != true) return;
        var enabled = Services.Where(x => x.Enabled).Select(x => x.Name).ToList();
        SupportReportService.Write(dialog.FileName, new MainViewModelState(StatusText, ConflictWarning, enabled, _engineLogLines.ToList()));
        Message = "Отчёт сохранён без подписки, UUID, адресов серверов, REALITY-ключей и публичных IP.";
    }

    private void EditRoutes()
    {
        AppPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { AppPaths.ServicesFile }, UseShellExecute = false });
        Message = $"Открыт локальный файл маршрутов. После сохранения нажмите «Проверить маршруты». Путь: {AppPaths.ServicesFile}";
    }

    private void SavePreferences()
    {
        _state.Servers = Servers.ToList();
        _state.Services = Services.ToDictionary(x => x.Id, x => new ServicePreference { Enabled = x.Enabled, Target = x.Target });
        EnabledServicesText = $"Включено сервисов: {Services.Count(x => x.Enabled)}";
        _store.Save(_state);
    }

    private async Task UpdateConflictWarningAsync()
    {
        var conflicts = await NetworkConflictDetector.GetActiveVpnAdaptersAsync();
        ConflictWarning = conflicts.Count == 0 ? "" : "Обнаружены активные VPN-адаптеры: " + string.Join(", ", conflicts) + ". Они могут перехватывать трафик раньше marsianskiy.";
    }

    private async Task RecoverAfterCrashAsync(string details)
    {
        if (!AutoFailover || _crashRecoveries >= 3) return;
        _crashRecoveries++;
        await _operation.WaitAsync();
        try
        {
            StatusText = "Mihomo завершился. Аварийное восстановление и поиск доступного узла…";
            await MeasureAllCoreAsync();
            if (Servers.All(x => x.LatencyMs is null or <= 0)) throw new InvalidOperationException("Для аварийного восстановления нет доступных узлов. Нажмите «Переподключить».");
            await RestartCoreAsync();
            Message = "Соединение автоматически восстановлено после сбоя движка.";
        }
        catch (Exception ex)
        {
            ConnectionState = VpnConnectionState.Error;
            ShowError("Аварийное восстановление не выполнено", ex);
        }
        finally { _operation.Release(); }
    }

    private async Task FailoverAsync(IReadOnlyList<string> failed)
    {
        if (!AutoFailover || DateTime.UtcNow - _lastFailoverUtc < TimeSpan.FromMinutes(2)) return;
        _lastFailoverUtc = DateTime.UtcNow;
        await _operation.WaitAsync();
        try
        {
            StatusText = "Сервер недоступен. Автоматический подбор резервного узла…";
            await MeasureAllCoreAsync();
            if (Servers.All(x => x.LatencyMs is null or <= 0)) throw new InvalidOperationException("Нет доступного резервного узла.");
            await RestartCoreAsync();
            Message = "Выполнено автоматическое переключение. Прежний узел: " + string.Join(", ", failed);
        }
        catch (Exception ex)
        {
            ConnectionState = VpnConnectionState.Degraded;
            ShowError("Автопереключение не выполнено", ex);
        }
        finally { _operation.Release(); }
    }

    private void StartHealthMonitor()
    {
        _healthCancellation?.Cancel();
        _healthCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _healthCancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(45), token); } catch (OperationCanceledException) { break; }
                if (token.IsCancellationRequested || !IsRunning() || _operation.CurrentCount == 0) continue;
                var failed = new List<string>();
                try
                {
                    var selected = Services.Where(x => x.Enabled).Select(x => TargetResolver.Resolve(x.Target, Servers.ToList())).DistinctBy(x => x.Id).ToList();
                    foreach (var server in selected)
                    {
                        try { await _latency.MeasureAsync(server, token); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { failed.Add(server.Name); }
                    }
                    RunOnUi(() =>
                    {
                        if (!IsRunning()) return;
                        if (failed.Count > 0)
                        {
                            ConnectionState = VpnConnectionState.Degraded;
                            StatusText = AutoFailover ? "Проверка узла не прошла. Выполняется автопереключение…" : "VPN-сервер недоступен. Нажмите «Переподключить» или выберите другой сервер.";
                            Message = "Не прошли проверку: " + string.Join(", ", failed) + ". Остальной трафик по-прежнему прямой.";
                            if (AutoFailover) _ = FailoverAsync(failed);
                        }
                        else
                        {
                            ConnectionState = VpnConnectionState.Connected;
                            StatusText = "VPN подключён и проверен. Выбранные сервисы защищены; остальной трафик — напрямую.";
                        }
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { RunOnUi(() => Message = "Проверка состояния: " + SecretRedactor.Redact(ex.Message, Servers, _state.SubscriptionUrl)); }
            }
        }, token);
    }

    public async Task ShutdownAsync()
    {
        _lifetime.Cancel();
        _healthCancellation?.Cancel();
        await _operation.WaitAsync();
        try { SavePreferences(); }
        catch (Exception ex) { Debug.WriteLine("State save on shutdown: " + SecretRedactor.Redact(ex.Message, Servers, _state.SubscriptionUrl)); }
        try
        {
            await _engine.StopAsync();
        await WindowsDnsGuard.RestoreAsync();
            StateStore.CleanupStaleRuntimeFiles();
        }
        finally { _operation.Release(); }
    }

    private void UpdateSessionTimer()
    {
        if (ConnectionState is VpnConnectionState.Connected or VpnConnectionState.Degraded)
        {
            if (!_sessionTimer.IsEnabled)
            {
                _connectedAtUtc = DateTime.UtcNow;
                SessionDurationText = "Сессия: 00:00:00";
                _sessionTimer.Start();
            }
        }
        else
        {
            _sessionTimer.Stop();
            SessionDurationText = "Сессия: не активна";
        }
    }

    private void CompleteOnboarding()
    {
        if (_state.OnboardingCompleted) return;
        _state.OnboardingCompleted = true;
        _store.Save(_state);
    }

    private bool IsRunning() => _engine.IsRunning;
    private void ShowError(string prefix, Exception ex) => Message = prefix + ": " + SecretRedactor.Redact(ex.Message, Servers, _state.SubscriptionUrl);
    private static void RunOnUi(Action action) => System.Windows.Application.Current.Dispatcher.Invoke(action);
}

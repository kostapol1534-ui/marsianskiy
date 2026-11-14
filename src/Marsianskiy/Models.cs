using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Marsianskiy;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum ServerProtocol
{
    VlessReality,
    Hysteria2
}

public sealed class VpnServer : ObservableObject
{
    private string _name = "";
    private string _country = "Не указана";
    private int? _latencyMs;
    private string _latencyStatus = "Не проверено";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ServerProtocol Protocol { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Country { get => _country; set => Set(ref _country, string.IsNullOrWhiteSpace(value) ? "Не указана" : value.Trim()); }

    public string Uuid { get; set; } = "";
    public string Flow { get; set; } = "";
    public string RealityPublicKey { get; set; } = "";
    public string RealityShortId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ClientFingerprint { get; set; } = "chrome";

    public string Password { get; set; } = "";
    public string Obfs { get; set; } = "";
    public string ObfsPassword { get; set; } = "";
    public string Ports { get; set; } = "";
    public int HopInterval { get; set; }
    public int UpMbps { get; set; }
    public int DownMbps { get; set; }
    public bool SkipCertVerify { get; set; }

    [JsonIgnore] public int? LatencyMs { get => _latencyMs; set { Set(ref _latencyMs, value); Notify(nameof(LatencyText)); } }
    [JsonIgnore] public string LatencyStatus { get => _latencyStatus; set { Set(ref _latencyStatus, value); Notify(nameof(LatencyText)); } }
    [JsonIgnore] public string LatencyText => LatencyMs is > 0 ? $"{LatencyMs} мс" : LatencyStatus;
    [JsonIgnore] public string ProtocolName => Protocol == ServerProtocol.VlessReality ? "VLESS · REALITY" : "Hysteria2";
    [JsonIgnore] public string NodeTag => "node_" + Id;
}

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public string SubscriptionUrl { get; set; } = "";
    public List<VpnServer> Servers { get; set; } = new();
    public Dictionary<string, ServicePreference> Services { get; set; } = new();
    public bool GeminiIncludeGoogleSignIn { get; set; }
    public bool OnboardingCompleted { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; }
    public bool AutoReconnect { get; set; }
    public bool AutoFailover { get; set; } = true;
    public int SubscriptionRefreshHours { get; set; }
    public DateTime? LastSubscriptionRefreshUtc { get; set; }
    public bool ReduceMotion { get; set; }
}

public sealed class ServicePreference
{
    public bool Enabled { get; set; }
    public string Target { get; set; } = "auto";
}

public sealed class ServiceDefinition : ObservableObject
{
    private bool _enabled;
    private string _target = "auto";
    private string _targetCountry = "Автовыбор";
    private string _targetMode = "Минимальный ping";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mark { get; set; } = "";
    public string Color { get; set; } = "#A98BFF";
    public List<string> Domains { get; set; } = new();
    public List<string> OptionalDomains { get; set; } = new();
    public List<string> IpCidrs { get; set; } = new();
    public List<string> ProcessNames { get; set; } = new();

    [JsonIgnore] public string Icon => $"pack://application:,,,/Marsianskiy;component/Assets/Services/{Id}.svg";
    [JsonIgnore] public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    [JsonIgnore] public string Target { get => _target; set => Set(ref _target, value); }
    [JsonIgnore] public string TargetCountry { get => _targetCountry; set => Set(ref _targetCountry, value); }
    [JsonIgnore] public string TargetMode { get => _targetMode; set => Set(ref _targetMode, value); }
    [JsonIgnore] public ObservableCollection<RouteTarget> Targets { get; } = new();

    public IEnumerable<string> ActiveDomains(bool includeGoogleSignIn) =>
        Id == "gemini" && includeGoogleSignIn ? Domains.Concat(OptionalDomains) : Domains;
}

public sealed record RouteTarget(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record ImportResult(List<VpnServer> Servers, List<string> Errors)
{
    public string Summary => Errors.Count == 0
        ? $"Импортировано серверов: {Servers.Count}."
        : $"Импортировано: {Servers.Count}. Пропущено: {Errors.Count}.";
}

public sealed record RouteOverlap(string Domain, string FirstService, string SecondService);

public enum VpnConnectionState
{
    Disconnected,
    Checking,
    Connecting,
    Connected,
    Degraded,
    Error
}

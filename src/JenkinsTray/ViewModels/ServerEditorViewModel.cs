using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JenkinsTray.Models;
using JenkinsTray.Services;

namespace JenkinsTray.ViewModels;

public partial class ServerEditorViewModel : ObservableObject
{
    private readonly ServerConfig _target;
    private readonly bool _isNew;

    public ServerEditorViewModel(ServerConfig target, bool isNew)
    {
        _target = target;
        _isNew = isNew;

        _name = target.Name;
        _url = target.Url;
        _username = target.Username;
        _token = SecretProtector.Unprotect(target.TokenProtected);
        _acceptInvalidCertificate = target.AcceptInvalidCertificate;
        _enabled = target.Enabled;
    }

    public string Title => Loc.T(_isNew ? "Editor_TitleNew" : "Editor_TitleEdit");

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _token;
    [ObservableProperty] private bool _acceptInvalidCertificate;
    [ObservableProperty] private bool _enabled;

    [ObservableProperty] private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestMessage))]
    private string? _testMessage;

    [ObservableProperty] private bool _testFailed;

    public bool HasTestMessage => !string.IsNullOrWhiteSpace(TestMessage);

    [RelayCommand]
    private async Task TestAsync()
    {
        TestMessage = null;
        TestFailed = false;

        if (!TryBuildProbe(out var probe, out var error))
        {
            TestFailed = true;
            TestMessage = error;
            return;
        }

        IsTesting = true;

        try
        {
            using var client = new JenkinsClient(probe);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            TestMessage = await client.TestConnectionAsync(cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TestFailed = true;
            TestMessage = ex is TaskCanceledException ? Loc.T("Editor_Timeout") : ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool TryBuildProbe(out ServerConfig probe, out string? error)
    {
        probe = new ServerConfig();

        try
        {
            var baseUri = JenkinsClient.NormalizeBaseUri(Url);

            probe = new ServerConfig
            {
                Url = baseUri.AbsoluteUri,
                Username = Username.Trim(),
                // The probe lives in memory only; DPAPI round-trip keeps a single code path.
                TokenProtected = SecretProtector.Protect(Token),
                AcceptInvalidCertificate = AcceptInvalidCertificate,
            };

            error = null;
            return true;
        }
        catch (UriFormatException)
        {
            error = Loc.T("Editor_InvalidUrl");
            return false;
        }
    }

    /// <summary>Validates the form and writes it back into the configuration object.</summary>
    public bool TryApply(out string? error)
    {
        if (!TryBuildProbe(out var probe, out error))
            return false;

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Token))
        {
            error = Loc.T("Editor_CredentialsRequired");
            return false;
        }

        _target.Name = string.IsNullOrWhiteSpace(Name) ? new Uri(probe.Url).Host : Name.Trim();
        _target.Url = probe.Url;
        _target.Username = probe.Username;
        _target.TokenProtected = probe.TokenProtected;
        _target.AcceptInvalidCertificate = AcceptInvalidCertificate;
        _target.Enabled = Enabled;

        error = null;
        return true;
    }
}

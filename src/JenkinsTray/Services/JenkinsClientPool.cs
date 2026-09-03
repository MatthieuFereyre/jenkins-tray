using JenkinsTray.Models;

namespace JenkinsTray.Services;

/// <summary>
/// Keeps one <see cref="JenkinsClient"/> alive per server so sockets and TLS sessions are reused.
/// A client is rebuilt only when the connection settings actually change.
/// </summary>
public sealed class JenkinsClientPool : IDisposable
{
    private readonly Dictionary<string, JenkinsClient> _clients = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public JenkinsClient Get(ServerConfig config)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(config.Id, out var existing))
            {
                if (existing.Fingerprint == config.ConnectionFingerprint())
                    return existing;

                existing.Dispose();
                _clients.Remove(config.Id);
            }

            var client = new JenkinsClient(config);
            _clients[config.Id] = client;
            return client;
        }
    }

    /// <summary>Drops clients for servers that no longer exist in the configuration.</summary>
    public void Prune(IEnumerable<string> liveServerIds)
    {
        var live = new HashSet<string>(liveServerIds, StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var id in _clients.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _clients[id].Dispose();
                _clients.Remove(id);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Values)
                client.Dispose();

            _clients.Clear();
        }
    }
}

using Microsoft.Extensions.Logging;
using Repro.Core.Config;
using Temporalio.Client;
using Temporalio.Runtime;

namespace Repro.Core.Temporal;

/// <summary>Ports the Go original's <c>Dial</c>. Plain gRPC locally; TLS when a key or API key is set.</summary>
public static class ClientFactory
{
    /// <param name="config">Loaded configuration.</param>
    /// <param name="runtime">
    /// The process's single runtime. Required, not optional: a client that connects without one
    /// binds to TemporalRuntime.Default and loses its metrics silently. See <c>ReproRuntime</c>.
    /// </param>
    /// <param name="role">
    /// worker / loadgen / starter / replay, distinct per client in a process: identity is
    /// <c>role@machine:pid</c>, so a shared role is indistinguishable in `workflow describe`.
    /// </param>
    /// <param name="loggerFactory">Logger factory for SDK-level logging.</param>
    /// <param name="namespaceOverride">
    /// Connect to this namespace instead of <see cref="ReproConfig.Namespace"/>. A namespace is a
    /// client property and a worker binds one client, so this is what lets
    /// <c>WorkflowLocalActivity</c> live elsewhere, without a second connect method.
    /// </param>
    public static async Task<ITemporalClient> ConnectAsync(
        ReproConfig config,
        TemporalRuntime runtime,
        string role,
        ILoggerFactory loggerFactory,
        string? namespaceOverride = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = new TemporalClientConnectOptions(config.Address)
        {
            Namespace = string.IsNullOrEmpty(namespaceOverride) ? config.Namespace : namespaceOverride,
            Runtime = runtime,
            LoggerFactory = loggerFactory,

            // The default identity is pid@hostname, useless when four processes share a queue.
            Identity = $"{role}@{Environment.MachineName}:{Environment.ProcessId}",
        };

        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            options.ApiKey = config.ApiKey;

            // Required even when empty: TlsOptions must be non-null for TLS at all, and an API
            // key over plaintext is a credential leak Cloud rejects. Domain is carried here too,
            // because a bare `new()` drops tls.serverName and the handshake then fails silently.
            options.Tls = new()
            {
                Domain = string.IsNullOrEmpty(config.Tls.ServerName) ? null : config.Tls.ServerName,
            };
        }
        else if (!string.IsNullOrEmpty(config.Tls.CertPath) || !string.IsNullOrEmpty(config.Tls.KeyPath))
        {
            // Both-or-neither, same rule and same message as the Go original.
            if (string.IsNullOrEmpty(config.Tls.CertPath) || string.IsNullOrEmpty(config.Tls.KeyPath))
            {
                throw new ArgumentException("tls.certPath and tls.keyPath must be set together");
            }

            // .NET's TlsOptions wants PEM bytes. The config keeps paths, so config.local.yaml
            // stays readable, and the reading happens here.
            options.Tls = new()
            {
                ClientCert = await File.ReadAllBytesAsync(config.Tls.CertPath).ConfigureAwait(false),
                ClientPrivateKey = await File.ReadAllBytesAsync(config.Tls.KeyPath).ConfigureAwait(false),
                ServerRootCACert = string.IsNullOrEmpty(config.Tls.ServerCaPath)
                    ? null
                    : await File.ReadAllBytesAsync(config.Tls.ServerCaPath).ConfigureAwait(false),
                Domain = string.IsNullOrEmpty(config.Tls.ServerName) ? null : config.Tls.ServerName,
            };
        }

        return await TemporalClient.ConnectAsync(options).ConfigureAwait(false);
    }
}

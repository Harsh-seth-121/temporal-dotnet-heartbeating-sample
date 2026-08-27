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
    /// The process's single runtime. Passing null here is how metrics get silently
    /// lost, so it is required rather than optional — see <c>ReproRuntime</c>.
    /// </param>
    /// <param name="role">worker / loadgen / starter / replay. Becomes part of the client identity.</param>
    /// <param name="loggerFactory">Logger factory for SDK-level logging.</param>
    public static async Task<ITemporalClient> ConnectAsync(
        ReproConfig config, TemporalRuntime runtime, string role, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = new TemporalClientConnectOptions(config.Address)
        {
            Namespace = config.Namespace,
            Runtime = runtime,
            LoggerFactory = loggerFactory,

            // Default identity is pid@hostname, which tells you nothing when four
            // processes share a task queue. `temporal workflow describe` prints this.
            Identity = $"{role}@{Environment.MachineName}:{Environment.ProcessId}",
        };

        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            options.ApiKey = config.ApiKey;

            // REQUIRED even though it is empty. TlsOptions must be non-null for TLS
            // to be enabled at all; an API key over a plaintext connection is both a
            // credential leak and rejected by Cloud.
            options.Tls = new();
        }
        else if (!string.IsNullOrEmpty(config.Tls.CertPath) || !string.IsNullOrEmpty(config.Tls.KeyPath))
        {
            // Both-or-neither, same rule and same message as the Go original.
            if (string.IsNullOrEmpty(config.Tls.CertPath) || string.IsNullOrEmpty(config.Tls.KeyPath))
            {
                throw new ArgumentException("tls.certPath and tls.keyPath must be set together");
            }

            // Go took file paths straight through; .NET's TlsOptions wants PEM BYTES.
            // The config keeps paths (so config.local.yaml stays readable) and the
            // reading happens here.
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

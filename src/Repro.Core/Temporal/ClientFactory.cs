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
    /// <param name="role">
    /// worker / loadgen / starter / replay. Becomes part of the client identity.
    /// <para>
    /// MUST BE DISTINCT PER CLIENT WITHIN A PROCESS, which stopped being automatic when the
    /// local-activity case introduced a second namespace. Identity is
    /// <c>role@machine:pid</c>, so two clients in one process sharing a role produce a
    /// byte-identical identity and `temporal workflow describe` can no longer tell you which
    /// one is holding a run -- which is the entire reason this field is set. The worker and
    /// loadgen pass <c>worker-la</c> and <c>loadgen-la</c> for their second client.
    /// </para>
    /// </param>
    /// <param name="loggerFactory">Logger factory for SDK-level logging.</param>
    /// <param name="namespaceOverride">
    /// Connect to this namespace instead of <see cref="ReproConfig.Namespace"/>.
    /// <para>
    /// A namespace is a CLIENT property and a worker binds one client, so this is what makes
    /// <c>WorkflowLocalActivity</c> able to live somewhere else. It has to: the setting that
    /// case depends on, <c>history.workflowTaskHeartbeatTimeout</c>, is declared server-side as
    /// NewNamespaceDurationSetting and filters by namespace and nothing finer.
    /// </para>
    /// <para>
    /// An OVERRIDE rather than a second connect method, so the API key, TLS and runtime paths
    /// below stay shared. Those are the parts that are easy to get subtly wrong twice.
    /// </para>
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

            // Default identity is pid@hostname, which tells you nothing when four
            // processes share a task queue. `temporal workflow describe` prints this.
            Identity = $"{role}@{Environment.MachineName}:{Environment.ProcessId}",
        };

        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            options.ApiKey = config.ApiKey;

            // REQUIRED even when there is nothing to put in it. TlsOptions must be
            // non-null for TLS to be enabled at all; an API key over a plaintext
            // connection is both a credential leak and rejected by Cloud.
            //
            // Domain is carried in here too, exactly as the Go original did in this
            // same branch (config.go:140). `new()` on its own dropped tls.serverName,
            // and an API key against a host whose cert CN differs from config.address
            // then fails the handshake with nothing pointing back at this line.
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

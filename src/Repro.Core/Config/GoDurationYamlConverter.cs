using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Repro.Core.Config;

/// <summary>
/// Teaches YamlDotNet to read <c>150ms</c> / <c>1m30s</c> into a <see cref="TimeSpan"/>.
/// </summary>
/// <remarks>
/// Without this, YamlDotNet parses TimeSpan in its own format and
/// <c>latency: 150ms</c> fails with an unhelpful conversion error.
/// </remarks>
public sealed class GoDurationYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(TimeSpan) || type == typeof(TimeSpan?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        ArgumentNullException.ThrowIfNull(parser);
        var scalar = parser.Consume<Scalar>();
        if (scalar.Value.Length == 0)
        {
            // GOTCHA: `latency:` with nothing after it is an EMPTY SCALAR, not an
            // absent key — the property is still assigned, so returning TimeSpan.Zero
            // here overwrote the POCO default with 0s and the run looked configured.
            // GoDuration.Parse("") is itself an error, and the loader's whole policy is
            // that a config mistake stops the process instead of picking a value.
            throw new YamlException(
                scalar.Start,
                scalar.End,
                "empty duration. A key with no value REPLACES the default with 0s rather than leaving " +
                "it alone. Write \"0s\" if you mean zero, or delete the key.");
        }

        return GoDuration.Parse(scalar.Value);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(emitter);
        emitter.Emit(new Scalar(value is TimeSpan d ? GoDuration.ToGoString(d) : "0s"));
    }
}

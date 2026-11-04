using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SFerrand.Http.Internal;

/// <summary>
/// Gets the serializer metadata of a type from the options, the way <see cref="JsonSerializer"/> does internally, but through
/// <see cref="JsonTypeInfo"/> overloads that carry no trimming or native-AOT warning: with a source-generated context (<c>options.TypeInfoResolver</c>) the
/// library works trimmed and ahead-of-time compiled; without one it uses reflection, as before.
/// </summary>
internal static class JsonMetadata
{
    public static JsonTypeInfo Get(JsonSerializerOptions options, Type type)
    {
        Configure(options);
        return options.GetTypeInfo(type);
    }

    public static JsonTypeInfo<T> Get<T>(JsonSerializerOptions options)
        => (JsonTypeInfo<T>)Get(options, typeof(T));

    /// <summary>
    /// <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> needs options that have been configured (a resolver in place). <see cref="JsonSerializer"/> does it
    /// by itself at its first use; here it is done explicitly. Without a resolver, reflection is used when the application allows it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No resolver was supplied and reflection is turned off (trimmed or ahead-of-time compiled application).</exception>
    private static void Configure(JsonSerializerOptions options)
    {
        if (options.IsReadOnly)
            return;

        if (options.TypeInfoResolver is null)
        {
            if (!JsonSerializer.IsReflectionEnabledByDefault)
            {
                throw new InvalidOperationException(
                    $"Reflection-based JSON is turned off in this application: set {nameof(HttpClientOptions)}.{nameof(HttpClientOptions.JsonSerializerOptions)}.{nameof(JsonSerializerOptions.TypeInfoResolver)} to a source-generated context.");
            }

            options.TypeInfoResolver = ReflectionResolver();
        }

        options.MakeReadOnly();
    }

    // IsReflectionEnabledByDefault is a feature switch: when it is off at publish time the trimmer removes the only call site, so nothing reflective is kept.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only reached when JsonSerializer.IsReflectionEnabledByDefault is true.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only reached when JsonSerializer.IsReflectionEnabledByDefault is true.")]
    private static IJsonTypeInfoResolver ReflectionResolver() => new DefaultJsonTypeInfoResolver();
}

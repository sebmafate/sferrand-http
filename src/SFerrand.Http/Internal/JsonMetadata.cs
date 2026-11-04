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
    /// by itself at its first use; here it is done explicitly. With reflection available and no resolver supplied, the default one is used.
    /// </summary>
    private static void Configure(JsonSerializerOptions options)
    {
        if (!options.IsReadOnly)
            options.MakeReadOnly(populateMissingResolver: true);
    }
}

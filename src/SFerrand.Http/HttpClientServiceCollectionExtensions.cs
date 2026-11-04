using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace SFerrand.Http;

/// <summary>Registration of typed clients derived from <see cref="HttpClientBase"/> with the safe defaults.</summary>
public static class HttpClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a typed client with its own options and an <see cref="HttpClient"/> configured the safe way: <b>no automatic redirects</b> (the client follows them
    /// itself, applying its origin rules at each hop) and automatic decompression. The options are validated, and a bad configuration makes the host fail at start-up.
    /// </summary>
    /// <typeparam name="TClient">
    /// The client. It needs a public constructor taking <c>IOptions&lt;HttpClientOptions&gt;</c>, an <see cref="IHttpClientFactory"/> and an <c>ILogger&lt;TClient&gt;</c>, as in
    /// <see cref="HttpClientBase"/>.
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options of this client (and only this one: each client has its own).</param>
    /// <returns>
    /// The builder of the underlying <see cref="HttpClient"/>, to add handlers (resilience, logging, certificates...). The primary handler is already set; to replace it
    /// keep <c>AllowAutoRedirect = false</c>, otherwise redirects are followed before the client can check them.
    /// </returns>
    /// <example>
    /// <code>
    /// services.AddSFerrandHttpClient&lt;WidgetsClient&gt;(o =>
    /// {
    ///     o.BaseUri = "https://api.example.com/v2";
    ///     o.TokenFactory = () => Environment.GetEnvironmentVariable("API_TOKEN");
    /// });
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddSFerrandHttpClient<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TClient>(
        this IServiceCollection services,
        Action<HttpClientOptions> configure)
        where TClient : HttpClientBase
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var name = typeof(TClient).FullName ?? typeof(TClient).Name;

        services.AddOptions<HttpClientOptions>(name)
            .Configure(configure)
            .PostConfigure(options => options.HttpClientName ??= name)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HttpClientOptions>, HttpClientOptionsValidator>());

        services.AddTransient(provider =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<HttpClientOptions>>().Get(name);
            return ActivatorUtilities.CreateInstance<TClient>(provider, Options.Create(options));
        });

        return services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
    }
}

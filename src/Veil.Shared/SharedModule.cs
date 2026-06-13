using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using System.Text;
using Veil.Shared.Obfuscation;
using Wiaoj.Modulith;
using Wiaoj.Primitives.Obfuscation;

namespace Veil.Shared;
public sealed class SharedModule : IModule {
    public string Name => nameof(SharedModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<Observability.MetricsCollector>();

        services.Configure<ObfuscationOptions>(configuration.GetSection(ObfuscationOptions.SectionName));
        services.AddSingleton<IObfuscator>(sp => new FeistelBase62Obfuscator(
            new FeistelObfuscatorOptions {
                Seed = Encoding.UTF8.GetBytes(sp.GetRequiredService<IOptions<ObfuscationOptions>>().Value.Seed),
            }));

#if DEBUG
        services.AddSingleton(typeof(IObfuscator<>), typeof(TransparentObfuscator<>));
#else
        services.AddSingleton(typeof(IObfuscator<>), typeof(Obfuscator<>));
#endif
    }
}
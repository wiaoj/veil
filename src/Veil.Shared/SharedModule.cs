using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text;
using Veil.Shared.Obfuscation;
using Wiaoj.Modulith;
using Wiaoj.Primitives.Obfuscation;

namespace Veil.Shared; 
public sealed class SharedModule : IModule {
    public string Name => nameof(SharedModule);

    public void Register(IServiceCollection services, IConfiguration configuration) {
        services.TryAddSingleton(TimeProvider.System);

        string obfuscationSeed = configuration["Obfuscation:Seed"]
            ?? "vaultex-iam-server-dev-obfuscation-seed";
        services.AddSingleton<IObfuscator>(_ => new FeistelBase62Obfuscator(
            new FeistelObfuscatorOptions {
                Seed = Encoding.UTF8.GetBytes(obfuscationSeed),
            }));

#if DEBUG
        services.AddSingleton(typeof(IObfuscator<>), typeof(TransparentObfuscator<>));
#else
        services.AddSingleton(typeof(IObfuscator<>), typeof(Obfuscator<>));
#endif
    }
}
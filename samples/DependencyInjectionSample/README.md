# DependencyInjectionSample

This sample demonstrates how to integrate TokenRateGate with Microsoft.Extensions.DependencyInjection, which is the standard DI container used in ASP.NET Core and .NET applications.

## What You'll Learn

- How to register TokenRateGate with `IServiceCollection`
- Using `AddTokenRateGate()` extension method
- Injecting `ITokenRateGate` into your services
- Configuring TokenRateGate via the options pattern

## Key Concepts

### Registration

```csharp
services.AddTokenRateGate(options =>
{
    options.TokenLimit = 100_000;
    options.WindowSeconds = 60;
    options.MaxConcurrentRequests = 5;
});
```

### Injection

```csharp
public class LlmService
{
    private readonly ITokenRateGate _rateGate;

    public LlmService(ITokenRateGate rateGate)
    {
        _rateGate = rateGate;
    }
}
```

## Running the Sample

```bash
cd samples/DependencyInjectionSample
dotnet run
```

## Use Cases

This pattern is ideal for:
- ASP.NET Core applications
- Worker services
- Console applications using Generic Host
- Any application using Microsoft.Extensions.DependencyInjection

## Next Steps

- Check out **MultiTenantSample** for managing multiple rate limit pools
- See **BasicUsage** for manual integration without DI

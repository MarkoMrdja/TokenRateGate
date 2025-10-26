# MultiTenantSample

Demonstrates managing multiple TokenRateGate instances for different tenants or API keys, where each tenant has independent rate limits.

## What You'll Learn

- Using `ITokenRateGateFactory` to create named instances
- Managing separate rate limit pools per tenant
- Tenant isolation and independent tracking

## Key Concepts

```csharp
services.AddTokenRateGateFactory();

// Create rate gates per tenant
var rateGateA = factory.Create("TenantA", optionsA);
var rateGateB = factory.Create("TenantB", optionsB);
```

## Running

```bash
cd samples/MultiTenantSample
dotnet run
```

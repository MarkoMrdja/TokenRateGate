# Azure OpenAI Basic Usage Sample

This sample demonstrates basic usage of TokenRateGate with Azure OpenAI Service.

## Features Demonstrated

- **Azure OpenAI Client Setup**: Configure authentication using API key or managed identity
- **Rate-Limited Chat Completions**: Execute chat completions with automatic token rate limiting
- **Token Estimation**: Use tiktoken for accurate token counting
- **Usage Monitoring**: Track token usage, efficiency, and capacity
- **Streaming Support**: Stream responses while maintaining rate limits

## Prerequisites

1. **Azure OpenAI Resource**: You need an Azure OpenAI resource deployed in Azure
2. **Deployment**: Create a deployment (e.g., GPT-4, GPT-3.5-Turbo) in your Azure OpenAI resource
3. **Authentication**: Either:
   - API key from your Azure OpenAI resource, OR
   - Managed identity configured (for production scenarios)

## Configuration

You can configure the sample using one of three methods (in priority order):

### Method 1: Environment Variables

```bash
# Required
export AZURE_OPENAI_ENDPOINT="https://YOUR-RESOURCE-NAME.openai.azure.com/"
export AZURE_OPENAI_DEPLOYMENT="your-deployment-name"

# Optional - defaults to "gpt-4" if not specified
export AZURE_OPENAI_MODEL="gpt-4"

# For API key authentication (omit to use managed identity)
export AZURE_OPENAI_API_KEY="your-api-key"
```

### Method 2: appsettings.Development.json (Recommended for Development)

Create or edit `appsettings.Development.json`:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://YOUR-RESOURCE-NAME.openai.azure.com/",
    "ApiKey": "your-api-key-here",
    "DeploymentName": "gpt-4",
    "ModelName": "gpt-4"
  },
  "RateLimits": {
    "TokenLimit": 90000,
    "RequestLimit": 300
  }
}
```

### Method 3: appsettings.json (Baseline Configuration)

Edit `appsettings.json` for default values (don't commit secrets here):

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://YOUR-RESOURCE-NAME.openai.azure.com/",
    "ApiKey": "",
    "DeploymentName": "gpt-4",
    "ModelName": "gpt-4"
  },
  "RateLimits": {
    "TokenLimit": 90000,
    "RequestLimit": 300
  }
}
```

**Note**: Environment variables take precedence over appsettings files. `appsettings.Development.json` is ignored by git (listed in `.gitignore`), making it safe for local development secrets.

### Finding Your Configuration

1. **Endpoint**: Azure Portal → Your Azure OpenAI Resource → Keys and Endpoint → Endpoint
2. **Deployment Name**: Azure Portal → Your Azure OpenAI Resource → Model deployments → Deployment name
3. **Model Name**: The underlying model (e.g., "gpt-4", "gpt-35-turbo", "gpt-4o")
4. **API Key**: Azure Portal → Your Azure OpenAI Resource → Keys and Endpoint → Key 1 or Key 2

### Rate Limits

Check your actual rate limits in Azure Portal:
- Navigate to: Your Azure OpenAI Resource → Quotas → View quotas
- Note the TPM (Tokens Per Minute) and RPM (Requests Per Minute) limits
- Update the values in the sample code accordingly

## Running the Sample

```bash
# From the sample directory
dotnet run

# Or from the solution root
dotnet run --project samples/AzureOpenAI.BasicUsage/AzureOpenAI.BasicUsage.csproj
```

## Understanding the Code

### 1. Azure OpenAI Client Creation

```csharp
// Option A: API Key authentication
var azureClient = new AzureOpenAIClient(
    new Uri(endpoint),
    new AzureKeyCredential(apiKey));

// Option B: Managed Identity (recommended for production)
var azureClient = new AzureOpenAIClient(
    new Uri(endpoint),
    new DefaultAzureCredential());
```

### 2. TokenRateGate Configuration

```csharp
var options = new TokenRateGateOptions
{
    TokenLimit = 90_000,  // Your Azure deployment's TPM limit
    WindowSeconds = 60,
    MaxRequestsPerMinute = 300,  // Your Azure deployment's RPM limit
    OutputEstimationStrategy = OutputEstimationStrategy.FixedMultiplier
};

var rateGate = new TokenRateGate.Core.TokenRateGate(
    Microsoft.Extensions.Options.Options.Create(options),
    logger);
```

### 3. Azure Chat Helper

The `AzureChatHelper` handles token estimation and usage extraction:

```csharp
var helper = new AzureChatHelper(
    deploymentName: "my-gpt-4-deployment",  // Azure deployment name
    modelName: "gpt-4",                      // Model for token counting
    options,
    logger);
```

### 4. Rate-Limited Chat Completion

```csharp
var response = await rateGate.ExecuteAzureChatAsync(
    chatClient,
    messages,
    helper);
```

This automatically:
- Estimates tokens using tiktoken
- Reserves capacity (waits if needed)
- Executes the API call
- Records actual usage
- Releases the reservation

## Key Differences from OpenAI Integration

1. **Deployment vs Model**: Azure uses deployment names instead of model names
2. **Authentication**: Azure supports API key + managed identity (DefaultAzureCredential)
3. **Endpoints**: Azure has regional endpoints (e.g., eastus.openai.azure.com)
4. **Rate Limits**: Set per deployment in Azure Portal, not per organization

## Expected Output

```
=== Azure OpenAI Basic Usage Sample ===

Configuration:
  Endpoint: https://your-resource.openai.azure.com/
  Deployment: gpt-4
  Model: gpt-4
  Rate Limits: 90000 tokens/min, 300 requests/min

Using API key authentication

TokenRateGate configured and ready

Sending chat completion request...

Response from Azure OpenAI:
Azure OpenAI is a managed service that provides access to OpenAI's powerful language models through Azure infrastructure...

Usage Statistics:
  Current Usage: 245 tokens
  Reserved Tokens: 0
  Available Capacity: 88755 tokens
  Usage Percentage: 0.3%
  Active Reservations: 0

=== Streaming Example ===

Streaming response: 1 2 3 4 5

Streaming completed successfully!

Final Statistics:
  Total Usage: 398 tokens
  Active Requests: 0

=== Sample Completed ===
```

## Troubleshooting

### "Unauthorized" or "InvalidApiKey" errors
- Verify your API key is correct
- Check that the endpoint matches your resource
- Ensure your Azure OpenAI resource is active

### "DeploymentNotFound" errors
- Verify the deployment name exists in your resource
- Check spelling of the deployment name
- Ensure the deployment is fully deployed and active

### Rate limit errors (429)
- Verify your rate limit configuration matches Azure Portal settings
- Consider lowering the configured limits slightly for safety margin
- Check if other applications are using the same deployment

### Managed Identity issues
- Ensure your application has appropriate RBAC roles
- Required role: "Cognitive Services OpenAI User" or "Cognitive Services OpenAI Contributor"
- For local development, run `az login` to authenticate

## Production Considerations

1. **Use Managed Identity**: Avoid API keys in production
2. **Configure Rate Limits Accurately**: Match Azure Portal quotas
3. **Set Safety Buffers**: Reserve tokens to avoid hitting exact limits
4. **Monitor Usage**: Use Azure Monitor for deployment metrics
5. **Handle Errors**: Implement retry logic for transient failures
6. **Secure Configuration**: Use Azure Key Vault for sensitive settings

## Related Samples

- `OpenAI.BasicUsage`: Basic usage with OpenAI (non-Azure)
- `OpenAI.StreamingChat`: Advanced streaming examples
- `OpenAI.BatchProcessing`: Batch request handling

## Resources

- [Azure OpenAI Documentation](https://learn.microsoft.com/en-us/azure/ai-services/openai/)
- [Azure OpenAI .NET SDK](https://github.com/Azure/azure-sdk-for-net/tree/main/sdk/openai)
- [Azure Identity Documentation](https://learn.microsoft.com/en-us/dotnet/api/azure.identity)
- [TokenRateGate Documentation](../../README.md)

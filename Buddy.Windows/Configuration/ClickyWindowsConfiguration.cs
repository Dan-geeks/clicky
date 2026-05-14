using System;

namespace Buddy.Windows.Configuration;

public static class BuddyWindowsConfiguration
{
    public const string WorkerBaseUrlEnvironmentVariableName = "BUDDY_WORKER_BASE_URL";
    public const string ChatProviderEnvironmentVariableName = "BUDDY_AI_PROVIDER";
    public const string ChatModelEnvironmentVariableName = "BUDDY_AI_MODEL";
    public const string FastChatProviderEnvironmentVariableName = "BUDDY_FAST_AI_PROVIDER";
    public const string FastChatModelEnvironmentVariableName = "BUDDY_FAST_AI_MODEL";
    public const string EnableVoiceEnvironmentVariableName = "BUDDY_ENABLE_VOICE";
    public const string EnableScrollContextEnvironmentVariableName = "BUDDY_ENABLE_SCROLL_CONTEXT";
    public const string EnableTextContextEnvironmentVariableName = "BUDDY_ENABLE_TEXT_CONTEXT";
    public const string ComputerUseModelEnvironmentVariableName = "BUDDY_COMPUTER_USE_MODEL";
    public const string ComputerUseMaxTurnsEnvironmentVariableName = "BUDDY_COMPUTER_USE_MAX_TURNS";

    private const string DefaultChatProvider = "gemini";
    private const string DefaultAnthropicModel = "claude-sonnet-4-6";
    private const string DefaultOpenAIModel = "gpt-4o-mini";
    private const string DefaultGrokModel = "grok-4";
    private const string DefaultGeminiModel = "gemini-3.1-flash-lite-preview";
    private const string DefaultComputerUseModel = "gemini-2.5-computer-use-preview-10-2025";
    private const int DefaultComputerUseMaxTurns = 16;
    private const string PlaceholderWorkerBaseUrl = "https://your-worker-name.your-subdomain.workers.dev";

    public static bool IsWorkerBaseUrlConfigured
    {
        get
        {
            string? workerBaseUrl = GetConfiguredEnvironmentVariable(WorkerBaseUrlEnvironmentVariableName);
            return !string.IsNullOrWhiteSpace(workerBaseUrl);
        }
    }

    public static bool IsVoiceEnabled
    {
        get
        {
            string? configuredVoiceEnabled = GetConfiguredEnvironmentVariable(EnableVoiceEnvironmentVariableName);
            string normalizedVoiceEnabled = configuredVoiceEnabled?.Trim().ToLowerInvariant() ?? "";

            return normalizedVoiceEnabled is "1" or "true" or "yes" or "on";
        }
    }

    public static bool IsScrollContextEnabled
    {
        get
        {
            string? configuredScrollContextEnabled =
                GetConfiguredEnvironmentVariable(EnableScrollContextEnvironmentVariableName);
            string normalizedScrollContextEnabled =
                configuredScrollContextEnabled?.Trim().ToLowerInvariant() ?? "";

            return normalizedScrollContextEnabled is not ("0" or "false" or "no" or "off");
        }
    }

    public static bool IsTextContextEnabled
    {
        get
        {
            string? configuredTextContextEnabled =
                GetConfiguredEnvironmentVariable(EnableTextContextEnvironmentVariableName);
            string normalizedTextContextEnabled =
                configuredTextContextEnabled?.Trim().ToLowerInvariant() ?? "";

            return normalizedTextContextEnabled is not ("0" or "false" or "no" or "off");
        }
    }

    public static Uri CreateWorkerEndpointUri(string endpointPath)
    {
        string workerBaseUrl = GetConfiguredEnvironmentVariable(WorkerBaseUrlEnvironmentVariableName)
            ?? PlaceholderWorkerBaseUrl;
        string normalizedWorkerBaseUrl = workerBaseUrl.Trim().TrimEnd('/');
        string normalizedEndpointPath = endpointPath.TrimStart('/');

        return new Uri($"{normalizedWorkerBaseUrl}/{normalizedEndpointPath}");
    }

    public static string GetChatProvider()
    {
        string? configuredChatProvider = GetConfiguredEnvironmentVariable(ChatProviderEnvironmentVariableName);
        string normalizedChatProvider = configuredChatProvider?.Trim().ToLowerInvariant() ?? "";

        return normalizedChatProvider switch
        {
            "openai" => "openai",
            "grok" => "grok",
            "xai" => "grok",
            "x.ai" => "grok",
            "gemini" => "gemini",
            "google" => "gemini",
            _ => DefaultChatProvider
        };
    }

    public static string GetChatModel(string chatProvider)
    {
        string? configuredChatModel = GetConfiguredEnvironmentVariable(ChatModelEnvironmentVariableName);

        if (!string.IsNullOrWhiteSpace(configuredChatModel))
        {
            return configuredChatModel.Trim();
        }

        return chatProvider switch
        {
            "openai" => DefaultOpenAIModel,
            "grok" => DefaultGrokModel,
            "gemini" => DefaultGeminiModel,
            _ => DefaultAnthropicModel
        };
    }

    public static string GetFastChatProvider(string fallbackChatProvider)
    {
        string? configuredFastChatProvider = GetConfiguredEnvironmentVariable(FastChatProviderEnvironmentVariableName);
        string normalizedFastChatProvider = configuredFastChatProvider?.Trim().ToLowerInvariant() ?? "";

        return normalizedFastChatProvider switch
        {
            "openai" => "openai",
            "grok" => "grok",
            "xai" => "grok",
            "x.ai" => "grok",
            "gemini" => "gemini",
            "google" => "gemini",
            "anthropic" => "anthropic",
            _ => fallbackChatProvider
        };
    }

    public static string GetComputerUseModel()
    {
        string? configuredComputerUseModel = GetConfiguredEnvironmentVariable(ComputerUseModelEnvironmentVariableName);

        return string.IsNullOrWhiteSpace(configuredComputerUseModel)
            ? DefaultComputerUseModel
            : configuredComputerUseModel.Trim();
    }

    public static int GetComputerUseMaxTurns()
    {
        string? configuredComputerUseMaxTurns = GetConfiguredEnvironmentVariable(ComputerUseMaxTurnsEnvironmentVariableName);

        if (int.TryParse(configuredComputerUseMaxTurns, out int parsedComputerUseMaxTurns)
            && parsedComputerUseMaxTurns > 0
            && parsedComputerUseMaxTurns <= 64)
        {
            return parsedComputerUseMaxTurns;
        }

        return DefaultComputerUseMaxTurns;
    }

    public static string GetFastChatModel(string fastChatProvider)
    {
        string? configuredFastChatModel = GetConfiguredEnvironmentVariable(FastChatModelEnvironmentVariableName);

        if (!string.IsNullOrWhiteSpace(configuredFastChatModel))
        {
            return configuredFastChatModel.Trim();
        }

        return GetChatModel(fastChatProvider);
    }

    private static string? GetConfiguredEnvironmentVariable(string environmentVariableName)
    {
        string? processValue = Environment.GetEnvironmentVariable(
            environmentVariableName,
            EnvironmentVariableTarget.Process);

        if (!string.IsNullOrWhiteSpace(processValue))
        {
            return processValue;
        }

        string? userValue = Environment.GetEnvironmentVariable(
            environmentVariableName,
            EnvironmentVariableTarget.User);

        if (!string.IsNullOrWhiteSpace(userValue))
        {
            return userValue;
        }

        string? machineValue = Environment.GetEnvironmentVariable(
            environmentVariableName,
            EnvironmentVariableTarget.Machine);

        return string.IsNullOrWhiteSpace(machineValue)
            ? null
            : machineValue;
    }
}

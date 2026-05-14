using System;
using System.Collections.Generic;
using Buddy.Windows.Diagnostics;

namespace Buddy.Windows.Configuration;

public static class BuddyRuntimeModelSelection
{
    private const string DefaultComputerUseModel = "gemini-2.5-computer-use-preview-10-2025";
    private static readonly object ModelSelectionLock = new();
    private static readonly IReadOnlyList<BuddyChatModelOption> ChatModelOptionsInternal = CreateChatModelOptions();
    private static int selectedChatModelIndex = ResolveInitialChatModelIndex(ChatModelOptionsInternal);

    public static event EventHandler<BuddyRuntimeModelSelectionChangedEventArgs>? ModelSelectionChanged;

    public static IReadOnlyList<BuddyChatModelOption> ChatModelOptions => ChatModelOptionsInternal;

    public static BuddyChatModelOption CurrentChatModel
    {
        get
        {
            lock (ModelSelectionLock)
            {
                return ChatModelOptionsInternal[selectedChatModelIndex];
            }
        }
    }

    public static int CurrentChatModelNumber
    {
        get
        {
            lock (ModelSelectionLock)
            {
                return selectedChatModelIndex + 1;
            }
        }
    }

    public static int ChatModelCount => ChatModelOptionsInternal.Count;

    public static string CurrentChatProvider => CurrentChatModel.Provider;

    public static string CurrentChatModelName => CurrentChatModel.Model;

    public static string CurrentChatDisplayName => CurrentChatModel.DisplayName;

    public static string CurrentComputerUseModel => BuddyWindowsConfiguration.GetComputerUseModel();

    public static string CurrentComputerUseDisplayName
    {
        get
        {
            string computerUseModel = CurrentComputerUseModel;

            return computerUseModel.Equals(DefaultComputerUseModel, StringComparison.OrdinalIgnoreCase)
                ? "Gemini 2.5 Computer Use"
                : FormatModelDisplayName(computerUseModel);
        }
    }

    public static BuddyRuntimeModelSelectionChangedEventArgs CreateSnapshot()
    {
        return new BuddyRuntimeModelSelectionChangedEventArgs(
            CurrentChatModel,
            CurrentChatModelNumber,
            ChatModelCount,
            CurrentComputerUseModel,
            CurrentComputerUseDisplayName);
    }

    public static void CycleChatModel()
    {
        BuddyRuntimeModelSelectionChangedEventArgs snapshot;

        lock (ModelSelectionLock)
        {
            selectedChatModelIndex = (selectedChatModelIndex + 1) % ChatModelOptionsInternal.Count;
            snapshot = CreateSnapshot();
        }

        BuddyLog.Info(
            $"Ask Buddy model changed to {snapshot.ChatModel.DisplayName} ({snapshot.ChatModel.Provider}/{snapshot.ChatModel.Model}).");
        ModelSelectionChanged?.Invoke(null, snapshot);
    }

    public static void SelectChatModel(int chatModelIndex)
    {
        if (chatModelIndex < 0 || chatModelIndex >= ChatModelOptionsInternal.Count)
        {
            return;
        }

        BuddyRuntimeModelSelectionChangedEventArgs snapshot;

        lock (ModelSelectionLock)
        {
            if (selectedChatModelIndex == chatModelIndex)
            {
                return;
            }

            selectedChatModelIndex = chatModelIndex;
            snapshot = CreateSnapshot();
        }

        BuddyLog.Info(
            $"Ask Buddy model selected: {snapshot.ChatModel.DisplayName} ({snapshot.ChatModel.Provider}/{snapshot.ChatModel.Model}).");
        ModelSelectionChanged?.Invoke(null, snapshot);
    }

    private static IReadOnlyList<BuddyChatModelOption> CreateChatModelOptions()
    {
        List<BuddyChatModelOption> configuredChatModelOptions = new()
        {
            new BuddyChatModelOption("Gemini 3.1 Flash Lite", "gemini", "gemini-3.1-flash-lite-preview"),
            new BuddyChatModelOption("Gemini 2.5 Flash", "gemini", "gemini-2.5-flash"),
            new BuddyChatModelOption("Gemini Flash Lite Latest", "gemini", "gemini-flash-lite-latest")
        };

        string configuredChatProvider = BuddyWindowsConfiguration.GetChatProvider();
        string configuredChatModel = BuddyWindowsConfiguration.GetChatModel(configuredChatProvider);

        foreach (BuddyChatModelOption configuredChatModelOption in configuredChatModelOptions)
        {
            if (configuredChatModelOption.Matches(configuredChatProvider, configuredChatModel))
            {
                return configuredChatModelOptions;
            }
        }

        configuredChatModelOptions.Insert(
            0,
            new BuddyChatModelOption(
                FormatModelDisplayName(configuredChatModel),
                configuredChatProvider,
                configuredChatModel));

        return configuredChatModelOptions;
    }

    private static int ResolveInitialChatModelIndex(IReadOnlyList<BuddyChatModelOption> chatModelOptions)
    {
        string configuredChatProvider = BuddyWindowsConfiguration.GetChatProvider();
        string configuredChatModel = BuddyWindowsConfiguration.GetChatModel(configuredChatProvider);

        for (int chatModelIndex = 0; chatModelIndex < chatModelOptions.Count; chatModelIndex++)
        {
            if (chatModelOptions[chatModelIndex].Matches(configuredChatProvider, configuredChatModel))
            {
                return chatModelIndex;
            }
        }

        return 0;
    }

    private static string FormatModelDisplayName(string modelName)
    {
        string trimmedModelName = modelName.Trim();

        if (string.IsNullOrWhiteSpace(trimmedModelName))
        {
            return "Custom model";
        }

        return trimmedModelName
            .Replace("gemini-", "Gemini ", StringComparison.OrdinalIgnoreCase)
            .Replace("-", " ", StringComparison.Ordinal)
            .Replace("preview", "Preview", StringComparison.OrdinalIgnoreCase)
            .Replace("flash", "Flash", StringComparison.OrdinalIgnoreCase)
            .Replace("lite", "Lite", StringComparison.OrdinalIgnoreCase)
            .Replace("computer use", "Computer Use", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BuddyChatModelOption
{
    public BuddyChatModelOption(string displayName, string provider, string model)
    {
        DisplayName = displayName;
        Provider = provider;
        Model = model;
    }

    public string DisplayName { get; }

    public string Provider { get; }

    public string Model { get; }

    public bool Matches(string provider, string model)
    {
        return Provider.Equals(provider, StringComparison.OrdinalIgnoreCase)
            && Model.Equals(model, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BuddyRuntimeModelSelectionChangedEventArgs : EventArgs
{
    public BuddyRuntimeModelSelectionChangedEventArgs(
        BuddyChatModelOption chatModel,
        int chatModelNumber,
        int chatModelCount,
        string computerUseModel,
        string computerUseDisplayName)
    {
        ChatModel = chatModel;
        ChatModelNumber = chatModelNumber;
        ChatModelCount = chatModelCount;
        ComputerUseModel = computerUseModel;
        ComputerUseDisplayName = computerUseDisplayName;
    }

    public BuddyChatModelOption ChatModel { get; }

    public int ChatModelNumber { get; }

    public int ChatModelCount { get; }

    public string ComputerUseModel { get; }

    public string ComputerUseDisplayName { get; }
}

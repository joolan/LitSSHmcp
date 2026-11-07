using System.Windows;
using LitSSHmcp.App.Views;
using LitSSHmcp.Core.Models;
using LitSSHmcp.Core.Services.Storage;

namespace LitSSHmcp.App.Services.Pet;

public enum PetAiState
{
    Idle,
    Running,
    WaitingApproval,
    Completed,
    Failed
}

public static class PetService
{
    private static readonly object Gate = new();
    private static PetWindow? _window;
    private static string? _windowAssetId;
    private static PetAiState _aiState = PetAiState.Idle;
    private static long? _aiSessionId;

    public static async Task ApplyAsync(IConfigService configService)
    {
        try
        {
            var config = await configService.LoadConfigAsync();
            await ApplyAsync(config.Pet ?? new PetConfig());
        }
        catch
        {
            // 配置读取失败时不影响主程序
        }
    }

    public static async Task ApplyAsync(PetConfig config)
    {
        if (!config.Enabled)
        {
            Close();
            return;
        }

        PetWindow? existing;
        string? existingAssetId;
        lock (Gate)
        {
            existing = _window;
            existingAssetId = _windowAssetId;
        }
        if (existing is not null && existingAssetId == config.AssetId)
        {
            await InvokeAsync(() =>
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_window, existing))
                        existing.ApplyConfig(config);
                }
            });
            return;
        }

        var pack = await new PetAssetService().LoadAsync(config.AssetId);
        await InvokeAsync(() =>
        {
            PetWindow? previous;
            lock (Gate)
            {
                previous = _window;
                _window = null;
            }
            if (previous is not null)
            {
                try { previous.Close(); } catch { /* ignore */ }
            }

            var window = new PetWindow(config, pack.Frames);
            window.Closed += (_, _) =>
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_window, window))
                    {
                        _window = null;
                        _windowAssetId = null;
                    }
                }
            };
            lock (Gate)
            {
                _window = window;
                _windowAssetId = config.AssetId;
            }
            window.Show();
            window.SetAiState(_aiState, _aiSessionId);
        });
    }

    public static bool IsVisible
    {
        get
        {
            lock (Gate)
                return _window?.IsVisible == true;
        }
    }

    public static void Show()
    {
        PetWindow? window;
        lock (Gate)
            window = _window;
        if (window is null)
            return;
        if (!window.IsVisible)
            window.Show();
        window.Activate();
    }

    public static void Hide()
    {
        PetWindow? window;
        lock (Gate)
            window = _window;
        window?.Hide();
    }

    public static void SetAiState(PetAiState state, long? sessionId = null)
    {
        _aiState = state;
        _aiSessionId = sessionId;
        PetWindow? window;
        lock (Gate)
            window = _window;
        window?.SetAiState(state, sessionId);
    }

    public static void ToggleVisibility()
    {
        if (IsVisible)
            Hide();
        else
            Show();
    }

    public static void ResetPosition()
    {
        PetWindow? window;
        lock (Gate)
            window = _window;
        window?.ResetPosition();
    }

    public static void ToggleMovement()
    {
        PetWindow? window;
        lock (Gate)
            window = _window;
        window?.ToggleMovement();
    }

    public static void Close()
    {
        PetWindow? window;
        lock (Gate)
        {
            window = _window;
            _window = null;
            _windowAssetId = null;
        }
        if (window is null)
            return;
        try { window.Close(); } catch { /* ignore */ }
    }

    private static Task InvokeAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }
}

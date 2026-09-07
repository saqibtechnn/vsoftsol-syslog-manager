namespace VSoftSol.Syslog.Web.Components.DesignSystem;

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A transient notification. <see cref="Id"/> is stable so the host can key the render list.</summary>
public sealed record Toast(Guid Id, ToastLevel Level, string Message, string? Title, TimeSpan Duration);

/// <summary>
/// The one toast surface for the whole product (PHASE_04 design system). Any component
/// raises a toast through this scoped service; <c>ToastHost</c> renders them and every
/// action's "visible confirmation" (UX_STANDARDS.md §5) goes through here.
/// </summary>
public sealed class ToastService
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    public event Action? Changed;

    private readonly List<Toast> _toasts = [];

    public IReadOnlyList<Toast> Current => _toasts;

    public void Info(string message, string? title = null) => Show(ToastLevel.Info, message, title);

    public void Success(string message, string? title = null) => Show(ToastLevel.Success, message, title);

    public void Warning(string message, string? title = null) => Show(ToastLevel.Warning, message, title);

    public void Error(string message, string? title = null) => Show(ToastLevel.Error, message, title, TimeSpan.FromSeconds(10));

    public void Show(ToastLevel level, string message, string? title = null, TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _toasts.Add(new Toast(Guid.NewGuid(), level, message, title, duration ?? DefaultDuration));
        Changed?.Invoke();
    }

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }
}

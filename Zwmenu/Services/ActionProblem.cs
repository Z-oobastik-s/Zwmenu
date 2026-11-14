using Zwmenu.Models;

namespace Zwmenu.Services;

/// <summary>Понятная проблема запуска: не сырой текст Win32.</summary>
public sealed class ActionProblem
{
    public required MenuAction Action { get; init; }
    public required string Title { get; init; }
    public required string Explanation { get; init; }
    public string? BrokenPath { get; init; }
    public bool CanFixPath { get; init; }
}

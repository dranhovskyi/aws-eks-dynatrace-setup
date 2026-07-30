namespace TodoApi.Models;

public class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public bool IsComplete { get; set; } = false;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public record TodoCreateRequest(string Title);

public record TodoUpdateRequest(string? Title, bool? IsComplete);

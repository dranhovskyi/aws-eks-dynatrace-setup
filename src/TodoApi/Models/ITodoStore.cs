using System.Collections.Concurrent;

namespace TodoApi.Models;

public interface ITodoStore
{
    IEnumerable<TodoItem> GetAll();
    TodoItem? Get(Guid id);
    TodoItem Add(string title);
    bool Update(Guid id, string? title, bool? isComplete);
    bool Delete(Guid id);
}

// In-memory store. Good for demos; swap for EF Core + a real database
// (e.g. Amazon RDS / DynamoDB) for production use.
public class InMemoryTodoStore : ITodoStore
{
    private readonly ConcurrentDictionary<Guid, TodoItem> _items = new();

    public InMemoryTodoStore()
    {
        var seed = new TodoItem { Title = "Deploy the Todo API to EKS" };
        _items[seed.Id] = seed;
    }

    public IEnumerable<TodoItem> GetAll() =>
        _items.Values.OrderBy(t => t.CreatedAtUtc);

    public TodoItem? Get(Guid id) =>
        _items.TryGetValue(id, out var item) ? item : null;

    public TodoItem Add(string title)
    {
        var item = new TodoItem { Title = title };
        _items[item.Id] = item;
        return item;
    }

    public bool Update(Guid id, string? title, bool? isComplete)
    {
        if (!_items.TryGetValue(id, out var item)) return false;
        if (title is not null) item.Title = title;
        if (isComplete is not null) item.IsComplete = isComplete.Value;
        return true;
    }

    public bool Delete(Guid id) => _items.TryRemove(id, out _);
}

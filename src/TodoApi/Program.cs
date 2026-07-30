using TodoApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ITodoStore, InMemoryTodoStore>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Kubernetes runs behind a load balancer, so listen on all interfaces on port 8080.
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(8080));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// Liveness/readiness probe target for the Kubernetes Deployment.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

var todos = app.MapGroup("/api/todos");

todos.MapGet("/", (ITodoStore store) => store.GetAll());

todos.MapGet("/{id:guid}", (Guid id, ITodoStore store) =>
    store.Get(id) is { } item ? Results.Ok(item) : Results.NotFound());

todos.MapPost("/", (TodoCreateRequest request, ITodoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "Title is required." });

    var item = store.Add(request.Title);
    return Results.Created($"/api/todos/{item.Id}", item);
});

todos.MapPut("/{id:guid}", (Guid id, TodoUpdateRequest request, ITodoStore store) =>
    store.Update(id, request.Title, request.IsComplete) ? Results.NoContent() : Results.NotFound());

todos.MapDelete("/{id:guid}", (Guid id, ITodoStore store) =>
    store.Delete(id) ? Results.NoContent() : Results.NotFound());

app.Run();

namespace DevX.Domain;

// Async-local: concurrent hosts/tasks must not leak attribution into each other.
public static class OperationContext
{
    public static readonly AsyncLocal<string?> TaskLabel = new();
}

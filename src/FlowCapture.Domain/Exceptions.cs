namespace FlowCapture.Domain;

/// <summary>Base for errors raised by business rules. Mapped to HTTP 422 unless a subclass says otherwise.</summary>
public class DomainException(string message, string code = "business_rule") : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class InvalidTransitionException(string message) : DomainException(message, "invalid_transition");

public sealed class NotFoundException(string entity, object key)
    : Exception($"{entity} '{key}' was not found.");

public sealed class ForbiddenException(string message = "You do not have access to this resource.") : Exception(message);

/// <summary>Optimistic concurrency or duplicate conflict. ServerCopy (optional) is returned to the client.</summary>
public sealed class ConflictException(string message, object? serverCopy = null) : Exception(message)
{
    public object? ServerCopy { get; } = serverCopy;
}

/// <summary>A tool the server depends on (headless browser, LibreOffice) is not available. Mapped to HTTP 503.</summary>
public sealed class DependencyUnavailableException(string message) : Exception(message);

/// <summary>The monthly AI token budget has been used up. Mapped to HTTP 429.</summary>
public sealed class AiBudgetExceededException(string message) : DomainException(message, "ai_budget_exceeded");

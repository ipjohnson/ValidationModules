using DependencyModules.Runtime.Attributes;
using ValidationModules;
using ValidationModules.Constraints;

namespace SutProject.Dm;

/// <summary>The application's own module. Generated validators compose into it.</summary>
[DependencyModule]
public partial class ApplicationModule;

public sealed record Account {
    [Required]
    [StringLength(min: 3, max: 20)]
    public string? Handle { get; init; }

    [Range(0, 150)]
    public int Age { get; init; }
}

/// <summary>
/// A hand-written structural validator for a type the generator also validates. Both run and their
/// results merge - a business rule must not be able to make a structural constraint disappear.
/// </summary>
[SingletonService]
public sealed class AccountReservedHandleValidator : IValidatorFor<Account> {
    private static readonly string[] Reserved = { "admin", "root", "system" };

    public ValidationFlow Validate(ref ValidationContext context, Account value) =>
        value.Handle is not null && Array.IndexOf(Reserved, value.Handle) >= 0
            ? context.Report("handle", "reserved", "handle is reserved.")
            : ValidationFlow.Continue;
}

/// <summary>
/// A hand-written business rule. Takes a dependency, which is the whole reason this side exists,
/// and runs only when structural validation found nothing.
/// </summary>
[ScopedService]
public sealed class AccountUniquenessValidator : IAsyncValidatorFor<Account> {
    private readonly IHandleDirectory _directory;

    public AccountUniquenessValidator(IHandleDirectory directory) {
        _directory = directory;
    }

    public async ValueTask ValidateAsync(ValidationContext context, Account value, CancellationToken cancellationToken) {
        if (value.Handle is null) {
            return;
        }

        if (await _directory.IsTakenAsync(value.Handle, cancellationToken)) {
            context.Report("handle", "duplicate", "handle is already taken.");
        }
    }
}

public interface IHandleDirectory {
    ValueTask<bool> IsTakenAsync(string handle, CancellationToken cancellationToken);
}

[SingletonService]
public sealed class InMemoryHandleDirectory : IHandleDirectory {
    public ValueTask<bool> IsTakenAsync(string handle, CancellationToken cancellationToken) =>
        new(handle == "taken");
}

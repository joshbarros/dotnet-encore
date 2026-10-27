namespace Encore.Shared.Contracts;

public record GigInfo(Guid Id, string Code, long PriceCents);

/// <summary>
/// What Orders may ask Gigs, without referencing it. Gigs implements it over its own tables;
/// Orders never reads another module's schema.
/// </summary>
public interface IGigCatalog
{
    Task<GigInfo?> FindAsync(string code, CancellationToken ct);
}

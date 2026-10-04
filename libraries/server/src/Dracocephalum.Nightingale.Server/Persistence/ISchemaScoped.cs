namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// A context whose tables live in a schema chosen when the context is made, not when its type
/// is written; <see cref="SchemaModelCacheKeyFactory"/> keeps a model per schema for it.
/// </summary>
public interface ISchemaScoped
{
    /// <summary>Gets the schema the context's tables live in.</summary>
    string Schema { get; }
}

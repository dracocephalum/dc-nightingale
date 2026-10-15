namespace Dracocephalum.Nightingale.Server.Data;

/// <summary>
/// A context whose tables live in a schema chosen when the context is made, not when its type
/// is written; <see cref="SchemaModelCacheKeyFactory"/> keeps a model per schema for it.
/// </summary>
public interface ISchemaScoped
{
    /// <summary>Gets the schema the context's tables live in.</summary>
    string Schema { get; }

    /// <summary>Gets what the context's model is cached under beside its type: its schema, or every schema its model names when it reads another's tables too.</summary>
    object ModelKey => Schema;
}

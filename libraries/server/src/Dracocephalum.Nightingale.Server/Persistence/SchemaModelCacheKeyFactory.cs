using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dracocephalum.Nightingale.Server.Persistence;

/// <summary>
/// Makes the schema part of what a context's model is cached under. A model is built once per
/// context type and reused, and the schema is baked into it, so without this a second context
/// of the same type over another schema would read and write the first one's tables. One
/// process serving two schemas is rare outside tests, and silent when it happens.
/// </summary>
public sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc/>
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context is ISchemaScoped scoped
            ? (context.GetType(), scoped.Schema, designTime)
            : (context.GetType(), designTime);
    }
}

using System.Runtime.CompilerServices;
using Patchouli.Core.Library;

namespace Patchouli.Host.Composition;

/// <summary>
/// Composition registration (issue #84): as soon as the Host assembly loads, its module
/// initializer publishes the concrete <see cref="LibrarySession"/> opener on the Core
/// <see cref="LibrarySessionRuntime"/> seam. Business UI therefore opens Libraries through
/// Core only; nothing in the UI layer references this registration type.
/// </summary>
internal static class LibrarySessionRuntimeRegistration
{
    // CA2255: a module initializer is deliberate here — the Host composition registers its
    // Library session opener as soon as the assembly loads, so business UI can use the Core
    // seam without referencing the Host implementation (issue #84).
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        // An async lambda (not a method group) is required: Task<T> has no variance, so the
        // concrete session is upcast by the async method's return conversion instead.
        LibrarySessionRuntime.Register(static async (request, cancellationToken) =>
            (object)await LibrarySession.OpenAsync(request, cancellationToken));
    }
}

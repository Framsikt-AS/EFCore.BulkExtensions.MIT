using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EFCore.BulkExtensions;

internal static class IEnumerableExtensions
{
    /// <summary>
    /// Maps from IEnumerable to IList
    /// </summary>
    public static IList<T> EnsureList<T>(this IEnumerable<T> source)
        where T : class => source as IList<T> ?? [.. source];
}

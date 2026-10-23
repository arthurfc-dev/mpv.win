using System.Globalization;

namespace Player.App.Library;

internal static class LibrarySearch
{
    // Search only catalog strings: no Windows Search, content extraction or
    // additional directory enumeration. Every whitespace-delimited term matches.
    internal static string[] Find(IReadOnlyList<string> files, string query, CancellationToken token)
    {
        string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        var matches = new List<string>();
        foreach (string path in files)
        {
            token.ThrowIfCancellationRequested();
            if (terms.All(term => compare.IndexOf(path, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
                matches.Add(path);
        }
        return matches.ToArray();
    }
}

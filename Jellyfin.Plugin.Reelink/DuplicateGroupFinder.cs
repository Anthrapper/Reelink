using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Reelink;

/// <summary>
/// Finds connected sets of series that agree on configured external IDs.
/// </summary>
internal static class DuplicateGroupFinder
{
    internal static IReadOnlyList<IReadOnlyList<Series>> Find(
        IReadOnlyList<Series> series,
        IReadOnlyList<string> providers,
        int minimumMatches)
    {
        var disjointSet = new DisjointSet(series.Count);
        var candidates = new HashSet<(int Left, int Right)>();

        foreach (var provider in providers)
        {
            foreach (var matchingId in series
                         .Select((item, index) => (Value: ShowMergeManager.GetProviderId(item, provider), Index: index))
                         .Where(entry => entry.Value is not null)
                         .GroupBy(entry => entry.Value!, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                var indexes = matchingId.Select(entry => entry.Index).ToArray();
                for (var left = 0; left < indexes.Length - 1; left++)
                {
                    for (var right = left + 1; right < indexes.Length; right++)
                    {
                        candidates.Add((Math.Min(indexes[left], indexes[right]), Math.Max(indexes[left], indexes[right])));
                    }
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (AreCompatible(series[candidate.Left], series[candidate.Right], providers, minimumMatches))
            {
                disjointSet.Union(candidate.Left, candidate.Right);
            }
        }

        var groups = series
            .Select((item, index) => (Item: item, Root: disjointSet.Find(index)))
            .GroupBy(entry => entry.Root)
            .Select(group => (IReadOnlyList<Series>)group.Select(entry => entry.Item).OrderBy(item => item.Id).ToArray())
            .Where(group => group.Count > 1)
            .OrderBy(group => group[0].Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = new List<IReadOnlyList<Series>>();
        foreach (var group in groups)
        {
            result.AddRange(SplitIntoConsistentGroups(group, providers, minimumMatches));
        }

        return result;
    }

    private static int CountMatches(Series left, Series right, IReadOnlyList<string> providers)
    {
        return providers.Count(provider =>
        {
            var leftId = ShowMergeManager.GetProviderId(left, provider);
            var rightId = ShowMergeManager.GetProviderId(right, provider);
            return leftId is not null
                   && rightId is not null
                   && string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool AreCompatible(
        Series left,
        Series right,
        IReadOnlyList<string> providers,
        int minimumMatches)
    {
        foreach (var provider in providers)
        {
            var leftId = ShowMergeManager.GetProviderId(left, provider);
            var rightId = ShowMergeManager.GetProviderId(right, provider);
            if (leftId is not null
                && rightId is not null
                && !string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return CountMatches(left, right, providers) >= minimumMatches;
    }

    private static IReadOnlyList<IReadOnlyList<Series>> SplitIntoConsistentGroups(
        IReadOnlyList<Series> group,
        IReadOnlyList<string> providers,
        int minimumMatches)
    {
        var groups = new List<List<Series>>();
        foreach (var series in group)
        {
            var compatible = groups.FirstOrDefault(existing =>
                existing.All(member => AreCompatible(series, member, providers, minimumMatches)));

            if (compatible is null)
            {
                groups.Add([series]);
            }
            else
            {
                compatible.Add(series);
            }
        }

        return groups.Where(items => items.Count > 1).Cast<IReadOnlyList<Series>>().ToArray();
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public DisjointSet(int count)
        {
            _parent = Enumerable.Range(0, count).ToArray();
            _rank = new byte[count];
        }

        public int Find(int value)
        {
            while (_parent[value] != value)
            {
                _parent[value] = _parent[_parent[value]];
                value = _parent[value];
            }

            return value;
        }

        public void Union(int left, int right)
        {
            left = Find(left);
            right = Find(right);
            if (left == right)
            {
                return;
            }

            if (_rank[left] < _rank[right])
            {
                (left, right) = (right, left);
            }

            _parent[right] = left;
            if (_rank[left] == _rank[right])
            {
                _rank[left]++;
            }
        }
    }
}

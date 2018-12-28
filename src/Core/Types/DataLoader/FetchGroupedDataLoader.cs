using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GreenDonut;

namespace HotChocolate.DataLoader
{
    internal sealed class FetchGroupedDataLoader<TKey, TValue>
        : DataLoaderBase<TKey, TValue[]>
    {
        private readonly FetchGrouped<TKey, TValue> _fetch;

        public FetchGroupedDataLoader(FetchGrouped<TKey, TValue> fetch)
            : base(new DataLoaderOptions<TKey>
            {
                AutoDispatching = false,
                Batching = true,
                CacheSize = 100,
                MaxBatchSize = 0,
                SlidingExpiration = TimeSpan.Zero
            })
        {
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        }

        protected override async Task<IReadOnlyList<IResult<TValue[]>>> Fetch(
            IReadOnlyList<TKey> keys)
        {
            ILookup<TKey, TValue> result = await _fetch(keys);
            var items = new IResult<TValue[]>[keys.Count];

            for (int i = 0; i < keys.Count; i++)
            {
                items[i] = Result<TValue[]>.Resolve(result[keys[i]].ToArray());
            }

            return items;
        }
    }
}
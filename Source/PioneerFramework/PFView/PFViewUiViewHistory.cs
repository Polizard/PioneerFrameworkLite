using System;

namespace PioneerFramework
{
    internal sealed class PFViewUiViewHistory<T> where T : class
    {
        private T? previous;
        public bool HasValue => previous != null;
        public void Remember(T snapshot) => previous = snapshot;
        public void Clear() => previous = null;

        public bool TryTake(Predicate<T> valid, out T? snapshot)
        {
            snapshot = previous;
            previous = null;
            if (snapshot != null && valid(snapshot)) return true;
            snapshot = null;
            return false;
        }
    }
}

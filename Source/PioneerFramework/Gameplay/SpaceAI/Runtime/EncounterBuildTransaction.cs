using System;
using System.Collections.Generic;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Owns only newly generated ships until fleet registration commits.</summary>
    public sealed class EncounterBuildTransaction : IDisposable
    {
        private readonly Action<int> rollback;
        private readonly List<int> created = new();
        private bool committed, disposed;
        public EncounterBuildTransaction(Action<int> rollback) { this.rollback = rollback; }
        public void Add(int groupId) => created.Add(groupId);
        public void Commit() => committed = true;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (committed) return;
            List<Exception>? failures = null;
            for (int i = created.Count - 1; i >= 0; i--)
                try { rollback(created[i]); } catch (Exception e) { (failures ??= new()).Add(e); }
            if (failures != null) throw new AggregateException(failures);
        }
    }
}

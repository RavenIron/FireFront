using System.Collections.Generic;
using FireFront.Config;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// Bounded FIFO for ignitions that arrive while the concurrent burn cap is full.
    /// No distance weighting — strict arrival order. When full, TryEnqueue returns
    /// false and the caller drops the ignition silently; spread naturally re-attempts
    /// it on the next cycle because the burning neighbor is still burning.
    ///
    /// ZDOID-keyed rather than Component-keyed: a dedicated server can tear down
    /// and later re-instantiate the underlying GameObject for a queued target at
    /// any point (confirmed via live testing — force-created instances get
    /// reaped by the server's own housekeeping), so holding a live Component
    /// reference here would go stale the same way _burning's did. ZDOID is a
    /// plain value struct — always valid to hold, resolved to a live Component
    /// only at promotion time.
    /// </summary>
    public class FireQueue
    {
        private readonly Queue<ZDOID> _queue = new Queue<ZDOID>();
        private readonly HashSet<ZDOID> _members = new HashSet<ZDOID>();

        public int Count => _queue.Count;

        public int Capacity => FireConfig.QueueSize.Value;

        public bool Contains(ZDOID id) => _members.Contains(id);

        /// <summary>False = queue full (or duplicate); ignition is dropped silently.</summary>
        public bool TryEnqueue(ZDOID id)
        {
            if (_members.Contains(id)) return false;
            if (_queue.Count >= Capacity) return false;

            _queue.Enqueue(id);
            _members.Add(id);
            return true;
        }

        /// <summary>Pops the oldest entry. ZDOID.None (default) when empty.</summary>
        public ZDOID DequeueNextValid()
        {
            if (_queue.Count == 0) return ZDOID.None;
            ZDOID id = _queue.Dequeue();
            _members.Remove(id);
            return id;
        }

        public void Remove(ZDOID id)
        {
            if (!_members.Contains(id)) return;
            _members.Remove(id);
            // Rebuild without the removed entry, preserving FIFO order.
            var kept = new List<ZDOID>(_queue.Count);
            while (_queue.Count > 0)
            {
                ZDOID item = _queue.Dequeue();
                if (!item.Equals(id)) kept.Add(item);
            }
            foreach (ZDOID item in kept) _queue.Enqueue(item);
        }

        public void Clear()
        {
            _queue.Clear();
            _members.Clear();
        }
    }
}
using ForzaNext.Core.Virtual;

namespace ForzaNext.Core.FFB;

// Parameters supersede older parameters only before the next lifecycle command.
// Never replace across start/stop/solo/free/reset, or a change of effect type.
public sealed class FfbInbox
{
    private readonly object _gate = new();
    private readonly LinkedList<FfbPacket> _queue = new();
    private readonly Dictionary<(FFBPType, byte, int), LinkedListNode<FfbPacket>> _pending = new();
    private readonly Dictionary<byte, FFBEType> _types = new();
    private readonly int _capacity;
    private long _coalesced;
    public long Coalesced { get { lock (_gate) return _coalesced; } }
    public int Count { get { lock (_gate) return _queue.Count; } }
    public FfbInbox(int capacity = 4096) => _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    public bool Enqueue(FfbPacket packet)
    {
        lock (_gate)
        {
            byte id = packet.Value switch
            {
                FfbEffReport r => r.EffectBlockIndex, FfbEffConstant c => c.EffectBlockIndex,
                FfbEffPeriod p => p.EffectBlockIndex, FfbEffCond c => c.EffectBlockIndex,
                FfbEffRamp r => r.EffectBlockIndex, FfbEffEnvelope e => e.EffectBlockIndex, _ => 0
            };
            bool parameter = id != 0 || packet.Type == FFBPType.PT_GAINREP;
            if (packet.Value is FfbEffReport report)
            {
                if (_types.TryGetValue(id, out var type) && type != report.EffectType)
                {
                    var keysToRemove = _pending.Keys.Where(k => k.Item2 == id).ToList();
                    foreach (var k in keysToRemove) _pending.Remove(k);
                }
                _types[id] = report.EffectType;
            }
            if (!parameter)
            {
                _pending.Clear();
                if (packet.Type == FFBPType.PT_BLKFRREP && packet.Value is int freed) _types.Remove((byte)freed);
                if (packet.Value is FFB_CTRL.CTRL_DEVRST) _types.Clear();
            }
            var key = (packet.Type, id, packet.Value is FfbEffCond condition ? condition.isY : 0);
            if (parameter && _pending.TryGetValue(key, out var old))
            { old.Value = packet; _coalesced++; return true; }
            if (_queue.Count == _capacity) return false;
            var node = _queue.AddLast(packet);
            if (parameter) _pending[key] = node;
            return true;
        }
    }
    public void Drain(List<FfbPacket> target, int maximum = 256)
    {
        target.Clear();
        lock (_gate)
        {
            while (target.Count < maximum && _queue.First is { } node)
            {
                // Removing only matching nodes keeps keys from later segments valid.
                var value = node.Value;
                byte id = value.Value switch
                {
                    FfbEffReport r => r.EffectBlockIndex, FfbEffConstant c => c.EffectBlockIndex,
                    FfbEffPeriod p => p.EffectBlockIndex, FfbEffCond c => c.EffectBlockIndex,
                    FfbEffRamp r => r.EffectBlockIndex, FfbEffEnvelope e => e.EffectBlockIndex, _ => 0
                };
                var key = (value.Type, id, value.Value is FfbEffCond condition ? condition.isY : 0);
                if (_pending.TryGetValue(key, out var pending) && ReferenceEquals(pending, node)) _pending.Remove(key);
                _queue.RemoveFirst(); target.Add(value);
            }
        }
    }
}

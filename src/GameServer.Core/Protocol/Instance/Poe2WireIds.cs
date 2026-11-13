using System.Buffers.Binary;
using System.Globalization;

namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The instance message ids of the client build in use, as shifts from the ids this code is written with: PoE2 0.5.5e's,
/// the ones in PORTING-POE2.md. A game update inserts message ids, which moves every id above them up while the
/// messages themselves keep their layouts (PORTING-POE2.md, wave 36). An entry (From, Delta) says that the ids from
/// <c>From</c> up, until the next entry, are <c>Delta</c> higher on the wire. Applied where bytes leave and arrive, so
/// the rest of the code keeps one numbering.
/// </summary>
public sealed class Poe2WireIds
{
    /// <summary>Wire ids are the code's ids (PoE2 0.5.5e).</summary>
    public static readonly Poe2WireIds Identity = new([]);

    /// <summary>PoE2 0.5.5f: three ids were inserted between 0x2e8 and 0x2f1, so the world group (change area 0x2f1,
    /// the frames 0x2f4 / 0x2f5, create object 0x2f7, ...) and everything above it are 3 higher. Seen live for 0x2f1,
    /// 0x2f4, 0x2f5, 0x2f7, 0x2f9, 0x324 and the client's 0x355 and 0x357; ids up to 0x2e8 did not move. Where exactly
    /// the three sit below 0x2f1 is not known: no id of 0x2e9–0x2f0 is used.</summary>
    public static readonly Poe2WireIds Client055f = new([(0x2f1, 3)]);

    private readonly (ushort From, int Delta)[] _shifts;

    public Poe2WireIds(IEnumerable<(ushort From, int Delta)> shifts)
    {
        _shifts = [.. shifts.OrderBy(s => s.From)];
        for (int i = 0; i < _shifts.Length; i++)
        {
            int previous = i == 0 ? 0 : _shifts[i - 1].Delta;
            if (_shifts[i].Delta < previous || (i > 0 && _shifts[i].From == _shifts[i - 1].From))
                throw new ArgumentException("id shifts must grow with the id: an update only inserts ids", nameof(shifts));
        }
    }

    /// <summary>Parses "9b:1,2f0:6": hexadecimal first id, decimal shift.</summary>
    public static Poe2WireIds Parse(string text) =>
        new(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part =>
        {
            string[] pair = part.Split(':');
            if (pair.Length != 2) throw new FormatException($"id shift '{part}' is not <hex id>:<shift>");
            return (ushort.Parse(pair[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(pair[1], CultureInfo.InvariantCulture));
        }));

    /// <summary>The id the client knows this message by.</summary>
    public ushort ToWire(ushort id)
    {
        int delta = 0;
        foreach (var (from, shift) in _shifts)
        {
            if (id < from) break;
            delta = shift;
        }
        return (ushort)(id + delta);
    }

    /// <summary>The code's id for a client id; false for an id the update added, which the code has no number for.</summary>
    public bool TryFromWire(ushort wire, out ushort id)
    {
        int from = 0, delta = 0;
        for (int i = 0; i <= _shifts.Length; i++)
        {
            int end = i < _shifts.Length ? _shifts[i].From : 0x10000;
            if (wire - delta >= from && wire - delta < end)
            {
                id = (ushort)(wire - delta);
                return true;
            }
            if (i < _shifts.Length) (from, delta) = _shifts[i];
        }
        id = 0;
        return false;
    }

    /// <summary>A copy of an outgoing buffer with its id as the client knows it. A buffer made of several messages of
    /// <paramref name="each"/> bytes gets every one's id rewritten.</summary>
    public byte[] ToWire(ReadOnlySpan<byte> message, int each = 0)
    {
        byte[] wire = message.ToArray();
        int step = each > 0 ? each : Math.Max(wire.Length, 2);
        for (int at = 0; at + 2 <= wire.Length; at += step)
            BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(at), ToWire(BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(at))));
        return wire;
    }
}

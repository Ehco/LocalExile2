namespace GameServer.Core.World;

/// <summary>
/// Server-side estimate of a PoE2 player walking in a direction (C→S 0x63, PORTING-POE2.md wave 12): in lockstep the
/// client walks in a straight line at its walking speed from the cell of the server's last echo until the next one, and
/// the next echo (S→C 0x64) must carry the cell it reached, or the client is put back there. The walk ignores walls:
/// there is no PoE2 walkability grid on the server yet, so a player held against a wall is ahead of the client.
/// </summary>
public sealed class DirectionWalk
{
    /// <summary>The client's walking speed in grid cells per second (PoE1's measured ~37; PoE2 matched it live,
    /// 2026-11-03: about 3.7 cells per 100 ms along an axis).</summary>
    public const double DefaultSpeed = 37;

    private readonly double _speed;
    private double _x, _y, _since, _dx, _dy;

    /// <summary>Starts standing in cell (<paramref name="x"/>, <paramref name="y"/>), at its centre.</summary>
    public DirectionWalk(int x, int y, double speed = DefaultSpeed)
    {
        if (!(speed > 0) || double.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        (_x, _y, _speed) = (x + 0.5, y + 0.5, speed);
    }

    public bool Walking => _dx != 0 || _dy != 0;

    /// <summary>The cell reached at <paramref name="now"/> (seconds, any monotonic clock).</summary>
    public (int X, int Y) Cell(double now)
    {
        Advance(now);
        return ((int)Math.Floor(_x), (int)Math.Floor(_y));
    }

    /// <summary>Takes a new direction at <paramref name="now"/> (0, 0 stops) and returns the cell the walk reached,
    /// which the echo carries. Only the direction counts, not its length.</summary>
    public (int X, int Y) Turn(int dx, int dy, double now)
    {
        var here = Cell(now);
        double length = Math.Sqrt((double)dx * dx + (double)dy * dy);
        (_dx, _dy) = length > 0 ? (dx / length, dy / length) : (0, 0);
        return here;
    }

    /// <summary>Puts the walker in a cell, standing (e.g. the client reported where it is).</summary>
    public void Place(int x, int y, double now)
    {
        (_x, _y, _since, _dx, _dy) = (x + 0.5, y + 0.5, now, 0, 0);
    }

    private void Advance(double now)
    {
        double elapsed = now - _since;
        if (elapsed > 0)
        {
            _x += _dx * _speed * elapsed;
            _y += _dy * _speed * elapsed;
        }
        _since = Math.Max(_since, now);
    }
}

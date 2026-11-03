namespace GameServer.Core.World;

/// <summary>
/// Server-side estimate of a PoE2 player walking in a direction (C→S 0x63, PORTING-POE2.md waves 12 and 14): in lockstep
/// the client walks in a straight line at its walking speed from the cell of the server's last echo until the next one,
/// and the next echo (S→C 0x64) must carry the cell it reached, or the client is put back there. With the area's
/// walkability the walk stops at walls the way the client's does: a blocked step slides along the wall on the free axis
/// (live 2026-11-03: a held diagonal walk ran on along x at full speed), and a step with no free axis waits there.
/// </summary>
public sealed class DirectionWalk
{
    /// <summary>The client's walking speed in grid cells per second (PoE1's measured ~37; PoE2 matched it live,
    /// 2026-11-03: about 3.7 cells per 100 ms along an axis).</summary>
    public const double DefaultSpeed = 37;

    // The longest step checked against the walkability, in cells: short enough not to cross a one-cell wall corner.
    private const double MaxStep = 0.25;

    private readonly double _speed;
    private readonly Func<int, int, bool>? _walkable;
    private double _x, _y, _since, _dx, _dy;

    /// <summary>Starts standing in cell (<paramref name="x"/>, <paramref name="y"/>), at its centre. Without
    /// <paramref name="walkable"/> the walk goes through everything.</summary>
    public DirectionWalk(int x, int y, double speed = DefaultSpeed, Func<int, int, bool>? walkable = null)
    {
        if (!(speed > 0) || double.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        (_x, _y, _speed, _walkable) = (x + 0.5, y + 0.5, speed, walkable);
    }

    public bool Walking => _dx != 0 || _dy != 0;

    /// <summary>The cell reached at <paramref name="now"/> (seconds, any monotonic clock).</summary>
    public (int X, int Y) Cell(double now)
    {
        Advance(now);
        return Here;
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

    private (int X, int Y) Here => ((int)Math.Floor(_x), (int)Math.Floor(_y));

    private void Advance(double now)
    {
        double elapsed = now - _since;
        _since = Math.Max(_since, now);
        if (elapsed <= 0 || !Walking) return;
        double distance = _speed * elapsed;
        if (!double.IsFinite(distance)) return;
        if (_walkable is null)
        {
            (_x, _y) = (_x + _dx * distance, _y + _dy * distance);
            return;
        }
        while (distance > 0)
        {
            double step = Math.Min(distance, MaxStep);
            distance -= step;
            if (!TryStep(_dx * step, _dy * step) && !Slide(step)) return;
        }
    }

    // Against a wall: the free axis at full speed, the walk's larger component first.
    private bool Slide(double step)
    {
        (double X, double Y) along = (Math.Sign(_dx) * step, 0), across = (0, Math.Sign(_dy) * step);
        if (Math.Abs(_dy) > Math.Abs(_dx)) (along, across) = (across, along);
        return (along != (0, 0) && TryStep(along.X, along.Y)) || (across != (0, 0) && TryStep(across.X, across.Y));
    }

    // A step into a cell that is not walkable is refused, and so is a diagonal step between two blocked cells (a wall
    // corner), unless the walker already stands on a blocked cell (a spawn or an echo off the walkable ground): there it
    // walks freely until it is back on walkable ground.
    private bool TryStep(double dx, double dy)
    {
        double x = _x + dx, y = _y + dy;
        var here = Here;
        int nextX = (int)Math.Floor(x), nextY = (int)Math.Floor(y);
        if (_walkable!(here.X, here.Y))
        {
            if ((nextX, nextY) != here && !_walkable(nextX, nextY)) return false;
            if (nextX != here.X && nextY != here.Y && !(_walkable(nextX, here.Y) && _walkable(here.X, nextY))) return false;
        }
        (_x, _y) = (x, y);
        return true;
    }
}

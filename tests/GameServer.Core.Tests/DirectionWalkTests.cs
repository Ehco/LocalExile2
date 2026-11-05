using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>The server's estimate of a PoE2 direction walk (PORTING-POE2.md, wave 13).</summary>
public class DirectionWalkTests
{
    [Fact]
    public void Standing_walker_stays_in_its_cell()
    {
        var walk = new DirectionWalk(200, 200);

        Assert.Equal((200, 200), walk.Cell(5));
        Assert.False(walk.Walking);
    }

    [Fact]
    public void Walks_along_an_axis_at_its_speed()
    {
        var walk = new DirectionWalk(200, 200, speed: 37);

        Assert.Equal((200, 200), walk.Turn(180, 0, now: 1));
        // Live 2026-11-03: about 3.7 cells per 100 ms along an axis, from the centre of the cell (200.5 + 3.7).
        Assert.Equal((204, 200), walk.Cell(1.1));
        Assert.Equal((237, 200), walk.Cell(2));
    }

    [Fact]
    public void Only_the_direction_counts_not_its_length()
    {
        var shortVector = new DirectionWalk(0, 0, speed: 10);
        var longVector = new DirectionWalk(0, 0, speed: 10);

        shortVector.Turn(1, 1, 0);
        longVector.Turn(127, 127, 0);

        Assert.Equal(shortVector.Cell(2), longVector.Cell(2));
        Assert.Equal((14, 14), longVector.Cell(2));   // 20 cells along the diagonal
    }

    [Fact]
    public void Turning_reports_where_the_previous_direction_led_and_stop_holds_it()
    {
        var walk = new DirectionWalk(100, 100, speed: 10);

        walk.Turn(0, -180, 0);
        Assert.Equal((100, 90), walk.Turn(0, 0, 1));
        Assert.False(walk.Walking);
        Assert.Equal((100, 90), walk.Cell(10));
    }

    [Fact]
    public void Place_puts_it_in_a_cell_standing()
    {
        var walk = new DirectionWalk(0, 0, speed: 10);
        walk.Turn(1, 0, 0);

        walk.Place(50, -7, 1);

        Assert.Equal((50, -7), walk.Cell(3));
    }

    [Fact]
    public void A_clock_going_back_does_not_move_it_backwards()
    {
        var walk = new DirectionWalk(0, 0, speed: 10);
        walk.Turn(1, 0, 2);

        Assert.Equal((10, 0), walk.Cell(3));
        Assert.Equal((10, 0), walk.Cell(1));
    }

    // Open ground for x < 10, a wall from x = 10 on.
    static bool WestOfWall(int x, int y) => x < 10;

    [Fact]
    public void A_wall_stops_a_walk_into_it()
    {
        var walk = new DirectionWalk(5, 5, speed: 10, walkable: WestOfWall);

        walk.Turn(180, 0, 0);

        Assert.Equal((9, 5), walk.Cell(3));
    }

    [Fact]
    public void A_diagonal_walk_slides_along_a_wall_at_full_speed()
    {
        var walk = new DirectionWalk(5, 5, speed: 10, walkable: WestOfWall);

        walk.Turn(127, -127, 0);

        // About 6.3 cells along the diagonal to reach the wall (x 5.5 → 9.8, y 5.5 → 1.2), then the rest of the 20
        // cells along y: y ≈ 1.2 − 13.7.
        var (x, y) = walk.Cell(2);
        Assert.Equal(9, x);
        Assert.Equal(-13, y);
    }

    [Fact]
    public void A_diagonal_walk_does_not_slip_between_two_blocked_cells()
    {
        // A checkerboard corner: (5, 5) and (6, 6) open, (6, 5) and (5, 6) blocked, everything else open.
        var walk = new DirectionWalk(5, 5, speed: 10,
            walkable: (x, y) => (x, y) is not ((6, 5) or (5, 6)));

        walk.Turn(127, 127, 0);

        Assert.Equal((5, 5), walk.Cell(1));
    }

    [Fact]
    public void A_walk_away_from_the_wall_is_free()
    {
        var walk = new DirectionWalk(9, 5, speed: 10, walkable: WestOfWall);

        walk.Turn(-180, 0, 0);

        Assert.Equal((-1, 5), walk.Cell(1));
    }

    [Fact]
    public void A_walker_off_the_walkable_ground_walks_freely_until_it_is_back()
    {
        var walk = new DirectionWalk(20, 5, speed: 10, walkable: WestOfWall);

        walk.Turn(-180, 0, 0);
        Assert.Equal((5, 5), walk.Cell(1.5));

        walk.Turn(180, 0, 1.5);
        Assert.Equal((9, 5), walk.Cell(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Speed_must_be_positive_and_finite(double speed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DirectionWalk(0, 0, speed));
    }
}

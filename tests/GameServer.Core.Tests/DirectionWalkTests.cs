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

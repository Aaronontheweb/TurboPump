using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace TurboPump.Tests;

public class WorkQueueTests
{
    /// <summary>
    /// Need to test queue emptying with just push and pop - doesn't test steals.
    /// </summary>
    /// <param name="workAmount">The amount of items to populate into the work queue</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void ShouldFillAndDrainQueue(int workAmount)
    {
        // arrange
        var expectedItems = Enumerable.Range(0, workAmount).ToArray();
        var q = new CircularWorkStealingQueue<int>();
        
        // act
        foreach (var a in expectedItems)
        {
            q.PushBottom(a);
        }

        var actualItems = new int[workAmount];
        for (var i = 0; i < workAmount; i++)
        {
            actualItems[i] = q.PopBottom().item;
        }

        // assert
        actualItems.Sum().Should().Be(expectedItems.Sum()); // FluentAssertions can't handle large list comparisons
    }

    /// <summary>
    /// Need to test queue emptying with both pop AND steal
    /// </summary>
    /// <param name="workAmount">The amount of items to populate into the work queue</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void ShouldDrainQueueViaPopAndSteal(int workAmount)
    {
        // arrange
        var expectedItems = Enumerable.Range(0, workAmount).ToArray();
        var q = new CircularWorkStealingQueue<int>();
        
        // act
        foreach (var a in expectedItems)
        {
            q.PushBottom(a);
        }
        
        var actualItems = new int[workAmount];
        for (var i = 0; i < workAmount; i++)
        {
            if(i % 3 == 0)
                actualItems[i] = q.Steal().item;
            else
                actualItems[i] = q.PopBottom().item;
        }
        
        // assert
        actualItems.Sum().Should().Be(expectedItems.Sum()); // FluentAssertions can't handle large list comparisons
    }
}
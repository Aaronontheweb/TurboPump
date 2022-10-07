using FluentAssertions;
using Xunit;

namespace TurboPump.Tests
{
    public class CircularArrayTests
    {
        [Fact]
        public void CircularArrayMustPerformBasicInserts()
        {
            // arrange
            var a = new CircularArray<int>(2);
            
            // act
            
            // should exceed the normal indexers
            a[10] = 2;
            a[11] = 3;
            a[12] = 4;
            a[13] = 5;
            
            // assert
            a.Size.Should().Be(4); // n^2
            a[10].Should().Be(2);

            a[14] = 6; // should override the position a[10] occupied
            a[10].Should().Be(6); 
        }

        [Fact]
        public void CircularArrayMustGrowWithoutDataLoss()
        {
            // arrange
            var a = new CircularArray<int>(2); // 4 items
            
            // act
            
            a[0] = 1;
            a[1] = 2;
            a[2] = 3;
            a[3] = 4;

            var b = a.Grow(4, 0); // grow to 8 items
            
            // assert
            b.Size.Should().Be(8); // n^2
            b[0].Should().Be(a[0]);
            b[1].Should().Be(a[1]);
            b[2].Should().Be(a[2]);
            b[3].Should().Be(a[3]);
            
            // values shouldn't be the same - b[4] should be zero, a[4] == a[0] == 1 
            b[4].Should().NotBe(a[4]);
        }
        
        [Fact]
        public void CircularArrayMustShrinkWithoutDataLoss()
        {
            // arrange
            var a = new CircularArray<int>(3); // 8 items
            
            // act
            
            a[0] = 1;
            a[1] = 2;
            a[2] = 3;
            a[3] = 4;
            a[4] = 5; // item that is going to disappear once we shrink.

            var b = a.Shrink(4, 0); // shrink to 4 items
            
            // assert
            b.Size.Should().Be(4); // n^2
            b[0].Should().Be(a[0]);
            b[1].Should().Be(a[1]);
            b[2].Should().Be(a[2]);
            b[3].Should().Be(a[3]);
            
            // values shouldn't be the same - a[4] should be 5, b[4] == a[0] == 1 
            b[4].Should().NotBe(a[4]);
        }
    }
}

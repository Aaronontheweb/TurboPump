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
    }
}

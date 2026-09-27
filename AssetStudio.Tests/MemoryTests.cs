using Xunit;

namespace AssetStudio.Tests
{
    public class SpillStorageTests
    {
        public SpillStorageTests()
        {
            //on the disk next to the tests: /tmp can be tmpfs, in the RAM
            SpillStorage.Directory = Path.Combine(AppContext.BaseDirectory, "spill");
        }

        [Fact]
        public void Stream_KeepsWhatIsWritten()
        {
            var data = Enumerable.Range(0, 100_000).Select(x => (byte)(x * 7)).ToArray();
            using var stream = SpillStorage.TryCreate(data.Length, true);
            Assert.NotNull(stream);
            Assert.Equal(data.Length, stream.Length);

            new MemoryStream(data).CopyTo(stream);
            stream.Position = 0;
            var read = new byte[data.Length];
            stream.ReadExactly(read);
            Assert.Equal(data, read);
        }

        [Fact]
        public void Slice_SeesTheSameBytes()
        {
            using var stream = SpillStorage.TryCreate(64, true);
            stream.Write(Enumerable.Range(0, 64).Select(x => (byte)x).ToArray());

            using var slice = stream.Slice(16, 8);
            Assert.Equal(8, slice.Length);
            Assert.Equal(0, slice.Position);
            var read = new byte[8];
            slice.ReadExactly(read);
            Assert.Equal(new byte[] { 16, 17, 18, 19, 20, 21, 22, 23 }, read);
            //the slice ends where it should
            Assert.Equal(0, slice.Read(new byte[1]));

            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Slice(60, 8));
        }

        [Fact]
        public void Streams_DoNotOverlap()
        {
            using var a = SpillStorage.TryCreate(13, true);
            using var b = SpillStorage.TryCreate(13, true);
            a.Write(Enumerable.Repeat((byte)0xAA, 13).ToArray());
            b.Write(Enumerable.Repeat((byte)0xBB, 13).ToArray());
            a.Position = 0;
            Assert.All(Enumerable.Range(0, 13), _ => Assert.Equal(0xAA, a.ReadByte()));
        }

        [Fact]
        public void NothingIsSpilled_WithoutSizeOrNeed()
        {
            Assert.Null(SpillStorage.TryCreate(0, true));
            var spillBelow = MemoryBudget.SpillBelow;
            try
            {
                MemoryBudget.SpillBelow = 0;
                Assert.Null(SpillStorage.TryCreate(1024));
            }
            finally
            {
                MemoryBudget.SpillBelow = spillBelow;
            }
        }
    }

    public class MemoryBudgetTests
    {
        [Fact]
        public void ReadsTheSystemMemory()
        {
            Assert.True(MemoryBudget.Total > 0);
            Assert.InRange(MemoryBudget.Available, 0, MemoryBudget.Total);
            Assert.InRange(MemoryBudget.Critical, 1L << 30, 4L << 30);
        }
    }
}

using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>OneDrive's quickXorHash, against published vectors and Microsoft's reference implementation.</summary>
public sealed class QuickXorHashTests
{
    /// <summary>rclone's test vectors (backend/onedrive/quickxorhash), checked against OneDrive: input and hash in Base64.</summary>
    [Theory]
    [InlineData("", "AAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("Sg==", "SgAAAAAAAAAAAAAAAQAAAAAAAAA=")]
    [InlineData("tbQ=", "taAFAAAAAAAAAAAAAgAAAAAAAAA=")]
    [InlineData("0pZP", "0rDEEwAAAAAAAAAAAwAAAAAAAAA=")]
    [InlineData("jRRDVA==", "jaDAEKgAAAAAAAAABAAAAAAAAAA=")]
    [InlineData("eAV52qE=", "eChAHrQRCgAAAAAABQAAAAAAAAA=")]
    [InlineData("luBZlaT6", "lgBHFipBCn0AAAAABgAAAAAAAAA=")]
    [InlineData("qaApEj66lw==", "qQBFCiTgA11cAgAABwAAAAAAAAA=")]
    [InlineData("T6LYJIfDh81JrAK309H2JMJTXis=", "zBTHrspn3mEcohlJdIUAbjGNaNg=")]
    [InlineData("DWAAX5/CIfrmErgZa8ot6ZraeSbu", "LR2Z0PjuRYGKQB/mhQAuMrAGZbQ=")]
    [InlineData("4SVmjtXIL8BB8SfkbR5Cpaljm2jpyUfAhIBf65XmKxHlz9dy5XixgiE/q1lv+esZW/E=", "wxZ0rxkMQEnRNAp8ZgEZLT4RdLM=")]
    public void Matches_the_published_vectors(string input, string hash) => Assert.Equal(hash, QuickXorHash.Of(Convert.FromBase64String(input)));

    [Fact]
    public void Matches_the_reference_implementation_in_any_pieces()
    {
        var random = new Random(365);
        foreach (var length in new[] { 1, 159, 160, 161, 320, 999, 4096, (1 << 20) + 7 })
        {
            var data = new byte[length];
            random.NextBytes(data);
            var expected = Reference(data);
            Assert.Equal(expected, QuickXorHash.Of(data));

            var pieces = new QuickXorHash();
            for (var at = 0; at < data.Length;)
            {
                var n = Math.Min(random.Next(1, 700), data.Length - at);
                pieces.Append(data.AsSpan(at, n));
                at += n;
            }
            Assert.Equal(expected, pieces.Finish());
        }
    }

    [Fact]
    public void A_file_hashes_as_its_bytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "vfs365-tests", $"{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[3 * 1024 * 1024 + 11];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(path, data);
        try
        {
            Assert.Equal(Reference(data), QuickXorHash.OfFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The C# sample of learn.microsoft.com/onedrive/developer/code-snippets/quickxorhash, ported as it is.</summary>
    static string Reference(byte[] array)
    {
        const int BitsInLastCell = 32, Shift = 11, WidthInBits = 160;
        var data = new ulong[(WidthInBits - 1) / 64 + 1];
        int currentShift = 0, ibStart = 0, cbSize = array.Length;
        var vectorArrayIndex = currentShift / 64;
        var vectorOffset = currentShift % 64;
        var iterations = Math.Min(cbSize, WidthInBits);
        for (var i = 0; i < iterations; i++)
        {
            var isLastCell = vectorArrayIndex == data.Length - 1;
            var bitsInVectorCell = isLastCell ? BitsInLastCell : 64;
            if (vectorOffset <= bitsInVectorCell - 8)
            {
                for (var j = ibStart + i; j < cbSize + ibStart; j += WidthInBits)
                {
                    data[vectorArrayIndex] ^= (ulong)array[j] << vectorOffset;
                }
            }
            else
            {
                var index1 = vectorArrayIndex;
                var index2 = isLastCell ? 0 : vectorArrayIndex + 1;
                var low = (byte)(bitsInVectorCell - vectorOffset);
                byte xoredByte = 0;
                for (var j = ibStart + i; j < cbSize + ibStart; j += WidthInBits)
                {
                    xoredByte ^= array[j];
                }
                data[index1] ^= (ulong)xoredByte << vectorOffset;
                data[index2] ^= (ulong)xoredByte >> low;
            }
            vectorOffset += Shift;
            while (vectorOffset >= bitsInVectorCell)
            {
                vectorArrayIndex = isLastCell ? 0 : vectorArrayIndex + 1;
                vectorOffset -= bitsInVectorCell;
            }
        }
        var rgb = new byte[(WidthInBits - 1) / 8 + 1];
        for (var i = 0; i < data.Length - 1; i++)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(data[i]), 0, rgb, i * 8, 8);
        }
        Buffer.BlockCopy(BitConverter.GetBytes(data[^1]), 0, rgb, (data.Length - 1) * 8, rgb.Length - (data.Length - 1) * 8);
        var lengthBytes = BitConverter.GetBytes((long)cbSize);
        for (var i = 0; i < lengthBytes.Length; i++)
        {
            rgb[WidthInBits / 8 - lengthBytes.Length + i] ^= lengthBytes[i];
        }
        return Convert.ToBase64String(rgb);
    }
}

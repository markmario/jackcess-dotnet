using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Verifies <see cref="DataTypeExtensions"/>'s shape-of-type predicates and
/// size constants. These drive every row encode/decode path so wrong values
/// here surface as silent corruption — explicit assertions per type.
/// </summary>
public sealed class DataTypeExtensionsTests
{
    [Theory]
    [InlineData(DataType.Boolean,       1)]
    [InlineData(DataType.Byte,          1)]
    [InlineData(DataType.Int16,           2)]
    [InlineData(DataType.Int32,          4)]
    [InlineData(DataType.Currency,         8)]
    [InlineData(DataType.Single,         4)]
    [InlineData(DataType.Double,        8)]
    [InlineData(DataType.DateTime, 8)]
    [InlineData(DataType.Guid,          16)]
    [InlineData(DataType.Decimal,       17)]
    [InlineData(DataType.Complex,       4)]
    public void GetFixedSize_PerType(DataType dt, int expected)
        => Assert.Equal(expected, dt.GetFixedSize());

    [Theory]
    [InlineData(DataType.Boolean, false)]
    [InlineData(DataType.Int32,    false)]
    [InlineData(DataType.Decimal, false)]
    [InlineData(DataType.Text,    true)]
    [InlineData(DataType.Binary,  true)]
    [InlineData(DataType.Memo,    true)]
    [InlineData(DataType.OleObject,     true)]
    public void IsVariableLength_PerType(DataType dt, bool expected)
        => Assert.Equal(expected, dt.IsVariableLength());

    [Theory]
    [InlineData(DataType.Int32,    true)]
    [InlineData(DataType.Text,    false)]
    [InlineData(DataType.Binary,  false)]
    [InlineData(DataType.Memo,    false)]
    [InlineData(DataType.OleObject,     false)]
    public void IsFixedLength_IsInverseOfIsVariable(DataType dt, bool expected)
        => Assert.Equal(expected, dt.IsFixedLength());

    [Theory]
    [InlineData(DataType.Memo, true)]
    [InlineData(DataType.OleObject,  true)]
    [InlineData(DataType.Text, false)]
    [InlineData(DataType.Int32, false)]
    [InlineData(DataType.Binary, false)]
    public void IsLongValue_PerType(DataType dt, bool expected)
        => Assert.Equal(expected, dt.IsLongValue());

    [Theory]
    [InlineData(DataType.Text,   510)]   // 255 chars × 2 bytes UTF-16
    [InlineData(DataType.Binary, 255)]
    [InlineData(DataType.Memo,   0)]     // long values have no inline max
    [InlineData(DataType.Int32,   0)]
    public void GetMaxSize_PerType(DataType dt, int expected)
        => Assert.Equal(expected, dt.GetMaxSize());

    [Theory]
    [InlineData(DataType.Int32,    4)]
    [InlineData(DataType.Boolean, 1)]
    [InlineData(DataType.Text,    100)]   // 50 chars × 2 bytes
    [InlineData(DataType.Binary,  50)]
    [InlineData(DataType.Memo,    0)]
    public void GetDefaultSize_PerType(DataType dt, int expected)
        => Assert.Equal(expected, dt.GetDefaultSize());

    [Fact]
    public void GetInfo_UnknownType_ReturnsPlaceholder()
    {
        // Unknown Jet type codes shouldn't throw — they're treated as variable-length
        // placeholders so the whole TDEF parse keeps going.
        var unknown = (DataType)0xFE;
        var info = unknown.GetInfo();
        Assert.True(info.IsVariable);
        Assert.False(info.IsLong);
        Assert.Equal(0, info.FixedSize);
    }
}

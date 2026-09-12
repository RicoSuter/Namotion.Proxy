using Moq;
using Namotion.Interceptor.Ads.Client;
using System.Text;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

/// <summary>
/// The ADS any-type marshaller cannot take a property's declared type as-is: an enum and a nullable
/// are rejected outright, and a string or array needs a length only the PLC symbol knows.
/// </summary>
public class NotificationTypeResolutionTests
{
    private enum Mode : short
    {
        Idle = 0,
        Running = 1,
    }

    private static ISymbol CreateSymbol(int byteSize, IDataType? dataType = null)
    {
        var symbol = new Mock<ISymbol>();
        symbol.As<IBitSize>().Setup(b => b.ByteSize).Returns(byteSize);
        symbol.Setup(sy => sy.DataType).Returns(dataType!);
        return symbol.Object;
    }

    private static IDataType CreateStringType(int length, int byteSize)
    {
        var stringType = new Mock<IStringType>();
        stringType.Setup(t => t.Length).Returns(length);
        stringType.As<IBitSize>().Setup(t => t.ByteSize).Returns(byteSize);
        return stringType.As<IDataType>().Object;
    }

    private static IDataType CreateArrayType(int elementCount, int elementByteSize)
    {
        var dimensions = new Mock<IDimensionCollection>();
        dimensions.Setup(d => d.Count).Returns(1);
        dimensions.Setup(d => d.ElementCount).Returns(elementCount);

        var elementType = new Mock<IDataType>();
        elementType.As<IBitSize>().Setup(t => t.ByteSize).Returns(elementByteSize);

        var arrayType = new Mock<IArrayType>();
        arrayType.Setup(t => t.IsJagged).Returns(false);
        arrayType.Setup(t => t.Dimensions).Returns(dimensions.Object);
        arrayType.Setup(t => t.ElementType).Returns(elementType.Object);
        return arrayType.As<IDataType>().Object;
    }

    [Theory]
    [InlineData(typeof(bool), 1)]
    [InlineData(typeof(short), 2)]
    [InlineData(typeof(int), 4)]
    [InlineData(typeof(long), 8)]
    [InlineData(typeof(float), 4)]
    [InlineData(typeof(double), 8)]
    public void Resolve_ForAPrimitiveMatchingThePlcWidth_Succeeds(Type propertyType, int byteSize)
    {
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            propertyType, CreateSymbol(byteSize), out var marshalType, out var args));
        Assert.Equal(propertyType, marshalType);
        Assert.Null(args);
    }

    [Fact]
    public void Resolve_ForANullable_UnwrapsIt()
    {
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(double?), CreateSymbol(8), out var marshalType, out _));
        Assert.Equal(typeof(double), marshalType);
    }

    [Fact]
    public void Resolve_ForAnEnum_UsesItsUnderlyingType()
    {
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(Mode), CreateSymbol(2), out var marshalType, out _));
        Assert.Equal(typeof(short), marshalType);
    }

    [Fact]
    public void Resolve_ForANullableEnum_Fails()
    {
        // The value arrives as a boxed underlying integer. Unboxing that into an enum works, but
        // into a nullable enum it throws, and the property writer swallows it on every delivery.
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(Mode?), CreateSymbol(2), out _, out _));
    }

    [Fact]
    public void Resolve_ForAString_TakesTheLengthFromThePlcStringType()
    {
        // A PLC STRING(80) occupies 81 bytes, and the marshaller wants the 80.
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(string), CreateSymbol(81, CreateStringType(80, 81)), out var marshalType, out var args));
        Assert.Equal(typeof(string), marshalType);
        Assert.Equal([80], Assert.IsType<int[]>(args));
    }

    [Fact]
    public void Resolve_ForAnAliasedString_UsesTheResolvedType()
    {
        // A PLC alias such as `TYPE T_MaxString : STRING(255)` presents as an AliasType, which
        // implements neither IStringType nor IArrayType. Matching the unresolved type would drop
        // every aliased string to polling.
        var baseType = new StringType(80, Encoding.ASCII);
        var alias = new AliasType("T_MaxString", baseType);

        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(string), CreateSymbol(baseType.ByteSize, alias), out var marshalType, out var args));
        Assert.Equal(typeof(string), marshalType);
        Assert.Equal([80], Assert.IsType<int[]>(args));
    }

    [Fact]
    public void Resolve_ForARealStringType_Succeeds()
    {
        // The same path with no alias, using the real TwinCAT type rather than a mock.
        var stringType = new StringType(80, Encoding.ASCII);

        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(string), CreateSymbol(stringType.ByteSize, stringType), out _, out var args));
        Assert.Equal([80], Assert.IsType<int[]>(args));
    }

    [Fact]
    public void Resolve_ForARealWideStringType_Fails()
    {
        var wideStringType = new StringType(80, Encoding.Unicode);

        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(string), CreateSymbol(wideStringType.ByteSize, wideStringType), out _, out _));
    }

    [Fact]
    public void Resolve_ForAWideString_Fails()
    {
        // A WSTRING(80) holds two bytes per character and occupies 162. Deriving the length from the
        // byte size would pass the width check, because MarshalSize(string, [n]) is n + 1 by
        // definition, and then decode UTF-16 as single-byte text ending at the first NUL.
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(string), CreateSymbol(162, CreateStringType(80, 162)), out _, out _));
    }

    [Fact]
    public void Resolve_ForAStringOverANonStringSymbol_Fails()
    {
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(string), CreateSymbol(81), out _, out _));
    }

    [Fact]
    public void Resolve_ForAnArray_TakesTheElementCountFromThePlcArrayType()
    {
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(int[]), CreateSymbol(20, CreateArrayType(5, 4)), out var marshalType, out var args));
        Assert.Equal(typeof(int[]), marshalType);
        Assert.Equal([5], Assert.IsType<int[]>(args));
    }

    [Fact]
    public void Resolve_ForAByteArrayOverANonArraySymbol_Fails()
    {
        // A byte element size of one makes every width divide evenly, so a size check alone would
        // accept a byte[] against any symbol at all, including an LREAL.
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(byte[]), CreateSymbol(8), out _, out _));
    }

    [Fact]
    public void Resolve_ForAnArrayWhoseElementWidthDisagrees_Fails()
    {
        // int[] over an array of INT: the element counts would still multiply out to the same total.
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(int[]), CreateSymbol(20, CreateArrayType(10, 2)), out _, out _));
    }

    [Theory]
    [InlineData(typeof(float), 8)]  // REAL property over an LREAL symbol
    [InlineData(typeof(long), 4)]   // LINT property over a DINT symbol
    public void Resolve_WhenTheWidthDisagreesWithThePlc_Fails(Type propertyType, int byteSize)
    {
        // A narrower type is refused by the controller anyway; a wider one is accepted and reads
        // past the variable, so neither may be registered.
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            propertyType, CreateSymbol(byteSize), out _, out _));
    }

    [Fact]
    public void Resolve_ForATypeTheMarshallerRejects_Fails()
    {
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(char), CreateSymbol(2), out _, out _));
    }

    [Fact]
    public void Resolve_WhenTheSymbolHasNoSize_Fails()
    {
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(int), Mock.Of<ISymbol>(), out _, out _));
    }
}

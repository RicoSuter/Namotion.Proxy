using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Ads;

/// <summary>
/// Handles type conversion between ADS/PLC types and .NET types.
/// Override virtual methods for custom type mappings.
/// </summary>
public class AdsValueConverter
{
    /// <summary>
    /// Converts a value received from the PLC to a .NET property value.
    /// Override for custom type mappings.
    /// </summary>
    /// <param name="adsValue">The value received from the PLC.</param>
    /// <param name="property">The target property descriptor.</param>
    /// <returns>The converted .NET property value.</returns>
    public virtual object? ConvertToPropertyValue(object? adsValue, RegisteredSubjectProperty property)
    {
        if (adsValue is null)
            return null;

        var targetType = property.Type;

        // Handle DATE_AND_TIME -> DateTimeOffset (PLC DateTime values are assumed UTC)
        if (targetType == typeof(DateTimeOffset) && adsValue is DateTime dateTime)
            return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc), TimeSpan.Zero);

        // An enum arrives as an integer, from the raw-integer read for a PLC type that will not
        // resolve or from a notification registered on the underlying type. Unboxing is strict about
        // both signedness and nullability, so assigning a short to a ushort-backed enum, or any
        // integer to a nullable enum, throws where Enum.ToObject succeeds.
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (enumType.IsEnum && IsIntegral(adsValue))
            return Enum.ToObject(enumType, adsValue);

        return adsValue;
    }

    /// <summary>
    /// True for the integer types <see cref="Enum.ToObject(Type, object)"/> accepts. It throws for a
    /// floating-point value, and that throw would abort the rest of the polling pass.
    /// </summary>
    private static bool IsIntegral(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong;

    /// <summary>
    /// Converts a .NET property value to an ADS-compatible value for writing to the PLC.
    /// Override for custom type mappings.
    /// </summary>
    /// <param name="propertyValue">The .NET property value to convert.</param>
    /// <param name="property">The source property descriptor.</param>
    /// <returns>The converted ADS-compatible value.</returns>
    public virtual object? ConvertToAdsValue(object? propertyValue, RegisteredSubjectProperty property)
    {
        if (propertyValue is null)
            return null;

        // Mirrors the read direction. The any-type path, used for a symbol whose PLC type will not
        // resolve, cannot marshal an enum instance and rejects the write with DeviceInvalidSize, so
        // it goes out as the underlying integer.
        if (propertyValue.GetType().IsEnum)
            return Convert.ChangeType(propertyValue, Enum.GetUnderlyingType(propertyValue.GetType()));

        // Handle DateTimeOffset -> DateTime for DATE_AND_TIME
        if (propertyValue is DateTimeOffset dateTimeOffset)
            return dateTimeOffset.UtcDateTime;

        return propertyValue;
    }
}

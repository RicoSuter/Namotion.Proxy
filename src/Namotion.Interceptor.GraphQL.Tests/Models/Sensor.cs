using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.GraphQL.Tests.Models;

[InterceptorSubject]
public partial class Sensor
{
    public partial decimal Temperature { get; set; }

    public partial decimal Humidity { get; set; }

    public partial Location? Location { get; set; }

    [Derived]
    public string Status => Temperature > 30 ? "Hot" : "Normal";
}

[InterceptorSubject]
public partial class Location
{
    public partial string? Building { get; set; }

    public partial string? Room { get; set; }
}
